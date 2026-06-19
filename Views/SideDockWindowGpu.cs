using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Polaris.Models;
using Polaris.Services;
using Polaris.Services.Gpu;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using FontStyle = Vortice.DirectWrite.FontStyle;

namespace Polaris.Views;

/// <summary>GPU side dock (spike) — Stage A static render, Stage B hover hit-test +
/// floating name label, Stage C macOS-style magnify wave, Stage D running-app strip.
/// Draws the liquid-glass slab, the pinned icon column, a light-split divider and the
/// running-but-unpinned strip (Polaris tile first, green running dots, "+N" overflow)
/// in Direct2D under DirectComposition; a cursor poll drives a continuous magnify wave
/// across both halves and shows the hovered icon's name. Per-monitor DPI aware (layout
/// in DIPs, window + swap chain in physical px, D2D target DPI = 96 x scale). The window
/// stays click-through — launch/drag come in Stage E. Shown behind POLARIS_GPU_SIDEDOCK=1.</summary>
internal sealed class SideDockWindowGpu : IDisposable, ISideDock
{
    private const float GlassIconScale = 1.32f;
    private const float SideDockScaleK = 0.70f;
    private const float HoverScale = 1.5f;
    // Pointer travel (window-DIP, Manhattan) before a press becomes a drag.
    // Matches the WPF dock's 6 px DragThreshold so small reposition gestures
    // register as drags instead of being misread as a launch-click.
    private const float DragThreshold = 6f;

    private enum SlotKind { Pinned, Run, Overflow }

    private readonly struct Slot
    {
        public readonly Vector2 Center;
        public readonly float G;
        public readonly string Name;
        public readonly bool Running;     // draws the breathing green dot
        public readonly SlotKind Kind;
        public readonly string IconKey;   // D2D bitmap cache key
        public readonly BitmapSource? Image;
        public readonly AppEntry? Entry;  // pinned: the app to launch
        public readonly IntPtr Window;    // running: the window to activate
        public readonly string? RunPath;  // running: exe path (for right-click pin/close)
        public readonly string? RunAumid; // running: AUMID (for right-click pin/close)
        public Slot(Vector2 c, float g, string name, bool run, SlotKind kind, string iconKey,
            BitmapSource? img, AppEntry? entry, IntPtr window, string? runPath = null, string? runAumid = null)
        { Center = c; G = g; Name = name; Running = run; Kind = kind; IconKey = iconKey; Image = img; Entry = entry; Window = window; RunPath = runPath; RunAumid = runAumid; }
    }

    private readonly AppConfig _config;
    private IntPtr _hwnd;
    private CompositionHost? _host;
    private IDWriteFactory? _dwrite;
    private IDWriteTextFormat? _labelFormat;
    private IDWriteTextFormat? _hoverFormat;   // floating hover name (SemiBold, hover-scaled)
    private float _hoverFontPx = 16f;
    private readonly Dictionary<string, ID2D1Bitmap?> _bmpCache = new();
    private readonly Dictionary<string, BitmapSource?> _iconCache = new();
    private DispatcherTimer? _timer;

    private readonly List<Slot> _slots = new();
    private DockSide _side;
    private int _winX, _winY, _winW, _winH;
    private double _dpi = 1.0;
    private int _hover = -1;
    private float _sx, _sy, _sw, _sh, _trayRadius, _opacity, _frost;
    private float _gIcon, _cellH;
    private float _seamMain, _bodyCross, _bodyCrossLen;
    private float _colCenterCross, _slabCrossLen, _pinnedAreaMain, _cellMain;
    private int _pinnedVisible;
    private float[] _waveCur = Array.Empty<float>();
    private const float WaveSupport = 2.3f;

    // ---- New-message attention badge (parity with the GPU main dock / WPF) -------
    private volatile System.Collections.Generic.HashSet<string> _flashKeys = new();
    private float _badgePulse = 1f;       // attention dot pulse scale (~1.0..1.18, 1.4s)
    private long _attnLast;               // last attention poll tick (throttle to ~800ms)
    private volatile bool _attnBusy;      // an attention poll task is in flight

    // ---- Running-icon glow pulse + glass orbit light (parity with the GPU main dock) ----
    private float _runPulse = 0.5f;       // running-icon glow pulse (0.35..0.8, 2.2s breathe)
    private float _orbitAngle;            // glass orbit-light angle (deg, 36s/rev clockwise)
    private bool _anyRunning;             // any slot is running (gates the pulse/orbit)

    // ---- Show/hide slide + fade (parity with WPF SideDockWindow.DoShow/DoHide) -------
    private long _introStart;             // tick the slide/fade animation began
    private int _introMode;               // 0=idle, 1=show (slide-in + fade-in), 2=hide (fade-out)
    private float _introSlidePx;          // physical-px cross slide distance for the show anim

    // ---- Drag-reorder "push neighbours aside" (parity with WPF ArrangeForDrag) -------
    private float[] _dragShift = Array.Empty<float>();     // per-slot main-axis gap offset (DIP, eased)
    private float[] _dragShiftTgt = Array.Empty<float>();  // its target
    private int _dragInsert = -1;                          // current insertion index (pinned region)
    private long _dragShiftLastMs;                         // last drag-shift advance (for time-based ease)
    private const float DragShiftTauMs = 45f;              // push-aside ease time constant

    // ---- Saturn dark-slab styling (black smoked dock + flame + debris + stars) ----
    private bool _saturn;
    private float _satBaseEdge, _satSlabMain, _satSlabLen, _flameFeather, _satDriftAmp;
    private bool _curActive; private float _curMain;
    private Vector2[] _stars = Array.Empty<Vector2>();
    private float[] _starSz = Array.Empty<float>();
    private float[] _starA = Array.Empty<float>();
    private float[] _starTwk = Array.Empty<float>();    // twinkle period (s); 0 = steady
    private float[] _starPhase = Array.Empty<float>();
    private float[] _debMain = Array.Empty<float>();
    private float[] _debCross = Array.Empty<float>();
    private float[] _debR = Array.Empty<float>();
    private float[] _debA = Array.Empty<float>();
    private float[] _debPar = Array.Empty<float>();
    private float[] _debCur = Array.Empty<float>();
    // Faceted-rock rendering (parity with WPF MakeRock): per-rock jittered polygon
    // offsets (local px), a base grey value, and lazily-built device geometry/brush.
    private Vector2[][] _debVerts = Array.Empty<Vector2[]>();
    private byte[] _debVal = Array.Empty<byte>();
    private ID2D1PathGeometry?[] _debGeo = Array.Empty<ID2D1PathGeometry?>();
    private ID2D1LinearGradientBrush?[] _debBrush = Array.Empty<ID2D1LinearGradientBrush?>();

    // ---- Interaction (Stage E) -------------------------------------------
    private static readonly Dictionary<IntPtr, SideDockWindowGpu> s_instances = new();
    private int _pressIdx = -1;        // slot under the mouse-down, or -1
    private bool _dragging;            // press has crossed the drag threshold
    private float _pressMain, _pressCross;   // mouse-down point (window-local DIP)
    private float _dragMain, _dragCross;     // current drag point (window-local DIP)

    public SideDockWindowGpu(AppConfig config) => _config = config;

    // ---- Visibility (ISideDock) ------------------------------------------
    // The dock is realised once (hidden) and summoned by any of four independent
    // show reasons, mirroring the WPF side dock. The Win32 window is kept built
    // and toggled with ShowWindow so a summon doesn't pay to recreate the DComp
    // surface; the magnify/render Tick idles while hidden.
    private bool _shown;
    private bool _realized;
    private bool _byMain, _byEdge, _byDrag, _byPinned;

    /// <summary>Raised after the dock mutates the shared main-dock app list, so the
    /// host can refresh the main dock (parity with the WPF side dock).</summary>
    public event Action? MainDockChanged;

    /// <summary>Invoked when the dock's Polaris tile asks to toggle the pinned docks.</summary>
    public Action? ToggleDocks { get; set; }

    public bool DockVisible => _shown;
    public DockSide DockSidePosition => _config.Settings.DockPosition;

    public void Realize()
    {
        if (_realized)
            return;
        _realized = true;
        try { Build(); }   // builds the window HIDDEN (since _shown == false)
        catch (Exception ex) { Log.Warn("SideDockGpu", "realize failed: " + ex); }
    }

    /// <summary>Legacy entry point (was the always-on spike). Now just realises the
    /// dock hidden, ready to be summoned by the host.</summary>
    public void Show() => Realize();

    public void SetMainShown(bool shown) { _byMain = shown; UpdateVisibility(); }
    public void SetPinnedShown(bool shown) { _byPinned = shown; UpdateVisibility(); }
    public void SetEdgeShown(bool shown) { _byEdge = shown; UpdateVisibility(); }
    public void SetDragActive(bool active) { _byDrag = active; UpdateVisibility(); }

    public void HideAll()
    {
        _byMain = _byEdge = _byDrag = _byPinned = false;
        UpdateVisibility();
    }

    /// <summary>The GPU dock has no perpetual breathing animation (the running dot
    /// is a static gradient and the magnify wave already settles on its own), so
    /// there is nothing to pause — kept for ISideDock parity.</summary>
    public void SetAmbientPaused(bool paused) { /* no-op */ }

    private void UpdateVisibility()
    {
        // Keep the dock shown while a hover-thumbnail preview is open (the pointer is
        // over the floating preview, which sits beyond the slab, so the edge poll would
        // otherwise retract the dock out from under it — mirrors the WPF hold reasons).
        bool want = _byMain || _byEdge || _byDrag || _byPinned || (_preview?.IsOpen == true);
        if (want == _shown)
            return;
        if (want) DoShow();
        else DoHide();
    }

    private void DoShow()
    {
        _shown = true;
        if (!_realized) { Realize(); StartIntro(); return; }
        // Recreate so the running strip + layout reflect the latest state, then
        // ensure the window is visible and on top.
        Rebuild();
        if (_hwnd != IntPtr.Zero)
        {
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        }
        StartIntro();
    }

    private void DoHide()
    {
        _shown = false;
        CloseSlotMenu();
        ClosePreview();
        _hover = -1; _prevHover = -1;
        // Fade out on the GPU compositor, then SW_HIDE once it completes (DriveIntro
        // mode 2). If there's no live host yet, just hide immediately.
        if (_host != null && _hwnd != IntPtr.Zero)
        {
            _introStart = Environment.TickCount64;
            _introMode = 2;
            DriveIntro();
        }
        else if (_hwnd != IntPtr.Zero)
            ShowWindow(_hwnd, SW_HIDE);
    }

    /// <summary>Begins the show slide-in + fade-up: the dock eases in from its anchored
    /// edge (QuinticEaseOut, 220ms) while fading up (120ms), mirroring WPF DoShow.</summary>
    private void StartIntro()
    {
        if (!_shown || _host == null) return;
        _introSlidePx = (_slabCrossLen + 40f) * (float)_dpi;
        _introStart = Environment.TickCount64;
        _introMode = 1;
        DriveIntro();
    }

    /// <summary>Advances the show/hide slide+fade on the GPU compositor. Returns true
    /// while an animation is live (so the caller keeps rendering). On show: slides the
    /// visual from the edge to rest and fades up. On hide: fades out, then SW_HIDEs.</summary>
    private bool DriveIntro()
    {
        if (_introMode == 0 || _host == null) return false;
        long now = Environment.TickCount64;
        if (_introMode == 1)
        {
            float st = Math.Clamp((now - _introStart) / 220f, 0f, 1f);   // slide 220ms
            float ot = Math.Clamp((now - _introStart) / 120f, 0f, 1f);   // fade  120ms
            float e = 1f - MathF.Pow(1f - st, 5f);                       // QuinticEaseOut
            var off = PopOffset(-_introSlidePx * (1f - e));              // start toward the edge
            _host.SetIntro(off.X, off.Y, ot);
            if (st >= 1f && ot >= 1f) { _host.SetIntro(0f, 0f, 1f); _introMode = 0; return false; }
            return true;
        }
        // mode 2: fade out, then hide
        float ft = Math.Clamp((now - _introStart) / 160f, 0f, 1f);
        _host.SetIntro(0f, 0f, 1f - ft);
        if (ft >= 1f)
        {
            _introMode = 0;
            if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_HIDE);
            return false;
        }
        return true;
    }

    public void RefreshFromConfig()
    {
        try { DockSync.MirrorResidentToLeft(_config); } catch { }
        if (_realized) Rebuild();
    }

    public void RefreshLayout()
    {
        if (_realized) Rebuild();
    }

    /// <summary>Screen-DIP rectangle of the glass slab (the host's edge poll and the
    /// main dock's "drop into side dock" test work in DIPs). _winX/_winY and the
    /// slab _sx/_sy/_sw/_sh are all DIPs.</summary>
    public System.Windows.Rect GetDockScreenBounds()
    {
        if (!_realized) Realize();
        return new System.Windows.Rect(_winX + _sx, _winY + _sy, _sw, _sh);
    }

    /// <summary>Pins an entry dragged from the main dock onto this dock. The point
    /// is DEVICE pixels (from the main dock's PointToScreen); convert to window-local
    /// DIP and test against the slab, then insert at the pointer's main-axis slot.</summary>
    public bool TryAcceptDrop(System.Windows.Point screenDevicePoint, AppEntry entry)
    {
        if (!_realized) Realize();
        float lx = (float)(screenDevicePoint.X / _dpi - _winX);
        float ly = (float)(screenDevicePoint.Y / _dpi - _winY);
        float m = _gIcon * 0.5f;
        bool inside = lx >= _sx - m && lx <= _sx + _sw + m && ly >= _sy - m && ly <= _sy + _sh + m;
        if (!inside)
            return false;
        if (_config.Apps.FindIndex(a => DockSync.Matches(a, entry)) < 0)
        {
            float main = _side is DockSide.Left or DockSide.Right ? ly : lx;
            int dropIdx = (int)Math.Round((main - _pinnedAreaMain) / _cellMain);
            dropIdx = Math.Clamp(dropIdx, 0, DockSync.ResidentCount(_config));
            DockSync.InsertResident(_config, entry, dropIdx);
            PersistAndRebuild();
        }
        return true;
    }

    private void Build()
    {
        LayoutContent();
        CreateHostWindow();
    }

    /// <summary>Computes window geometry + all icon slots without touching the
    /// Win32/DComp window, so a reorder can relayout in place (parity with the main
    /// dock's LayoutContent) instead of recreating the window — which flashes.</summary>
    private void LayoutContent()
    {
        _slots.Clear();
        var wa = MonitorLayout.ActiveWorkArea;
        _side = _config.Settings.DockPosition;
        bool vertical = _side is DockSide.Left or DockSide.Right;
        double uiScale = Math.Clamp(MonitorLayout.ActiveBounds.Height / 1080.0, 1.0, 2.0);
        double iconSize = _config.Settings.IconSize;
        double effIcon = iconSize * uiScale * (SideDockScaleK / GlassIconScale);
        double gIcon = effIcon * GlassIconScale;
        double cellH = effIcon * 1.46;

        double crossGap = 1 * uiScale;
        double padCross = gIcon * (HoverScale - 1.0) / 2.0 + effIcon * 0.12;
        double slabCrossLen = gIcon + padCross * 2.0;
        double slabCross = crossGap;
        double edgeBias = gIcon * (HoverScale - 1.0) * 0.30;
        double colCenterCross = slabCross + slabCrossLen / 2.0 - edgeBias;

        double startPad = effIcon * 0.7, endPad = effIcon * 0.7;
        double thickness = vertical ? gIcon * HoverScale + 240 * uiScale : gIcon * HoverScale + 130 * uiScale;

        var apps = _config.SideDockApps;
        int pinnedCount = apps.Count;

        // Horizontal docks are sized to their CONTENT (centred), not full-width —
        // matching the real dock's DesiredContentMain (incl. the running-area
        // reserve), so the GPU dock has the same footprint.
        double defCell = effIcon * 1.46;
        const int maxRunSlots = 1 + 10 + 1;   // Polaris + RunningMaxComplete + overflow
        double desiredMain = 12 * uiScale + startPad + pinnedCount * defCell
                           + effIcon * 0.55 + maxRunSlots * defCell + endPad + 12 * uiScale;
        double winMain = vertical ? wa.Height : Math.Min(desiredMain, wa.Width);
        _winW = (int)Math.Ceiling(vertical ? thickness : winMain);
        _winH = (int)Math.Ceiling(vertical ? wa.Height : thickness);
        double mainExtent = winMain;

        double startReserve = 12 * uiScale, endReserve = vertical ? 56 * uiScale : 12 * uiScale;
        double usableMain = mainExtent - startReserve - endReserve;

        // ---- Running-but-unpinned strip (Stage D) -------------------------
        var runItems = CollectRunning(apps, out int overflow);
        int runSlots = 1 + runItems.Count + (overflow > 0 ? 1 : 0);   // Polaris + apps + overflow
        double seam = effIcon * 0.55;

        // One uniform cell pitch above and below the divider; shrink only if the
        // combined column would overflow the usable band (mirrors the real dock).
        int totalCells = pinnedCount + runSlots;
        double fixedChrome = startPad + endPad + seam;
        double availForCells = usableMain - fixedChrome;
        if (totalCells > 0 && totalCells * cellH > availForCells)
            cellH = Math.Max(gIcon * 1.04, availForCells / totalCells);

        double runningBlockH = runSlots * cellH;          // RunStep == cellH
        int maxVisible = Math.Max(1, (int)Math.Floor((availForCells - runningBlockH) / cellH));
        int pinnedVisible = Math.Min(pinnedCount, maxVisible);
        double pinnedBlockH = pinnedVisible * cellH;
        double slabMainLen = startPad + pinnedBlockH + seam + runningBlockH + endPad;

        // Centre the VISIBLE ICON CLUSTER (pinned + running, incl. the seam gap),
        // not the slab box, on the usable band — same correction as the real dock.
        int visibleCells = pinnedVisible + runSlots;
        double centroidFromSlab = startPad
            + (visibleCells > 0 ? cellH * visibleCells / 2.0 : 0)
            + (visibleCells > 0 ? seam * runSlots / (double)visibleCells : 0);
        double slabMain = (startReserve + usableMain / 2.0) - centroidFromSlab;
        slabMain = Math.Min(slabMain, mainExtent - endReserve - slabMainLen);
        slabMain = Math.Max(slabMain, startReserve);
        double pinnedAreaMain = slabMain + startPad;
        double runAreaMain = pinnedAreaMain + pinnedBlockH + seam;

        double lastPinnedEnd = pinnedAreaMain + pinnedBlockH - (cellH - gIcon) / 2.0;
        double firstRunStart = runAreaMain + (cellH - gIcon) / 2.0;
        double seamMain = pinnedVisible > 0
            ? (lastPinnedEnd + firstRunStart) / 2.0
            : runAreaMain - seam / 2.0;

        // Dark (Saturn) and clear-glass docks use different body geometry, matching
        // SideDockWindow.Layout: the dark slab bleeds its edge-side off-screen
        // (darkBleed) so the feathered black sits flush against the screen edge (no
        // floating gap), and reserves a larger interior pad (darkPad). The clear-glass
        // dock hugs the column with a modest glassPad and no bleed.
        bool darkSlab = ThemeRegistry.Get(_config.Settings.Theme).IsSaturn;
        double bodyCross, bodyCrossLen;
        if (darkSlab)
        {
            double darkBleed = gIcon * 0.4, darkPad = gIcon * 0.5;
            bodyCross = slabCross - darkBleed;
            bodyCrossLen = (colCenterCross - bodyCross) + gIcon / 2.0 + darkPad;
        }
        else
        {
            double glassPad = gIcon * 0.30;
            bodyCross = slabCross;
            bodyCrossLen = (colCenterCross - bodyCross) + gIcon / 2.0 + glassPad;
        }
        _trayRadius = (float)(iconSize * uiScale * 0.42);
        _opacity = (float)(1.0 - Math.Clamp(_config.Settings.PanelTransparency, 0.0, 1.0));
        _frost = (float)GlassChrome.FrostStrengthFor(_config.Settings.PanelTransparency);

        _winX = _side switch
        {
            DockSide.Right => (int)(wa.Right - _winW),
            DockSide.Left => (int)wa.Left,
            _ => (int)(wa.Left + (wa.Width - _winW) / 2.0),   // Top/Bottom centred
        };
        _winY = _side switch { DockSide.Bottom => (int)(wa.Bottom - _winH), _ => (int)wa.Top };

        (_sx, _sy, _sw, _sh) = _side switch
        {
            DockSide.Left => ((float)bodyCross, (float)slabMain, (float)bodyCrossLen, (float)slabMainLen),
            DockSide.Right => ((float)(_winW - bodyCross - bodyCrossLen), (float)slabMain, (float)bodyCrossLen, (float)slabMainLen),
            DockSide.Top => ((float)slabMain, (float)bodyCross, (float)slabMainLen, (float)bodyCrossLen),
            _ => ((float)slabMain, (float)(_winH - bodyCross - bodyCrossLen), (float)slabMainLen, (float)bodyCrossLen),
        };

        var running = RunningAppTracker.SnapshotRunning();
        var noTitles = new List<string>();
        var noAumids = new HashSet<string>();
        for (int i = 0; i < pinnedVisible && i < apps.Count; i++)
        {
            var entry = apps[i];
            double mainC = pinnedAreaMain + i * cellH + cellH / 2.0;
            (float cx, float cy) = ToLocal(_side, mainC, colCenterCross, _winW, _winH);
            bool run = RunningAppTracker.IsEntryRunning(entry, running, noTitles, noAumids);
            var img = IconExtractor.GetCached(entry.EffectiveIconSource, _iconCache);
            _slots.Add(new Slot(new Vector2(cx, cy), (float)gIcon, entry.Name, run,
                SlotKind.Pinned, entry.EffectiveIconSource, img, entry, IntPtr.Zero));
        }
        for (int k = 0; k < runSlots; k++)
        {
            double mainC = runAreaMain + k * cellH + cellH / 2.0;
            (float cx, float cy) = ToLocal(_side, mainC, colCenterCross, _winW, _winH);
            if (k == 0)
            {
                string exe = Environment.ProcessPath ?? "";
                _slots.Add(new Slot(new Vector2(cx, cy), (float)gIcon, "Polaris", true,
                    SlotKind.Run, "polaris:" + exe, SafeIcon(exe), null, IntPtr.Zero));
            }
            else if (overflow > 0 && k == runSlots - 1)
            {
                _slots.Add(new Slot(new Vector2(cx, cy), (float)gIcon, "+" + overflow, false,
                    SlotKind.Overflow, "", null, null, IntPtr.Zero));
            }
            else
            {
                var it = runItems[k - 1];
                _slots.Add(new Slot(new Vector2(cx, cy), (float)gIcon, it.Name, true,
                    SlotKind.Run, it.IconKey, it.Image, null, it.Window, it.Path, it.Aumid));
            }
        }
        _seamMain = (float)seamMain;
        _bodyCross = (float)bodyCross;
        _bodyCrossLen = (float)bodyCrossLen;
        _pinnedVisible = pinnedVisible;
        _colCenterCross = (float)colCenterCross;
        _slabCrossLen = (float)slabCrossLen;
        _pinnedAreaMain = (float)pinnedAreaMain;
        _cellMain = (float)cellH;
        _gIcon = (float)gIcon;
        _cellH = (float)cellH;
        _saturn = ThemeRegistry.Get(_config.Settings.Theme).IsSaturn;
        if (_saturn)
            BuildSaturnField(slabMain, slabMainLen, bodyCross, bodyCrossLen, gIcon, uiScale);
        _waveCur = new float[_slots.Count];
        Array.Fill(_waveCur, 1f);
        _dragShift = new float[_slots.Count];
        _dragShiftTgt = new float[_slots.Count];
        _dragInsert = -1;
        _anyRunning = false;
        foreach (var sl in _slots) if (sl.Running) { _anyRunning = true; break; }
    }

    /// <summary>Creates the Win32 host window, DComp host and render timer for the
    /// current geometry. Split from <see cref="LayoutContent"/> so reorders can
    /// relayout without recreating the window.</summary>
    private void CreateHostWindow()
    {
        _hwnd = CreateWindow(_winW, _winH);
        s_instances[_hwnd] = this;
        _dpi = DpiScale();
        // Layout is computed in DIPs (MonitorLayout returns DIPs); the Win32 window
        // + DComp swap chain live in PHYSICAL pixels. Size the window to physical px
        // and tell D2D the target DPI so all DIP-space drawing scales up 1:1.
        int pw = (int)Math.Ceiling(_winW * _dpi), ph = (int)Math.Ceiling(_winH * _dpi);
        int px = (int)Math.Round(_winX * _dpi), py = (int)Math.Round(_winY * _dpi);
        SetWindowPos(_hwnd, HWND_TOPMOST, px, py, pw, ph, SWP_NOACTIVATE);
        ShowWindow(_hwnd, _shown ? SW_SHOWNOACTIVATE : SW_HIDE);
        DragAcceptFiles(_hwnd, true);   // accept desktop shortcuts / files dropped to pin
        _host = new CompositionHost(_hwnd, pw, ph, (float)(96.0 * _dpi));
        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
        _labelFormat?.Dispose();
        _hoverFormat?.Dispose();
        _labelFormat = _dwrite.CreateTextFormat("Microsoft YaHei UI", null, FontWeight.Normal,
            FontStyle.Normal, FontStretch.Normal, 13f, "zh-cn");
        _labelFormat.TextAlignment = TextAlignment.Center;
        _labelFormat.ParagraphAlignment = ParagraphAlignment.Center;
        // Floating hover name: SemiBold and sized to read at the hover-zoom scale, exactly
        // like the WPF ShowHoverLabelCore (10.5 × HoverScale × FontScale).
        _hoverFontPx = (float)(10.5 * HoverScale * FontScale.Current);
        _hoverFormat = _dwrite.CreateTextFormat("Microsoft YaHei UI", null, FontWeight.SemiBold,
            FontStyle.Normal, FontStretch.Normal, _hoverFontPx, "zh-cn");
        _hoverFormat.TextAlignment = TextAlignment.Center;
        _hoverFormat.ParagraphAlignment = ParagraphAlignment.Center;

        Render();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private float WaveScaleAt(float cursorMain, float iconMain)
    {
        float d = Math.Abs(cursorMain - iconMain) / _cellH;
        if (d >= WaveSupport)
            return 1f;
        float f = 0.5f * (1f + (float)Math.Cos(Math.PI * d / WaveSupport));
        return 1f + (HoverScale - 1f) * f;
    }

    private Vector2 PopOffset(float pop) => _side switch
    {
        DockSide.Left => new Vector2(pop, 0),
        DockSide.Right => new Vector2(-pop, 0),
        DockSide.Top => new Vector2(0, pop),
        _ => new Vector2(0, -pop),
    };

    /// <summary>True when a window-local DIP point lies on the dock's interactive
    /// surface — the glass slab plus each icon's outward magnify-pop headroom. The
    /// pop-side margin is generous enough to cover a fully hover-zoomed icon, so a
    /// press anywhere on a (possibly popped) icon hit-tests as ours. Without this the
    /// upper, popped-out part of an icon falls in the slab's transparent headroom,
    /// and the global click-away watcher mistakes the press for a click outside the
    /// dock and dismisses BOTH docks the instant the button goes down.</summary>
    private bool InsideHitRegion(float lx, float ly)
    {
        float m = _gIcon * 0.5f;
        // Outward reach of a fully magnified icon: scale growth + pop offset, with
        // headroom so a press on the popped-up upper half of an icon still hits us.
        float mPop = (HoverScale - 1f) * _gIcon * 1.18f + _gIcon * 1.2f;
        float l = _sx - m, r = _sx + _sw + m, t = _sy - m, b = _sy + _sh + m;
        switch (_side)
        {
            case DockSide.Left: r = _sx + _sw + mPop; break;   // pops right
            case DockSide.Right: l = _sx - mPop; break;        // pops left
            case DockSide.Top: b = _sy + _sh + mPop; break;    // pops down
            default: t = _sy - mPop; break;                    // Bottom: pops up
        }
        return lx >= l && lx <= r && ly >= t && ly <= b;
    }

    private void Tick()
    {
        if (_host == null)
            return;
        // Drive the show/hide slide+fade. During the hide outro _shown is already
        // false but the fade is still playing, so animate-and-render then bail.
        bool intro = DriveIntro();
        if (!_shown)
        {
            if (intro) Render();
            return;
        }
        bool vertical = _side is DockSide.Left or DockSide.Right;
        bool active = false;
        float curMain = 0;
        if (!_dragging && GetCursorPos(out POINT p))
        {
            float lx = (float)(p.X / _dpi - _winX), ly = (float)(p.Y / _dpi - _winY);
            float m = _gIcon * 0.6f;
            if (lx >= _sx - m && lx <= _sx + _sw + m && ly >= _sy - m && ly <= _sy + _sh + m)
            {
                active = true;
                curMain = vertical ? ly : lx;
            }
        }

        float k = 1f - (float)Math.Exp(-0.016 / 0.045);   // tau 45ms
        float maxDelta = 0f;
        int focal = -1; float best = float.MaxValue;
        for (int i = 0; i < _slots.Count; i++)
        {
            float iconMain = vertical ? _slots[i].Center.Y : _slots[i].Center.X;
            float target = active ? WaveScaleAt(curMain, iconMain) : 1f;
            float cur = _waveCur[i] + (target - _waveCur[i]) * k;
            _waveCur[i] = cur;
            maxDelta = Math.Max(maxDelta, Math.Abs(target - cur));
            if (active)
            {
                float d = Math.Abs(curMain - iconMain);
                if (d < best) { best = d; focal = i; }
            }
        }
        _hover = active && focal >= 0 && best <= _cellH ? focal : -1;
        DrivePreview(_hover);

        // Drag-reorder "push neighbours aside": ease each non-dragged pinned slot's
        // main-axis offset toward its open-gap target (while dragging) or back to rest
        // (after a drop that didn't rebuild), mirroring WPF ArrangeForDrag/AnimateIconTo.
        // Time-based so it stays smooth despite DispatcherTimer jitter, and is also
        // advanced from WM_MOUSEMOVE so neighbours track the dragged icon during fast drags.
        if (_dragShift.Length > 0)
            maxDelta = Math.Max(maxDelta, AdvanceDragShift());

        // Saturn debris: ease each rock's outward push toward the magnify wave at its
        // main coordinate so the rubble belt bulges under the cursor and relaxes behind.
        if (_saturn && _debCur.Length > 0)
        {
            _curActive = active; _curMain = curMain;
            float denom = Math.Max(0.0001f, HoverScale - 1f);
            float maxPush = _gIcon * 0.5f;
            for (int i = 0; i < _debCur.Length; i++)
            {
                float tgt = 0f;
                if (active)
                {
                    float a = Math.Clamp((WaveScaleAt(curMain, _debMain[i]) - 1f) / denom, 0f, 1f);
                    tgt = a * maxPush * _debPar[i];
                }
                float cur = _debCur[i] + (tgt - _debCur[i]) * k;
                _debCur[i] = cur;
                maxDelta = Math.Max(maxDelta, Math.Abs(tgt - cur));
            }
        }
        else if (_saturn)
            _curActive = active;

        // Render every frame while the wave is live or a launch bounce is playing;
        // once settled at rest, render one final frame and idle (the timer keeps
        // polling for re-entry). The Saturn dock keeps animating while shown so its
        // starfield twinkles and the debris belt drifts (cheap on the GPU).
        bool bouncing = _bounceIdx >= 0 && (Environment.TickCount64 - _bounceStart) <= BounceDurMs;
        if (!bouncing) _bounceIdx = -1;

        // New-message attention badge: poll flashing windows off-thread (~800ms) while
        // shown, pulse the dot, and keep rendering while any icon is flashing.
        if (_shown && Environment.TickCount64 - _attnLast > 800)
        {
            _attnLast = Environment.TickCount64;
            PollAttention();
        }
        bool anyFlash = _flashKeys.Count > 0;
        if (anyFlash)
            _badgePulse = 1.09f + 0.09f * MathF.Sin(Environment.TickCount64 / 1000f * 2f * MathF.PI / 1.4f);

        // Running-icon glow pulse: breathe the running dot's halo (2.2s) in both themes
        // (mirrors RadialIcon's UpdateRunningDot ambient pulse). The flowing border is a
        // main-dock-only effect (WPF side dock shows only the dot, no sweep border).
        if (_anyRunning)
        {
            double rph = Environment.TickCount64 / 1000.0 * 2.0 * Math.PI / 2.2;
            _runPulse = 0.575f + 0.225f * MathF.Sin((float)rph);
        }
        if (!_saturn && _shown)
            _orbitAngle = (_orbitAngle + 16f * 360f / 36000f) % 360f;

        if (active || bouncing || maxDelta > 0.001f || (_saturn && _shown) || (!_saturn && _shown) || anyFlash || intro)
            Render();
    }

    /// <summary>Snapshots the running icons' flashing windows off the UI thread and
    /// rebuilds <see cref="_flashKeys"/>, the set of icon keys whose windows are
    /// requesting attention. Reuses <see cref="PreviewSourceFor"/> so the icon→window
    /// matching is identical to the hover previews and the WPF dock. Skips the
    /// window enumeration entirely when nothing is flashing (the common case).</summary>
    private void PollAttention()
    {
        if (_attnBusy)
            return;
        _attnBusy = true;
        var sources = new List<(string key, Func<List<WindowPreview>> src)>();
        foreach (var s in _slots)
        {
            if (!s.Running) continue;
            var src = PreviewSourceFor(s);
            if (src != null) sources.Add((s.IconKey, src));
        }
        System.Threading.Tasks.Task.Run(() =>
        {
            var keys = new System.Collections.Generic.HashSet<string>();
            try
            {
                var flashing = Polaris.Services.AttentionService.SnapshotFlashing();
                if (flashing.Count > 0)
                    foreach (var (key, src) in sources)
                    {
                        try
                        {
                            foreach (var w in src())
                                if (flashing.Contains(w.Handle)) { keys.Add(key); break; }
                        }
                        catch { }
                    }
            }
            catch { }
            _flashKeys = keys;
            _attnBusy = false;
        });
    }

    private const long BounceDurMs = 480;
    private int _bounceIdx = -1;
    private long _bounceStart;

    /// <summary>Extra outward "pop" (in the dock's pop direction) for the launch
    /// bounce: the clicked icon leaps off the dock surface and falls back over
    /// <see cref="BounceDurMs"/> ms — a single damped hop (sin half-cycle with a
    /// gentle settle), mirroring the WPF dock's macOS-style launch hop.</summary>
    private float BounceOffset(int i)
    {
        if (i != _bounceIdx)
            return 0f;
        long el = Environment.TickCount64 - _bounceStart;
        if (el < 0 || el > BounceDurMs)
            return 0f;
        float t = el / (float)BounceDurMs;
        // Up fast, fall back with a small overshoot for a springy settle.
        float hop = MathF.Sin(MathF.PI * t);
        float settle = -0.12f * MathF.Sin(MathF.PI * 2f * t) * (1f - t);
        return _gIcon * 0.6f * (hop + settle);
    }

    private static Color4 Col(byte a, byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, a / 255f);

    /// <summary>Precomputes the Saturn dock's starfield and debris belt (mirrors the
    /// WPF DrawDockStarfield / DrawDebrisBelt). Stars are window-local and only twinkle
    /// in alpha; debris keep logical main/cross coords so the magnify wave can shove
    /// them outward and a slow drift can orbit them along the belt.</summary>
    private void BuildSaturnField(double slabMain, double slabMainLen, double bodyCross, double bodyCrossLen, double gIcon, double uiScale)
    {
        _satSlabMain = (float)slabMain;
        _satSlabLen = (float)slabMainLen;
        _satBaseEdge = (float)(bodyCross + bodyCrossLen);
        _flameFeather = (float)Math.Max(16.0, Math.Min(slabMainLen, bodyCrossLen) * 0.24);
        double s = Math.Max(0.5, uiScale);
        _satDriftAmp = (float)(1.6 * s);
        double mainLo = slabMain + gIcon * 0.1, mainHi = slabMain + slabMainLen - gIcon * 0.1;
        double mainSpan = mainHi - mainLo;
        _stars = Array.Empty<Vector2>(); _debMain = Array.Empty<float>();
        if (mainSpan <= 0) return;

        // Starfield over the black slab.
        var rng = new Random(0x2B17F3);
        double crossLo = bodyCross + gIcon * 0.08;
        double crossSpan = Math.Max(1.0, _satBaseEdge - crossLo);
        int sc = Math.Max(14, (int)(mainSpan / (19.0 * s)));
        var sp = new List<Vector2>(sc); var sz = new List<float>(sc); var sa = new List<float>(sc);
        var stw = new List<float>(sc); var sph = new List<float>(sc);
        for (int i = 0; i < sc; i++)
        {
            double main = mainLo + rng.NextDouble() * mainSpan;
            double cross = crossLo + rng.NextDouble() * crossSpan;
            var (lx, ly) = ToLocal(_side, main, cross, _winW, _winH);
            sp.Add(new Vector2(lx, ly));
            sz.Add((float)((0.6 + 1.7 * rng.NextDouble()) * s));
            sa.Add((float)((50 + 140 * rng.NextDouble()) / 255.0));
            if (rng.NextDouble() > 0.62) { stw.Add((float)(1.5 + 2.4 * rng.NextDouble())); sph.Add((float)(rng.NextDouble() * Math.PI * 2)); }
            else { stw.Add(0f); sph.Add(0f); }
        }
        _stars = sp.ToArray(); _starSz = sz.ToArray(); _starA = sa.ToArray(); _starTwk = stw.ToArray(); _starPhase = sph.ToArray();

        // Debris belt: dense along the interior edge plus grains through the body.
        rng = new Random(0x9C34A1);
        double innerCross = bodyCross + gIcon * 0.08;
        double beltCross = _satBaseEdge;
        int beltCount = Math.Max(16, (int)(mainSpan / (6.0 * s)));
        int bodyCount = Math.Max(20, (int)(mainSpan / (6.0 * s)));
        var dm = new List<float>(); var dc = new List<float>(); var dr = new List<float>();
        var da = new List<float>(); var dp = new List<float>();
        var dv = new List<Vector2[]>(); var dval = new List<byte>();
        void AddRock(double main, double cross, double r, double alpha)
        {
            dm.Add((float)main); dc.Add((float)cross); dr.Add((float)r); da.Add((float)alpha);
            dp.Add((float)(0.35 + Math.Clamp(r / (7.0 * s), 0.0, 1.0) * 0.65));
            // Jittered, faceted polygon offsets around the rock centre (mirrors MakeRock).
            int verts = 6 + rng.Next(3);
            var poly = new Vector2[verts];
            double a0 = rng.NextDouble() * Math.PI * 2.0;
            for (int k = 0; k < verts; k++)
            {
                double ang = a0 + (Math.PI * 2.0) * k / verts + (rng.NextDouble() - 0.5) * 0.55;
                double rad = r * (0.58 + rng.NextDouble() * 0.42);
                poly[k] = new Vector2((float)(Math.Cos(ang) * rad), (float)(Math.Sin(ang) * rad));
            }
            dv.Add(poly);
            dval.Add((byte)(70 + rng.Next(60)));   // mid-grey base value
        }
        for (int i = 0; i < beltCount; i++)
        {
            double main = mainLo + rng.NextDouble() * mainSpan;
            double g = (rng.NextDouble() + rng.NextDouble() + rng.NextDouble()) / 3.0 - 0.5;
            double cross = beltCross + g * gIcon * 1.05 - gIcon * 0.05;
            double r = (1.4 + rng.NextDouble() * rng.NextDouble() * 5.9) * s;
            AddRock(main, cross, r, 0.16 + rng.NextDouble() * 0.44);
        }
        for (int i = 0; i < bodyCount; i++)
        {
            double main = mainLo + rng.NextDouble() * mainSpan;
            double cross = innerCross + rng.NextDouble() * Math.Max(1.0, beltCross - innerCross);
            double r = (1.2 + rng.NextDouble() * rng.NextDouble() * 4.1) * s;
            AddRock(main, cross, r, 0.12 + rng.NextDouble() * 0.34);
        }
        _debMain = dm.ToArray(); _debCross = dc.ToArray(); _debR = dr.ToArray();
        _debA = da.ToArray(); _debPar = dp.ToArray(); _debCur = new float[dm.Count];
        _debVerts = dv.ToArray(); _debVal = dval.ToArray();
        DisposeRockResources();
        _debGeo = new ID2D1PathGeometry?[dm.Count];
        _debBrush = new ID2D1LinearGradientBrush?[dm.Count];
    }

    /// <summary>Releases the lazily-built per-rock geometry and gradient brushes (the
    /// geometry is device-independent, the brushes are device-bound — both are rebuilt
    /// on demand after a layout/device rebuild).</summary>
    private void DisposeRockResources()
    {
        foreach (var g in _debGeo) g?.Dispose();
        foreach (var b in _debBrush) b?.Dispose();
        _debGeo = Array.Empty<ID2D1PathGeometry?>();
        _debBrush = Array.Empty<ID2D1LinearGradientBrush?>();
    }

    /// <summary>Pre-renders the Saturn dock's fused slab+flame silhouette into a command
    /// list and wraps it in a GaussianBlur, mirroring the WPF "darkGroup": the slab and
    /// the wave-riding flame are drawn as ONE opaque black mass (no per-element opacity)
    /// and feathered by a SINGLE blur, so they share one identical soft edge instead of
    /// reading as two stacked semi-transparent layers. The panel transparency is baked
    /// into the source alpha once (the shapes don't visibly overlap at their feathered
    /// edges, so no double-darkening). Runs as its own BeginDraw pass on an alternate
    /// target, so it must be called before the main render pass. The caller draws the
    /// returned blur and disposes both handles after EndDraw.</summary>
    private (ID2D1CommandList src, Vortice.Direct2D1.Effects.GaussianBlur blur) PrepareSaturnSilhouette(ID2D1DeviceContext ctx)
    {
        byte a = (byte)Math.Clamp(255f * _opacity, 0f, 255f);
        var rr = new RoundedRectangle { Rect = new Rect(_sx, _sy, _sw, _sh), RadiusX = _trayRadius, RadiusY = _trayRadius };
        var flame = BuildFlameGeometry(ctx);

        // Keep the flame tongue clear of the slab's rounded ends (WPF clips the tongue
        // to a main-axis inset rect); the slab itself covers everything below baseEdge.
        float m0 = _satSlabMain + _trayRadius, m1 = _satSlabMain + _satSlabLen - _trayRadius;
        Rect tongueClip = _side is DockSide.Left or DockSide.Right
            ? new Rect(0f, m0, _winW, Math.Max(0f, m1 - m0))
            : new Rect(m0, 0f, Math.Max(0f, m1 - m0), _winH);

        var src = ctx.CreateCommandList();
        ctx.Target = src;
        ctx.BeginDraw();
        using (var black = ctx.CreateSolidColorBrush(Col(a, 6, 8, 12)))
        {
            ctx.FillRoundedRectangle(rr, black);
            if (flame != null)
            {
                ctx.PushAxisAlignedClip(tongueClip, AntialiasMode.PerPrimitive);
                ctx.FillGeometry(flame, black);
                ctx.PopAxisAlignedClip();
            }
        }
        ctx.EndDraw();
        src.Close();
        _host!.SetDefaultTarget();
        flame?.Dispose();

        var blur = new Vortice.Direct2D1.Effects.GaussianBlur(ctx);
        blur.SetInput(0, src, true);
        // WPF feathers the group with BlurEffect.Radius = max(12, slabFeather); a D2D
        // Gaussian standard deviation of radius/3 matches that penumbra (same ratio the
        // notch clock uses for its 14px halo).
        blur.StandardDeviation = Math.Max(12f, _flameFeather) / 3f;
        return (src, blur);
    }

    /// <summary>Builds the wave-riding "black flame" tongue as a filled path (geometry
    /// only — the fill and feather happen once in the fused silhouette pass). Returns
    /// null when no icon is magnified. Port of WPF UpdateWaveBulge.</summary>
    private ID2D1PathGeometry? BuildFlameGeometry(ID2D1DeviceContext ctx)
    {
        if (!_curActive)
            return null;
        bool vertical = _side is DockSide.Left or DockSide.Right;
        float denom = Math.Max(0.0001f, HoverScale - 1f);
        float peak = 0f, wsum = 0f, csum = 0f;
        for (int i = 0; i < _slots.Count; i++)
        {
            float a = Math.Clamp((_waveCur[i] - 1f) / denom, 0f, 1f);
            if (a <= 0f) continue;
            float w = a * a;
            float main = vertical ? _slots[i].Center.Y : _slots[i].Center.X;
            wsum += w; csum += w * main; if (a > peak) peak = a;
        }
        if (peak < 0.05f || wsum <= 0f)
            return null;
        float cm = csum / wsum;
        float baseEdge = _satBaseEdge, gIcon = _gIcon;
        float rootC = Math.Max(_bodyCross, baseEdge - _flameFeather - gIcon * 0.55f);
        double t = Environment.TickCount64 / 1000.0;
        float half = _cellH * (2.05f + 1.85f * peak);
        float flick = (float)(Math.Sin(t * 8.5) * 0.5 + Math.Sin(t * 5.3 + 1.1) * 0.5);
        float Hgt = (float)(Math.Pow(peak, 1.1) * (gIcon * 1.05) * 0.90 * (0.88 + 0.12 * flick));
        float lean = (float)((0.18 * Math.Sin(t * 3.7) + 0.10 * Math.Sin(t * 6.1 + 0.7)) * Hgt);
        const int M = 40;
        float mainLo = _satSlabMain, mainHi = _satSlabMain + _satSlabLen;
        float endPad = gIcon * 0.45f, endRamp = gIcon * 1.00f;
        static float SS(float e) { e = Math.Clamp(e, 0f, 1f); return e * e * (3f - 2f * e); }
        var geo = ctx.Factory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.BeginFigure(ToLocalV(cm - half, rootC), FigureBegin.Filled);
            for (int k = 0; k <= M; k++)
            {
                float x = -1f + 2f * k / M;
                float b = 0.5f * (1f + MathF.Cos(MathF.PI * x));
                float env = MathF.Pow(0.40f * b * b + 0.60f * b, 1.6f);
                float protUp = Hgt * env * (1f + 0.05f * MathF.Sin(3.5f * MathF.PI * x + (float)t * 5f));
                float up = MathF.Pow(Math.Clamp(protUp / Math.Max(1e-6f, Hgt), 0f, 1f), 1.3f);
                float m = cm + x * half + lean * up;
                float edgeFac = SS((m - (mainLo + endPad)) / endRamp) * SS(((mainHi - endPad) - m) / endRamp);
                protUp *= edgeFac;
                sink.AddLine(ToLocalV(m, baseEdge + protUp));
            }
            sink.AddLine(ToLocalV(cm + half, rootC));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        return geo;
    }

    private void DrawSaturnStars(ID2D1DeviceContext ctx)
    {
        if (_stars.Length == 0) return;
        double t = Environment.TickCount64 / 1000.0;
        using var br = ctx.CreateSolidColorBrush(Col(255, 255, 255, 250));
        for (int i = 0; i < _stars.Length; i++)
        {
            float a = _starA[i];
            if (_starTwk[i] > 0f)
            {
                float f = 0.5f + 0.5f * MathF.Sin((float)(t * 2 * Math.PI / _starTwk[i]) + _starPhase[i]);
                a = _starA[i] * (0.28f + 0.72f * f);
            }
            br.Opacity = a * _opacity;
            float r = _starSz[i] * 0.5f;
            ctx.FillEllipse(new Ellipse { Point = _stars[i], RadiusX = r, RadiusY = r }, br);
        }
    }

    private void DrawSaturnDebris(ID2D1DeviceContext ctx)
    {
        if (_debMain.Length == 0) return;
        double t = Environment.TickCount64 / 1000.0;
        float drift = (float)(Math.Sin(t * 2 * Math.PI / 16.0)) * _satDriftAmp;
        for (int i = 0; i < _debMain.Length; i++)
        {
            var (lx, ly) = ToLocal(_side, _debMain[i] + drift, _debCross[i], _winW, _winH);
            Vector2 push = PopOffset(_debCur[i]);
            var geo = _debGeo[i] ??= BuildRockGeometry(ctx, _debVerts[i]);
            var br = _debBrush[i] ??= BuildRockBrush(ctx, _debVal[i], _debR[i]);
            br.Opacity = _debA[i] * _opacity;
            // The faceted polygon is centred on the origin; translate it onto the rock's
            // local position (plus the magnify-wave push) so its shading rides the bulge.
            ctx.Transform = Matrix3x2.CreateTranslation(lx + push.X, ly + push.Y);
            ctx.FillGeometry(geo, br);
        }
        ctx.Transform = Matrix3x2.Identity;
    }

    /// <summary>Builds (once) a closed faceted polygon from precomputed vertex offsets.
    /// Device-independent (factory resource), so it survives a device rebuild.</summary>
    private ID2D1PathGeometry BuildRockGeometry(ID2D1DeviceContext ctx, Vector2[] verts)
    {
        var geo = ctx.Factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.BeginFigure(verts[0], FigureBegin.Filled);
        for (int k = 1; k < verts.Length; k++)
            sink.AddLine(verts[k]);
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geo;
    }

    /// <summary>Builds (once) a per-rock linear gradient shaded from a lit upper-left
    /// facet through a mid tone to a dark lower-right, so each pebble reads as a 3D rock
    /// (mirrors WPF MakeRock's gradient). Coordinates are relative to the rock centre, so
    /// the brush rides the rock's translate transform.</summary>
    private ID2D1LinearGradientBrush BuildRockBrush(ID2D1DeviceContext ctx, byte b, float r)
    {
        byte Lit(int add) => (byte)Math.Min(255, b + add);
        byte Dark(double mul) => (byte)Math.Max(0, (int)(b * mul));
        using var stops = ctx.CreateGradientStopCollection(new[]
        {
            new Vortice.Direct2D1.GradientStop { Position = 0f,    Color = new Color4(Lit(58) / 255f, Lit(54) / 255f, Lit(50) / 255f, 1f) },
            new Vortice.Direct2D1.GradientStop { Position = 0.55f, Color = new Color4(b / 255f,       b / 255f,       Lit(6) / 255f,  1f) },
            new Vortice.Direct2D1.GradientStop { Position = 1f,    Color = new Color4(Dark(0.32) / 255f, Dark(0.32) / 255f, Dark(0.40) / 255f, 1f) },
        });
        return ctx.CreateLinearGradientBrush(
            new LinearGradientBrushProperties { StartPoint = new Vector2(-0.6f * r, -0.8f * r), EndPoint = new Vector2(0.7f * r, 0.9f * r) },
            stops);
    }

    private Vector2 ToLocalV(float main, float cross)
    {
        var (x, y) = ToLocal(_side, main, cross, _winW, _winH);
        return new Vector2(x, y);
    }

    private void Render()
    {
        if (_host == null)
            return;
        var ctx = _host.Context;

        // Saturn: pre-render the fused slab+flame silhouette and blur it (its own
        // BeginDraw pass on an alternate target, so it has to happen before the main
        // pass). Stars/debris are drawn crisp on top in the main pass.
        ID2D1CommandList? satSrc = null;
        Vortice.Direct2D1.Effects.GaussianBlur? satBlur = null;
        if (_saturn)
            (satSrc, satBlur) = PrepareSaturnSilhouette(ctx);

        ctx.BeginDraw();
        ctx.Clear(Col(0, 0, 0, 0));
        if (_saturn)
        {
            if (satBlur != null)
                ctx.DrawImage(satBlur, new Vector2(0, 0), InterpolationMode.Linear, CompositeMode.SourceOver);
            DrawSaturnStars(ctx);
            DrawSaturnDebris(ctx);
        }
        else
            GlassSlab.DrawGlass(ctx, _sx, _sy, _sw, _sh, _trayRadius, _opacity, _frost);
        if (!_saturn)
            DrawOrbitLight(ctx);
        if (_pinnedVisible > 0)
            DrawSeam(ctx);

        // Draw smallest-first so the magnified (focal) icon sits on top. The icon
        // being dragged is skipped here and drawn last at the cursor.
        var order = new int[_slots.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => _waveCur[a].CompareTo(_waveCur[b]));
        int dragIdx = _dragging ? _pressIdx : -1;
        foreach (int i in order)
        {
            if (i == dragIdx)
                continue;
            float scale = _waveCur[i];
            Vector2 pop = PopOffset((scale - 1f) * _gIcon * 1.18f + BounceOffset(i));
            if (i < _dragShift.Length && _dragShift[i] != 0f)
                pop += (_side is DockSide.Left or DockSide.Right)
                    ? new Vector2(0f, _dragShift[i]) : new Vector2(_dragShift[i], 0f);
            DrawIcon(ctx, _slots[i], scale, pop);
        }

        if (dragIdx >= 0 && dragIdx < _slots.Count)
        {
            // The dragged icon follows the cursor, lifted 1.12x with no pop.
            var s = _slots[dragIdx];
            var moved = new Slot(new Vector2(_dragMain, _dragCross), s.G, s.Name, s.Running,
                s.Kind, s.IconKey, s.Image, s.Entry, s.Window);
            DrawIcon(ctx, moved, 1.12f, Vector2.Zero);
        }
        else if (_hover >= 0 && _hover < _slots.Count)
            DrawHoverLabel(ctx, _slots[_hover], _waveCur[_hover]);
        ctx.EndDraw();
        _host.Present();
        satBlur?.Dispose();
        satSrc?.Dispose();
    }

    /// <summary>Glass orbit light: a cool lamp drifts around the slab (one rev / 36s),
    /// its radial glow clipped to the rounded glass body — parity with GlassOrbitLight
    /// and the GPU main dock's DrawOrbitLight.</summary>
    private void DrawOrbitLight(ID2D1DeviceContext ctx)
    {
        float cx = _sx + _sw / 2f, cy = _sy + _sh / 2f;
        float orbitR = MathF.Max(_sw, _sh) * 0.5f + _gIcon * 1.4f;
        float lampR = orbitR * 1.3f;
        float th = _orbitAngle * MathF.PI / 180f;
        var lamp = new Vector2(cx + orbitR * MathF.Sin(th), cy - orbitR * MathF.Cos(th));
        using var stops = ctx.CreateGradientStopCollection(new[]
        {
            new Vortice.Direct2D1.GradientStop { Position = 0f,    Color = Col(0x3C, 0xCF, 0xEC, 0xFF) },
            new Vortice.Direct2D1.GradientStop { Position = 0.34f, Color = Col(0x22, 0x76, 0xC4, 0xFF) },
            new Vortice.Direct2D1.GradientStop { Position = 0.62f, Color = Col(0x0A, 0x4C, 0x9E, 0xF0) },
            new Vortice.Direct2D1.GradientStop { Position = 1f,    Color = Col(0x00, 0x3A, 0x86, 0xE0) },
        });
        using var brush = ctx.CreateRadialGradientBrush(
            new RadialGradientBrushProperties { Center = lamp, RadiusX = lampR, RadiusY = lampR }, stops);
        var slab = new RoundedRectangle { Rect = new Rect(_sx, _sy, _sw, _sh), RadiusX = _trayRadius, RadiusY = _trayRadius };
        ctx.FillRoundedRectangle(slab, brush);
    }

    private void DrawIcon(ID2D1DeviceContext ctx, in Slot s, float scale, Vector2 pop)
    {
        float g = s.G, half = g / 2f, cx = s.Center.X, cy = s.Center.Y;
        var wave = Matrix3x2.CreateScale(scale, scale, s.Center) * Matrix3x2.CreateTranslation(pop);

        ctx.Transform = wave;
        var plate = new Rect(cx - half, cy - half, g, g);

        if (s.Kind == SlotKind.Overflow)
        {
            // Taskbar-style "+N" overflow marker — no icon, just centred text.
            if (_labelFormat != null && !string.IsNullOrEmpty(s.Name))
                using (var ink = ctx.CreateSolidColorBrush(Col(0xE6, 0xFF, 0xFF, 0xFF)))
                    ctx.DrawText(s.Name, _labelFormat, plate, ink);
        }
        else
        {
            var bmp = GetBitmap(ctx, s.IconKey, s.Image);
            if (bmp != null)
            {
                float pad = g * 0.14f, dstX = cx - half + pad, dstY = cy - half + pad, dstSz = g - pad * 2;
                var bs = bmp.Size;
                ctx.Transform = Matrix3x2.CreateScale(dstSz / Math.Max(1f, bs.Width), dstSz / Math.Max(1f, bs.Height))
                              * Matrix3x2.CreateTranslation(dstX, dstY) * wave;
                ctx.DrawBitmap(bmp, 1f, InterpolationMode.HighQualityCubic);
                ctx.Transform = wave;
            }
        }

        if (s.Running)
        {
            float dot = Math.Max(2.6f, g * 0.07f), glow = dot * 2.3f;
            (float dx, float dy) = _side switch
            {
                DockSide.Left => (cx - half + dot * 0.05f, cy),
                DockSide.Right => (cx + half - dot * 0.05f, cy),
                DockSide.Top => (cx, cy - half + dot * 0.05f),
                _ => (cx, cy + half - dot * 0.05f),
            };
            // Soft halo via a radial gradient (green core → transparent) instead of a
            // hard-edged disc, so it reads as the same subtle breathing glow as the
            // WPF dock's blurred ellipse rather than an oversized solid blob. The halo
            // breathes with _runPulse (2.2s) and the glow radius swells slightly so the
            // dot pulses like the WPF ambient-driven RunningDot rather than sitting flat.
            float breath = _runPulse / 0.575f;                 // ~0.61..1.39, centred on 1
            float gr = glow / 2f * (0.85f + 0.18f * breath);   // swell the halo radius
            var center = new Vector2(dx, dy);
            using (var stops = ctx.CreateGradientStopCollection(new[]
            {
                new Vortice.Direct2D1.GradientStop { Position = 0f, Color = new Color4(0x5C / 255f, 1f, 0x7A / 255f, 0.45f * breath) },
                new Vortice.Direct2D1.GradientStop { Position = 0.5f, Color = new Color4(0x5C / 255f, 1f, 0x7A / 255f, 0.18f * breath) },
                new Vortice.Direct2D1.GradientStop { Position = 1f, Color = new Color4(0x5C / 255f, 1f, 0x7A / 255f, 0f) },
            }))
            using (var gl = ctx.CreateRadialGradientBrush(
                new RadialGradientBrushProperties { Center = center, GradientOriginOffset = Vector2.Zero, RadiusX = gr, RadiusY = gr },
                stops))
                ctx.FillEllipse(new Ellipse(center, gr, gr), gl);
            using (var co = ctx.CreateSolidColorBrush(new Color4(0x4C / 255f, 0xE0 / 255f, 0x6B / 255f, 1f)))
                ctx.FillEllipse(new Ellipse(center, dot / 2f, dot / 2f), co);
        }

        // New-message attention dot: a small pulsing red disc hugging the icon's
        // lower-left corner when any of this app's windows is flashing for attention
        // (parity with RadialIcon's lower-left AttentionBadge and the GPU main dock).
        if (s.Running && _flashKeys.Contains(s.IconKey))
        {
            ctx.Transform = wave;
            float d = Math.Clamp(g * 0.12f, 5f, 10f) * _badgePulse;
            var bp = new Vector2(cx - half + d * 0.55f, cy + half - d * 0.55f);
            using (var glow = ctx.CreateSolidColorBrush(Col(0x55, 0xFF, 0x3B, 0x30)))
                ctx.FillEllipse(new Ellipse(bp, d * 0.78f, d * 0.78f), glow);
            using (var rd = ctx.CreateSolidColorBrush(Col(0xFF, 0xFF, 0x3B, 0x30)))
                ctx.FillEllipse(new Ellipse(bp, d * 0.5f, d * 0.5f), rd);
        }
        ctx.Transform = Matrix3x2.Identity;
    }

    private void DrawHoverLabel(ID2D1DeviceContext ctx, in Slot s, float scale)
    {
        if (_hoverFormat == null || string.IsNullOrEmpty(s.Name))
            return;
        // Clear the magnified + popped focal icon.
        float reach = s.G / 2f * scale + (scale - 1f) * _gIcon * 1.18f;
        float fp = _hoverFontPx;
        float w = Math.Max(40f, s.Name.Length * fp * 0.95f + 20f), h = fp + 12f, gap = 8f;
        (float lx, float ly) = _side switch
        {
            DockSide.Left => (s.Center.X + reach + gap + w / 2f, s.Center.Y),
            DockSide.Right => (s.Center.X - reach - gap - w / 2f, s.Center.Y),
            DockSide.Top => (s.Center.X, s.Center.Y + reach + gap + h / 2f),
            _ => (s.Center.X, s.Center.Y - reach - gap - h / 2f),
        };
        var rect = new Rect(lx - w / 2f, ly - h / 2f, w, h);
        // The real hover label is just floating text on a barely-there dark tint
        // (ARGB 0x05,1A1A1A) — no visible plate.
        using (var bg = ctx.CreateSolidColorBrush(Col(0x05, 0x1A, 0x1A, 0x1A)))
            ctx.FillRoundedRectangle(new RoundedRectangle { Rect = rect, RadiusX = 7f, RadiusY = 7f }, bg);
        // 3-D raised lettering: dark offset copies behind the light text give the name
        // depth and a legibility halo, mirroring the WPF DropShadowEffect (black, depth
        // 1.4, direction 315° → a ~1px down-right offset, plus a soft second copy).
        using (var halo = ctx.CreateSolidColorBrush(Col(0xE6, 0, 0, 0)))
        {
            ctx.DrawText(s.Name, _hoverFormat, new Rect(rect.X + 1f, rect.Y + 1.2f, rect.Width, rect.Height), halo);
            ctx.DrawText(s.Name, _hoverFormat, new Rect(rect.X - 0.6f, rect.Y + 0.5f, rect.Width, rect.Height), halo);
        }
        using (var ink = ctx.CreateSolidColorBrush(Col(0xF2, 0xFF, 0xFF, 0xFF)))
            ctx.DrawText(s.Name, _hoverFormat, rect, ink);
    }

    private ID2D1Bitmap? GetBitmap(ID2D1DeviceContext ctx, string key, BitmapSource? src)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        if (_bmpCache.TryGetValue(key, out var cached))
            return cached;
        ID2D1Bitmap? d2d = null;
        try
        {
            if (src != null)
            {
                if (src.Format != PixelFormats.Pbgra32)
                    src = new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
                int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
                var px = new byte[stride * h];
                src.CopyPixels(px, stride, 0);
                d2d = _host!.CreateBitmap(w, h, px, stride);
            }
        }
        catch { d2d = null; }
        _bmpCache[key] = d2d;
        return d2d;
    }

    /// <summary>Light-split divider between the pinned column and the running strip:
    /// a soft cool glow plus a bright glassy highlight, drawn across the body at
    /// <see cref="_seamMain"/> (mirrors the WPF dock's <c>DrawSeam</c>).</summary>
    private void DrawSeam(ID2D1DeviceContext ctx)
    {
        (float ax, float ay) = ToLocal(_side, _seamMain, _bodyCross + 10f, _winW, _winH);
        (float bx, float by) = ToLocal(_side, _seamMain, _bodyCross + _bodyCrossLen - 10f, _winW, _winH);
        var p0 = new Vector2(ax, ay);
        var p1 = new Vector2(bx, by);
        ctx.Transform = Matrix3x2.Identity;
        // Approximate the WPF BlurEffect glow with two stacked translucent strokes.
        using (var glowWide = ctx.CreateSolidColorBrush(Col(0x40, 0xBF, 0xE0, 0xFF)))
            ctx.DrawLine(p0, p1, glowWide, 7f);
        using (var glow = ctx.CreateSolidColorBrush(Col(0x90, 0xBF, 0xE0, 0xFF)))
            ctx.DrawLine(p0, p1, glow, 4f);
        using (var shine = ctx.CreateSolidColorBrush(Col(0xDD, 0xEA, 0xF4, 0xFF)))
            ctx.DrawLine(p0, p1, shine, 1f);
    }

    private readonly struct RunItem
    {
        public readonly string Name, IconKey;
        public readonly BitmapSource? Image;
        public readonly IntPtr Window;
        public readonly string? Path, Aumid;
        public RunItem(string name, string key, BitmapSource? img, IntPtr window, string? path, string? aumid)
        { Name = name; IconKey = key; Image = img; Window = window; Path = path; Aumid = aumid; }
    }

    /// <summary>Collects running-but-unpinned taskbar apps for the running strip.
    /// A lightweight version of the WPF dock's filter (excludes pinned apps by full
    /// path / file name) — enough for the spike's visual parity.</summary>
    private List<RunItem> CollectRunning(IReadOnlyList<AppEntry> pinned, out int overflow)
    {
        overflow = 0;
        var result = new List<RunItem>();
        try
        {
            var excludePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var excludeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var excludeAumids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddPathAndName(string p)
            {
                try { excludePaths.Add(System.IO.Path.GetFullPath(p)); } catch { excludePaths.Add(p); }
                try { var fn = System.IO.Path.GetFileName(p); if (!string.IsNullOrWhiteSpace(fn)) excludeNames.Add(fn); }
                catch { /* unreadable path */ }
            }
            foreach (var a in pinned)
            {
                if (string.IsNullOrWhiteSpace(a.Path))
                    continue;
                // Mirror the WPF dock: resolve each pinned app's launcher AUMID and
                // its real AppsFolder exe, so AppsFolder-launched pins (Edge, VS Code…)
                // — whose a.Path is a pseudo-launcher, not the running exe — are still
                // matched and excluded from the running strip (no duplicate tile).
                string? aumid = WindowPreviewService.TryGetLauncherAumid(a.Path, a.Arguments);
                if (aumid != null)
                {
                    excludeAumids.Add(aumid);
                    string? exe = WindowPreviewService.TryResolveAppsFolderExe(aumid);
                    if (!string.IsNullOrWhiteSpace(exe))
                        AddPathAndName(exe);
                }
                else
                {
                    AddPathAndName(a.Path);
                }
            }

            var filtered = new List<TaskbarApp>();
            foreach (var ta in WindowPreviewService.GetTaskbarApps())
            {
                string full;
                try { full = System.IO.Path.GetFullPath(ta.Path); } catch { full = ta.Path; }
                if (!string.IsNullOrEmpty(full) && excludePaths.Contains(full))
                    continue;
                if (ta.Aumid != null)
                {
                    bool excluded = excludeAumids.Contains(ta.Aumid);
                    if (!excluded)
                        foreach (var ex in excludeAumids)
                            if (WindowPreviewService.AumidFamilyMatches(ta.Aumid, ex)) { excluded = true; break; }
                    if (excluded)
                        continue;
                }
                try { var fn = System.IO.Path.GetFileName(ta.Path); if (!string.IsNullOrWhiteSpace(fn) && excludeNames.Contains(fn)) continue; }
                catch { /* unreadable path */ }
                filtered.Add(ta);
            }

            const int max = 10;   // RunningMaxComplete
            if (filtered.Count > max)
            {
                overflow = filtered.Count - max;
                filtered = filtered.GetRange(0, max);
            }
            foreach (var ta in filtered)
            {
                bool pathless = string.IsNullOrEmpty(ta.Path);
                string key = !string.IsNullOrEmpty(ta.Aumid) ? "aumid:" + ta.Aumid
                           : (pathless ? "win:" + ta.Window : ta.Path);
                result.Add(new RunItem(FriendlyRunName(ta, pathless), key, ResolveRunIcon(ta, pathless), ta.Window, ta.Path, ta.Aumid));
            }
        }
        catch (Exception ex) { Log.Warn("SideDockGpu", "running collect failed: " + ex.Message); }
        return result;
    }

    /// <summary>A human-friendly label for a running app: the executable's embedded
    /// product description (e.g. "Microsoft Edge"), then its file name, then the raw
    /// window title for path-protected / UWP windows. The window title alone is a
    /// poor label — a browser's is the full page name, a terminal's is the tab.</summary>
    private static string FriendlyRunName(TaskbarApp ta, bool pathless)
    {
        if (!pathless && !string.IsNullOrEmpty(ta.Path))
        {
            try
            {
                var desc = System.Diagnostics.FileVersionInfo.GetVersionInfo(ta.Path).FileDescription;
                if (!string.IsNullOrWhiteSpace(desc))
                    return desc.Trim();
            }
            catch { /* unreadable metadata — fall through */ }
            try { return System.IO.Path.GetFileNameWithoutExtension(ta.Path); }
            catch { /* unreadable path */ }
        }
        return ta.Title ?? "";
    }

    private static BitmapSource? ResolveRunIcon(TaskbarApp ta, bool pathless)
    {
        try
        {
            // A real Win32 exe (not a WindowsApps-packaged path) carries its own
            // icon and is the most reliable source. The AppsFolder lookup is only
            // preferable for packaged/UWP apps whose exe has no embedded icon — and
            // for some apps (e.g. Edge launched under a profile-specific AUMID) it
            // returns a generic blank document, so try the exe icon first here.
            bool packaged = !string.IsNullOrEmpty(ta.Path)
                && ta.Path.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!packaged && !string.IsNullOrEmpty(ta.Path))
            {
                var pb = IconExtractor.GetIcon(ta.Path);
                if (pb != null)
                    return pb;
            }
            if (!string.IsNullOrEmpty(ta.Aumid))
            {
                var b = IconExtractor.GetIcon(ShellNamespace.NormalizeAppsFolderPath(ta.Aumid));
                if (b == null && ta.Window != IntPtr.Zero)
                    b = WindowPreviewService.GetWindowIconImage(ta.Window);
                return b;
            }
            return pathless
                ? WindowPreviewService.GetWindowIconImage(ta.Window)
                : (IconExtractor.GetIcon(ta.Path)
                   ?? (ta.Window != IntPtr.Zero ? WindowPreviewService.GetWindowIconImage(ta.Window) : null));
        }
        catch { return null; }
    }

    private static BitmapSource? SafeIcon(string path)
    {
        try { return string.IsNullOrEmpty(path) ? null : IconExtractor.GetIcon(path); }
        catch { return null; }
    }

    public void Dispose()
    {
        _timer?.Stop();
        CloseSlotMenu();
        ClosePreview();
        if (_anchorWin != null) { try { _anchorWin.Close(); } catch { } _anchorWin = null; _anchorEl = null; _preview = null; }
        foreach (var b in _bmpCache.Values) b?.Dispose();
        _bmpCache.Clear();
        DisposeRockResources();
        _labelFormat?.Dispose(); _labelFormat = null;
        _hoverFormat?.Dispose(); _hoverFormat = null;
        _host?.Dispose();
        if (_hwnd != IntPtr.Zero) { s_instances.Remove(_hwnd); DestroyWindow(_hwnd); }
    }

    // ---- Interaction (Stage E): click-launch, drag-reorder, drag-out-unpin ----

    private const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_NCHITTEST = 0x0084;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_DROPFILES = 0x0233;
    private const int HTTRANSPARENT = -1, HTCLIENT = 1;

    [DllImport("shell32.dll")] private static extern void DragAcceptFiles(IntPtr hwnd, bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, System.Text.StringBuilder? buf, uint cch);
    [DllImport("shell32.dll")] private static extern bool DragQueryPoint(IntPtr hDrop, out POINT pt);
    [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr hDrop);

    /// <summary>Routes the raw window messages this instance cares about. Returns
    /// true (with <paramref name="result"/>) when handled; false defers to DefWindowProc.</summary>
    private bool HandleMessage(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        switch (msg)
        {
            case WM_DROPFILES:
            {
                IntPtr hDrop = wParam;
                try
                {
                    uint count = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
                    DragQueryPoint(hDrop, out POINT pt);
                    var paths = new List<string>();
                    for (uint i = 0; i < count; i++)
                    {
                        uint len = DragQueryFileW(hDrop, i, null, 0);
                        var sb = new System.Text.StringBuilder((int)len + 1);
                        if (DragQueryFileW(hDrop, i, sb, len + 1) > 0)
                            paths.Add(sb.ToString());
                    }
                    HandleDropFiles(paths, pt);
                }
                catch (Exception ex) { Log.Warn("SideDockGpu", "drop-in failed: " + ex.Message); }
                finally { DragFinish(hDrop); }
                return true;
            }
            case WM_NCHITTEST:
            {
                // lParam = SCREEN coords. Inside the glass slab → grab the click;
                // elsewhere → HTTRANSPARENT so the empty reserve passes through.
                int sx = unchecked((short)((long)lParam & 0xFFFF));
                int sy = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
                float lx = (float)(sx / _dpi - _winX), ly = (float)(sy / _dpi - _winY);
                result = InsideHitRegion(lx, ly) ? HTCLIENT : HTTRANSPARENT;
                return true;
            }
            case WM_LBUTTONDOWN:
            {
                (float lx, float ly) = ClientDip(lParam);
                _pressMain = lx; _pressCross = ly;
                _dragMain = lx; _dragCross = ly;
                _pressIdx = HitSlot(lx, ly);
                _dragging = false;
                if (_pressIdx >= 0)
                {
                    // Pin the dock for the whole press-drag-release gesture (parity
                    // with the WPF dock's Icon_PreviewMouseLeftButtonDown): the edge
                    // poll fires every 100 ms, so without this a drag that travels
                    // off the narrow slab could let the poll hide the dock mid-gesture.
                    SetDragActive(true);
                    SetCapture(_hwnd);
                }
                return true;
            }
            case WM_MOUSEMOVE:
            {
                if (_pressIdx < 0)
                    return false;
                (float lx, float ly) = ClientDip(lParam);
                _dragMain = lx; _dragCross = ly;
                if (!_dragging && (MathF.Abs(lx - _pressMain) + MathF.Abs(ly - _pressCross)) > DragThreshold)
                {
                    _dragging = true;
                    _dragShiftLastMs = Environment.TickCount64;   // seed so first advance dt is small
                }
                if (_dragging)
                {
                    UpdateDragGap(lx, ly);
                    AdvanceDragShift();   // keep neighbours in step with the cursor between ticks
                    Render();
                }
                return true;
            }
            case WM_LBUTTONUP:
            {
                ReleaseCapture();
                SetDragActive(false);   // release the press-drag hold; edge poll resumes
                int idx = _pressIdx;
                bool wasDrag = _dragging;
                (float lx, float ly) = ClientDip(lParam);
                _pressIdx = -1;
                _dragging = false;
                _dragInsert = -1;
                if (idx >= 0 && idx < _slots.Count)
                {
                    if (!wasDrag) ClickSlot(idx);
                    else DropSlot(idx, lx, ly);
                }
                if (_hwnd != IntPtr.Zero)
                    Render();
                return true;
            }
            case WM_RBUTTONUP:
            {
                (float lx, float ly) = ClientDip(lParam);
                int idx = HitSlot(lx, ly);
                if (idx >= 0 && idx < _slots.Count)
                    ShowSlotMenu(idx);
                return true;
            }
        }
        return false;
    }

    private (float lx, float ly) ClientDip(IntPtr lParam)
    {
        int cx = unchecked((short)((long)lParam & 0xFFFF));
        int cy = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
        return ((float)(cx / _dpi), (float)(cy / _dpi));
    }

    /// <summary>Index of the slot whose icon is under the (window-local DIP) point,
    /// or -1.</summary>
    private int HitSlot(float lx, float ly)
    {
        int best = -1; float bestD = _gIcon * 0.75f;
        for (int i = 0; i < _slots.Count; i++)
        {
            var c = _slots[i].Center;
            float d = MathF.Abs(lx - c.X) + MathF.Abs(ly - c.Y);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private void ClickSlot(int idx)
    {
        var s = _slots[idx];
        try
        {
            if (s.Kind == SlotKind.Pinned && s.Entry != null)
            {
                _bounceIdx = idx; _bounceStart = Environment.TickCount64;   // launch hop
                AppLauncher.Launch(s.Entry, null);
            }
            else if (s.Kind == SlotKind.Run && s.Window != IntPtr.Zero)
                WindowPreviewService.Activate(s.Window);
            else if (s.Kind == SlotKind.Run && s.Window == IntPtr.Zero
                     && s.IconKey.StartsWith("polaris:", StringComparison.Ordinal))
                ToggleDocks?.Invoke();   // the Polaris tile toggles the pinned docks
        }
        catch (Exception ex) { Log.Warn("SideDockGpu", "launch failed: " + ex.Message); }
    }

    // ---- Right-click context menu (parity with the WPF dock) -----------------

    private System.Windows.Controls.Primitives.Popup? _slotMenu;

    /// <summary>Shows a dock-styled right-click menu for the slot under the cursor:
    /// pinned icons offer unpin (+ close window when running); running-strip tiles
    /// offer pin-to-resident + close. Mirrors <c>SideDockWindow.Menu.cs</c>.</summary>
    private void ShowSlotMenu(int idx)
    {
        var s = _slots[idx];
        var items = new List<(string text, Action action)>();
        if (s.Kind == SlotKind.Pinned && s.Entry != null)
        {
            var entry = s.Entry;
            items.Add(("从常驻区取消固定", () => UnpinPinned(entry)));
            if (s.Running)
                items.Add(("关闭窗口", () => CloseSlotWindows(s)));
        }
        else if (s.Kind == SlotKind.Run)
        {
            if (!string.IsNullOrWhiteSpace(s.RunPath) || !string.IsNullOrWhiteSpace(s.RunAumid))
                items.Add(("固定到常驻区", () => PinRunningSlot(s)));
            items.Add(("关闭窗口", () => CloseSlotWindows(s)));
        }
        if (items.Count == 0)
            return;
        BuildAndShowSlotMenu(s, items);
    }

    private void BuildAndShowSlotMenu(in Slot s, List<(string text, Action action)> items)
    {
        CloseSlotMenu();

        bool light = SystemTheme.IsLight;
        var textColor   = light ? System.Windows.Media.Color.FromArgb(0xF0, 0x1B, 0x1B, 0x1F) : System.Windows.Media.Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF);
        var hoverColor  = light ? System.Windows.Media.Color.FromArgb(0x14, 0x00, 0x00, 0x00) : System.Windows.Media.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF);
        var shellColor  = light ? System.Windows.Media.Color.FromArgb(0xF4, 0xF3, 0xF3, 0xF6) : System.Windows.Media.Color.FromArgb(0xF2, 0x1E, 0x1E, 0x22);
        var borderColor = light ? System.Windows.Media.Color.FromArgb(0x22, 0x00, 0x00, 0x00) : System.Windows.Media.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);

        var panel = new System.Windows.Controls.StackPanel();
        foreach (var (text, action) in items)
        {
            var label = new System.Windows.Controls.TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                FontSize = 13 * FontScale.Current,
                Foreground = new SolidColorBrush(textColor),
            };
            var row = new System.Windows.Controls.Border
            {
                CornerRadius = new System.Windows.CornerRadius(6),
                Background = System.Windows.Media.Brushes.Transparent,
                Padding = new System.Windows.Thickness(13, 7, 18, 7),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = label,
            };
            row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(hoverColor);
            row.MouseLeave += (_, _) => row.Background = System.Windows.Media.Brushes.Transparent;
            var act = action;
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                CloseSlotMenu();
                try { act(); } catch (Exception ex) { Log.Warn("SideDockGpu", "menu action failed: " + ex.Message); }
            };
            panel.Children.Add(row);
        }

        var shell = new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(shellColor),
            CornerRadius = new System.Windows.CornerRadius(10),
            BorderBrush = new SolidColorBrush(borderColor),
            BorderThickness = new System.Windows.Thickness(1),
            Padding = new System.Windows.Thickness(5),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 20, ShadowDepth = 3, Direction = 270,
                Opacity = light ? 0.28 : 0.5, Color = System.Windows.Media.Colors.Black,
            },
            Child = panel,
        };

        // Anchor the menu just past the icon, opening toward the screen interior.
        // All of _winX/_winY/_gIcon/s.Center are layout DIPs (the window is sized in
        // DIPs and positioned at _winX*_dpi), and WPF Popup.Absolute offsets are
        // screen DIPs — so screen-DIP icon centre = _winX + s.Center. Measure the
        // shell so we can centre / offset it without a WPF placement target.
        shell.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var ds = shell.DesiredSize;
        double half = _gIcon / 2.0; const double gap = 4.0;
        double cxDip = _winX + s.Center.X;
        double cyDip = _winY + s.Center.Y;
        double px, py;
        switch (_side)
        {
            case DockSide.Left:   px = cxDip + half + gap;            py = cyDip - ds.Height / 2.0; break;
            case DockSide.Right:  px = cxDip - half - gap - ds.Width; py = cyDip - ds.Height / 2.0; break;
            case DockSide.Top:    px = cxDip - ds.Width / 2.0;        py = cyDip + half + gap; break;
            default:              px = cxDip - ds.Width / 2.0;        py = cyDip - half - gap - ds.Height; break;  // Bottom → above
        }
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            Child = shell,
            StaysOpen = false,
            AllowsTransparency = true,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute,
            HorizontalOffset = px,
            VerticalOffset = py,
            PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade,
        };
        popup.Closed += (_, _) => { if (ReferenceEquals(_slotMenu, popup)) _slotMenu = null; };
        _slotMenu = popup;
        popup.IsOpen = true;
    }

    private void CloseSlotMenu()
    {
        if (_slotMenu != null) { _slotMenu.IsOpen = false; _slotMenu = null; }
    }

    /// <summary>Closes every window of the slot's app (right-click "关闭窗口").</summary>
    private void CloseSlotWindows(in Slot s)
    {
        List<WindowPreview> wins;
        try
        {
            if (s.Kind == SlotKind.Pinned && s.Entry != null && !string.IsNullOrWhiteSpace(s.Entry.Path))
                wins = WindowPreviewService.GetWindowsForEntry(s.Entry.Path, s.Entry.Arguments);
            else if (!string.IsNullOrWhiteSpace(s.RunAumid))
                wins = WindowPreviewService.GetWindowsByAumid(s.RunAumid!);
            else if (!string.IsNullOrWhiteSpace(s.RunPath))
                wins = WindowPreviewService.GetWindowsForEntry(s.RunPath!, null);
            else
                wins = WindowPreviewService.GetWindowsByHandle(s.Window);
        }
        catch { wins = new List<WindowPreview>(); }
        if (wins.Count == 0 && s.Window != IntPtr.Zero)
            WindowPreviewService.CloseWindow(s.Window);
        else
            foreach (var w in wins)
                WindowPreviewService.CloseWindow(w.Handle);
        // Refresh the strip shortly after so the closed tile drops off.
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        t.Tick += (o, _) => { ((DispatcherTimer)o!).Stop(); if (_hwnd != IntPtr.Zero) Rebuild(); };
        t.Start();
    }

    /// <summary>Pins a running-strip app to the resident region (right-click).</summary>
    private void PinRunningSlot(in Slot s)
    {
        AppEntry? entry = ShellNamespace.FromAumid(s.RunAumid);
        if (entry == null && !string.IsNullOrWhiteSpace(s.RunPath))
            entry = ShortcutResolver.CreateEntry(s.RunPath!);
        if (entry == null)
            return;
        if (!string.IsNullOrWhiteSpace(s.Name) && string.IsNullOrWhiteSpace(entry.Name))
            entry.Name = s.Name;
        if (_config.Apps.FindIndex(a => DockSync.Matches(a, entry)) >= 0)
            return;   // already pinned
        DockSync.InsertResident(_config, entry, DockSync.ResidentCount(_config));
        PersistAndRebuild();
    }

    // ---- Hover window-thumbnail preview (parity with the WPF dock) -----------
    // The polished thumbnail popup (live capture, caching, per-tile close button,
    // minimized / no-preview fallbacks) lives in WindowPreviewPopup, which anchors
    // to a WPF FrameworkElement. The GPU dock has no per-icon WPF visuals, so we
    // park a tiny, transparent, click-through anchor window over the hovered icon
    // and use it as the popup's placement target. A single popup is reused; its own
    // open/close dwell timers drive show/hide as the GPU dock reports hover changes.

    private System.Windows.Window? _anchorWin;
    private System.Windows.Controls.Border? _anchorEl;
    private WindowPreviewPopup? _preview;
    private Func<List<WindowPreview>>? _previewSource;
    private int _prevHover = -1;

    private const int GWL_EXSTYLE = -20;
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr h, int idx);
    [DllImport("user32.dll")] private static extern int SetWindowLongW(IntPtr h, int idx, int val);

    /// <summary>Builds the windows-source delegate for a slot, or null when the slot
    /// has no previewable windows (overflow, or our own Polaris tile).</summary>
    private Func<List<WindowPreview>>? PreviewSourceFor(in Slot s)
    {
        if (s.Kind == SlotKind.Pinned && s.Entry != null && !string.IsNullOrWhiteSpace(s.Entry.Path))
        {
            var path = s.Entry.Path; var args = s.Entry.Arguments;
            return () => WindowPreviewService.GetWindowsForEntry(path, args);
        }
        if (s.Kind == SlotKind.Run)
        {
            if (!string.IsNullOrWhiteSpace(s.RunAumid))
            { var a = s.RunAumid!; return () => WindowPreviewService.GetWindowsByAumid(a); }
            if (!string.IsNullOrWhiteSpace(s.RunPath))
            { var p = s.RunPath!; return () => WindowPreviewService.GetWindowsForEntry(p, null); }
            if (s.Window != IntPtr.Zero)
            { var w = s.Window; return () => WindowPreviewService.GetWindowsByHandle(w); }
        }
        return null;
    }

    /// <summary>Called every Tick with the slot under the cursor (or -1). Drives the
    /// reusable thumbnail popup: re-anchors and re-enters on a slot change, leaves on
    /// exit. The popup's own dwell timers handle the open/close delays and the
    /// icon→popup pointer travel.</summary>
    private void DrivePreview(int hover)
    {
        if (hover == _prevHover)
            return;
        if (_prevHover >= 0)
            _preview?.OnPointerLeave();
        _prevHover = hover;
        if (hover < 0 || hover >= _slots.Count)
            return;
        var s = _slots[hover];
        var src = PreviewSourceFor(s);
        if (src == null)
            return;
        _previewSource = src;
        EnsureAnchor();
        AnchorOverSlot(s);
        _preview!.Placement = PreviewPlacementForSide();
        _preview.OnPointerEnter();
    }

    private PreviewPlacement PreviewPlacementForSide() => _side switch
    {
        DockSide.Left => PreviewPlacement.Right,
        DockSide.Right => PreviewPlacement.Left,
        DockSide.Top => PreviewPlacement.Below,
        _ => PreviewPlacement.Above,
    };

    private void EnsureAnchor()
    {
        if (_anchorWin != null)
            return;
        _anchorEl = new System.Windows.Controls.Border { Background = System.Windows.Media.Brushes.Transparent };
        _anchorWin = new System.Windows.Window
        {
            WindowStyle = System.Windows.WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            Width = 1, Height = 1, Left = -10000, Top = -10000,
            Content = _anchorEl,
        };
        _anchorWin.Show();
        // Make the anchor fully click-through / non-activating so it never steals
        // clicks from the GPU dock icon it sits over.
        var h = new System.Windows.Interop.WindowInteropHelper(_anchorWin).Handle;
        int ex = GetWindowLongW(h, GWL_EXSTYLE);
        SetWindowLongW(h, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        _preview = new WindowPreviewPopup(
            _anchorEl,
            () => _previewSource?.Invoke() ?? new List<WindowPreview>(),
            minWindows: 1,
            onActivated: null);
    }

    /// <summary>Positions the anchor window so its element exactly overlaps the
    /// hovered icon's FULLY-MAGNIFIED, popped-out glyph box (screen DIPs), matching the
    /// WPF preview which anchors to the icon visual carrying the wave's scale + pop. The
    /// popup then opens clear of the enlarged icon (same vertical clearance as the
    /// original) instead of overlapping it.</summary>
    private void AnchorOverSlot(in Slot s)
    {
        if (_anchorWin == null)
            return;
        float scale = HoverScale;
        Vector2 po = PopOffset((scale - 1f) * _gIcon * 1.18f);
        double size = _gIcon * scale;
        double cx = _winX + s.Center.X + po.X;
        double cy = _winY + s.Center.Y + po.Y;
        _anchorWin.Width = size;
        _anchorWin.Height = size;
        _anchorWin.Left = cx - size / 2.0;
        _anchorWin.Top = cy - size / 2.0;
    }

    private void ClosePreview()
    {
        _preview?.Close();
        _prevHover = -1;
    }

    /// <summary>Advances the per-slot "push aside" offsets toward their targets using
    /// real elapsed time (exponential ease, <see cref="DragShiftTauMs"/>). Driven from
    /// both <c>Tick</c> and <c>WM_MOUSEMOVE</c> so the gap stays in lockstep with the
    /// cursor-tracked drag icon and is immune to DispatcherTimer jitter. Returns the
    /// largest remaining distance so callers can keep the render loop alive.</summary>
    private float AdvanceDragShift()
    {
        if (_dragShift.Length == 0)
            return 0f;
        long now = Environment.TickCount64;
        float dt = (now - _dragShiftLastMs) / 1000f;
        _dragShiftLastMs = now;
        if (dt <= 0f)
            return 0f;
        if (dt > 0.1f) dt = 0.1f;   // clamp after a stall so we don't snap
        float k = 1f - MathF.Exp(-dt / (DragShiftTauMs / 1000f));
        float maxDelta = 0f;
        for (int i = 0; i < _dragShift.Length; i++)
        {
            float tgt = _dragging ? _dragShiftTgt[i] : 0f;
            _dragShift[i] += (tgt - _dragShift[i]) * k;
            maxDelta = Math.Max(maxDelta, Math.Abs(tgt - _dragShift[i]));
        }
        return maxDelta;
    }

    /// <summary>Recomputes the drag insertion index from the pointer and, when it
    /// changes, retargets the neighbour "push aside" offsets — the GPU equivalent of
    /// WPF ArrangeForDrag. Dragging clear of the column closes the gap.</summary>
    private void UpdateDragGap(float lx, float ly)
    {
        if (_pressIdx < 0 || _pinnedVisible <= 0)
            return;
        float cross = _side switch
        {
            DockSide.Left => lx,
            DockSide.Right => _winW - lx,
            DockSide.Top => ly,
            _ => _winH - ly,
        };
        int gap;
        if (MathF.Abs(cross - _colCenterCross) > _slabCrossLen * 0.85f)
            gap = int.MaxValue;   // out of the column → neighbours fill back in
        else
        {
            float main = _side is DockSide.Left or DockSide.Right ? ly : lx;
            gap = (int)MathF.Round((main - _pinnedAreaMain - _cellMain / 2f) / _cellMain);
            gap = Math.Clamp(gap, 0, Math.Max(0, _pinnedVisible - 1));
        }
        if (gap == _dragInsert)
            return;
        _dragInsert = gap;
        ArrangeDragTargets(gap);
    }

    /// <summary>Sets each non-dragged pinned slot's main-axis target so the column opens
    /// a one-slot gap at <paramref name="gap"/> (or compacts when gap is int.MaxValue).</summary>
    private void ArrangeDragTargets(int gap)
    {
        Array.Clear(_dragShiftTgt, 0, _dragShiftTgt.Length);
        int src = _pressIdx, compact = 0;
        for (int i = 0; i < _pinnedVisible && i < _dragShiftTgt.Length; i++)
        {
            if (i == src)
                continue;
            int visual = (gap == int.MaxValue) ? compact : (compact < gap ? compact : compact + 1);
            _dragShiftTgt[i] = (visual - i) * _cellMain;
            compact++;
        }
    }

    private void DropSlot(int idx, float lx, float ly)
    {
        var s = _slots[idx];
        if (s.Kind != SlotKind.Pinned || s.Entry == null)
            return;   // only pinned icons reorder / unpin

        // Dragged clear of the icon column (across the body) → unpin.
        float cross = _side switch
        {
            DockSide.Left => lx,
            DockSide.Right => _winW - lx,
            DockSide.Top => ly,
            _ => _winH - ly,
        };
        if (MathF.Abs(cross - _colCenterCross) > _slabCrossLen * 0.85f)
        {
            UnpinPinned(s.Entry);
            return;
        }

        // Otherwise reorder to the slot nearest the drop's main-axis position.
        float main = _side is DockSide.Left or DockSide.Right ? ly : lx;
        int tgt = (int)Math.Round((main - _pinnedAreaMain - _cellMain / 2.0) / _cellMain);
        tgt = Math.Clamp(tgt, 0, _config.SideDockApps.Count - 1);
        int src = idx;   // pinned slots are laid out in SideDockApps order
        if (src >= 0 && tgt != src && src < _config.Apps.Count && tgt < _config.Apps.Count)
        {
            var e = _config.Apps[src];
            _config.Apps.RemoveAt(src);
            _config.Apps.Insert(tgt, e);
            PersistAndRelayout();
        }
        else
        {
            Render();
        }
    }

    /// <summary>Pins desktop shortcuts / executables dropped onto the dock from
    /// outside (Explorer / desktop), mirroring the WPF dock's OnDrop: each becomes
    /// a resident app inserted at the pointer's main-axis position.</summary>
    private void HandleDropFiles(List<string> paths, POINT clientPt)
    {
        var entries = new List<AppEntry>();
        foreach (var p in paths)
        {
            try { var e = ShortcutResolver.CreateEntry(p); if (e != null) entries.Add(e); }
            catch { /* skip an unresolvable drop */ }
        }
        if (entries.Count == 0)
            return;

        // DragQueryPoint gives client (physical) coords; convert to window-local DIP.
        float lx = (float)(clientPt.X / _dpi), ly = (float)(clientPt.Y / _dpi);
        float main = _side is DockSide.Left or DockSide.Right ? ly : lx;
        int dropIdx = (int)Math.Round((main - _pinnedAreaMain) / _cellMain);
        dropIdx = Math.Clamp(dropIdx, 0, DockSync.ResidentCount(_config));

        bool changed = false;
        foreach (var entry in entries)
        {
            if (_config.Apps.FindIndex(a => DockSync.Matches(a, entry)) >= 0)
                continue;   // already present — don't duplicate
            DockSync.InsertResident(_config, entry, dropIdx);
            dropIdx++;
            changed = true;
        }
        if (changed)
            PersistAndRebuild();
    }

    private void UnpinPinned(AppEntry entry)
    {
        int idx = _config.Apps.FindIndex(e => DockSync.Matches(e, entry));
        if (idx >= 0)
        {
            int resident = Math.Min(DockSync.ResidentCount(_config), _config.Apps.Count);
            bool wasResident = idx < resident;
            _config.Apps.RemoveAt(idx);
            if (wasResident)
                _config.Settings.Ring0Count = Math.Max(0, resident - 1);
        }
        PersistAndRebuild();
    }

    /// <summary>Mirrors the resident region into the side-dock list, saves the config,
    /// rebuilds the GPU dock and notifies the host so the main dock refreshes live
    /// (parity with the WPF side dock's MainDockChanged).</summary>
    private void PersistAndRebuild()
    {
        try
        {
            DockSync.MirrorResidentToLeft(_config);
            ConfigStore.Save(_config);
        }
        catch (Exception ex) { Log.Warn("SideDockGpu", "persist failed: " + ex.Message); }
        Rebuild();
        MainDockChanged?.Invoke();
    }

    /// <summary>Persists then relayouts in place (no window/host recreation) so a
    /// reorder drop does not flash. Used for reorders, where the icon count — and
    /// hence the window geometry — is unchanged (mirrors the main dock's
    /// PersistAndRelayout).</summary>
    private void PersistAndRelayout()
    {
        try
        {
            DockSync.MirrorResidentToLeft(_config);
            ConfigStore.Save(_config);
        }
        catch (Exception ex) { Log.Warn("SideDockGpu", "persist failed: " + ex.Message); }
        RelayoutInPlace();
        MainDockChanged?.Invoke();
    }

    /// <summary>Recomputes the slots/geometry in the existing window. Falls back to a
    /// full Rebuild only when the window geometry actually changed.</summary>
    private void RelayoutInPlace()
    {
        if (_host == null || _hwnd == IntPtr.Zero) { Rebuild(); return; }
        int ow = _winW, oh = _winH, ox = _winX, oy = _winY;
        _hover = -1; _pressIdx = -1; _dragging = false;
        LayoutContent();
        if (_winW != ow || _winH != oh || _winX != ox || _winY != oy)
        {
            Rebuild();   // geometry changed → window/swapchain must be recreated
            return;
        }
        Render();
    }

    private void Rebuild()
    {
        _timer?.Stop();
        CloseSlotMenu();
        ClosePreview();
        if (_hwnd != IntPtr.Zero) s_instances.Remove(_hwnd);
        _host?.Dispose(); _host = null;
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        foreach (var b in _bmpCache.Values) b?.Dispose();
        _bmpCache.Clear();
        DisposeRockResources();
        _hover = -1; _pressIdx = -1; _dragging = false;
        try { Build(); }
        catch (Exception ex) { Log.Warn("SideDockGpu", "rebuild failed: " + ex); }
    }

    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();

    /// <summary>Device pixels per DIP for the active monitor. Computed from the
    /// physical mode (EnumDisplaySettings, which is independent of the caller's DPI
    /// awareness) over the WPF DIP width — reliable even before the GPU window is
    /// realized, unlike GetDpiForWindow/GetDpiForMonitor which can race to 96.</summary>
    private static double DpiScale()
    {
        try
        {
            var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm) && dm.dmPelsWidth > 0)
            {
                double dipW = System.Windows.SystemParameters.PrimaryScreenWidth;
                if (dipW > 0)
                {
                    double s = dm.dmPelsWidth / dipW;
                    if (s >= 0.5 && s <= 4.0) return s;
                }
            }
        }
        catch { /* fall through to 1.0 */ }
        return 1.0;
    }

    private const int ENUM_CURRENT_SETTINGS = -1;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE dm);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2,
            dmPanningWidth, dmPanningHeight;
    }


    private static (float x, float y) ToLocal(DockSide side, double main, double cross, int winW, int winH) => side switch
    {
        DockSide.Left => ((float)cross, (float)main),
        DockSide.Right => ((float)(winW - cross), (float)main),
        DockSide.Top => ((float)main, (float)cross),
        _ => ((float)main, (float)(winH - cross)),
    };

    // ---- Raw Win32 NOREDIRECTIONBITMAP window --------------------------------

    private static readonly WndProc s_wndProc = WndProcImpl;
    private static ushort s_atom;

    private static IntPtr WndProcImpl(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (s_instances.TryGetValue(h, out var inst) && inst.HandleMessage(m, w, l, out IntPtr r))
            return r;
        return DefWindowProcW(h, m, w, l);
    }

    private static IntPtr CreateWindow(int w, int h)
    {
        if (s_atom == 0)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                hInstance = GetModuleHandleW(null),
                lpszClassName = "PolarisSideDockGpu",
            };
            s_atom = RegisterClassExW(ref wc);
        }
        // Interactive (Stage E): NO WS_EX_TRANSPARENT — the window receives mouse
        // input. WM_NCHITTEST returns HTTRANSPARENT outside the glass slab so clicks
        // on the empty reserved area still pass through. WS_EX_NOACTIVATE keeps it
        // from stealing focus on click.
        return CreateWindowExW(
            WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST |
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "PolarisSideDockGpu", string.Empty, WS_POPUP,
            0, 0, w, h, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
    }

    private delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_POPUP = 0x80000000;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_HIDE = 0;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOMOVE = 0x0002;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc;
        public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
        public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int ex, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? n);
}
