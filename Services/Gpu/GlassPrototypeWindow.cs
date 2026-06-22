using System;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using FontStyle = Vortice.DirectWrite.FontStyle;

namespace Polaris.Services.Gpu;

/// <summary>GPU-rendering spike — visual-fidelity prototype (POLARIS_GLASS_PROTO=1).
/// Reproduces the liquid-glass slab (drop shadow, translucent body, frost veil,
/// edge vignette, centre bloom, luminous rim) and a clock label entirely in
/// <b>Direct2D + DirectWrite</b> under DirectComposition, so the GPU material can be
/// eyeballed against the real WPF glass dock before committing to a full main-dock
/// port. Drawn with native gradient brushes (soft without a blur pass), a real D2D
/// <c>Shadow</c> effect for the drop shadow, and DirectWrite for crisp text.</summary>
internal sealed class GlassPrototypeWindow
{
    private readonly IntPtr _hwnd;
    private readonly CompositionHost _host;
    private readonly IDWriteFactory _dwrite;
    private readonly IDWriteTextFormat _timeFormat;
    private readonly IDWriteTextFormat _dateFormat;
    private readonly DispatcherTimer _timer;

    private readonly int _w, _h;
    private readonly double _dpi;
    private readonly float _margin = 70f;   // room for the drop shadow

    public static void Show()
    {
        try { _ = new GlassPrototypeWindow(); }
        catch (Exception ex) { Services.Log.Warn("GlassProto", "prototype failed: " + ex.Message); }
    }

    private GlassPrototypeWindow()
    {
        _w = 1280;
        _h = 620;
        _hwnd = CreateWindow(_w, _h);
        _dpi = CompositionHost.DpiScale(_hwnd);
        // Place it bottom-flush, same TALL+WIDE proportions as the real glass dock
        // (its slab is ~700px tall), so the radial gradients fall off as gently as
        // the real one — a thin strip makes them look far more uneven than they are.
        var wa = System.Windows.SystemParameters.WorkArea;
        // Layout is in DIPs; the window + swap chain are physical-pixel. Size the
        // window physically (DIP × dpi) and tell the host the scaled DPI so the
        // DIP-space drawing maps 1:1 (correct on >100% displays).
        int pw = (int)Math.Ceiling(_w * _dpi), ph = (int)Math.Ceiling(_h * _dpi);
        SetWindowPos(_hwnd, IntPtr.Zero,
            (int)Math.Round((wa.Left + (wa.Width - _w) / 2) * _dpi),
            (int)Math.Round((wa.Bottom - _h + 50) * _dpi), pw, ph, 0x0004 | 0x0010);
        ShowWindow(_hwnd, 4);

        _host = new CompositionHost(_hwnd, pw, ph, (float)(96.0 * _dpi));

        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
        _timeFormat = _dwrite.CreateTextFormat("Segoe UI", null, FontWeight.SemiLight,
            FontStyle.Normal, FontStretch.Normal, 56f, "en-us");
        _timeFormat.TextAlignment = TextAlignment.Center;
        _timeFormat.ParagraphAlignment = ParagraphAlignment.Center;
        _dateFormat = _dwrite.CreateTextFormat("Microsoft YaHei UI", null, FontWeight.Normal,
            FontStyle.Normal, FontStretch.Normal, 20f, "zh-cn");
        _dateFormat.TextAlignment = TextAlignment.Center;
        _dateFormat.ParagraphAlignment = ParagraphAlignment.Center;

        Render();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Render();
        _timer.Start();
    }

    private static Color4 C(byte a, byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, a / 255f);

    // The whole glass slab is drawn at the panel opacity (1 - PanelTransparency).
    // For the prototype the user's setting is ~0.40, so every slab layer's alpha is
    // scaled by this (the clock text is separate and stays full).
    private const float Op = 0.40f;
    private static Color4 Cs(byte a, byte r, byte g, byte b) =>
        new(r / 255f, g / 255f, b / 255f, a / 255f * Op);

    private void Render()
    {
        try { RenderCore(); }
        catch (Exception ex)
        {
            _timer?.Stop();
            Services.Log.Warn("GlassProto", "render failed: " + ex);
        }
    }

    private void RenderCore()
    {
        var ctx = _host.Context;
        float x = _margin, y = _margin;
        float sw = _w - _margin * 2, sh = _h - _margin * 2;
        float radius = 28f;
        var slab = new RoundedRectangle { Rect = new Rect(x, y, sw, sh), RadiusX = radius, RadiusY = radius };
        var cx = x + sw / 2f;
        var cy = y + sh / 2f;

        // --- Soft drop shadow: a faint, downward-biased halo (the real
        //     DropShadowEffect is offset down, not a ring around all four sides).
        ctx.BeginDraw();
        ctx.Clear(C(0, 0, 0, 0));
        for (int i = 5; i >= 1; i--)
        {
            float grow = i * 2.4f;
            var sRect = new RoundedRectangle
            {
                Rect = new Rect(x - grow * 0.5f, y + 14, sw + grow, sh + grow),
                RadiusX = radius + grow,
                RadiusY = radius + grow,
            };
            using var sb = ctx.CreateSolidColorBrush(C(0x06, 0x06, 0x0B, 0x16));
            ctx.FillRoundedRectangle(sRect, sb);
        }

        // --- Body: clear-glass radial (centre-bright cool -> clearer rim) — faithful
        //     to GlassChrome so the wallpaper shows through like the real dock.
        using (var body = ctx.CreateRadialGradientBrush(
            Radial(cx, cy, sw * 0.72f, sh * 0.72f),
            Stops(ctx, (0f, Cs(0x2E, 0xDA, 0xEC, 0xFF)), (0.48f, Cs(0x1A, 0xEA, 0xF2, 0xFF)),
                       (0.8f, Cs(0x12, 0xCE, 0xDE, 0xF2)), (1f, Cs(0x0A, 0xAE, 0xC2, 0xDC)))))
            ctx.FillRoundedRectangle(slab, body);

        // --- Frost veil: milky diffusion centred upper-third, peak = fs*0xC8
        //     (fs = 1 - PanelTransparency ~ 0.40). Faint/concentrated, NOT a flat
        //     milky fill — the real dock stays mostly clear.
        using (var frost = ctx.CreateRadialGradientBrush(
            Radial(cx, y + sh * 0.34f, sw * 0.95f, sh * 1.05f),
            Stops(ctx, (0f, Cs(0x50, 0xFF, 0xFF, 0xFF)), (0.55f, Cs(0x4A, 0xF2, 0xF6, 0xFF)),
                       (1f, Cs(0x45, 0xE4, 0xEC, 0xF8)))))
            ctx.FillRoundedRectangle(slab, frost);

        // --- Edge vignette: soft dark rim (faithful).
        using (var edge = ctx.CreateRadialGradientBrush(
            Radial(cx, cy, sw * 0.72f, sh * 0.72f),
            Stops(ctx, (0f, Cs(0x00, 0x0A, 0x12, 0x20)), (0.6f, Cs(0x00, 0x0A, 0x12, 0x20)),
                       (1f, Cs(0x20, 0x0A, 0x12, 0x20)))))
            ctx.FillRoundedRectangle(slab, edge);

        // --- Centre specular bloom.
        using (var bloom = ctx.CreateRadialGradientBrush(
            Radial(cx, cy, sw * 0.5f, sh * 0.62f),
            Stops(ctx, (0f, Cs(0x32, 0xFF, 0xFF, 0xFF)), (0.5f, Cs(0x12, 0xFF, 0xFF, 0xFF)),
                       (1f, Cs(0x00, 0xFF, 0xFF, 0xFF)))))
            ctx.FillRoundedRectangle(
                new RoundedRectangle { Rect = new Rect(x + sw * 0.07f, cy - sh * 0.275f, sw * 0.86f, sh * 0.55f), RadiusX = sw * 0.43f, RadiusY = sw * 0.43f },
                bloom);

        // --- Luminous rim hairline (brightest top-left & bottom-right).
        using (var rim = ctx.CreateLinearGradientBrush(
            new LinearGradientBrushProperties { StartPoint = new Vector2(x, y), EndPoint = new Vector2(x + sw, y + sh) },
            Stops(ctx, (0f, Cs(0xF2, 0xFF, 0xFF, 0xFF)), (0.4f, Cs(0x59, 0xFF, 0xFF, 0xFF)),
                       (0.62f, Cs(0x30, 0xC8, 0xDA, 0xF5)), (1f, Cs(0x9C, 0xFF, 0xFF, 0xFF)))))
            ctx.DrawRoundedRectangle(slab, rim, 1.1f);

        // --- Clock text (DirectWrite), with a soft dark shadow for legibility.
        string time = DateTime.Now.ToString("H:mm", CultureInfo.InvariantCulture);
        string date = DateTime.Now.ToString("M月d日 ddd", CultureInfo.GetCultureInfo("zh-CN"));
        var timeRect = new Rect(x, y + sh * 0.10f, sw, sh * 0.55f);
        var dateRect = new Rect(x, y + sh * 0.58f, sw, sh * 0.30f);
        using (var sh1 = ctx.CreateSolidColorBrush(C(0x80, 0x00, 0x10, 0x22)))
        {
            ctx.DrawText(time, _timeFormat, new Rect(timeRect.X + 1, timeRect.Y + 1, timeRect.Width, timeRect.Height), sh1);
            ctx.DrawText(date, _dateFormat, new Rect(dateRect.X + 1, dateRect.Y + 1, dateRect.Width, dateRect.Height), sh1);
        }
        using (var ink = ctx.CreateSolidColorBrush(C(0xF4, 0xEC, 0xF4, 0xFF)))
        {
            ctx.DrawText(time, _timeFormat, timeRect, ink);
            ctx.DrawText(date, _dateFormat, dateRect, ink);
        }

        ctx.EndDraw();
        _host.Present();
    }

    private static RadialGradientBrushProperties Radial(float cx, float cy, float rx, float ry) =>
        new() { Center = new Vector2(cx, cy), GradientOriginOffset = new Vector2(0, 0), RadiusX = rx, RadiusY = ry };

    private static ID2D1GradientStopCollection Stops(ID2D1DeviceContext ctx, params (float pos, Color4 col)[] s)
    {
        var arr = new GradientStop[s.Length];
        for (int i = 0; i < s.Length; i++) arr[i] = new GradientStop { Position = s[i].pos, Color = s[i].col };
        return ctx.CreateGradientStopCollection(arr);
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
                lpszClassName = "PolarisGlassProto",
            };
            s_atom = RegisterClassExW(ref wc);
        }
        return CreateWindowExW(
            WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "PolarisGlassProto", string.Empty, WS_POPUP,
            0, 0, w, h, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
    }

    private delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_POPUP = 0x80000000;

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
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? n);
}
