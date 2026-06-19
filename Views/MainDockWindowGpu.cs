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
internal sealed class MainDockWindowGpu : IDisposable
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
    private readonly List<IconSlot> _slots = new();

    // ---- Stage B magnify wave state ----
    private DispatcherTimer? _timer;
    private IDWriteFactory? _dwrite;
    private IDWriteTextFormat? _labelFormat;
    private float[] _waveCur = Array.Empty<float>();   // smoothed per-icon scale
    private float[] _waveOffX = Array.Empty<float>();  // smoothed spread offset (DIP)
    private float[] _waveOffY = Array.Empty<float>();
    private int _hover = -1;

    private readonly struct IconSlot
    {
        public readonly Vector2 Center;       // window-local DIP
        public readonly string IconKey;
        public readonly string Name;
        public readonly bool Running;
        public readonly BitmapSource? Image;
        public IconSlot(Vector2 c, string key, string name, bool running, BitmapSource? img)
        { Center = c; IconKey = key; Name = name; Running = running; Image = img; }
    }

    public MainDockWindowGpu(AppConfig config) => _config = config;

    public void Show()
    {
        try { Build(); }
        catch (Exception ex) { Log.Warn("MainDockGpu", "failed: " + ex); }
    }

    private void Build()
    {
        _slots.Clear();
        var mon = MonitorLayout.ActiveBounds;
        var wa = MonitorLayout.ActiveWorkArea;
        double sw = mon.Width, sh = mon.Height;

        double icon = _config.Settings.IconSize * ThemeScale;   // EffectiveIconSize (glass)
        double gIcon = icon * GlassIconScale;
        double cellW = icon * LiquidGlassTheme.ColumnPitch;
        double cellH = icon * LiquidGlassTheme.RowPitch;
        double gridW = (LiquidGlassTheme.Columns - 1) * cellW;
        double padX = icon * 1.15;
        double dockW = gridW + icon + padX * 2;

        // Bottom-docked margin: slab bottom rests above the system taskbar.
        double taskbarH = Math.Max(0.0, mon.Bottom - wa.Bottom);
        double bottomMargin = taskbarH + icon * 0.12;

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
                entry.EffectiveIconSource, entry.Name, run, img));
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
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        _host = new CompositionHost(_hwnd, pw, ph, (float)(96.0 * _dpi));

        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
        _labelFormat = _dwrite.CreateTextFormat("Microsoft YaHei UI", null, Vortice.DirectWrite.FontWeight.SemiBold,
            FontStyle.Normal, Vortice.DirectWrite.FontStretch.Normal, 13f, "zh-cn");
        _labelFormat.TextAlignment = Vortice.DirectWrite.TextAlignment.Center;
        _labelFormat.ParagraphAlignment = ParagraphAlignment.Center;

        Render();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private static Color4 Col(byte a, byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, a / 255f);

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

        bool active = false;
        Vector2 cur = default;
        if (GetCursorPos(out POINT p))
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

        if (active || maxDelta > 0.001f)
            Render();
    }

    private void Render()
    {
        if (_host == null)
            return;
        var ctx = _host.Context;
        ctx.BeginDraw();
        ctx.Clear(Col(0, 0, 0, 0));

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

        // Draw smallest-first so the magnified (focal) icon sits on top of its neighbours.
        var order = new int[_slots.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => _waveCur[a].CompareTo(_waveCur[b]));
        foreach (int i in order)
            DrawIcon(ctx, _slots[i], _waveCur[i], new Vector2(_waveOffX[i], _waveOffY[i]));

        if (_hover >= 0 && _hover < _slots.Count)
            DrawHoverLabel(ctx, _slots[_hover], _waveCur[_hover]);

        ctx.EndDraw();
        _host.Present();
    }

    private void DrawIcon(ID2D1DeviceContext ctx, in IconSlot s, float scale, Vector2 off)
    {
        float g = _gIcon, half = g / 2f, cx = s.Center.X + off.X, cy = s.Center.Y + off.Y;
        var center = new Vector2(cx, cy);
        var wave = Matrix3x2.CreateScale(scale, scale, center);

        // Running indicator (glass theme): a soft blue glow border hugging the icon,
        // mirroring RadialIcon's RunningGlowBorder (#3FA9FF, rounded, blurred). Drawn
        // under the icon and scaled with the wave so a magnified running app keeps its
        // ring. Static (no breathe/sweep) to stay idle-friendly, matching the GPU side
        // dock. A few concentric strokes of falling alpha fake the Gaussian halo.
        if (s.Running)
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

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
        _labelFormat?.Dispose();
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
        => DefWindowProcW(h, m, w, l);

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
        // Click-through for Stage A (WS_EX_TRANSPARENT); interaction lands later.
        return CreateWindowExW(
            WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST | WS_EX_TRANSPARENT |
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
}
