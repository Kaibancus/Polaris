using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Polaris.Models;
using Polaris.Services;
using Polaris.Services.Gpu;
using Polaris.Interop;
using static Polaris.Interop.Win32;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using FontStyle = Vortice.DirectWrite.FontStyle;

namespace Polaris.Views;

/// <summary>GPU side dock: the liquid-glass slab, the pinned icon column, a light-split
/// divider and the running-but-unpinned strip (green running dots on up to 12 running apps,
/// Polaris app tile + permanent date/time widget at the right end) drawn in Direct2D under
/// DirectComposition; a cursor poll drives a continuous magnify wave across both halves and
/// shows the hovered icon's name.
/// Per-monitor DPI aware (layout in DIPs, window + swap chain in physical px, D2D target
/// DPI = 96 × scale).</summary>
internal sealed class SideDockWindowGpu : GpuDockBase, IDisposable, ISideDock
{
    private const float GlassIconScale = 1.32f;
    private const float SideDockScaleK = 0.70f;
    // Peak magnification the icon directly under the pointer reaches. Pop-out is
    // driven off (scale − 1), so a taller peak also pops the focal icon further.
    private const float HoverScale = 1.68f;
    // Pointer travel (window-DIP, Euclidean) before a press becomes a drag.
    // Matches the WPF dock's 6 px Euclidean DragThreshold so small reposition
    // gestures register as drags instead of being misread as a launch-click.
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
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private DropShimWindow? _dropShim; // overlay that catches external drags + forwards them
    private IDragGhost? _ghost;        // independent desktop overlay for the dragged icon
    private IDWriteFactory? _dwrite;
    private IDWriteTextFormat? _labelFormat;
    private IDWriteTextFormat? _hoverFormat;   // floating hover name (SemiBold, hover-scaled)
    private float _hoverFontPx = 16f;
    // Hover-label auto-fit (vertical docks): shrink the font so a long name still fits the
    // window thickness without clipping (parity with WPF FitFontSize). Cached by name so the
    // DWrite measure only runs when the hovered slot changes, not every render frame.
    private IDWriteTextFormat? _fitFormat;
    private float _fitFormatFp;
    private string? _labelFitName;
    private float _labelFitFp, _labelFitW;
    // Permanent date/time widget pinned at the far (right) end of a horizontal running
    // strip: a classic calendar page (red header "YYYY年M月" + big bold day number) plus
    // two text lines beside it (bold weekday over the 24-hour time). Replaces the Polaris
    // tile's old hover calendar/clock popup. Horizontal docks lay the text to the right;
    // vertical (Left/Right) docks stack it below. The widget stays static (no hover wave).
    private bool _dateWidget;
    private float _dateIconCX, _dateIconCY, _dateIconG;
    private IDWriteTextFormat? _calMonthFormat, _calDayFormat, _weekFormat, _timeFormat;
    // Custom DirectWrite rendering params (NaturalSymmetric → no grid-fitting) giving the
    // date/time widget's weekday + time lines smoother, sub-pixel glyph edges. Shared by both
    // themes (the widget is theme-independent; only its ink colour differs).
    private IDWriteRenderingParams? _smoothText;
    private float _dateFmtG;   // gIcon the calendar formats were last built for
    private DispatcherTimer? _persistTimer;   // debounce config writes so repeated drags don't block the UI thread
    private DispatcherTimer? _mainDockChangedTimer;   // debounce host main-dock refresh notifications across rapid drags

    // Render-thread infrastructure (UseRenderThread, _loop, _host, _timer, _gcActive +
    // EnsureLoop/StartDriver/StopDriver/RunOnRender/InvokeOnRender/OnUi/RequestRender) lives in
    // the shared GpuDockBase. The dock's _slots/caches/animation state are owned and driven by
    // that render thread (or while it is quiesced during rebuild).
    private int[]? _orderBuf;            // reused draw-order scratch (avoids a per-frame alloc)
    private Comparison<int>? _orderCmp;  // cached so the sort doesn't alloc a closure each frame

    protected override string RenderThreadName => "PolarisSideDockGpu";
    protected override Dispatcher UiDispatcher => _dispatcher;
    protected override float DragIconSize => _gIcon;
    protected override Vector2 ScreenToLocal(int screenX, int screenY) =>
        new((float)(screenX / _dpi - _winX), (float)(screenY / _dpi - _winY));
    // Guards the interaction scalars written by the UI-thread WndProc and read by the render
    // thread in Tick/Render (drag point, intro animation phase). Layout/_slots/host/device are
    // mutated only on the render thread (or while it is quiesced during rebuild). Held briefly.
    private readonly object _stateLock = new();
    private int _lastPreviewHover = -2;   // last hover marshaled to DrivePreview (render-thread path)

    private readonly List<Slot> _slots = new();
    private DockSide _side;
    private int _winX, _winY, _winW, _winH;
    private double _dpi = 1.0;
    private int _hover = -1;
    // Hover-label fade (parity with WPF ShowHoverLabel/HideHoverLabel 110 ms opacity
    // animation): _labelIdx keeps the slot whose name is showing while it fades out.
    private int _labelIdx = -1;
    private float _labelOp;
    private long _labelLastMs;
    private float _sx, _sy, _sw, _sh, _trayRadius, _opacity, _frost;
    private float _gIcon, _cellH;
    private float _seamMain, _bodyCross, _bodyCrossLen;
    private float _colCenterCross, _slabCrossLen, _pinnedAreaMain, _cellMain;
    private int _pinnedVisible;
    private float[] _waveCur = Array.Empty<float>();
    // Hover magnify wave, defined directly as (distance-in-cells, scale) control points tuned by feel.
    // A monotone cubic (PCHIP) interpolates between them, so every listed distance hits its value exactly
    // with a smooth bell crest and no overshoot. Pop-out is driven off (scale-1), so the same points also
    // set how far each icon lifts off the edge — bigger scale = bigger pop:
    //   d=0 -> 1.68 (focal: max magnify + max pop)   d=1 -> 1.24   d=1.5 -> 1.10   d=2 -> 1.04
    //   d>=3 -> 1.00 (clamped: no magnify, no pop, and no push-apart at any distance)
    // To retune, just edit a scale value (or add a control point) — the curve re-fits automatically.
    private static readonly float[] _waveDist = { 0f, 1f, 1.5f, 2f, 3f };
    private static readonly float[] _waveScaleY = { HoverScale, 1.24f, 1.10f, 1.04f, 1f };
    private static readonly float[] _waveTan = BuildWaveTangents(_waveDist, _waveScaleY);

    // Fritsch–Carlson monotone tangents; flat ends (0 crest/tail slope) avoid a cusp at the peak.
    private static float[] BuildWaveTangents(float[] x, float[] y)
    {
        int n = x.Length;
        var m = new float[n];
        var d = new float[n - 1];
        for (int i = 0; i < n - 1; i++)
            d[i] = (y[i + 1] - y[i]) / (x[i + 1] - x[i]);
        for (int i = 1; i < n - 1; i++)
        {
            if (d[i - 1] * d[i] <= 0f)
            {
                m[i] = 0f;
            }
            else
            {
                float h0 = x[i] - x[i - 1];
                float h1 = x[i + 1] - x[i];
                float w1 = 2f * h1 + h0;
                float w2 = h1 + 2f * h0;
                m[i] = (w1 + w2) / (w1 / d[i - 1] + w2 / d[i]);
            }
        }
        return m;
    }

    // ---- New-message attention badge (parity with the GPU main dock / WPF) -------
    private volatile System.Collections.Generic.HashSet<string> _flashKeys = new();
    private float _badgePulse = 1f;       // attention dot pulse scale (~1.0..1.18, 1.4s)
    private long _attnLast;               // last attention poll tick (throttle to ~800ms)
    private volatile bool _attnBusy;      // an attention poll task is in flight

    // ---- Running-icon glow pulse + glass orbit light (parity with the GPU main dock) ----
    private float _runPulse = 0.5f;       // running-icon glow pulse (0.35..0.8, 4.0s breathe)
    private float _orbitAngle;            // glass orbit-light angle (deg, 36s/rev clockwise)
    private long _animLastMs;             // last frame timestamp for dt-based (refresh-rate independent) animation
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
    private Vortice.Direct2D1.Effects.GaussianBlur? _satBlurEffect;   // cached flame-feather blur (reused per frame; recreated with the host)
    // Running-dot brushes cached for the HOST's lifetime (nulled + recreated by DisposeHostResources,
    // like _satBlurEffect). The green dot + breathing radial halo render for every running icon every
    // frame; building a gradient-stop collection + two brushes per icon per frame churned finalizable
    // COM wrappers that pressured gen2 GC during long drag sessions (see RenderGcScope). The halo's
    // alpha pulses with _runPulse (hard-bounded to [0.35,0.8] by the sine), so a CONSTANT stop
    // collection (alphas baked at the _runPulse=0.8 peak) plus the brush's Opacity = _runPulse/0.8
    // (always ≤1) reproduces the old per-stop alpha × breath EXACTLY; centre/radius are set per draw.
    private ID2D1SolidColorBrush? _runDotBrush;
    private ID2D1GradientStopCollection? _runHaloStops;
    private ID2D1RadialGradientBrush? _runHaloBrush;
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
    private bool _byMain, _byEdge, _byDrag, _byPinned, _byMenu, _byBounce;
    private bool _dismissing;   // launch committed: block hover re-magnify through the dismiss fade (WPF parity)
    private bool _refreshPending;             // one or more RefreshFromConfig requests are queued
    private DispatcherTimer? _refreshTimer;   // coalesces repeated main-dock updates across rapid drags
    private bool _suspendRefreshWhileDrag;    // hard block: main-dock drag sequence owns the side dock's timing
    private bool _layoutDirty;                // config changed while hidden/blocked; consume on next non-drag show

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

    public void SetMainShown(bool shown)
    {
        _byMain = shown;
        UpdateVisibility();
        // Main-dock-originated refreshes are intentionally deferred for the whole lifetime of
        // the main dock being shown. Flush them only once that interaction has fully ended.
        if (!shown && _refreshPending && !_byDrag)
            ScheduleRefresh();
    }
    public void SetPinnedShown(bool shown) { _byPinned = shown; UpdateVisibility(); }
    public void SetEdgeShown(bool shown) { _byEdge = shown; UpdateVisibility(); }
    public void SetDragActive(bool active)
    {
        _byDrag = active;
        UpdateVisibility();
        // Main-dock drag sequences own the side dock completely: a 65-75ms side-dock refresh
        // landing anywhere between two quick drags is exactly what made later drags feel
        // progressively heavier. Therefore, once a drag begins, HARD-suspend all side-dock
        // refresh execution (including already queued timers). When the drag releases, flush the
        // accumulated pending refreshes once via the normal debounce path.
        if (active)
        {
            _suspendRefreshWhileDrag = true;
            _refreshTimer?.Stop();
        }
        else
        {
            _suspendRefreshWhileDrag = false;
            if (_refreshPending)
                ScheduleRefresh();
        }
    }

    public void HideAll()
    {
        _byMain = _byEdge = _byDrag = _byPinned = _byMenu = _byBounce = false;
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
        bool want = _byMain || _byEdge || _byDrag || _byPinned || _byMenu || _byBounce || (_preview?.IsOpen == true);
        if (want) _dismissing = false;   // any reason to stay/become visible ends a launch dismiss
        if (want == _shown)
            return;
        if (want) DoShow();
        else DoHide();
    }

    private void DoShow()
    {
        _shown = true;
        StartDriver();   // re-arm the render loop (it is paused while hidden to avoid idle churn)
        if (!_realized) { Realize(); StartIntro(); return; }
        // Drag-only summons happen every time the user starts dragging a main-dock icon, so
        // rebuilding here means a quick sequence of drag attempts repeatedly tears the side
        // dock down and recreates it. That heavy path is exactly the sort of cumulative work
        // that can make later drags feel progressively more sluggish even when nothing was
        // deleted. For the drag-target use case we only need the ALREADY-synchronised hidden
        // dock to become visible; RefreshFromConfig/AppsChanged keeps the content up to date.
        //
        // Keep the full rebuild for non-drag summons (edge/main/pinned/menu/bounce), where
        // the user is actually looking at the side dock and expects the running strip/layout
        // to reflect the latest state right at show time.
        bool dragOnly = _byDrag && !_byMain && !_byEdge && !_byPinned && !_byMenu && !_byBounce;
        DragPerfStats.Event("side", 0, "do-show", dragOnly ? "dragOnly" : "full");
        if (!dragOnly || _layoutDirty)
        {
            _layoutDirty = false;
            _refreshPending = false;
            Rebuild();
        }
        if (_hwnd != IntPtr.Zero)
        {
            // Prime the slide-in start state (off-screen toward the edge + transparent) on the
            // compositor BEFORE the window is shown, so the first visible frame is already at the
            // animation's start instead of flashing once at the rest position. Rebuild recreates
            // the host with a default at-rest visual (offset 0, opacity 1), so without this the
            // DWM composites one rest frame the instant ShowWindow runs, then StartIntro jumps it
            // off-screen to slide in — the visible "jump/flicker" on summon.
            _introSlidePx = (_slabCrossLen + 40f) * (float)_dpi;
            Vector2 startOff = PopOffset(-_introSlidePx);
            InvokeOnRender(() => _host?.SetIntro(startOff.X, startOff.Y, 0f));
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            SyncShim(); _dropShim?.Show();
        }
        StartIntro();
    }

    private void DoHide()
    {
        _shown = false;
        CloseSlotMenu();
        ClosePreview();
        EndDragGhost();
        _dropShim?.Hide();
        _hover = -1; _prevHover = -1;
        // Fade out on the GPU compositor, then SW_HIDE once it completes (DriveIntro
        // mode 2). If there's no live host yet, just hide immediately.
        if (_host != null && _hwnd != IntPtr.Zero)
        {
            _introStart = Environment.TickCount64;
            _introMode = 2;
            StartDriver();   // ensure the loop runs to animate the fade-out
            if (!UseRenderThread) DriveIntro();
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
        if (!UseRenderThread) DriveIntro();
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
            _dismissing = false;   // dock fully hidden — clear the launch-dismiss latch
            StopDriver();          // hide animation finished: pause the render loop
            OnUi(() => { if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_HIDE); });
            return false;
        }
        return true;
    }

    public void RefreshFromConfig()
    {
        _refreshPending = true;
        // While the MAIN dock is shown the side dock is shown-by-main (_byMain); a main-dock
        // reorder must still propagate here, so _byMain is intentionally NOT a block reason.
        // Active drags are still hard-suspended via _suspendRefreshWhileDrag / _byDrag (set by
        // SetDragActive), so a refresh never lands mid-drag — it only runs once the user settles.
        bool block = _suspendRefreshWhileDrag || _byDrag || !_shown;
        if (block)
        {
            _layoutDirty = true;
            DragPerfStats.Event("side", 0, "refresh-deferred",
                _suspendRefreshWhileDrag ? "suspended-by-drag"
                : _byDrag ? "while-dragging"
                : "while-hidden");
            return;
        }
        DragPerfStats.Event("side", 0, "refresh-request", "visible-idle");
        ScheduleRefresh();
    }

    /// <summary>Forces a full rebuild so the dock re-reads the display metrics (DPI, work area,
    /// resolution) and re-lays out. Called when the OS reports a display / work-area change —
    /// notably the post-login settle, where auto-start ran before the real mode/DPI applied.</summary>
    public void RefreshForDisplayChange()
    {
        if (!_realized) return;
        try { Rebuild(); }
        catch (Exception ex) { Log.Warn("SideDockGpu", "display-change refresh failed: " + ex.Message); }
    }

    private void RefreshFromConfigCore()
    {
        long t0 = Stopwatch.GetTimestamp();
        _refreshPending = false;
        _layoutDirty = false;
        try { DockSync.MirrorResidentToLeft(_config); } catch { }
        // Relayout in place (no window teardown) so the resident count updates without the
        // side dock flashing/vanishing — used on every main-dock resident change.
        if (_realized) RelayoutInPlace();
        DragPerfStats.Event("side", 0, "refresh-from-config",
            ((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency).ToString("0.000", CultureInfo.InvariantCulture) + "ms");
    }

    private void ScheduleRefresh()
    {
        if (_refreshTimer == null)
        {
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _refreshTimer.Tick += (_, _) =>
            {
                _refreshTimer!.Stop();
                if (_suspendRefreshWhileDrag || _byDrag || !_refreshPending)
                    return;
                _refreshPending = false;
                RefreshFromConfigCore();
            };
        }
        _refreshTimer.Stop();
        _refreshTimer.Start();
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
        float main = _side is DockSide.Left or DockSide.Right ? ly : lx;
        int dropIdx = (int)Math.Round((main - _pinnedAreaMain) / _cellMain);
        dropIdx = Math.Clamp(dropIdx, 0, DockSync.ResidentCount(_config));
        int existing = _config.Apps.FindIndex(a => DockSync.Matches(a, entry));
        if (existing < 0)
        {
            DockSync.InsertResident(_config, entry, dropIdx);
            PersistAndRebuild();
        }
        else if (existing >= DockSync.ResidentCount(_config))
        {
            // Already in the dock but not resident — promote it into the resident
            // region at the drop position (mirrors WPF SideDockWindow.Sync.cs OnDrop).
            var moved = _config.Apps[existing];
            _config.Apps.RemoveAt(existing);
            DockSync.InsertResident(_config, moved, Math.Clamp(dropIdx, 0, DockSync.ResidentCount(_config)));
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

        // Permanent date/time widget reserve: one calendar-page cell past the last running
        // icon, then a two-line text block — to the RIGHT on horizontal docks, or stacked
        // BELOW on vertical (Left/Right) docks. gIcon-based so it is known before the window
        // is sized; the cell term is added after cellH settles.
        double widgetGap = 0;                                  // calendar sits one normal icon-pitch (cellH) past the last running icon
        double widgetTextW = !vertical ? gIcon * 1.00 : 0;     // horizontal: text block to the right
        double widgetTextH = vertical ? gIcon * 0.90 : 0;      // vertical: two text lines stacked below

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
        const int maxRunSlots = 1 + 12;   // Polaris + RunningMaxComplete (extras dropped, no overflow tile)
        double desiredMain = 12 * uiScale + startPad + pinnedCount * defCell
                           + effIcon * 0.55 + maxRunSlots * defCell + endPad + 12 * uiScale
                           + (!vertical ? widgetGap + defCell + widgetTextW : 0);
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

        // Now that the cell pitch is final, size the widget: gap + one calendar cell + text.
        double widgetMain = widgetGap + cellH + (vertical ? widgetTextH : widgetTextW);

        double runningBlockH = runSlots * cellH;          // RunStep == cellH
        int maxVisible = Math.Max(1, (int)Math.Floor((availForCells - runningBlockH) / cellH));
        int pinnedVisible = Math.Min(pinnedCount, maxVisible);
        double pinnedBlockH = pinnedVisible * cellH;
        double slabMainLen = startPad + pinnedBlockH + seam + runningBlockH + widgetMain + endPad;

        // Centre the VISIBLE ICON CLUSTER (pinned + running, incl. the seam gap and the
        // trailing date widget), not the slab box, on the usable band — same correction as
        // the real dock. The widget extends the cluster on the running end, so its half-width
        // shifts the centroid toward that end to keep the whole thing balanced.
        int visibleCells = pinnedVisible + runSlots;
        double centroidFromSlab = startPad
            + (visibleCells > 0 ? cellH * visibleCells / 2.0 : 0)
            + (visibleCells > 0 ? seam * runSlots / (double)visibleCells : 0)
            + widgetMain / 2.0;
        double slabMain = (startReserve + usableMain / 2.0) - centroidFromSlab;
        slabMain = Math.Min(slabMain, mainExtent - endReserve - slabMainLen);
        slabMain = Math.Max(slabMain, startReserve);
        double pinnedAreaMain = slabMain + startPad;
        double runAreaMain = pinnedAreaMain + pinnedBlockH + seam;

        // Date/time widget: the calendar page occupies the cell right after the last running
        // icon (aligned to the icon row/column on the cross axis); the text is drawn to its
        // right (horizontal) or stacked below it (vertical) in DrawDateWidget.
        {
            double calCenterMain = runAreaMain + runningBlockH + widgetGap + cellH / 2.0;
            (float dcx, float dcy) = ToLocal(_side, calCenterMain, colCenterCross, _winW, _winH);
            _dateIconCX = dcx; _dateIconCY = dcy; _dateIconG = (float)gIcon;
            _dateWidget = true;
        }

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
            // darkPad/glassPad are the INTERIOR pad (icon's far edge → slab inner edge); they
            // set the slab thickness (= the dock's visible height/width) WITHOUT affecting the
            // icon's distance from the screen edge (colCenterCross, computed above and unrelated
            // to these). Trimmed slightly to make the dock a touch thinner per user request.
            double darkBleed = gIcon * 0.4, darkPad = gIcon * 0.4;
            bodyCross = slabCross - darkBleed;
            bodyCrossLen = (colCenterCross - bodyCross) + gIcon / 2.0 + darkPad;
        }
        else
        {
            double glassPad = gIcon * 0.20;
            bodyCross = slabCross;
            bodyCrossLen = (colCenterCross - bodyCross) + gIcon / 2.0 + glassPad;
        }
        _trayRadius = (float)(iconSize * uiScale * 0.42);
        // Saturn black is remapped denser (50% slider ≈ old 30%); liquid glass keeps
        // the plain 1 − transparency. _saturn is assigned later (≈l.639), so resolve
        // the theme directly here.
        _opacity = ThemeRegistry.Get(_config.Settings.Theme).IsSaturn
            ? DockTuning.SaturnPanelOpacity(_config.Settings.PanelTransparency)
            : (float)(1.0 - Math.Clamp(_config.Settings.PanelTransparency, 0.0, 1.0));
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
        // Explorer window titles + running AUMIDs are REQUIRED by IsEntryRunning to
        // light shell-hosted launchers (File Explorer, This PC…) and packaged/UWP
        // apps. Without them File Explorer never shows its green running dot even
        // when open. Mirrors the WPF side dock's RefreshRunning (SideDockWindow.Running.cs).
        List<string> explorerTitles;
        try { explorerTitles = WindowPreviewService.GetExplorerWindowTitles(); }
        catch { explorerTitles = new List<string>(); }
        HashSet<string> runningAumids;
        try { runningAumids = WindowPreviewService.SnapshotRunningAumids(); }
        catch { runningAumids = new HashSet<string>(); }
        for (int i = 0; i < pinnedVisible && i < apps.Count; i++)
        {
            var entry = apps[i];
            double mainC = pinnedAreaMain + i * cellH + cellH / 2.0;
            (float cx, float cy) = ToLocal(_side, mainC, colCenterCross, _winW, _winH);
            bool run = RunningAppTracker.IsEntryRunning(entry, running, explorerTitles, runningAumids);
            var img = IconExtractor.GetCached(entry.EffectiveIconSource, _iconCache);
            _slots.Add(new Slot(new Vector2(cx, cy), (float)gIcon, entry.Name, run,
                SlotKind.Pinned, entry.EffectiveIconSource, img, entry, IntPtr.Zero));
        }
        for (int k = 0; k < runSlots; k++)
        {
            double mainC = runAreaMain + k * cellH + cellH / 2.0;
            (float cx, float cy) = ToLocal(_side, mainC, colCenterCross, _winW, _winH);
            if (k == runSlots - 1)
            {
                // The Polaris tile sits at the RIGHT end of the running strip, just left of the
                // date widget; shows Polaris's own app icon with a green running dot. Click toggles docks.
                string exe = Environment.ProcessPath ?? "";
                _slots.Add(new Slot(new Vector2(cx, cy), (float)gIcon, "Polaris", true,
                    SlotKind.Run, "polaris:" + exe, SafeIcon(exe), null, IntPtr.Zero));
            }
            else if (overflow > 0 && k == runItems.Count)
            {
                _slots.Add(new Slot(new Vector2(cx, cy), (float)gIcon, "+" + overflow, false,
                    SlotKind.Overflow, "", null, null, IntPtr.Zero));
            }
            else
            {
                var it = runItems[k];
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
        _dpi = MonitorLayout.PrimaryDpiScale;
        // Layout is computed in DIPs (MonitorLayout returns DIPs); the Win32 window
        // + DComp swap chain live in PHYSICAL pixels. Size the window to physical px
        // and tell D2D the target DPI so all DIP-space drawing scales up 1:1.
        int pw = (int)Math.Ceiling(_winW * _dpi), ph = (int)Math.Ceiling(_winH * _dpi);
        int px = (int)Math.Round(_winX * _dpi), py = (int)Math.Round(_winY * _dpi);
        SetWindowPos(_hwnd, HWND_TOPMOST, px, py, pw, ph, SWP_NOACTIVATE);
        ShowWindow(_hwnd, _shown ? SW_SHOWNOACTIVATE : SW_HIDE);
        // Composition-only windows (WS_EX_NOREDIRECTIONBITMAP) can't be OLE drop targets nor
        // receive WM_DROPFILES, so external drags (Explorer / desktop) are caught by a near-
        // invisible overlay above the dock that hosts the OLE drop target and forwards the
        // initial mouse press + drops back to us (see DropShimWindow), exactly like the main
        // dock. The shim is kept ALIVE across rebuilds (only its owner HWND is re-pointed).
        if (_dropShim == null)
            _dropShim = new DropShimWindow(_hwnd, ForwardShimInput, HandleOleDrop, OnExternalDragMove);
        else
            _dropShim.SetOwner(_hwnd);

        // The GPU device + Direct2D/DirectComposition resources have hard thread affinity, so
        // on the render-thread path they are created and driven ONLY on the render thread; the
        // UI thread never touches the host. Invoke runs the creation there and waits so the
        // shim re-top below sees a live host.
        if (UseRenderThread)
        {
            EnsureLoop();
            _loop!.Invoke(CreateHostResources);
        }
        else
        {
            CreateHostResources();
        }

        // Top the shim AFTER creating the CompositionHost (building the DComp swap chain re-
        // raises the dock above the shim) so external drags land on the shim's drop target.
        if (_shown) { SyncShim(); _dropShim!.Show(); }

        if (UseRenderThread)
            _loop!.SetActive(_shown);
    }

    /// <summary>Creates the GPU host + DirectWrite text formats and renders the first frame.
    /// On the render-thread path this runs ON the render thread (posted from
    /// <see cref="CreateHostWindow"/>); on the default path it runs inline and starts the
    /// FrameClock. Window creation + the drop shim stay on the UI thread either way.</summary>
    private void CreateHostResources()
    {
        int pw = (int)Math.Ceiling(_winW * _dpi), ph = (int)Math.Ceiling(_winH * _dpi);
        _host = new CompositionHost(_hwnd, pw, ph, (float)(96.0 * _dpi), waitable: UseRenderThread);
        _animLastMs = 0;
        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
        _smoothText?.Dispose(); _smoothText = null;
        try
        {
            // Keep the system gamma/contrast/pixel-geometry, but switch to NaturalSymmetric so
            // glyph edges are anti-aliased without hard grid-fitting → smoother on the glass.
            using var def = _dwrite.CreateRenderingParams();
            _smoothText = _dwrite.CreateCustomRenderingParams(def.Gamma, def.EnhancedContrast,
                def.ClearTypeLevel, def.PixelGeometry, Vortice.DirectWrite.RenderingMode.NaturalSymmetric);
        }
        catch { _smoothText = null; }
        _labelFormat?.Dispose();
        _hoverFormat?.Dispose();
        _fitFormat?.Dispose(); _fitFormat = null; _fitFormatFp = 0; _labelFitName = null;   // geometry changed → re-fit on next hover
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

        if (!UseRenderThread)
        {
            _timer = new Polaris.Services.Gpu.FrameClock();
            _timer.Tick += Tick;
            _timer.Start();
        }
    }

    /// <summary>Disposes all render-thread-owned GPU resources (host, text formats, icon +
    /// debris caches). MUST run on the render thread (it owns the device).</summary>
    private void DisposeHostResources()
    {
        foreach (var b in _bmpCache.Values) b?.Dispose();
        _bmpCache.Clear();
        DisposeRockResources();
        _satBlurEffect?.Dispose(); _satBlurEffect = null;
        _runDotBrush?.Dispose(); _runDotBrush = null;
        _runHaloBrush?.Dispose(); _runHaloBrush = null;
        _runHaloStops?.Dispose(); _runHaloStops = null;
        _labelFormat?.Dispose(); _labelFormat = null;
        _hoverFormat?.Dispose(); _hoverFormat = null;
        _fitFormat?.Dispose(); _fitFormat = null; _fitFormatFp = 0; _labelFitName = null;
        _calMonthFormat?.Dispose(); _calMonthFormat = null;
        _calDayFormat?.Dispose(); _calDayFormat = null;
        _weekFormat?.Dispose(); _weekFormat = null;
        _timeFormat?.Dispose(); _timeFormat = null; _dateFmtG = 0;
        _smoothText?.Dispose(); _smoothText = null;
        _dwrite?.Dispose(); _dwrite = null;
        _host?.Dispose(); _host = null;
    }

    // ---- External drag-in (drop shim) + drag-out ghost (parity with the main dock) ----

    /// <summary>Re-maps a mouse message that landed on the drop-shim overlay (SCREEN pixels)
    /// into this dock's client space and runs it through <see cref="HandleMessage"/>, so a
    /// press on the shim behaves exactly as a press on the dock (the dock's SetCapture then
    /// routes the rest of a drag straight here, bypassing the shim).</summary>
    internal (bool handled, IntPtr result) ForwardShimInput(uint msg, IntPtr wParam, int screenX, int screenY)
    {
        int cx = screenX - (int)Math.Round(_winX * _dpi);
        int cy = screenY - (int)Math.Round(_winY * _dpi);
        IntPtr lParam = (IntPtr)(((cy & 0xFFFF) << 16) | (cx & 0xFFFF));
        bool handled = HandleMessage(msg, wParam, lParam, out var res);
        return (handled, res);
    }

    /// <summary>Re-raises the drop-shim above the dock when the bare composition dock has come
    /// to cover it at the slab centre. Throttled to ~150ms so it runs cheaply from Tick.</summary>
    private void EnsureShimTopmost()
    {
        if (_dropShim == null || !_shown) return;
        long now = Environment.TickCount64;
        if (now - _shimCheckMs < 150) return;
        _shimCheckMs = now;
        // _dropShim.Show() is Win32 on a UI-owned window, so marshal off the render thread
        // (the 150ms throttle keeps this to a few BeginInvokes/sec).
        OnUi(() =>
        {
            try
            {
                if (_dropShim == null || !_shown) return;
                int cx = (int)Math.Round((_winX + _sx + _sw / 2f) * _dpi);
                int cy = (int)Math.Round((_winY + _sy + _sh / 2f) * _dpi);
                IntPtr top = WindowFromPoint(new POINT { X = cx, Y = cy });
                if (top == _hwnd)
                    _dropShim.Show();
            }
            catch { }
        });
    }
    private long _shimCheckMs;

    /// <summary>Positions the drop-shim over the dock's interactive surface (the slab plus
    /// each icon's outward magnify-pop headroom — the same box <see cref="InsideHitRegion"/>
    /// claims) and carves the window region to match, so external drags land on the shim and
    /// the surrounding desktop stays reachable.</summary>
    private void SyncShim()
    {
        if (_dropShim == null) return;
        var (l, t, r, b) = HitBox();
        int sx = (int)Math.Round((_winX + l) * _dpi);
        int sy = (int)Math.Round((_winY + t) * _dpi);
        int sw = Math.Max(1, (int)Math.Ceiling((r - l) * _dpi));
        int sh = Math.Max(1, (int)Math.Ceiling((b - t) * _dpi));
        _dropShim.SetBounds(sx, sy, sw, sh);
        ApplyWindowRegion();
    }

    /// <summary>The dock's interactive box in window-local DIPs: the slab plus the outward
    /// magnify-pop headroom on the pop side (matches <see cref="InsideHitRegion"/>).</summary>
    private (float l, float t, float r, float b) HitBox()
    {
        float m = _gIcon * 0.6f;
        float mPop = (HoverScale - 1f) * _gIcon * 1.18f + _gIcon * 1.2f;
        float l = _sx - m, r = _sx + _sw + m, t = _sy - m, b = _sy + _sh + m;
        switch (_side)
        {
            // Vertical docks: the hover name label extends along the cross axis (the window
            // thickness) toward the interior and can be long, so cover the FULL thickness on
            // that side — otherwise the window region clips the label. (The main-axis reserve
            // above/below the icon column is still carved by t/b for desktop passthrough.)
            case DockSide.Left: r = _winW; break;
            case DockSide.Right: l = 0f; break;
            case DockSide.Top: b = _sy + _sh + mPop; break;
            default: t = _sy - mPop; break;
        }
        return (l, t, r, b);
    }

    /// <summary>Carves the composition window down to its interactive box with SetWindowRgn,
    /// so the transparent reserve around the dock is no longer part of the (topmost) window
    /// and never intercepts a press meant for a desktop icon under it. OS-level, render-
    /// thread-independent passthrough — see the main dock's ApplyWindowRegion for the full
    /// rationale (WS_EX_TRANSPARENT is a no-op on a composition window; NCHITTEST is racy).</summary>
    private void ApplyWindowRegion()
    {
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            var (l, t, r, b) = HitBox();
            float left = Math.Max(0f, l), top = Math.Max(0f, t);
            float right = Math.Min(_winW, r), bottom = Math.Min(_winH, b);
            IntPtr rgn = CreateRectRgn((int)Math.Floor(left * _dpi), (int)Math.Floor(top * _dpi),
                (int)Math.Ceiling(right * _dpi), (int)Math.Ceiling(bottom * _dpi));
            SetWindowRgn(_hwnd, rgn, true);
        }
        catch (Exception ex) { Log.Warn("SideDockGpu", "ApplyWindowRegion failed: " + ex.Message); }
    }

    /// <summary>Lifts the icon at <paramref name="idx"/> into an independent topmost desktop
    /// overlay pinned to the cursor, so the dragged icon roams the whole desktop and never
    /// clips at the narrow side-dock window's edge (parity with the main dock / WPF).</summary>
    private void StartDragGhost(int idx)
    {
        if (idx < 0 || idx >= _slots.Count) { EndDragGhost(); return; }
        var img = _slots[idx].Image;
        if (img == null) { EndDragGhost(); return; }   // text/no-bitmap tile — keep the in-window draw
        try
        {
            double dip = _gIcon * 1.12;
            BitmapSource src = img;
            int targetPx = Math.Max(1, (int)Math.Round(dip * _dpi));
            if (src.PixelWidth != targetPx)
            {
                double sx = targetPx / (double)src.PixelWidth, sy = targetPx / (double)src.PixelHeight;
                var scaled = new TransformedBitmap(src, new System.Windows.Media.ScaleTransform(sx, sy));
                scaled.Freeze();
                src = scaled;
            }
            double dipW = src.PixelWidth / _dpi, dipH = src.PixelHeight / _dpi;
            // Reuse one persistent ghost host across drags (see the main dock for the rationale):
            // recreating a whole CompositionHost per drag churned transient driver threads / windows
            // that degraded the frame rate during rapid repeated drags.
            if (_ghost == null) _ghost = new DragGhostWindowGpu(src, dipW, dipH);
            else _ghost.SetSnapshot(src, dipW, dipH);
            MoveDragGhost(_dragMain, _dragCross);
            _ghost.Show();
        }
        catch (Exception ex) { Log.Warn("SideDockGpu", "drag ghost start failed: " + ex.Message); try { _ghost?.Hide(); } catch { } }
    }

    private void MoveDragGhost(float lx, float ly)
    {
        if (_ghost == null) return;
        _ghost.MoveCenterTo(_winX + lx, _winY + ly);
        // Fade while over the unpin zone (dragged clear of the icon column).
        float cross = _side switch
        {
            DockSide.Left => lx,
            DockSide.Right => _winW - lx,
            DockSide.Top => ly,
            _ => _winH - ly,
        };
        bool unpinZone = MathF.Abs(cross - _colCenterCross) > _slabCrossLen * 0.85f;
        _ghost.GhostOpacity = unpinZone ? 0.4 : 1.0;
    }

    private void EndDragGhost()
    {
        // Hide (not destroy) so the GPU host is reused on the next drag; full teardown is DisposeGhost.
        try { _ghost?.Hide(); } catch { }
    }

    /// <summary>Tears down the persistent drag-ghost host (Dispose only; per-drag end just hides it).</summary>
    private void DisposeGhost()
    {
        try { _ghost?.Close(); } catch { }
        _ghost = null;
    }

    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    private float WaveScaleAt(float cursorMain, float iconMain)
    {
        float d = Math.Abs(cursorMain - iconMain) / _cellH;
        float[] xs = _waveDist, ys = _waveScaleY, ms = _waveTan;
        int last = xs.Length - 1;
        if (d <= xs[0]) return ys[0];
        if (d >= xs[last]) return 1f;
        int i = 0;
        while (i < last - 1 && d >= xs[i + 1]) i++;   // locate segment [xs[i], xs[i+1]]
        float h = xs[i + 1] - xs[i];
        float t = (d - xs[i]) / h;
        float t2 = t * t, t3 = t2 * t;
        float h00 = 2f * t3 - 3f * t2 + 1f;
        float h10 = t3 - 2f * t2 + t;
        float h01 = -2f * t3 + 3f * t2;
        float h11 = t3 - t2;
        return h00 * ys[i] + h10 * h * ms[i] + h01 * ys[i + 1] + h11 * h * ms[i + 1];
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

    protected override void Tick()
    {
        if (_host == null)
            return;
        long frameNow = Environment.TickCount64;
        float dt = _animLastMs == 0 ? 0.016f : Math.Clamp((frameNow - _animLastMs) / 1000f, 0f, 0.1f);
        _animLastMs = frameNow;
        EnsureShimTopmost();
        // Drive the show/hide slide+fade. During the hide outro _shown is already
        // false but the fade is still playing, so animate-and-render then bail.
        bool intro = DriveIntro();
        if (!_shown)
        {
            if (intro) Render();
            else StopDriver();   // Hide animation finished: pause the render loop so it no
                                 // longer renders every frame while the dock is hidden.
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

        // During the launch bounce — and through the dismiss fade that follows the hop
        // — keep hover-magnify fully off so the clicked icon de-magnifies, falls back
        // cleanly, and never re-magnifies under the still-stationary cursor (parity with
        // the WPF dock's ResetWave + _dismissing on launch).
        if (_byBounce || _dismissing || !_shown) active = false;

        float k = 1f - (float)Math.Exp(-dt / 0.030f);   // tau 30ms, refresh-rate independent
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
        // Preview is a WPF popup → on the render-thread path marshal to the UI thread, and
        // only when the target changes (DrivePreview is a no-op for an unchanged hover).
        if (UseRenderThread)
        {
            if (_hover != _lastPreviewHover)
            {
                _lastPreviewHover = _hover;
                int h = _hover;
                _dispatcher.BeginInvoke(new Action(() => DrivePreview(h)));
            }
        }
        else
            DrivePreview(_hover);

        // Ease the hover-label opacity toward 1 while an icon is hovered (and not
        // dragging) and toward 0 otherwise, over ~110 ms (parity with WPF). Retain
        // the last hovered slot in _labelIdx so the name fades out in place.
        {
            if (_hover >= 0 && !_dragging) _labelIdx = _hover;
            long nowL = Environment.TickCount64;
            float dtL = Math.Clamp((nowL - _labelLastMs) / 1000f, 0f, 0.1f);
            _labelLastMs = nowL;
            float tgtL = (_hover >= 0 && !_dragging) ? 1f : 0f;
            float kL = 1f - MathF.Exp(-dtL / 0.05f);   // ~110 ms settle
            _labelOp += (tgtL - _labelOp) * kL;
            if (_labelOp < 0.01f && tgtL == 0f) { _labelOp = 0f; _labelIdx = -1; }
        }

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

        // Running-icon glow pulse: breathe the running dot's halo (4.0s full cycle = WPF
        // UpdateRunningDot's 2.0s AutoReverse) in both themes. The flowing border is a
        // main-dock-only effect (WPF side dock shows only the dot, no sweep border).
        if (_anyRunning)
        {
            double rph = Environment.TickCount64 / 1000.0 * 2.0 * Math.PI / 4.0;
            _runPulse = 0.575f + 0.225f * MathF.Sin((float)rph);
        }
        if (!_saturn && _shown)
            _orbitAngle = (_orbitAngle + dt * 360f / 36f) % 360f;

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

    private const long BounceDurMs = 520;   // match WPF DockBounce.Total
    private const long SideSettleMs = 130;  // visible de-magnify before the hop starts
    private int _bounceIdx = -1;
    private long _bounceStart;

    /// <summary>Normalised launch-bounce curve (0 → 1 at the apex → 0 with a springy
    /// landing), mirroring WPF DockBounce: a quick QuadraticEaseOut leap to the apex at
    /// ~33% of the duration, then a BounceEaseOut(2, 2.4) fall with two settling bounces.
    /// Drives both the upward hop and the scale "pop" so the icon leaps and swells like
    /// the original macOS-style dock bounce.</summary>
    private static float BounceCurve01(float t)
    {
        if (t <= 0f || t >= 1f) return 0f;
        const float apex = 170f / 520f;   // WPF ApexAt / Total
        if (t < apex)
        {
            float u = t / apex;
            return 1f - (1f - u) * (1f - u);   // QuadraticEaseOut: 0 → 1
        }
        float p = (t - apex) / (1f - apex);
        return 1f - BounceEaseOut(p);          // 1 → 0 with two landing bounces
    }

    private static float BounceEaseOut(float t) => 1f - BounceEaseIn(1f - t);

    /// <summary>WPF BounceEase core (Bounces = 2, Bounciness = 2.4, EaseIn) ported to
    /// procedural math so the GPU hop lands with the exact original spring.</summary>
    private static float BounceEaseIn(float t)
    {
        const float bounces = 2f, bounciness = 2.4f;
        float pow = MathF.Pow(bounciness, bounces);
        float oneMinusB = 1f - bounciness;
        float sumOfUnits = (1f - pow) / oneMinusB + pow * 0.5f;
        float unitAtT = t * sumOfUnits;
        float bounceAtT = MathF.Log(-unitAtT * oneMinusB + 1f) / MathF.Log(bounciness);
        float start = MathF.Floor(bounceAtT);
        float end = start + 1f;
        float startTime = (1f - MathF.Pow(bounciness, start)) / (oneMinusB * sumOfUnits);
        float endTime = (1f - MathF.Pow(bounciness, end)) / (oneMinusB * sumOfUnits);
        float midTime = (startTime + endTime) * 0.5f;
        float trp = t - midTime;
        float radius = midTime - startTime;
        float amplitude = MathF.Pow(1f / bounciness, bounces - start);
        return (-amplitude / (radius * radius)) * (trp - radius) * (trp + radius);
    }

    /// <summary>Normalised progress (0..1) of the clicked icon's launch bounce, or -1
    /// when this slot is not bouncing / the hop has finished.</summary>
    private float BounceT(int i)
    {
        if (i != _bounceIdx) return -1f;
        long el = Environment.TickCount64 - _bounceStart;
        if (el < 0 || el > BounceDurMs) return -1f;
        return el / (float)BounceDurMs;
    }

    /// <summary>Upward hop offset (px, in the dock's pop direction) for the clicked icon
    /// during its launch bounce — mirrors WPF DockBounce.BuildTranslate(GIcon*0.6).</summary>
    private float BounceOffset(int i)
    {
        float t = BounceT(i);
        return t < 0f ? 0f : _gIcon * 0.6f * BounceCurve01(t);
    }

    /// <summary>Scale "pop" delta (added to the icon's draw scale) synced to the hop, so
    /// the icon swells to ~1.2x at the apex — mirrors WPF DockBounce.BuildScale(1.2).</summary>
    private float BounceScale(int i)
    {
        float t = BounceT(i);
        return t < 0f ? 0f : 0.2f * BounceCurve01(t);
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
    private ID2D1CommandList PrepareSaturnSilhouette(ID2D1DeviceContext ctx)
    {
        // Slab + flame are drawn OPAQUE (alpha 255) into the silhouette. The flame root
        // deliberately overlaps the slab so the single feather blur fuses them; drawing both
        // opaque makes the overlap opaque-over-opaque (no SourceOver double-darkening), and the
        // panel transparency is applied ONCE when the blurred silhouette is composited (see the
        // opacity layer in Render). This keeps slab and flame at the exact same alpha AND avoids
        // a per-frame geometry UNION — that churned ~6 COM objects/frame and spiked gen2 GC
        // (200-280ms hitches) during side-dock flame hover.
        var rr = new RoundedRectangle { Rect = new Rect(_sx, _sy, _sw, _sh), RadiusX = _trayRadius, RadiusY = _trayRadius };
        var flame = BuildFlameGeometry(ctx);

        // Keep the flame tongue clear of the slab's rounded ends: clip it to a main-axis inset.
        float m0 = _satSlabMain + _trayRadius, m1 = _satSlabMain + _satSlabLen - _trayRadius;
        Rect tongueClip = _side is DockSide.Left or DockSide.Right
            ? new Rect(0f, m0, _winW, Math.Max(0f, m1 - m0))
            : new Rect(m0, 0f, Math.Max(0f, m1 - m0), _winH);

        var src = ctx.CreateCommandList();
        ctx.Target = src;
        ctx.BeginDraw();
        using (var black = ctx.CreateSolidColorBrush(Col(255, 6, 8, 12)))
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

        // Reuse one cached GaussianBlur effect (recreated only with the host) instead of newing
        // a COM effect object every frame — this lets D2D reuse the blur's intermediate render
        // targets and avoids per-frame effect churn. The command list `src` still changes each frame.
        _satBlurEffect ??= new Vortice.Direct2D1.Effects.GaussianBlur(ctx);
        _satBlurEffect.SetInput(0, src, true);
        // WPF feathers the group with BlurEffect.Radius = max(12, slabFeather); a D2D
        // Gaussian standard deviation of radius/3 matches that penumbra (same ratio the
        // notch clock uses for its 14px halo).
        _satBlurEffect.StandardDeviation = Math.Max(12f, _flameFeather) / 3f;
        return src;
    }

    /// <summary>Builds the wave-riding "black flame" tongue as a filled path (geometry
    /// only — the fill and feather happen once in the fused silhouette pass). Returns
    /// null when no icon is magnified. Port of WPF UpdateWaveBulge.</summary>
    private ID2D1PathGeometry? BuildFlameGeometry(ID2D1DeviceContext ctx)
    {
        bool vertical = _side is DockSide.Left or DockSide.Right;
        float denom = Math.Max(0.0001f, HoverScale - 1f);
        float peak = 0f, wsum = 0f, csum = 0f;
        // Magnify-wave tongue: collapse the per-icon hover wave into one flame (only while
        // the cursor is actually driving the wave — _byBounce/dismiss force it off).
        if (_curActive)
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                float a = Math.Clamp((_waveCur[i] - 1f) / denom, 0f, 1f);
                if (a <= 0f) continue;
                float w = a * a;
                float main = vertical ? _slots[i].Center.Y : _slots[i].Center.X;
                wsum += w; csum += w * main; if (a > peak) peak = a;
            }
        }
        // Launch-bounce tongue: the clicked icon's live hop height drives its own flame so
        // the tongue leaps up with the icon and falls back with it (parity with WPF
        // StartBounceFlame / OnBounceFlameTick feeding _bounceFlameAmp into UpdateWaveBulge).
        float bt = BounceT(_bounceIdx);
        if (bt >= 0f && _bounceIdx >= 0 && _bounceIdx < _slots.Count)
        {
            float ba = Math.Clamp(BounceCurve01(bt), 0f, 1f);
            if (ba > 0.01f)
            {
                float w = ba * ba;
                float main = vertical ? _slots[_bounceIdx].Center.Y : _slots[_bounceIdx].Center.X;
                wsum += w; csum += w * main; if (ba > peak) peak = ba;
            }
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

    protected override void Render()
    {
        if (_host == null)
            return;
        var ctx = _host.Context;

        // Saturn: pre-render the fused slab+flame silhouette and blur it (its own
        // BeginDraw pass on an alternate target, so it has to happen before the main
        // pass). Stars/debris are drawn crisp on top in the main pass.
        ID2D1CommandList? satSrc = null;
        if (_saturn)
            satSrc = PrepareSaturnSilhouette(ctx);

        ctx.BeginDraw();
        ctx.Clear(Col(0, 0, 0, 0));
        if (_saturn)
        {
            if (_satBlurEffect != null)
            {
                // The silhouette is opaque; apply the panel transparency ONCE here (an opacity
                // layer = a struct param, zero GC alloc) so the slab+flame overlap stays a single
                // uniform alpha with no double-darkening.
                float op = Math.Clamp(_opacity, 0f, 1f);
                if (op < 0.999f)
                {
                    var lp = new LayerParameters1
                    {
                        ContentBounds = new Vortice.RawRectF(float.NegativeInfinity, float.NegativeInfinity, float.PositiveInfinity, float.PositiveInfinity),
                        MaskAntialiasMode = AntialiasMode.PerPrimitive,
                        MaskTransform = Matrix3x2.Identity,
                        Opacity = op,
                    };
                    ctx.PushLayer(lp, null!);
                    ctx.DrawImage(_satBlurEffect, new Vector2(0, 0), InterpolationMode.Linear, CompositeMode.SourceOver);
                    ctx.PopLayer();
                }
                else
                    ctx.DrawImage(_satBlurEffect, new Vector2(0, 0), InterpolationMode.Linear, CompositeMode.SourceOver);
            }
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
        int n = _slots.Count;
        if (_orderBuf == null || _orderBuf.Length != n) _orderBuf = new int[n];
        var order = _orderBuf;
        for (int i = 0; i < n; i++) order[i] = i;
        _orderCmp ??= (a, b) => (_waveCur[a] + BounceScale(a)).CompareTo(_waveCur[b] + BounceScale(b));
        Array.Sort(order, _orderCmp);
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
            DrawIcon(ctx, _slots[i], scale + BounceScale(i), pop);
        }

        // Permanent date/time widget at the far end of the running strip (horizontal docks).
        DrawDateWidget(ctx);

        if (dragIdx >= 0 && dragIdx < _slots.Count && _ghost == null)
        {
            // The dragged icon follows the cursor, lifted 1.12x with no pop.
            var s = _slots[dragIdx];
            var moved = new Slot(new Vector2(_dragMain, _dragCross), s.G, s.Name, s.Running,
                s.Kind, s.IconKey, s.Image, s.Entry, s.Window);
            DrawIcon(ctx, moved, 1.12f, Vector2.Zero);
        }
        // Side dock icons intentionally show NO floating name label (parity request:
        // names only on the main dock). The hover-fade bookkeeping still runs so the
        // preview popup logic is unaffected; we simply don't draw the name here.
        if (_extDragPt is { } dp && _dragIconKey != null)
            DrawDragPreview(ctx, dp);
        ctx.EndDraw();
        _host.Present();
        satSrc?.Dispose();   // the cached _satBlurEffect is reused (disposed with the host)
        Polaris.Services.GpuFrameStats.Frame("side");
    }

    /// <summary>(Re)creates the four DirectWrite formats for the date widget, scaled to the
    /// current icon size. Cheap to skip: only rebuilds when the icon size changes.</summary>
    private void EnsureDateFormats(float g)
    {
        if (_dwrite == null || (_calMonthFormat != null && Math.Abs(_dateFmtG - g) < 0.5f))
            return;
        _calMonthFormat?.Dispose();
        _calDayFormat?.Dispose();
        _weekFormat?.Dispose();
        _timeFormat?.Dispose();
        _dateFmtG = g;
        _calMonthFormat = _dwrite.CreateTextFormat("Microsoft YaHei UI", null, FontWeight.SemiBold,
            FontStyle.Normal, FontStretch.Normal, g * 0.126f, "zh-cn");
        _calMonthFormat.TextAlignment = TextAlignment.Center;
        _calMonthFormat.ParagraphAlignment = ParagraphAlignment.Center;
        _calDayFormat = _dwrite.CreateTextFormat("Microsoft YaHei UI", null, FontWeight.Bold,
            FontStyle.Normal, FontStretch.Normal, g * 0.36f, "zh-cn");
        _calDayFormat.TextAlignment = TextAlignment.Center;
        _calDayFormat.ParagraphAlignment = ParagraphAlignment.Center;
        _weekFormat = _dwrite.CreateTextFormat("Segoe UI Semibold", null, FontWeight.Bold,
            FontStyle.Normal, FontStretch.Normal, g * 0.28f, "zh-cn");
        _weekFormat.TextAlignment = TextAlignment.Leading;
        _weekFormat.ParagraphAlignment = ParagraphAlignment.Center;
        _timeFormat = _dwrite.CreateTextFormat("Segoe UI Semibold", null, FontWeight.Normal,
            FontStyle.Normal, FontStretch.Normal, g * 0.30f, "zh-cn");
        _timeFormat.TextAlignment = TextAlignment.Leading;
        _timeFormat.ParagraphAlignment = ParagraphAlignment.Center;
    }

    /// <summary>Draws the permanent date/time widget pinned one icon-pitch past the last
    /// running icon: a classic calendar page — white body, red header band with "YYYY年M月",
    /// a big bold day number — plus two text lines (bold weekday over the 24-hour time), laid
    /// to the RIGHT on horizontal docks or stacked BELOW on vertical ones. The dock
    /// re-renders every frame while shown, so the date/time read DateTime.Now with no timer.</summary>
    private void DrawDateWidget(ID2D1DeviceContext ctx)
    {
        if (!_dateWidget || _dwrite == null)
            return;
        float g = _dateIconG;
        if (g < 1f)
            return;
        EnsureDateFormats(g);
        var now = DateTime.Now;
        bool vertical = _side is DockSide.Left or DockSide.Right;

        // The widget stays static (no hover magnify/pop). Reset to identity so it never
        // inherits a leftover wave transform from the last magnified icon.
        ctx.Transform = Matrix3x2.Identity;

        float pageG = g * 0.72f;                 // match the app-icon glyph (DrawIcon pads 0.14g → 0.72g visible)
        float half = pageG / 2f;
        float px = _dateIconCX - half, py = _dateIconCY - half;
        var pageRect = new Rect(px, py, pageG, pageG);
        float r = pageG * 0.16f;
        float bandH = pageG * 0.34f;
        float straight = bandH * 0.5f;

        // Raised drop shadow (stronger + larger offset → more 3-D lift off the glass).
        using (var sh = ctx.CreateSolidColorBrush(Col(0x55, 0x18, 0x1A, 0x22)))
            ctx.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(px + pageG * 0.035f, py + pageG * 0.08f, pageG, pageG), RadiusX = r, RadiusY = r }, sh);
        using (var page = ctx.CreateSolidColorBrush(Col(0xFF, 0xFC, 0xFC, 0xFD)))
            ctx.FillRoundedRectangle(new RoundedRectangle { Rect = pageRect, RadiusX = r, RadiusY = r }, page);

        // Red header — rounded top corners (match the page), straight bottom via an overlay
        // rect so the band sits flat against the body (mirrors CalendarClockPopupGpu).
        using (var hstops = ctx.CreateGradientStopCollection(new[]
        {
            new Vortice.Direct2D1.GradientStop { Position = 0f, Color = Col(0xFF, 0xF0, 0x55, 0x55) },
            new Vortice.Direct2D1.GradientStop { Position = 1f, Color = Col(0xFF, 0xD7, 0x37, 0x37) },
        }))
        using (var red = ctx.CreateLinearGradientBrush(
            new LinearGradientBrushProperties { StartPoint = new Vector2(px, py), EndPoint = new Vector2(px, py + bandH) }, hstops))
        {
            ctx.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(px, py, pageG, bandH), RadiusX = r, RadiusY = r }, red);
            ctx.FillRectangle(new Rect(px, py + bandH - straight, pageG, straight), red);
        }
        if (_calMonthFormat != null)
            using (var mt = ctx.CreateSolidColorBrush(Col(0xFF, 0xFF, 0xFF, 0xFF)))
                ctx.DrawText($"{now.Year}年{now.Month}月", _calMonthFormat, new Rect(px, py, pageG, bandH), mt);

        // Big bold day number filling the body below the band.
        if (_calDayFormat != null)
            using (var dk = ctx.CreateSolidColorBrush(Col(0xFF, 0x2C, 0x2E, 0x36)))
                ctx.DrawText(now.Day.ToString(), _calDayFormat, new Rect(px, py + bandH - pageG * 0.04f, pageG, pageG - bandH), dk);

        // Glossy top-edge highlight + hairline frame → a crisper, more 3-D glass edge.
        using (var hi = ctx.CreateSolidColorBrush(Col(0x66, 0xFF, 0xFF, 0xFF)))
            ctx.FillRectangle(new Rect(px + r, py + 0.6f, pageG - r * 2f, 1.1f), hi);
        using (var bd = ctx.CreateSolidColorBrush(Col(0x5A, 0xFF, 0xFF, 0xFF)))
            ctx.DrawRoundedRectangle(new RoundedRectangle { Rect = pageRect, RadiusX = r, RadiusY = r }, bd, 1f);

        // Weekday (bold) over the 24-hour time. To the RIGHT (horizontal) or stacked BELOW
        // (vertical). A faint copy offset down-right gives an embossed / letterpress 3-D look.
        string week = "星期" + "日一二三四五六"[(int)now.DayOfWeek];
        string time = $"{now.Hour:D2}:{now.Minute:D2}";
        var inkCol = _saturn ? Col(0xFF, 0xF4, 0xF6, 0xFA) : Col(0xFF, 0x0A, 0x0A, 0x0A);
        var embCol = _saturn ? Col(0x80, 0x00, 0x00, 0x00) : Col(0x9A, 0xFF, 0xFF, 0xFF);
        Rect wkRect, tmRect;
        if (!vertical)
        {
            if (_weekFormat != null) _weekFormat.TextAlignment = TextAlignment.Leading;
            if (_timeFormat != null) _timeFormat.TextAlignment = TextAlignment.Leading;
            float textLeft = _dateIconCX + half + g * 0.20f;
            float lineH = g * 0.40f;
            wkRect = new Rect(textLeft, _dateIconCY - lineH, g * 1.75f, lineH);
            tmRect = new Rect(textLeft, _dateIconCY, g * 1.75f, lineH);
        }
        else
        {
            if (_weekFormat != null) _weekFormat.TextAlignment = TextAlignment.Center;
            if (_timeFormat != null) _timeFormat.TextAlignment = TextAlignment.Center;
            float lineH = g * 0.42f;
            float colW = g * 1.7f;
            float colLeft = _dateIconCX - colW / 2f;
            float textTop = _dateIconCY + half + g * 0.08f;
            wkRect = new Rect(colLeft, textTop, colW, lineH);
            tmRect = new Rect(colLeft, textTop + lineH, colW, lineH);
        }
        // Smooth (non-grid-fitted) glyph edges for just the weekday + time lines.
        if (_smoothText != null) ctx.TextRenderingParams = _smoothText;
        using (var emb = ctx.CreateSolidColorBrush(embCol))
        {
            if (_weekFormat != null) ctx.DrawText(week, _weekFormat, new Rect(wkRect.X + 0.8f, wkRect.Y + 0.9f, wkRect.Width, wkRect.Height), emb);
            if (_timeFormat != null) ctx.DrawText(time, _timeFormat, new Rect(tmRect.X + 0.8f, tmRect.Y + 0.9f, tmRect.Width, tmRect.Height), emb);
        }
        using (var ink = ctx.CreateSolidColorBrush(inkCol))
        {
            if (_weekFormat != null) ctx.DrawText(week, _weekFormat, wkRect, ink);
            if (_timeFormat != null) ctx.DrawText(time, _timeFormat, tmRect, ink);
        }
        if (_smoothText != null) ctx.TextRenderingParams = null;
        ctx.Transform = Matrix3x2.Identity;
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
            new Vortice.Direct2D1.GradientStop { Position = 0f,    Color = Col(0x3C, 0xE0, 0xEC, 0xEC) },
            new Vortice.Direct2D1.GradientStop { Position = 0.34f, Color = Col(0x22, 0x88, 0xC4, 0xEC) },
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
            // Breathing radial halo: cache a CONSTANT-alpha stop collection (alphas baked at the
            // _runPulse=0.8 peak) + radial brush ONCE (host lifetime); per draw move the brush to this
            // icon and scale its alpha via Opacity = _runPulse/0.8 (always ≤1) — exactly equal to the
            // old per-stop alpha × breath since _runPulse is bounded to ≤0.8. Avoids rebuilding the
            // gradient + brush every running icon every frame.
            if (_runHaloBrush == null)
            {
                const float peak = 0.8f / 0.575f;   // breath at _runPulse = 0.8
                _runHaloStops = ctx.CreateGradientStopCollection(new[]
                {
                    new Vortice.Direct2D1.GradientStop { Position = 0f, Color = new Color4(0x5C / 255f, 1f, 0x7A / 255f, 0.45f * peak) },
                    new Vortice.Direct2D1.GradientStop { Position = 0.5f, Color = new Color4(0x5C / 255f, 1f, 0x7A / 255f, 0.18f * peak) },
                    new Vortice.Direct2D1.GradientStop { Position = 1f, Color = new Color4(0x5C / 255f, 1f, 0x7A / 255f, 0f) },
                });
                _runHaloBrush = ctx.CreateRadialGradientBrush(
                    new RadialGradientBrushProperties { Center = center, GradientOriginOffset = Vector2.Zero, RadiusX = gr, RadiusY = gr },
                    _runHaloStops);
            }
            _runHaloBrush.Center = center;
            _runHaloBrush.RadiusX = gr;
            _runHaloBrush.RadiusY = gr;
            _runHaloBrush.Opacity = _runPulse / 0.8f;
            ctx.FillEllipse(new Ellipse(center, gr, gr), _runHaloBrush);
            _runDotBrush ??= ctx.CreateSolidColorBrush(new Color4(0x4C / 255f, 0xE0 / 255f, 0x6B / 255f, 1f));
            ctx.FillEllipse(new Ellipse(center, dot / 2f, dot / 2f), _runDotBrush);
        }

        // New-message attention dot: a small pulsing red disc hugging the icon's
        // top-right corner when any of this app's windows is flashing for attention
        // (mirrors the system taskbar's top-right unread badge and the GPU main dock).
        if (s.Running && _flashKeys.Contains(s.IconKey))
        {
            ctx.Transform = wave;
            float d = Math.Clamp(g * 0.10f, 4.5f, 9f) * _badgePulse;
            var bp = new Vector2(cx + half - d, cy - half + d);
            using (var glow = ctx.CreateSolidColorBrush(Col(0x55, 0xFF, 0x3B, 0x30)))
                ctx.FillEllipse(new Ellipse(bp, d * 0.78f, d * 0.78f), glow);
            using (var rd = ctx.CreateSolidColorBrush(Col(0xFF, 0xFF, 0x3B, 0x30)))
                ctx.FillEllipse(new Ellipse(bp, d * 0.5f, d * 0.5f), rd);
        }
        ctx.Transform = Matrix3x2.Identity;
    }

    private void DrawHoverLabel(ID2D1DeviceContext ctx, in Slot s, float scale, float labelOp)
    {
        if (_hoverFormat == null || _dwrite == null || string.IsNullOrEmpty(s.Name))
            return;
        bool vertical = _side is DockSide.Left or DockSide.Right;
        // Clear the magnified + popped focal icon. The label's near edge sits right at
        // the hover-enlarged icon's outer edge (parity with WPF ShowHoverLabelCore).
        float reach = s.G / 2f * scale + (scale - 1f) * _gIcon * 1.18f;
        float baseFp = _hoverFontPx;

        // Auto-fit the font so a long name fits the dock thickness (vertical docks grow the
        // label along the cross axis toward the interior; a fixed font would overrun the
        // window). Recompute only when the hovered name changes — DrawHoverLabel runs every
        // render frame while the label is up.
        if (!string.Equals(_labelFitName, s.Name, StringComparison.Ordinal))
        {
            _labelFitName = s.Name;
            float naturalW = MeasureLabelWidth(s.Name);
            float fp = baseFp;
            if (vertical)
            {
                float avail = _side == DockSide.Left
                    ? _winW - (s.Center.X + reach) - 8f
                    : (s.Center.X - reach) - 8f;
                avail = Math.Max(40f, avail);
                float minFp = (float)(7.5 * HoverScale * FontScale.Current);
                const float pad = 18f;
                if (naturalW + pad > avail)
                    fp = Math.Max(minFp, baseFp * (avail - pad) / Math.Max(1f, naturalW));
            }
            _labelFitFp = fp;
            _labelFitW = naturalW * fp / baseFp + 18f;
        }

        float fpUse = _labelFitFp;
        float w = Math.Max(40f, _labelFitW), h = fpUse + 12f;
        // Pick the format: the shared base format unless the name was shrunk, in which case a
        // cached fitted-size format (recreated only when the fitted size changes).
        IDWriteTextFormat fmt = _hoverFormat;
        if (Math.Abs(fpUse - baseFp) > 0.5f)
        {
            if (_fitFormat == null || Math.Abs(_fitFormatFp - fpUse) > 0.5f)
            {
                _fitFormat?.Dispose();
                _fitFormat = _dwrite.CreateTextFormat("Microsoft YaHei UI", null, FontWeight.SemiBold,
                    FontStyle.Normal, FontStretch.Normal, fpUse, "zh-cn");
                _fitFormat.ParagraphAlignment = ParagraphAlignment.Center;
                _fitFormatFp = fpUse;
            }
            fmt = _fitFormat;
        }
        (float lx, float ly) = _side switch
        {
            DockSide.Left => (s.Center.X + reach + w / 2f, s.Center.Y),
            DockSide.Right => (s.Center.X - reach - w / 2f, s.Center.Y),
            DockSide.Top => (s.Center.X, s.Center.Y + reach + h / 2f),
            _ => (s.Center.X, s.Center.Y - reach - h / 2f),
        };
        var rect = new Rect(lx - w / 2f, ly - h / 2f, w, h);
        // Hug the icon edge: for vertical docks the label grows toward the interior, so align
        // the text to the icon side of its box (leading on the left dock, trailing on the
        // right) instead of centring it. Horizontal docks stay centred over the icon.
        fmt.TextAlignment = _side switch
        {
            DockSide.Left => TextAlignment.Leading,
            DockSide.Right => TextAlignment.Trailing,
            _ => TextAlignment.Center,
        };
        byte A(byte a) => (byte)Math.Clamp(a * labelOp, 0, 255);
        // The real hover label is just floating text on a barely-there dark tint
        // (ARGB 0x05,1A1A1A) — no visible plate.
        using (var bg = ctx.CreateSolidColorBrush(Col(A(0x05), 0x1A, 0x1A, 0x1A)))
            ctx.FillRoundedRectangle(new RoundedRectangle { Rect = rect, RadiusX = 7f, RadiusY = 7f }, bg);
        // 3-D raised lettering: dark offset copies behind the light text give the name
        // depth and a legibility halo, mirroring the WPF DropShadowEffect (black, depth
        // 1.4, direction 315° → a ~1px down-right offset, plus a soft second copy).
        using (var halo = ctx.CreateSolidColorBrush(Col(A(0xE6), 0, 0, 0)))
        {
            ctx.DrawText(s.Name, fmt, new Rect(rect.X + 1f, rect.Y + 1.2f, rect.Width, rect.Height), halo);
            ctx.DrawText(s.Name, fmt, new Rect(rect.X - 0.6f, rect.Y + 0.5f, rect.Width, rect.Height), halo);
        }
        using (var ink = ctx.CreateSolidColorBrush(Col(A(0xF2), 0xFF, 0xFF, 0xFF)))
            ctx.DrawText(s.Name, fmt, rect, ink);
    }

    /// <summary>Natural (unconstrained) DIP width of the name at the base hover font, used to
    /// decide whether the label must shrink to fit a vertical dock's thickness.</summary>
    private float MeasureLabelWidth(string text)
    {
        try
        {
            using var layout = _dwrite!.CreateTextLayout(text, _hoverFormat!, 10000f, 200f);
            return layout.Metrics.Width;
        }
        catch { return text.Length * _hoverFontPx * 0.95f; }
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
        using (var glowWide = ctx.CreateSolidColorBrush(Col(0x2C, 0xBF, 0xE0, 0xFF)))
            ctx.DrawLine(p0, p1, glowWide, 3f);
        using (var glow = ctx.CreateSolidColorBrush(Col(0x66, 0xBF, 0xE0, 0xFF)))
            ctx.DrawLine(p0, p1, glow, 1.6f);
        using (var shine = ctx.CreateSolidColorBrush(Col(0xA0, 0xEA, 0xF4, 0xFF)))
            ctx.DrawLine(p0, p1, shine, 0.5f);
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
    // A pinned launcher whose running window is a separate helper process (not
    // matched to the pinned exe by path or name) lists its helper exe file name(s)
    // here, so the helper window is folded into the launcher tile instead of showing
    // as a separate running app. Keyed on the pinned launcher's exe file name.
    // Mirrors SideDockWindow.LauncherHelperExeNames.
    private static readonly Dictionary<string, string[]> LauncherHelperExeNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["steam.exe"] = new[] { "steamwebhelper.exe" },
        };

    private List<RunItem> CollectRunning(IReadOnlyList<AppEntry> pinned, out int overflow)
    {
        overflow = 0;
        var result = new List<RunItem>();
        try
        {
            var excludePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var excludeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var excludeAumids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var excludeTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddLauncherHelpers(string exePath)
            {
                string launcher;
                try { launcher = System.IO.Path.GetFileName(exePath); }
                catch { return; }
                if (!string.IsNullOrWhiteSpace(launcher) && LauncherHelperExeNames.TryGetValue(launcher, out var helpers))
                    foreach (var h in helpers) excludeNames.Add(h);
            }
            void AddPathAndName(string p)
            {
                try { excludePaths.Add(System.IO.Path.GetFullPath(p)); } catch { excludePaths.Add(p); }
                try { var fn = System.IO.Path.GetFileName(p); if (!string.IsNullOrWhiteSpace(fn)) excludeNames.Add(fn); }
                catch { /* unreadable path */ }
                AddLauncherHelpers(p);
            }
            foreach (var a in pinned)
            {
                // Path-protected apps (UU加速器) expose no usable path/AUMID for their
                // running window, so also exclude any running window whose TITLE equals
                // a resident pin's display name (parity with WPF excludeTitles).
                if (!string.IsNullOrWhiteSpace(a.Name))
                    excludeTitles.Add(a.Name);
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
                // Folder de-dup for launcher pins whose running main exe lives in a
                // (version) subfolder, e.g. resident UU\uu_launcher.exe vs the running
                // UU\5224\uu.exe. Mirrors the WPF side dock's RefreshRunning filter and
                // the green-light IsSameOrChildInstallFolder, so one app never shows both
                // a pinned icon and a duplicate running tile.
                bool inPinnedFolder = false;
                if (!string.IsNullOrWhiteSpace(ta.Path))
                {
                    foreach (var a in pinned)
                    {
                        if (!string.IsNullOrWhiteSpace(a.Path)
                            && RunningAppTracker.IsSameOrChildInstallFolder(a.Path, ta.Path))
                        {
                            inPinnedFolder = true;
                            break;
                        }
                    }
                }
                if (inPinnedFolder)
                    continue;
                // Title fallback for path-protected apps whose running window carries
                // no usable path/AUMID (parity with WPF excludeTitles).
                if (!string.IsNullOrWhiteSpace(ta.Title) && excludeTitles.Contains(ta.Title))
                    continue;
                filtered.Add(ta);
            }

            const int max = 12;   // RunningMaxComplete — hard cap; apps beyond 12 are dropped (no "+N" tile)
            if (filtered.Count > max)
                filtered = filtered.GetRange(0, max);
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
        StopDriver();
        _timer?.Stop();
        _refreshTimer?.Stop();
        FlushPersist();
        FlushMainDockChanged();
        CloseSlotMenu();
        ClosePreview();
        if (_anchorWin != null) { try { _anchorWin.Close(); } catch { } _anchorWin = null; _anchorEl = null; _preview = null; }
        _calClock?.Dispose(); _calClock = null;
        // Dispose all GPU resources on the render thread (their owner) and wait, then stop +
        // join the render thread, all BEFORE the HWND is destroyed. Inline on the default path.
        InvokeOnRender(DisposeHostResources);
        if (UseRenderThread) { _loop?.Stop(); _loop = null; }
        DisposeGhost();
        _dropShim?.Dispose(); _dropShim = null;
        if (_hwnd != IntPtr.Zero) { s_instances.Remove(_hwnd); DestroyWindow(_hwnd); }
    }

    // ---- Interaction (Stage E): click-launch, drag-reorder, drag-out-unpin ----

    private const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_NCHITTEST = 0x0084;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_DROPFILES = 0x0233;
    private const int HTTRANSPARENT = -1, HTCLIENT = 1;

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
                int hit = HitSlot(lx, ly);
                _pressMain = lx; _pressCross = ly;
                _dragMain = lx; _dragCross = ly;
                _pressIdx = hit;
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
                bool startDrag = false;
                _dragMain = lx; _dragCross = ly;
                if (!_dragging)
                {
                    float ddx = lx - _pressMain, ddy = ly - _pressCross;
                    if (ddx * ddx + ddy * ddy > DragThreshold * DragThreshold)
                    {
                        _dragging = true;
                        _dragShiftLastMs = Environment.TickCount64;   // seed so first advance dt is small
                        startDrag = true;
                    }
                }
                if (startDrag)
                    StartDragGhost(_pressIdx);   // lift the icon into an independent desktop overlay
                if (_dragging)
                {
                    MoveDragGhost(lx, ly);
                    UpdateDragGap(lx, ly);
                    // Default path: keep neighbours in step between ticks + repaint inline. On
                    // the render-thread path the loop runs AdvanceDragShift + draws each frame.
                    if (!UseRenderThread) { AdvanceDragShift(); Render(); }
                }
                return true;
            }
            case WM_LBUTTONUP:
            {
                ReleaseCapture();
                EndDragGhost();   // dismiss the desktop overlay before committing the drop
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
                    RequestRender();
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
        // Hit-test against each icon's CURRENT rendered position/size: a hovered icon
        // magnifies and pops outward from the slab, so testing the static slot centre
        // would miss clicks on the visibly popped icon (parity with WPF, which hit-tests
        // the magnified icon). Grow the catch radius with the icon's live wave scale.
        int best = -1; float bestD = float.MaxValue;
        for (int i = 0; i < _slots.Count; i++)
        {
            float scale = i < _waveCur.Length ? _waveCur[i] : 1f;
            Vector2 pop = PopOffset((scale - 1f) * _gIcon * 1.18f + BounceOffset(i));
            var c = _slots[i].Center + pop;
            float r = _gIcon * 0.75f * MathF.Max(1f, scale);
            float d = MathF.Abs(lx - c.X) + MathF.Abs(ly - c.Y);
            if (d < r && d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private void ClickSlot(int idx)
    {
        var s = _slots[idx];
        // Resolve the action to run AFTER the launch bounce plays. Parity with WPF
        // (SideDockWindow.Bounce/Running): every tile — pinned, running, the Polaris
        // self-tile — hops first while the dock is held visible, then launches /
        // activates / toggles. Doing it first would bring the target window over the
        // dock and hide the bounce.
        Action? act = null;
        if (s.Kind == SlotKind.Pinned && s.Entry != null)
        {
            var entry = s.Entry;
            act = () => { try { AppLauncher.Launch(entry, null); } catch (Exception ex) { Log.Warn("SideDockGpu", "launch failed: " + ex.Message); } };
        }
        else if (s.Kind == SlotKind.Run && s.Window != IntPtr.Zero)
        {
            var win = s.Window;
            act = () => { try { WindowPreviewService.Activate(win); } catch (Exception ex) { Log.Warn("SideDockGpu", "activate failed: " + ex.Message); } };
        }
        else if (s.Kind == SlotKind.Run && s.Window == IntPtr.Zero
                 && s.IconKey.StartsWith("polaris:", StringComparison.Ordinal))
        {
            act = () => ToggleDocks?.Invoke();   // the Polaris tile toggles the pinned docks
        }
        if (act == null)
            return;

        // Original launch order: the clicked icon first eases back to its REST size (a brief,
        // visible de-magnify), THEN hops up and falls back. _byBounce forces the magnify wave
        // off (Tick) so it settles to 1.0 during the settle window, and the hop only begins
        // once _bounceStart is reached (BounceT returns -1 until then).
        _bounceIdx = idx; _bounceStart = Environment.TickCount64 + SideSettleMs;   // settle, then hop
        _byBounce = true;                                            // hold the dock open through the hop
        UpdateVisibility();
        StartDriver();
        RequestRender();
        var hold = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SideSettleMs + BounceDurMs) };
        hold.Tick += (_, _) =>
        {
            hold.Stop();
            act();
            _byBounce = false;
            // Retract the dock after the hop (parity with the WPF dock's
            // SetEdgeShown(false) on launch) and latch _dismissing so the wave cannot
            // re-magnify the clicked icon under the still-stationary cursor while the
            // dock fades out. _dismissing clears once the dock is hidden (DriveIntro) or
            // any fresh show reason appears (UpdateVisibility).
            _byEdge = false;
            _dismissing = true;
            UpdateVisibility();
        };
        hold.Start();
    }

    // ---- Right-click context menu (parity with the WPF dock) -----------------

    private System.Windows.Controls.Primitives.Popup? _slotMenu;
    private int _menuIdx = -1;   // slot the right-click menu is anchored to (-1 = none)

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
            // The Polaris tile (no backing window) has no meaningful menu — the WPF
            // dock gives it none either, so skip it rather than offer a dead "close".
            if (s.IconKey.StartsWith("polaris:", StringComparison.Ordinal))
                return;
            if (!string.IsNullOrWhiteSpace(s.RunPath) || !string.IsNullOrWhiteSpace(s.RunAumid))
                items.Add(("固定到常驻区", () => PinRunningSlot(s)));
            items.Add(("关闭窗口", () => CloseSlotWindows(s)));
        }
        if (items.Count == 0)
            return;
        _menuIdx = idx;
        // If the hover thumbnail preview is open, fade it out first and show the menu only
        // once the animation finishes, so the two never overlap (parity with WPF
        // ShowDockMenu(fadePreview)). Hold the dock open across the fade gap.
        var slot = s;
        if (_preview != null && _preview.IsOpen)
        {
            _byMenu = true;
            UpdateVisibility();
            _preview.CloseAnimated(() => BuildAndShowSlotMenu(slot, items));
            return;
        }
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
        // Anchor relative to the HOVER-MAGNIFIED + popped icon (same box the thumbnail preview
        // anchors to in AnchorOverSlot), opening toward the screen interior, and clamp to the
        // work area so the menu stays fully on-screen no matter which edge the dock is on. The
        // pop factor is dialled back from the icon's full visual pop (1.18) so the menu sits a
        // touch closer to the icon (the full pop reads as slightly too far).
        float scale = HoverScale;
        Vector2 po = PopOffset((scale - 1f) * _gIcon * 0.6f);
        double half = _gIcon * scale / 2.0; const double gap = 6.0;
        double cxDip = _winX + s.Center.X + po.X;
        double cyDip = _winY + s.Center.Y + po.Y;
        double px, py;
        switch (_side)
        {
            case DockSide.Left:   px = cxDip + half + gap;            py = cyDip - ds.Height / 2.0; break;
            case DockSide.Right:  px = cxDip - half - gap - ds.Width; py = cyDip - ds.Height / 2.0; break;
            case DockSide.Top:    px = cxDip - ds.Width / 2.0;        py = cyDip + half + gap; break;
            default:              px = cxDip - ds.Width / 2.0;        py = cyDip - half - gap - ds.Height; break;  // Bottom → above
        }
        var wa = MonitorLayout.ActiveWorkArea;   // DIPs — keep the menu within the visible work area
        const double edge = 6.0;
        px = Math.Clamp(px, wa.Left + edge, Math.Max(wa.Left + edge, wa.Right - ds.Width - edge));
        py = Math.Clamp(py, wa.Top + edge, Math.Max(wa.Top + edge, wa.Bottom - ds.Height - edge));
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
        popup.Closed += (_, _) => { if (ReferenceEquals(_slotMenu, popup)) _slotMenu = null; _byMenu = false; UpdateVisibility(); };
        _slotMenu = popup;
        _byMenu = true;   // hold the dock open while the context menu is up (parity with WPF _menuHold)
        UpdateVisibility();
        popup.IsOpen = true;
    }

    private void CloseSlotMenu()
    {
        _menuIdx = -1;
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
    private CalendarClockPopupGpu? _calClock;   // hover popup on the Polaris tile (glass theme)

    /// <summary>Names of OTHER pinned shell-namespace folders (This PC, Recycle Bin…) so the
    /// generic File Explorer preview can drop the windows those sibling pins already claim.</summary>
    private List<string> SiblingShellNames(AppEntry self)
    {
        var names = new List<string>();
        foreach (var a in _config.Apps)
            if (!ReferenceEquals(a, self) && WindowPreviewService.IsShellFolderPath(a.Path)
                && !string.IsNullOrWhiteSpace(a.Name))
                names.Add(a.Name);
        return names;
    }

    /// <summary>Builds the windows-source delegate for a slot, or null when the slot
    /// has no previewable windows (overflow, or our own Polaris tile).</summary>
    private Func<List<WindowPreview>>? PreviewSourceFor(in Slot s)
    {
        if (s.Kind == SlotKind.Pinned && s.Entry != null && !string.IsNullOrWhiteSpace(s.Entry.Path))
        {
            var path = s.Entry.Path; var args = s.Entry.Arguments; var name = s.Entry.Name;
            var excl = SiblingShellNames(s.Entry);
            return () => WindowPreviewService.GetWindowsForEntry(path, args, name, excl);
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

    /// <summary>True for the dock's own Polaris tile in the running strip (the first running
    /// slot, keyed "polaris:&lt;exe&gt;"). It has no window preview; instead it shows the
    /// calendar/clock hover popup.</summary>
    private static bool IsPolarisTile(in Slot s) =>
        s.Kind == SlotKind.Run && s.IconKey != null
        && s.IconKey.StartsWith("polaris:", StringComparison.Ordinal);

    /// <summary>Retired: the Polaris tile's hover calendar/clock popup has been replaced by the
    /// permanent date/time widget pinned at the far end of the running strip (see
    /// <see cref="DrawDateWidget"/>). Kept as a no-op that simply hides any popup left over from a
    /// previous build, so the Polaris tile now hovers like any other icon (click still toggles
    /// the docks). Called on every hover change from <see cref="DrivePreview"/>.</summary>
    private void UpdateCalendarClock(int hover)
    {
        _ = hover;
        _calClock?.Hide();
    }

    /// <summary>Called every Tick with the slot under the cursor (or -1). Drives the
    /// reusable thumbnail popup: re-anchors and re-enters on a slot change, leaves on
    /// exit. The popup's own dwell timers handle the open/close delays and the
    /// icon→popup pointer travel.</summary>
    private void DrivePreview(int hover)
    {
        if (hover == _prevHover)
            return;
        // A right-click menu is anchored to one icon; moving the pointer onto a different
        // icon dismisses it (parity with WPF / standard menu behaviour).
        if (_slotMenu != null && hover != _menuIdx)
            CloseSlotMenu();
        if (_prevHover >= 0)
            _preview?.OnPointerLeave();
        _prevHover = hover;
        if (hover < 0 || hover >= _slots.Count)
        {
            UpdateCalendarClock(hover);   // pointer off any tile → hide the clock
            return;
        }
        var s = _slots[hover];
        // The Polaris tile shows the calendar/clock popup instead of a window
        // thumbnail preview (see IsPolarisTile). Don't drive the thumbnail popup
        // for it: PreviewSourceFor would return Polaris's OWN (effectively empty)
        // window list, so OnPointerEnter below would re-target the shared popup to
        // it — blanking the previous tile's thumbnails and letting the emptied
        // popup linger through the close delay. The pointer has definitively
        // landed on another icon, so close any open preview at once and show only
        // the clock.
        //
        // Close the preview BEFORE showing the clock: tearing the thumbnail popup
        // down (WPF popup + DWM thumbnails) runs on this UI thread and can stall it
        // ~100ms, which — if done AFTER UpdateCalendarClock — delays the clock's
        // first fade tick by that long, so the clock appears already shown instead
        // of fading in (the "no fade-in when coming from an icon with a preview
        // open" bug). Doing the teardown first lets the fade start on the next tick.
        if (IsPolarisTile(s))
        {
            _preview?.Close();
            UpdateCalendarClock(hover);
            return;
        }
        UpdateCalendarClock(hover);   // non-Polaris tile → hide the clock
        var src = PreviewSourceFor(s);
        if (src == null)
            return;
        _previewSource = src;
        EnsureAnchor();
        if (_preview == null || _anchorWin == null)
            return;   // anchor/popup not ready (e.g. creation failed) — skip this hover
        AnchorOverSlot(s);
        _preview.Placement = PreviewPlacementForSide();
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
        // Use _preview (created last) as the "fully initialised" flag, not _anchorWin:
        // if a previous attempt assigned _anchorWin but then threw before creating
        // _preview, keying off _anchorWin would skip re-creation forever and leave
        // _preview null, NRE-ing every frame in DrivePreview (this was the 441x
        // SideDockWindowGpu.Tick NRE in the log).
        if (_preview != null)
            return;
        try
        {
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
        catch (Exception ex)
        {
            Log.Warn("SideDockGpu", "preview anchor init failed: " + ex.Message);
            try { _anchorWin?.Close(); } catch { }
            _anchorWin = null;
            _anchorEl = null;
            _preview = null;
        }
    }

    /// <summary>Positions the anchor window so its element overlaps the hovered icon's
    /// FULLY-MAGNIFIED, popped-out glyph box (screen DIPs), then pulls it a fixed few px back
    /// toward the icon. The preview card's thumbnails are inset from its shell edge by a tile's
    /// worth of chrome (shell padding + tile margin/padding), so parked at the raw gap they read
    /// as farther from the icon than the calendar/clock card's edge-filled glass. Nudging the
    /// anchor in by that inset makes the visible thumbnails hug the icon by the same amount the
    /// clock card does (a fixed px, not a scale factor, since the chrome is a fixed DIP inset).</summary>
    private void AnchorOverSlot(in Slot s)
    {
        if (_anchorWin == null)
            return;
        float scale = HoverScale;
        // ~one tile's near-side chrome (border + shell padding + tile margin/padding); pulls the
        // inset thumbnails in to the same near edge the clock card keeps beside the Polaris tile.
        // Nudged up from 12 so the preview tail hugs the icon a touch closer still.
        const float previewHug = 16f;
        Vector2 po = PopOffset((scale - 1f) * _gIcon * 1.18f - previewHug);
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
        _calClock?.Hide();
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
            RequestRender();
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
        // DragQueryPoint gives client (physical) coords; convert to window-local DIP.
        InsertDroppedEntries(entries, (float)(clientPt.X / _dpi), (float)(clientPt.Y / _dpi));
    }

    /// <summary>Handles a committed OLE drop from Explorer / the desktop (files AND
    /// shell-namespace items), pinning each into the resident column. Mirrors the WPF
    /// side dock's AllowDrop. <paramref name="screenX"/>/<paramref name="screenY"/> are
    /// screen pixels (the IDropTarget POINTL).</summary>
    private void HandleOleDrop(List<string> files, byte[]? shellIdList, int screenX, int screenY)
    {
        var entries = new List<AppEntry>();
        foreach (var f in files)
        {
            try { var e = ShortcutResolver.CreateEntry(f); if (e != null) entries.Add(e); }
            catch { /* skip an unresolvable drop */ }
        }
        if (shellIdList != null)
        {
            try { entries.AddRange(ShellNamespace.CreateEntriesFromBytes(shellIdList)); }
            catch (Exception ex) { Log.Warn("SideDockGpu", "shell-item drop parse failed: " + ex.Message); }
        }
        // Screen pixels → window-local DIPs.
        float lx = (float)(screenX / _dpi - _winX);
        float ly = (float)(screenY / _dpi - _winY);
        _extDragPt = null; _dragIconKey = null;   // drag finished — clear the preview
        _dispatcher.BeginInvoke(new Action(() => InsertDroppedEntries(entries, lx, ly)),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Shared drop core: pins each resolved entry into the resident column at
    /// the slot nearest the window-local drop point (<paramref name="lx"/>,<paramref
    /// name="ly"/>).</summary>
    private void InsertDroppedEntries(List<AppEntry> entries, float lx, float ly)
    {
        if (entries.Count == 0)
            return;

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
        try { DockSync.MirrorResidentToLeft(_config); }
        catch (Exception ex) { Log.Warn("SideDockGpu", "mirror failed: " + ex.Message); }
        ThemeRegistry.SaveAppearance(_config.Settings);
        PersistSoon();
        // Count-changing operations should not make the side dock vanish/pop back or delay the
        // next edge summon. Keep the existing host/window alive and relayout in place so add /
        // delete is visually stable and the edge trigger remains immediately responsive.
        RelayoutInPlace();
        NotifyMainDockChangedSoon();
    }

    /// <summary>Persists then relayouts in place (no window/host recreation) so a
    /// reorder drop does not flash. Used for reorders, where the icon count — and
    /// hence the window geometry — is unchanged (mirrors the main dock's
    /// PersistAndRelayout).</summary>
    private void PersistAndRelayout()
    {
        try { DockSync.MirrorResidentToLeft(_config); }
        catch (Exception ex) { Log.Warn("SideDockGpu", "mirror failed: " + ex.Message); }
        ThemeRegistry.SaveAppearance(_config.Settings);
        PersistSoon();
        RelayoutInPlace();
        NotifyMainDockChangedSoon();
    }

    private void PersistSoon()
    {
        if (_persistTimer == null)
        {
            _persistTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _persistTimer.Tick += (_, _) =>
            {
                _persistTimer!.Stop();
                try
                {
                    ThemeRegistry.SaveAppearance(_config.Settings);
                    ConfigStore.Save(_config);
                }
                catch (Exception ex) { Log.Warn("SideDockGpu", "persist failed: " + ex.Message); }
            };
        }
        _persistTimer.Stop();
        _persistTimer.Start();
    }

    private void FlushPersist()
    {
        if (_persistTimer is { IsEnabled: true })
        {
            _persistTimer.Stop();
            try
            {
                ThemeRegistry.SaveAppearance(_config.Settings);
                ConfigStore.Save(_config);
            }
            catch (Exception ex) { Log.Warn("SideDockGpu", "persist failed: " + ex.Message); }
        }
    }

    private void NotifyMainDockChangedSoon()
    {
       if (MainDockChanged == null)
           return;
       if (_mainDockChangedTimer == null)
       {
           _mainDockChangedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
           _mainDockChangedTimer.Tick += (_, _) =>
           {
               _mainDockChangedTimer!.Stop();
                   try { MainDockChanged?.Invoke(); } catch { }
           };
       }
       _mainDockChangedTimer.Stop();
       _mainDockChangedTimer.Start();
    }

    private void FlushMainDockChanged()
    {
       if (_mainDockChangedTimer is { IsEnabled: true })
       {
           _mainDockChangedTimer.Stop();
           try { MainDockChanged?.Invoke(); } catch { }
       }
    }

    /// <summary>Recomputes the slots/geometry in the existing window. Falls back to a
    /// full Rebuild only when the window geometry actually changed.</summary>
    private void RelayoutInPlace()
    {
        long t0All = Stopwatch.GetTimestamp();
        if (_host == null || _hwnd == IntPtr.Zero) { Rebuild(); return; }
        int ow = _winW, oh = _winH, ox = _winX, oy = _winY;
        // Quiesce the render thread before mutating _slots / geometry (it reads them every
        // frame). On the default path StopDriver is a no-op pause; InvokeOnRender runs inline.
        StopDriver();
        InvokeOnRender(() => { });   // barrier: ensure the render thread is idle (not mid-frame)
        _hover = -1; _pressIdx = -1; _dragging = false;
        LayoutContent();
        if (_winW != ow || _winH != oh || _winX != ox || _winY != oy)
        {
            // Geometry changed (an icon was added/removed). Resize the window + swap chain
            // IN PLACE instead of a full teardown, which makes the side dock vanish + pop
            // back. The DComp visual stays bound to the same swap chain, so only the back
            // buffer is reallocated.
            try
            {
                int pw = (int)Math.Ceiling(_winW * _dpi), ph = (int)Math.Ceiling(_winH * _dpi);
                int px = (int)Math.Round(_winX * _dpi), py = (int)Math.Round(_winY * _dpi);
                // Resize the swap chain and PAINT the new content BEFORE moving/resizing the
                // window (device work → render thread). A horizontal dock re-centres when an
                // icon is added/removed; if the window moved first, the OS would composite the
                // just-resized (blank, pre-Present) swap chain at the new position for a frame.
                InvokeOnRender(() =>
                {
                    _host!.Resize(pw, ph);
                    // Same rationale as MainDockWindowGpu.RelayoutInPlace: an in-place resize keeps
                    // the same D3D/D2D device alive, so icon bitmaps stay valid. Clearing them here
                    // needlessly forces a full icon re-upload after every resident-count / pin-list
                    // relayout, which amplifies the main-dock drag-out-delete jank because each
                    // delete also refreshes the side dock via AppsChanged.
                    DisposeRockResources();   // debris cache is window-sized; rebuilt lazily on render
                    Render();                  // paint the new content into the resized swap chain
                });
                SyncShim();   // reposition the shim + re-apply the window region for the new geometry
                // Now move + resize the window to match; SWP_NOZORDER avoids a re-raise flash.
                SetWindowPos(_hwnd, IntPtr.Zero, px, py, pw, ph, SWP_NOACTIVATE | SWP_NOZORDER);
                InvokeOnRender(Render);   // final crisp paint at the exact new size
                StartDriver();
                DragPerfStats.Event("side", 0, "relayout-resize",
                    ((Stopwatch.GetTimestamp() - t0All) * 1000.0 / Stopwatch.Frequency).ToString("0.000", CultureInfo.InvariantCulture) + "ms");
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("SideDockGpu", "in-place resize failed, rebuilding: " + ex.Message);
                Rebuild();
                return;
            }
        }
        InvokeOnRender(Render);
        StartDriver();
        DragPerfStats.Event("side", 0, "relayout-same-size",
            ((Stopwatch.GetTimestamp() - t0All) * 1000.0 / Stopwatch.Frequency).ToString("0.000", CultureInfo.InvariantCulture) + "ms");
    }

    private void Rebuild()
    {
        long t0 = Stopwatch.GetTimestamp();
        StopDriver();
        CloseSlotMenu();
        ClosePreview();
        EndDragGhost();
        if (_hwnd != IntPtr.Zero) s_instances.Remove(_hwnd);
        // Dispose all GPU resources ON the render thread (it owns the device) and WAIT, so the
        // host is gone before the UI thread destroys the HWND below — and the render thread is
        // guaranteed idle (not mid-frame) before we relayout _slots. Inline on the default path.
        // Keep _dropShim alive across the rebuild (re-owned in CreateHostWindow) so an external
        // drag started right after a drop never hits a no-drop-target gap.
        InvokeOnRender(DisposeHostResources);
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        _hover = -1; _pressIdx = -1; _dragging = false;
        try { Build(); }
        catch (Exception ex) { Log.Warn("SideDockGpu", "rebuild failed: " + ex); }
        DragPerfStats.Event("side", 0, "rebuild",
            ((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency).ToString("0.000", CultureInfo.InvariantCulture) + "ms");
    }

    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();

    private static (float x, float y) ToLocal(DockSide side, double main, double cross, int winW, int winH) => side switch
    {
        DockSide.Left => ((float)cross, (float)main),
        DockSide.Right => ((float)(winW - cross), (float)main),
        DockSide.Top => ((float)main, (float)cross),
        _ => ((float)main, (float)(winH - cross)),
    };

    // ---- Raw Win32 NOREDIRECTIONBITMAP window --------------------------------

    private static readonly Win32.WndProc s_wndProc = WndProcImpl;
    private static ushort s_atom;

    private static IntPtr WndProcImpl(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (s_instances.TryGetValue(h, out var inst) && inst.HandleMessage(m, w, l, out IntPtr r))
            return r;
        return DefWindowProcW(h, m, w, l);
    }

    // Interactive: NO WS_EX_TRANSPARENT — the window receives mouse input. WM_NCHITTEST
    // returns HTTRANSPARENT outside the glass slab so clicks on the empty reserved area
    // still pass through. WS_EX_NOACTIVATE keeps it from stealing focus on click.
    private static IntPtr CreateWindow(int w, int h) => Win32.CreateWindow(
        "PolarisSideDockGpu",
        WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        w, h, s_wndProc, ref s_atom, LoadCursorW(IntPtr.Zero, IDC_ARROW));
}
