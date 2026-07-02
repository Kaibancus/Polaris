using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
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

/// <summary>GPU main dock: the bottom-docked liquid-glass slab (via the shared
/// <see cref="GlassSlab"/>) and the 7-column pinned icon grid drawn in Direct2D under
/// DirectComposition (<c>LiquidGlassTheme.ComputeSlots</c> layout), plus the Saturn-ring
/// theme. A 16 ms cursor poll drives a continuous 2-D fisheye magnify wave (raised-cosine
/// falloff, focal anchoring + neighbour spread) and a floating hover name label below the
/// focal icon. Per-monitor DPI aware (layout in DIPs, window + swap chain in physical px,
/// D2D target DPI = 96 × scale).</summary>
internal sealed class MainDockWindowGpu : GpuDockBase, IMainDock, IDisposable
{
    // Mirror the WPF glass theme scale factors (see RadialWindow: _uiScale=1, _themeScale=0.9
    // for glass; glyphs drawn at icon*GlassIconScale).
    private const double ThemeScale = 0.9;
    // Extra whole-dock shrink applied ONLY to the liquid-glass theme (Saturn uses its own
    // BuildSaturnLayout and is untouched). Multiplied into the glass EffectiveIconSize, so every
    // icon-derived measure (icons, grid pitch, slab size, paddings, gear, clock, drag headroom)
    // scales with it; the one fixed pixel value that also defines the dock's size — the slab corner
    // radius — is scaled by it too, keeping the smaller dock's proportions intact. 0.9 == 10% smaller.
    private const double GlassSizeScale = 0.9;
    private const float GlassIconScale = 1.32f;
    // Corner radius of every per-icon glass frame (running-app sweep border, the resting 3D glass
    // rim and the hover water-lens), as a fraction of the icon box. Kept in one place so those three
    // frames stay in lockstep; raised for a rounder, softer tile look.
    private const float IconCornerRatio = 0.26f;
    // The per-icon glass frame (resting rim / hover water-lens / running-app sweep) is drawn a touch
    // larger than the icon box so the glyph's square corners tuck inside the frame's rounded corners
    // instead of poking out past them.
    private const float IconFrameScale = 1.08f;

    // Magnify wave constants — kept in lockstep with DockTuning so the GPU dock
    // feels identical to the WPF glass dock.
    private const float MagnifyPeak = (float)DockTuning.HoverScale;       // 1.7x under the cursor
    private const float SpreadPush = (float)DockTuning.SpreadPush;        // 0.75 * iconSize
    private const float SpreadInfluence = (float)DockTuning.SpreadInfluence; // 2.7 * iconSize

    private readonly AppConfig _config;
    private IntPtr _hwnd;
    private DropShimWindow? _dropShim;   // overlay that catches external drags + forwards them
    private IDragGhost? _ghost;           // independent desktop overlay for the dragged icon

    // Render-thread infrastructure (UseRenderThread, _loop, _host, _timer, _gcActive +
    // EnsureLoop/StartDriver/StopDriver/RunOnRender/InvokeOnRender/OnUi/RequestRender) lives in
    // the shared GpuDockBase. The dock's _slots/caches/animation state are owned and driven by
    // that render thread (or while it is quiesced during rebuild).
    private int[]? _orderBuf;            // reused draw-order scratch (avoids a per-frame alloc)
    private Comparison<int>? _orderCmp;  // cached so the sort doesn't alloc a closure each frame
    // Guards the interaction scalars written by the UI-thread WndProc and read by the
    // render thread inside Tick/Render (drag point, scroll target, launch + show/hide
    // animation phases). Layout/_slots/host/device are NOT under this lock — they are
    // mutated only on the render thread (or while it is quiesced during rebuild), so the
    // render thread is their single owner. Held briefly; never across WPF/Win32/Launch.
    private readonly object _stateLock = new();

    private int _winX, _winY, _winW, _winH;   // DIP
    private double _dpi = 1.0;

    // Slab geometry (window-local DIP) + the laid-out icon slots.
    private float _slabX, _slabY, _slabW, _slabH, _radius, _opacity, _frost;
    private float _gIcon;
    private float _effIcon;                    // EffectiveIconSize (DIP), wave support unit
    private float _iconRaw;                     // raw IconSize (DIP), spread unit
    private Vector2 _gearC;                     // settings-gear button centre (window-local DIP)
    private float _gearR;                        // settings-gear button radius
    private bool _gearHover;                     // cursor is over the gear
    private float _gearAngle;                    // gear glyph rotation (deg), spins while hovered
    private float _gearScale = 1f;               // gear press-scale (1.0 idle, ~0.8 pressed)
    private float _planetHitR;                   // Saturn: planet click radius (settings)
    private bool _pressGear;                     // mouse-down landed on the gear
    private readonly List<IconSlot> _slots = new();

    // ---- Stage B magnify wave state ----
    private IDWriteFactory? _dwrite;
    private IDWriteTextFormat? _labelFormat;
    private float _labelFontPx = 13f;
    private IDWriteTextFormat? _clockFormat;   // glass top-left date/time bar
    private int _lastClockMin = -1;            // last rendered minute (forces a clock repaint)
    private readonly Polaris.Services.WeatherService _weather = new();   // clock weather suffix
    private bool _weatherHooked;               // Updated event subscribed once
    private float[] _waveCur = Array.Empty<float>();   // smoothed per-icon scale
    private float[] _waveOffX = Array.Empty<float>();  // smoothed spread offset (DIP)
    private float[] _waveOffY = Array.Empty<float>();
    private int _hover = -1;
    private int _lastPreviewHover = -2;   // last hover marshaled to DrivePreview (render-thread path)
    // Hover window-thumbnail preview (parity with the WPF dock / GPU side dock): a
    // tiny transparent click-through anchor window is parked over the hovered icon and
    // used as WindowPreviewPopup's placement target. The popup's own dwell timers drive
    // show/hide as the dock reports hover changes.
    private System.Windows.Window? _anchorWin;
    private System.Windows.Controls.Border? _anchorEl;
    private WindowPreviewPopup? _preview;
    private Func<List<WindowPreview>>? _previewSource;
    private int _prevHover = -1;
    // Hover-intent: while a preview is open, defer switching it to a DIFFERENT icon so the
    // cursor can be steered onto the floating preview (which in the Saturn ring layout sits
    // over neighbouring icons) without a transient icon-crossing closing it. The switch
    // commits only on a deliberate dwell on the new icon.
    private DispatcherTimer? _previewSwitchTimer;
    private int _pendingPreviewHover = -2;
    private const double PreviewSwitchGraceMs = 300;
    private float _runSweep;              // running-icon sweep gradient angle (deg)
    private float _runPulse = 0.5f;       // running-icon glow pulse (0.35..0.8)
    private bool _anyRunning;             // a glass running icon is present (drives sweep render)
    // New-message attention badges: keys (EffectiveIconSource) of running icons whose
    // windows are flashing for attention, polled off-thread; a small red dot pulses on
    // each such icon's top-right corner (parity with RadialIcon.SetAttention).
    private volatile System.Collections.Generic.HashSet<string> _flashKeys = new();
    private float _badgePulse = 1f;       // attention dot pulse scale (1.0..1.18, 1.4s)
    private long _attnLast;               // last attention poll tick (throttle to ~800ms)
    private volatile bool _attnBusy;      // an attention poll task is in flight
    private float _orbitAngle;            // glass orbit-light angle (deg, 36s/rev clockwise)
    private long _animLastMs;             // last frame timestamp for dt-based (refresh-rate independent) animation

    // ---- Stage D interaction state ----
    private int _pressIdx = -1;          // slot under the mouse-down, or -1
    private bool _dragging;              // press has crossed the drag threshold
    private float _pressX, _pressY;      // mouse-down point (window-local DIP)
    private float _dragX, _dragY;        // current drag point (window-local DIP)
    private long _dragArrangeLastMs;     // last drag-reflow advance (time-based ease)
    private int _dragDiagSession;        // DragPerfStats session id for the current drag gesture
    private double _dragArrangeDiagMs;   // AdvanceDragArrange duration recorded for the next presented frame
    // Launch animation: the clicked icon first eases back to its REST size (a visible
    // de-magnify), THEN swells past rest (an "enlarge" pop) before the dock fades — the
    // original main-dock launch order ("restore to normal size, then enlarge, then close").
    private int _pressLaunchIdx = -1;    // slot playing the launch animation, or -1
    private long _pressLaunchStart;
    private float _pressFromScale = 1f;  // the icon's magnify scale captured at click
    private bool _launching;             // a launch animation is in flight (suppress hover-magnify)
    private float _pressShrink;          // 0..1 press feedback: how far the held icon's GLYPH is shrunk to rest (lens frame stays)
    private const long PressLaunchMs = 320;
    private DispatcherTimer? _persistTimer;   // debounce config writes so repeated drags don't block the UI thread
    private DispatcherTimer? _appsChangedTimer;   // debounce host side-dock refresh notifications across rapid drags
    private bool _appsChangedPending;        // side dock needs one refresh once this main-dock session ends
    private System.Windows.Controls.Primitives.Popup? _slotMenu;
    private int _menuIdx = -1;   // slot the right-click menu is anchored to (-1 = none)
    // Hover-name label fade: eased opacity + the retained anchor slot so the focal name
    // fades in/out in place (~110ms) like WPF Show/HideGlassHoverLabel instead of hard-cutting.
    private float _labelOp;
    private int _labelIdx = -1;
    private long _labelLastMs;

    // ---- Glass grid scrolling state (liquid-glass theme, >VisibleRows rows) ----
    private double _glassScroll;          // committed vertical scroll offset (DIP, >=0 scrolls content up)
    private double _glassScrollTarget;    // wheel/bar target the offset eases toward
    private double _glassScrollMax;       // max offset = overflow-row height (0 = everything fits)
    private double _glassCellH;           // grid row pitch (raw IconSize * RowPitch), matches ComputeSlots
    private float _gridCenterY;           // window-local DIP centre of the visible row block
    private float _gvX, _gvY, _gvW, _gvH; // grid clip viewport (window-local DIP)
    private bool _scrollable;             // _glassScrollMax > 0.5
    private float _barX, _barTop, _barW, _barTrackH; // scrollbar track (window-local DIP)
    private bool _barDrag;                // a scrollbar-thumb drag is in progress
    private float _barGrabDy;             // pointer offset within the thumb at grab time
    private const uint WM_MOUSEWHEEL = 0x020A;

    // ---- Resident-region frame (glass theme): rounded box around the pinned rows --
    private bool _hasResident;            // a resident frame should be drawn
    private float _resX, _resY, _resW, _resH, _resR;   // frame rect (grid space, window-local DIP)

    // ---- Saturn theme state ----
    private bool _saturn;                 // active theme is the Saturn ring system
    private SaturnScene.Geom _sg;         // Saturn ring/planet geometry (window-local DIP)
    private float[] _slotG = Array.Empty<float>();   // per-slot icon draw size (Saturn rings differ)
    private double _spinAngle, _innerAngle, _outerAngle, _saturnTime;   // Saturn animation phases
    private long _saturnLastMs;
    private double _spinRate = 1.0;          // Saturn: planet spin multiplier (eased toward hover target)
    private const double PlanetSpinSeconds = 60.0;
    private const double PlanetHoverSpinMul = 20.0;   // hover over the planet -> ~3s period (WPF StartSpin(3.0))
    private const double InnerOrbitRatio = 0.9023;
    private const double OuterOrbitRatio = 1.3941;
    // Baked Saturn layers: full-window static rings + backing; the planet body/disc/shade are
    // baked to a planet-centred SUB-REGION (see _satPlanetOx/Oy) — they only span ~1.4*PlanetR,
    // so a tight bitmap saves ~3 full-window RGBA bitmaps of VRAM/commit on the near-fullscreen
    // Saturn window. The spinning polar disc is rotated each frame; the static planet shading on top.
    private ID2D1Bitmap1? _satStaticInner, _satStaticOuter, _satDisc, _satShade, _satInner, _satOuter;
    private ID2D1Bitmap1? _satBacking, _satPlanet;
    private float _satPlanetOx, _satPlanetOy;   // DIP top-left of the planet sub-region bitmaps
    private float _satRingOx, _satRingOy;        // DIP top-left of the ring/backing/cue sub-region bitmaps
    private const float SaturnEnlarge = 1.10f;
    private const float SaturnDiskEnlarge = 1.3f;
    private const float SaturnInnerIconScale = 0.85f / SaturnEnlarge;
    private const float SaturnOuterIconScale = 1.0f;
    private const float RingTiltY = 0.97f;
    private const int Ring0Cap = 14;
    private const int Ring1Cap = 28;
    // Staggered rings-expand summon (mirrors WPF AnimateRingsExpand): the inner
    // band leads and the outer band follows ~0.28 of the summon later, each growing
    // 0.55->1.0 from the planet with a fade-in. Set in Render, read by the icon loop.
    private float _satInnerScl = 1f, _satOuterScl = 1f, _satInnerOp = 1f, _satOuterOp = 1f;
    private int _satR0;

    // ---- Stage E host integration (IMainDock) state ----
    private bool _realized;              // window created once (kept hidden between summons)
    private bool _shown;                 // logical shown state (IMainDock.IsShown)
    private bool _pinned;                // stays open until explicitly dismissed
    private bool _visible;               // window is currently not SW_HIDE
    private float _summon;               // 0 = fully dismissed (off-screen), 1 = fully docked
    private int _summonDir;              // +1 rising, -1 dropping, 0 settled
    private long _summonLast;            // last summon-tick timestamp (ms)
    private float _riseUnit;             // slide distance (DIP) that clears the slab off-screen
    private Action? _onFaded;            // deferred callback once a dismiss finishes
    private const float SummonInSec = 0.32f;
    private const float SummonOutSec = 0.20f;
    // Pointer travel (window-DIP, Euclidean) before a press becomes a drag. Matches
    // the WPF dock's 6 px Euclidean DragThreshold (RadialWindow.xaml.cs) so small
    // reorder/unpin gestures aren't misread as clicks (was icon*0.35 ~ 24 px).
    private const float DragThreshold = 6f;
    // Content fade (DComp visual opacity): summon eases the whole dock from 0->1 over
    // FadeInMs (CubicEaseOut); dismiss is a PURE fade-out 1->0 over FadeOutMs (no slide /
    // scale), mirroring the WPF RootGrid opacity animation in both themes.
    private float _fadeOpacity = 1f;     // current window opacity pushed to the compositor
    private int _fadeDir;                // +1 fading in, -1 fading out, 0 idle
    private long _fadeStart;             // fade start timestamp (ms)
    private float _fadeFrom = 1f;        // opacity the current fade started from
    private const float FadeInMs = 160f;
    private const float FadeOutMs = 170f;

    public event Action? RequestOpenSettings;
    public event Action? PanelDismissed;
    public Func<Point, AppEntry, bool>? DropToSideDock { get; set; }
    public Func<double>? BottomDockReserve { get; set; }
    public Action? AppsChanged { get; set; }
    public Action<bool>? GlassDragActiveChanged { get; set; }
    public Dispatcher Dispatcher { get; } = Dispatcher.CurrentDispatcher;
    public bool IsShown => _shown;

    protected override string RenderThreadName => "PolarisMainDockGpu";
    protected override Dispatcher UiDispatcher => Dispatcher;
    protected override float DragIconSize => _gIcon;
    protected override Vector2 ScreenToLocal(int screenX, int screenY) =>
        new((float)(screenX / _dpi - _winX), (float)(screenY / _dpi - _winY));

    private readonly struct IconSlot
    {
        public readonly Vector2 Center;       // window-local DIP
        public readonly string IconKey;
        public readonly string Name;
        public readonly bool Running;
        public readonly BitmapSource? Image;
        public readonly AppEntry? Entry;
        public IconSlot(Vector2 c, string key, string name, bool running, BitmapSource? img, AppEntry? entry)
        { Center = c; IconKey = key; Name = name; Running = running; Image = img; Entry = entry; }
    }

    public MainDockWindowGpu(AppConfig config) => _config = config;

    public void Show()
    {
        _shown = true; _summon = 1f; _visible = true;
        try { Build(true); }
        catch (Exception ex) { Log.Warn("MainDockGpu", "failed: " + ex); }
    }

    private void Build() => Build(true);

    private void Build(bool visible)
    {
        LayoutContent();
        CreateWindowAndHost(visible);
    }

    /// <summary>Recomputes slot positions, running state, grid/scroll geometry and the
    /// window rect from the current config — everything except creating the OS window and
    /// GPU host. Reused by the initial build and by in-place reorder relayouts.</summary>
    private void LayoutContent()
    {
        _slots.Clear();
        // Reset scroll/resident state; the glass branch recomputes, Saturn leaves off.
        _scrollable = false; _glassScrollMax = 0; _barTrackH = 0; _hasResident = false; _anyRunning = false;
        var mon = MonitorLayout.ActiveBounds;
        var wa = MonitorLayout.ActiveWorkArea;
        double sw = mon.Width, sh = mon.Height;

        _saturn = ThemeRegistry.Get(_config.Settings.Theme).IsSaturn;
        if (_saturn)
        {
            BuildSaturnLayout(mon, sw, sh);
        }
        else
        {
        double icon = _config.Settings.IconSize * ThemeScale * GlassSizeScale;   // EffectiveIconSize (glass, incl. whole-dock GlassSizeScale)
        double gIcon = icon * GlassIconScale;
        double cellW = icon * LiquidGlassTheme.ColumnPitch;
        double cellH = icon * LiquidGlassTheme.RowPitch;
        double gridW = (LiquidGlassTheme.Columns - 1) * cellW;
        double padX = icon * 1.15;
        double dockW = gridW + icon + padX * 2;

        // Bottom-docked margin: slab bottom rests above the system taskbar, and
        // lifts further when the side dock reserves space at the bottom edge. A small
        // extra gap (icon * 0.2) lifts the glass dock slightly clear of the edge.
        double taskbarH = Math.Max(0.0, mon.Bottom - wa.Bottom);
        double bottomMargin = Math.Max(taskbarH + icon * 0.12, BottomDockReserve?.Invoke() ?? 0.0) + icon * 0.1;

        // Dock heights (mirror RadialWindow glass geometry).
        double padY = icon * 0.95;
        double gridHVis = (LiquidGlassTheme.VisibleRows - 1) * cellH;
        double bodyHeight = gridHVis + icon + padY * 2 + icon * LiquidGlassTheme.ResidentGap;
        double bottomReserve = icon * 0.22;
        double topInset = icon * 0.55;
        double totalHeight = bodyHeight + topInset + bottomReserve;

        // Window size (mirror SizeToActiveContent's glass branch): content + headroom.
        double shadowPad = 72.0;
        double scrollPad = icon * 1.6;
        double hoverHeadroom = icon * 2.4;
        // Side headroom kept around the slab so (a) an in-window drag of a TEXT icon (no
        // bitmap, which keeps the in-window draw) has room to roam, and (b) — more
        // importantly — the outermost icon column's grab region (WM_NCHITTEST extends the
        // hit box to _slabX ± _gIcon*0.5) stays INSIDE the window. If this surplus is
        // narrower than that 0.5*gIcon grab margin, the window's physical edge clips the
        // outer icons' hit box and a press there falls through to the desktop, so dragging
        // the outermost icons left/right out of the dock gets blocked. gIcon = icon*1.32,
        // so 0.5*gIcon = icon*0.66; ThemeScale=0.9, so this factor must be >= 0.66/0.9 ≈
        // 0.74. Use 0.9 (icon*0.81 > icon*0.66) for a safety margin while still trimming
        // far below the original 1.8. The icon-pop / hover-label headroom is untouched.
        double glassDragHeadroom = _config.Settings.IconSize * ThemeScale * GlassSizeScale * 0.9;
        double w = Math.Min(dockW + shadowPad * 2 + scrollPad + glassDragHeadroom * 2, sw);
        double h = Math.Min(totalHeight + bottomMargin + hoverHeadroom + shadowPad + glassDragHeadroom, sh);

        _winW = (int)Math.Ceiling(w);
        _winH = (int)Math.Ceiling(h);
        _winX = (int)(mon.Left + (sw - w) / 2.0);
        _winY = (int)(mon.Bottom - h);

        // Layout centre (window-local) and the bottom-docked vertical anchor.
        double centerX = w / 2.0;
        double slabBottom = h - bottomMargin;
        double slabTopGeom = slabBottom - totalHeight;
        double bodyTop = slabTopGeom + topInset;
        double dockCenterY = bodyTop + bodyHeight / 2.0;

        // Slab rect (mirror DrawGlassPanel).
        double slabLeft = centerX - dockW / 2.0;
        double gridTop = dockCenterY - bodyHeight / 2.0;
        double slabTop = gridTop - topInset;
        double slabBottomExtend = gIcon * 0.1 + 2.0;
        double slabTotalH = bodyHeight + topInset + bottomReserve + slabBottomExtend;

        _slabX = (float)slabLeft;
        _slabY = (float)slabTop;
        _slabW = (float)dockW;
        _slabH = (float)slabTotalH;
        _radius = (float)(28.0 * GlassSizeScale);
        _gIcon = (float)gIcon;
        _effIcon = (float)icon;
        _iconRaw = (float)_config.Settings.IconSize;
        _opacity = (float)(1.0 - Math.Clamp(_config.Settings.PanelTransparency, 0.0, 1.0));
        _frost = (float)GlassChrome.FrostStrengthFor(_config.Settings.PanelTransparency);

        // Summon slide distance: push the slab (plus its shadow headroom) fully past
        // the bottom of the window so a dismissed dock is entirely off-screen.
        _riseUnit = (_winH - _slabY) + _effIcon;

        // Settings gear button: a frosted disc tucked into the slab's top-right
        // corner (mirrors RadialWindow.Glass gear: gs = max(40, icon*0.72), inset
        // 14px from the right edge and 6px from the top).
        float gs = MathF.Max(40f, _effIcon * 0.72f);
        _gearR = gs / 2f;
        _gearC = new Vector2(_slabX + _slabW - _gearR - 14f, _slabY + 6f + _gearR);
        // Pinned-icon grid positions (window-local DIP) via the theme layout. Match
        // the WPF dock exactly: lay out on the resolution-scaled icon size (so the
        // grid spacing tracks EffectiveIconSize, not the raw slider value) and tell
        // the layout how many apps form the resident block (Ring0Count = ResidentCount)
        // so the overflow rows start on a fresh row beneath the framed region.
        var apps = _config.Apps;
        int residentN = DockSync.ResidentCount(_config);
        var scaledSettings = new AppSettings { IconSize = icon, Ring0Count = residentN };
        int count = Math.Min(apps.Count, LiquidGlassTheme.Capacity);
        var slots = ((LiquidGlassTheme)ThemeRegistry.Get("liquidglass"))
            .ComputeSlots(apps.Count, new Point(centerX, dockCenterY), scaledSettings, out _);
        var running = RunningAppTracker.SnapshotRunning();
        var (explorerTitles, runningAumids) = SnapshotRunningExtras();
        for (int i = 0; i < count && i < slots.Count; i++)
        {
            var entry = apps[i];
            var img = IconExtractor.GetCached(entry.EffectiveIconSource, _iconCache);
            bool run = RunningAppTracker.IsEntryRunning(entry, running, explorerTitles, runningAumids);
            if (run) _anyRunning = true;
            _slots.Add(new IconSlot(new Vector2((float)slots[i].X, (float)slots[i].Y),
                entry.EffectiveIconSource, entry.Name, run, img, entry));
        }

        // ---- Grid scrolling geometry (rows beyond VisibleRows scroll vertically) --
        // The grid is laid out on the scaled icon pitch (see above), so the scroll
        // step and viewport use the same scaled units.
        _gridCenterY = (float)dockCenterY;
        _glassCellH = icon * LiquidGlassTheme.RowPitch;
        int resCount = Math.Min(residentN, count);
        int resRows = resCount > 0 ? (resCount + LiquidGlassTheme.Columns - 1) / LiquidGlassTheme.Columns : 0;
        int restRows = (Math.Max(0, count - resCount) + LiquidGlassTheme.Columns - 1) / LiquidGlassTheme.Columns;
        int totalRows = Math.Max(1, resRows + restRows);
        _glassScrollMax = Math.Max(0.0, totalRows - LiquidGlassTheme.VisibleRows) * _glassCellH;
        _scrollable = _glassScrollMax > 0.5;
        _glassScroll = Math.Clamp(_glassScroll, 0, _glassScrollMax);
        _glassScrollTarget = Math.Clamp(_glassScrollTarget, 0, _glassScrollMax);

        // Clip the grid to the visible block, with a margin under the row pitch so
        // the adjacent scrolled-out rows stay hidden.
        double visBlockH = (LiquidGlassTheme.VisibleRows - 1) * _glassCellH;
        double vMargin = icon * 1.4;
        double hMargin = icon * 1.9;
        _gvX = (float)(_slabX - hMargin);
        _gvW = (float)(_slabW + hMargin * 2.0);
        _gvY = (float)(dockCenterY - visBlockH / 2.0 - vMargin);
        _gvH = (float)(visBlockH + vMargin * 2.0);

        // Scrollbar track: a slim rounded rail just inside the slab's right edge,
        // spanning the visible block (only drawn / interactive when scrollable).
        if (_scrollable)
        {
            _barW = (float)(icon * 0.12);
            _barX = _slabX + _slabW - _barW - (float)(icon * 0.30);
            _barTop = (float)(dockCenterY - visBlockH / 2.0 - icon * 0.3);
            _barTrackH = (float)(visBlockH + icon * 0.6);
        }

        // ---- Resident-region frame: a rounded box around the pinned (resident)
        // rows, mirroring RadialWindow.DrawResidentRegionBorder. It lives in grid
        // space (scrolls with the grid) and reads as an etched inner frame.
        _hasResident = count > 0 && residentN > 0;
        if (_hasResident)
        {
            double resCellW = icon * LiquidGlassTheme.ColumnPitch;
            double resGridW = (LiquidGlassTheme.Columns - 1) * resCellW;
            double y0 = dockCenterY - (LiquidGlassTheme.VisibleRows - 1) * _glassCellH / 2.0; // row 0 centre
            double glyph = gIcon;                          // icon glyph size (icon * GlassIconScale)
            int resClamp = Math.Min(residentN, count);
            int residentRows = Math.Clamp((resClamp + LiquidGlassTheme.Columns - 1) / LiquidGlassTheme.Columns, 1, 2);
            double lastRowY = y0 + (residentRows - 1) * _glassCellH;
            double resPadY = icon * 0.56, resPadX = icon * 0.82;
            double rTop = y0 - glyph / 2.0 - resPadY;
            double rBottom = lastRowY + glyph / 2.0 + resPadY;
            double half = resGridW / 2.0 + glyph / 2.0 + resPadX;
            _resX = (float)(centerX - half);
            _resW = (float)(half * 2.0);
            _resY = (float)rTop;
            _resH = (float)(rBottom - rTop);
            _resR = (float)(icon * 0.42);
        }

        }

        // Magnify wave state starts at rest (scale 1, no spread).
        _waveCur = new float[_slots.Count];
        _waveOffX = new float[_slots.Count];
        _waveOffY = new float[_slots.Count];
        Array.Fill(_waveCur, 1f);
    }

    /// <summary>Creates the layered OS window and the DirectComposition/D2D host, builds the
    /// text formats and Saturn cache, then starts the render timer. Split from
    /// <see cref="LayoutContent"/> so a reorder can relayout without recreating the host
    /// (recreating it would blank/flash the screen for a frame).</summary>
    private void CreateWindowAndHost(bool visible)
    {
        _hwnd = CreateWindow(_winW, _winH);
        s_instances[_hwnd] = this;
        _dpi = MonitorLayout.PrimaryDpiScale;
        int pw = (int)Math.Ceiling(_winW * _dpi), ph = (int)Math.Ceiling(_winH * _dpi);
        int px = (int)Math.Round(_winX * _dpi), py = (int)Math.Round(_winY * _dpi);
        SetWindowPos(_hwnd, HWND_TOPMOST, px, py, pw, ph, SWP_NOACTIVATE);
        if (visible) ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        // Composition-only windows (WS_EX_NOREDIRECTIONBITMAP) can't be OLE drop targets
        // nor receive WM_DROPFILES, so external drags (Explorer / desktop) are caught by a
        // near-invisible overlay above the dock that hosts the OLE drop target and forwards
        // the initial mouse press + drops back to us (see DropShimWindow). OLE gives the
        // live DragOver feedback used to draw the drag-follow preview. The shim is kept ALIVE
        // across dock rebuilds (only its owner HWND is re-pointed) so a drop's rebuild never
        // leaves a window where a fresh external drag finds no drop target.
        if (_dropShim == null)
            _dropShim = new DropShimWindow(_hwnd, ForwardShimInput, HandleOleDrop, OnExternalDragMove);
        else
            _dropShim.SetOwner(_hwnd);
        _visible = visible;

        // The GPU device + its Direct2D/DirectComposition resources have hard thread
        // affinity, so on the render-thread path they are created and driven ONLY on the
        // render thread; the UI thread never touches the host. Invoke runs the creation
        // there and waits, so the shim re-top + first SetIntro below see a live host.
        if (UseRenderThread)
        {
            EnsureLoop();
            _loop!.Invoke(() => CreateHostResources(visible));
        }
        else
        {
            CreateHostResources(visible);
        }

        // Top the shim AFTER creating the CompositionHost: building the DComp swap chain /
        // visual re-raises the dock window above the shim, so raising the shim last keeps it
        // on top for every rebuild path (not just summon), which is required for an external
        // drag to land on the shim's drop target rather than the bare composition dock.
        if (visible && _dropShim != null) { SyncShim(); _dropShim.Show(); }

        if (UseRenderThread)
            _loop!.SetActive(visible);
    }

    /// <summary>Creates the GPU host + Direct2D/DirectWrite resources and renders the first
    /// frame. On the render-thread path this runs ON the render thread (posted from
    /// <see cref="CreateWindowAndHost"/>); on the default path it runs inline on the UI
    /// thread and also starts the <see cref="FrameClock"/>. Window creation and the drop
    /// shim stay on the UI thread either way.</summary>
    private void CreateHostResources(bool visible)
    {
        int pw = (int)Math.Ceiling(_winW * _dpi), ph = (int)Math.Ceiling(_winH * _dpi);
        _host = new CompositionHost(_hwnd, pw, ph, (float)(96.0 * _dpi), waitable: UseRenderThread);
        _animLastMs = 0;

        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
        // Match WPF ShowGlassHoverLabel: a fixed 11.5pt label that lived inside the
        // icon visual tree and read as ~11.5*HoverScale once the icon zoomed.
        _labelFontPx = (float)(11.5 * DockTuning.HoverScale * FontScale.Current);
        _labelFormat = _dwrite.CreateTextFormat("Microsoft YaHei UI", null, Vortice.DirectWrite.FontWeight.SemiBold,
            FontStyle.Normal, Vortice.DirectWrite.FontStretch.Normal, _labelFontPx, "zh-cn");
        _labelFormat.TextAlignment = Vortice.DirectWrite.TextAlignment.Center;
        _labelFormat.ParagraphAlignment = ParagraphAlignment.Center;
        _labelMeasureName = null;   // font size changed → re-measure widths against the new format
        float clockSize = (float)(Math.Max(18.0, _effIcon * 0.36) * FontScale.Current);
        _clockFormat = _dwrite.CreateTextFormat("Segoe UI Semibold", null, Vortice.DirectWrite.FontWeight.SemiBold,
            FontStyle.Normal, Vortice.DirectWrite.FontStretch.Normal, clockSize, "zh-cn");
        _clockFormat.TextAlignment = Vortice.DirectWrite.TextAlignment.Leading;
        _clockFormat.ParagraphAlignment = ParagraphAlignment.Near;

        if (_saturn) { _saturnLastMs = 0; BuildSaturnCache(); }

        Render();

        if (!UseRenderThread)
        {
            _timer = new Polaris.Services.Gpu.FrameClock();
            _timer.Tick += Tick;
            if (visible) _timer.Start();
        }
    }

    private void NotifyAppsChangedSoon()
    {
        if (AppsChanged == null)
            return;
        _appsChangedPending = true;
        // Coalesce rapid reorders and refresh the side dock once the user has settled — even
        // while the main dock is still shown, so a main-dock reorder propagates to the side dock
        // live instead of only on dismiss. The refresh costs ~70-80ms on the UI thread, so it
        // must never land during an active drag: the debounce restarts on every drop and the
        // tick re-arms while a drag is still in progress, so it fires only after the last drop
        // with the mouse released. FlushAppsChanged on dismiss still applies any pending refresh.
        if (_appsChangedTimer == null)
        {
            _appsChangedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _appsChangedTimer.Tick += (_, _) =>
            {
                if (_dragging) { _appsChangedTimer!.Stop(); _appsChangedTimer!.Start(); return; }
                _appsChangedTimer!.Stop();
                if (!_appsChangedPending)
                    return;
                _appsChangedPending = false;
                try { AppsChanged?.Invoke(); } catch { }
            };
        }
        _appsChangedTimer.Stop();
        _appsChangedTimer.Start();
    }

    private void FlushAppsChanged()
    {
        if (_appsChangedTimer is { IsEnabled: true })
            _appsChangedTimer.Stop();
        if (_appsChangedPending)
        {
            _appsChangedPending = false;
            try { AppsChanged?.Invoke(); } catch { }
        }
    }

    /// <summary>Disposes all render-thread-owned GPU resources (host, text formats, Saturn
    /// + icon bitmap caches). MUST run on the render thread (it owns the device); the caller
    /// quiesces the loop and Invokes this before the UI thread destroys the HWND.</summary>
    private void DisposeHostResources()
    {
        DisposeSaturnCache();
        _runHaloBrush?.Dispose(); _runHaloBrush = null;
        _runSweepBrush?.Dispose(); _runSweepBrush = null;
        _runSweepStops?.Dispose(); _runSweepStops = null;
        _iconRimBrush?.Dispose(); _iconRimBrush = null;
        _iconRimStops?.Dispose(); _iconRimStops = null;
        _iconShadeBrush?.Dispose(); _iconShadeBrush = null;
        _iconShadeStops?.Dispose(); _iconShadeStops = null;
        _host?.Dispose(); _host = null;
        _labelFormat?.Dispose(); _labelFormat = null;
        _clockFormat?.Dispose(); _clockFormat = null;
        _dwrite?.Dispose(); _dwrite = null;
        foreach (var b in _bmpCache.Values) b?.Dispose();
        _bmpCache.Clear();
    }

    // Running-indicator brushes cached for the HOST's lifetime (nulled + recreated by
    // DisposeHostResources, exactly like the icon _bmpCache). The running halo + flowing sweep
    // are drawn for EVERY running icon EVERY frame; building fresh D2D brushes + a gradient-stop
    // collection per icon per frame churned finalizable COM wrappers that pressured gen2 GC during
    // long drag sessions (see RenderGcScope). The sweep's colours are constant — only its gradient
    // AXIS rotates — so its stop collection + brush are built once and only the brush's start/end
    // points are updated per draw; the halo brush's colour (alpha pulses) is set per draw.
    private ID2D1SolidColorBrush? _runHaloBrush;
    private ID2D1GradientStopCollection? _runSweepStops;
    private ID2D1LinearGradientBrush? _runSweepBrush;
    // Resting liquid-glass icon edge (glass theme): a cool rounded-rect hairline drawn on EVERY
    // icon every frame so each icon always carries a glass border, not only while hovered. Its
    // colours are constant — only the gradient axis + brush opacity change per icon — so the stop
    // collection + brush are cached for the host's lifetime (like the running-sweep brush) to keep
    // this allocation-free even with a full grid.
    private ID2D1GradientStopCollection? _iconRimStops;
    private ID2D1LinearGradientBrush? _iconRimBrush;
    // Bevel shade stroke for the resting 3D glass rim; same caching rationale as the rim above.
    private ID2D1GradientStopCollection? _iconShadeStops;
    private ID2D1LinearGradientBrush? _iconShadeBrush;

    private static Color4 Col(byte a, byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, a / 255f);

    /// <summary>Open Explorer window titles + running packaged-app AUMIDs — both REQUIRED
    /// by RunningAppTracker.IsEntryRunning to light shell-hosted launchers (File Explorer,
    /// This PC…) and UWP/Store apps. Mirrors the WPF dock's RefreshRunning so the GPU dock's
    /// green running dots never miss File Explorer or packaged apps.</summary>
    private static (List<string> titles, HashSet<string> aumids) SnapshotRunningExtras()
    {
        List<string> titles;
        try { titles = WindowPreviewService.GetExplorerWindowTitles(); }
        catch { titles = new List<string>(); }
        HashSet<string> aumids;
        try { aumids = WindowPreviewService.SnapshotRunningAumids(); }
        catch { aumids = new HashSet<string>(); }
        return (titles, aumids);
    }

    /// <summary>Saturn theme layout: a screen-centred overlay sized to the ring disc,
    /// two concentric icon rings (inner B ring + outer F ringlet) and the Direct2D
    /// ring/planet geometry. Mirrors RadialWindow's Saturn sizing + ComputeLayout.</summary>
    private void BuildSaturnLayout(System.Windows.Rect mon, double sw, double sh)
    {
        float uiScale = (float)Math.Clamp(sh / 1080.0, 1.0, 2.0);
        float innerRadius = 140f * uiScale * SaturnEnlarge * SaturnDiskEnlarge;
        float ringStep = 88f * uiScale * SaturnEnlarge * SaturnDiskEnlarge;
        float icon = (float)(_config.Settings.IconSize * uiScale * SaturnEnlarge);  // EffectiveIconSize
        float outerIcon = icon * SaturnOuterIconScale;
        float planetR = 56f * uiScale * SaturnEnlarge * SaturnDiskEnlarge * 2.5f / 2f;

        // Window size (mirror SizeToActiveContent's Saturn branch): centred square.
        double discR = innerRadius + ringStep + outerIcon;
        double reach = discR + outerIcon;
        double dragHeadroom = _config.Settings.IconSize * SaturnEnlarge * 5.0;
        double margin = 180.0 * uiScale;
        double halfExt = reach + dragHeadroom;
        double w = Math.Min((halfExt + margin) * 2.0, sw);
        double h = Math.Min((halfExt + margin) * 2.0, sh);

        _winW = (int)Math.Ceiling(w);
        _winH = (int)Math.Ceiling(h);
        _winX = (int)(mon.Left + (sw - w) / 2.0);
        _winY = (int)(mon.Top + (sh - h) / 2.0);

        float cx = (float)(w / 2.0), cy = (float)(h / 2.0);

        _sg = new SaturnScene.Geom
        {
            Cx = cx,
            Cy = cy,
            InnerRadius = innerRadius,
            RingStep = ringStep,
            OuterRadius = innerRadius + ringStep,
            Icon = icon,
            OuterIcon = outerIcon,
            PlanetR = planetR,
            TiltY = RingTiltY,
            DiscOpacity = DockTuning.SaturnPanelOpacity(_config.Settings.PanelTransparency),
        };

        _gIcon = icon;             // base draw size; per-slot size in _slotG
        _effIcon = icon;
        _iconRaw = (float)_config.Settings.IconSize;
        _gearR = _effIcon * 0.30f; // bead radius for the top-right settings gear
        // The centre planet is the settings button on Saturn (mirrors the WPF
        // DrawCenterButton MouseLeftButtonUp -> RequestOpenSettings).
        _gearC = new Vector2(_sg.Cx, _sg.Cy);
        _planetHitR = planetR;
        _opacity = _sg.DiscOpacity;

        // Saturn pops in place for now (rings-expand summon lands in Stage 4).
        _riseUnit = 0f;

        // Hit-test / click-outside region: a square box around the ring system
        // (the glass branch uses _slabX/Y/W/H; reuse it so NCHITTEST + dismiss work).
        float hitReach = _sg.OuterRadius + _sg.OuterIcon;
        _slabX = (float)(_sg.Cx - hitReach);
        _slabY = (float)(_sg.Cy - hitReach);
        _slabW = hitReach * 2f;
        _slabH = hitReach * 2f;

        // --- Two-ring icon layout (mirror SlotPositionsFor / RingPoint) -------
        var apps = _config.Apps;
        int count = apps.Count;
        int r0 = EffectiveRing0Count(count);
        var running = RunningAppTracker.SnapshotRunning();
        var (explorerTitles, runningAumids) = SnapshotRunningExtras();
        int ring1 = Math.Max(0, count - r0);
        var sizes = new List<float>(count);
        for (int i = 0; i < count; i++)
        {
            bool inner = i < r0;
            float radius = inner ? innerRadius : innerRadius + ringStep;
            int k = inner ? i : i - r0;
            int n = inner ? r0 : ring1;
            double angle = -Math.PI / 2 + 2 * Math.PI * k / Math.Max(1, n);
            float sx = cx + (float)(radius * Math.Cos(angle));
            float sy = cy + (float)(radius * Math.Sin(angle) * RingTiltY);

            var entry = apps[i];
            var img = IconExtractor.GetCached(entry.EffectiveIconSource, _iconCache);
            bool run = RunningAppTracker.IsEntryRunning(entry, running, explorerTitles, runningAumids);
            if (run) _anyRunning = true;
            _slots.Add(new IconSlot(new Vector2(sx, sy),
                entry.EffectiveIconSource, entry.Name, run, img, entry));
            sizes.Add(icon * (inner ? SaturnInnerIconScale : SaturnOuterIconScale));
        }
        _slotG = sizes.ToArray();
    }

    /// <summary>Inner-ring (resident) icon count for <paramref name="n"/> total icons
    /// (port of RadialWindow.EffectiveRing0Count).</summary>
    private int EffectiveRing0Count(int n)
    {
        if (n <= 0) return 0;
        int userR0 = _config.Settings.Ring0Count;
        int r0 = userR0 > 0
            ? Math.Min(userR0, Math.Min(Ring0Cap, n))
            : Math.Min(Ring0Cap, n);
        if (n - r0 > Ring1Cap)
            r0 = Math.Min(n, n - Ring1Cap);
        return Math.Clamp(r0, 1, n);
    }

    /// <summary>Bakes the static Saturn layers into full-window target bitmaps so
    /// only the cheap spin (a single rotated DrawBitmap) and twinkle redraw each
    /// frame — mirroring the WPF BitmapCache layering that kept the spinning dock
    /// from re-tessellating hundreds of vector shapes every frame.</summary>
    private void BuildSaturnCache()
    {
        if (_host == null) return;
        DisposeSaturnCache();
        var ctx = _host.Context;
        float bdpi = (float)(96.0 * _dpi);
        // Inner and outer ring layers are baked separately so the summon can expand them on the
        // inner vs outer bands. Backing disc + starfield, the two static ring groups and the two
        // revolution-cue groups are all bounded by the ring radius (OuterRadius + OuterIcon;
        // RingTiltY≈1 so nearly circular, and the starfield sits within r·0.96), so — like the
        // planet — bake them to a ring-centred sub-region instead of the full (near-fullscreen)
        // window. 1.3x margin clears any ringlet bloom / shepherd moons. The cue groups set
        // ctx.Transform themselves via RevolveXform(baseXform), so their offset rides that
        // baseXform (Translation(-ring origin)); the others honour the helper's pre-set translation.
        float rmr = (_sg.OuterRadius + _sg.OuterIcon) * 1.3f;
        float rox = Math.Max(0f, _sg.Cx - rmr), roy = Math.Max(0f, _sg.Cy - rmr);
        float rx1 = Math.Min((float)_winW, _sg.Cx + rmr), ry1 = Math.Min((float)_winH, _sg.Cy + rmr);
        _satRingOx = rox; _satRingOy = roy;
        float rwid = Math.Max(1f, rx1 - rox), rhei = Math.Max(1f, ry1 - roy);
        var ringBase = System.Numerics.Matrix3x2.CreateTranslation(-rox, -roy);
        _satBacking = RenderToBitmapRegion(ctx, rox, roy, rwid, rhei, bdpi, c => SaturnScene.DrawBacking(c, _sg));
        _satStaticInner = RenderToBitmapRegion(ctx, rox, roy, rwid, rhei, bdpi, c => SaturnScene.DrawInnerRings(c, _sg));
        _satStaticOuter = RenderToBitmapRegion(ctx, rox, roy, rwid, rhei, bdpi, c => SaturnScene.DrawStaticOuter(c, _sg));
        // Planet body + spinning disc + shade only span ~1.4*PlanetR around the centre (the warm
        // glow halo). Bake them to a planet-centred sub-region (1.5x margin > the 1.4x halo)
        // instead of a full-window bitmap — on the near-fullscreen Saturn window this saves ~3
        // full-window RGBA bitmaps of VRAM/commit. The draw places each back via a translation,
        // and the disc still rotates about the absolute centre, so the spin is unchanged.
        float pmr = _sg.PlanetR * 1.5f;
        float pox = Math.Max(0f, _sg.Cx - pmr), poy = Math.Max(0f, _sg.Cy - pmr);
        float px1 = Math.Min((float)_winW, _sg.Cx + pmr), py1 = Math.Min((float)_winH, _sg.Cy + pmr);
        _satPlanetOx = pox; _satPlanetOy = poy;
        float pwid = Math.Max(1f, px1 - pox), phei = Math.Max(1f, py1 - poy);
        _satPlanet = RenderToBitmapRegion(ctx, pox, poy, pwid, phei, bdpi, c => SaturnScene.DrawPlanet(c, _sg));
        _satDisc = RenderToBitmapRegion(ctx, pox, poy, pwid, phei, bdpi, c => SaturnScene.DrawPlanetDisc(c, _sg));
        _satShade = RenderToBitmapRegion(ctx, pox, poy, pwid, phei, bdpi, c => SaturnScene.DrawPlanetShade(c, _sg));
        // Revolution cues are static apart from their orbit angle, so bake each
        // orbit group flat (no tilt, orbit=0) once and re-revolve the bitmap per
        // frame — same trick as the planet disc, keeps the cues near-free.
        var gFlat = _sg; gFlat.TiltY = 1f;
        // Pass ringBase (= Translation(-ring origin)) as the cue baseXform so the flat (orbit=0,
        // tilt=1) bake lands inside the ring sub-region — DrawInnerCues sets ctx.Transform itself
        // via RevolveXform(baseXform), so the helper's pre-set translation would be overwritten;
        // the offset rides the baseXform instead.
        _satInner = RenderToBitmapRegion(ctx, rox, roy, rwid, rhei, bdpi, c => { SaturnScene.DrawInnerCues(c, gFlat, 0, ringBase); c.Transform = System.Numerics.Matrix3x2.Identity; });
        _satOuter = RenderToBitmapRegion(ctx, rox, roy, rwid, rhei, bdpi, c => { SaturnScene.DrawOuterCues(c, gFlat, 0, ringBase); c.Transform = System.Numerics.Matrix3x2.Identity; });
    }

    private ID2D1Bitmap1 RenderToBitmap(ID2D1DeviceContext ctx, int pw, int ph, float dpi, Action<ID2D1DeviceContext> draw)
    {
        var props = new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            dpi, dpi, BitmapOptions.Target);
        var bmp = ctx.CreateBitmap(new Vortice.Mathematics.SizeI(pw, ph), IntPtr.Zero, 0, props);
        ctx.Target = bmp;
        ctx.BeginDraw();
        ctx.Clear(new Color4(0, 0, 0, 0));
        draw(ctx);
        ctx.EndDraw();
        _host!.SetDefaultTarget();
        return bmp;
    }

    /// <summary>Bakes a localized layer into a tight bitmap covering only the DIP rectangle
    /// (ox,oy,wDip,hDip) instead of the full window: the draw is translated by (-ox,-oy) so
    /// absolute-coordinate scene drawing lands inside the sub-bitmap. Callers place it back
    /// with a matching +(ox,oy) translation at draw time. Saves VRAM/commit for layers (the
    /// planet) that cover only a small fraction of the near-fullscreen Saturn window.</summary>
    private ID2D1Bitmap1 RenderToBitmapRegion(ID2D1DeviceContext ctx, float ox, float oy, float wDip, float hDip, float dpi, Action<ID2D1DeviceContext> draw)
    {
        int pw = Math.Max(1, (int)Math.Ceiling(wDip * _dpi));
        int ph = Math.Max(1, (int)Math.Ceiling(hDip * _dpi));
        var props = new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            dpi, dpi, BitmapOptions.Target);
        var bmp = ctx.CreateBitmap(new Vortice.Mathematics.SizeI(pw, ph), IntPtr.Zero, 0, props);
        ctx.Target = bmp;
        ctx.BeginDraw();
        ctx.Clear(new Color4(0, 0, 0, 0));
        ctx.Transform = System.Numerics.Matrix3x2.CreateTranslation(-ox, -oy);
        draw(ctx);
        ctx.Transform = System.Numerics.Matrix3x2.Identity;
        ctx.EndDraw();
        _host!.SetDefaultTarget();
        return bmp;
    }

    private void DisposeSaturnCache()
    {
        _satStaticInner?.Dispose(); _satStaticInner = null;
        _satStaticOuter?.Dispose(); _satStaticOuter = null;
        _satBacking?.Dispose(); _satBacking = null;
        _satPlanet?.Dispose(); _satPlanet = null;
        _satDisc?.Dispose(); _satDisc = null;
        _satShade?.Dispose(); _satShade = null;
        _satInner?.Dispose(); _satInner = null;
        _satOuter?.Dispose(); _satOuter = null;
    }


    /// <summary>Raised-cosine fisheye falloff: peaks at <see cref="MagnifyPeak"/> under the
    /// cursor and eases to 1.0 at the support radius (EffectiveIconSize × 1.3), mirroring
    /// <c>RadialWindow.MagnifyScaleAt</c>.</summary>
    private float WaveScaleAt(float dist)
    {
        float s = _effIcon * 1.3f;
        if (dist >= s)
            return 1f;
        float f = 0.5f * (1f + (float)Math.Cos(Math.PI * dist / s));
        return 1f + (MagnifyPeak - 1f) * f;
    }

    /// <summary>16 ms cursor poll: feeds the live pointer into the 2-D magnify wave,
    /// updates per-icon scale + neighbour-spread offsets, tracks the focal (hovered)
    /// icon, and re-renders while anything is in motion.</summary>
    protected override void Tick()
    {
        if (_host == null || _slots.Count == 0)
            return;
        long frameNow = Environment.TickCount64;
        float frameDt = _animLastMs == 0 ? 0.016f : Math.Clamp((frameNow - _animLastMs) / 1000f, 0f, 0.1f);
        _animLastMs = frameNow;

        // Keep the drop-shim reliably above the dock: various rebuild / resize / DComp
        // operations can momentarily raise the bare composition dock above the shim, and an
        // external drag started in that window misses the shim's drop target. Rather than get
        // the z-order exactly right in every path, poll (~150ms) and, ONLY when the dock is
        // actually covering the shim at the drop centre, re-raise the shim. This targets the
        // bug state precisely and never disturbs legitimately-higher windows (settings, menus).
        EnsureShimTopmost();

        // Advance the summon (rise / drop) slide.
        bool animating = _summonDir != 0;
        if (animating)
        {
            long nowMs = Environment.TickCount64;
            float dt = Math.Clamp((nowMs - _summonLast) / 1000f, 0f, 0.1f);
            _summonLast = nowMs;
            if (_summonDir > 0)
            {
                // Saturn's rings-expand reads as a sequenced inside-out bloom, so it
                // wants a touch more time than the glass slab's quick rise to make the
                // inner-then-outer stagger perceptible.
                float inSec = _saturn ? 0.42f : SummonInSec;
                _summon = Math.Min(1f, _summon + dt / inSec);
                if (_summon >= 1f) _summonDir = 0;
            }
            else
            {
                _summon = Math.Max(0f, _summon - dt / SummonOutSec);
                if (_summon <= 0f) { _summonDir = 0; OnDismissComplete(); return; }
            }
        }

        // Drive the content fade (DComp visual opacity). Summon eases in (CubicEaseOut);
        // dismiss is a pure fade-out and owns the SW_HIDE once fully transparent so the
        // dock disappears exactly like the WPF RootGrid fade (no slide on the way out).
        bool fading = _fadeDir != 0;
        if (fading)
        {
            long nowMs = Environment.TickCount64;
            if (_fadeDir > 0)
            {
                float t = Math.Clamp((nowMs - _fadeStart) / FadeInMs, 0f, 1f);
                _fadeOpacity = _fadeFrom + (1f - _fadeFrom) * CubicEaseOut(t);
                if (t >= 1f) { _fadeOpacity = 1f; _fadeDir = 0; }
                _host?.SetIntro(0f, 0f, _fadeOpacity);
            }
            else
            {
                float t = Math.Clamp((nowMs - _fadeStart) / FadeOutMs, 0f, 1f);
                _fadeOpacity = _fadeFrom * (1f - t);
                _host?.SetIntro(0f, 0f, _fadeOpacity);
                if (t >= 1f) { _fadeOpacity = 0f; _fadeDir = 0; OnDismissComplete(); return; }
            }
        }

        bool active = false;
        bool planetHover = false;
        Vector2 cur = default;
        float scrollY = _scrollable ? (float)_glassScroll : 0f;
        if (!_dragging && GetCursorPos(out POINT p))
        {
            cur = new Vector2((float)(p.X / _dpi - _winX), (float)(p.Y / _dpi - _winY));
            // Compare against grid-space slot centres (icons draw at Center.Y - scroll).
            Vector2 curGrid = new Vector2(cur.X, cur.Y + scrollY);
            bool inViewport = !_scrollable || (cur.Y >= _gvY && cur.Y <= _gvY + _gvH);
            float nearest = float.MaxValue;
            for (int i = 0; i < _slots.Count; i++)
                nearest = Math.Min(nearest, Vector2.Distance(_slots[i].Center, curGrid));
            active = inViewport && nearest <= _effIcon * 1.3f;
            if (_saturn)
                planetHover = Vector2.Distance(cur, _gearC) <= _planetHitR;
            else
                _gearHover = Vector2.Distance(cur, _gearC) <= _gearR + 2f;
            cur = curGrid;
        }
        else
            _gearHover = false;

        // While dismissing (the click-launch fade-out) or while a launch press is playing,
        // force hover-magnify off so the clicked icon shrinks back toward its own centre
        // instead of staying magnified under the cursor (parity with the WPF dock's
        // HidePanel → ResetMagnify on launch).
        if (!_shown || _launching) active = false;

        // Gear button: spin the glyph while hovered (1.7s/rev, WPF parity) and coast
        // to rest on leave; ease the press-scale toward 0.8 while held.
        if (!_saturn)
        {
            if (_gearHover)
                _gearAngle = (_gearAngle + frameDt * 360f / 1.7f) % 360f;
            float gearTarget = (_pressGear && !_dragging) ? 0.8f : 1f;
            _gearScale += (gearTarget - _gearScale) * 0.25f;
        }

        // Focal icon = nearest to the cursor; it stays anchored while neighbours
        // part around its FIXED slot (so it never slides out from under the pointer).
        int focal = -1; float best = float.MaxValue;
        if (active)
            for (int i = 0; i < _slots.Count; i++)
            {
                float d = Vector2.Distance(_slots[i].Center, cur);
                if (d < best) { best = d; focal = i; }
            }
        float focalF = 0f;
        Vector2 fp = default;
        if (focal >= 0)
        {
            focalF = (WaveScaleAt(best) - 1f) / (MagnifyPeak - 1f);
            fp = _slots[focal].Center;
        }

        float k = 1f - (float)Math.Exp(-frameDt / 0.030f);    // tau 30ms, refresh-rate independent
        float influence = _iconRaw * SpreadInfluence;
        float push = _iconRaw * SpreadPush;
        float maxDelta = 0f;

        // While dragging, neighbours reflow to make room. The arrangement + offset ease
        // are driven by AdvanceDragArrange (time-based, also called from WM_MOUSEMOVE so the
        // gap keeps pace with the cursor); the hover-spread below only runs when not dragging.
        for (int i = 0; i < _slots.Count; i++)
        {
            float d = active ? Vector2.Distance(_slots[i].Center, cur) : 0f;
            float target = active ? WaveScaleAt(d) : 1f;
            float c = _waveCur[i] + (target - _waveCur[i]) * k;
            _waveCur[i] = c;
            maxDelta = Math.Max(maxDelta, Math.Abs(target - c));

            if (_dragging)
                continue;   // offsets owned by AdvanceDragArrange while dragging

            float tx = 0f, ty = 0f;
            if (active && focal >= 0 && i != focal && focalF > 0.001f)
            {
                Vector2 v = _slots[i].Center - fp;
                float vd = v.Length();
                if (vd > 0.01f && vd < influence)
                {
                    float amt = push * (1f - vd / influence) * focalF;
                    tx = v.X / vd * amt; ty = v.Y / vd * amt;
                }
            }
            _waveOffX[i] += (tx - _waveOffX[i]) * k;
            _waveOffY[i] += (ty - _waveOffY[i]) * k;
            maxDelta = Math.Max(maxDelta, Math.Abs(tx - _waveOffX[i]));
            maxDelta = Math.Max(maxDelta, Math.Abs(ty - _waveOffY[i]));
        }
        if (_dragging)
        {
            long t0 = Stopwatch.GetTimestamp();
            maxDelta = Math.Max(maxDelta, AdvanceDragArrange());
            _dragArrangeDiagMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        }

        // Press feedback: while the button is held over an icon (not dragging, not yet
        // launching) ease its GLYPH down to rest — the lens frame is kept at hover size (see
        // DrawIcon) so only the icon shrinks. On release the launch animation grows it back to
        // the hover peak. Eased slowly (tau ~120ms) for a soft, deliberate push.
        bool pressing = _pressIdx >= 0 && !_dragging && !_launching;
        float shrinkTarget = pressing ? 1f : 0f;
        float sk = 1f - (float)Math.Exp(-frameDt / 0.090f);
        if (Math.Abs(shrinkTarget - _pressShrink) > 0.0005f)
        {
            _pressShrink += (shrinkTarget - _pressShrink) * sk;
            maxDelta = Math.Max(maxDelta, 0.02f);
        }
        else _pressShrink = shrinkTarget;

        // Launch press: override the clicked icon's scale with the button-press curve
        // (compress past rest, then spring back to 1.0) while the launch hold runs, so the
        // click reads as a tactile push. Other icons de-magnify (active was forced off
        // above); _launching clears in the hold timer as the press finishes at scale 1.
        if (_launching && _pressLaunchIdx >= 0 && _pressLaunchIdx < _waveCur.Length)
        {
            float pt = Math.Clamp((Environment.TickCount64 - _pressLaunchStart) / (float)PressLaunchMs, 0f, 1f);
            float ps = PressScale(pt);
            maxDelta = Math.Max(maxDelta, Math.Abs(ps - _waveCur[_pressLaunchIdx]));
            _waveCur[_pressLaunchIdx] = ps;
        }

        // Hover only when the pointer is genuinely over an icon's cell.
        _hover = active && focal >= 0 && best <= _effIcon * 0.85f ? focal : -1;
        // Ease the hover-label opacity toward 1 while an icon is hovered (not dragging /
        // dismissing) and toward 0 otherwise (~110ms), retaining the last slot so the name
        // fades out in place — parity with WPF Show/HideGlassHoverLabel's 110ms opacity fade.
        {
            if (_hover >= 0 && !_dragging) _labelIdx = _hover;
            long nowL = Environment.TickCount64;
            float dtL = Math.Clamp((nowL - _labelLastMs) / 1000f, 0f, 0.1f);
            _labelLastMs = nowL;
            float tgtL = (_hover >= 0 && !_dragging && _shown) ? 1f : 0f;
            float kL = 1f - MathF.Exp(-dtL / 0.05f);   // ~110 ms settle
            _labelOp += (tgtL - _labelOp) * kL;
            if (_labelOp < 0.01f && tgtL == 0f) { _labelOp = 0f; _labelIdx = -1; }
        }
        // Drive the hover thumbnail preview (suppressed while dragging or dismissing). The
        // preview is a WPF popup, so on the render-thread path marshal it to the UI thread,
        // and only when the target changes (DrivePreview is a no-op for an unchanged hover).
        int wantPreview = _dragging || !_shown ? -1 : _hover;
        if (UseRenderThread)
        {
            if (wantPreview != _lastPreviewHover)
            {
                _lastPreviewHover = wantPreview;
                Dispatcher.BeginInvoke(new Action(() => DrivePreview(wantPreview)));
            }
        }
        else
            DrivePreview(wantPreview);

        // Ease the grid scroll toward its wheel/scrollbar target (tau ~70ms) so the
        // whole grid glides rather than jumping a row at a time.
        bool scrolling = false;
        if (_scrollable && Math.Abs(_glassScrollTarget - _glassScroll) > 0.05)
        {
            _glassScroll += (_glassScrollTarget - _glassScroll) * (1.0 - Math.Exp(-frameDt / 0.07f));
            scrolling = true;
        }
        else if (_scrollable)
            _glassScroll = _glassScrollTarget;

        // Saturn ambient: advance the planet spin, ring revolutions and twinkle
        // clock and re-render every tick while shown (matches the WPF perpetual
        // idle spin/orbit). The scene is GPU-drawn so the per-frame cost is cheap.
        if (_saturn && _visible)
        {
            long now = Environment.TickCount64;
            double satDt = _saturnLastMs == 0 ? 0.016 : Math.Clamp((now - _saturnLastMs) / 1000.0, 0, 0.1);
            _saturnLastMs = now;
            _saturnTime += satDt;
            // Ease the spin multiplier toward the hover target (accelerate on planet
            // hover, coast back to ambient on leave) — tau ~0.4s for a smooth ramp.
            double spinTarget = planetHover ? PlanetHoverSpinMul : 1.0;
            _spinRate += (spinTarget - _spinRate) * (1.0 - Math.Exp(-satDt / 0.4));
            _spinAngle = (_spinAngle + _spinRate * satDt * 360.0 / PlanetSpinSeconds) % 360.0;
            _innerAngle = (_innerAngle + satDt * 360.0 / (PlanetSpinSeconds * InnerOrbitRatio)) % 360.0;
            _outerAngle = (_outerAngle + satDt * 360.0 / (PlanetSpinSeconds * OuterOrbitRatio)) % 360.0;
        }

        // Running-icon sweep + glow pulse: rotate the gradient (7.2s/rev) and breathe the
        // halo (2.2s) so running apps show a flowing border in BOTH themes (the WPF
        // RadialIcon RunningBorder is not theme-gated).
        if (_anyRunning)
        {
            _runSweep = (_runSweep + frameDt * 360f / 7.2f) % 360f;
            double ph = Environment.TickCount64 / 1000.0 * 2.0 * Math.PI / 2.2;
            _runPulse = 0.575f + 0.225f * MathF.Sin((float)ph);
        }
        // Glass orbit light: a cool lamp drifts around the slab, one rev / 36s.
        if (!_saturn && _visible)
            _orbitAngle = (_orbitAngle + frameDt * 360f / 36f) % 360f;
        // New-message badges: poll the flashing window set off-thread (~800ms) and
        // breathe the dot (1.0->1.18, 1.4s) so it pulses like the WPF taskbar flash.
        if (_visible && _shown && _anyRunning && Environment.TickCount64 - _attnLast > 800)
        {
            _attnLast = Environment.TickCount64;
            PollAttention();
        }
        bool anyFlash = _flashKeys.Count > 0;
        if (anyFlash)
            _badgePulse = 1.09f + 0.09f * MathF.Sin(Environment.TickCount64 / 1000f * 2f * MathF.PI / 1.4f);
        if (_saturn || animating || fading || active || _dragging || scrolling || _barDrag || maxDelta > 0.001f
            || _gearHover || MathF.Abs(_gearScale - 1f) > 0.002f || _anyRunning
            || (!_saturn && _visible))
            Render();
    }

    /// <summary>Snapshots the running icons' flashing windows off the UI thread and
    /// rebuilds <see cref="_flashKeys"/>, the set of icon keys whose windows are
    /// requesting attention. Reuses AttentionBadges so the icon->window matching is
    /// identical to the hover previews and the WPF dock.</summary>
    private void PollAttention()
    {
        if (_attnBusy)
            return;
        _attnBusy = true;
        var entries = new List<(string key, string path, string? args)>();
        foreach (var s in _slots)
            if (s.Running && s.Entry != null)
                entries.Add((s.IconKey, s.Entry.Path, s.Entry.Arguments));
        System.Threading.Tasks.Task.Run(() =>
        {
            var keys = new System.Collections.Generic.HashSet<string>();
            try
            {
                var flashing = Polaris.Services.AttentionService.SnapshotFlashing();
                foreach (var (key, path, args) in entries)
                    if (AttentionBadges.ForEntry(path, args, flashing, "MainDockGpu").flashing)
                        keys.Add(key);
            }
            catch { }
            _flashKeys = keys;
            _attnBusy = false;
        });
    }

    protected override void Render()
    {
        if (_host == null)
            return;
        var ctx = _host.Context;
        ctx.BeginDraw();
        ctx.Clear(Col(0, 0, 0, 0));

        // Summon slide: translate the whole scene down past the screen edge while
        // dismissed and let it ease up into its docked rest position with a soft BackEase-out
        // overshoot. Dismiss no longer slides (pure fade), so _summon stays at 1 and this is
        // a no-op on the way out.
        float pos = BackEaseOut(_summon);
        float riseOff = (1f - pos) * _riseUnit;
        bool slid = riseOff > 0.01f;
        // Glass vertical squash/stretch: the slab springs from 0.94 -> 1.0 about its
        // bottom-centre (ElasticEase Osc=1 Spring=4), mirroring the WPF AnimateGlassRise
        // stretch so the dock reads as a fluid blob settling rather than a rigid slide.
        float stretchY = 0.94f + 0.06f * ElasticEaseOut(_summon);
        bool stretching = !_saturn && MathF.Abs(stretchY - 1f) > 0.001f;
        if (slid || stretching)
        {
            var pivot = new Vector2(_slabX + _slabW * 0.5f, _slabY + _slabH);
            ctx.Transform = System.Numerics.Matrix3x2.CreateScale(1f, stretchY, pivot)
                * System.Numerics.Matrix3x2.CreateTranslation(0f, riseOff);
        }

        if (_saturn)
        {
            // Summon "rings expand" (mirrors WPF AnimateRingsExpand): the inner band
            // leads and the outer band follows ~0.26 of the summon later, each growing
            // 0.55 -> 1.0 from the planet with a CubicEaseOut fade-in, so the dock reads
            // as rings blooming out from the centre one after another. The backing disc
            // and the centre planet are drawn at FULL size (they never scale with the
            // rings — parity with WPF, where the disc sits at PanelCanvas index 0 and the
            // planet lives on PanelCanvas; both only fade with the overall panel opacity).
            float innerP = CubicEaseOut(Math.Clamp(_summon / 0.74f, 0f, 1f));
            float outerP = CubicEaseOut(Math.Clamp((_summon - 0.26f) / 0.74f, 0f, 1f));
            _satInnerScl = 0.55f + 0.45f * innerP;
            _satOuterScl = 0.55f + 0.45f * outerP;
            _satInnerOp = innerP;
            _satOuterOp = outerP;
            _satR0 = EffectiveRing0Count(_slots.Count);
            var cen = new Vector2(_sg.Cx, _sg.Cy);
            var satBase = System.Numerics.Matrix3x2.CreateScale(_satInnerScl, _satInnerScl, cen);
            var outerBase = System.Numerics.Matrix3x2.CreateScale(_satOuterScl, _satOuterScl, cen);

            // Backing disc + base starfield, the static rings and the revolution cues are all
            // baked to the ring sub-region; place each back with a +ring-origin translation
            // (prepended to its existing scale/revolve transform).
            var ringTf = System.Numerics.Matrix3x2.CreateTranslation(_satRingOx, _satRingOy);
            ctx.Transform = ringTf;
            if (_satBacking != null)
                ctx.DrawBitmap(_satBacking, 1f, InterpolationMode.Linear);

            // Inner ring group blooms on the inner band; the outer A/F/G/E ring layer
            // follows on the outer band so the ring graphic expands inside-out.
            ctx.Transform = ringTf * satBase;
            if (_satStaticInner != null)
                ctx.DrawBitmap(_satStaticInner, _satInnerOp, InterpolationMode.Linear);
            ctx.Transform = ringTf * outerBase;
            if (_satStaticOuter != null)
                ctx.DrawBitmap(_satStaticOuter, _satOuterOp, InterpolationMode.Linear);
            // Re-revolve the baked cue bitmaps: ringTf * Rotate(orbit) * Scale(1,tilt) * base.
            // Inner cues bloom with the inner band; outer cues trail on the outer band.
            if (_satInner != null)
            {
                ctx.Transform = ringTf * System.Numerics.Matrix3x2.CreateRotation((float)(_innerAngle * Math.PI / 180.0), cen)
                    * System.Numerics.Matrix3x2.CreateScale(1f, _sg.TiltY, cen) * satBase;
                ctx.DrawBitmap(_satInner, _satInnerOp, InterpolationMode.Linear);
            }
            if (_satOuter != null)
            {
                ctx.Transform = ringTf * System.Numerics.Matrix3x2.CreateRotation((float)(_outerAngle * Math.PI / 180.0), cen)
                    * System.Numerics.Matrix3x2.CreateScale(1f, _sg.TiltY, cen) * outerBase;
                ctx.DrawBitmap(_satOuter, _satOuterOp, InterpolationMode.Linear);
            }

            // Centre planet: full size, full opacity, drawn over the rings (parity with
            // WPF, where the planet is added to PanelCanvas after the ring layers).
            // Planet/disc/shade are sub-region bitmaps; place each at its baked offset via a
            // translation (the disc additionally rotates about the absolute centre, so the spin
            // is identical to the old full-window bake).
            var planetTf = System.Numerics.Matrix3x2.CreateTranslation(_satPlanetOx, _satPlanetOy);
            ctx.Transform = planetTf;
            if (_satPlanet != null)
                ctx.DrawBitmap(_satPlanet, 1f, InterpolationMode.Linear);
            if (_satDisc != null)
            {
                ctx.Transform = planetTf * System.Numerics.Matrix3x2.CreateRotation(
                    (float)(_spinAngle * Math.PI / 180.0), cen);
                ctx.DrawBitmap(_satDisc, 1f, InterpolationMode.Linear);
            }
            ctx.Transform = planetTf;
            if (_satShade != null)
                ctx.DrawBitmap(_satShade, 1f, InterpolationMode.Linear);
            ctx.Transform = System.Numerics.Matrix3x2.Identity;
            SaturnScene.DrawTwinkle(ctx, _sg, _saturnTime);
            ctx.Transform = System.Numerics.Matrix3x2.Identity;
        }
        else
        {
        // Floating liquid-glass slab (drop shadow on, since the main dock floats above
        // the desktop rather than being flush to a screen edge).
        GlassSlab.DrawGlass(ctx, _slabX, _slabY, _slabW, _slabH, _radius, _opacity, _frost, shadowExtent: 16f);

        // Orbit light: a cool lamp circling the slab centre casts an inward glow.
        // Filling the rounded slab with the lamp's radial gradient clips it to the
        // glass automatically (parity with GlassOrbitLight, 36s/rev).
        var slab = new RoundedRectangle { Rect = new Vortice.Mathematics.Rect(_slabX, _slabY, _slabW, _slabH), RadiusX = _radius, RadiusY = _radius };
        DrawOrbitLight(ctx, slab);

        // Stereoscopic rim: a soft cool glow + crisp dark/bright double rim, mirroring
        // DrawGlassPanel's slabGlow/slabShade/slabRim strokes.
        using (var glow = ctx.CreateSolidColorBrush(Col(0x73, 0xBF, 0xE0, 0xFF)))
            ctx.DrawRoundedRectangle(slab, glow, 5f);
        using (var shade = ctx.CreateSolidColorBrush(Col(0x80, 0x06, 0x0B, 0x16)))
            ctx.DrawRoundedRectangle(slab, shade, 2.4f);
        using (var rim = ctx.CreateSolidColorBrush(Col(0xE6, 0xEA, 0xF4, 0xFF)))
            ctx.DrawRoundedRectangle(slab, rim, 1.4f);

        DrawGear(ctx);
        DrawClock(ctx);
        }

        // Draw smallest-first so the magnified (focal) icon sits on top of its neighbours.
        // The dragged icon is skipped here and drawn last, lifted under the cursor.
        int dragIdx = _dragging ? _pressIdx : -1;
        float scrollY = _scrollable ? (float)_glassScroll : 0f;
        int n = _slots.Count;
        if (_orderBuf == null || _orderBuf.Length != n) _orderBuf = new int[n];
        var order = _orderBuf;
        for (int i = 0; i < n; i++) order[i] = i;
        _orderCmp ??= (a, b) => _waveCur[a].CompareTo(_waveCur[b]);
        Array.Sort(order, _orderCmp);
        bool clip = _scrollable;
        if (clip)
            ctx.PushAxisAlignedClip(new Vortice.Mathematics.Rect(_gvX, _gvY, _gvW, _gvH), AntialiasMode.Aliased);
        if (!_saturn && _hasResident)
            DrawResidentFrame(ctx, scrollY);
        foreach (int i in order)
        {
            if (i == dragIdx)
                continue;
            var off = new Vector2(_waveOffX[i], _waveOffY[i] - scrollY);
            float gi = _saturn && i < _slotG.Length ? _slotG[i] : 0f;
            if (_saturn && (_satInnerScl != 1f || _satOuterScl != 1f))
            {
                // Staggered rings-expand: inner-ring icons bloom with the inner band,
                // outer-ring icons trail with the outer band — each scaled out from the
                // planet and faded in on its own ring's timeline.
                bool inner = i < _satR0;
                float rScl = inner ? _satInnerScl : _satOuterScl;
                float rOp = inner ? _satInnerOp : _satOuterOp;
                var ce = new Vector2(_sg.Cx, _sg.Cy);
                var ss = _slots[i];
                var scaled = new IconSlot(ce + (ss.Center - ce) * rScl, ss.IconKey, ss.Name, ss.Running, ss.Image, ss.Entry);
                DrawIcon(ctx, scaled, _waveCur[i], off * rScl, gi * rScl, rOp);
            }
            else
            {
                // Pressed icon: shrink only the glyph toward rest (lens frame kept) by glyphMul.
                float glyphMul = (!_dragging && !_launching && i == _pressIdx && _pressShrink > 0.0005f && _waveCur[i] > 1.0001f)
                    ? (1f + (1f / _waveCur[i] - 1f) * _pressShrink)
                    : 1f;
                DrawIcon(ctx, _slots[i], _waveCur[i], off, gi, 1f, glyphMul);
            }
        }
        if (clip)
            ctx.PopAxisAlignedClip();

        if (dragIdx >= 0 && dragIdx < _slots.Count && _ghost == null)
        {
            // The dragged icon follows the cursor, lifted 1.12x with no spread. Skipped when
            // an independent drag-ghost overlay is showing it instead (so it roams the desktop).
            var s = _slots[dragIdx];
            var moved = new IconSlot(new Vector2(_dragX, _dragY), s.IconKey, s.Name, s.Running, s.Image, s.Entry);
            DrawIcon(ctx, moved, 1.12f, Vector2.Zero, _saturn && dragIdx < _slotG.Length ? _slotG[dragIdx] : 0f);
        }
        else if (_labelIdx >= 0 && _labelIdx < _slots.Count && _labelOp > 0.01f)
        {
            var hs = _slots[_labelIdx];
            var hsv = scrollY != 0f
                ? new IconSlot(new Vector2(hs.Center.X, hs.Center.Y - scrollY), hs.IconKey, hs.Name, hs.Running, hs.Image, hs.Entry)
                : hs;
            DrawHoverLabel(ctx, hsv, _waveCur[_labelIdx], _labelOp);
        }

        if (!_saturn && _scrollable)
            DrawScrollBar(ctx);

        // Preview the dragged item's icon at the drop point while an external drag hovers the
        // dock (the OS drag image isn't shown over the topmost composition window).
        if (_extDragPt is { } dp && _dragIconKey != null)
            DrawDragPreview(ctx, dp);

        if (slid || stretching)
            ctx.Transform = System.Numerics.Matrix3x2.Identity;
        ctx.EndDraw();
        _host.Present();
        Polaris.Services.GpuFrameStats.Frame("main");
        if (_dragging && _dragDiagSession != 0)
            DragPerfStats.Frame(_dragDiagSession, _dragArrangeDiagMs);
    }

    // BackEase-out (soft overshoot, mirrors the WPF glass-rise settle).
    private static float BackEaseOut(float t)
    {
        const float s = 1.18f;   // Amplitude ~0.18
        float u = t - 1f;
        return u * u * ((s + 1f) * u + s) + 1f;
    }

    private static float EaseInCubic(float t) => t * t * t;

    private static float CubicEaseOut(float t)
    {
        float u = 1f - Math.Clamp(t, 0f, 1f);
        return 1f - u * u * u;
    }

    // ElasticEase EaseOut (Oscillations=1, Springiness=4) — springs slightly past the
    // target then relaxes back, matching the WPF glass-rise vertical stretch.
    private static float ElasticEaseOut(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return 1f - ElasticEaseIn(1f - t);
    }

    private static float ElasticEaseIn(float t)
    {
        const float osc = 1f, spring = 4f;
        float expo = (MathF.Exp(spring * t) - 1f) / (MathF.Exp(spring) - 1f);
        return expo * MathF.Sin((MathF.PI * 2f * osc + MathF.PI * 0.5f) * t);
    }

    /// <summary>Settings gear in the slab's top-right corner: a hollow-glass bead
    /// (bright cool-white rim over a near-transparent interior) holding a large
    /// outlined cog — eight hollow teeth + a hollow hub drawn as stroked vector
    /// geometry (a font ⚙ glyph renders the teeth solid and too small). The cog
    /// spins while hovered and the bead dips on press. Clicking opens settings.</summary>
    private void DrawGear(ID2D1DeviceContext ctx)
    {
        float r = _gearR * _gearScale;
        var e = new Vortice.Direct2D1.Ellipse(_gearC, r, r);
        // Hollow-glass bead: a very faint frosted interior so the disc reads as a
        // hollow ring (parity with the original), with a crisp cool-white rim.
        using (var stops = ctx.CreateGradientStopCollection(new[]
        {
            new Vortice.Direct2D1.GradientStop { Position = 0f,   Color = Col(0x2A, 0xFF, 0xFF, 0xFF) },
            new Vortice.Direct2D1.GradientStop { Position = 0.5f, Color = Col(0x16, 0xEA, 0xF2, 0xFF) },
            new Vortice.Direct2D1.GradientStop { Position = 1f,   Color = Col(0x22, 0xD6, 0xE4, 0xF6) },
        }))
        using (var fill = ctx.CreateLinearGradientBrush(
            new LinearGradientBrushProperties { StartPoint = new Vector2(_gearC.X, _gearC.Y - r), EndPoint = new Vector2(_gearC.X, _gearC.Y + r) }, stops))
            ctx.FillEllipse(e, fill);
        using (var rim = ctx.CreateSolidColorBrush(Col(0xFF, 0xEA, 0xF4, 0xFF)))
            ctx.DrawEllipse(e, rim, 2.2f);

        // Outlined cog: rotate while hovered, scale on press (about the bead centre).
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateRotation(_gearAngle * MathF.PI / 180f, _gearC)
            * Matrix3x2.CreateScale(_gearScale, _gearScale, _gearC) * saved;

        const int teeth = 8;
        float rTip = _gearR * 0.72f;     // tooth tip radius (large, fills the bead)
        float rRoot = _gearR * 0.50f;    // valley radius between teeth
        float rHole = _gearR * 0.24f;    // hollow hub
        double step = 2 * Math.PI / teeth;
        double tipHalf = step * 0.17;    // narrow flat tip
        double rootHalf = step * 0.33;   // wider base, so each tooth is a trapezoid
        Vector2 Polar(double a, float rad) =>
            new Vector2(_gearC.X + (float)(Math.Cos(a) * rad), _gearC.Y + (float)(Math.Sin(a) * rad));
        var pts = new List<Vector2>(teeth * 4);
        for (int i = 0; i < teeth; i++)
        {
            double baseA = i * step - Math.PI / 2;          // first tooth points up
            pts.Add(Polar(baseA - rootHalf, rRoot));        // wide base, left
            pts.Add(Polar(baseA - tipHalf, rTip));          // slope in to narrow tip, left
            pts.Add(Polar(baseA + tipHalf, rTip));          // tip flat
            pts.Add(Polar(baseA + rootHalf, rRoot));        // slope out to wide base, right
        }
        using (var cog = ctx.Factory.CreatePathGeometry())
        {
            using (var sink = cog.Open())
            {
                sink.BeginFigure(pts[0], FigureBegin.Hollow);
                for (int k = 1; k < pts.Count; k++) sink.AddLine(pts[k]);
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }
            float sw = MathF.Max(2.4f, _gearR * 0.16f);
            using var stroke = ctx.CreateSolidColorBrush(Col(0xF2, 0xF2, 0xF7, 0xFF));
            ctx.DrawGeometry(cog, stroke, sw);
            ctx.DrawEllipse(new Vortice.Direct2D1.Ellipse(_gearC, rHole, rHole), stroke, sw);
        }
        ctx.Transform = saved;
    }

    /// <summary>Orbit light: a cool radial lamp circling the slab centre (36s/rev),
    /// painted by filling the rounded slab with the lamp's radial gradient so the
    /// glow is clipped to the glass (parity with GlassOrbitLight).</summary>
    private void DrawOrbitLight(ID2D1DeviceContext ctx, in RoundedRectangle slab)
    {
        float cx = _slabX + _slabW / 2f, cy = _slabY + _slabH / 2f;
        float orbitR = MathF.Max(_slabW, _slabH) * 0.5f + _effIcon * 1.4f;
        float lampR = orbitR * 1.7f;                 // larger soft pool so more of the cool light lands on the glass
        float th = _orbitAngle * MathF.PI / 180f;
        var lamp = new Vector2(cx + orbitR * MathF.Sin(th), cy - orbitR * MathF.Cos(th));
        using var stops = ctx.CreateGradientStopCollection(new[]
        {
            new Vortice.Direct2D1.GradientStop { Position = 0f,    Color = Col(0x58, 0xE8, 0xEC, 0xE4) },
            new Vortice.Direct2D1.GradientStop { Position = 0.34f, Color = Col(0x3A, 0x98, 0xC6, 0xE2) },
            new Vortice.Direct2D1.GradientStop { Position = 0.62f, Color = Col(0x1A, 0x5E, 0xA2, 0xE6) },
            new Vortice.Direct2D1.GradientStop { Position = 1f,    Color = Col(0x00, 0x4C, 0x88, 0xD6) },
        });
        using var brush = ctx.CreateRadialGradientBrush(
            new RadialGradientBrushProperties { Center = lamp, RadiusX = lampR, RadiusY = lampR }, stops);
        ctx.FillRoundedRectangle(slab, brush);
    }

    /// <summary>Top-left date/time bar on the glass slab (parity with RadialWindow's
    /// inline clock): frosted white gradient text with a soft dark halo, formatted
    /// "yyyy年M月d日  ddd   H:mm" in the local culture.</summary>
    private void DrawClock(ID2D1DeviceContext ctx)
    {
        if (_clockFormat == null) return;
        _lastClockMin = DateTime.Now.Minute;
        string text = DateTime.Now.ToString("yyyy\u5e74M\u6708d\u65e5  ddd   HH:mm",
            System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
        string? wx = _weather.Summary;
        if (!string.IsNullOrEmpty(wx))
            text += "     " + wx;
        float x = _slabX + 18f, y = _slabY + 14f;
        var rect = new Vortice.Mathematics.Rect(x, y, _slabW - 36f, _effIcon * 1.2f);
        // Dark halo: a faint offset underlay reads as a soft drop shadow.
        using (var halo = ctx.CreateSolidColorBrush(Col(0xB0, 0x03, 0x06, 0x0E)))
        {
            ctx.DrawText(text, _clockFormat, new Vortice.Mathematics.Rect(x + 1f, y + 1.5f, rect.Width, rect.Height), halo);
            ctx.DrawText(text, _clockFormat, new Vortice.Mathematics.Rect(x - 0.6f, y + 0.6f, rect.Width, rect.Height), halo);
        }
        // Frosted white vertical gradient (WPF: F0FFFFFF -> D2F2F6FF -> BED8E2F2).
        using (var stops = ctx.CreateGradientStopCollection(new[]
        {
            new Vortice.Direct2D1.GradientStop { Position = 0f,   Color = Col(0xF0, 0xFF, 0xFF, 0xFF) },
            new Vortice.Direct2D1.GradientStop { Position = 0.55f, Color = Col(0xD2, 0xF2, 0xF6, 0xFF) },
            new Vortice.Direct2D1.GradientStop { Position = 1f,   Color = Col(0xBE, 0xD8, 0xE2, 0xF2) },
        }))
        using (var ink = ctx.CreateLinearGradientBrush(
            new LinearGradientBrushProperties { StartPoint = new Vector2(x, y), EndPoint = new Vector2(x, y + _clockFormat.FontSize * 1.2f) }, stops))
            ctx.DrawText(text, _clockFormat, rect, ink);
    }

    /// <summary>Returns the current scrollbar thumb rect (window-local DIP), or a
    /// zero rect when the grid is not scrollable.</summary>
    private Vortice.Mathematics.Rect ThumbRect()
    {
        if (!_scrollable || _barTrackH <= 0f)
            return default;
        float frac = (float)(LiquidGlassTheme.VisibleRows * _glassCellH /
                              ((LiquidGlassTheme.VisibleRows * _glassCellH) + _glassScrollMax));
        float thumbH = Math.Max(_barTrackH * Math.Clamp(frac, 0.08f, 1f), _barW * 2.4f);
        float t = _glassScrollMax > 0.5 ? (float)(_glassScroll / _glassScrollMax) : 0f;
        float top = _barTop + (_barTrackH - thumbH) * t;
        return new Vortice.Mathematics.Rect(_barX, top, _barW, thumbH);
    }

    /// <summary>Draws the slim white scroll rail + thumb on the slab's right edge
    /// (parity with the WPF glass dock's vertical scrollbar).</summary>
    private void DrawScrollBar(ID2D1DeviceContext ctx)
    {
        float r = _barW / 2f;
        var track = new RoundedRectangle { Rect = new Vortice.Mathematics.Rect(_barX, _barTop, _barW, _barTrackH), RadiusX = r, RadiusY = r };
        using (var tb = ctx.CreateSolidColorBrush(Col(0x30, 0xFF, 0xFF, 0xFF)))
            ctx.FillRoundedRectangle(track, tb);
        var th = ThumbRect();
        var thumb = new RoundedRectangle { Rect = th, RadiusX = r, RadiusY = r };
        byte a = (byte)(_barDrag ? 0xF0 : 0xC0);
        using (var thb = ctx.CreateSolidColorBrush(Col(a, 0xFF, 0xFF, 0xFF)))
            ctx.FillRoundedRectangle(thumb, thb);
    }

    /// <summary>Handles a left-press on the scrollbar: starts a thumb drag (grabbing
    /// at the press offset, or centring the thumb on a track click). Returns true
    /// when the press is consumed so the icon/gear handlers are skipped.</summary>
    private bool TryBarPress(float lx, float ly)
    {
        if (_saturn || !_scrollable)
            return false;
        bool inX = lx >= _barX - 6f && lx <= _barX + _barW + 6f;
        bool inY = ly >= _barTop - 2f && ly <= _barTop + _barTrackH + 2f;
        if (!inX || !inY)
            return false;
        var th = ThumbRect();
        bool onThumb = ly >= (float)th.Top - 2f && ly <= (float)th.Top + (float)th.Height + 2f;
        _barGrabDy = onThumb ? ly - (float)th.Top : (float)th.Height / 2f;
        _barDrag = true;
        BarDragTo(ly);
        SetCapture(_hwnd);
        return true;
    }

    /// <summary>Maps a thumb-drag pointer position to the scroll offset (commits the
    /// offset immediately so the grid tracks the thumb 1:1).</summary>
    private void BarDragTo(float ly)
    {
        var th = ThumbRect();
        float range = _barTrackH - (float)th.Height;
        if (range <= 0.01f)
            return;
        float top = Math.Clamp(ly - _barGrabDy, _barTop, _barTop + range);
        double frac = (top - _barTop) / range;
        _glassScroll = _glassScrollTarget = frac * _glassScrollMax;
    }

    /// <summary>Rounded frame around the resident (pinned) rows: a ~95%-transparent cool
    /// fill plus a 3D bevel border matching the per-icon glass rim — a dark shade biased to
    /// the lower-right under a bright top-left highlight (light from top-left) — so the region
    /// edge reads as raised glass, consistent with the icon tiles. Drawn in grid space (shifted
    /// by the scroll offset) under the icons.</summary>
    private void DrawResidentFrame(ID2D1DeviceContext ctx, float scrollY)
    {
        float lft = _resX, top = _resY - scrollY;
        var rect = new Vortice.Mathematics.Rect(lft, top, _resW, _resH);
        var rr = new RoundedRectangle { Rect = rect, RadiusX = _resR, RadiusY = _resR };
        using (var fill = ctx.CreateSolidColorBrush(Col(0x10, 0xFF, 0xFF, 0xFF)))
            ctx.FillRoundedRectangle(rr, fill);
        // 3D bevel border, identical treatment to DrawIcon's resting rim: two diagonal strokes
        // along the frame's top-left -> lower-right axis fake a raised edge. (a) A dark cool shade
        // deepening to the lower-right (edge facing away from the light), drawn wider and under;
        // (b) a bright cool-white highlight strongest at the top-left, riding on top.
        var tl = new Vector2(lft, top);
        var brc = new Vector2(lft + _resW, top + _resH);
        using (var shadeStops = ctx.CreateGradientStopCollection(new[]
        {
            new Vortice.Direct2D1.GradientStop { Position = 0f,    Color = Col(0x00, 0x0B, 0x14, 0x24) },
            new Vortice.Direct2D1.GradientStop { Position = 0.55f, Color = Col(0x12, 0x0B, 0x14, 0x24) },
            new Vortice.Direct2D1.GradientStop { Position = 1f,    Color = Col(0x70, 0x0B, 0x14, 0x24) },
        }))
        using (var shade = ctx.CreateLinearGradientBrush(
            new LinearGradientBrushProperties { StartPoint = tl, EndPoint = brc }, shadeStops))
            ctx.DrawRoundedRectangle(rr, shade, 2.0f);
        using (var rimStops = ctx.CreateGradientStopCollection(new[]
        {
            new Vortice.Direct2D1.GradientStop { Position = 0f,    Color = Col(0xCA, 255, 255, 255) },
            new Vortice.Direct2D1.GradientStop { Position = 0.42f, Color = Col(0x22, 255, 255, 255) },
            new Vortice.Direct2D1.GradientStop { Position = 0.62f, Color = Col(0x0C, 255, 255, 255) },
            new Vortice.Direct2D1.GradientStop { Position = 1f,    Color = Col(0x52, 0xD8, 0xEC, 0xFF) },
        }))
        using (var rim = ctx.CreateLinearGradientBrush(
            new LinearGradientBrushProperties { StartPoint = tl, EndPoint = brc }, rimStops))
            ctx.DrawRoundedRectangle(rr, rim, 1.2f);
    }

    // Lens colour with an opacity multiplier (the water-droplet lens fades in with magnification).
    private static Color4 LensCol(int a, int r, int g, int b, float mul)
        => new(r / 255f, g / 255f, b / 255f, a / 255f * mul);

    private void DrawIcon(ID2D1DeviceContext ctx, in IconSlot s, float scale, Vector2 off, float gIcon = 0f, float opacity = 1f, float glyphMul = 1f)
    {
        float g = gIcon > 0f ? gIcon : _gIcon, half = g / 2f, cx = s.Center.X + off.X, cy = s.Center.Y + off.Y;
        var center = new Vector2(cx, cy);
        // Compose every icon transform with the ambient context transform (the glass
        // summon slide + bottom-anchored squash/stretch) so the icons ride the slab as it
        // rises, instead of snapping to their docked positions while the slab slides.
        var baseTf = ctx.Transform;
        var wave = Matrix3x2.CreateScale(scale, scale, center) * baseTf;
        // Same magnify, but blown up by IconFrameScale — used for the glass frame layers (rim /
        // running sweep) so they sit a little outside the glyph and their rounded corners clear it.
        var waveFrame = Matrix3x2.CreateScale(scale * IconFrameScale, scale * IconFrameScale, center) * baseTf;

        // Running indicator: a soft pulsing glow halo plus a flowing sweep border — a
        // rounded-rect stroke painted with a linear gradient whose axis rotates
        // (7.2s/rev). Shown in BOTH themes, mirroring RadialIcon's RunningBorder which
        // is not theme-gated.
        if (s.Running)
        {
            ctx.Transform = _saturn ? wave : waveFrame;
            // Frame the icon at its full nominal size (parity with the WPF RunningBorder,
            // a Border of Width/Height = IconSize) so the flow ring sits clear of the
            // glyph rather than cutting into it.
            float box = g, rr = box * IconCornerRatio;
            var rect = new Vortice.Mathematics.Rect(cx - box / 2f, cy - box / 2f, box, box);
            var rrect = new RoundedRectangle { Rect = rect, RadiusX = rr, RadiusY = rr };
            // Pulsing cool halo (a few falling-alpha strokes fake the blurred glow): reuse ONE
            // cached solid brush, just setting its colour per stroke (see the cached-brush fields).
            float pulse = _runPulse;
            _runHaloBrush ??= ctx.CreateSolidColorBrush(Col(0x22, 0x3F, 0xA9, 0xFF));
            _runHaloBrush.Color = Col((byte)(0x22 * pulse), 0x3F, 0xA9, 0xFF);
            ctx.DrawRoundedRectangle(rrect, _runHaloBrush, 7f);
            _runHaloBrush.Color = Col((byte)(0x44 * pulse), 0x3F, 0xA9, 0xFF);
            ctx.DrawRoundedRectangle(rrect, _runHaloBrush, 4.5f);
            // Flowing sweep: a rotating linear gradient stroked around the icon. The colours are
            // constant, so build the stop collection + brush ONCE (host lifetime) and only move
            // the brush's gradient axis per frame, instead of recreating both every frame.
            float ang = _runSweep * MathF.PI / 180f, R = box * 0.6f;
            var dir = new Vector2(MathF.Cos(ang) * R, MathF.Sin(ang) * R);
            if (_runSweepBrush == null)
            {
                _runSweepStops = ctx.CreateGradientStopCollection(new[]
                {
                    new Vortice.Direct2D1.GradientStop { Position = 0f,    Color = Col(0x10, 0x3D, 0xA9, 0xFF) },
                    new Vortice.Direct2D1.GradientStop { Position = 0.28f, Color = Col(0x66, 0x57, 0xC8, 0xFF) },
                    new Vortice.Direct2D1.GradientStop { Position = 0.5f,  Color = Col(0xFF, 0x6F, 0xD3, 0xFF) },
                    new Vortice.Direct2D1.GradientStop { Position = 0.72f, Color = Col(0x66, 0x57, 0xC8, 0xFF) },
                    new Vortice.Direct2D1.GradientStop { Position = 1f,    Color = Col(0x10, 0x3D, 0xA9, 0xFF) },
                });
                _runSweepBrush = ctx.CreateLinearGradientBrush(
                    new LinearGradientBrushProperties { StartPoint = new Vector2(cx - dir.X, cy - dir.Y), EndPoint = new Vector2(cx + dir.X, cy + dir.Y) },
                    _runSweepStops);
            }
            _runSweepBrush.StartPoint = new Vector2(cx - dir.X, cy - dir.Y);
            _runSweepBrush.EndPoint = new Vector2(cx + dir.X, cy + dir.Y);
            ctx.DrawRoundedRectangle(rrect, _runSweepBrush, 2.5f);
            ctx.Transform = baseTf;
        }

        var bmp = GetBitmap(ctx, s.IconKey, s.Image);
        if (bmp == null)
            return;
        float pad = g * 0.06f, dstX = cx - half + pad, dstY = cy - half + pad, dstSz = g - pad * 2;
        var bs = bmp.Size;
        // glyphMul (<1 while the icon is pressed) shrinks ONLY the glyph about the cell centre;
        // the magnify scale (and so the lens frame below) stays at its hover size, so pressing
        // shrinks the icon but keeps the water-droplet lens frame.
        float gScale = scale * glyphMul;
        ctx.Transform = Matrix3x2.CreateScale(dstSz / Math.Max(1f, bs.Width), dstSz / Math.Max(1f, bs.Height))
                      * Matrix3x2.CreateTranslation(dstX, dstY) * Matrix3x2.CreateScale(gScale, gScale, center) * baseTf;
        ctx.DrawBitmap(bmp, Math.Clamp(opacity, 0f, 1f), InterpolationMode.HighQualityCubic);
        ctx.Transform = baseTf;

        // Always-on 3D liquid-glass edge (glass theme): every icon keeps a raised glass rim at rest,
        // not only while hovered, so the grid reads as framed glass tiles. Two diagonal strokes fake
        // a bevel — a dark shade biased to the lower-right, then a bright highlight biased to the
        // top-left (light from top-left) — giving the edge real depth. It sits on the same frame as
        // the white tile (scale space), so on press the glyph depresses inside a steady tile. The rim
        // fades down as the magnification (hover lens below) rises, so the resting rim and the lens'
        // wet rim don't stack into one over-thick edge on the focal icon. Cached brushes per icon.
        if (!_saturn)
        {
            float rimOp = Math.Clamp(opacity, 0f, 1f)
                        * (1f - 0.6f * Math.Clamp((scale - 1f) / Math.Max(0.001f, MagnifyPeak - 1f), 0f, 1f));
            if (rimOp > 0.01f)
            {
                ctx.Transform = waveFrame;
                float x0 = cx - half, y0 = cy - half, rr = g * IconCornerRatio;
                var rrect = new RoundedRectangle { Rect = new Vortice.Mathematics.Rect(x0, y0, g, g), RadiusX = rr, RadiusY = rr };
                // (a) Bevel shade: clear at the top-left, deepening to a cool shadow at the lower-right
                // — the edge facing away from the light. Drawn slightly wider, under the highlight.
                if (_iconShadeBrush == null)
                {
                    _iconShadeStops = ctx.CreateGradientStopCollection(new[]
                    {
                        new Vortice.Direct2D1.GradientStop { Position = 0f,    Color = LensCol(0x00, 0x0B, 0x14, 0x24, 1f) },
                        new Vortice.Direct2D1.GradientStop { Position = 0.55f, Color = LensCol(0x12, 0x0B, 0x14, 0x24, 1f) },
                        new Vortice.Direct2D1.GradientStop { Position = 1f,    Color = LensCol(0x70, 0x0B, 0x14, 0x24, 1f) },
                    });
                    _iconShadeBrush = ctx.CreateLinearGradientBrush(
                        new LinearGradientBrushProperties { StartPoint = new Vector2(x0, y0), EndPoint = new Vector2(x0 + g, y0 + g) },
                        _iconShadeStops);
                }
                _iconShadeBrush.StartPoint = new Vector2(x0, y0);
                _iconShadeBrush.EndPoint = new Vector2(x0 + g, y0 + g);
                _iconShadeBrush.Opacity = rimOp;
                ctx.DrawRoundedRectangle(rrect, _iconShadeBrush, 2.0f);

                // (b) Bevel highlight: bright cool-white at the top-left, thinning to a cool tint at
                // the lower-right — the lit glass edge riding on top of the shade.
                if (_iconRimBrush == null)
                {
                    _iconRimStops = ctx.CreateGradientStopCollection(new[]
                    {
                        new Vortice.Direct2D1.GradientStop { Position = 0f,    Color = LensCol(0xCA, 255, 255, 255, 1f) },
                        new Vortice.Direct2D1.GradientStop { Position = 0.42f, Color = LensCol(0x22, 255, 255, 255, 1f) },
                        new Vortice.Direct2D1.GradientStop { Position = 0.62f, Color = LensCol(0x0C, 255, 255, 255, 1f) },
                        new Vortice.Direct2D1.GradientStop { Position = 1f,    Color = LensCol(0x52, 0xD8, 0xEC, 0xFF, 1f) },
                    });
                    _iconRimBrush = ctx.CreateLinearGradientBrush(
                        new LinearGradientBrushProperties { StartPoint = new Vector2(x0, y0), EndPoint = new Vector2(x0 + g, y0 + g) },
                        _iconRimStops);
                }
                _iconRimBrush.StartPoint = new Vector2(x0, y0);
                _iconRimBrush.EndPoint = new Vector2(x0 + g, y0 + g);
                _iconRimBrush.Opacity = rimOp;
                ctx.DrawRoundedRectangle(rrect, _iconRimBrush, 1.2f);
                ctx.Transform = baseTf;
            }
        }
        // Liquid-glass water-droplet lens over the magnified icon (port of the WPF RadialIcon
        // HoverGlow): a domed refraction tint + a wet rim + a specular shine + a focused
        // caustic, layered on top of the icon so a hovered icon reads as a bead of water
        // magnifying it. Glass only (Saturn has its own hover styling), and it fades in with the
        // magnification (strongest on the focal icon under the cursor) rather than a binary hover.
        if (!_saturn && scale > 1.001f)
        {
            float lensOp = Math.Clamp((scale - 1f) / Math.Max(0.001f, MagnifyPeak - 1f), 0f, 1f)
                         * Math.Clamp(opacity, 0f, 1f);
            if (lensOp > 0.01f)
            {
                // The lens scales WITH the glyph (gScale), so on press the whole bead shrinks
                // together; its opacity stays tied to the hover scale so it never vanishes. The extra
                // IconFrameScale matches the resting rim so the wet rim clears the glyph corners too.
                ctx.Transform = Matrix3x2.CreateScale(gScale * IconFrameScale, gScale * IconFrameScale, center) * baseTf;
                float x0 = cx - half, y0 = cy - half, rr = g * IconCornerRatio;
                var rrect = new RoundedRectangle { Rect = new Vortice.Mathematics.Rect(x0, y0, g, g), RadiusX = rr, RadiusY = rr };

                // 1) Domed refraction tint (radial, bright shoulder at 0.36,0.28 -> clear -> cool base).
                using (var stops = ctx.CreateGradientStopCollection(new[]
                {
                    new Vortice.Direct2D1.GradientStop { Position = 0f,    Color = LensCol(0x52, 255, 255, 255, lensOp) },
                    new Vortice.Direct2D1.GradientStop { Position = 0.40f, Color = LensCol(0x1E, 255, 255, 255, lensOp) },
                    new Vortice.Direct2D1.GradientStop { Position = 0.72f, Color = LensCol(0x00, 255, 255, 255, lensOp) },
                    new Vortice.Direct2D1.GradientStop { Position = 1f,    Color = LensCol(0x1A, 0x8F, 0xB6, 0xE8, lensOp) },
                }))
                using (var br = ctx.CreateRadialGradientBrush(new RadialGradientBrushProperties
                {
                    Center = new Vector2(x0 + g * 0.36f, y0 + g * 0.28f), GradientOriginOffset = Vector2.Zero,
                    RadiusX = g * 0.95f, RadiusY = g * 0.95f,
                }, stops))
                    ctx.FillRoundedRectangle(rrect, br);

                // 2) Wet rim: a faint, very thin hairline, strongest top-left, cooling to the lower-right.
                using (var stops = ctx.CreateGradientStopCollection(new[]
                {
                    new Vortice.Direct2D1.GradientStop { Position = 0f,    Color = LensCol(0x80, 255, 255, 255, lensOp) },
                    new Vortice.Direct2D1.GradientStop { Position = 0.45f, Color = LensCol(0x18, 255, 255, 255, lensOp) },
                    new Vortice.Direct2D1.GradientStop { Position = 0.60f, Color = LensCol(0x0A, 255, 255, 255, lensOp) },
                    new Vortice.Direct2D1.GradientStop { Position = 1f,    Color = LensCol(0x44, 0xD8, 0xEC, 0xFF, lensOp) },
                }))
                using (var br = ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties
                {
                    StartPoint = new Vector2(x0, y0), EndPoint = new Vector2(x0 + g, y0 + g),
                }, stops))
                    ctx.DrawRoundedRectangle(rrect, br, 0.8f);

                // 3) Specular shine: the bright soft dot on the droplet's shoulder.
                using (var stops = ctx.CreateGradientStopCollection(new[]
                {
                    new Vortice.Direct2D1.GradientStop { Position = 0f,   Color = LensCol(0xC8, 255, 255, 255, lensOp) },
                    new Vortice.Direct2D1.GradientStop { Position = 0.5f, Color = LensCol(0x48, 255, 255, 255, lensOp) },
                    new Vortice.Direct2D1.GradientStop { Position = 1f,   Color = LensCol(0x00, 255, 255, 255, lensOp) },
                }))
                using (var br = ctx.CreateRadialGradientBrush(new RadialGradientBrushProperties
                {
                    Center = new Vector2(x0 + g * 0.32f, y0 + g * 0.24f), GradientOriginOffset = Vector2.Zero,
                    RadiusX = g * 0.26f, RadiusY = g * 0.20f,
                }, stops))
                    ctx.FillEllipse(new Ellipse { Point = new Vector2(x0 + g * 0.32f, y0 + g * 0.24f), RadiusX = g * 0.26f, RadiusY = g * 0.20f }, br);

                // 4) Focused caustic: a faint bright pool at the lower-right.
                using (var stops = ctx.CreateGradientStopCollection(new[]
                {
                    new Vortice.Direct2D1.GradientStop { Position = 0f, Color = LensCol(0x3C, 255, 255, 255, lensOp) },
                    new Vortice.Direct2D1.GradientStop { Position = 1f, Color = LensCol(0x00, 255, 255, 255, lensOp) },
                }))
                using (var br = ctx.CreateRadialGradientBrush(new RadialGradientBrushProperties
                {
                    Center = new Vector2(x0 + g * 0.72f, y0 + g * 0.78f), GradientOriginOffset = Vector2.Zero,
                    RadiusX = g * 0.30f, RadiusY = g * 0.26f,
                }, stops))
                    ctx.FillEllipse(new Ellipse { Point = new Vector2(x0 + g * 0.72f, y0 + g * 0.78f), RadiusX = g * 0.30f, RadiusY = g * 0.26f }, br);

                ctx.Transform = baseTf;
            }
        }

        // New-message attention dot: a small pulsing red disc hugging the icon's
        // top-right corner when any of this app's windows is flashing for attention
        // (mirrors the system taskbar's top-right unread badge, parity with the WPF
        // RadialIcon AttentionBadge anchored Right/Top).
        if (s.Running && _flashKeys.Contains(s.IconKey))
        {
            float d = Math.Clamp(g * 0.10f, 4.5f, 9f) * _badgePulse * scale;
            float bx = cx + (half - d) * scale, by = cy - (half - d) * scale;
            using (var glow = ctx.CreateSolidColorBrush(Col(0x55, 0xFF, 0x3B, 0x30)))
                ctx.FillEllipse(new Ellipse { Point = new Vector2(bx, by), RadiusX = d * 0.78f, RadiusY = d * 0.78f }, glow);
            using (var dot = ctx.CreateSolidColorBrush(Col(0xFF, 0xFF, 0x3B, 0x30)))
                ctx.FillEllipse(new Ellipse { Point = new Vector2(bx, by), RadiusX = d * 0.5f, RadiusY = d * 0.5f }, dot);
        }
    }

    /// <summary>Floating name label centred just below the magnified focal icon
    /// (mirrors the WPF glass dock's <c>ShowGlassHoverLabel</c>: barely-there dark tint,
    /// 7px radius, light text). The icon zooms about its centre, so its visible bottom
    /// sits at center + gIcon/2 × scale.</summary>
    private void DrawHoverLabel(ID2D1DeviceContext ctx, in IconSlot s, float scale, float opacity = 1f)
    {
        if (_labelFormat == null || string.IsNullOrEmpty(s.Name) || opacity <= 0.01f)
            return;
        float zoomedHalf = _gIcon / 2f * scale;
        float h = _labelFontPx + 10f;
        float w = MeasureLabelWidth(s.Name);
        float lx = s.Center.X, ly = s.Center.Y + zoomedHalf + 2f + h / 2f;
        // Pixel-snap the label centre to the device grid so the name stays crisp while the
        // focal icon is still magnifying — a fractional, per-frame-moving text origin renders
        // soft/shimmery until it settles (the "blurry at first" artefact).
        lx = (float)(Math.Round(lx * _dpi) / _dpi);
        ly = (float)(Math.Round(ly * _dpi) / _dpi);
        var rect = new Vortice.Mathematics.Rect(lx - w / 2f, ly - h / 2f, w, h);
        // Saturn uses the icon's built-in dark pill (Background #261A1A1A, the
        // RadialIcon LabelChrome); glass uses a barely-there tint (#051A1A1A, the
        // floating ShowGlassHoverLabel). Match the per-theme opacity so the saturn
        // label reads as a solid name pill rather than near-transparent.
        byte bgA = _saturn ? (byte)0x26 : (byte)0x05;
        using (var bg = ctx.CreateSolidColorBrush(Col((byte)(bgA * opacity), 0x1A, 0x1A, 0x1A)))
            ctx.FillRoundedRectangle(new RoundedRectangle { Rect = rect, RadiusX = 7f, RadiusY = 7f }, bg);
        // 3-D raised lettering: dark offset copies behind the light text give the name
        // depth and a legibility halo, mirroring the WPF DropShadowEffect (black, depth
        // 1.4, direction 315° → a ~1px down-right offset, plus a soft second copy).
        using (var halo = ctx.CreateSolidColorBrush(Col((byte)(0xE6 * opacity), 0, 0, 0)))
        {
            ctx.DrawText(s.Name, _labelFormat, new Vortice.Mathematics.Rect(rect.X + 1f, rect.Y + 1.2f, rect.Width, rect.Height), halo);
            ctx.DrawText(s.Name, _labelFormat, new Vortice.Mathematics.Rect(rect.X - 0.6f, rect.Y + 0.5f, rect.Width, rect.Height), halo);
        }
        using (var ink = ctx.CreateSolidColorBrush(Col((byte)(0xF2 * opacity), 0xFF, 0xFF, 0xFF)))
            ctx.DrawText(s.Name, _labelFormat, rect, ink);
    }

    private string? _labelMeasureName;
    private float _labelMeasureW;
    /// <summary>Tight hover-label width = the name's ACTUAL measured text width (DirectWrite)
    /// plus a fixed inset, cached by name. Replaces a char-count × per-char-width estimate
    /// that over-padded short / Latin-heavy names, so every label now has a consistent margin
    /// regardless of how wide its individual glyphs are.</summary>
    private float MeasureLabelWidth(string name)
    {
        if (name == _labelMeasureName)
            return _labelMeasureW;
        float tw = name.Length * _labelFontPx * 0.7f;   // fallback if measuring fails
        if (_dwrite != null && _labelFormat != null)
        {
            try
            {
                using var tl = _dwrite.CreateTextLayout(name, _labelFormat, 10000f, 100f);
                tw = tl.Metrics.Width;
            }
            catch { }
        }
        _labelMeasureName = name;
        _labelMeasureW = Math.Max(48f, tw + 18f);
        return _labelMeasureW;
    }

    // ---- Hover window-thumbnail preview (parity with the WPF dock) -----------
    // The polished thumbnail popup (live capture, caching, per-tile close button,
    // minimized / no-preview fallbacks) lives in WindowPreviewPopup, which anchors to
    // a WPF FrameworkElement. The GPU dock has no per-icon WPF visuals, so a tiny,
    // transparent, click-through anchor window is parked over the hovered icon and
    // used as the popup's placement target. A single popup is reused.

    /// <summary>Called every Tick with the slot under the cursor (or -1). Drives the
    /// reusable thumbnail popup: re-anchors and re-enters on a slot change, leaves on
    /// exit. The popup's own dwell timers handle the open/close delays.</summary>
    private void DrivePreview(int hover)
    {
        if (hover == _prevHover)
        {
            // Back on the source icon — drop any pending switch to a neighbour.
            _previewSwitchTimer?.Stop();
            _pendingPreviewHover = -2;
            return;
        }
        // A right-click menu is anchored to one icon; moving the pointer onto a different
        // icon dismisses it (parity with WPF / standard menu behaviour).
        if (_slotMenu != null && hover != _menuIdx)
            CloseSlotMenu();

        // Hover-intent: while a preview is OPEN, moving onto a DIFFERENT icon does not switch
        // it immediately. Steering the cursor onto the floating preview usually has to cross a
        // neighbouring icon first — especially in the Saturn ring layout where the preview sits
        // over other icons — and an instant switch there closes the preview the user is reaching
        // for. Defer the switch by a short grace and commit only on a deliberate DWELL: a quick
        // (or merely continuous) transit across an icon never lands on it when the timer fires,
        // so it is ignored. Moving off icons (hover < 0, e.g. onto the preview popup itself)
        // cancels the pending switch and hands off to the popup's own pointer-grace.
        // The cursor is RESTING on the floating preview itself: in the ring layout the preview
        // overlaps icons, so the geometric hover of the icon underneath is not a real icon hover.
        // Never switch or close the preview while the pointer is genuinely on it.
        if (_preview is { IsOpen: true, PointerInPopup: true })
        {
            _previewSwitchTimer?.Stop();
            _pendingPreviewHover = -2;
            return;
        }

        bool previewOpen = _preview is { IsOpen: true } && _prevHover >= 0;
        if (previewOpen && hover >= 0)
        {
            _pendingPreviewHover = hover;
            EnsurePreviewSwitchTimer();
            _previewSwitchTimer!.Stop();
            _previewSwitchTimer!.Start();
            return;
        }

        _previewSwitchTimer?.Stop();
        _pendingPreviewHover = -2;
        CommitPreviewHover(hover);
    }

    private void EnsurePreviewSwitchTimer()
    {
        if (_previewSwitchTimer != null)
            return;
        _previewSwitchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PreviewSwitchGraceMs) };
        _previewSwitchTimer.Tick += (_, _) =>
        {
            _previewSwitchTimer!.Stop();
            int target = _pendingPreviewHover;
            _pendingPreviewHover = -2;
            // Commit only if the cursor is genuinely still settled on that icon (a deliberate
            // hover), not just passing through (in which case _hover has already moved on).
            if (target >= 0 && _hover == target && _shown && !_dragging
                && !(_preview?.PointerInPopup ?? false))
                CommitPreviewHover(target);
        };
    }

    private void CommitPreviewHover(int hover)
    {
        if (_prevHover >= 0)
            _preview?.OnPointerLeave();
        _prevHover = hover;
        if (hover < 0 || hover >= _slots.Count)
            return;
        var s = _slots[hover];
        if (s.Entry == null || string.IsNullOrWhiteSpace(s.Entry.Path))
            return;
        var path = s.Entry.Path; var args = s.Entry.Arguments; var name = s.Entry.Name;
        var excl = SiblingShellNames(s.Entry);
        _previewSource = () => WindowPreviewService.GetWindowsForEntry(path, args, name, excl);
        EnsureAnchor();
        if (_preview == null || _anchorWin == null)
            return;   // anchor/popup not ready (e.g. creation failed) — skip this hover
        AnchorOverSlot(s);
        _preview.Placement = PreviewPlacement.Above;   // main dock is bottom-anchored
        _preview.ExtraTopLift = 18;                    // raise the preview a little above the icon
        _preview.OnPointerEnter();
    }

    private void EnsureAnchor()
    {
        // Use _preview (created last) as the "fully initialised" flag, not _anchorWin:
        // if a previous attempt assigned _anchorWin but then threw before creating
        // _preview, keying off _anchorWin would skip re-creation forever and leave
        // _preview null, NRE-ing every frame in DrivePreview.
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
            Log.Warn("MainDockGpu", "preview anchor init failed: " + ex.Message);
            // Roll back any half-built state so a later hover can retry cleanly.
            try { _anchorWin?.Close(); } catch { }
            _anchorWin = null;
            _anchorEl = null;
            _preview = null;
        }
    }

    /// <summary>Positions the anchor window so its element overlaps the hovered icon's
    /// glyph box (screen DIPs), so the popup centres over the icon. The window sits at
    /// _winX*_dpi and the icon centre is _winX + s.Center in screen DIPs.</summary>
    private void AnchorOverSlot(in IconSlot s)
    {
        if (_anchorWin == null)
            return;
        double size = _gIcon;
        _anchorWin.Width = size;
        _anchorWin.Height = size;
        _anchorWin.Left = _winX + s.Center.X - size / 2.0;
        _anchorWin.Top = _winY + s.Center.Y - size / 2.0;
    }

    private void ClosePreview()
    {
        _previewSwitchTimer?.Stop();
        _pendingPreviewHover = -2;
        _preview?.Close();
        _prevHover = -1;
    }

    // ---- Stage D: interaction (click-launch, drag reorder / drag-out delete,
    //       right-click menu, drop-files-to-pin) -------------------------------

    /// <summary>Re-maps a mouse message that landed on the drop-shim overlay (in SCREEN
    /// pixels) into this dock's client space and runs it through <see cref="HandleMessage"/>,
    /// so a click on the shim behaves exactly as a click on the dock (the dock's SetCapture
    /// then routes the rest of a drag straight here, bypassing the shim).</summary>
    internal (bool handled, IntPtr result) ForwardShimInput(uint msg, IntPtr wParam, int screenX, int screenY)
    {
        IntPtr lParam;
        if (msg == 0x020A) // WM_MOUSEWHEEL keeps screen coords in lParam
            lParam = (IntPtr)(((screenY & 0xFFFF) << 16) | (screenX & 0xFFFF));
        else
        {
            int cx = screenX - (int)Math.Round(_winX * _dpi);
            int cy = screenY - (int)Math.Round(_winY * _dpi);
            lParam = (IntPtr)(((cy & 0xFFFF) << 16) | (cx & 0xFFFF));
        }
        bool handled = HandleMessage(msg, wParam, lParam, out var res);
        return (handled, res);
    }

    /// <summary>Re-raises the drop-shim above the dock when (and only when) the bare
    /// composition dock has come to cover it at the drop centre. Throttled to ~150ms so it
    /// runs cheaply from the render Tick without disturbing legitimately-higher windows.</summary>
    private void EnsureShimTopmost()
    {
        if (_dropShim == null || !_visible) return;
        long now = Environment.TickCount64;
        if (now - _shimCheckMs < 150) return;
        _shimCheckMs = now;
        // _dropShim.Show() is Win32 on a UI-thread-owned window, so marshal the check off the
        // render thread (the 150ms throttle above keeps this to a few BeginInvokes/sec).
        OnUi(() =>
        {
            try
            {
                if (_dropShim == null || !_visible) return;
                int cx = (int)Math.Round((_winX + _slabX + _slabW / 2f) * _dpi);
                int cy = (int)Math.Round((_winY + _slabY + _slabH / 2f) * _dpi);
                IntPtr top = WindowFromPoint(new POINT { X = cx, Y = cy });
                if (top == _hwnd)   // the dock itself is covering the shim → re-raise the shim
                    _dropShim.Show();
            }
            catch { }
        });
    }
    private long _shimCheckMs;

    /// <summary>Positions the drop-shim overlay over the REAL visible/interactable dock
    /// region (slab / ring box + icon-pop margin), not the whole composition window. The
    /// larger full-window shim blocked drags starting from desktop icons that happened to sit
    /// under the dock window's transparent headroom: the press landed on the shim before the
    /// icon could be picked up. Keeping the shim tight to the actual dock leaves surrounding
    /// desktop pixels reachable while still covering every place a drop should register.</summary>
    private void SyncShim()
    {
        if (_dropShim == null) return;
        // Cover the shim over the SAME area the window region carves out (the full mouse-solid
        // dock box incl. magnify/label headroom). If the shim were smaller, the headroom band
        // would be a topmost dock-window area with no drop target → the OS shows a "no-drop"
        // cursor there. Matching them means every solid dock pixel accepts the drag.
        var (left, top, right, bottom) = ContentRect();
        int sx = (int)Math.Round((_winX + left) * _dpi);
        int sy = (int)Math.Round((_winY + top) * _dpi);
        int sw = Math.Max(1, (int)Math.Ceiling((right - left) * _dpi));
        int sh = Math.Max(1, (int)Math.Ceiling((bottom - top) * _dpi));
        _dropShim.SetBounds(sx, sy, sw, sh);
        ApplyWindowRegion();
    }

    /// <summary>The dock's visible/interactive box in window-local DIPs (slab / ring disc plus
    /// magnification + hover-label headroom). Shared by the window region carve and the drop
    /// shim so the mouse-solid area and the drop-accepting area are identical.</summary>
    private (float left, float top, float right, float bottom) ContentRect()
    {
        float mx = _gIcon * 1.0f;
        float mTop = _saturn ? _gIcon * 1.0f : _gIcon * 3.2f;
        float mBot = _saturn ? _gIcon * 1.0f : _gIcon * 0.6f;
        float left = Math.Max(0f, _slabX - mx);
        float top = Math.Max(0f, _slabY - mTop);
        float right = Math.Min(_winW, _slabX + _slabW + mx);
        float bottom = Math.Min(_winH, _slabY + _slabH + mBot);
        return (left, top, right, bottom);
    }

    /// <summary>Carves the dock's composition window down to just the visible content (the
    /// slab / ring disc plus magnification + hover-label headroom) with a window region.
    ///
    /// The window is deliberately sized FAR larger than the content — Saturn's drag headroom
    /// makes it span almost the whole screen — and that huge transparent surplus, being part
    /// of a topmost window, swallowed mouse presses meant for the desktop icons sitting under
    /// it: you literally couldn't pick up a desktop icon to drag it into the dock because the
    /// invisible dock window was on top of it. <see cref="WS_EX_TRANSPARENT"/> can't fix this
    /// (it's a no-op on a NOREDIRECTIONBITMAP composition window, which can't be layered), and
    /// the per-message WM_NCHITTEST→HTTRANSPARENT passthrough is racy: when the render thread
    /// is briefly busy the hit-test reply is late and the OS treats the surplus as opaque.
    /// A window region removes the surplus from the window entirely — OS-level, render-thread-
    /// independent passthrough, exactly like the WPF dock's per-pixel-alpha hit-testing.</summary>
    private void ApplyWindowRegion()
    {
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            // Keep generous headroom so magnified icons / hover labels are never clipped
            // (see ContentRect); the drop shim covers this exact box so there is no perimeter
            // band of solid-but-no-drop-target window.
            var (left, top, right, bottom) = ContentRect();
            int rx = (int)Math.Floor(left * _dpi);
            int ry = (int)Math.Floor(top * _dpi);
            int rr = (int)Math.Ceiling(right * _dpi);
            int rb = (int)Math.Ceiling(bottom * _dpi);
            IntPtr rgn = CreateRectRgn(rx, ry, rr, rb);
            // SetWindowRgn takes ownership of the region (don't delete it) and frees any
            // previously-set region. bRedraw: true so the new clip takes effect immediately.
            SetWindowRgn(_hwnd, rgn, true);
        }
        catch (Exception ex) { Log.Warn("MainDockGpu", "ApplyWindowRegion failed: " + ex.Message); }
    }

    /// <summary>True when a window-local point is within the dock's real drop region (the
    /// slab / ring box plus the icon-pop margin) — the same area the NCHITTEST claims, hence
    /// where an OLE drop actually registers.</summary>
    private bool InDropRegion(float lx, float ly)
    {
        float m = _gIcon * 0.5f;
        return lx >= _slabX - m && lx <= _slabX + _slabW + m
            && ly >= _slabY - m && ly <= _slabY + _slabH + m;
    }

    /// <summary>Lifts the icon at <paramref name="idx"/> into an independent topmost
    /// desktop overlay (DragGhostWindowGpu) pinned to the cursor, so the dragged icon roams
    /// the whole desktop and never clips at the dock window's edge (parity with the WPF
    /// dock's StartDragGhost). Falls back to the in-window draw if the icon has no bitmap.</summary>
    private void StartDragGhost(int idx)
    {
        if (idx < 0 || idx >= _slots.Count) { EndDragGhost(); return; }
        var img = _slots[idx].Image;
        if (img == null) { EndDragGhost(); return; }   // no bitmap (text icon) — keep the in-window draw
        try
        {
            long t0 = Stopwatch.GetTimestamp();
            double dip = _gIcon * 1.12;                       // matches the lifted drag size
            BitmapSource src = img;
            int targetPx = Math.Max(1, (int)Math.Round(dip * _dpi));
            // The GPU ghost sizes its window to the snapshot's PIXEL dimensions and draws
            // it 1:1, so feed it a bitmap already scaled to the display size (not the icon's
            // native full-res), otherwise the ghost renders oversized + mis-centred.
            if (src.PixelWidth != targetPx)
            {
                double sx = targetPx / (double)src.PixelWidth, sy = targetPx / (double)src.PixelHeight;
                var scaled = new TransformedBitmap(src, new System.Windows.Media.ScaleTransform(sx, sy));
                scaled.Freeze();
                src = scaled;
            }
            // Pass the DIP size that makes the ghost's internal scale == _dpi, so MoveCenterTo
            // maps a DIP cursor point to the correct physical centre.
            double dipW = src.PixelWidth / _dpi, dipH = src.PixelHeight / _dpi;
            // Reuse one persistent ghost host across drags (created lazily on the first drag):
            // rebuilding a whole CompositionHost — its own DComposition device + swap chain +
            // window — per drag spun up transient driver threads / windows that piled up during
            // rapid repeated drags and progressively degraded the frame rate. SetSnapshot just
            // re-uploads the icon bitmap on the reused host.
            if (_ghost == null) _ghost = new DragGhostWindowGpu(src, dipW, dipH);
            else _ghost.SetSnapshot(src, dipW, dipH);
            MoveDragGhost(_dragX, _dragY);
            _ghost.Show();
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            DragPerfStats.GhostCreated(_dragDiagSession, "gpu", ms);
        }
        catch (Exception ex) { Log.Warn("MainDockGpu", "drag ghost start failed: " + ex.Message); try { _ghost?.Hide(); } catch { } }
    }

    /// <summary>Moves the drag ghost to the window-local cursor point and fades it while the
    /// cursor is outside the drop region (mirrors WPF GhostOpacity = deleteZone ? 0.4 : 1).</summary>
    private void MoveDragGhost(float lx, float ly)
    {
        if (_ghost == null) return;
        _ghost.MoveCenterTo(_winX + lx, _winY + ly);
        _ghost.GhostOpacity = InDropRegion(lx, ly) ? 1.0 : 0.4;
    }

    private void EndDragGhost()
    {
        // Hide (not destroy) the ghost so its GPU host is reused on the next drag; the host is
        // fully torn down only on Dispose (see DisposeGhost).
        try { _ghost?.Hide(); } catch { }
    }

    /// <summary>Tears down the persistent drag-ghost host. Called from Dispose; per-drag end uses
    /// <see cref="EndDragGhost"/> which only hides it.</summary>
    private void DisposeGhost()
    {
        try { _ghost?.Close(); } catch { }
        _ghost = null;
    }

    /// <summary>Routes the window messages this dock cares about. Returns true (with
    /// <paramref name="result"/>) when handled; false defers to DefWindowProc.</summary>
    private bool HandleMessage(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        switch (msg)
        {
            case WM_NCHITTEST:
            {
                // lParam = SCREEN coords. Inside the glass slab → grab the click;
                // the empty headroom around it passes through to the desktop.
                int sx = unchecked((short)((long)lParam & 0xFFFF));
                int sy = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
                float lx = (float)(sx / _dpi - _winX), ly = (float)(sy / _dpi - _winY);
                float m = _gIcon * 0.5f;
                // The bottom edge uses a tight margin so the slab's grab region never
                // reaches into the bottom side dock sitting just below it (otherwise the
                // side dock's Polaris toggle tile gets eaten by this topmost window).
                float bottomM = _saturn ? m : Math.Min(m, _effIcon * 0.12f);
                bool inside = lx >= _slabX - m && lx <= _slabX + _slabW + m
                           && ly >= _slabY - m && ly <= _slabY + _slabH + bottomM;
                result = inside ? HTCLIENT : HTTRANSPARENT;
                return true;
            }
            case WM_LBUTTONDOWN:
            {
                (float lx, float ly) = ClientDip(lParam);
                if (TryBarPress(lx, ly))
                    return true;
                float hitR = _saturn ? _planetHitR : _gearR;
                bool gear = Vector2.Distance(new Vector2(lx, ly), _gearC) <= hitR + 2f;
                int hit = gear ? -1 : HitSlot(lx, ly);
                // Hot input path: these are simple scalar writes (float/int/bool) and on the
                // 64-bit target machine are atomic enough for a render-thread consumer to read
                // without taking the giant whole-frame _stateLock. Contending with that lock on
                // every drag mouse-down/move was causing perceptible input stickiness even while
                // the render thread itself stayed at 140+ fps.
                _pressX = lx; _pressY = ly; _dragX = lx; _dragY = ly;
                _pressGear = gear;
                _pressIdx = hit;
                _dragging = false;
                if (_pressIdx >= 0 || _pressGear)
                    SetCapture(_hwnd);
                if (_pressIdx >= 0) RequestRender();   // animate the press-shrink to rest
                return true;
            }
            case WM_MOUSEMOVE:
            {
                (float lx, float ly) = ClientDip(lParam);
                if (_barDrag)
                {
                    BarDragTo(ly);
                    RequestRender();
                    return true;
                }
                if (_pressIdx < 0)
                    return false;
                bool startDrag = false;
                _dragX = lx; _dragY = ly;
                if (!_dragging)
                {
                    float mdx = lx - _pressX, mdy = ly - _pressY;
                    if (mdx * mdx + mdy * mdy > DragThreshold * DragThreshold)
                    {
                        _dragging = true;
                        _dragArrangeLastMs = Environment.TickCount64;   // seed the time-based reflow ease
                        startDrag = true;
                    }
                }
                if (startDrag)
                {
                    _dragDiagSession = DragPerfStats.Begin("main", _slots.Count, UseRenderThread, true);
                    GlassDragActiveChanged?.Invoke(true);   // keep the side dock shown as a drop target
                    StartDragGhost(_pressIdx);   // lift the icon into an independent desktop overlay
                }
                if (_dragging)
                {
                    MoveDragGhost(lx, ly);
                    // Default path: keep neighbours in step between ticks + repaint inline. On
                    // the render-thread path the loop runs AdvanceDragArrange + draws each frame.
                    if (!UseRenderThread)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        AdvanceDragArrange();
                        _dragArrangeDiagMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                        Render();
                    }
                }
                return true;
            }
            case WM_MOUSEWHEEL:
            {
                if (_saturn || !_scrollable)
                    return false;
                int delta = unchecked((short)(((long)wParam >> 16) & 0xFFFF));
                _glassScrollTarget = Math.Clamp(_glassScrollTarget - _glassCellH * (delta / 120.0), 0, _glassScrollMax);
                RequestRender();
                return true;
            }
            case WM_LBUTTONUP:
            {
                ReleaseCapture();
                if (_barDrag)
                {
                    _barDrag = false;
                    RequestRender();
                    return true;
                }
                int idx = _pressIdx;
                bool wasDrag = _dragging;
                bool gear = _pressGear;
                (float lx, float ly) = ClientDip(lParam);
                _pressIdx = -1;
                _dragging = false;
                _pressGear = false;
                EndDragGhost();   // dismiss the desktop overlay before committing the drop
                if (gear)
                {
                    float hitR = _saturn ? _planetHitR : _gearR;
                    if (Vector2.Distance(new Vector2(lx, ly), _gearC) <= hitR + 2f)
                        RequestOpenSettings?.Invoke();
                }
                else if (idx >= 0 && idx < _slots.Count)
                {
                    if (!wasDrag) ClickSlot(idx);
                    else DropSlot(idx, lx, ly);
                }
                if (_hwnd != IntPtr.Zero)
                    RequestRender();
                if (wasDrag)
                {
                    DragPerfStats.End(_dragDiagSession, "mouse-up");
                    _dragDiagSession = 0;
                    _dragArrangeDiagMs = 0;
                }
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
                    // DragQueryPoint gives a client-relative point in physical pixels;
                    // convert to window-local DIPs to match the icon-slot geometry.
                    HandleDropFiles(paths, (float)(pt.X / _dpi), (float)(pt.Y / _dpi));
                }
                catch (Exception ex) { Log.Warn("MainDockGpu", "drop-in failed: " + ex.Message); }
                finally { DragFinish(hDrop); }
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

    /// <summary>Index of the icon under the (window-local DIP) point, or -1.</summary>
    private int HitSlot(float lx, float ly)
    {
        // Restrict hits to the visible band and map the pointer into grid space
        // (icons draw at Center.Y - scroll) when the glass grid is scrolled.
        if (_scrollable)
        {
            if (ly < _gvY || ly > _gvY + _gvH)
                return -1;
            ly += (float)_glassScroll;
        }
        int best = -1; float bestD = _gIcon * 0.6f;
        for (int i = 0; i < _slots.Count; i++)
        {
            var c = _slots[i].Center;
            float d = MathF.Abs(lx - c.X) + MathF.Abs(ly - c.Y);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>Reading-order (row first, then column) insertion index for the dragged
    /// icon <paramref name="src"/> at grid-space point (<paramref name="px"/>,
    /// <paramref name="py"/>). Mirrors WPF <c>ComputeGridTarget</c>: a slot is "before"
    /// the cursor if it sits on an earlier row band (one cell-pitch tall) or, within the
    /// same band, to the left of the cursor. The dragged icon is removed first, so its
    /// own "before" count is discounted. This makes vertical drags switch rows reliably
    /// instead of snapping to the raw nearest centre.</summary>
    private int ComputeGridTarget(float px, float py, int src)
    {
        int n = _slots.Count;
        if (n == 0) return 0;
        float half = (float)(_glassCellH / 2.0);
        int before = 0; bool srcBefore = false;
        for (int i = 0; i < n; i++)
        {
            var s = _slots[i].Center;
            float dy = py - s.Y;
            bool isBefore = dy > half ? true : dy < -half ? false : px > s.X;
            if (isBefore) { before++; if (i == src) srcBefore = true; }
        }
        int tgt = before;
        if (src >= 0 && src < n && srcBefore) tgt--;
        return Math.Clamp(tgt, 0, n - 1);
    }

    /// <summary>The resident count a glass drop would commit: dragging an icon into
    /// the framed resident rows promotes it (+1), dragging a resident icon out of
    /// them demotes it (-1). Mirrors WPF <c>ProspectiveResidentCount</c>. <paramref
    /// name="src"/> is the dragged entry's index before the move; <paramref
    /// name="dropY"/> is scroll-adjusted (grid space, like <see cref="_resY"/>).</summary>
    private int ProspectiveResidentCount(int src, float lx, float dropY)
    {
        int resident = DockSync.ResidentCount(_config);
        // Classify by the resident-row Y band only (parity with WPF GlassRowAt /
        // ProspectiveResidentCount, which keys off the row and not the X): an icon
        // dragged within the resident rows promotes even if it drifts horizontally
        // past the framed icons, and only leaving the rows vertically demotes it.
        bool inResident = _hasResident
            && dropY >= _resY && dropY <= _resY + _resH;
        bool wasResident = src >= 0 && src < resident;
        if (inResident && !wasResident && resident < DockSync.MaxResidentCount)
            return resident + 1;
        if (!inResident && wasResident && resident > 1)
            return resident - 1;
        return resident;
    }

    /// <summary>Glass slot centres (window-local DIP) for a hypothetical resident
    /// count, used so a mid-drag reflow animates to the layout the drop will
    /// actually produce rather than the stale current one (mirrors WPF
    /// <c>ComputeGlassSlots</c>). Returns null if the prospective count matches the
    /// current layout (then the live <see cref="_slots"/> positions are used).</summary>
    private Vector2[]? ComputeGlassSlots(int residentCount)
    {
        var scaled = new AppSettings
        {
            IconSize = _effIcon,
            Ring0Count = Math.Clamp(residentCount, 0, _config.Apps.Count),
        };
        var pts = ((LiquidGlassTheme)ThemeRegistry.Get("liquidglass"))
            .ComputeSlots(_config.Apps.Count, new Point(_winW / 2.0, _gridCenterY), scaled, out _);
        var arr = new Vector2[Math.Min(pts.Count, _slots.Count)];
        for (int i = 0; i < arr.Length; i++)
            arr[i] = new Vector2((float)pts[i].X, (float)pts[i].Y);
        return arr;
    }

    /// <summary>Maps each entry index to the slot it should occupy when the dragged
    /// entry <paramref name="src"/> is reinserted at <paramref name="tgt"/> (mirrors
    /// WPF <c>GridArrangement</c>), producing the neighbour "make room" reflow.</summary>
    private int[] GridArrangement(int src, int tgt)
    {
        int n = _slots.Count;
        var order = new List<int>(n);
        for (int i = 0; i < n; i++) order.Add(i);
        if (src >= 0 && src < n)
        {
            order.Remove(src);
            order.Insert(Math.Clamp(tgt, 0, order.Count), src);
        }
        var slotOfEntry = new int[n];
        for (int slot = 0; slot < order.Count; slot++)
            slotOfEntry[order[slot]] = slot;
        return slotOfEntry;
    }

    /// <summary>Time-based neighbour reflow while dragging: recomputes the grid insertion
    /// target from the live cursor and eases each non-dragged icon's offset toward the slot
    /// it would occupy once the dragged icon is reinserted there (mirrors WPF GridArrangement
    /// "make room"). Called from both Tick and WM_MOUSEMOVE so the gap keeps pace with the
    /// pointer instead of stuttering on the 16ms timer. Returns the max per-frame delta so
    /// the caller can keep rendering until the reflow settles.</summary>
    private float AdvanceDragArrange()
    {
        if (_waveOffX.Length == 0)
            return 0f;
        long now = Environment.TickCount64;
        float dt = (now - _dragArrangeLastMs) / 1000f;
        _dragArrangeLastMs = now;
        if (dt <= 0f)
            return 0f;
        if (dt > 0.1f) dt = 0.1f;   // clamp after a stall so we don't snap
        float k = 1f - MathF.Exp(-dt / 0.045f);   // tau 45ms

        // Per-icon target offsets while dragging: glass reflows in reading-order grid
        // slots; saturn reflows along its two rings (re-spaced for the prospective
        // inner-ring count) so neighbours make room as the dragged icon orbits.
        int[]? arr = null;          // glass: entry -> slot over the current layout
        Vector2[]? prospSlots = null; // glass: prospective slot centres when the resident block resizes
        int[]? satSlot = null;      // saturn: entry -> flat slot
        int satN = 0, satR0 = 0;
        if (_dragging && _pressIdx >= 0 && _pressIdx < _slots.Count)
        {
            if (_saturn)
            {
                var (ring, pos) = ComputeSaturnDragTarget(_dragX, _dragY, _pressIdx);
                (satSlot, satR0) = ComputeSaturnArrangement(_pressIdx, ring, pos);
                satN = _slots.Count;
            }
            else
            {
                float dropY = _scrollable ? _dragY + (float)_glassScroll : _dragY;
                int tgt = ComputeGridTarget(_dragX, dropY, _pressIdx);
                arr = GridArrangement(_pressIdx, tgt);
                // When the drop would resize the resident block, reflow neighbours to
                // the prospective layout so an icon dragged in/out of the frame does
                // not push residents across the stale resident gap (parity with WPF
                // ReflowGrid's prospectiveResident path).
                int prosp = ProspectiveResidentCount(_pressIdx, _dragX, dropY);
                if (prosp != DockSync.ResidentCount(_config))
                    prospSlots = ComputeGlassSlots(prosp);
            }
        }

        float maxDelta = 0f;
        for (int i = 0; i < _slots.Count && i < _waveOffX.Length; i++)
        {
            float tx = 0f, ty = 0f;
            if (i != _pressIdx)
            {
                if (arr != null && i < arr.Length)
                {
                    Vector2 target = prospSlots != null && arr[i] < prospSlots.Length
                        ? prospSlots[arr[i]]
                        : _slots[arr[i]].Center;
                    Vector2 d = target - _slots[i].Center;
                    tx = d.X; ty = d.Y;
                }
                else if (satSlot != null && i < satSlot.Length)
                {
                    Vector2 d = SaturnSlotCenter(satN, satR0, satSlot[i]) - _slots[i].Center;
                    tx = d.X; ty = d.Y;
                }
            }
            _waveOffX[i] += (tx - _waveOffX[i]) * k;
            _waveOffY[i] += (ty - _waveOffY[i]) * k;
            maxDelta = Math.Max(maxDelta, Math.Abs(tx - _waveOffX[i]));
            maxDelta = Math.Max(maxDelta, Math.Abs(ty - _waveOffY[i]));
        }
        return maxDelta;
    }

    /// <summary>Saturn: window-local centre of flat slot <paramref name="flatSlot"/> in
    /// a layout of <paramref name="n"/> icons with <paramref name="r0"/> on the inner
    /// ring (mirrors WPF SlotPositionsFor / RingPoint).</summary>
    private Vector2 SaturnSlotCenter(int n, int r0, int flatSlot)
    {
        r0 = Math.Clamp(r0, 1, Math.Max(1, n));
        int ring1 = n - r0;
        bool inner = flatSlot < r0;
        int k = inner ? flatSlot : flatSlot - r0;
        int cnt = inner ? r0 : ring1;
        float radius = inner ? _sg.InnerRadius : _sg.OuterRadius;
        double angle = -Math.PI / 2 + 2 * Math.PI * k / Math.Max(1, cnt);
        float sx = _sg.Cx + (float)(radius * Math.Cos(angle));
        float sy = _sg.Cy + (float)(radius * Math.Sin(angle) * RingTiltY);
        return new Vector2(sx, sy);
    }

    /// <summary>Saturn: which ring (0 inner / 1 outer) and angular slot the dragged
    /// icon <paramref name="src"/> targets at window-local point (<paramref name="px"/>,
    /// <paramref name="py"/>). Port of WPF <c>ComputeDragTarget</c>.</summary>
    private (int ring, int pos) ComputeSaturnDragTarget(float px, float py, int src)
    {
        int n = _config.Apps.Count;
        int r0 = EffectiveRing0Count(n);
        int o0 = (src >= 0 && src < r0) ? r0 - 1 : r0;   // other inner-ring icons
        int m = Math.Max(0, n - 1);
        int ring1Others = m - o0;

        double dx = px - _sg.Cx, dy = py - _sg.Cy;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        double ringMid = (_sg.InnerRadius + _sg.OuterRadius) / 2.0;
        int ring = dist <= ringMid ? 0 : 1;

        // Respect caps: redirect to the other ring if the chosen one is full.
        if (ring == 0 && o0 + 1 > Ring0Cap) ring = 1;
        if (ring == 1 && ring1Others + 1 > Ring1Cap) ring = 0;

        int slotsAfter = ring == 0 ? o0 + 1 : ring1Others + 1;
        double ang = Math.Atan2(dy, dx);
        double fromTop = ang + Math.PI / 2.0;   // 0 at 12 o'clock, clockwise
        fromTop = ((fromTop % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
        int pos = (int)Math.Round(fromTop / (2 * Math.PI) * slotsAfter);
        pos = Math.Clamp(pos, 0, Math.Max(0, slotsAfter - 1));
        return (ring, pos);
    }

    /// <summary>Saturn: maps each entry index to its flat slot when the dragged entry
    /// <paramref name="src"/> is reinserted into <paramref name="ring"/> at angular
    /// position <paramref name="pos"/>; also returns the new inner-ring count. Port of
    /// WPF <c>ComputeArrangement</c>.</summary>
    private (int[] slotOfEntry, int newR0) ComputeSaturnArrangement(int src, int ring, int pos)
    {
        int n = _config.Apps.Count;
        int r0 = EffectiveRing0Count(n);
        int srcRing = src < r0 ? 0 : 1;

        var inner = new List<int>();
        for (int i = 0; i < r0; i++) inner.Add(i);
        var outer = new List<int>();
        for (int i = r0; i < n; i++) outer.Add(i);

        int newR0;
        if (ring == srcRing)
        {
            // Same ring: shift only the shorter arc between the dragged icon's
            // current angular slot and the target so crossing 12 o'clock nudges
            // neighbours instead of rotating the whole ring.
            var seq = ring == 0 ? inner : outer;
            int len = seq.Count;
            int cur = seq.IndexOf(src);
            int tgt = Math.Clamp(pos, 0, Math.Max(0, len - 1));
            int[] newIdx = ShortestArcShift(len, cur, tgt);
            var newSeq = new int[len];
            for (int j = 0; j < len; j++) newSeq[newIdx[j]] = seq[j];
            seq.Clear(); seq.AddRange(newSeq);
            newR0 = r0;
        }
        else
        {
            // Cross ring: remove from the source ring and insert into the target.
            if (srcRing == 0) inner.Remove(src); else outer.Remove(src);
            var tgt = ring == 0 ? inner : outer;
            int insertAt = Math.Clamp(pos, 0, tgt.Count);
            tgt.Insert(insertAt, src);
            newR0 = inner.Count;
        }

        int[] slotOfEntry = new int[n];
        int slot = 0;
        foreach (int e in inner) slotOfEntry[e] = slot++;
        foreach (int e in outer) slotOfEntry[e] = slot++;
        return (slotOfEntry, newR0);
    }

    /// <summary>For a ring of <paramref name="len"/> slots, the new angular index of
    /// each current index when the icon at <paramref name="cur"/> moves to
    /// <paramref name="tgt"/>, shifting only the shorter arc by one. Port of WPF
    /// <c>ShortestArcShift</c>.</summary>
    private static int[] ShortestArcShift(int len, int cur, int tgt)
    {
        int[] newIdx = new int[Math.Max(0, len)];
        if (len <= 0) return newIdx;
        cur = Math.Clamp(cur, 0, len - 1);
        tgt = Math.Clamp(tgt, 0, len - 1);
        newIdx[cur] = tgt;

        int df = ((tgt - cur) % len + len) % len;   // forward steps cur -> tgt
        int db = len - df;                            // backward steps
        bool forward = df <= db;

        for (int j = 0; j < len; j++)
        {
            if (j == cur) continue;
            int ns = j;
            if (forward)
            {
                int rel = ((j - cur) % len + len) % len;   // 1..len-1
                if (rel >= 1 && rel <= df)
                    ns = (j - 1 + len) % len;
            }
            else
            {
                int relT = ((j - tgt) % len + len) % len;   // 0..len-1
                if (relT >= 0 && relT < db)
                    ns = (j + 1) % len;
            }
            newIdx[j] = ns;
        }
        return newIdx;
    }

    /// <summary>Launch scale at normalised time t∈[0,1]. The icon is already shrunk to rest
    /// while the button was held (see the press-shrink in Tick), so on release it just swells
    /// from rest up to the hover-magnified peak (the size the cursor-over-icon position gives)
    /// and holds it into the dismiss fade. A tiny settle portion covers the rare case where the
    /// press wasn't held long enough to fully de-magnify, easing any residual scale to rest first.</summary>
    private float PressScale(float t)
    {
        const float tSettle = 0.10f;        // brief de-magnify of any residual scale to rest
        const float enlargePeak = MagnifyPeak;  // swell back to the hover-magnified size
        if (t < tSettle)
        {
            float u = t / tSettle;
            float e = 1f - (1f - u) * (1f - u);              // QuadraticEaseOut: quick restore to rest
            return _pressFromScale + (1f - _pressFromScale) * e;
        }
        float v = (t - tSettle) / (1f - tSettle);
        float e2 = 1f - (1f - v) * (1f - v);                 // QuadraticEaseOut: grow to the launch peak
        return 1f + (enlargePeak - 1f) * e2;                 // 1 → enlargePeak, held into the fade
    }

    private void ClickSlot(int idx)
    {
        var s = _slots[idx];
        if (s.Entry == null)
            return;
        // The glyph was already shrunk to rest while the button was held, so the launch starts
        // from rest and simply grows up to the hover peak before the dock fades (the requested
        // "press shrinks, release re-magnifies, then launch"). Capturing rest also keeps the
        // 10% settle a no-op when the icon was held only briefly.
        _pressLaunchIdx = idx;
        _pressLaunchStart = Environment.TickCount64;
        _pressFromScale = 1f;
        _pressShrink = 0f;
        _launching = true;
        StartDriver();
        var entry = s.Entry;
        var hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PressLaunchMs) };
        hold.Tick += (_, _) =>
        {
            hold.Stop();
            // Keep _launching set so the wave loop pins the launched icon at the enlarged peak
            // (PressScale clamps t→1 = MagnifyPeak) through the dismiss fade — the icon freezes
            // at its biggest size instead of easing back to rest. Reset on the next summon.
            try { AppLauncher.Launch(entry, () => HidePanel()); }
            catch (Exception ex) { Log.Warn("MainDockGpu", "launch failed: " + ex.Message); }
        };
        hold.Start();
    }

    /// <summary>Drop after a drag: outside the slab → unpin; otherwise reorder to the
    /// grid slot nearest the drop point.</summary>
    private void DropSlot(int idx, float lx, float ly)
    {
        // Drag gesture finished — let the host retract the side-dock drop target.
        GlassDragActiveChanged?.Invoke(false);

        var s = _slots[idx];
        if (s.Entry == null) { RequestRender(); return; }

        // Dropping onto the left-edge side dock pins it there (the main-dock entry
        // stays); checked first so a drag toward the dock adds rather than deletes.
        if (DropToSideDock != null)
        {
            var screen = new Point(_winX + lx, _winY + ly);
            if (DropToSideDock(screen, s.Entry))
            {
                RequestRender();   // snap the dragged icon back into its slot
                return;
            }
        }

        // Dragged clear of the dock → unpin / delete. Saturn uses a RADIAL threshold
        // past the outer ring (mirrors WPF DeleteRadius / IsDeleteDrop), so corner
        // drops outside the circular disc count as outside; glass uses the slab rect.
        bool outside;
        if (_saturn)
        {
            float dx = lx - _sg.Cx, dy = ly - _sg.Cy;
            float delR = _sg.OuterRadius + _gIcon * 1.25f;
            outside = (dx * dx + dy * dy) > delR * delR;
        }
        else
        {
            float m = _gIcon * 0.4f;
            outside = lx < _slabX - m || lx > _slabX + _slabW + m
                   || ly < _slabY - m || ly > _slabY + _slabH + m;
        }
        if (outside)
        {
            UnpinPinned(s.Entry);
            return;
        }

        // Saturn: reorder on the two rings by nearest ring + angular slot (mirrors
        // WPF ComputeDragTarget / ComputeArrangement) so the icon lands where the
        // pointer is on the ring, not in glass reading order.
        if (_saturn)
        {
            var (ring, pos) = ComputeSaturnDragTarget(lx, ly, idx);
            var (slotOfEntry, newR0) = ComputeSaturnArrangement(idx, ring, pos);
            int sn = _config.Apps.Count;
            if (slotOfEntry.Length == sn && sn > 0)
            {
                var ordered = new AppEntry[sn];
                for (int i = 0; i < sn; i++) ordered[slotOfEntry[i]] = _config.Apps[i];
                _config.Apps.Clear();
                foreach (var a in ordered) _config.Apps.Add(a);
                _config.Settings.Ring0Count = Math.Clamp(newR0, 0, sn);
                PersistAndRelayout();
            }
            else RequestRender();
            return;
        }

        // Otherwise reorder using the same reading-order hit test as the live drag
        // preview so the icon lands at the grid cell the pointer is actually over.
        float dropY = _scrollable ? ly + (float)_glassScroll : ly;
        int tgt = ComputeGridTarget(lx, dropY, idx);

        // Resident promote/demote (parity with WPF UpdateResidentCountForDrop):
        // dropping an icon inside the framed resident rows grows the resident count
        // (up to the cap) and dragging a resident icon out of the frame shrinks it.
        // Uses the same prospective-count helper the live drag preview reflows to, so
        // the committed layout matches what the neighbours animated toward.
        int resident = DockSync.ResidentCount(_config);
        int newResident = ProspectiveResidentCount(idx, lx, dropY);

        bool changed = false;
        if (tgt >= 0 && tgt != idx && idx < _config.Apps.Count && tgt < _config.Apps.Count)
        {
            var e = _config.Apps[idx];
            _config.Apps.RemoveAt(idx);
            _config.Apps.Insert(tgt, e);
            changed = true;
        }
        if (newResident != resident)
        {
            _config.Settings.Ring0Count = newResident;
            changed = true;
        }
        if (changed)
            PersistAndRelayout();
        else
            RequestRender();
    }

    /// <summary>Pins shortcuts / executables dropped from Explorer / the desktop,
    /// inserting each at the grid/ring slot nearest the drop point (mirrors the WPF
    /// dock OnDropPanel: cursor-slot insertion + resident/inner-ring growth).</summary>
    private void HandleDropFiles(List<string> paths, float lx, float ly)
    {
        var entries = new List<AppEntry>();
        foreach (var p in paths)
        {
            try { var e = ShortcutResolver.CreateEntry(p); if (e != null && !string.IsNullOrWhiteSpace(e.Path)) entries.Add(e); }
            catch { /* skip an unresolvable drop */ }
        }
        InsertDroppedEntries(entries, lx, ly);
    }

    /// <summary>True when an OLE drag carries something the dock can pin (files or
    /// shell IDList items), so the drag cursor shows the copy effect.</summary>
    private void HandleOleDrop(List<string> files, byte[]? shellIdList, int screenX, int screenY)
    {
        var entries = new List<AppEntry>();
        foreach (var f in files)
        {
            try { var e = ShortcutResolver.CreateEntry(f); if (e != null && !string.IsNullOrWhiteSpace(e.Path)) entries.Add(e); }
            catch (Exception ex) { Log.Warn("MainDockGpu", "CreateEntry failed for '" + f + "': " + ex.Message); }
        }
        // Shell-namespace items (This PC, Recycle Bin, packaged apps…) arrive as a
        // Shell IDList Array rather than CF_HDROP (parity with WPF OnDropPanel).
        if (shellIdList != null)
        {
            try
            {
                var shellEntries = ShellNamespace.CreateEntriesFromBytes(shellIdList);
                entries.AddRange(shellEntries);
            }
            catch (Exception ex) { Log.Warn("MainDockGpu", "shell-item drop parse failed: " + ex.Message); }
        }

        // Screen pixels → window-local DIPs (icon-slot geometry space).
        float lx = (float)(screenX / _dpi - _winX);
        float ly = (float)(screenY / _dpi - _winY);
        _extDragPt = null; _dragIconKey = null;   // drag finished — clear the preview
        // Defer the actual insert: it may rebuild (recreate) this window, which must
        // NOT happen while the OS is still inside the IDropTarget.Drop call. Run it
        // once the drop returns and the OLE drag loop has unwound.
        Dispatcher.BeginInvoke(new Action(() => InsertDroppedEntries(entries, lx, ly)),
            DispatcherPriority.Background);
    }

    /// <summary>Shared drop core: pins each resolved entry at the grid/ring slot
    /// nearest the window-local drop point (<paramref name="lx"/>,<paramref name="ly"/>),
    /// growing the resident / inner-ring count when the drop lands in that region.</summary>
    private void InsertDroppedEntries(List<AppEntry> entries, float lx, float ly)
    {
        if (entries.Count == 0)
            return;

        int cap = ThemeRegistry.Get(_config.Settings.Theme).MaxIcons;
        int n = _config.Apps.Count;

        // Insert at the slot nearest the pointer so the icon lands where it was
        // dropped rather than always at the resident boundary (parity with WPF).
        int insertIdx;
        bool intoInner;   // saturn: inner ring · glass: framed resident rows
        if (_saturn)
        {
            int r0 = EffectiveRing0Count(n);
            var (ring, pos) = ComputeSaturnDragTarget(lx, ly, -1);
            if (ring == 0) { intoInner = true; insertIdx = Math.Clamp(pos, 0, r0); }
            else { intoInner = false; insertIdx = Math.Clamp(r0 + pos, r0, n); }
        }
        else
        {
            float dropY = _scrollable ? ly + (float)_glassScroll : ly;
            insertIdx = ComputeGridInsertIndex(lx, dropY);
            intoInner = _hasResident
                && dropY >= _resY && dropY <= _resY + _resH;
        }

        bool changed = false;
        foreach (var entry in entries)
        {
            if (_config.Apps.Count >= cap)
                break;   // theme capacity reached (glass 42 / Saturn 42) — stop pinning
            int dup = _config.Apps.FindIndex(a => DockSync.Matches(a, entry));
            if (dup >= 0)
                continue;   // already present — don't duplicate
            insertIdx = Math.Clamp(insertIdx, 0, _config.Apps.Count);
            _config.Apps.Insert(insertIdx, entry);
            insertIdx++;
            if (intoInner)
            {
                if (_saturn)
                {
                    // Auto mode (Ring0Count == 0) already fills the inner ring first,
                    // so only an explicit count needs bumping (parity with WPF).
                    if (_config.Settings.Ring0Count > 0)
                        _config.Settings.Ring0Count = Math.Min(Ring0Cap, _config.Settings.Ring0Count + 1);
                }
                else if (DockSync.ResidentCount(_config) < DockSync.MaxResidentCount)
                {
                    _config.Settings.Ring0Count = DockSync.ResidentCount(_config) + 1;
                }
            }
            changed = true;
        }
        if (changed)
            PersistAndRebuild();
    }

    /// <summary>Grid insertion index (0..n) at a window-local point, allowing a drop
    /// past the last icon to append. Mirrors WPF <c>ComputeGridInsertIndex</c>.</summary>
    private int ComputeGridInsertIndex(float px, float py)
    {
        int n = _slots.Count;
        if (n == 0) return 0;
        float half = (float)(_glassCellH / 2.0);
        int before = 0;
        for (int i = 0; i < n; i++)
        {
            var s = _slots[i].Center;
            float dy = py - s.Y;
            bool isBefore = dy > half ? true : dy < -half ? false : px > s.X;
            if (isBefore) before++;
        }
        return Math.Clamp(before, 0, n);
    }

    private void UnpinPinned(AppEntry entry)
    {
        DragPerfStats.Event("main", _dragDiagSession, "drop-delete", entry.Name);
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

    private void PersistAndRebuild()
    {
        long t0 = Stopwatch.GetTimestamp();
        try { DockSync.MirrorResidentToLeft(_config); }
        catch (Exception ex) { Log.Warn("MainDockGpu", "mirror failed: " + ex.Message); }
        ThemeRegistry.SaveAppearance(_config.Settings);   // keep the per-theme resident count in sync for the pending save
        PersistSoon();
        NotifyAppsChangedSoon();
        // Count-changing operations (add/delete) should stay visually smooth: relayout in
        // place, resizing the existing swap chain/window instead of tearing the whole dock
        // down. The later drag-stutter bug turned out to be input/render-thread contention,
        // not an inherent "count change must rebuild" issue, so keep the original no-flash UX.
        DragPerfStats.Event("main", _dragDiagSession, "persist-rebuild",
            ((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency).ToString("0.000", CultureInfo.InvariantCulture) + "ms");
        RelayoutInPlace();
    }

    /// <summary>Persists the config then relayouts in place (no window/host recreation) so
    /// a reorder drop does not flash. Used for reorders, where the icon count — and hence
    /// the window geometry — is unchanged.</summary>
    private void PersistAndRelayout()
    {
        long t0 = Stopwatch.GetTimestamp();
        try { DockSync.MirrorResidentToLeft(_config); }
        catch (Exception ex) { Log.Warn("MainDockGpu", "mirror failed: " + ex.Message); }
        ThemeRegistry.SaveAppearance(_config.Settings);   // persist the per-theme resident count in the pending save
        PersistSoon();
        NotifyAppsChangedSoon();
        DragPerfStats.Event("main", _dragDiagSession, "persist-relayout",
            ((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency).ToString("0.000", CultureInfo.InvariantCulture) + "ms");
        RelayoutInPlace();
    }

    /// <summary>Re-lays out the slots after a reorder while keeping the existing window and
    /// GPU host alive, so the dock does not flash. Falls back to a full <see cref="Rebuild"/>
    /// if the host is gone or the window rect would change.</summary>
    private void RelayoutInPlace()
    {
        long t0All = Stopwatch.GetTimestamp();
        if (_host == null || _hwnd == IntPtr.Zero) { Rebuild(); return; }
        int ow = _winW, oh = _winH, ox = _winX, oy = _winY;
        // Quiesce the render thread before mutating _slots / geometry (it reads them every
        // frame). On the default path StopDriver is a no-op pause; InvokeOnRender runs inline.
        StopDriver();
        InvokeOnRender(() => { });   // barrier: ensure the render thread is idle (not mid-frame)
        _pressIdx = -1; _dragging = false; _hover = -1;
        LayoutContent();
        if (_winW != ow || _winH != oh || _winX != ox || _winY != oy)
        {
            // Geometry changed (an icon was added/removed). Resize the window + swap chain
            // IN PLACE rather than tearing the whole window down — a full Rebuild blanks the
            // DComp content for a frame and flashes. The composition visual stays bound to
            // the same swap chain, so only the back buffer is reallocated.
            try
            {
                int pw = (int)Math.Ceiling(_winW * _dpi), ph = (int)Math.Ceiling(_winH * _dpi);
                int px = (int)Math.Round(_winX * _dpi), py = (int)Math.Round(_winY * _dpi);
                // SWP_NOZORDER: keep the dock at its CURRENT z-position (below the shim) while
                // resizing. Re-topping it (HWND_TOPMOST) would briefly raise the dock above the
                // shim until SyncShim runs, and an external drag started in that window would
                // hit the bare dock (no drop target) and fail.
                SetWindowPos(_hwnd, IntPtr.Zero, px, py, pw, ph, SWP_NOACTIVATE | SWP_NOZORDER);
                // Swap-chain resize + bitmap caches + first repaint are device work → render thread.
                InvokeOnRender(() =>
                {
                    _host!.Resize(pw, ph);
                    // Keep the icon bitmap cache across an in-place relayout/resize: the D3D/D2D
                    // device stays the SAME (only the swap-chain back buffer is resized), so the
                    // cached icon bitmaps remain valid. Clearing them here forces every icon to be
                    // re-uploaded from BitmapSource on the next drag/hover frame, which is exactly
                    // what made drag-out deletes degrade into a very stuttery "cold cache" drag
                    // afterwards. Full Rebuild/Dispose still tears the host down and clears the
                    // cache there; only the cheap in-place relayout keeps it hot.
                    if (_saturn) { _saturnLastMs = 0; BuildSaturnCache(); }
                    Render();
                });
                SyncShim();
                StartDriver();
                DragPerfStats.Event("main", _dragDiagSession, "relayout-resize",
                    ((Stopwatch.GetTimestamp() - t0All) * 1000.0 / Stopwatch.Frequency).ToString("0.000", CultureInfo.InvariantCulture) + "ms");
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("MainDockGpu", "in-place resize failed, rebuilding: " + ex.Message);
                Rebuild();
                return;
            }
        }
        if (_saturn) { _saturnLastMs = 0; }   // cache is window-sized, unchanged; just resume
        InvokeOnRender(Render);
        StartDriver();
        DragPerfStats.Event("main", _dragDiagSession, "relayout-same-size",
            ((Stopwatch.GetTimestamp() - t0All) * 1000.0 / Stopwatch.Frequency).ToString("0.000", CultureInfo.InvariantCulture) + "ms");
    }

    private void Rebuild()
    {
        long t0 = Stopwatch.GetTimestamp();
        bool wasVisible = _visible;
        StopDriver();
        CloseSlotMenu();
        ClosePreview();
        EndDragGhost();
        if (_hwnd != IntPtr.Zero) s_instances.Remove(_hwnd);
        // Dispose all GPU resources ON the render thread (it owns the device) and WAIT, so
        // the host is gone before the UI thread destroys the HWND below — and the render
        // thread is guaranteed idle (not mid-frame) before we relayout _slots. On the default
        // path this runs inline (unchanged ordering).
        InvokeOnRender(DisposeHostResources);
        // NOTE: keep _dropShim alive across the rebuild (re-owned in CreateWindowAndHost) so
        // an external drag started right after a drop never hits a no-drop-target gap.
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        _hover = -1; _pressIdx = -1; _dragging = false;
        try { Build(wasVisible); }
        catch (Exception ex) { Log.Warn("MainDockGpu", "rebuild failed: " + ex); }
        DragPerfStats.Event("main", _dragDiagSession, "rebuild",
            ((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency).ToString("0.000", CultureInfo.InvariantCulture) + "ms");
    }

    // ---- Right-click context menu (dock-styled WPF popup, parity with WPF dock) ----

    private void ShowSlotMenu(int idx)
    {
        var s = _slots[idx];
        if (s.Entry == null)
            return;
        var entry = s.Entry;
        var items = new List<(string text, Action action)>
        {
            ("从常驻区取消固定", () => UnpinPinned(entry)),
        };
        if (s.Running)
            items.Add(("关闭窗口", () => CloseSlotWindows(s)));
        _menuIdx = idx;
        // If the hover thumbnail preview is open, fade it out first and show the menu only
        // once the animation has finished, so the two never overlap (parity with the WPF
        // dock's ShowDockMenu(fadePreview)).
        var slot = s;
        if (_preview != null && _preview.IsOpen)
        {
            _preview.CloseAnimated(() => BuildAndShowSlotMenu(slot, items));
            return;
        }
        BuildAndShowSlotMenu(s, items);
    }

    private void BuildAndShowSlotMenu(in IconSlot s, List<(string text, Action action)> items)
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
                try { act(); } catch (Exception ex) { Log.Warn("MainDockGpu", "menu action failed: " + ex.Message); }
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

        // Anchor above the icon (the dock sits at the screen bottom). All coords are
        // layout DIPs; the window is positioned at _winX*_dpi, and WPF Popup.Absolute
        // offsets are screen DIPs → screen-DIP icon centre = _winX + s.Center.
        shell.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var ds = shell.DesiredSize;
        // Clear the HOVER-MAGNIFIED icon: the right-clicked icon is under the cursor and so
        // popped to ~MagnifyPeak, extending well above its resting top. Anchor above that
        // (not the base half) plus a small gap, so the menu sits clearly over the icon.
        double half = _gIcon / 2.0 * MagnifyPeak; const double gap = 6.0;
        double px = _winX + s.Center.X - ds.Width / 2.0;
        double py = _winY + s.Center.Y - half - gap - ds.Height;
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
        _menuIdx = -1;
        if (_slotMenu != null) { _slotMenu.IsOpen = false; _slotMenu = null; }
    }

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

    /// <summary>Closes every window of the slot's app (right-click "关闭窗口").</summary>
    private void CloseSlotWindows(in IconSlot s)
    {
        if (s.Entry == null)
            return;
        List<WindowPreview> wins;
        try
        {
            wins = !string.IsNullOrWhiteSpace(s.Entry.Path)
                ? WindowPreviewService.GetWindowsForEntry(s.Entry.Path, s.Entry.Arguments, s.Entry.Name, SiblingShellNames(s.Entry))
                : new List<WindowPreview>();
        }
        catch { wins = new List<WindowPreview>(); }
        foreach (var w in wins)
            WindowPreviewService.CloseWindow(w.Handle);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        t.Tick += (o, _) => { ((DispatcherTimer)o!).Stop(); if (_hwnd != IntPtr.Zero) Rebuild(); };
        t.Start();
    }

    // ---- IMainDock host integration (Stage E) ----

    /// <summary>Create the dock window once, kept hidden (off-screen) until summoned.</summary>
    public void Realize()
    {
        if (_realized) return;
        _realized = true;
        _summon = 0f; _summonDir = 0; _shown = false;
        try { Build(false); }      // build hidden — no flash, no idle ticking
        catch (Exception ex) { Log.Warn("MainDockGpu", "realize failed: " + ex); }
    }

    /// <summary>Re-read config / running state and refresh the layout. While the dock is
    /// shown this relayouts IN PLACE (no window/host teardown) so a side-dock resident change
    /// does not make the main dock flash — a full <see cref="Rebuild"/> blanks the DComp
    /// content for a frame, which the user saw as a flicker when dragging a side-dock icon
    /// with both docks open. Mirrors the side dock's RefreshFromConfigCore. While hidden a
    /// plain Rebuild is used (no flash anyway, and the next Summon rebuilds regardless), which
    /// also avoids waking the render loop on an invisible window.</summary>
    public void RefreshFromConfig()
    {
        if (!_realized) return;
        if (_shown) RelayoutInPlace();   // live: in-place, no DComp blank/flash
        else Rebuild();                  // hidden: unchanged behavior, no flash
    }

    public void ShowPanel() => Summon(pinned: false);
    public void ShowPinned() => Summon(pinned: true);

    /// <summary>Forces a full rebuild so the dock re-reads the display metrics (DPI, work area,
    /// resolution) and re-lays out. Called when the OS reports a display / work-area change —
    /// notably the post-login settle, where auto-start ran before the real mode/DPI applied.</summary>
    public void RefreshForDisplayChange()
    {
        if (!_realized) return;
        try { Rebuild(); }
        catch (Exception ex) { Log.Warn("MainDockGpu", "display-change refresh failed: " + ex.Message); }
    }

    private void Summon(bool pinned)
    {
        Realize();
        _shown = true;
        _pinned = pinned;
        // Clear any frozen launch-press from the previous open so the icon starts at rest.
        _launching = false; _pressLaunchIdx = -1; _pressShrink = 0f;
        // Start fully dismissed (off-screen + transparent) so the very first frame after
        // the rebuild slides + fades in rather than popping at rest.
        _summon = 0f; _summonDir = +1; _summonLast = Environment.TickCount64;
        _fadeOpacity = 0f; _fadeFrom = 0f; _fadeDir = +1; _fadeStart = Environment.TickCount64;
        if (!_visible)
        {
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            _visible = true;
        }
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
        Rebuild();                 // pick up config / running changes; rebuilds visible
        RunOnRender(() => _host?.SetIntro(0f, 0f, 0f));   // first frame fully transparent before the fade-in
        if (!_weatherHooked) { _weather.Updated += OnWeatherUpdated; _weatherHooked = true; }
        _ = _weather.RefreshAsync();   // fetch weather promptly on show (self-throttled)
        StartDriver();
        // Re-assert the drop-shim as the topmost window AFTER every other summon-time window
        // op (dock rebuild, notch, side dock) so an external drag right after opening always
        // finds the shim (not the bare composition dock) under the cursor.
        if (_visible) { SyncShim(); _dropShim?.Show(); }
    }

    /// <summary>Repaint the clock line as soon as fresh weather arrives (the perpetual
    /// render loop picks the new suffix up on its next frame while shown).</summary>
    private void OnWeatherUpdated() => _lastClockMin = -1;

    public void HidePanel() => HidePanel(null);

    public void HidePanel(Action? onFaded)
    {
        if (!_realized || !_shown)
        {
            // Nothing to slide out — still honour the callback (e.g. open settings).
            onFaded?.Invoke();
            return;
        }
        _shown = false;
        _pinned = false;
        // Cancel any in-flight drag / menu so they don't outlive the dismiss.
        _pressIdx = -1; _dragging = false; _hover = -1;
        CloseSlotMenu();
        ClosePreview();
        PanelDismissed?.Invoke();   // retract the side dock together with the main dock
        _onFaded = onFaded;
        // Dismiss is a PURE fade-out in both themes (no slide / scale) — mirrors the
        // WPF RootGrid 170ms opacity animation. Leave _summon at rest so the scene holds
        // its docked position while the compositor fades the whole visual to transparent.
        _summonDir = 0;
        _fadeFrom = _fadeOpacity; _fadeDir = -1; _fadeStart = Environment.TickCount64;
        StartDriver();
    }

    public void HideIfNotPinned()
    {
        if (!_pinned)
            HidePanel();
    }

    /// <summary>The GPU dock has no perpetual ambient animation to pause (parity
    /// with the GPU side dock), so this is a no-op.</summary>
    public void SetAmbientPaused(bool paused) { }

    public void Close() => Dispose();

    /// <summary>Coalesces repeated dock mutations (especially drag reorders) into one disk
    /// write ~300ms after the last change, instead of serializing config.json synchronously on
    /// every mouse-up. Mirrors App.Persist() and keeps rapid drag sequences smooth.</summary>
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
                catch (Exception ex) { Log.Warn("MainDockGpu", "persist failed: " + ex.Message); }
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
            catch (Exception ex) { Log.Warn("MainDockGpu", "persist failed: " + ex.Message); }
        }
    }

    /// <summary>Called once a dismiss slide reaches the bottom: hide the window so a
    /// dismissed dock costs nothing, then run any deferred callback.</summary>
    private void OnDismissComplete()
    {
        _visible = false;
        StopDriver();
        var cb = _onFaded;
        _onFaded = null;
        // SW_HIDE + shim hide are Win32 on UI-thread-owned windows; marshal off the render
        // thread. Guard on !_shown so a re-summon that lands before this runs isn't hidden.
        OnUi(() =>
        {
            if (!_shown)
            {
                if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_HIDE);
                _dropShim?.Hide();
            }
        });
        if (cb != null)
            Dispatcher.BeginInvoke(cb, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        StopDriver();
        _timer?.Stop();
        _timer = null;
        FlushPersist();
        FlushAppsChanged();
        if (_weatherHooked) { _weather.Updated -= OnWeatherUpdated; _weatherHooked = false; }
        CloseSlotMenu();
        ClosePreview();
        if (_anchorWin != null) { try { _anchorWin.Close(); } catch { } _anchorWin = null; _anchorEl = null; _preview = null; }
        // Dispose all GPU resources on the render thread (their owner) and wait, then stop +
        // join the render thread, all BEFORE the HWND is destroyed. On the default path this
        // runs inline. DisposeHostResources covers host, text formats, Saturn + bitmap caches.
        InvokeOnRender(DisposeHostResources);
        if (UseRenderThread) { _loop?.Stop(); _loop = null; }
        DisposeGhost();
        _dropShim?.Dispose(); _dropShim = null;
        if (_hwnd != IntPtr.Zero) { s_instances.Remove(_hwnd); DestroyWindow(_hwnd); }
    }

    // ---- Raw Win32 NOREDIRECTIONBITMAP window (click-through for Stage A) ----

    private static readonly Dictionary<IntPtr, MainDockWindowGpu> s_instances = new();
    private static readonly Win32.WndProc s_wndProc = WndProcImpl;
    private static ushort s_atom;

    private static IntPtr WndProcImpl(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (s_instances.TryGetValue(h, out var self) && self.HandleMessage(m, w, l, out var res))
            return res;
        return DefWindowProcW(h, m, w, l);
    }

    // Pure render surface. The composition dock does NOT intercept the empty headroom with
    // WS_EX_TRANSPARENT — that style is a no-op for a WS_EX_NOREDIRECTIONBITMAP window (it
    // needs WS_EX_LAYERED to be click-through, which composition can't use). Instead the
    // visible content is carved out with SetWindowRgn so the transparent margins are literally
    // NOT part of the window (OS-level passthrough), while the DropShimWindow overlay catches
    // the slab input + external drops.
    private static IntPtr CreateWindow(int w, int h) => Win32.CreateWindow(
        "PolarisMainDockGpu",
        WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        w, h, s_wndProc, ref s_atom, LoadCursorW(IntPtr.Zero, IDC_ARROW));

    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("shell32.dll")] private static extern void DragAcceptFiles(IntPtr hwnd, bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, System.Text.StringBuilder? buf, uint cch);
    [DllImport("shell32.dll")] private static extern bool DragQueryPoint(IntPtr hDrop, out POINT pt);
    [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr hDrop);

    private const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205, WM_NCHITTEST = 0x0084, WM_DROPFILES = 0x0233;
    private const int HTTRANSPARENT = -1, HTCLIENT = 1;
}
