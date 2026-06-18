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

/// <summary>GPU side dock — STAGE A (static render) + STAGE B (hover hit-test + a
/// floating name label). Draws the liquid-glass slab and the pinned icon column in
/// Direct2D under DirectComposition; a cursor poll hit-tests the icons so a hovered
/// one shows its name on a small label (the window stays click-through — launch/drag
/// come in later stages). Shown behind POLARIS_GPU_SIDEDOCK=1. Assumes 100% DPI.</summary>
internal sealed class SideDockWindowGpu : IDisposable
{
    private const float GlassIconScale = 1.32f;
    private const float SideDockScaleK = 0.70f;
    private const float HoverScale = 1.5f;

    private readonly struct Slot
    {
        public readonly Vector2 Center;
        public readonly float G;
        public readonly string Name;
        public readonly AppEntry Entry;
        public readonly bool Running;
        public Slot(Vector2 c, float g, string name, AppEntry e, bool run)
        { Center = c; G = g; Name = name; Entry = e; Running = run; }
    }

    private readonly AppConfig _config;
    private IntPtr _hwnd;
    private CompositionHost? _host;
    private IDWriteFactory? _dwrite;
    private IDWriteTextFormat? _labelFormat;
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
    private float[] _waveCur = Array.Empty<float>();
    private const float WaveSupport = 2.3f;

    public SideDockWindowGpu(AppConfig config) => _config = config;

    public void Show()
    {
        try { Build(); }
        catch (Exception ex) { Log.Warn("SideDockGpu", "failed: " + ex); }
    }

    private void Build()
    {
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

        double availForCells = usableMain - (startPad + endPad);
        if (pinnedCount > 0 && pinnedCount * cellH > availForCells)
            cellH = Math.Max(gIcon * 1.04, availForCells / pinnedCount);
        int maxVisible = Math.Max(1, (int)Math.Floor(availForCells / cellH));
        int pinnedVisible = Math.Min(pinnedCount, maxVisible);
        double pinnedBlockH = pinnedVisible * cellH;
        double slabMainLen = startPad + pinnedBlockH + endPad;

        double centroidFromSlab = startPad + (pinnedVisible > 0 ? cellH * pinnedVisible / 2.0 : 0);
        double slabMain = (startReserve + usableMain / 2.0) - centroidFromSlab;
        slabMain = Math.Min(slabMain, mainExtent - endReserve - slabMainLen);
        slabMain = Math.Max(slabMain, startReserve);
        double pinnedAreaMain = slabMain + startPad;

        double glassPad = gIcon * 0.30;
        double bodyCross = slabCross;
        double bodyCrossLen = (colCenterCross - bodyCross) + gIcon / 2.0 + glassPad;
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
        for (int i = 0; i < pinnedVisible && i < apps.Count; i++)
        {
            var entry = apps[i];
            double mainC = pinnedAreaMain + i * cellH + cellH / 2.0;
            (float cx, float cy) = ToLocal(_side, mainC, colCenterCross, _winW, _winH);
            bool run = RunningAppTracker.IsEntryRunning(entry, running, new List<string>(), new HashSet<string>());
            _slots.Add(new Slot(new Vector2(cx, cy), (float)gIcon, entry.Name, entry, run));
        }
        _gIcon = (float)gIcon;
        _cellH = (float)cellH;
        _waveCur = new float[_slots.Count];
        Array.Fill(_waveCur, 1f);

        _hwnd = CreateWindow(_winW, _winH);
        uint rawDpi = GetDpiForWindow(_hwnd);
        _dpi = rawDpi >= 48 ? rawDpi / 96.0 : 1.0;
        // Layout is computed in DIPs (MonitorLayout returns DIPs); the Win32 window
        // + DComp swap chain live in PHYSICAL pixels. Size the window to physical px
        // and tell D2D the target DPI so all DIP-space drawing scales up 1:1.
        int pw = (int)Math.Ceiling(_winW * _dpi), ph = (int)Math.Ceiling(_winH * _dpi);
        int px = (int)Math.Round(_winX * _dpi), py = (int)Math.Round(_winY * _dpi);
        SetWindowPos(_hwnd, HWND_TOPMOST, px, py, pw, ph, SWP_NOACTIVATE);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        _host = new CompositionHost(_hwnd, pw, ph, (float)(96.0 * _dpi));
        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
        _labelFormat = _dwrite.CreateTextFormat("Microsoft YaHei UI", null, FontWeight.Normal,
            FontStyle.Normal, FontStretch.Normal, 13f, "zh-cn");
        _labelFormat.TextAlignment = TextAlignment.Center;
        _labelFormat.ParagraphAlignment = ParagraphAlignment.Center;

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

    private void Tick()
    {
        if (_host == null)
            return;
        bool vertical = _side is DockSide.Left or DockSide.Right;
        bool active = false;
        float curMain = 0;
        if (GetCursorPos(out POINT p))
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

        // Render every frame while the wave is live; once settled at rest, render
        // one final frame and idle (the timer keeps polling for re-entry).
        if (active || maxDelta > 0.001f)
            Render();
    }

    private static Color4 Col(byte a, byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, a / 255f);

    private void Render()
    {
        if (_host == null)
            return;
        var ctx = _host.Context;
        ctx.BeginDraw();
        ctx.Clear(Col(0, 0, 0, 0));
        GlassSlab.DrawGlass(ctx, _sx, _sy, _sw, _sh, _trayRadius, _opacity, _frost);

        // Draw smallest-first so the magnified (focal) icon sits on top.
        var order = new int[_slots.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => _waveCur[a].CompareTo(_waveCur[b]));
        foreach (int i in order)
        {
            float scale = _waveCur[i];
            Vector2 pop = PopOffset((scale - 1f) * _gIcon * 1.18f);
            DrawIcon(ctx, _slots[i], scale, pop);
        }

        if (_hover >= 0 && _hover < _slots.Count)
            DrawHoverLabel(ctx, _slots[_hover], _waveCur[_hover]);
        ctx.EndDraw();
        _host.Present();
    }

    private void DrawIcon(ID2D1DeviceContext ctx, in Slot s, float scale, Vector2 pop)
    {
        float g = s.G, half = g / 2f, cx = s.Center.X, cy = s.Center.Y;
        var wave = Matrix3x2.CreateScale(scale, scale, s.Center) * Matrix3x2.CreateTranslation(pop);

        ctx.Transform = wave;
        var plate = new Rect(cx - half, cy - half, g, g);
        using (var pb = ctx.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 0x08 / 255f)))
            ctx.FillRoundedRectangle(new RoundedRectangle { Rect = plate, RadiusX = 12f, RadiusY = 12f }, pb);

        var bmp = GetBitmap(ctx, s.Entry);
        if (bmp != null)
        {
            float pad = g * 0.14f, dstX = cx - half + pad, dstY = cy - half + pad, dstSz = g - pad * 2;
            var bs = bmp.Size;
            ctx.Transform = Matrix3x2.CreateScale(dstSz / Math.Max(1f, bs.Width), dstSz / Math.Max(1f, bs.Height))
                          * Matrix3x2.CreateTranslation(dstX, dstY) * wave;
            ctx.DrawBitmap(bmp, 1f, InterpolationMode.HighQualityCubic);
            ctx.Transform = wave;
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
            using (var gl = ctx.CreateSolidColorBrush(new Color4(0x5C / 255f, 1f, 0x7A / 255f, 0.5f)))
                ctx.FillEllipse(new Ellipse(new Vector2(dx, dy), glow / 2f, glow / 2f), gl);
            using (var co = ctx.CreateSolidColorBrush(new Color4(0x4C / 255f, 0xE0 / 255f, 0x6B / 255f, 1f)))
                ctx.FillEllipse(new Ellipse(new Vector2(dx, dy), dot / 2f, dot / 2f), co);
        }
        ctx.Transform = Matrix3x2.Identity;
    }

    private void DrawHoverLabel(ID2D1DeviceContext ctx, in Slot s, float scale)
    {
        if (_labelFormat == null || string.IsNullOrEmpty(s.Name))
            return;
        // Clear the magnified + popped focal icon.
        float reach = s.G / 2f * scale + (scale - 1f) * _gIcon * 1.18f;
        float w = Math.Max(40f, s.Name.Length * 13f * 0.95f + 18f), h = 24f, gap = 8f;
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
        using (var ink = ctx.CreateSolidColorBrush(Col(0xE6, 0xFF, 0xFF, 0xFF)))
            ctx.DrawText(s.Name, _labelFormat, rect, ink);
    }

    private ID2D1Bitmap? GetBitmap(ID2D1DeviceContext ctx, AppEntry entry)
    {
        string key = entry.EffectiveIconSource;
        if (_bmpCache.TryGetValue(key, out var cached))
            return cached;
        ID2D1Bitmap? d2d = null;
        try
        {
            var src = IconExtractor.GetCached(key, _iconCache);
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

    public void Dispose()
    {
        _timer?.Stop();
        foreach (var b in _bmpCache.Values) b?.Dispose();
        _bmpCache.Clear();
        _host?.Dispose();
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
    }

    private static (float x, float y) ToLocal(DockSide side, double main, double cross, int winW, int winH) => side switch
    {
        DockSide.Left => ((float)cross, (float)main),
        DockSide.Right => ((float)(winW - cross), (float)main),
        DockSide.Top => ((float)main, (float)cross),
        _ => ((float)main, (float)(winH - cross)),
    };

    // ---- Raw Win32 NOREDIRECTIONBITMAP window (click-through) -----------------

    private static readonly WndProc s_wndProc = DefWindowProcW;
    private static ushort s_atom;

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
        return CreateWindowExW(
            WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST | WS_EX_TRANSPARENT |
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
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;

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
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? n);
}
