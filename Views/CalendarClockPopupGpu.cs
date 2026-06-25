using System;
using System.Globalization;
using System.Numerics;
using System.Windows.Threading;
using Polaris.Interop;
using Polaris.Models;
using Polaris.Services;
using Polaris.Services.Gpu;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using FontStyle = Vortice.DirectWrite.FontStyle;

namespace Polaris.Views;

/// <summary>
/// A hover popup shown above the side dock's Polaris tile (liquid-glass theme): a frosted glass
/// card holding a skeuomorphic tear-off desk-calendar page (today's day / weekday / month-year)
/// and an analog clock whose hands sweep live. Rendered in Direct2D under DirectComposition in
/// its own NOREDIRECTIONBITMAP click-through window (modeled on <see cref="NotchClockWindowGpu"/>),
/// driven by a ~30fps <see cref="DispatcherTimer"/> while visible so the second hand sweeps
/// smoothly. The GPU device is released after the popup stays hidden a while (re-built lazily).
/// </summary>
internal sealed class CalendarClockPopupGpu : IDisposable
{
    // Card + window geometry (DIP). The window is padded around the card so the soft drop
    // shadow has room to bleed.
    private const float Pad = 16f;
    private const float CardW = 268f, CardH = 150f, CornerR = 15f, Gap = 3f;
    private static readonly int WinW = (int)(CardW + Pad * 2);
    private static readonly int WinH = (int)(CardH + Pad * 2);

    private IntPtr _hwnd;
    private CompositionHost? _host;
    private double _dpi = 1.0;
    private IDWriteFactory? _dwrite;
    private IDWriteTextFormat? _hdrFmt;   // calendar header (year/month)
    private IDWriteTextFormat? _dayFmt;   // big day number
    private IDWriteTextFormat? _wdFmt;    // weekday
    private IDWriteTextFormat? _numFmt;   // clock numerals
    private ID2D1StrokeStyle? _round;     // round-cap stroke for clock hands
    private float _glassOpacity = 1f, _frost = 0.5f;   // theme glass look (from the dock)
    private bool _saturn;                  // Saturn theme → feathered near-black card
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _releaseTimer;
    private bool _built;
    private bool _visible;

    public CalendarClockPopupGpu()
    {
        // ~30fps so the second hand sweeps smoothly (the notch clock's 1s tick would jump).
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Render();
        // Free the GPU device once hidden past the delay; a re-show within it cancels the release.
        _releaseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _releaseTimer.Tick += (_, _) => { _releaseTimer.Stop(); if (!_visible) ReleaseGpu(); };
    }

    /// <summary>Shows the popup anchored just outside the hovered Polaris tile, opening toward
    /// the screen interior (the side the tile pops toward), clamped to the dock's monitor.</summary>
    /// <param name="tileCenterXDip">Tile centre X in screen DIPs (already including hover-pop).</param>
    /// <param name="tileCenterYDip">Tile centre Y in screen DIPs.</param>
    /// <param name="tileSizeDip">Magnified tile edge length (DIP).</param>
    /// <param name="side">The edge the dock is anchored to.</param>
    /// <param name="glassOpacity">The dock's glass opacity (1 - PanelTransparency).</param>
    /// <param name="frostStrength">The dock's frost veil strength.</param>
    /// <param name="saturn">True for the Saturn theme (feathered near-black card instead of glass).</param>
    public void ShowFor(double tileCenterXDip, double tileCenterYDip, double tileSizeDip, DockSide side,
        float glassOpacity, float frostStrength, bool saturn)
    {
        try
        {
            _glassOpacity = glassOpacity;
            _frost = frostStrength;
            _saturn = saturn;
            _releaseTimer.Stop();
            EnsureBuilt();

            double half = tileSizeDip / 2.0;
            // Card top-left in screen DIPs, opening toward the dock interior.
            double cardLeft, cardTop;
            switch (side)
            {
                case DockSide.Left:   // dock on left → open to the right of the tile
                    cardLeft = tileCenterXDip + half + Gap;
                    cardTop = tileCenterYDip - CardH / 2.0;
                    break;
                case DockSide.Right:  // open to the left
                    cardLeft = tileCenterXDip - half - Gap - CardW;
                    cardTop = tileCenterYDip - CardH / 2.0;
                    break;
                case DockSide.Top:    // open below
                    cardLeft = tileCenterXDip - CardW / 2.0;
                    cardTop = tileCenterYDip + half + Gap;
                    break;
                default:              // Bottom → open above the tile
                    cardLeft = tileCenterXDip - CardW / 2.0;
                    cardTop = tileCenterYDip - half - Gap - CardH;
                    break;
            }

            // Window top-left = card top-left minus the shadow padding, clamped to the monitor.
            double winLeft = cardLeft - Pad;
            double winTop = cardTop - Pad;
            var mon = MonitorLayout.ActiveBounds;
            winLeft = Math.Clamp(winLeft, mon.Left + 2, Math.Max(mon.Left + 2, mon.Right - WinW - 2));
            winTop = Math.Clamp(winTop, mon.Top + 2, Math.Max(mon.Top + 2, mon.Bottom - WinH - 2));

            int pw = (int)Math.Ceiling(WinW * _dpi), ph = (int)Math.Ceiling(WinH * _dpi);
            int x = (int)Math.Round(winLeft * _dpi), y = (int)Math.Round(winTop * _dpi);
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, pw, ph, Win32.SWP_NOACTIVATE);
            if (!_visible)
            {
                Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
                _visible = true;
            }
            Render();
            _timer.Start();
        }
        catch (Exception ex) { Log.Warn("CalClockGpu", "show failed: " + ex.Message); }
    }

    public void Hide()
    {
        _timer.Stop();
        if (_visible)
        {
            Win32.ShowWindow(_hwnd, Win32.SW_HIDE);
            _visible = false;
        }
        _releaseTimer.Stop();
        _releaseTimer.Start();
    }

    public bool IsVisible => _visible;

    public void Dispose()
    {
        _timer.Stop();
        _releaseTimer.Stop();
        ReleaseGpu();
    }

    /// <summary>Disposes the GPU device + text resources + window while hidden; the next
    /// ShowFor rebuilds them lazily. Runs on the UI thread (timer-driven, no render thread).</summary>
    private void ReleaseGpu()
    {
        _round?.Dispose(); _round = null;
        _hdrFmt?.Dispose(); _hdrFmt = null;
        _dayFmt?.Dispose(); _dayFmt = null;
        _wdFmt?.Dispose(); _wdFmt = null;
        _numFmt?.Dispose(); _numFmt = null;
        _dwrite?.Dispose(); _dwrite = null;
        _host?.Dispose(); _host = null;
        if (_hwnd != IntPtr.Zero) { Win32.DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
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

        _hdrFmt = MakeFormat("Microsoft YaHei UI", FontWeight.SemiBold, 12.5f);
        _dayFmt = MakeFormat("Segoe UI", FontWeight.Bold, 54f);
        _wdFmt = MakeFormat("Microsoft YaHei UI", FontWeight.Normal, 13f);
        _numFmt = MakeFormat("Segoe UI", FontWeight.SemiBold, 9.5f);

        _round = _host.Context.Factory.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round,
            LineJoin = LineJoin.Round,
        });
        _built = true;
    }

    private IDWriteTextFormat MakeFormat(string family, FontWeight weight, float size)
    {
        var f = _dwrite!.CreateTextFormat(family, null, weight, FontStyle.Normal, FontStretch.Normal, size, "zh-cn");
        f.TextAlignment = TextAlignment.Center;
        f.ParagraphAlignment = ParagraphAlignment.Center;
        return f;
    }

    private static Color4 C(byte a, byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, a / 255f);

    private void Render()
    {
        if (_host == null)
            return;
        var ctx = _host.Context;

        float cardX = Pad, cardY = Pad;
        var cardRect = new Rect(cardX, cardY, CardW, CardH);
        var card = new RoundedRectangle { Rect = cardRect, RadiusX = CornerR, RadiusY = CornerR };

        // Build the backdrop on an alternate target BEFORE the main pass (command lists must be
        // recorded outside BeginDraw). Glass: a soft drop shadow. Saturn: a feathered near-black
        // slab matching the side dock's dark group (one blurred opaque-black mass → soft edges).
        var backSrc = ctx.CreateCommandList();
        ctx.Target = backSrc;
        ctx.BeginDraw();
        if (_saturn)
        {
            // Panel transparency baked into the source alpha once (single shape → no overlap /
            // double-darkening). Clamp so the card never gets too faint to read.
            byte a = (byte)Math.Clamp(_glassOpacity * 255f, 150f, 255f);
            using var black = ctx.CreateSolidColorBrush(C(a, 0x06, 0x08, 0x0C));
            ctx.FillRoundedRectangle(card, black);
        }
        else
        {
            using var sh = ctx.CreateSolidColorBrush(C(0x55, 0x10, 0x12, 0x18));
            ctx.FillRoundedRectangle(card, sh);
        }
        ctx.EndDraw();
        backSrc.Close();
        _host.SetDefaultTarget();
        using var backBlur = new Vortice.Direct2D1.Effects.GaussianBlur(ctx);
        backBlur.SetInput(0, backSrc, true);
        backBlur.StandardDeviation = _saturn ? 5f : 9f;   // Saturn: feathered rim; glass: shadow spread

        ctx.BeginDraw();
        ctx.Clear(C(0, 0, 0, 0));
        // Saturn draws the feathered black centred; glass offsets the shadow down a touch.
        ctx.DrawImage(backBlur, new Vector2(0, _saturn ? 0f : 4f), InterpolationMode.Linear, CompositeMode.SourceOver);
        backSrc.Dispose();

        // Card body. Glass → the same liquid-glass slab the dock renders. Saturn → the feathered
        // black above already IS the body (matches the side dock's dark slab), so skip the glass.
        if (!_saturn)
            GlassSlab.DrawGlass(ctx, cardX, cardY, CardW, CardH, CornerR, _glassOpacity, _frost, shadowExtent: 0f);
        DrawCalendar(ctx, cardX, cardY);
        DrawClock(ctx, cardX, cardY);

        ctx.EndDraw();
        _host.Present();
    }

    /// <summary>Skeuomorphic tear-off desk-calendar page on the card's left half: a red header
    /// strip with the year/month, the big day number, the weekday, and two metal binding rings.</summary>
    private void DrawCalendar(ID2D1DeviceContext ctx, float cardX, float cardY)
    {
        float px = cardX + 13f, py = cardY + 18f, pw = 104f, pht = CardH - 36f;
        float hdrH = 33f;
        var ci = CultureInfo.GetCultureInfo("zh-CN");
        var now = DateTime.Now;

        // Page shadow + white page.
        using (var psh = ctx.CreateSolidColorBrush(C(0x33, 0x20, 0x20, 0x28)))
            ctx.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(px + 1.2f, py + 2f, pw, pht), RadiusX = 9f, RadiusY = 9f }, psh);
        using (var page = ctx.CreateSolidColorBrush(C(0xFF, 0xFF, 0xFF, 0xFF)))
            ctx.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(px, py, pw, pht), RadiusX = 9f, RadiusY = 9f }, page);

        // Red header strip (rounded top, square bottom): a rounded rect for the top, a plain
        // rect for the lower half so its bottom edge is straight against the page body.
        using (var hstops = ctx.CreateGradientStopCollection(new[]
        {
            new GradientStop { Position = 0f, Color = C(0xFF, 0xF0, 0x55, 0x55) },
            new GradientStop { Position = 1f, Color = C(0xFF, 0xD7, 0x37, 0x37) },
        }))
        using (var red = ctx.CreateLinearGradientBrush(
            new LinearGradientBrushProperties { StartPoint = new Vector2(px, py), EndPoint = new Vector2(px, py + hdrH) }, hstops))
        {
            ctx.FillRoundedRectangle(new RoundedRectangle { Rect = new Rect(px, py, pw, hdrH), RadiusX = 9f, RadiusY = 9f }, red);
            ctx.FillRectangle(new Rect(px, py + hdrH - 10f, pw, 10f), red);
        }
        using (var ink = ctx.CreateSolidColorBrush(C(0xFF, 0xFF, 0xFF, 0xFF)))
            ctx.DrawText(now.ToString("yyyy 年 M 月", ci), _hdrFmt!, new Rect(px, py + 1f, pw, hdrH), ink);

        // Big day number.
        using (var dayInk = ctx.CreateSolidColorBrush(C(0xFF, 0x2C, 0x2E, 0x36)))
            ctx.DrawText(now.Day.ToString(CultureInfo.InvariantCulture), _dayFmt!,
                new Rect(px, py + hdrH - 5f, pw, pht - hdrH - 18f), dayInk);

        // Weekday near the bottom.
        using (var wdInk = ctx.CreateSolidColorBrush(C(0xFF, 0xCF, 0x3A, 0x3A)))
            ctx.DrawText(now.ToString("dddd", ci), _wdFmt!, new Rect(px, py + pht - 24f, pw, 22f), wdInk);

        // Two metal binding rings straddling the page top.
        for (int k = 0; k < 2; k++)
        {
            float bx = px + (k == 0 ? 26f : pw - 26f);
            var c = new Vector2(bx, py - 1f);
            using (var ring = ctx.CreateSolidColorBrush(C(0xFF, 0x9A, 0x9D, 0xA6)))
                ctx.DrawEllipse(new Ellipse(c, 4.4f, 4.4f), ring, 2f);
            using (var hole = ctx.CreateSolidColorBrush(C(0xFF, 0x6B, 0x18, 0x18)))
                ctx.FillEllipse(new Ellipse(c, 1.8f, 1.8f), hole);
        }
    }

    /// <summary>Analog clock on the card's right half: a cream face with a bezel, hour/minute
    /// ticks, all twelve numerals, and hour/minute/second hands. The second hand sweeps
    /// continuously (fractional seconds), so it glides rather than ticks.</summary>
    private void DrawClock(ID2D1DeviceContext ctx, float cardX, float cardY)
    {
        float cx = cardX + CardW - 76f, cy = cardY + CardH / 2f, R = 56f;
        var c = new Vector2(cx, cy);

        // Bezel (light → gray) then cream face.
        using (var bstops = ctx.CreateGradientStopCollection(new[]
        {
            new GradientStop { Position = 0f, Color = C(0xFF, 0xFF, 0xFF, 0xFF) },
            new GradientStop { Position = 1f, Color = C(0xFF, 0xBE, 0xC2, 0xCC) },
        }))
        using (var bez = ctx.CreateLinearGradientBrush(
            new LinearGradientBrushProperties { StartPoint = new Vector2(cx, cy - R - 3f), EndPoint = new Vector2(cx, cy + R + 3f) }, bstops))
            ctx.FillEllipse(new Ellipse(c, R + 3f, R + 3f), bez);
        using (var face = ctx.CreateSolidColorBrush(C(0xFF, 0xFD, 0xFC, 0xF7)))
            ctx.FillEllipse(new Ellipse(c, R, R), face);
        using (var rim = ctx.CreateSolidColorBrush(C(0x40, 0x20, 0x24, 0x30)))
            ctx.DrawEllipse(new Ellipse(c, R, R), rim, 1f);

        // Tick marks: 60 minute ticks, every 5th longer/darker (hour).
        for (int i = 0; i < 60; i++)
        {
            double a = i / 60.0 * Math.PI * 2 - Math.PI / 2;
            bool hour = i % 5 == 0;
            float r1 = R - 2.5f;
            float r2 = R - (hour ? 8f : 4.5f);
            var p1 = new Vector2(cx + (float)Math.Cos(a) * r1, cy + (float)Math.Sin(a) * r1);
            var p2 = new Vector2(cx + (float)Math.Cos(a) * r2, cy + (float)Math.Sin(a) * r2);
            using var tb = ctx.CreateSolidColorBrush(hour ? C(0xFF, 0x3A, 0x3D, 0x46) : C(0xCC, 0x9A, 0x9D, 0xA6));
            ctx.DrawLine(p1, p2, tb, hour ? 1.8f : 0.9f);
        }

        // All twelve numerals (1–12).
        using (var ni = ctx.CreateSolidColorBrush(C(0xFF, 0x33, 0x36, 0x40)))
            for (int n = 1; n <= 12; n++)
            {
                double a = n / 12.0 * Math.PI * 2 - Math.PI / 2;
                float nr = R - 15f;
                float nx = cx + (float)Math.Cos(a) * nr;
                float ny = cy + (float)Math.Sin(a) * nr;
                ctx.DrawText(n.ToString(CultureInfo.InvariantCulture), _numFmt!, new Rect(nx - 11f, ny - 10f, 22f, 20f), ni);
            }

        // Hands (continuous sweep via fractional seconds).
        var now = DateTime.Now;
        double s = now.Second + now.Millisecond / 1000.0;
        double m = now.Minute + s / 60.0;
        double h = (now.Hour % 12) + m / 60.0;
        double aH = h / 12.0 * Math.PI * 2 - Math.PI / 2;
        double aM = m / 60.0 * Math.PI * 2 - Math.PI / 2;
        double aS = s / 60.0 * Math.PI * 2 - Math.PI / 2;

        using (var dark = ctx.CreateSolidColorBrush(C(0xFF, 0x2A, 0x2D, 0x36)))
        {
            Hand(ctx, c, aH, R * 0.50f, R * 0.12f, 4.0f, dark);
            Hand(ctx, c, aM, R * 0.74f, R * 0.14f, 3.0f, dark);
        }
        using (var red = ctx.CreateSolidColorBrush(C(0xFF, 0xE5, 0x3A, 0x3A)))
        {
            Hand(ctx, c, aS, R * 0.82f, R * 0.22f, 1.4f, red);
            ctx.FillEllipse(new Ellipse(c, 3.6f, 3.6f), red);
        }
        using (var hub = ctx.CreateSolidColorBrush(C(0xFF, 0x2A, 0x2D, 0x36)))
            ctx.FillEllipse(new Ellipse(c, 1.9f, 1.9f), hub);
    }

    private void Hand(ID2D1DeviceContext ctx, Vector2 c, double ang, float len, float tail, float width, ID2D1Brush brush)
    {
        var tip = new Vector2(c.X + (float)Math.Cos(ang) * len, c.Y + (float)Math.Sin(ang) * len);
        var back = new Vector2(c.X - (float)Math.Cos(ang) * tail, c.Y - (float)Math.Sin(ang) * tail);
        ctx.DrawLine(back, tip, brush, width, _round);
    }

    // ---- Raw Win32 NOREDIRECTIONBITMAP window (shared plumbing in Interop/Win32) ----

    private static readonly Win32.WndProc s_wndProc = Win32.DefWindowProcW;
    private static ushort s_atom;

    private static IntPtr CreateWindow(int w, int h) => Win32.CreateWindow(
        "PolarisCalClockGpu",
        Win32.WS_EX_NOREDIRECTIONBITMAP | Win32.WS_EX_TOPMOST | Win32.WS_EX_TRANSPARENT |
        Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE,
        w, h, s_wndProc, ref s_atom);
}
