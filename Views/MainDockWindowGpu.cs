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
    private bool _pressGear;                     // mouse-down landed on the gear
    private readonly List<IconSlot> _slots = new();

    // ---- Stage B magnify wave state ----
    private DispatcherTimer? _timer;
    private IDWriteFactory? _dwrite;
    private IDWriteTextFormat? _labelFormat;
    private IDWriteTextFormat? _gearFormat;
    private float[] _waveCur = Array.Empty<float>();   // smoothed per-icon scale
    private float[] _waveOffX = Array.Empty<float>();  // smoothed spread offset (DIP)
    private float[] _waveOffY = Array.Empty<float>();
    private int _hover = -1;

    // ---- Stage D interaction state ----
    private int _pressIdx = -1;          // slot under the mouse-down, or -1
    private bool _dragging;              // press has crossed the drag threshold
    private float _pressX, _pressY;      // mouse-down point (window-local DIP)
    private float _dragX, _dragY;        // current drag point (window-local DIP)
    private int _bounceIdx = -1;         // slot playing the launch hop, or -1
    private long _bounceStart;
    private const long BounceDurMs = 480;
    private System.Windows.Controls.Primitives.Popup? _slotMenu;

    // ---- Saturn theme state ----
    private bool _saturn;                 // active theme is the Saturn ring system
    private SaturnScene.Geom _sg;         // Saturn ring/planet geometry (window-local DIP)
    private float[] _slotG = Array.Empty<float>();   // per-slot icon draw size (Saturn rings differ)
    private const float SaturnEnlarge = 1.10f;
    private const float SaturnDiskEnlarge = 1.3f;
    private const float SaturnInnerIconScale = 0.85f / SaturnEnlarge;
    private const float SaturnOuterIconScale = 1.0f;
    private const float RingTiltY = 0.97f;
    private const int Ring0Cap = 14;
    private const int Ring1Cap = 28;

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
        _slots.Clear();
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

        // Settings gear button: a small circle tucked into the slab's top-right band.
        _gearR = _effIcon * 0.30f;
        _gearC = new Vector2(_slabX + _slabW - _gearR - _effIcon * 0.28f, _slabY + _gearR + _effIcon * 0.10f);
        // Pinned-icon grid positions (window-local DIP) via the theme layout.
        var apps = _config.Apps;
        int count = Math.Min(apps.Count, LiquidGlassTheme.Capacity);
        var slots = ((LiquidGlassTheme)ThemeRegistry.Get("liquidglass"))
            .ComputeSlots(count, new Point(centerX, dockCenterY), _config.Settings, out _);
        var running = RunningAppTracker.SnapshotRunning();
        var noTitles = new List<string>();
        var noAumids = new HashSet<string>();
        for (int i = 0; i < count && i < slots.Count; i++)
        {
            var entry = apps[i];
            var img = IconExtractor.GetCached(entry.EffectiveIconSource, _iconCache);
            bool run = RunningAppTracker.IsEntryRunning(entry, running, noTitles, noAumids);
            _slots.Add(new IconSlot(new Vector2((float)slots[i].X, (float)slots[i].Y),
                entry.EffectiveIconSource, entry.Name, run, img, entry));
        }

        }

        // Magnify wave state starts at rest (scale 1, no spread).
        _waveCur = new float[_slots.Count];
        _waveOffX = new float[_slots.Count];
        _waveOffY = new float[_slots.Count];
        Array.Fill(_waveCur, 1f);

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
        _labelFormat = _dwrite.CreateTextFormat("Microsoft YaHei UI", null, Vortice.DirectWrite.FontWeight.SemiBold,
            FontStyle.Normal, Vortice.DirectWrite.FontStretch.Normal, 13f, "zh-cn");
        _labelFormat.TextAlignment = Vortice.DirectWrite.TextAlignment.Center;
        _labelFormat.ParagraphAlignment = ParagraphAlignment.Center;
        _gearFormat = _dwrite.CreateTextFormat("Segoe UI Symbol", null, Vortice.DirectWrite.FontWeight.Normal,
            FontStyle.Normal, Vortice.DirectWrite.FontStretch.Normal, _gearR * 1.05f, "en-us");
        _gearFormat.TextAlignment = Vortice.DirectWrite.TextAlignment.Center;
        _gearFormat.ParagraphAlignment = ParagraphAlignment.Center;

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
        _gearR = _effIcon * 0.30f; // unused on Saturn but keeps _gearFormat sizing valid
        _opacity = _sg.DiscOpacity;

        // Saturn pops in place for now (rings-expand summon lands in Stage 4).
        _riseUnit = 0f;

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

        bool active = false;
        Vector2 cur = default;
        if (!_dragging && GetCursorPos(out POINT p))
        {
            cur = new Vector2((float)(p.X / _dpi - _winX), (float)(p.Y / _dpi - _winY));
            float nearest = float.MaxValue;
            for (int i = 0; i < _slots.Count; i++)
                nearest = Math.Min(nearest, Vector2.Distance(_slots[i].Center, cur));
            active = nearest <= _effIcon * 1.3f;
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

        for (int i = 0; i < _slots.Count; i++)
        {
            float d = active ? Vector2.Distance(_slots[i].Center, cur) : 0f;
            float target = active ? WaveScaleAt(d) : 1f;
            float c = _waveCur[i] + (target - _waveCur[i]) * k;
            _waveCur[i] = c;
            maxDelta = Math.Max(maxDelta, Math.Abs(target - c));

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

        // Hover only when the pointer is genuinely over an icon's cell.
        _hover = active && focal >= 0 && best <= _effIcon * 0.85f ? focal : -1;

        bool bouncing = _bounceIdx >= 0 && (Environment.TickCount64 - _bounceStart) <= BounceDurMs;
        if (!bouncing) _bounceIdx = -1;
        if (animating || active || _dragging || bouncing || maxDelta > 0.001f)
            Render();
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
        // dismissed and let it ease up into its docked rest position. BackEase-out
        // on the way in gives a soft settle overshoot; ease-in on the way out.
        float pos = _summonDir < 0 ? EaseInCubic(_summon) : BackEaseOut(_summon);
        float riseOff = (1f - pos) * _riseUnit;
        bool slid = riseOff > 0.01f;
        if (slid)
            ctx.Transform = System.Numerics.Matrix3x2.CreateTranslation(0f, riseOff);

        if (_saturn)
        {
            SaturnScene.Draw(ctx, _sg);
        }
        else
        {
        // Floating liquid-glass slab (drop shadow on, since the main dock floats above
        // the desktop rather than being flush to a screen edge).
        GlassSlab.DrawGlass(ctx, _slabX, _slabY, _slabW, _slabH, _radius, _opacity, _frost, shadowExtent: 26f);

        // Stereoscopic rim: a soft cool glow + crisp dark/bright double rim, mirroring
        // DrawGlassPanel's slabGlow/slabShade/slabRim strokes.
        var slab = new RoundedRectangle { Rect = new Vortice.Mathematics.Rect(_slabX, _slabY, _slabW, _slabH), RadiusX = _radius, RadiusY = _radius };
        using (var glow = ctx.CreateSolidColorBrush(Col(0x73, 0xBF, 0xE0, 0xFF)))
            ctx.DrawRoundedRectangle(slab, glow, 5f);
        using (var shade = ctx.CreateSolidColorBrush(Col(0x80, 0x06, 0x0B, 0x16)))
            ctx.DrawRoundedRectangle(slab, shade, 2.4f);
        using (var rim = ctx.CreateSolidColorBrush(Col(0xE6, 0xEA, 0xF4, 0xFF)))
            ctx.DrawRoundedRectangle(slab, rim, 1.4f);

        DrawGear(ctx);
        }

        // Draw smallest-first so the magnified (focal) icon sits on top of its neighbours.
        // The dragged icon is skipped here and drawn last, lifted under the cursor.
        int dragIdx = _dragging ? _pressIdx : -1;
        var order = new int[_slots.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => _waveCur[a].CompareTo(_waveCur[b]));
        foreach (int i in order)
        {
            if (i == dragIdx)
                continue;
            var off = new Vector2(_waveOffX[i], _waveOffY[i] - BounceOffset(i));
            DrawIcon(ctx, _slots[i], _waveCur[i], off, _saturn && i < _slotG.Length ? _slotG[i] : 0f);
        }

        if (dragIdx >= 0 && dragIdx < _slots.Count)
        {
            // The dragged icon follows the cursor, lifted 1.12x with no spread.
            var s = _slots[dragIdx];
            var moved = new IconSlot(new Vector2(_dragX, _dragY), s.IconKey, s.Name, s.Running, s.Image, s.Entry);
            DrawIcon(ctx, moved, 1.12f, Vector2.Zero, _saturn && dragIdx < _slotG.Length ? _slotG[dragIdx] : 0f);
        }
        else if (_hover >= 0 && _hover < _slots.Count)
            DrawHoverLabel(ctx, _slots[_hover], _waveCur[_hover]);

        if (slid)
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

    /// <summary>Settings gear: a small blue disc with a ⚙ glyph in the slab's top
    /// band; clicking it asks the host to open the settings window (parity with the
    /// WPF dock's centre gear button).</summary>
    private void DrawGear(ID2D1DeviceContext ctx)
    {
        if (_gearFormat == null) return;
        var e = new Vortice.Direct2D1.Ellipse(_gearC, _gearR, _gearR);
        using (var fill = ctx.CreateSolidColorBrush(Col(0xCC, 0x2A, 0x6C, 0xF0)))
            ctx.FillEllipse(e, fill);
        using (var rim = ctx.CreateSolidColorBrush(Col(0xEE, 0xFF, 0xFF, 0xFF)))
            ctx.DrawEllipse(e, rim, 1.4f);
        var rect = new Vortice.Mathematics.Rect(_gearC.X - _gearR, _gearC.Y - _gearR, _gearR * 2f, _gearR * 2f);
        using (var fg = ctx.CreateSolidColorBrush(Col(0xFF, 0xFF, 0xFF, 0xFF)))
            ctx.DrawText("\u2699", _gearFormat, rect, fg);
    }

    private void DrawIcon(ID2D1DeviceContext ctx, in IconSlot s, float scale, Vector2 off, float gIcon = 0f)
    {
        float g = gIcon > 0f ? gIcon : _gIcon, half = g / 2f, cx = s.Center.X + off.X, cy = s.Center.Y + off.Y;
        var center = new Vector2(cx, cy);
        var wave = Matrix3x2.CreateScale(scale, scale, center);

        // Running indicator (glass theme): a soft blue glow border hugging the icon,
        // mirroring RadialIcon's RunningGlowBorder (#3FA9FF, rounded, blurred). Drawn
        // under the icon and scaled with the wave so a magnified running app keeps its
        // ring. Static (no breathe/sweep) to stay idle-friendly, matching the GPU side
        // dock. A few concentric strokes of falling alpha fake the Gaussian halo.
        if (s.Running && !_saturn)
        {
            ctx.Transform = wave;
            float box = g * 0.9f, rr = box * 0.24f;
            var rect = new Vortice.Mathematics.Rect(cx - box / 2f, cy - box / 2f, box, box);
            (float w, byte a)[] ring = { (7f, 0x1E), (4.5f, 0x3A), (2.4f, 0x80) };
            foreach (var (sw, a) in ring)
                using (var br = ctx.CreateSolidColorBrush(Col(a, 0x3F, 0xA9, 0xFF)))
                    ctx.DrawRoundedRectangle(new RoundedRectangle { Rect = rect, RadiusX = rr, RadiusY = rr }, br, sw);
            using (var hot = ctx.CreateSolidColorBrush(Col(0xC8, 0x6F, 0xD3, 0xFF)))
                ctx.DrawRoundedRectangle(new RoundedRectangle { Rect = rect, RadiusX = rr, RadiusY = rr }, hot, 1.2f);
            ctx.Transform = Matrix3x2.Identity;
        }

        var bmp = GetBitmap(ctx, s.IconKey, s.Image);
        if (bmp == null)
            return;
        float pad = g * 0.06f, dstX = cx - half + pad, dstY = cy - half + pad, dstSz = g - pad * 2;
        var bs = bmp.Size;
        ctx.Transform = Matrix3x2.CreateScale(dstSz / Math.Max(1f, bs.Width), dstSz / Math.Max(1f, bs.Height))
                      * Matrix3x2.CreateTranslation(dstX, dstY) * wave;
        ctx.DrawBitmap(bmp, 1f, InterpolationMode.HighQualityCubic);
        ctx.Transform = Matrix3x2.Identity;
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
        float w = Math.Max(48f, s.Name.Length * 13f * 0.95f + 20f), h = 24f;
        float lx = s.Center.X, ly = s.Center.Y + zoomedHalf + 2f + h / 2f;
        var rect = new Vortice.Mathematics.Rect(lx - w / 2f, ly - h / 2f, w, h);
        using (var bg = ctx.CreateSolidColorBrush(Col(0x05, 0x1A, 0x1A, 0x1A)))
            ctx.FillRoundedRectangle(new RoundedRectangle { Rect = rect, RadiusX = 7f, RadiusY = 7f }, bg);
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
                bool inside = lx >= _slabX - m && lx <= _slabX + _slabW + m
                           && ly >= _slabY - m && ly <= _slabY + _slabH + m;
                result = inside ? HTCLIENT : HTTRANSPARENT;
                return true;
            }
            case WM_LBUTTONDOWN:
            {
                (float lx, float ly) = ClientDip(lParam);
                _pressX = lx; _pressY = ly; _dragX = lx; _dragY = ly;
                _pressGear = Vector2.Distance(new Vector2(lx, ly), _gearC) <= _gearR + 2f;
                _pressIdx = _pressGear ? -1 : HitSlot(lx, ly);
                _dragging = false;
                if (_pressIdx >= 0 || _pressGear)
                    SetCapture(_hwnd);
                return true;
            }
            case WM_MOUSEMOVE:
            {
                if (_pressIdx < 0)
                    return false;
                (float lx, float ly) = ClientDip(lParam);
                _dragX = lx; _dragY = ly;
                if (!_dragging && (MathF.Abs(lx - _pressX) + MathF.Abs(ly - _pressY)) > _gIcon * 0.35f)
                {
                    _dragging = true;
                    GlassDragActiveChanged?.Invoke(true);   // keep the side dock shown as a drop target
                }
                if (_dragging)
                    Render();
                return true;
            }
            case WM_LBUTTONUP:
            {
                ReleaseCapture();
                int idx = _pressIdx;
                bool wasDrag = _dragging;
                bool gear = _pressGear;
                (float lx, float ly) = ClientDip(lParam);
                _pressIdx = -1;
                _dragging = false;
                _pressGear = false;
                if (gear)
                {
                    if (Vector2.Distance(new Vector2(lx, ly), _gearC) <= _gearR + 2f)
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
        int best = -1; float bestD = _gIcon * 0.6f;
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

        // Otherwise reorder to the slot nearest the drop point (2-D grid).
        int tgt = -1; float bestD = float.MaxValue;
        for (int i = 0; i < _slots.Count; i++)
        {
            float d = Vector2.Distance(_slots[i].Center, new Vector2(lx, ly));
            if (d < bestD) { bestD = d; tgt = i; }
        }
        if (tgt >= 0 && tgt != idx && idx < _config.Apps.Count && tgt < _config.Apps.Count)
        {
            var e = _config.Apps[idx];
            _config.Apps.RemoveAt(idx);
            _config.Apps.Insert(tgt, e);
            PersistAndRebuild();
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
        foreach (var entry in entries)
        {
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

    private void Rebuild()
    {
        bool wasVisible = _visible;
        _timer?.Stop();
        CloseSlotMenu();
        if (_hwnd != IntPtr.Zero) s_instances.Remove(_hwnd);
        _host?.Dispose(); _host = null;
        _labelFormat?.Dispose(); _labelFormat = null;
        _gearFormat?.Dispose(); _gearFormat = null;
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
        // Start fully dismissed (off-screen) so the very first frame after the
        // rebuild slides in rather than popping at rest.
        _summon = 0f; _summonDir = +1; _summonLast = Environment.TickCount64;
        if (!_visible)
        {
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            _visible = true;
        }
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
        Rebuild();                 // pick up config / running changes; rebuilds visible
        _timer?.Start();
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
        PanelDismissed?.Invoke();   // retract the side dock together with the main dock
        _onFaded = onFaded;
        _summonDir = -1; _summonLast = Environment.TickCount64;
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
        CloseSlotMenu();
        _labelFormat?.Dispose();
        _gearFormat?.Dispose();
        _dwrite?.Dispose();
        foreach (var b in _bmpCache.Values) b?.Dispose();
        _bmpCache.Clear();
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
