using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Polaris.Services;

namespace Polaris.Views;

/// <summary>A small phone-"notch"-style panel that hangs from the top (or, when
/// the side dock is anchored to the top edge, the bottom) centre of the active
/// monitor whenever the Saturn main dock is summoned. It shows the current date
/// and time in raised 3-D white lettering on a black trapezoid.
///
/// It is a separate borderless, click-through, top-most layered window so it can
/// sit flush with the very screen edge, independent of the content-sized,
/// screen-centred main dock window.</summary>
public sealed class NotchClockWindow : Window
{
    private readonly TextBlock _line;        // foreground white text
    private readonly TextBlock _lineShadow;  // dark offset copy behind it (3-D lift)
    private readonly Path _plate;
    private readonly Path _plateGlow;        // blurred black halo bleeding past the slanted edges
    private readonly Grid _panel;            // plate-sized host, offset on the root canvas
    private readonly DispatcherTimer _timer;
    private bool _atBottom;
    private bool _shaped;

    private const double PlateWidth = 250;
    private const double PlateHeight = 32;
    private const double Slant = 20;         // horizontal inset of the narrow free edge
    private const double SidePad = 16;       // room each side for the slant-edge blur halo
    private const double FreePad = 14;       // room past the narrow free edge for the halo

    public NotchClockWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        IsHitTestVisible = false;            // never steal pointer input
        Focusable = false;
        SizeToContent = SizeToContent.Manual;
        Width = PlateWidth + SidePad * 2;
        Height = PlateHeight + FreePad;

        // Soft blurred black layer sitting behind the plate; the blur bleeds out
        // past the slanted (and free) edges so the notch fades into a dark penumbra.
        _plateGlow = new Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(215, 0, 0, 0)),
            IsHitTestVisible = false,
            Effect = new BlurEffect { Radius = 14, RenderingBias = RenderingBias.Performance },
        };

        _plate = new Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(238, 7, 8, 11)),
            IsHitTestVisible = false,
        };

        // 3-D raised lettering: a dark copy offset down-right sits behind a bright
        // white copy carrying a faint cool glow, so the text reads as embossed.
        _lineShadow = MakeText(Color.FromArgb(205, 0, 0, 0));
        _lineShadow.Margin = new Thickness(1.3, 1.6, 0, 0);
        _line = MakeText(Colors.White);
        _line.Effect = new DropShadowEffect
        {
            Color = Color.FromRgb(150, 185, 255),
            BlurRadius = 7,
            ShadowDepth = 0,
            Opacity = 0.40,
        };

        var textHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        textHost.Children.Add(_lineShadow);
        textHost.Children.Add(_line);

        // Plate-sized host (its children — incl. the blurred glow — are NOT
        // clipped, so the halo spreads into the padded window around it).
        _panel = new Grid { Width = PlateWidth, Height = PlateHeight };
        _panel.Children.Add(_plateGlow);
        _panel.Children.Add(_plate);
        _panel.Children.Add(textHost);

        var root = new Canvas();
        root.Children.Add(_panel);
        Content = root;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateText();
    }

    private static readonly FontFamily NotchFont = new("华文新魏, 楷体, Microsoft YaHei");

    private static TextBlock MakeText(Color c) => new()
    {
        Foreground = new SolidColorBrush(c),
        FontFamily = NotchFont,
        FontSize = 20,
        FontWeight = FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        IsHitTestVisible = false,
    };

    /// <summary>Positions the notch centred on the active monitor's top edge, or
    /// its bottom edge when <paramref name="atBottom"/> is true, and shows it.</summary>
    public void ShowNotch(bool atBottom)
    {
        double winW = PlateWidth + SidePad * 2;
        double winH = PlateHeight + FreePad;
        if (!_shaped || _atBottom != atBottom)
        {
            _atBottom = atBottom;
            _shaped = true;
            var geo = BuildPlate(atBottom);
            _plate.Data = geo;
            _plateGlow.Data = geo;
            // Wide edge flush with the screen edge: for the top notch the plate
            // sits at the window top (halo room below); for the bottom notch it
            // sits FreePad down so the halo room is above it.
            Canvas.SetLeft(_panel, SidePad);
            Canvas.SetTop(_panel, atBottom ? FreePad : 0);
        }

        Rect mon = MonitorLayout.ActiveBounds;
        Left = mon.Left + (mon.Width - winW) / 2.0;
        Top = atBottom ? mon.Bottom - winH : mon.Top;

        UpdateText();
        if (!IsVisible)
            Show();
        Topmost = true;
        _timer.Start();
    }

    public void HideNotch()
    {
        _timer.Stop();
        if (IsVisible)
            Hide();
    }

    private void UpdateText()
    {
        var zh = CultureInfo.GetCultureInfo("zh-CN");
        string txt = DateTime.Now.ToString("M月d日 ddd  H:mm", zh);
        _line.Text = txt;
        _lineShadow.Text = txt;
    }

    /// <summary>Builds the trapezoid: the edge flush with the screen is the wide
    /// one, the free edge is inset by <see cref="Slant"/> on each side — mirrored
    /// vertically for the bottom placement.</summary>
    private static Geometry BuildPlate(bool atBottom)
    {
        double w = PlateWidth, h = PlateHeight, s = Slant;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            if (!atBottom)
            {
                // Wide edge along the top (y = 0); narrow free edge at the bottom.
                ctx.BeginFigure(new Point(0, 0), true, true);
                ctx.LineTo(new Point(w, 0), true, false);
                ctx.LineTo(new Point(w - s, h), true, false);
                ctx.LineTo(new Point(s, h), true, false);
            }
            else
            {
                // Wide edge along the bottom (y = h); narrow free edge at the top.
                ctx.BeginFigure(new Point(s, 0), true, true);
                ctx.LineTo(new Point(w - s, 0), true, false);
                ctx.LineTo(new Point(w, h), true, false);
                ctx.LineTo(new Point(0, h), true, false);
            }
        }
        g.Freeze();
        return g;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Make the window click-through and keep it out of Alt+Tab / activation so
        // it is purely a decorative overlay that never interrupts the desktop.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE,
            ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
