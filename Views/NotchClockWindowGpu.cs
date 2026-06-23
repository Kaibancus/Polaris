using System;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Polaris.Services;
using Polaris.Services.Gpu;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using FontStyle = Vortice.DirectWrite.FontStyle;

namespace Polaris.Views;

/// <summary>GPU-rendered Saturn notch clock: the trapezoid "notch" plate, its soft
/// dark halo and the 3-D pale-gold lettering drawn in Direct2D + DirectWrite under
/// DirectComposition.</summary>
internal sealed class NotchClockWindowGpu : INotchClock
{
    private const float PlateWidth = 240, PlateHeight = 30, Slant = 20, SidePad = 16, FreePad = 14;
    private static readonly int WinW = (int)(PlateWidth + SidePad * 2);
    private static readonly int WinH = (int)(PlateHeight + FreePad);

    private IntPtr _hwnd;
    private CompositionHost? _host;
    private double _dpi = 1.0;
    private IDWriteFactory? _dwrite;
    private IDWriteTextFormat? _format;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _releaseTimer;
    private bool _atBottom;
    private bool _built;
    private bool _visible;

    public NotchClockWindowGpu()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Render();
        // Release the GPU device after the notch stays hidden past the delay (the Saturn
        // theme is frequently inactive). A re-summon within the delay cancels it, so quick
        // hide/show cycles don't churn the device; a sustained hide frees it for the trim.
        _releaseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _releaseTimer.Tick += (_, _) => { _releaseTimer.Stop(); if (!_visible) ReleaseGpu(); };
    }

    public void ShowNotch(bool atBottom)
    {
        try
        {
            _releaseTimer.Stop();   // cancel a pending device release — we're showing again
            EnsureBuilt();
            _atBottom = atBottom;

            var mon = MonitorLayout.ActiveBounds;
            int pw = (int)Math.Ceiling(WinW * _dpi), ph = (int)Math.Ceiling(WinH * _dpi);
            int x = (int)Math.Round((mon.Left + (mon.Width - WinW) / 2.0) * _dpi);
            int y = (int)Math.Round((atBottom ? mon.Bottom - WinH : mon.Top) * _dpi);
            SetWindowPos(_hwnd, HWND_TOPMOST, x, y, pw, ph, SWP_NOACTIVATE);
            if (!_visible)
            {
                ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
                _visible = true;
            }
            Render();
            _timer.Start();
        }
        catch (Exception ex) { Log.Warn("NotchGpu", "show failed: " + ex.Message); }
    }

    public void HideNotch()
    {
        _timer.Stop();
        if (_visible)
        {
            ShowWindow(_hwnd, SW_HIDE);
            _visible = false;
        }
        // Free the GPU device (a full D3D11 device + its driver worker threads) once the
        // notch has stayed hidden past the delay, so a glass session / dismissed dock does
        // not keep a Saturn-only device committed. Recreated lazily on the next ShowNotch.
        _releaseTimer.Stop();
        _releaseTimer.Start();
    }

    /// <summary>Disposes the GPU device + text resources + window while hidden. The next
    /// ShowNotch rebuilds them lazily via EnsureBuilt. Runs on the UI thread (the notch
    /// renders from a DispatcherTimer, so it has no render thread to coordinate with).</summary>
    private void ReleaseGpu()
    {
        _format?.Dispose(); _format = null;
        _dwrite?.Dispose(); _dwrite = null;
        _host?.Dispose(); _host = null;
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        _built = false;
    }

    private void EnsureBuilt()
    {
        if (_built)
            return;
        _hwnd = CreateWindow(WinW, WinH);
        _dpi = CompositionHost.DpiScale(_hwnd);
        _host = new CompositionHost(_hwnd, (int)Math.Ceiling(WinW * _dpi),
            (int)Math.Ceiling(WinH * _dpi), (float)(96.0 * _dpi));
        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
        _format = _dwrite.CreateTextFormat("华文新魏", null, FontWeight.SemiBold,
            FontStyle.Normal, FontStretch.Normal, 20f, "zh-cn");
        _format.TextAlignment = TextAlignment.Center;
        _format.ParagraphAlignment = ParagraphAlignment.Center;
        _built = true;
    }

    private static Color4 C(byte a, byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, a / 255f);

    private void Render()
    {
        if (_host == null || _format == null)
            return;
        var ctx = _host.Context;

        // Plate origin inside the window: the wide edge hugs the screen edge, so the
        // panel sits at the window top for a top notch, FreePad down for a bottom one.
        float ox = SidePad;
        float oy = _atBottom ? FreePad : 0f;

        using var plate = BuildPlate(ctx, ox, oy, _atBottom);

        // Soft dark halo: render the black trapezoid into a command list and blur it
        // with a real D2D GaussianBlur (the WPF version is a 14px blur), so the
        // penumbra around the slant edges is smooth (no banding).
        var glowSrc = ctx.CreateCommandList();
        ctx.Target = glowSrc;
        ctx.BeginDraw();
        using (var black = ctx.CreateSolidColorBrush(C(0xD7, 0x00, 0x00, 0x00)))
            ctx.FillGeometry(plate, black);
        ctx.EndDraw();
        glowSrc.Close();
        _host.SetDefaultTarget();

        using var blur = new Vortice.Direct2D1.Effects.GaussianBlur(ctx);
        blur.SetInput(0, glowSrc, true);
        blur.StandardDeviation = 14f / 3f;

        // Warm tan glow behind the gold text (parity with WPF DropShadowEffect:
        // colour CBBC95, blur 7, depth 0, opacity 0.45) so the clock reads as part
        // of the Saturn ring palette rather than flat gold. Built here (outside the
        // main BeginDraw) as its own command list, then composited under the gold.
        string txt = DateTime.Now.ToString("M月d日 ddd  H:mm", CultureInfo.GetCultureInfo("zh-CN"));
        var rect = new Rect(ox, oy, PlateWidth, PlateHeight);
        var textGlowSrc = ctx.CreateCommandList();
        ctx.Target = textGlowSrc;
        ctx.BeginDraw();
        using (var tan = ctx.CreateSolidColorBrush(C(0x73, 0xCB, 0xBC, 0x95)))   // 0x73 ≈ 0.45 opacity
            ctx.DrawText(txt, _format, rect, tan);
        ctx.EndDraw();
        textGlowSrc.Close();
        _host.SetDefaultTarget();

        using var textGlow = new Vortice.Direct2D1.Effects.GaussianBlur(ctx);
        textGlow.SetInput(0, textGlowSrc, true);
        textGlow.StandardDeviation = 7f / 3f;

        ctx.BeginDraw();
        ctx.Clear(C(0, 0, 0, 0));
        ctx.DrawImage(blur, new Vector2(0, 0), InterpolationMode.Linear, CompositeMode.SourceOver);
        glowSrc.Dispose();

        // Crisp black plate.
        using (var plateBrush = ctx.CreateSolidColorBrush(C(0xEE, 0x07, 0x08, 0x0B)))
            ctx.FillGeometry(plate, plateBrush);

        // 3-D raised lettering: dark offset copy, warm tan glow, then pale-gold copy.
        using (var dark = ctx.CreateSolidColorBrush(C(0xCD, 0x00, 0x00, 0x00)))
            ctx.DrawText(txt, _format, new Rect(rect.X + 1.3f, rect.Y + 1.6f, rect.Width, rect.Height), dark);
        ctx.DrawImage(textGlow, new Vector2(0, 0), InterpolationMode.Linear, CompositeMode.SourceOver);
        textGlowSrc.Dispose();
        using (var gold = ctx.CreateSolidColorBrush(C(0xFF, 0xEC, 0xDF, 0xBE)))
            ctx.DrawText(txt, _format, rect, gold);

        ctx.EndDraw();
        _host.Present();
    }

    private static ID2D1PathGeometry BuildPlate(ID2D1DeviceContext ctx, float ox, float oy, bool atBottom)
    {
        float w = PlateWidth, h = PlateHeight, s = Slant;
        var geo = ctx.Factory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            if (!atBottom)
            {
                sink.BeginFigure(new Vector2(ox + 0, oy + 0), FigureBegin.Filled);
                sink.AddLine(new Vector2(ox + w, oy + 0));
                sink.AddLine(new Vector2(ox + w - s, oy + h));
                sink.AddLine(new Vector2(ox + s, oy + h));
            }
            else
            {
                sink.BeginFigure(new Vector2(ox + s, oy + 0), FigureBegin.Filled);
                sink.AddLine(new Vector2(ox + w - s, oy + 0));
                sink.AddLine(new Vector2(ox + w, oy + h));
                sink.AddLine(new Vector2(ox + 0, oy + h));
            }
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        return geo;
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
                lpszClassName = "PolarisNotchGpu",
            };
            s_atom = RegisterClassExW(ref wc);
        }
        return CreateWindowExW(
            WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST | WS_EX_TRANSPARENT |
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "PolarisNotchGpu", string.Empty, WS_POPUP,
            0, 0, w, h, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
    }

    private delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_POPUP = 0x80000000;
    private const int SW_SHOWNOACTIVATE = 4, SW_HIDE = 0;
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
