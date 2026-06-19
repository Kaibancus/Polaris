using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
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

/// <summary>GPU main dock (spike) — Stage A static render of the LIQUID-GLASS theme.
/// Draws the bottom-docked liquid-glass slab (via the shared <see cref="GlassSlab"/>)
/// and the 7-column pinned icon grid in Direct2D under DirectComposition, mirroring the
/// WPF <see cref="RadialWindow"/>'s glass layout (<c>DrawGlassPanel</c> +
/// <c>LiquidGlassTheme.ComputeSlots</c>). Per-monitor DPI aware (layout in DIPs, window +
/// swap chain in physical px, D2D target DPI = 96 × scale).
/// <para>Stage B: a 16 ms cursor poll drives a continuous 2-D fisheye magnify wave
/// (raised-cosine falloff, focal anchoring + neighbour spread mirroring
/// <c>RadialWindow.Magnify</c>) and a floating hover name label below the focal icon.
/// Still click-through (hit-test is poll-based) — launch / drag interaction lands in
/// Stage D. Shown behind POLARIS_GPU_MAINDOCK=1.</summary>
internal sealed class MainDockWindowGpu : IMainDock, IDisposable
{
    // Mirror the WPF glass theme scale factors (see RadialWindow: _uiScale=1, _themeScale=0.9
    // for glass; glyphs drawn at icon*GlassIconScale).
    private const double ThemeScale = 0.9;
    private const float GlassIconScale = 1.32f;

    // Magnify wave constants — kept in lockstep with DockTuning so the GPU dock
    // feels identical to the WPF glass dock.
    private const float MagnifyPeak = (float)DockTuning.HoverScale;       // 1.7x under the cursor
    private const float SpreadPush = (float)DockTuning.SpreadPush;        // 0.75 * iconSize
    private const float SpreadInfluence = (float)DockTuning.SpreadInfluence; // 2.7 * iconSize

    private readonly AppConfig _config;
    private IntPtr _hwnd;
    private CompositionHost? _host;
    private readonly Dictionary<string, ID2D1Bitmap?> _bmpCache = new();
    private readonly Dictionary<string, BitmapSource?> _iconCache = new();

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
    private DispatcherTimer? _timer;
    private IDWriteFactory? _dwrite;
    private IDWriteTextFormat? _labelFormat;
    private float _labelFontPx = 13f;
    private IDWriteTextFormat? _gearFormat;
    private IDWriteTextFormat? _clockFormat;   // glass top-left date/time bar
    private int _lastClockMin = -1;            // last rendered minute (forces a clock repaint)
    private readonly Polaris.Services.WeatherService _weather = new();   // clock weather suffix
    private bool _weatherHooked;               // Updated event subscribed once
    private float[] _waveCur = Array.Empty<float>();   // smoothed per-icon scale
    private float[] _waveOffX = Array.Empty<float>();  // smoothed spread offset (DIP)
    private float[] _waveOffY = Array.Empty<float>();
    private int _hover = -1;
    // Hover window-thumbnail preview (parity with the WPF dock / GPU side dock): a
    // tiny transparent click-through anchor window is parked over the hovered icon and
    // used as WindowPreviewPopup's placement target. The popup's own dwell timers drive
    // show/hide as the dock reports hover changes.
    private System.Windows.Window? _anchorWin;
    private System.Windows.Controls.Border? _anchorEl;
    private WindowPreviewPopup? _preview;
    private Func<List<WindowPreview>>? _previewSource;
    private int _prevHover = -1;
    private float _runSweep;              // running-icon sweep gradient angle (deg)
    private float _runPulse = 0.5f;       // running-icon glow pulse (0.35..0.8)
    private bool _anyRunning;             // a glass running icon is present (drives sweep render)
    // New-message attention badges: keys (EffectiveIconSource) of running icons whose
    // windows are flashing for attention, polled off-thread; a small red dot pulses on
    // each such icon's lower-left corner (parity with RadialIcon.SetAttention).
    private volatile System.Collections.Generic.HashSet<string> _flashKeys = new();
    private float _badgePulse = 1f;       // attention dot pulse scale (1.0..1.18, 1.4s)
    private long _attnLast;               // last attention poll tick (throttle to ~800ms)
    private volatile bool _attnBusy;      // an attention poll task is in flight
    private float _orbitAngle;            // glass orbit-light angle (deg, 36s/rev clockwise)

    // ---- Stage D interaction state ----
    private int _pressIdx = -1;          // slot under the mouse-down, or -1
    private bool _dragging;              // press has crossed the drag threshold
    private float _pressX, _pressY;      // mouse-down point (window-local DIP)
    private float _dragX, _dragY;        // current drag point (window-local DIP)
    private long _dragArrangeLastMs;     // last drag-reflow advance (time-based ease)
    private int _bounceIdx = -1;         // slot playing the launch hop, or -1
    private long _bounceStart;
    private const long BounceDurMs = 480;
    private System.Windows.Controls.Primitives.Popup? _slotMenu;

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
    // Phone-notch date/time panel shown at the screen top while the Saturn theme is
    // summoned (mirrors RadialWindow._notch / ShowNotchIfSaturn).
    private INotchClock? _notch;
    private static readonly bool UseGpuNotch =
        Environment.GetEnvironmentVariable("POLARIS_GPU_NOTCH") == "1";
    private SaturnScene.Geom _sg;         // Saturn ring/planet geometry (window-local DIP)
    private float[] _slotG = Array.Empty<float>();   // per-slot icon draw size (Saturn rings differ)
    private double _spinAngle, _innerAngle, _outerAngle, _saturnTime;   // Saturn animation phases
    private long _saturnLastMs;
    private double _spinRate = 1.0;          // Saturn: planet spin multiplier (eased toward hover target)
    private const double PlanetSpinSeconds = 60.0;
    private const double PlanetHoverSpinMul = 20.0;   // hover over the planet -> ~3s period (WPF StartSpin(3.0))
    private const double InnerOrbitRatio = 0.9023;
    private const double OuterOrbitRatio = 1.3941;
    // Baked Saturn layers (full-window): static rings+disc+planet body, the
    // spinning polar disc (rotated each frame), and the static planet shading.
    private ID2D1Bitmap1? _satStatic, _satDisc, _satShade, _satInner, _satOuter;
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
        double icon = _config.Settings.IconSize * ThemeScale;   // EffectiveIconSize (glass)
        double gIcon = icon * GlassIconScale;
        double cellW = icon * LiquidGlassTheme.ColumnPitch;
        double cellH = icon * LiquidGlassTheme.RowPitch;
        double gridW = (LiquidGlassTheme.Columns - 1) * cellW;
        double padX = icon * 1.15;
        double dockW = gridW + icon + padX * 2;

        // Bottom-docked margin: slab bottom rests above the system taskbar, and
        // lifts further when the side dock reserves space at the bottom edge.
        double taskbarH = Math.Max(0.0, mon.Bottom - wa.Bottom);
        double bottomMargin = Math.Max(taskbarH + icon * 0.12, BottomDockReserve?.Invoke() ?? 0.0);

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
        double glassDragHeadroom = _config.Settings.IconSize * ThemeScale * 1.8;
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
        _radius = 28f;
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
        var noTitles = new List<string>();
        var noAumids = new HashSet<string>();
        for (int i = 0; i < count && i < slots.Count; i++)
        {
            var entry = apps[i];
            var img = IconExtractor.GetCached(entry.EffectiveIconSource, _iconCache);
            bool run = RunningAppTracker.IsEntryRunning(entry, running, noTitles, noAumids);
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
        _dpi = DpiScale();
        int pw = (int)Math.Ceiling(_winW * _dpi), ph = (int)Math.Ceiling(_winH * _dpi);
        int px = (int)Math.Round(_winX * _dpi), py = (int)Math.Round(_winY * _dpi);
        SetWindowPos(_hwnd, HWND_TOPMOST, px, py, pw, ph, SWP_NOACTIVATE);
        if (visible) ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        DragAcceptFiles(_hwnd, true);   // accept desktop shortcuts / files dropped to pin
        _host = new CompositionHost(_hwnd, pw, ph, (float)(96.0 * _dpi));

        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
        // Match WPF ShowGlassHoverLabel: a fixed 11.5pt label that lived inside the
        // icon visual tree and read as ~11.5*HoverScale once the icon zoomed.
        _labelFontPx = (float)(11.5 * DockTuning.HoverScale * FontScale.Current);
        _labelFormat = _dwrite.CreateTextFormat("Microsoft YaHei UI", null, Vortice.DirectWrite.FontWeight.SemiBold,
            FontStyle.Normal, Vortice.DirectWrite.FontStretch.Normal, _labelFontPx, "zh-cn");
        _labelFormat.TextAlignment = Vortice.DirectWrite.TextAlignment.Center;
        _labelFormat.ParagraphAlignment = ParagraphAlignment.Center;
        _gearFormat = _dwrite.CreateTextFormat("Segoe UI Symbol", null, Vortice.DirectWrite.FontWeight.Normal,
            FontStyle.Normal, Vortice.DirectWrite.FontStretch.Normal, _gearR * 1.16f, "en-us");
        _gearFormat.TextAlignment = Vortice.DirectWrite.TextAlignment.Center;
        _gearFormat.ParagraphAlignment = ParagraphAlignment.Center;
        float clockSize = (float)(Math.Max(18.0, _effIcon * 0.36) * FontScale.Current);
        _clockFormat = _dwrite.CreateTextFormat("Segoe UI Semibold", null, Vortice.DirectWrite.FontWeight.SemiBold,
            FontStyle.Normal, Vortice.DirectWrite.FontStretch.Normal, clockSize, "zh-cn");
        _clockFormat.TextAlignment = Vortice.DirectWrite.TextAlignment.Leading;
        _clockFormat.ParagraphAlignment = ParagraphAlignment.Near;

        if (_saturn) { _saturnLastMs = 0; BuildSaturnCache(); }

        Render();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
        _visible = visible;
        if (visible) _timer.Start();
    }

    private static Color4 Col(byte a, byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, a / 255f);

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
            DiscOpacity = (float)(1.0 - Math.Clamp(_config.Settings.PanelTransparency, 0.0, 1.0)),
        };

        _gIcon = icon;             // base draw size; per-slot size in _slotG
        _effIcon = icon;
        _iconRaw = (float)_config.Settings.IconSize;
        _gearR = _effIcon * 0.30f; // keeps _gearFormat sizing valid
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
        var noTitles = new List<string>();
        var noAumids = new HashSet<string>();
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
            bool run = RunningAppTracker.IsEntryRunning(entry, running, noTitles, noAumids);
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
        int pw = (int)Math.Ceiling(_winW * _dpi), ph = (int)Math.Ceiling(_winH * _dpi);
        float bdpi = (float)(96.0 * _dpi);
        _satStatic = RenderToBitmap(ctx, pw, ph, bdpi, c => SaturnScene.DrawStaticScene(c, _sg));
        _satDisc = RenderToBitmap(ctx, pw, ph, bdpi, c => SaturnScene.DrawPlanetDisc(c, _sg));
        _satShade = RenderToBitmap(ctx, pw, ph, bdpi, c => SaturnScene.DrawPlanetShade(c, _sg));
        // Revolution cues are static apart from their orbit angle, so bake each
        // orbit group flat (no tilt, orbit=0) once and re-revolve the bitmap per
        // frame — same trick as the planet disc, keeps the cues near-free.
        var gFlat = _sg; gFlat.TiltY = 1f;
        _satInner = RenderToBitmap(ctx, pw, ph, bdpi, c => { SaturnScene.DrawInnerCues(c, gFlat, 0, System.Numerics.Matrix3x2.Identity); c.Transform = System.Numerics.Matrix3x2.Identity; });
        _satOuter = RenderToBitmap(ctx, pw, ph, bdpi, c => { SaturnScene.DrawOuterCues(c, gFlat, 0, System.Numerics.Matrix3x2.Identity); c.Transform = System.Numerics.Matrix3x2.Identity; });
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

    private void DisposeSaturnCache()
    {
        _satStatic?.Dispose(); _satStatic = null;
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
    private void Tick()
    {
        if (_host == null || _slots.Count == 0)
            return;

        // Advance the summon (rise / drop) slide.
        bool animating = _summonDir != 0;
        if (animating)
        {
            long nowMs = Environment.TickCount64;
            float dt = Math.Clamp((nowMs - _summonLast) / 1000f, 0f, 0.1f);
            _summonLast = nowMs;
            if (_summonDir > 0)
            {
                _summon = Math.Min(1f, _summon + dt / SummonInSec);
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

        // Gear button: spin the glyph while hovered (1.7s/rev, WPF parity) and coast
        // to rest on leave; ease the press-scale toward 0.8 while held.
        if (!_saturn)
        {
            if (_gearHover)
                _gearAngle = (_gearAngle + 16f * 360f / 1700f) % 360f;
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

        float k = 1f - (float)Math.Exp(-0.016 / 0.045);    // tau 45ms
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
            maxDelta = Math.Max(maxDelta, AdvanceDragArrange());

        // Hover only when the pointer is genuinely over an icon's cell.
        _hover = active && focal >= 0 && best <= _effIcon * 0.85f ? focal : -1;
        // Drive the hover thumbnail preview (suppressed while dragging or dismissing).
        DrivePreview(_dragging || !_shown ? -1 : _hover);

        bool bouncing = _bounceIdx >= 0 && (Environment.TickCount64 - _bounceStart) <= BounceDurMs;
        if (!bouncing) _bounceIdx = -1;

        // Ease the grid scroll toward its wheel/scrollbar target (tau ~70ms) so the
        // whole grid glides rather than jumping a row at a time.
        bool scrolling = false;
        if (_scrollable && Math.Abs(_glassScrollTarget - _glassScroll) > 0.05)
        {
            _glassScroll += (_glassScrollTarget - _glassScroll) * (1.0 - Math.Exp(-0.016 / 0.07));
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
            double dt = _saturnLastMs == 0 ? 0.016 : Math.Clamp((now - _saturnLastMs) / 1000.0, 0, 0.1);
            _saturnLastMs = now;
            _saturnTime += dt;
            // Ease the spin multiplier toward the hover target (accelerate on planet
            // hover, coast back to ambient on leave) — tau ~0.4s for a smooth ramp.
            double spinTarget = planetHover ? PlanetHoverSpinMul : 1.0;
            _spinRate += (spinTarget - _spinRate) * (1.0 - Math.Exp(-dt / 0.4));
            _spinAngle = (_spinAngle + _spinRate * dt * 360.0 / PlanetSpinSeconds) % 360.0;
            _innerAngle = (_innerAngle + dt * 360.0 / (PlanetSpinSeconds * InnerOrbitRatio)) % 360.0;
            _outerAngle = (_outerAngle + dt * 360.0 / (PlanetSpinSeconds * OuterOrbitRatio)) % 360.0;
        }

        // Running-icon sweep + glow pulse: rotate the gradient (4.2s/rev) and breathe the
        // halo (2.2s) so running apps show a flowing border in BOTH themes (the WPF
        // RadialIcon RunningBorder is not theme-gated).
        if (_anyRunning)
        {
            _runSweep = (_runSweep + 16f * 360f / 4200f) % 360f;
            double ph = Environment.TickCount64 / 1000.0 * 2.0 * Math.PI / 2.2;
            _runPulse = 0.575f + 0.225f * MathF.Sin((float)ph);
        }
        // Glass orbit light: a cool lamp drifts around the slab, one rev / 36s.
        if (!_saturn && _visible)
            _orbitAngle = (_orbitAngle + 16f * 360f / 36000f) % 360f;
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
        if (_saturn || animating || fading || active || _dragging || bouncing || scrolling || _barDrag || maxDelta > 0.001f
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

    /// <summary>Launch-hop offset (px, upward) for the clicked icon: a single damped
    /// hop over <see cref="BounceDurMs"/> ms, mirroring the WPF/side-dock macOS bounce.</summary>
    private float BounceOffset(int i)
    {
        if (i != _bounceIdx)
            return 0f;
        long el = Environment.TickCount64 - _bounceStart;
        if (el < 0 || el > BounceDurMs)
            return 0f;
        float t = el / (float)BounceDurMs;
        float hop = MathF.Sin(MathF.PI * t);
        float settle = -0.12f * MathF.Sin(MathF.PI * 2f * t) * (1f - t);
        return _gIcon * 0.5f * (hop + settle);
    }

    private void Render()
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
            // leads and the outer band follows ~0.28 of the summon later, each growing
            // 0.55 -> 1.0 from the planet with a CubicEaseOut fade-in, so the dock reads
            // as rings blooming out from the centre rather than a single rigid zoom.
            float innerP = CubicEaseOut(Math.Clamp(_summon / 0.72f, 0f, 1f));
            float outerP = CubicEaseOut(Math.Clamp((_summon - 0.28f) / 0.72f, 0f, 1f));
            _satInnerScl = 0.55f + 0.45f * innerP;
            _satOuterScl = 0.55f + 0.45f * outerP;
            _satInnerOp = innerP;
            _satOuterOp = outerP;
            _satR0 = EffectiveRing0Count(_slots.Count);
            var cen = new Vector2(_sg.Cx, _sg.Cy);
            var satBase = System.Numerics.Matrix3x2.CreateScale(_satInnerScl, _satInnerScl, cen);
            ctx.Transform = satBase;
            if (_satStatic != null)
                ctx.DrawBitmap(_satStatic, _satInnerOp, InterpolationMode.Linear);
            // Re-revolve the baked cue bitmaps: Rotate(orbit) * Scale(1,tilt) * base.
            // Inner cues bloom with the inner band; outer cues trail on the outer band.
            if (_satInner != null)
            {
                ctx.Transform = System.Numerics.Matrix3x2.CreateRotation((float)(_innerAngle * Math.PI / 180.0), cen)
                    * System.Numerics.Matrix3x2.CreateScale(1f, _sg.TiltY, cen) * satBase;
                ctx.DrawBitmap(_satInner, _satInnerOp, InterpolationMode.Linear);
            }
            if (_satOuter != null)
            {
                var outerBase = System.Numerics.Matrix3x2.CreateScale(_satOuterScl, _satOuterScl, cen);
                ctx.Transform = System.Numerics.Matrix3x2.CreateRotation((float)(_outerAngle * Math.PI / 180.0), cen)
                    * System.Numerics.Matrix3x2.CreateScale(1f, _sg.TiltY, cen) * outerBase;
                ctx.DrawBitmap(_satOuter, _satOuterOp, InterpolationMode.Linear);
            }
            ctx.Transform = satBase;
            if (_satDisc != null)
            {
                ctx.Transform = System.Numerics.Matrix3x2.CreateRotation(
                    (float)(_spinAngle * Math.PI / 180.0), cen) * satBase;
                ctx.DrawBitmap(_satDisc, _satInnerOp, InterpolationMode.Linear);
                ctx.Transform = satBase;
            }
            if (_satShade != null)
                ctx.DrawBitmap(_satShade, _satInnerOp, InterpolationMode.Linear);
            SaturnScene.DrawTwinkle(ctx, _sg, _saturnTime);
            ctx.Transform = System.Numerics.Matrix3x2.Identity;
        }
        else
        {
        // Floating liquid-glass slab (drop shadow on, since the main dock floats above
        // the desktop rather than being flush to a screen edge).
        GlassSlab.DrawGlass(ctx, _slabX, _slabY, _slabW, _slabH, _radius, _opacity, _frost, shadowExtent: 26f);

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
        var order = new int[_slots.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => _waveCur[a].CompareTo(_waveCur[b]));
        bool clip = _scrollable;
        if (clip)
            ctx.PushAxisAlignedClip(new Vortice.Mathematics.Rect(_gvX, _gvY, _gvW, _gvH), AntialiasMode.Aliased);
        if (!_saturn && _hasResident)
            DrawResidentFrame(ctx, scrollY);
        foreach (int i in order)
        {
            if (i == dragIdx)
                continue;
            var off = new Vector2(_waveOffX[i], _waveOffY[i] - BounceOffset(i) - scrollY);
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
                DrawIcon(ctx, _slots[i], _waveCur[i], off, gi);
        }
        if (clip)
            ctx.PopAxisAlignedClip();

        if (dragIdx >= 0 && dragIdx < _slots.Count)
        {
            // The dragged icon follows the cursor, lifted 1.12x with no spread.
            var s = _slots[dragIdx];
            var moved = new IconSlot(new Vector2(_dragX, _dragY), s.IconKey, s.Name, s.Running, s.Image, s.Entry);
            DrawIcon(ctx, moved, 1.12f, Vector2.Zero, _saturn && dragIdx < _slotG.Length ? _slotG[dragIdx] : 0f);
        }
        else if (_hover >= 0 && _hover < _slots.Count)
        {
            var hs = _slots[_hover];
            var hsv = scrollY != 0f
                ? new IconSlot(new Vector2(hs.Center.X, hs.Center.Y - scrollY), hs.IconKey, hs.Name, hs.Running, hs.Image, hs.Entry)
                : hs;
            DrawHoverLabel(ctx, hsv, _waveCur[_hover]);
        }

        if (!_saturn && _scrollable)
            DrawScrollBar(ctx);

        if (slid || stretching)
            ctx.Transform = System.Numerics.Matrix3x2.Identity;
        ctx.EndDraw();
        _host.Present();
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

    /// <summary>Settings gear: a frosted disc with a ⚙ glyph in the slab's top-right
    /// corner; the glyph spins while hovered and the disc dips on press, mirroring the
    /// WPF dock gear. Clicking it asks the host to open the settings window.</summary>
    private void DrawGear(ID2D1DeviceContext ctx)
    {
        if (_gearFormat == null) return;
        float r = _gearR * _gearScale;
        var e = new Vortice.Direct2D1.Ellipse(_gearC, r, r);
        // Frosted vertical gradient fill (WPF: 70FFFFFF -> 42EAF2FF -> 5CD6E4F6).
        using (var stops = ctx.CreateGradientStopCollection(new[]
        {
            new Vortice.Direct2D1.GradientStop { Position = 0f,   Color = Col(0x70, 0xFF, 0xFF, 0xFF) },
            new Vortice.Direct2D1.GradientStop { Position = 0.5f, Color = Col(0x42, 0xEA, 0xF2, 0xFF) },
            new Vortice.Direct2D1.GradientStop { Position = 1f,   Color = Col(0x5C, 0xD6, 0xE4, 0xF6) },
        }))
        using (var fill = ctx.CreateLinearGradientBrush(
            new LinearGradientBrushProperties { StartPoint = new Vector2(_gearC.X, _gearC.Y - r), EndPoint = new Vector2(_gearC.X, _gearC.Y + r) }, stops))
            ctx.FillEllipse(e, fill);
        using (var rim = ctx.CreateSolidColorBrush(Col(0xFF, 0xEA, 0xF4, 0xFF)))
            ctx.DrawEllipse(e, rim, 2.0f);
        // ⚙ glyph: rotate while hovered, scale on press.
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateRotation(_gearAngle * MathF.PI / 180f, _gearC)
            * Matrix3x2.CreateScale(_gearScale, _gearScale, _gearC) * saved;
        var rect = new Vortice.Mathematics.Rect(_gearC.X - _gearR, _gearC.Y - _gearR, _gearR * 2f, _gearR * 2f);
        using (var fg = ctx.CreateSolidColorBrush(Col(0xF0, 0xFF, 0xFF, 0xFF)))
            ctx.DrawText("\u2699", _gearFormat, rect, fg);
        ctx.Transform = saved;
    }

    /// <summary>Orbit light: a cool radial lamp circling the slab centre (36s/rev),
    /// painted by filling the rounded slab with the lamp's radial gradient so the
    /// glow is clipped to the glass (parity with GlassOrbitLight).</summary>
    private void DrawOrbitLight(ID2D1DeviceContext ctx, in RoundedRectangle slab)
    {
        float cx = _slabX + _slabW / 2f, cy = _slabY + _slabH / 2f;
        float orbitR = MathF.Max(_slabW, _slabH) * 0.5f + _effIcon * 1.4f;
        float lampR = orbitR * 1.3f;                 // lampD = orbitR*2.6
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
        ctx.FillRoundedRectangle(slab, brush);
    }

    /// <summary>Top-left date/time bar on the glass slab (parity with RadialWindow's
    /// inline clock): frosted white gradient text with a soft dark halo, formatted
    /// "yyyy年M月d日  ddd   H:mm" in the local culture.</summary>
    private void DrawClock(ID2D1DeviceContext ctx)
    {
        if (_clockFormat == null) return;
        _lastClockMin = DateTime.Now.Minute;
        string text = DateTime.Now.ToString("yyyy\u5e74M\u6708d\u65e5  ddd   H:mm",
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

    /// <summary>Etched rounded frame around the resident (pinned) rows, mirroring
    /// RadialWindow.DrawResidentRegionBorder: ~95%-transparent fill, a soft cool
    /// glow and a brighter cool rim. Drawn in grid space (shifted by the scroll
    /// offset) under the icons.</summary>
    private void DrawResidentFrame(ID2D1DeviceContext ctx, float scrollY)
    {
        var rect = new Vortice.Mathematics.Rect(_resX, _resY - scrollY, _resW, _resH);
        var rr = new RoundedRectangle { Rect = rect, RadiusX = _resR, RadiusY = _resR };
        using (var fill = ctx.CreateSolidColorBrush(Col(0x10, 0xFF, 0xFF, 0xFF)))
            ctx.FillRoundedRectangle(rr, fill);
        // Soft cool glow (a couple of falling-alpha strokes fake the WPF blur=4 halo).
        using (var glow = ctx.CreateSolidColorBrush(Col(0x18, 0xBF, 0xE0, 0xFF)))
            ctx.DrawRoundedRectangle(rr, glow, 4.5f);
        using (var glow2 = ctx.CreateSolidColorBrush(Col(0x24, 0xBF, 0xE0, 0xFF)))
            ctx.DrawRoundedRectangle(rr, glow2, 2.4f);
        using (var rim = ctx.CreateSolidColorBrush(Col(0x66, 0xEA, 0xF4, 0xFF)))
            ctx.DrawRoundedRectangle(rr, rim, 1.0f);
    }

    private void DrawIcon(ID2D1DeviceContext ctx, in IconSlot s, float scale, Vector2 off, float gIcon = 0f, float opacity = 1f)
    {
        float g = gIcon > 0f ? gIcon : _gIcon, half = g / 2f, cx = s.Center.X + off.X, cy = s.Center.Y + off.Y;
        var center = new Vector2(cx, cy);
        // Compose every icon transform with the ambient context transform (the glass
        // summon slide + bottom-anchored squash/stretch) so the icons ride the slab as it
        // rises, instead of snapping to their docked positions while the slab slides.
        var baseTf = ctx.Transform;
        var wave = Matrix3x2.CreateScale(scale, scale, center) * baseTf;

        // Running indicator: a soft pulsing glow halo plus a flowing sweep border — a
        // rounded-rect stroke painted with a linear gradient whose axis rotates
        // (4.2s/rev). Shown in BOTH themes, mirroring RadialIcon's RunningBorder which
        // is not theme-gated.
        if (s.Running)
        {
            ctx.Transform = wave;
            // Frame the icon at its full nominal size (parity with the WPF RunningBorder,
            // a Border of Width/Height = IconSize) so the flow ring sits clear of the
            // glyph rather than cutting into it.
            float box = g, rr = box * 0.18f;
            var rect = new Vortice.Mathematics.Rect(cx - box / 2f, cy - box / 2f, box, box);
            var rrect = new RoundedRectangle { Rect = rect, RadiusX = rr, RadiusY = rr };
            // Pulsing cool halo (a few falling-alpha strokes fake the blurred glow).
            float pulse = _runPulse;
            (float w, byte a)[] ring = { (7f, (byte)(0x22 * pulse)), (4.5f, (byte)(0x44 * pulse)) };
            foreach (var (sw, a) in ring)
                using (var br = ctx.CreateSolidColorBrush(Col(a, 0x3F, 0xA9, 0xFF)))
                    ctx.DrawRoundedRectangle(rrect, br, sw);
            // Flowing sweep: a rotating linear gradient stroked around the icon.
            float ang = _runSweep * MathF.PI / 180f, R = box * 0.6f;
            var dir = new Vector2(MathF.Cos(ang) * R, MathF.Sin(ang) * R);
            using (var stops = ctx.CreateGradientStopCollection(new[]
            {
                new Vortice.Direct2D1.GradientStop { Position = 0f,    Color = Col(0x10, 0x3D, 0xA9, 0xFF) },
                new Vortice.Direct2D1.GradientStop { Position = 0.28f, Color = Col(0x66, 0x57, 0xC8, 0xFF) },
                new Vortice.Direct2D1.GradientStop { Position = 0.5f,  Color = Col(0xFF, 0x6F, 0xD3, 0xFF) },
                new Vortice.Direct2D1.GradientStop { Position = 0.72f, Color = Col(0x66, 0x57, 0xC8, 0xFF) },
                new Vortice.Direct2D1.GradientStop { Position = 1f,    Color = Col(0x10, 0x3D, 0xA9, 0xFF) },
            }))
            using (var sweep = ctx.CreateLinearGradientBrush(
                new LinearGradientBrushProperties { StartPoint = new Vector2(cx - dir.X, cy - dir.Y), EndPoint = new Vector2(cx + dir.X, cy + dir.Y) }, stops))
                ctx.DrawRoundedRectangle(rrect, sweep, 2.5f);
            ctx.Transform = baseTf;
        }

        var bmp = GetBitmap(ctx, s.IconKey, s.Image);
        if (bmp == null)
            return;
        float pad = g * 0.06f, dstX = cx - half + pad, dstY = cy - half + pad, dstSz = g - pad * 2;
        var bs = bmp.Size;
        ctx.Transform = Matrix3x2.CreateScale(dstSz / Math.Max(1f, bs.Width), dstSz / Math.Max(1f, bs.Height))
                      * Matrix3x2.CreateTranslation(dstX, dstY) * wave;
        ctx.DrawBitmap(bmp, Math.Clamp(opacity, 0f, 1f), InterpolationMode.HighQualityCubic);
        ctx.Transform = baseTf;

        // New-message attention dot: a small pulsing red disc hugging the icon's
        // lower-left corner when any of this app's windows is flashing for attention
        // (parity with RadialIcon's lower-left AttentionBadge).
        if (s.Running && _flashKeys.Contains(s.IconKey))
        {
            float d = Math.Clamp(g * 0.12f, 5f, 10f) * _badgePulse * scale;
            float bx = cx - (half - d * 0.55f) * scale, by = cy + (half - d * 0.55f) * scale;
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
    private void DrawHoverLabel(ID2D1DeviceContext ctx, in IconSlot s, float scale)
    {
        if (_labelFormat == null || string.IsNullOrEmpty(s.Name))
            return;
        float zoomedHalf = _gIcon / 2f * scale;
        float h = _labelFontPx + 10f;
        float w = Math.Max(48f, s.Name.Length * _labelFontPx * 0.95f + 20f);
        float lx = s.Center.X, ly = s.Center.Y + zoomedHalf + 2f + h / 2f;
        var rect = new Vortice.Mathematics.Rect(lx - w / 2f, ly - h / 2f, w, h);
        using (var bg = ctx.CreateSolidColorBrush(Col(0x05, 0x1A, 0x1A, 0x1A)))
            ctx.FillRoundedRectangle(new RoundedRectangle { Rect = rect, RadiusX = 7f, RadiusY = 7f }, bg);
        // 3-D raised lettering: dark offset copies behind the light text give the name
        // depth and a legibility halo, mirroring the WPF DropShadowEffect (black, depth
        // 1.4, direction 315° → a ~1px down-right offset, plus a soft second copy).
        using (var halo = ctx.CreateSolidColorBrush(Col(0xE6, 0, 0, 0)))
        {
            ctx.DrawText(s.Name, _labelFormat, new Vortice.Mathematics.Rect(rect.X + 1f, rect.Y + 1.2f, rect.Width, rect.Height), halo);
            ctx.DrawText(s.Name, _labelFormat, new Vortice.Mathematics.Rect(rect.X - 0.6f, rect.Y + 0.5f, rect.Width, rect.Height), halo);
        }
        using (var ink = ctx.CreateSolidColorBrush(Col(0xF2, 0xFF, 0xFF, 0xFF)))
            ctx.DrawText(s.Name, _labelFormat, rect, ink);
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
                var pxbuf = new byte[stride * h];
                src.CopyPixels(pxbuf, stride, 0);
                d2d = _host!.CreateBitmap(w, h, pxbuf, stride);
            }
        }
        catch { d2d = null; }
        _bmpCache[key] = d2d;
        return d2d;
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
            return;
        if (_prevHover >= 0)
            _preview?.OnPointerLeave();
        _prevHover = hover;
        if (hover < 0 || hover >= _slots.Count)
            return;
        var s = _slots[hover];
        if (s.Entry == null || string.IsNullOrWhiteSpace(s.Entry.Path))
            return;
        var path = s.Entry.Path; var args = s.Entry.Arguments;
        _previewSource = () => WindowPreviewService.GetWindowsForEntry(path, args);
        EnsureAnchor();
        AnchorOverSlot(s);
        _preview!.Placement = PreviewPlacement.Above;   // main dock is bottom-anchored
        _preview.OnPointerEnter();
    }

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
        _preview?.Close();
        _prevHover = -1;
    }

    // ---- Stage D: interaction (click-launch, drag reorder / drag-out delete,
    //       right-click menu, drop-files-to-pin) -------------------------------

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
                _pressX = lx; _pressY = ly; _dragX = lx; _dragY = ly;
                float hitR = _saturn ? _planetHitR : _gearR;
                _pressGear = Vector2.Distance(new Vector2(lx, ly), _gearC) <= hitR + 2f;
                _pressIdx = _pressGear ? -1 : HitSlot(lx, ly);
                _dragging = false;
                if (_pressIdx >= 0 || _pressGear)
                    SetCapture(_hwnd);
                return true;
            }
            case WM_MOUSEMOVE:
            {
                (float lx, float ly) = ClientDip(lParam);
                if (_barDrag)
                {
                    BarDragTo(ly);
                    Render();
                    return true;
                }
                if (_pressIdx < 0)
                    return false;
                _dragX = lx; _dragY = ly;
                if (!_dragging && (MathF.Abs(lx - _pressX) + MathF.Abs(ly - _pressY)) > _gIcon * 0.35f)
                {
                    _dragging = true;
                    _dragArrangeLastMs = Environment.TickCount64;   // seed the time-based reflow ease
                    GlassDragActiveChanged?.Invoke(true);   // keep the side dock shown as a drop target
                }
                if (_dragging)
                {
                    AdvanceDragArrange();   // keep neighbours in step with the cursor between ticks
                    Render();
                }
                return true;
            }
            case WM_MOUSEWHEEL:
            {
                if (_saturn || !_scrollable)
                    return false;
                int delta = unchecked((short)(((long)wParam >> 16) & 0xFFFF));
                _glassScrollTarget = Math.Clamp(_glassScrollTarget - _glassCellH * (delta / 120.0), 0, _glassScrollMax);
                Render();
                return true;
            }
            case WM_LBUTTONUP:
            {
                ReleaseCapture();
                if (_barDrag)
                {
                    _barDrag = false;
                    Render();
                    return true;
                }
                int idx = _pressIdx;
                bool wasDrag = _dragging;
                bool gear = _pressGear;
                (float lx, float ly) = ClientDip(lParam);
                _pressIdx = -1;
                _dragging = false;
                _pressGear = false;
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
                    HandleDropFiles(paths);
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
                    Vector2 d = _slots[arr[i]].Center - _slots[i].Center;
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

    private void ClickSlot(int idx)
    {
        var s = _slots[idx];
        if (s.Entry == null)
            return;
        try
        {
            _bounceIdx = idx; _bounceStart = Environment.TickCount64;   // launch hop
            // Launching dismisses the dock (and, via PanelDismissed, the side dock).
            AppLauncher.Launch(s.Entry, () => HidePanel());
        }
        catch (Exception ex) { Log.Warn("MainDockGpu", "launch failed: " + ex.Message); }
    }

    /// <summary>Drop after a drag: outside the slab → unpin; otherwise reorder to the
    /// grid slot nearest the drop point.</summary>
    private void DropSlot(int idx, float lx, float ly)
    {
        // Drag gesture finished — let the host retract the side-dock drop target.
        GlassDragActiveChanged?.Invoke(false);

        var s = _slots[idx];
        if (s.Entry == null) { Render(); return; }

        // Dropping onto the left-edge side dock pins it there (the main-dock entry
        // stays); checked first so a drag toward the dock adds rather than deletes.
        if (DropToSideDock != null)
        {
            var screen = new Point(_winX + lx, _winY + ly);
            if (DropToSideDock(screen, s.Entry))
            {
                Render();   // snap the dragged icon back into its slot
                return;
            }
        }

        // Dragged clear of the slab (with a margin) → unpin / delete.
        float m = _gIcon * 0.4f;
        bool outside = lx < _slabX - m || lx > _slabX + _slabW + m
                    || ly < _slabY - m || ly > _slabY + _slabH + m;
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
            else Render();
            return;
        }

        // Otherwise reorder using the same reading-order hit test as the live drag
        // preview so the icon lands at the grid cell the pointer is actually over.
        float dropY = _scrollable ? ly + (float)_glassScroll : ly;
        int tgt = ComputeGridTarget(lx, dropY, idx);
        if (tgt >= 0 && tgt != idx && idx < _config.Apps.Count && tgt < _config.Apps.Count)
        {
            var e = _config.Apps[idx];
            _config.Apps.RemoveAt(idx);
            _config.Apps.Insert(tgt, e);
            PersistAndRelayout();
        }
        else
        {
            Render();
        }
    }

    /// <summary>Pins shortcuts / executables dropped from Explorer / the desktop,
    /// inserting each at the pointer's grid position (mirrors the WPF dock OnDrop).</summary>
    private void HandleDropFiles(List<string> paths)
    {
        var entries = new List<AppEntry>();
        foreach (var p in paths)
        {
            try { var e = ShortcutResolver.CreateEntry(p); if (e != null) entries.Add(e); }
            catch { /* skip an unresolvable drop */ }
        }
        if (entries.Count == 0)
            return;
        bool changed = false;
        int at = DockSync.ResidentCount(_config);
        int cap = ThemeRegistry.Get(_config.Settings.Theme).MaxIcons;
        foreach (var entry in entries)
        {
            if (_config.Apps.Count >= cap)
                break;   // theme capacity reached (glass 42 / Saturn 42) — stop pinning
            if (_config.Apps.FindIndex(a => DockSync.Matches(a, entry)) >= 0)
                continue;   // already present — don't duplicate
            DockSync.InsertResident(_config, entry, at);
            at++;
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

    private void PersistAndRebuild()
    {
        try
        {
            DockSync.MirrorResidentToLeft(_config);
            ConfigStore.Save(_config);
        }
        catch (Exception ex) { Log.Warn("MainDockGpu", "persist failed: " + ex.Message); }
        AppsChanged?.Invoke();   // let the host re-mirror the resident region
        Rebuild();
    }

    /// <summary>Persists the config then relayouts in place (no window/host recreation) so
    /// a reorder drop does not flash. Used for reorders, where the icon count — and hence
    /// the window geometry — is unchanged.</summary>
    private void PersistAndRelayout()
    {
        try
        {
            DockSync.MirrorResidentToLeft(_config);
            ConfigStore.Save(_config);
        }
        catch (Exception ex) { Log.Warn("MainDockGpu", "persist failed: " + ex.Message); }
        AppsChanged?.Invoke();
        RelayoutInPlace();
    }

    /// <summary>Re-lays out the slots after a reorder while keeping the existing window and
    /// GPU host alive, so the dock does not flash. Falls back to a full <see cref="Rebuild"/>
    /// if the host is gone or the window rect would change.</summary>
    private void RelayoutInPlace()
    {
        if (_host == null || _hwnd == IntPtr.Zero) { Rebuild(); return; }
        int ow = _winW, oh = _winH, ox = _winX, oy = _winY;
        _pressIdx = -1; _dragging = false; _hover = -1;
        LayoutContent();
        if (_winW != ow || _winH != oh || _winX != ox || _winY != oy)
        {
            Rebuild();   // geometry changed → the swapchain/window must be recreated
            return;
        }
        if (_saturn) { _saturnLastMs = 0; }   // cache is window-sized, unchanged; just resume
        Render();
    }

    private void Rebuild()
    {
        bool wasVisible = _visible;
        _timer?.Stop();
        CloseSlotMenu();
        ClosePreview();
        if (_hwnd != IntPtr.Zero) s_instances.Remove(_hwnd);
        DisposeSaturnCache();
        _host?.Dispose(); _host = null;
        _labelFormat?.Dispose(); _labelFormat = null;
        _gearFormat?.Dispose(); _gearFormat = null;
        _clockFormat?.Dispose(); _clockFormat = null;
        _dwrite?.Dispose(); _dwrite = null;
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        foreach (var b in _bmpCache.Values) b?.Dispose();
        _bmpCache.Clear();
        _hover = -1; _pressIdx = -1; _dragging = false;
        try { Build(wasVisible); }
        catch (Exception ex) { Log.Warn("MainDockGpu", "rebuild failed: " + ex); }
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
        double half = _gIcon / 2.0; const double gap = 4.0;
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
        if (_slotMenu != null) { _slotMenu.IsOpen = false; _slotMenu = null; }
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
                ? WindowPreviewService.GetWindowsForEntry(s.Entry.Path, s.Entry.Arguments)
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

    /// <summary>Re-read config / running state and rebuild the layout in place.</summary>
    public void RefreshFromConfig()
    {
        if (!_realized) return;
        Rebuild();
    }

    public void ShowPanel() => Summon(pinned: false);
    public void ShowPinned() => Summon(pinned: true);

    private void Summon(bool pinned)
    {
        Realize();
        _shown = true;
        _pinned = pinned;
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
        _host?.SetIntro(0f, 0f, 0f);   // first frame fully transparent before the fade-in
        ShowNotchIfSaturn();
        if (!_weatherHooked) { _weather.Updated += OnWeatherUpdated; _weatherHooked = true; }
        _ = _weather.RefreshAsync();   // fetch weather promptly on show (self-throttled)
        _timer?.Start();
    }

    /// <summary>Repaint the clock line as soon as fresh weather arrives (the perpetual
    /// render loop picks the new suffix up on its next frame while shown).</summary>
    private void OnWeatherUpdated() => _lastClockMin = -1;

    /// <summary>Shows the phone-notch date/time panel when the Saturn theme is active
    /// (hidden for any other theme), sitting on the active monitor's top edge — or its
    /// bottom edge when the side dock is anchored to the top. Mirrors
    /// RadialWindow.ShowNotchIfSaturn so the GPU main dock owns the notch too.</summary>
    private void ShowNotchIfSaturn()
    {
        if (!_saturn)
        {
            _notch?.HideNotch();
            return;
        }
        _notch ??= UseGpuNotch ? new NotchClockWindowGpu() : (INotchClock)new NotchClockWindow();
        bool atBottom = _config.Settings.DockPosition == Models.DockSide.Top;
        _notch.ShowNotch(atBottom);
    }

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
        _notch?.HideNotch();        // retract the Saturn notch with the dock
        PanelDismissed?.Invoke();   // retract the side dock together with the main dock
        _onFaded = onFaded;
        // Dismiss is a PURE fade-out in both themes (no slide / scale) — mirrors the
        // WPF RootGrid 170ms opacity animation. Leave _summon at rest so the scene holds
        // its docked position while the compositor fades the whole visual to transparent.
        _summonDir = 0;
        _fadeFrom = _fadeOpacity; _fadeDir = -1; _fadeStart = Environment.TickCount64;
        _timer?.Start();
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

    /// <summary>Called once a dismiss slide reaches the bottom: hide the window so a
    /// dismissed dock costs nothing, then run any deferred callback.</summary>
    private void OnDismissComplete()
    {
        if (_hwnd != IntPtr.Zero)
            ShowWindow(_hwnd, SW_HIDE);
        _visible = false;
        _timer?.Stop();
        var cb = _onFaded;
        _onFaded = null;
        if (cb != null)
            Dispatcher.BeginInvoke(cb, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
        if (_weatherHooked) { _weather.Updated -= OnWeatherUpdated; _weatherHooked = false; }
        CloseSlotMenu();
        ClosePreview();
        if (_anchorWin != null) { try { _anchorWin.Close(); } catch { } _anchorWin = null; _anchorEl = null; _preview = null; }
        _notch?.HideNotch();
        _labelFormat?.Dispose();
        _gearFormat?.Dispose();
        _clockFormat?.Dispose();
        _dwrite?.Dispose();
        foreach (var b in _bmpCache.Values) b?.Dispose();
        _bmpCache.Clear();
        DisposeSaturnCache();
        _host?.Dispose();
        if (_hwnd != IntPtr.Zero) { s_instances.Remove(_hwnd); DestroyWindow(_hwnd); }
    }

    /// <summary>Device pixels per DIP for the active monitor (EnumDisplaySettings ÷ WPF
    /// DIP width — reliable before the window is realized, unlike GetDpiForWindow).</summary>
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
        catch { /* fall through */ }
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

    // ---- Raw Win32 NOREDIRECTIONBITMAP window (click-through for Stage A) ----

    private static readonly Dictionary<IntPtr, MainDockWindowGpu> s_instances = new();
    private static readonly WndProc s_wndProc = WndProcImpl;
    private static ushort s_atom;

    private static IntPtr WndProcImpl(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (s_instances.TryGetValue(h, out var self) && self.HandleMessage(m, w, l, out var res))
            return res;
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
                lpszClassName = "PolarisMainDockGpu",
            };
            s_atom = RegisterClassExW(ref wc);
        }
        // Interactive window (Stage D): receives mouse + WM_DROPFILES. WM_NCHITTEST
        // returns HTTRANSPARENT outside the glass slab so the empty headroom passes
        // clicks through to the desktop. Still WS_EX_NOACTIVATE so it never steals focus.
        return CreateWindowExW(
            WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST |
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "PolarisMainDockGpu", string.Empty, WS_POPUP,
            0, 0, w, h, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
    }

    private delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int GWL_EXSTYLE = -20;
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr h, int idx);
    [DllImport("user32.dll")] private static extern int SetWindowLongW(IntPtr h, int idx, int val);
    private const uint WS_POPUP = 0x80000000;
    private const int SW_SHOWNOACTIVATE = 4;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const int SW_HIDE = 0;

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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? n);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
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
