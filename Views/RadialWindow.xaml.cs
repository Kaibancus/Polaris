using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DesktopPanel.Models;
using DesktopPanel.Services;

namespace DesktopPanel.Views;

/// <summary>
/// Transparent, top-most radial launcher overlay. Shows app icons on concentric
/// rings with a center settings button. Supports hover animation, click-to-launch,
/// drag-to-reorder and drag-out-to-delete.
/// </summary>
public partial class RadialWindow : Window
{
    private const double DragThreshold = 6.0;
    private const double InnerRadius = 140.0;
    private const double RingStep = 88.0;
    private const int Ring0Cap = 12;
    private const int Ring1Cap = 24;

    // Outer-ring icons are drawn slightly larger than inner-ring icons.
    private const double OuterIconScale = 1.18;

    // Saturn's self-rotation period (seconds per turn) used for the centre
    // planet's idle spin and as the reference for the ring revolution speeds.
    private const double PlanetSpinSeconds = 60.0;

    // Ring revolution periods are set in real Saturn proportion to the planet's
    // self-rotation. With Saturn rotating in ~10.656 h, Keplerian orbital
    // periods give: B ring (inner icons, 1.739 Rs) ~9.62 h and F ring (outer
    // icons, 2.324 Rs) ~14.86 h, i.e. 0.902x and 1.394x the rotation period.
    private const double InnerOrbitRatio = 0.9023;
    private const double OuterOrbitRatio = 1.3941;

    // Rotation transforms applied to the two ring "orbit" layers so the inner
    // and outer ring bands slowly revolve about the planet. The icons do NOT
    // sit on these layers, so they stay put while the rings turn beneath them.
    private readonly RotateTransform _innerOrbit = new RotateTransform(0);
    private readonly RotateTransform _outerOrbit = new RotateTransform(0);

    // The two rotating ring layers, and the layer ring strokes are drawn into
    // (null = the static PanelCanvas, used for the disc background and icons).
    private Canvas? _innerOrbitLayer;
    private Canvas? _outerOrbitLayer;
    private Canvas? _ringLayer;

    private readonly AppConfig _config;
    private readonly Action _persist;
    private readonly Dictionary<string, BitmapSource?> _iconCache = new();

    // Periodically refreshes each icon's running indicator while the panel is shown.
    private readonly System.Windows.Threading.DispatcherTimer _runningTimer;

    // Set while showing the window so the SizeChanged fired by Show() does not
    // trigger a premature (wrong-centre) Rebuild that would flash the ring.
    private bool _suppressRebuild;

    // Intended visible state. Guards the deferred build callback so it does not
    // re-apply a fade after the panel was hidden again (fast key press/release).
    private bool _shown;

    // Whether the window has been realised (shown once) at startup.
    private bool _realized;

    private Point _center;
    private double _outerRadius;
    private readonly List<Point> _slotPositions = new();

    // Drag state
    private RadialIcon? _pressedIcon;
    private Point _pressPoint;
    private bool _dragging;

    // Icons in current _config.Apps order, parallel to the entries. Used to
    // animate the non-dragged icons aside while reordering.
    private readonly List<RadialIcon> _iconElements = new();

    // Slot the dragged icon is currently hovering toward, expressed as a target
    // ring (0 = inner, 1 = outer, -1 = none) and angular position within it.
    private int _dragTargetRing = -1;
    private int _dragTargetPos = -1;

    // Icon the pointer is currently hovering (for the "spread apart" effect).
    private RadialIcon? _hoverIcon;

    /// <summary>
    /// When true the panel stays open (opened from the tray) so the user can
    /// drag desktop shortcuts onto it. Key-release will not hide it.
    /// </summary>
    public bool IsPinned { get; private set; }

    public event Action? RequestOpenSettings;

    public RadialWindow(AppConfig config, Action persist)
    {
        _config = config;
        _persist = persist;
        InitializeComponent();

        SizeToPrimaryScreen();
        Loaded += (_, _) => Rebuild();
        SizeChanged += (_, _) => { if (!_suppressRebuild) Rebuild(); };

        _runningTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5),
        };
        _runningTimer.Tick += (_, _) => RefreshRunningStates();
    }

    private void SizeToPrimaryScreen()
    {
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        UpdateCenter();
    }

    /// <summary>
    /// Uses the actual rendered size of the canvas so the ring stays centered
    /// even when DPI scaling makes the window's real size differ from Width/Height.
    /// </summary>
    private void UpdateCenter()
    {
        double w = PanelCanvas.ActualWidth > 0 ? PanelCanvas.ActualWidth
                 : RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth
                 : Width;
        double h = PanelCanvas.ActualHeight > 0 ? PanelCanvas.ActualHeight
                 : RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight
                 : Height;
        _center = new Point(w / 2.0, h / 2.0);
    }

    public void ShowPanel()
    {
        IsPinned = false;
        ShowFaded(pinned: false);
    }

    /// <summary>
    /// Shows the panel in pinned mode (stays open until explicitly closed),
    /// so the user can drag desktop shortcuts onto the ring.
    /// </summary>
    public void ShowPinned()
    {
        ShowFaded(pinned: true);
    }

    /// <summary>True while the overlay is faded in and interactive.</summary>
    public bool IsShown => _shown;

    /// <summary>
    /// Realises the transparent overlay once at startup and keeps it shown but
    /// fully transparent. Re-showing an <c>AllowsTransparency</c> window with
    /// Hide()/Show() recreates its layered surface and flashes a frame every
    /// time; staying shown and only fading the opacity avoids that flicker.
    /// While Opacity is 0 the layered window is fully transparent, so mouse
    /// clicks pass straight through to the desktop beneath.
    /// </summary>
    public void Realize()
    {
        if (_realized) return;
        _realized = true;
        _suppressRebuild = true;
        SizeToPrimaryScreen();
        Opacity = 0;
        Show();                         // one-time; the window then stays shown
        _suppressRebuild = false;
        Rebuild();
        _runningTimer.Stop();           // nothing to poll while hidden
    }

    /// <summary>
    /// Fades the already-realised overlay in. No Hide()/Show() is involved, so
    /// there is no layered-surface flash.
    /// </summary>
    private void ShowFaded(bool pinned)
    {
        Realize();                      // safety: ensure the window exists
        _shown = true;
        if (pinned)
            IsPinned = true;

        SizeToPrimaryScreen();
        _suppressRebuild = false;
        Rebuild();                      // pick up any config changes
        Topmost = true;
        Activate();
        _runningTimer.Start();

        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240));
        BeginAnimation(OpacityProperty, fade);
    }

    public void HidePanel()
    {
        _shown = false;
        IsPinned = false;
        CancelDrag();
        _runningTimer.Stop();

        // Fade out instead of hiding; at Opacity 0 the window is click-through.
        BeginAnimation(OpacityProperty, null);
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(200));
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Hides the panel only if it is not pinned (used on key-release).</summary>
    public void HideIfNotPinned()
    {
        if (!IsPinned)
            HidePanel();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Keep the always-shown overlay out of Alt+Tab and the taskbar.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            HidePanel();
            e.Handled = true;
        }
    }

    private Brush LabelBrush => new SolidColorBrush(ParseColor(_config.Settings.FontColor, Colors.White));
    private Color AccentColor => ParseColor(_config.Settings.AccentColor, Color.FromRgb(0x3D, 0x7E, 0xFF));

    private void Rebuild()
    {
        UpdateCenter();
        PanelCanvas.Children.Clear();
        _iconElements.Clear();
        _hoverIcon = null;
        ComputeLayout(_config.Apps.Count);

        // Two rotating layers that carry the ring bands so the inner and outer
        // ring groups revolve about the planet at real Saturn proportions. The
        // layers span the whole panel so their rotation centre (0.5,0.5)
        // coincides with the panel centre. Icons are NOT placed on these layers.
        double pw = _center.X * 2.0;
        double ph = _center.Y * 2.0;
        _innerOrbitLayer = new Canvas
        {
            Width = pw,
            Height = ph,
            IsHitTestVisible = false,
            Opacity = 0.78,                   // match the planet's translucency
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _innerOrbit,
        };
        _outerOrbitLayer = new Canvas
        {
            Width = pw,
            Height = ph,
            IsHitTestVisible = false,
            Opacity = 0.78,                   // match the planet's translucency
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _outerOrbit,
        };
        PanelCanvas.Children.Add(_innerOrbitLayer);
        PanelCanvas.Children.Add(_outerOrbitLayer);

        DrawBackingDisc();

        int r0 = EffectiveRing0Count(_config.Apps.Count);
        for (int i = 0; i < _config.Apps.Count; i++)
        {
            var entry = _config.Apps[i];
            double size = i < r0 ? _config.Settings.IconSize
                                 : _config.Settings.IconSize * OuterIconScale;
            var icon = CreateIcon(entry, size);
            PlaceCentered(icon, _slotPositions[i]);
            PanelCanvas.Children.Add(icon);
            _iconElements.Add(icon);
        }

        DrawCenterButton();
        StartOrbits();
        RefreshRunningStates();
    }

    /// <summary>Updates each icon's flowing-blue running indicator.</summary>
    private void RefreshRunningStates()
    {
        // Enumerate processes on a background thread so the (relatively slow)
        // snapshot never blocks the UI thread and stutters the light animation.
        var icons = new List<RadialIcon>(_iconElements);
        System.Threading.Tasks.Task.Run(() =>
        {
            var running = RunningAppTracker.SnapshotRunningWindowNames();
            Dispatcher.BeginInvoke(() =>
            {
                foreach (var icon in icons)
                {
                    try
                    {
                        icon.IsRunning = RunningAppTracker.IsRunningByName(icon.Entry.Path, running);
                    }
                    catch
                    {
                        icon.IsRunning = false;
                    }
                }
            });
        });
    }

    /// <summary>Starts (or restarts) the inner/outer ring revolution at periods
    /// proportional to the planet's self-rotation, matching real Saturn ratios.</summary>
    private void StartOrbits()
    {
        StartOrbit(_innerOrbit, PlanetSpinSeconds * InnerOrbitRatio);
        StartOrbit(_outerOrbit, PlanetSpinSeconds * OuterOrbitRatio);
    }

    private static void StartOrbit(RotateTransform rt, double secondsPerTurn)
    {
        rt.BeginAnimation(RotateTransform.AngleProperty, null);
        rt.Angle = 0;
        var anim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(secondsPerTurn))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        rt.BeginAnimation(RotateTransform.AngleProperty, anim);
    }

    private RadialIcon CreateIcon(AppEntry entry, double iconSize)
    {
        if (!_iconCache.TryGetValue(entry.EffectiveIconSource, out var bmp))
        {
            bmp = IconExtractor.GetIcon(entry.EffectiveIconSource);
            _iconCache[entry.EffectiveIconSource] = bmp;
        }

        var icon = new RadialIcon(entry, bmp, iconSize, AccentColor, LabelBrush);
        icon.PreviewMouseLeftButtonDown += Icon_PreviewMouseLeftButtonDown;
        icon.HoverStarted += OnIconHoverStarted;
        icon.HoverEnded += OnIconHoverEnded;
        return icon;
    }

    private void DrawBackingDisc()
    {
        double icon = _config.Settings.IconSize;
        double outerIcon = icon * OuterIconScale;
        double r = _outerRadius + outerIcon;
        double d = r * 2;

        // --- Realistic Saturn ring system ------------------------------------
        // Ring order from the planet outward (matching the real Saturn):
        //   D · C · B · [Cassini Division] · A · [Roche Division] · F
        // The inner-ring icons (centred at InnerRadius) ride on the bright B
        // ring; the Cassini Division forms the empty gap between the inner and
        // outer icon groups; the outer-ring icons (centred at InnerRadius +
        // RingStep) ride on the thin F ringlet.

        // Near-black disc background, slightly translucent so the desktop shows
        // through faintly; also the interactive / drop-target area.
        var hit = new Ellipse
        {
            Width = d,
            Height = d,
            Fill = new SolidColorBrush(Color.FromArgb(0xE6, 0, 0, 0)),
        };
        // Place the disc at the very bottom so the rotating ring layers (already
        // added to PanelCanvas) render on top of it rather than being hidden.
        Canvas.SetLeft(hit, _center.X - r);
        Canvas.SetTop(hit, _center.Y - r);
        PanelCanvas.Children.Insert(0, hit);

        bool hasOuter = _outerRadius > InnerRadius + 0.5;

        double rB = InnerRadius;                 // bright B ring -> inner icons
        double rF = InnerRadius + RingStep;      // thin F ringlet -> outer icons

        // Planet body radius (must match DrawCenterButton: IconSize * 2.5 / 2).
        double planetR = icon * 2.5 / 2.0;

        // --- Real Saturn ring radii (in units of Saturn's equatorial radius) -
        // From NASA/Cassini data; the planet surface is at 1.0.
        const double Rplanet = 1.000;
        const double RDin = 1.110, RDout = 1.236;   // D ring
        const double RCin = 1.239, RCout = 1.526;   // C ring (crepe)
        const double RBin = 1.526, RBmid = 1.739, RBout = 1.951; // B ring (brightest)
        const double RCassIn = 1.951, RCassOut = 2.025;          // Cassini Division
        const double RAin = 2.025, REncke = 2.214, RAout = 2.269; // A ring + Encke gap
        const double RRoche = 2.320, RF = 2.324;    // Roche Division + F ringlet

        // Piecewise-linear map R (Saturn radii) -> pixels, anchored so that the
        // planet edge sits at planetR, the B-ring centre at rB (inner icons),
        // and the F ring at rF (outer icons). The inner segment governs the
        // planet->B span; the outer segment the B->F span. This preserves the
        // *relative* widths and gaps of the real rings (notably the wide
        // Cassini Division) while keeping the icon anchors exact.
        double kIn = (rB - planetR) / (RBmid - Rplanet);
        double bOutPx = planetR + (RBout - Rplanet) * kIn;
        double kOut = (rF - bOutPx) / (RF - RBout);
        double MapR(double rr) => rr <= RBout
            ? planetR + (rr - Rplanet) * kIn
            : bOutPx + (rr - RBout) * kOut;

        // Particle tints: B ring is the brightest/whitest, A a touch greyer,
        // C ring is the dim translucent "crepe" ring, D the faintest dust,
        // G/E rings are icy and slightly bluish. Kept a little dim/muted so the
        // rings sit calmly on the black disc.
        Color paleB = Color.FromRgb(0xCB, 0xBC, 0x95);
        Color tanA = Color.FromRgb(0xAA, 0x8E, 0x64);
        Color dimC = Color.FromRgb(0x70, 0x5C, 0x3D);
        Color faintD = Color.FromRgb(0x54, 0x46, 0x30);
        Color icyG = Color.FromRgb(0x97, 0xA3, 0xA8);

        // --- Inner group: D, C, B (icons land on the B ring) -----------------
        // Drawn into the inner rotating layer so the inner bands revolve.
        _ringLayer = _innerOrbitLayer;
        DrawRingZone(MapR(RDin), MapR(RDout), faintD, 0.07, 0.14, icon);   // D ring (faint dust)
        DrawRingZone(MapR(RCin), MapR(RCout), dimC, 0.18, 0.35, icon);     // C ring (crepe)
        DrawRingZone(MapR(RBin), MapR(RBout), paleB, 0.60, 0.66, icon);    // B ring (bright, widest)

        if (hasOuter)
        {
            // Outer group drawn into the outer rotating layer (slower revolution).
            _ringLayer = _outerOrbitLayer;

            // Cassini Division: the prominent dark gap between B and A. Drawn
            // only as a barely-there dust hint so it reads as empty space; it
            // also forms the separation between the inner and outer icon groups.
            DrawRingZone(MapR(RCassIn), MapR(RCassOut), faintD, 0.03, 0.04, icon);

            // A ring, split by the thin dark Encke gap.
            DrawRingZone(MapR(RAin), MapR(REncke - 0.004), tanA, 0.42, 0.50, icon);   // A inner
            DrawRingZone(MapR(REncke + 0.004), MapR(RAout), tanA, 0.44, 0.48, icon);  // A outer

            // Roche Division then the narrow, bright F ringlet centred on rF.
            DrawRingZone(MapR(RRoche), MapR(RF) - icon * 0.06, faintD, 0.03, 0.05, icon); // Roche gap
            DrawRingZone(rF - icon * 0.09, rF + icon * 0.09, paleB, 0.62, 0.70, icon * 0.42); // F ring

            // --- Faint outer rings: a modest gap, then the narrow G ring,
            // then the very broad, diffuse E ring fading to the disc edge.
            // (G/E are radially compressed to sit close to F inside the disc.) --
            double gIn = rF + outerIcon * 0.34;            // tighter Roche-to-G gap
            DrawRingZone(gIn, gIn + icon * 0.06, icyG, 0.18, 0.25, icon * 0.5); // G ring (narrow)
            double eIn = gIn + icon * 0.18;
            double eOut = r - icon * 0.04;
            DrawRingZone(eIn, eOut, icyG, 0.147, 0.034, icon);                  // E ring (broad halo)
        }

        _ringLayer = null; // subsequent draws (icons, planet) stay on PanelCanvas
    }

    /// <summary>
    /// Draws one named Saturn ring zone as a dense stack of concentric particle
    /// strokes from <paramref name="rInner"/> to <paramref name="rOuter"/>, with
    /// alpha ramping from <paramref name="aInner"/> to <paramref name="aOuter"/>
    /// and a subtle per-radius brightness flicker for a granular look.
    /// </summary>
    private void DrawRingZone(double rInner, double rOuter, Color color,
        double aInner, double aOuter, double iconSize)
    {
        if (rOuter <= rInner || rOuter <= 1)
            return;

        double spacing = Math.Max(1.4, iconSize * 0.030);
        double thickness = spacing * 1.7;
        int n = Math.Max(1, (int)Math.Round((rOuter - rInner) / spacing));

        for (int i = 0; i <= n; i++)
        {
            double t = n == 0 ? 0.5 : i / (double)n;
            double rr = rInner + (rOuter - rInner) * t;
            if (rr <= 1)
                continue;

            // Granular density variation across the zone.
            double flick = 0.80 + 0.20 * Math.Sin(rr * 0.7) * Math.Cos(rr * 0.23);
            double alpha = Math.Clamp((aInner + (aOuter - aInner) * t) * flick, 0, 1);
            double shadeT = 0.5 + 0.5 * Math.Sin(rr * 0.5);
            Color shade = LerpColor(Darken(color, 0.18), Lighten(color, 0.12), shadeT);

            var ring = new Ellipse
            {
                Width = rr * 2,
                Height = rr * 2,
                Stroke = new SolidColorBrush(WithAlpha(shade, alpha)),
                StrokeThickness = thickness,
                IsHitTestVisible = false,
            };
            StackCentered(ring, rr);
        }

        // Crisp bright edge on the outer rim of the zone.
        var rim = new Ellipse
        {
            Width = rOuter * 2,
            Height = rOuter * 2,
            Stroke = new SolidColorBrush(WithAlpha(Lighten(color, 0.25), aOuter * 0.5)),
            StrokeThickness = 1.0,
            IsHitTestVisible = false,
        };
        StackCentered(rim, rOuter);
    }

    private static Color LerpColor(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private void StackCentered(FrameworkElement el, double r)
    {
        Canvas.SetLeft(el, _center.X - r);
        Canvas.SetTop(el, _center.Y - r);
        (_ringLayer ?? PanelCanvas).Children.Add(el);
    }

    private static Color WithAlpha(Color c, double opacity)
    {
        byte a = (byte)Math.Clamp(opacity * 255.0, 0, 255);
        return Color.FromArgb(a, c.R, c.G, c.B);
    }

    private static Color Lighten(Color c, double amount)
    {
        return Color.FromRgb(
            (byte)Math.Clamp(c.R + (255 - c.R) * amount, 0, 255),
            (byte)Math.Clamp(c.G + (255 - c.G) * amount, 0, 255),
            (byte)Math.Clamp(c.B + (255 - c.B) * amount, 0, 255));
    }

    private static Color Darken(Color c, double amount)
    {
        return Color.FromRgb(
            (byte)Math.Clamp(c.R * (1 - amount), 0, 255),
            (byte)Math.Clamp(c.G * (1 - amount), 0, 255),
            (byte)Math.Clamp(c.B * (1 - amount), 0, 255));
    }

    private void DrawCenterButton()
    {
        double size = _config.Settings.IconSize * 2.5;
        double r = size / 2;

        // Saturn planet at the centre. Click opens settings; hovering slowly
        // rotates the atmospheric bands. Hosted in a Grid so it can scale/rotate
        // around its centre.
        var root = new Grid
        {
            Width = size,
            Height = size,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent, // keep the full square hit-testable
            ToolTip = "设置",
            Opacity = 0.78,                   // planet slightly translucent
            RenderTransformOrigin = new Point(0.5, 0.5),
        };

        // Soft drop shadow under the planet for depth.
        root.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 22,
            ShadowDepth = 0,
            Opacity = 0.55,
        };

        Color amber = Color.FromRgb(0xD8, 0xB4, 0x76);
        Color amberDark = Color.FromRgb(0x6E, 0x52, 0x2E);
        Color amberLight = Color.FromRgb(0xF6, 0xE6, 0xBE);

        // Circular planet body with a clip so the bands stay inside the globe.
        var globe = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(r),
            // Clip to a true circle so the atmospheric bands never fill the
            // square corners (ClipToBounds alone would clip to the rectangle).
            Clip = new EllipseGeometry(new Point(r, r), r, r),
            Background = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.36, 0.30),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.72,
                RadiusY = 0.72,
                GradientStops =
                {
                    new GradientStop(amberLight, 0.0),
                    new GradientStop(amber, 0.5),
                    new GradientStop(Darken(amber, 0.25), 0.82),
                    new GradientStop(amberDark, 1.0),
                },
            },
        };

        // Polar (top-down) view of Saturn: looking straight down the rotation
        // axis, perpendicular to the equatorial/ring plane. The latitude belts
        // therefore appear as concentric circles, and the whole disc spins
        // about its centre. Hosted in a rotating Canvas clipped to the globe.
        var discRotate = new RotateTransform(0);
        var disc = new Canvas
        {
            Width = size,
            Height = size,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = discRotate,
        };

        // Concentric latitude belts (circles) from the limb in to the pole.
        int beltCount = 7;
        for (int i = 0; i < beltCount; i++)
        {
            double tt = i / (double)(beltCount - 1);     // 0 = limb, 1 = pole
            double br = r * (1.0 - tt * 0.86);           // shrinking radius
            double s = 0.5 + 0.5 * Math.Sin(i * 2.0 + 0.6);
            Color shade = s < 0.5
                ? LerpColor(amberDark, amber, s * 2.0)
                : LerpColor(amber, amberLight, (s - 0.5) * 2.0);
            byte a = (byte)(70 + 80 * Math.Abs(Math.Sin(i * 1.4)));
            var belt = new Ellipse
            {
                Width = br * 2,
                Height = br * 2,
                Stroke = new SolidColorBrush(Color.FromArgb(a, shade.R, shade.G, shade.B)),
                StrokeThickness = Math.Max(2.0, size / beltCount * 0.62),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(belt, r - br);
            Canvas.SetTop(belt, r - br);
            disc.Children.Add(belt);
        }

        // Off-centre storm ovals so the rotation is clearly visible.
        (double fr, double ang, double fw, double fh, Color c, byte a)[] storms =
        {
            (0.52, 0.4, 0.22, 0.13, Lighten(amber, 0.22), 150),
            (0.40, 2.3, 0.16, 0.10, Darken(amber, 0.22), 140),
            (0.62, 4.1, 0.13, 0.08, amberLight, 120),
            (0.30, 5.2, 0.12, 0.08, Darken(amber, 0.16), 130),
        };
        foreach (var st in storms)
        {
            double cx = r + Math.Cos(st.ang) * r * st.fr;
            double cy = r + Math.Sin(st.ang) * r * st.fr;
            var storm = new Ellipse
            {
                Width = size * st.fw,
                Height = size * st.fh,
                Fill = new SolidColorBrush(Color.FromArgb(st.a, st.c.R, st.c.G, st.c.B)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(storm, cx - size * st.fw / 2);
            Canvas.SetTop(storm, cy - size * st.fh / 2);
            disc.Children.Add(storm);
        }

        // Saturn's north-polar hexagon at the centre — a hallmark of the
        // top-down view, and a clear visual anchor for the spin.
        var hex = new System.Windows.Shapes.Polygon
        {
            Stroke = new SolidColorBrush(Color.FromArgb(170, 0xF6, 0xE6, 0xBE)),
            StrokeThickness = Math.Max(1.0, size * 0.012),
            Fill = new SolidColorBrush(Color.FromArgb(60, 0x9A, 0x78, 0x44)),
            IsHitTestVisible = false,
        };
        double hexR = r * 0.16;
        for (int k = 0; k < 6; k++)
        {
            double ang = -Math.PI / 2 + k * Math.PI / 3;
            hex.Points.Add(new Point(r + Math.Cos(ang) * hexR, r + Math.Sin(ang) * hexR));
        }
        disc.Children.Add(hex);

        globe.Child = disc;
        root.Children.Add(globe);

        // Terminator shadow: darken the lower-right to give a spherical feel.
        var shadow = new Ellipse
        {
            Width = size,
            Height = size,
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.68, 0.72),
                Center = new Point(0.68, 0.72),
                RadiusX = 0.85,
                RadiusY = 0.85,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(150, 0, 0, 0), 0.0),
                    new GradientStop(Color.FromArgb(40, 0, 0, 0), 0.5),
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.85),
                },
            },
        };
        root.Children.Add(shadow);

        // Specular highlight on the upper-left.
        var highlight = new Ellipse
        {
            Width = size * 0.9,
            Height = size * 0.9,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.34, 0.26),
                Center = new Point(0.30, 0.22),
                RadiusX = 0.6,
                RadiusY = 0.6,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(150, 255, 255, 255), 0.0),
                    new GradientStop(Color.FromArgb(30, 255, 255, 255), 0.35),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.6),
                },
            },
        };
        root.Children.Add(highlight);

        // Crisp rim around the globe.
        var rim = new Ellipse
        {
            Width = size,
            Height = size,
            IsHitTestVisible = false,
            Stroke = new SolidColorBrush(Color.FromArgb(120, 0xFF, 0xF0, 0xCE)),
            StrokeThickness = 1.2,
        };
        root.Children.Add(rim);

        // --- Rotation: spin the polar disc about its centre. Always turning
        // slowly; hovering speeds it up. The animation is restarted from the
        // current angle so speed changes are seamless. --------------------
        void StartSpin(double secondsPerTurn)
        {
            double cur = discRotate.Angle % 360;
            discRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            discRotate.Angle = cur;
            var anim = new DoubleAnimation(cur, cur + 360,
                TimeSpan.FromSeconds(secondsPerTurn))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            discRotate.BeginAnimation(RotateTransform.AngleProperty, anim);
        }

        StartSpin(PlanetSpinSeconds);            // gentle idle self-rotation
        root.MouseEnter += (_, _) => StartSpin(3.0);
        root.MouseLeave += (_, _) => StartSpin(PlanetSpinSeconds);

        root.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            RequestOpenSettings?.Invoke();
        };
        Panel.SetZIndex(root, 2000); // keep Saturn above the ring bands
        Canvas.SetLeft(root, _center.X - size / 2);
        Canvas.SetTop(root, _center.Y - size / 2);
        PanelCanvas.Children.Add(root);
    }

    private void PlaceCentered(FrameworkElement el, Point center)
    {
        double s = el is RadialIcon ri ? ri.IconSize : _config.Settings.IconSize;
        Canvas.SetLeft(el, center.X - s / 2);
        Canvas.SetTop(el, center.Y - s / 2);
    }

    /// <summary>Computes ring slot positions for <paramref name="count"/> icons,
    /// split across up to two rings per the inner-ring count.</summary>
    private void ComputeLayout(int count)
    {
        _slotPositions.Clear();
        _outerRadius = InnerRadius;

        if (count <= 0)
            return;

        int r0 = EffectiveRing0Count(count);
        _slotPositions.AddRange(SlotPositionsFor(count, r0));
        _outerRadius = (count - r0 > 0) ? InnerRadius + RingStep : InnerRadius;
    }

    // ---- External drop (add desktop shortcuts) ---------------------------

    private void OnDragOverPanel(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropPanel(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        bool added = false;
        foreach (var f in files)
        {
            var entry = ShortcutResolver.CreateEntry(f);
            if (entry != null && !string.IsNullOrWhiteSpace(entry.Path))
            {
                _config.Apps.Add(entry);
                added = true;
            }
        }

        if (added)
        {
            _persist();
            Rebuild();
        }
        e.Handled = true;
    }

    // ---- Drag & click handling -------------------------------------------

    private void Icon_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressedIcon = (RadialIcon)sender;
        _pressPoint = e.GetPosition(PanelCanvas);
        _dragging = false;
        _dragTargetRing = -1;
        _dragTargetPos = -1;
        PanelCanvas.CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressedIcon == null)
            return;

        Point p = e.GetPosition(PanelCanvas);
        if (!_dragging)
        {
            if ((p - _pressPoint).Length < DragThreshold)
                return;
            _dragging = true;
            Panel.SetZIndex(_pressedIcon, 1000);
            // Stop any residual reflow animation on the dragged icon so it tracks
            // the cursor exactly.
            _pressedIcon.BeginAnimation(Canvas.LeftProperty, null);
            _pressedIcon.BeginAnimation(Canvas.TopProperty, null);
        }

        PlaceCentered(_pressedIcon, p);

        double dist = (p - _center).Length;
        _pressedIcon.Opacity = dist > DeleteRadius ? 0.4 : 1.0;

        // Push other icons aside to reveal the slot the dragged icon is over.
        // Skip while the icon is dragged out past the outer ring (delete zone).
        if (dist <= DeleteRadius)
        {
            int src = _iconElements.IndexOf(_pressedIcon);
            var (ring, pos) = ComputeDragTarget(p, src);
            if (ring != _dragTargetRing || pos != _dragTargetPos)
            {
                _dragTargetRing = ring;
                _dragTargetPos = pos;
                ReflowAround(ring, pos);
            }
        }
        else if (_dragTargetRing != -1)
        {
            // Dragged into the delete zone — snap the others back to their slots.
            _dragTargetRing = -1;
            _dragTargetPos = -1;
            RestoreSlots();
        }
    }

    /// <summary>
    /// Determines which ring (0 inner / 1 outer) and angular position the dragged
    /// icon is targeting, honouring the per-ring caps and creating the outer ring
    /// when the icon is dragged out to that distance.
    /// </summary>
    private (int ring, int pos) ComputeDragTarget(Point p, int src)
    {
        int n = _config.Apps.Count;
        int r0 = EffectiveRing0Count(n);
        int o0 = (src >= 0 && src < r0) ? r0 - 1 : r0; // other icons on the inner ring
        int m = Math.Max(0, n - 1);
        int ring1Others = m - o0;

        double dist = (p - _center).Length;
        double ringMid = (InnerRadius + (InnerRadius + RingStep)) / 2.0;
        int ring = dist <= ringMid ? 0 : 1;

        // Respect caps: redirect to the other ring if the chosen one is full.
        if (ring == 0 && o0 + 1 > Ring0Cap)
            ring = 1;
        if (ring == 1 && ring1Others + 1 > Ring1Cap)
            ring = 0;

        int slotsAfter = ring == 0 ? o0 + 1 : ring1Others + 1;
        double ang = Math.Atan2(p.Y - _center.Y, p.X - _center.X);
        double fromTop = ang + Math.PI / 2.0; // 0 at 12 o'clock, clockwise
        fromTop = ((fromTop % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
        int pos = (int)Math.Round(fromTop / (2 * Math.PI) * slotsAfter);
        pos = Math.Clamp(pos, 0, Math.Max(0, slotsAfter - 1));
        return (ring, pos);
    }

    /// <summary>
    /// Number of icons on the inner ring for <paramref name="n"/> total icons,
    /// derived from the persisted <c>Ring0Count</c> (0 = auto) and clamped to the
    /// per-ring caps (inner ≤ 12, outer ≤ 24).
    /// </summary>
    private int EffectiveRing0Count(int n)
    {
        if (n <= 0)
            return 0;

        int r0 = _config.Settings.Ring0Count;
        if (r0 <= 0 || r0 > n)
            r0 = Math.Min(Ring0Cap, n); // auto: fill the inner ring first

        r0 = Math.Clamp(r0, 1, Math.Min(Ring0Cap, n));

        // If the outer ring would overflow its cap, push more onto the inner ring.
        if (n - r0 > Ring1Cap)
            r0 = Math.Min(Ring0Cap, n);
        return r0;
    }

    /// <summary>Builds the slot centres for a layout of <paramref name="n"/> icons
    /// with <paramref name="r0"/> of them on the inner ring.</summary>
    private List<Point> SlotPositionsFor(int n, int r0)
    {
        var list = new List<Point>(Math.Max(0, n));
        if (n <= 0)
            return list;

        r0 = Math.Clamp(r0, 1, n);
        int ring1 = n - r0;
        for (int k = 0; k < r0; k++)
            list.Add(RingPoint(InnerRadius, k, r0));
        for (int k = 0; k < ring1; k++)
            list.Add(RingPoint(InnerRadius + RingStep, k, ring1));
        return list;
    }

    private Point RingPoint(double radius, int k, int count)
    {
        double angle = -Math.PI / 2 + 2 * Math.PI * k / Math.Max(1, count);
        return new Point(_center.X + radius * Math.Cos(angle),
                         _center.Y + radius * Math.Sin(angle));
    }

    /// <summary>
    /// Maps each entry index to its flat slot when the dragged entry
    /// <paramref name="src"/> is inserted into <paramref name="ring"/> at angular
    /// position <paramref name="pos"/>; also returns the resulting inner-ring count.
    /// </summary>
    private (int[] slotOfEntry, int newR0) ComputeArrangement(int src, int ring, int pos)
    {
        int n = _config.Apps.Count;
        int r0 = EffectiveRing0Count(n);
        int srcRing = src < r0 ? 0 : 1;

        // Current angular sequences per ring. Rebuild() places entry i at flat
        // slot i, so the inner ring is entries 0..r0-1 and the outer ring is the
        // rest, both already in angular (clockwise-from-top) order.
        var inner = new List<int>();
        for (int i = 0; i < r0; i++)
            inner.Add(i);
        var outer = new List<int>();
        for (int i = r0; i < n; i++)
            outer.Add(i);

        int newR0;
        if (ring == srcRing)
        {
            // Same ring: shift only the icons on the shorter arc between the
            // dragged icon's current angular slot and the target slot, so
            // crossing the 12 o'clock boundary nudges neighbours instead of
            // rotating the whole ring.
            var seq = ring == 0 ? inner : outer;
            int len = seq.Count;
            int cur = seq.IndexOf(src);
            int tgt = Math.Clamp(pos, 0, Math.Max(0, len - 1));
            int[] newIdx = ShortestArcShift(len, cur, tgt);

            var newSeq = new int[len];
            for (int j = 0; j < len; j++)
                newSeq[newIdx[j]] = seq[j];

            seq.Clear();
            seq.AddRange(newSeq);
            newR0 = r0;
        }
        else
        {
            // Cross ring: remove from the source ring and insert into the target
            // ring at the angular position. Both rings re-space by their new
            // counts, which is the expected behaviour when moving between rings.
            if (srcRing == 0)
                inner.Remove(src);
            else
                outer.Remove(src);

            var tgt = ring == 0 ? inner : outer;
            int insertAt = Math.Clamp(pos, 0, tgt.Count);
            tgt.Insert(insertAt, src);
            newR0 = inner.Count;
        }

        int[] slotOfEntry = new int[n];
        int slot = 0;
        foreach (int e in inner)
            slotOfEntry[e] = slot++;
        foreach (int e in outer)
            slotOfEntry[e] = slot++;
        return (slotOfEntry, newR0);
    }

    /// <summary>
    /// For a ring of <paramref name="len"/> slots, returns the new angular index
    /// of each current index when the icon at <paramref name="cur"/> moves to
    /// <paramref name="tgt"/>, shifting only the shorter arc between them by one.
    /// </summary>
    private static int[] ShortestArcShift(int len, int cur, int tgt)
    {
        int[] newIdx = new int[Math.Max(0, len)];
        if (len <= 0)
            return newIdx;

        cur = Math.Clamp(cur, 0, len - 1);
        tgt = Math.Clamp(tgt, 0, len - 1);
        newIdx[cur] = tgt;

        int df = ((tgt - cur) % len + len) % len; // forward steps cur -> tgt
        int db = len - df;                          // backward steps
        bool forward = df <= db;

        for (int j = 0; j < len; j++)
        {
            if (j == cur)
                continue;
            int ns = j;
            if (forward)
            {
                int rel = ((j - cur) % len + len) % len; // 1..len-1
                if (rel >= 1 && rel <= df)
                    ns = (j - 1 + len) % len;
            }
            else
            {
                int relT = ((j - tgt) % len + len) % len; // 0..len-1
                if (relT >= 0 && relT < db)
                    ns = (j + 1) % len;
            }
            newIdx[j] = ns;
        }
        return newIdx;
    }

    // ---- Hover "spread apart" --------------------------------------------

    /// <summary>
    /// On hover, raise the icon above its neighbours and push the nearby icons
    /// radially away so the enlarged icon + its name label have room to breathe.
    /// </summary>
    private void OnIconHoverStarted(RadialIcon ic)
    {
        // Ignore hover effects while a drag is in progress.
        if (_pressedIcon != null)
            return;

        int idx = _iconElements.IndexOf(ic);
        if (idx < 0)
            return;

        _hoverIcon = ic;
        Panel.SetZIndex(ic, 500);
        SpreadNeighbours(idx);
    }

    private void OnIconHoverEnded(RadialIcon ic)
    {
        if (_pressedIcon != null)
            return;

        int idx = _iconElements.IndexOf(ic);
        if (idx >= 0)
            Panel.SetZIndex(ic, 0);

        if (_hoverIcon == ic)
            _hoverIcon = null;

        RestoreSlots();
    }

    /// <summary>
    /// Pushes icons near <paramref name="hovered"/> away from it, with the shift
    /// falling off by distance, so closer neighbours move more.
    /// </summary>
    private void SpreadNeighbours(int hovered)
    {
        double iconSize = _config.Settings.IconSize;
        double push = iconSize * 0.75;
        double influence = iconSize * 2.7;
        Point hp = _slotPositions[hovered];

        for (int i = 0; i < _iconElements.Count; i++)
        {
            if (i == hovered)
                continue;

            Vector v = _slotPositions[i] - hp;
            double d = v.Length;
            if (d > 0.01 && d < influence)
            {
                double amount = push * (1 - d / influence);
                Point np = _slotPositions[i] + (v / d) * amount;
                AnimateTo(_iconElements[i], np);
            }
            else
            {
                AnimateTo(_iconElements[i], _slotPositions[i]);
            }
        }
    }

    /// <summary>Animates all icons back to their home ring slots.</summary>
    private void RestoreSlots()
    {
        for (int i = 0; i < _iconElements.Count && i < _slotPositions.Count; i++)
        {
            if (_iconElements[i] == _pressedIcon)
                continue;
            AnimateTo(_iconElements[i], _slotPositions[i]);
        }
    }

    /// <summary>
    /// Animates every non-dragged icon to its slot in the prospective layout
    /// where the dragged icon occupies (<paramref name="ring"/>, <paramref name="pos"/>),
    /// producing the "make room" effect across both rings.
    /// </summary>
    private void ReflowAround(int ring, int pos)
    {
        int src = _iconElements.IndexOf(_pressedIcon!);
        if (src < 0)
            return;

        var (slotOfEntry, newR0) = ComputeArrangement(src, ring, pos);
        int n = _config.Apps.Count;
        var positions = SlotPositionsFor(n, newR0);
        for (int i = 0; i < _iconElements.Count; i++)
        {
            if (_iconElements[i] == _pressedIcon)
                continue;
            int slot = slotOfEntry[i];
            if (slot >= 0 && slot < positions.Count)
                AnimateTo(_iconElements[i], positions[slot]);
        }
    }

    /// <summary>Smoothly slides an icon to a new slot center.</summary>
    private void AnimateTo(FrameworkElement el, Point center)
    {
        double s = el is RadialIcon ri ? ri.IconSize : _config.Settings.IconSize;
        double left = center.X - s / 2;
        double top = center.Y - s / 2;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var la = new DoubleAnimation(left, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        var ta = new DoubleAnimation(top, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        el.BeginAnimation(Canvas.LeftProperty, la);
        el.BeginAnimation(Canvas.TopProperty, ta);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_pressedIcon == null)
        {
            // Clicking empty space no longer closes the pinned panel — it stays
            // open for drag-to-add; use Esc to dismiss it.
            return;
        }

        var icon = _pressedIcon;
        bool wasDragging = _dragging;
        Point p = e.GetPosition(PanelCanvas);
        int ring = _dragTargetRing;
        int pos = _dragTargetPos;

        PanelCanvas.ReleaseMouseCapture();
        _pressedIcon = null;
        _dragging = false;
        _dragTargetRing = -1;
        _dragTargetPos = -1;

        if (!wasDragging)
        {
            Launch(icon.Entry);
            return;
        }

        double dist = (p - _center).Length;
        if (dist > DeleteRadius)
        {
            DeleteEntry(icon.Entry);
        }
        else
        {
            CommitArrangement(icon.Entry, ring, pos, p);
        }
    }

    /// <summary>Distance past the outer ring beyond which a dropped icon is deleted.</summary>
    private double DeleteRadius => InnerRadius + RingStep + _config.Settings.IconSize * 1.25;

    private void CommitArrangement(AppEntry entry, int ring, int pos, Point dropPoint)
    {
        int src = _config.Apps.IndexOf(entry);
        if (src < 0)
        {
            Rebuild();
            return;
        }

        if (ring < 0)
            (ring, pos) = ComputeDragTarget(dropPoint, src);

        var (slotOfEntry, newR0) = ComputeArrangement(src, ring, pos);
        int n = _config.Apps.Count;

        // Reorder the entries by their new slot so Rebuild() (entry i -> slot i)
        // reproduces the arrangement, and persist the new inner-ring count.
        var ordered = new AppEntry[n];
        for (int i = 0; i < n; i++)
            ordered[slotOfEntry[i]] = _config.Apps[i];

        _config.Apps.Clear();
        foreach (var a in ordered)
            _config.Apps.Add(a);

        _config.Settings.Ring0Count = Math.Clamp(newR0, 0, n);

        _persist();
        Rebuild();
    }

    private void DeleteEntry(AppEntry entry)
    {
        int n = _config.Apps.Count;
        int r0 = EffectiveRing0Count(n);
        int idx = _config.Apps.IndexOf(entry);

        _config.Apps.Remove(entry);

        // If an inner-ring icon was removed, keep the inner-ring count in step.
        if (idx >= 0 && idx < r0)
            _config.Settings.Ring0Count = Math.Max(0, r0 - 1);

        _persist();
        Rebuild();
    }

    private void CancelDrag()
    {
        if (_pressedIcon != null)
        {
            PanelCanvas.ReleaseMouseCapture();
            _pressedIcon = null;
            _dragging = false;
            _dragTargetRing = -1;
            _dragTargetPos = -1;
        }
    }

    private void Launch(AppEntry entry)
    {
        HidePanel();

        // If the program is already running, bring its existing window to the
        // foreground instead of starting a second instance.
        try
        {
            if (RunningAppTracker.ActivateExisting(entry.Path))
                return;
        }
        catch
        {
            // Fall through to a normal launch if activation fails.
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = entry.Path,
                Arguments = entry.Arguments,
                UseShellExecute = true,
            };
            if (!string.IsNullOrWhiteSpace(entry.WorkingDirectory))
                psi.WorkingDirectory = entry.WorkingDirectory;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法启动 {entry.Name}:\n{ex.Message}", "DesktopPanel",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
                return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
        }
        return fallback;
    }
}
