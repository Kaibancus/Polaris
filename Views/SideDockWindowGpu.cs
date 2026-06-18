using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Polaris.Models;
using Polaris.Services;
using Polaris.Services.Gpu;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Polaris.Views;

/// <summary>GPU side dock — STAGE A (static renderer): draws the liquid-glass slab
/// and the pinned icon column (faint plate + icon bitmap + green running dot) for a
/// LEFT vertical dock entirely in Direct2D under DirectComposition, reusing the real
/// dock's layout formulas. Shown for visual validation behind POLARIS_GPU_SIDEDOCK=1.
/// Interaction (hit-test, magnify wave, running strip, drag) comes in later stages.
/// Assumes 100% DPI (matching the other GPU spikes).</summary>
internal sealed class SideDockWindowGpu : IDisposable
{
    private const float GlassIconScale = 1.32f;
    private const float SideDockScaleK = 0.70f;          // EffectiveIconSize*GlassIconScale = IconSize*uiScale*0.70
    private const float HoverScale = 1.5f;

    private readonly AppConfig _config;
    private IntPtr _hwnd;
    private CompositionHost? _host;
    private readonly Dictionary<string, ID2D1Bitmap?> _bmpCache = new();
    private readonly Dictionary<string, BitmapSource?> _iconCache = new();

    public SideDockWindowGpu(AppConfig config) => _config = config;

    public void Show()
    {
        try { Build(); }
        catch (Exception ex) { Log.Warn("SideDockGpu", "stage-A failed: " + ex); }
    }

    private void Build()
    {
        var wa = MonitorLayout.ActiveWorkArea;
        var side = _config.Settings.DockPosition;
        bool vertical = side is DockSide.Left or DockSide.Right;
        double uiScale = Math.Clamp(MonitorLayout.ActiveBounds.Height / 1080.0, 1.0, 2.0);
        double iconSize = _config.Settings.IconSize;
        double effIcon = iconSize * uiScale * (SideDockScaleK / GlassIconScale);
        double gIcon = effIcon * GlassIconScale;                 // = iconSize*uiScale*0.70
        double cellH = effIcon * 1.46;

        double crossGap = 1 * uiScale;
        double padCross = gIcon * (HoverScale - 1.0) / 2.0 + effIcon * 0.12;
        double slabCrossLen = gIcon + padCross * 2.0;
        double slabCross = crossGap;
        double edgeBias = gIcon * (HoverScale - 1.0) * 0.30;
        double colCenterCross = slabCross + slabCrossLen / 2.0 - edgeBias;

        double startPad = effIcon * 0.7, endPad = effIcon * 0.7;
        double thickness = (vertical ? gIcon * HoverScale + 240 * uiScale : gIcon * HoverScale + 130 * uiScale);
        int winW = (int)Math.Ceiling(vertical ? thickness : wa.Width);
        int winH = (int)Math.Ceiling(vertical ? wa.Height : thickness);
        double mainExtent = vertical ? wa.Height : wa.Width;

        double startReserve = 12 * uiScale, endReserve = vertical ? 56 * uiScale : 12 * uiScale;
        double usableMain = mainExtent - startReserve - endReserve;

        var apps = _config.SideDockApps;
        int pinnedCount = apps.Count;
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
        double trayRadius = iconSize * uiScale * 0.42;
        double opacity = 1.0 - Math.Clamp(_config.Settings.PanelTransparency, 0.0, 1.0);
        double frost = GlassChrome.FrostStrengthFor(_config.Settings.PanelTransparency);

        int px = side switch
        {
            DockSide.Left => (int)wa.Left,
            DockSide.Right => (int)(wa.Right - winW),
            DockSide.Top => (int)wa.Left,
            _ => (int)wa.Left,
        };
        int py = side switch
        {
            DockSide.Top => (int)wa.Top,
            DockSide.Bottom => (int)(wa.Bottom - winH),
            _ => (int)wa.Top,
        };
        if (side == DockSide.Bottom) py = (int)(wa.Bottom - winH);

        _hwnd = CreateWindow(winW, winH);
        SetWindowPos(_hwnd, HWND_TOPMOST, px, py, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        _host = new CompositionHost(_hwnd, winW, winH);

        // Slab rect from LogicalRect(slabMain, bodyCross, slabMainLen, bodyCrossLen).
        (float sx, float sy, float sw, float sh) = side switch
        {
            DockSide.Left => ((float)bodyCross, (float)slabMain, (float)bodyCrossLen, (float)slabMainLen),
            DockSide.Right => ((float)(winW - bodyCross - bodyCrossLen), (float)slabMain, (float)bodyCrossLen, (float)slabMainLen),
            DockSide.Top => ((float)slabMain, (float)bodyCross, (float)slabMainLen, (float)bodyCrossLen),
            _ => ((float)slabMain, (float)(winH - bodyCross - bodyCrossLen), (float)slabMainLen, (float)bodyCrossLen),
        };

        var ctx = _host.Context;
        ctx.BeginDraw();
        ctx.Clear(new Color4(0, 0, 0, 0));
        GlassSlab.DrawGlass(ctx, sx, sy, sw, sh, (float)trayRadius, (float)opacity, (float)frost);

        var running = RunningAppTracker.SnapshotRunning();
        for (int i = 0; i < pinnedVisible && i < apps.Count; i++)
        {
            var entry = apps[i];
            double mainC = pinnedAreaMain + i * cellH + cellH / 2.0;
            (float cx, float cy) = ToLocal(side, mainC, colCenterCross, winW, winH);
            DrawIcon(ctx, entry, cx, cy, (float)gIcon, side,
                RunningAppTracker.IsEntryRunning(entry, running, new List<string>(), new HashSet<string>()));
        }

        ctx.EndDraw();
        _host.Present();
    }

    private static (float x, float y) ToLocal(DockSide side, double main, double cross, int winW, int winH) => side switch
    {
        DockSide.Left => ((float)cross, (float)main),
        DockSide.Right => ((float)(winW - cross), (float)main),
        DockSide.Top => ((float)main, (float)cross),
        _ => ((float)main, (float)(winH - cross)),
    };

    private void DrawIcon(ID2D1DeviceContext ctx, AppEntry entry, float cx, float cy, float g, DockSide side, bool isRunning)
    {
        float half = g / 2f;
        // Faint icon plate (#08FFFFFF, rounded 12 — matches RadialIcon IconPlate).
        var plate = new RoundedRectangle { Rect = new Rect(cx - half, cy - half, g, g), RadiusX = 12f, RadiusY = 12f };
        using (var pb = ctx.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 0x08 / 255f)))
            ctx.FillRoundedRectangle(plate, pb);

        // Icon bitmap (padding ~8 like the WPF IconPlate).
        var bmp = GetBitmap(ctx, entry);
        if (bmp != null)
        {
            float pad = g * 0.14f;
            float dstX = cx - half + pad, dstY = cy - half + pad, dstSz = g - pad * 2;
            var bs = bmp.Size;
            float sx = dstSz / Math.Max(1f, bs.Width), sy = dstSz / Math.Max(1f, bs.Height);
            ctx.Transform = Matrix3x2.CreateScale(sx, sy) * Matrix3x2.CreateTranslation(dstX, dstY);
            ctx.DrawBitmap(bmp, 1f, InterpolationMode.HighQualityCubic);
            ctx.Transform = Matrix3x2.Identity;
        }

        // Green running dot hugging the screen-edge side of the tile.
        if (isRunning)
        {
            float dot = Math.Max(2.6f, g * 0.07f);
            float glow = dot * 2.3f;
            (float dx, float dy) = side switch
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
                d2d = ((CompositionHost)_host!).CreateBitmap(w, h, px, stride);
            }
        }
        catch { d2d = null; }
        _bmpCache[key] = d2d;
        return d2d;
    }

    public void Dispose()
    {
        foreach (var b in _bmpCache.Values) b?.Dispose();
        _bmpCache.Clear();
        _host?.Dispose();
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
    }

    // ---- Raw Win32 NOREDIRECTIONBITMAP window --------------------------------

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
}
