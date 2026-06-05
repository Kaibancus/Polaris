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

    // Vertical squash applied to the ring plane so it reads as a tilted disc
    // seen in perspective (1.0 = top-down circle, smaller = more edge-on). The
    // planet body stays a circle (a sphere looks round from any angle); only
    // the flat ring plane and the icon orbits are foreshortened into ellipses.
    private const double RingTiltY = 0.97;

    // Orbit-angle transforms. They no longer rotate the (now elliptical) ring
    // layers — spinning a concentric ellipse would make it tumble — instead
    // they drive the two "shimmer" highlights that sweep along the elliptical
    // ring orbits, which is what now conveys the revolution.
    private readonly RotateTransform _innerOrbit = new RotateTransform(0);
    private readonly RotateTransform _outerOrbit = new RotateTransform(0);

    // The two ring layers the bands are drawn into (kept static now that the
    // bands are foreshortened ellipses). null = the static PanelCanvas.
    private Canvas? _innerOrbitLayer;
    private Canvas? _outerOrbitLayer;
    private Canvas? _ringLayer;

    // Current per-ring vertical radius factor used by StackCentered so a band
    // can be stacked as an ellipse (height = width * factor). 1.0 = circle.
    private double _stackTiltY = 1.0;

    private readonly AppConfig _config;
    private readonly Action _persist;
    private readonly Dictionary<string, BitmapSource?> _iconCache = new();

    // Active visual theme (layout + background). Resolved from config on Rebuild.
    private PanelTheme _theme = ThemeRegistry.Get("saturn");

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
        AnimateRingsExpand();           // grow the rings out from the centre
        Topmost = true;
        Activate();
        _runningTimer.Start();

        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(210));
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Animates the two ring layers growing out from the planet, the
    /// inner band leading and the outer band following, for a "summon" feel.</summary>
    private void AnimateRingsExpand()
    {
        ExpandLayer(_innerOrbitLayer, 0.0);
        ExpandLayer(_outerOrbitLayer, 0.11);
    }

    private void ExpandLayer(Canvas? layer, double delaySeconds)
    {
        if (layer == null)
            return;

        layer.RenderTransformOrigin = new Point(0.5, 0.5);
        var sc = new ScaleTransform(0.55, 0.55);
        layer.RenderTransform = sc;

        var begin = TimeSpan.FromSeconds(delaySeconds);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var grow = new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(380))
        {
            BeginTime = begin,
            EasingFunction = ease,
        };
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, grow.Clone());

        var fadeIn = new DoubleAnimation(0, 0.78, TimeSpan.FromMilliseconds(380))
        {
            BeginTime = begin,
            EasingFunction = ease,
        };
        layer.BeginAnimation(OpacityProperty, fadeIn);
    }

    public void HidePanel()
    {
        _shown = false;
        IsPinned = false;
        CancelDrag();
        _runningTimer.Stop();

        // Fade out instead of hiding; at Opacity 0 the window is click-through.
        // Capture the current (animated) opacity BEFORE replacing the animation
        // so we start the fade from what's on screen. Do NOT clear the animation
        // first: clearing snaps Opacity back to its base value (0, set in
        // ShowFaded), which would make the fade run 0->0 and the panel vanish.
        double from = Opacity;
        var fade = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(240));
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

        // Resolve the active theme from config each rebuild so switching the
        // theme in settings takes effect on the next render.
        _theme = ThemeRegistry.Get(_config.Settings.Theme);
        RootGrid.Background = _theme.WindowBackground;

        PanelCanvas.Children.Clear();
        _iconElements.Clear();
        _hoverIcon = null;

        // --- Layout (theme-driven) -------------------------------------------
        if (_theme.IsSaturn)
        {
            ComputeLayout(_config.Apps.Count);
        }
        else
        {
            _slotPositions.Clear();
            _slotPositions.AddRange(_theme.ComputeSlots(
                _config.Apps.Count, _center, _config.Settings, out _outerRadius));
        }

        // --- Background / animation (Saturn only) ----------------------------
        if (_theme.IsSaturn)
        {
            // Two rotating layers that carry the ring bands so the inner and
            // outer ring groups revolve about the planet at real Saturn
            // proportions. The layers span the whole panel so their rotation
            // centre (0.5,0.5) coincides with the panel centre. Icons are NOT
            // placed on these layers.
            double pw = _center.X * 2.0;
            double ph = _center.Y * 2.0;
            _innerOrbitLayer = new Canvas
            {
                Width = pw,
                Height = ph,
                IsHitTestVisible = false,
                Opacity = 0.78,                   // match the planet's translucency
            };
            _outerOrbitLayer = new Canvas
            {
                Width = pw,
                Height = ph,
                IsHitTestVisible = false,
                Opacity = 0.78,                   // match the planet's translucency
            };
            PanelCanvas.Children.Add(_innerOrbitLayer);
            PanelCanvas.Children.Add(_outerOrbitLayer);

            DrawBackingDisc();
        }
        else
        {
            _innerOrbitLayer = null;
            _outerOrbitLayer = null;
        }

        int r0 = _theme.IsSaturn ? EffectiveRing0Count(_config.Apps.Count) : int.MaxValue;
        for (int i = 0; i < _config.Apps.Count && i < _slotPositions.Count; i++)
        {
            var entry = _config.Apps[i];
            double size = (_theme.IsSaturn && i >= r0)
                ? _config.Settings.IconSize * OuterIconScale
                : _config.Settings.IconSize;
            var icon = CreateIcon(entry, size);
            PlaceCentered(icon, _slotPositions[i]);
            PanelCanvas.Children.Add(icon);
            _iconElements.Add(icon);
        }

        if (_theme.IsSaturn)
        {
            DrawCenterButton();
            StartOrbits();
        }
        else
        {
            DrawSimpleCenterButton();
        }

        RefreshRunningStates();
    }

    /// <summary>Refreshes the panel from the current config (e.g. after the
    /// theme or icon size changes in settings).</summary>
    public void RefreshFromConfig()
    {
        if (IsLoaded)
            Rebuild();
    }

    /// <summary>A minimal centre control used by non-Saturn themes: a small
    /// circular button (placed above the grid) that opens settings.</summary>
    private void DrawSimpleCenterButton()
    {
        double s = Math.Max(34, _config.Settings.IconSize * 0.7);
        var btn = new Border
        {
            Width = s,
            Height = s,
            CornerRadius = new CornerRadius(s / 2),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x20, 0x20, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x60, 0x60, 0x60)),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = "⚙",
                FontSize = s * 0.5,
                Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0x33, 0x33, 0x33)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        btn.MouseLeftButtonUp += (_, e) => { e.Handled = true; RequestOpenSettings?.Invoke(); };

        // Place it above the grid so it never overlaps the icon cells.
        double gridTop = _center.Y - 2 * (_config.Settings.IconSize * 2.1);
        Canvas.SetLeft(btn, _center.X - s / 2);
        Canvas.SetTop(btn, gridTop - s - _config.Settings.IconSize * 0.6);
        Panel.SetZIndex(btn, 2000);
        PanelCanvas.Children.Add(btn);
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

        // Foreshorten every ring band stacked from here on into an ellipse.
        _stackTiltY = RingTiltY;

        // --- Realistic Saturn ring system ------------------------------------
        // Ring order from the planet outward (matching the real Saturn):
        //   D · C · B · [Cassini Division] · A · [Roche Division] · F
        // The inner-ring icons (centred at InnerRadius) ride on the bright B
        // ring; the Cassini Division forms the empty gap between the inner and
        // outer icon groups; the outer-ring icons (centred at InnerRadius +
        // RingStep) ride on the thin F ringlet.

        // Near-black disc background, foreshortened into an ellipse so it sits
        // in the same tilted plane as the rings. The user-configurable
        // "panel opacity" setting drives this disc's overall translucency so
        // the desktop shows through more or less behind the Saturn system.
        var hit = new Ellipse
        {
            Width = d,
            Height = d * RingTiltY,
            Opacity = Math.Clamp(_config.Settings.PanelOpacity, 0.0, 1.0),
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.46),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xFF, 0x05, 0x06, 0x0C), 0.0),
                    new GradientStop(Color.FromArgb(0xFF, 0x02, 0x03, 0x07), 0.72),
                    new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 1.0),
                },
            },
        };
        // Place the disc at the very bottom so the rotating ring layers (already
        // added to PanelCanvas) render on top of it rather than being hidden.
        Canvas.SetLeft(hit, _center.X - r);
        Canvas.SetTop(hit, _center.Y - r * RingTiltY);
        PanelCanvas.Children.Insert(0, hit);

        // Faint starfield sprinkled over the disc, behind the rings.
        DrawStarfield(r);

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

            // Soft outer bloom: a blurred icy halo over the faint G/E rings so
            // they glow and fade out rather than ending abruptly.
            AddBloomRing((gIn + eOut) / 2, (eOut - gIn) + icon * 0.7, icyG, 0.10);
        }

        // --- Ring-revolution cues ------------------------------------------
        // A continuously rotating axisymmetric ellipse looks identical to a
        // static one, so the revolution is conveyed by *local* features that
        // sweep along the ring orbits at the (differential) inner/outer rates:
        //   (1) bright shimmer arcs  (2) Voyager-style radial spokes
        //   (3) higher-density particle clumps  (4) a moving brightness edge
        //   (5) a subtle leading-edge cool/warm Doppler tint on the shimmers.
        double rBin = MapR(RBin), rBout = MapR(RBout);
        // (1) Inner B-ring shimmer: a single bright lead arc with a cool/warm
        // Doppler tint.
        AddShimmer(rB, _innerOrbit, paleB, phaseDeg: 0, intensity: 1.0, arcSpan: 0.30);
        // (2) Spokes anchored across the B ring, revolving with the inner orbit.
        AddSpoke(rBin, rBout, _innerOrbit, phaseDeg: 24, widthDeg: 7.0, alpha: 0.30);
        AddSpoke(rBin, rBout, _innerOrbit, phaseDeg: 132, widthDeg: 5.0, alpha: 0.22);
        AddSpoke(rBin, rBout, _innerOrbit, phaseDeg: 256, widthDeg: 6.0, alpha: 0.26);
        // (3) Density clumps that ride the bright B ring.
        AddRingBlob(rB, _innerOrbit, phaseDeg: 60, rx: rB * 0.16, ry: rB * 0.05,
                    color: Lighten(paleB, 0.30), alpha: 0.22);
        AddRingBlob(rB, _innerOrbit, phaseDeg: 300, rx: rB * 0.12, ry: rB * 0.045,
                    color: Lighten(paleB, 0.22), alpha: 0.18);

        if (hasOuter)
        {
            double rAmid = (MapR(RAin) + MapR(RAout)) / 2;
            double rAin = MapR(RAin), rAout = MapR(RAout);
            // Outer A-ring shimmer (slower revolution => visible differential rate).
            AddShimmer(rAmid, _outerOrbit, paleB, phaseDeg: 0, intensity: 0.8, arcSpan: 0.26);
            AddShimmer(rAmid, _outerOrbit, paleB, phaseDeg: 190, intensity: 0.40, arcSpan: 0.18);
            AddSpoke(rAin, rAout, _outerOrbit, phaseDeg: 80, widthDeg: 6.0, alpha: 0.20);
            AddSpoke(rAin, rAout, _outerOrbit, phaseDeg: 210, widthDeg: 5.0, alpha: 0.16);
            AddRingBlob(rAmid, _outerOrbit, phaseDeg: 150, rx: rAmid * 0.13, ry: rAmid * 0.04,
                        color: Lighten(tanA, 0.30), alpha: 0.16);

            // --- Saturn's shepherd moons: five faint bright points embedded in
            // the rings, each at its real ring location, revolving with the
            // outer orbit so they sweep along the ring plane.
            double moonD = Math.Max(2.2, icon * 0.05);
            AddMoon(MapR(REncke), _outerOrbit, phaseDeg: 18, dia: moonD);                 // Pan (Encke gap)
            AddMoon(MapR(RAout) - icon * 0.05, _outerOrbit, phaseDeg: 104, dia: moonD * 0.85); // Daphnis (Keeler gap)
            AddMoon(MapR(RAout) + icon * 0.09, _outerOrbit, phaseDeg: 200, dia: moonD * 1.05); // Atlas (A ring edge)
            AddMoon(rF - icon * 0.10, _outerOrbit, phaseDeg: 286, dia: moonD * 1.15);     // Prometheus (inner F)
            AddMoon(rF + icon * 0.10, _outerOrbit, phaseDeg: 330, dia: moonD);            // Pandora (outer F)
        }


        _stackTiltY = 1.0;
        _ringLayer = null; // subsequent draws (icons, planet) stay on PanelCanvas
    }

    /// <summary>Sprinkles a faint, mostly-static starfield across the disc, with
    /// a few twinkling stars, so the planet reads as floating in space.</summary>
    private void DrawStarfield(double r)
    {
        const int count = 84;
        for (int i = 0; i < count; i++)
        {
            double ang = Hash01(i * 2.17) * Math.PI * 2;
            double rad = Math.Sqrt(Hash01(i * 5.31)) * r * 0.96;
            double px = _center.X + Math.Cos(ang) * rad;
            double py = _center.Y + Math.Sin(ang) * rad * RingTiltY;
            double sz = 0.6 + 1.9 * Hash01(i * 7.7);
            byte br = (byte)(60 + 150 * Hash01(i * 3.3));
            var star = new Ellipse
            {
                Width = sz,
                Height = sz,
                Fill = new SolidColorBrush(Color.FromArgb(br, 255, 255, 250)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(star, px - sz / 2);
            Canvas.SetTop(star, py - sz / 2);
            PanelCanvas.Children.Add(star);

            if (Hash01(i * 11.1) > 0.68)   // a subset twinkles
            {
                double full = br / 255.0;
                var tw = new DoubleAnimation(full * 0.3, full,
                    TimeSpan.FromSeconds(1.4 + 2.2 * Hash01(i * 4.9)))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromSeconds(2.0 * Hash01(i * 6.2)),
                };
                star.BeginAnimation(OpacityProperty, tw);
            }
        }
    }

    /// <summary>Adds a blurred elliptical halo (bloom) at the given mean radius.</summary>
    private void AddBloomRing(double rMid, double thickness, Color color, double alpha)
    {
        var glow = new Ellipse
        {
            Width = rMid * 2,
            Height = rMid * 2 * _stackTiltY,
            Stroke = new SolidColorBrush(WithAlpha(color, alpha)),
            StrokeThickness = Math.Max(2, thickness),
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.BlurEffect
            {
                Radius = Math.Max(8, thickness * 0.6),
            },
        };
        Canvas.SetLeft(glow, _center.X - rMid);
        Canvas.SetTop(glow, _center.Y - rMid * _stackTiltY);
        (_ringLayer ?? PanelCanvas).Children.Add(glow);
    }

    /// <summary>Adds a soft shimmer arc that sits on the ring at <paramref name="phaseDeg"/>
    /// and is revolved about the centre by <paramref name="orbit"/>; an outer ScaleY
    /// squashes its circular orbit into the tilted ellipse so it tracks the ring plane.
    /// A faint cool leading / warm trailing pair gives a subtle Doppler hint.</summary>
    private void AddShimmer(double radius, RotateTransform orbit, Color baseColor,
        double phaseDeg = 0, double intensity = 1.0, double arcSpan = 0.30)
    {
        orbit.CenterX = _center.X;
        orbit.CenterY = _center.Y;

        double rx = Math.Max(26, radius * arcSpan);
        double ry = Math.Max(5, radius * 0.06);

        // Warm trailing half (just behind the crest).
        AddRevolvedEllipse(radius, orbit, phaseDeg - 6, rx * 0.9, ry,
            WithAlpha(Lighten(WarmShift(baseColor), 0.45), 0.22 * intensity), null);
        // Cool leading half (just ahead of the crest).
        AddRevolvedEllipse(radius, orbit, phaseDeg + 6, rx * 0.9, ry,
            WithAlpha(Lighten(CoolShift(baseColor), 0.55), 0.25 * intensity), null);
        // Bright central crest on top.
        AddRevolvedEllipse(radius, orbit, phaseDeg, rx, ry,
            WithAlpha(Lighten(baseColor, 0.70), 0.42 * intensity), baseColor);
    }

    /// <summary>Creates a soft radial-gradient ellipse on the ring at the given phase,
    /// revolved by <paramref name="orbit"/> and tilted into the ring plane.</summary>
    private void AddRevolvedEllipse(double radius, RotateTransform orbit, double phaseDeg,
        double rx, double ry, Color coreColor, Color? fadeColor)
    {
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(coreColor, 0.0),
                new GradientStop(WithAlpha(fadeColor ?? coreColor, 0.0), 1.0),
            },
        };
        var glow = new System.Windows.Shapes.Path
        {
            IsHitTestVisible = false,
            Fill = brush,
            Data = new EllipseGeometry(new Point(_center.X + radius, _center.Y), rx, ry),
        };
        glow.RenderTransform = RingRevolveTransform(orbit, phaseDeg);
        (_ringLayer ?? PanelCanvas).Children.Add(glow);
    }

    /// <summary>Builds the transform that places a ring feature authored at angle 0,
    /// rotates it to <paramref name="phaseDeg"/>, revolves it by the animated
    /// <paramref name="orbit"/>, then squashes the orbit into the tilted ellipse.</summary>
    private TransformGroup RingRevolveTransform(RotateTransform orbit, double phaseDeg)
    {
        orbit.CenterX = _center.X;
        orbit.CenterY = _center.Y;
        var tg = new TransformGroup();
        if (phaseDeg != 0)
            tg.Children.Add(new RotateTransform(phaseDeg, _center.X, _center.Y)); // phase offset
        tg.Children.Add(orbit);                                                    // revolve
        tg.Children.Add(new ScaleTransform(1, _stackTiltY, _center.X, _center.Y)); // tilt
        return tg;
    }

    /// <summary>Adds a Voyager/Cassini-style radial spoke (a soft dark wedge spanning
    /// <paramref name="rInner"/>..<paramref name="rOuter"/>) that revolves with the ring,
    /// giving the otherwise featureless band a trackable rotating mark.</summary>
    private void AddSpoke(double rInner, double rOuter, RotateTransform orbit,
        double phaseDeg, double widthDeg, double alpha)
    {
        if (rOuter <= rInner)
            return;

        double half = widthDeg * Math.PI / 360.0;       // half angular width in rad
        Point P(double r, double a) =>
            new Point(_center.X + Math.Cos(a) * r, _center.Y + Math.Sin(a) * r);

        // Wedge is slightly wider at the outer edge, like real spokes.
        var fig = new PathFigure { StartPoint = P(rInner, -half * 0.7), IsClosed = true };
        fig.Segments.Add(new LineSegment(P(rOuter, -half), true));
        fig.Segments.Add(new LineSegment(P(rOuter, half), true));
        fig.Segments.Add(new LineSegment(P(rInner, half * 0.7), true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);

        var spoke = new System.Windows.Shapes.Path
        {
            IsHitTestVisible = false,
            Fill = new SolidColorBrush(WithAlpha(Color.FromRgb(0x14, 0x10, 0x08), alpha)),
            Data = geo,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 3.0 },
        };
        spoke.RenderTransform = RingRevolveTransform(orbit, phaseDeg);
        (_ringLayer ?? PanelCanvas).Children.Add(spoke);
    }

    /// <summary>Adds a tangentially-elongated brighter "density clump" that revolves
    /// with the ring, reading as a particle concentration sweeping past.</summary>
    private void AddRingBlob(double radius, RotateTransform orbit, double phaseDeg,
        double rx, double ry, Color color, double alpha)
    {
        AddRevolvedEllipse(radius, orbit, phaseDeg, rx, ry,
            WithAlpha(color, alpha), color);
    }

    /// <summary>Shifts a colour slightly toward cool (blue) for the leading edge.</summary>
    private static Color CoolShift(Color c) => Color.FromRgb(
        (byte)Math.Clamp(c.R - 14, 0, 255),
        (byte)Math.Clamp(c.G - 4, 0, 255),
        (byte)Math.Clamp(c.B + 18, 0, 255));

    /// <summary>Shifts a colour slightly toward warm (amber) for the trailing edge.</summary>
    private static Color WarmShift(Color c) => Color.FromRgb(
        (byte)Math.Clamp(c.R + 16, 0, 255),
        (byte)Math.Clamp(c.G + 4, 0, 255),
        (byte)Math.Clamp(c.B - 16, 0, 255));

    /// <summary>Adds a faint shepherd-moon point on the ring at <paramref name="radius"/>
    /// and <paramref name="phaseDeg"/>: a tiny bright core wrapped in a soft glow,
    /// revolved with <paramref name="orbit"/> and tilted into the ring plane.</summary>
    private void AddMoon(double radius, RotateTransform orbit, double phaseDeg, double dia)
    {
        var center = new Point(_center.X + radius, _center.Y);

        // Soft glow halo.
        var halo = new System.Windows.Shapes.Path
        {
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(95, 0xFF, 0xF6, 0xE2), 0.0),
                    new GradientStop(Color.FromArgb(0, 0xFF, 0xF6, 0xE2), 1.0),
                },
            },
            Data = new EllipseGeometry(center, dia * 1.9, dia * 1.9),
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 1.6 },
        };
        halo.RenderTransform = RingRevolveTransform(orbit, phaseDeg);
        (_ringLayer ?? PanelCanvas).Children.Add(halo);

        // Bright core.
        var core = new System.Windows.Shapes.Path
        {
            IsHitTestVisible = false,
            Fill = new SolidColorBrush(Color.FromArgb(200, 0xFF, 0xFB, 0xF0)),
            Data = new EllipseGeometry(center, dia * 0.5, dia * 0.5),
        };
        core.RenderTransform = RingRevolveTransform(orbit, phaseDeg);
        (_ringLayer ?? PanelCanvas).Children.Add(core);
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

            // Multi-frequency granular density. A broad low-frequency envelope
            // gives the band large-scale bright/dark structure, while medium and
            // fine sinusoids plus deterministic noise add an icy-particle speckle
            // so the rings no longer look like flat concentric strokes.
            double grain =
                  0.60
                + 0.22 * Math.Sin(rr * 0.018 + 1.3)   // broad brightness envelope
                + 0.12 * Math.Sin(rr * 0.071)         // medium undulation
                + 0.10 * Math.Sin(rr * 0.193 + 0.7)   // fine ripple
                + 0.12 * (Hash01(rr) - 0.5);          // high-frequency speckle
            grain = Math.Clamp(grain, 0.32, 1.12);
            double alpha = Math.Clamp((aInner + (aOuter - aInner) * t) * grain, 0, 1);
            double shadeT = Math.Clamp(0.5 + 0.5 * Math.Sin(rr * 0.5)
                                       + 0.18 * (Hash01(rr * 3.1) - 0.5), 0, 1);
            Color shade = LerpColor(Darken(color, 0.20), Lighten(color, 0.16), shadeT);

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

        // Sparse bright/dark speckle scattered through the zone to break up the
        // perfect concentric stroke pattern (icy-particle grain). Positions are
        // deterministic so the look is stable across rebuilds.
        int speckles = (int)Math.Clamp((rOuter - rInner) * 0.7, 0, 46);
        for (int i = 0; i < speckles; i++)
        {
            double rr = rInner + (rOuter - rInner) * Hash01(rInner * 7.1 + i * 2.3);
            double ang = Hash01(rOuter * 3.7 + i * 5.9) * Math.PI * 2;
            double br = Hash01(i * 1.7 + rInner);
            double px = _center.X + Math.Cos(ang) * rr;
            double py = _center.Y + Math.Sin(ang) * rr * _stackTiltY;
            byte sa = (byte)(34 + 120 * br);
            Color sc = br > 0.5 ? Lighten(color, 0.45) : Darken(color, 0.45);
            double sz = 0.8 + 1.9 * Hash01(i * 9.3 + rOuter);
            var dot = new Ellipse
            {
                Width = sz,
                Height = sz,
                Fill = new SolidColorBrush(Color.FromArgb(sa, sc.R, sc.G, sc.B)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(dot, px - sz / 2);
            Canvas.SetTop(dot, py - sz / 2);
            (_ringLayer ?? PanelCanvas).Children.Add(dot);
        }
    }

    /// <summary>Deterministic pseudo-random value in [0,1) from a scalar seed.</summary>
    private static double Hash01(double x)
    {
        double s = Math.Sin(x * 12.9898) * 43758.5453;
        return s - Math.Floor(s);
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
        double ry = r * _stackTiltY;
        // Foreshorten the concentric circle into an ellipse so the ring plane
        // reads as a tilted disc. Width stays r*2 (set by the caller); we only
        // squash the height and re-centre vertically.
        if (_stackTiltY != 1.0)
            el.Height = ry * 2;
        Canvas.SetLeft(el, _center.X - r);
        Canvas.SetTop(el, _center.Y - ry);
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
            BlurRadius = 12,
            ShadowDepth = 0,
            Opacity = 0.5,
        };

        Color amber = Color.FromRgb(0xE2, 0xBE, 0x82);
        Color amberDark = Color.FromRgb(0x7A, 0x5C, 0x36);
        Color amberLight = Color.FromRgb(0xFC, 0xEF, 0xCC);

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
        var discBlur = new System.Windows.Media.Effects.BlurEffect { Radius = 0, KernelType = System.Windows.Media.Effects.KernelType.Gaussian };
        var disc = new Canvas
        {
            Width = size,
            Height = size,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = discRotate,
            Effect = discBlur,             // motion blur, scaled to spin speed
        };

        // --- Gas-giant banding ------------------------------------------------
        // Real Saturn shows alternating light "zones" and darker "belts" whose
        // boundaries are turbulent and wavy — never clean circles. Each band is
        // a filled annulus whose inner/outer edges are radius-modulated by a sum
        // of sines plus deterministic noise, so the borders ripple like wind
        // shear. Bands are translucent so the globe's spherical shading shows
        // through. Drawn limb -> pole.
        const int bandCount = 11;
        double[] bandEdges = new double[bandCount + 1];
        for (int i = 0; i <= bandCount; i++)
            bandEdges[i] = r * (i / (double)bandCount);

        // Wavy radius of edge index at angle theta (gentle, low-amplitude
        // turbulence so the boundaries only barely ripple).
        double EdgeRadius(int edge, double theta)
        {
            double baseR = bandEdges[edge];
            double amp = size * 0.003 + size * 0.0015 * Hash01(edge * 3.3);
            double w = baseR
                + amp * Math.Sin(theta * 3 + edge * 1.7)
                + amp * 0.5 * Math.Sin(theta * 7 - edge * 2.3)
                + amp * 0.3 * Math.Sin(theta * 13 + edge * 0.9)
                + amp * 0.25 * (Hash01(edge * 5.1 + Math.Floor(theta * 6)) - 0.5);
            return Math.Clamp(w, 0, r);
        }

        const int beltSeg = 110;
        for (int b = bandCount - 1; b >= 0; b--)        // outer (limb) first
        {
            double s = 0.5 + 0.5 * Math.Sin(b * 1.45 + 0.6);   // zone/belt alternation
            Color shade = s < 0.5
                ? LerpColor(amberDark, amber, s * 2.0)
                : LerpColor(amber, amberLight, (s - 0.5) * 2.0);
            byte a = (byte)(70 + 28 * Math.Sin(b * 1.9));

            var fig = new PathFigure { IsClosed = true };
            for (int k = 0; k <= beltSeg; k++)          // outer edge (b+1)
            {
                double th = k / (double)beltSeg * Math.PI * 2;
                double rr = EdgeRadius(b + 1, th);
                var p = new Point(r + Math.Cos(th) * rr, r + Math.Sin(th) * rr);
                if (k == 0) fig.StartPoint = p;
                else fig.Segments.Add(new LineSegment(p, false));
            }
            for (int k = beltSeg; k >= 0; k--)          // inner edge (b), reversed
            {
                double th = k / (double)beltSeg * Math.PI * 2;
                double rr = EdgeRadius(b, th);
                fig.Segments.Add(new LineSegment(
                    new Point(r + Math.Cos(th) * rr, r + Math.Sin(th) * rr), false));
            }
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            var band = new System.Windows.Shapes.Path
            {
                Fill = new SolidColorBrush(Color.FromArgb(a, shade.R, shade.G, shade.B)),
                Data = geo,
                IsHitTestVisible = false,
            };
            disc.Children.Add(band);
        }

        // Fine zonal wind streaks: thin arcs following the azimuthal flow with a
        // small radial wiggle so they read as turbulent jets, not clean lines.
        const int windStreaks = 30;
        for (int sN = 0; sN < windStreaks; sN++)
        {
            double rad0 = r * (0.14 + 0.82 * Hash01(sN * 1.7 + 0.3));
            double a0 = Hash01(sN * 2.9) * Math.PI * 2;
            double arc = 0.5 + 1.9 * Hash01(sN * 4.1);             // radians spanned
            bool light = Hash01(sN * 3.7) > 0.5;
            Color sc = light ? Lighten(amber, 0.30) : Darken(amber, 0.30);
            byte sa = (byte)(24 + 38 * Hash01(sN * 6.1));
            double amp = size * (0.004 + 0.011 * Hash01(sN * 5.3));

            var fig = new PathFigure { IsClosed = false };
            const int ss = 44;
            for (int k = 0; k <= ss; k++)
            {
                double th = a0 + arc * (k / (double)ss);
                double rr = rad0 + amp * Math.Sin(th * 9 + sN) + amp * 0.5 * Math.Sin(th * 17 - sN);
                var p = new Point(r + Math.Cos(th) * rr, r + Math.Sin(th) * rr);
                if (k == 0) fig.StartPoint = p;
                else fig.Segments.Add(new LineSegment(p, true));
            }
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            var streak = new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush(Color.FromArgb(sa, sc.R, sc.G, sc.B)),
                StrokeThickness = Math.Max(1.0, size * (0.005 + 0.009 * Hash01(sN * 7.7))),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = geo,
                IsHitTestVisible = false,
            };
            disc.Children.Add(streak);
        }


        // Saturn's north-polar hexagon at the centre — a hallmark of the
        // top-down view, and a clear visual anchor for the spin. The interior
        // carries the same gas-giant turbulence (wavy bands + wind streaks) as
        // the globe, clipped to the hexagon outline.
        double hexR = r * 0.16;
        var hexGeo = new PathGeometry();
        var hexFig = new PathFigure { IsClosed = true };
        for (int k = 0; k < 6; k++)
        {
            double ang = -Math.PI / 2 + k * Math.PI / 3;
            var p = new Point(r + Math.Cos(ang) * hexR, r + Math.Sin(ang) * hexR);
            if (k == 0) hexFig.StartPoint = p;
            else hexFig.Segments.Add(new LineSegment(p, true));
        }
        hexGeo.Figures.Add(hexFig);

        // Base hexagon: a brighter blue-grey storm so it stands out from the
        // amber bands without being a dark hole.
        Color hexBase = Color.FromRgb(0x66, 0x6E, 0x72);
        Color hexLight = Color.FromRgb(0x93, 0x9A, 0x98);
        Color hexDark = Color.FromRgb(0x45, 0x49, 0x4C);
        var hex = new System.Windows.Shapes.Path
        {
            Stroke = new SolidColorBrush(Color.FromArgb(180, 0xAE, 0xB2, 0xA8)),
            StrokeThickness = Math.Max(1.0, size * 0.012),
            Fill = new SolidColorBrush(hexBase),
            Data = hexGeo,
            IsHitTestVisible = false,
        };
        disc.Children.Add(hex);

        // Turbulence inside the hexagon, clipped to its outline.
        var hexInner = new Canvas { IsHitTestVisible = false, Clip = hexGeo };

        // Wavy concentric bands within the polar storm.
        double HexEdge(double baseR, double theta, double seed)
        {
            double amp = hexR * 0.10;
            return Math.Clamp(baseR
                + amp * Math.Sin(theta * 3 + seed * 1.7)
                + amp * 0.6 * Math.Sin(theta * 6 - seed * 2.1)
                + amp * 0.4 * (Hash01(seed * 5.1 + Math.Floor(theta * 5)) - 0.5), 0, hexR);
        }
        const int hexBands = 4;
        for (int b = hexBands; b >= 1; b--)
        {
            double baseOut = hexR * (b / (double)hexBands);
            double baseIn = hexR * ((b - 1) / (double)hexBands);
            double s = 0.5 + 0.5 * Math.Sin(b * 1.7);
            Color shade = LerpColor(hexDark, hexLight, s);
            byte a = (byte)(120 + 70 * Math.Sin(b * 1.3));
            var fig = new PathFigure { IsClosed = true };
            const int seg = 70;
            for (int k = 0; k <= seg; k++)
            {
                double th = k / (double)seg * Math.PI * 2;
                double rr = HexEdge(baseOut, th, b + 1);
                var p = new Point(r + Math.Cos(th) * rr, r + Math.Sin(th) * rr);
                if (k == 0) fig.StartPoint = p;
                else fig.Segments.Add(new LineSegment(p, false));
            }
            for (int k = seg; k >= 0; k--)
            {
                double th = k / (double)seg * Math.PI * 2;
                double rr = HexEdge(baseIn, th, b);
                fig.Segments.Add(new LineSegment(
                    new Point(r + Math.Cos(th) * rr, r + Math.Sin(th) * rr), false));
            }
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            hexInner.Children.Add(new System.Windows.Shapes.Path
            {
                Fill = new SolidColorBrush(Color.FromArgb(a, shade.R, shade.G, shade.B)),
                Data = geo,
                IsHitTestVisible = false,
            });
        }

        // Wind streaks swirling inside the polar storm.
        for (int sN = 0; sN < 10; sN++)
        {
            double rad0 = hexR * (0.18 + 0.78 * Hash01(sN * 1.7 + 9.1));
            double a0 = Hash01(sN * 2.9 + 3.3) * Math.PI * 2;
            double arc = 0.6 + 2.2 * Hash01(sN * 4.1 + 1.2);
            bool light = Hash01(sN * 3.7 + 2.0) > 0.5;
            Color sc = light ? hexLight : hexDark;
            byte sa = (byte)(40 + 50 * Hash01(sN * 6.1));
            double amp = hexR * (0.04 + 0.10 * Hash01(sN * 5.3));
            var fig = new PathFigure { IsClosed = false };
            const int ss = 40;
            for (int k = 0; k <= ss; k++)
            {
                double th = a0 + arc * (k / (double)ss);
                double rr = rad0 + amp * Math.Sin(th * 7 + sN) + amp * 0.5 * Math.Sin(th * 13 - sN);
                var p = new Point(r + Math.Cos(th) * rr, r + Math.Sin(th) * rr);
                if (k == 0) fig.StartPoint = p;
                else fig.Segments.Add(new LineSegment(p, true));
            }
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            hexInner.Children.Add(new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush(Color.FromArgb(sa, sc.R, sc.G, sc.B)),
                StrokeThickness = Math.Max(1.0, hexR * 0.05),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = geo,
                IsHitTestVisible = false,
            });
        }
        disc.Children.Add(hexInner);


        globe.Child = disc;
        root.Children.Add(globe);

        // Limb darkening: a soft ring of shadow hugging the very edge so the
        // sphere reads as rounded and three-dimensional rather than a flat disc.
        var limb = new Ellipse
        {
            Width = size,
            Height = size,
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.0),
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.74),
                    new GradientStop(Color.FromArgb(110, 0x16, 0x0D, 0x04), 0.92),
                    new GradientStop(Color.FromArgb(235, 0x0C, 0x07, 0x02), 1.0),
                },
            },
        };
        root.Children.Add(limb);

        // --- Matte sheen: a single soft, very faint light veil over the sphere
        // gives a slightly matte (non-glossy) finish without the grainy frosted
        // micro-dots. Clipped to the globe circle.
        var matte = new Ellipse
        {
            Width = size,
            Height = size,
            IsHitTestVisible = false,
            Clip = new EllipseGeometry(new Point(r, r), r, r),
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.42, 0.40),
                Center = new Point(0.42, 0.40),
                RadiusX = 0.75,
                RadiusY = 0.75,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(20, 0xFF, 0xF4, 0xDA), 0.0),
                    new GradientStop(Color.FromArgb(10, 0xFF, 0xF4, 0xDA), 0.45),
                    new GradientStop(Color.FromArgb(0, 0xFF, 0xF4, 0xDA), 1.0),
                },
            },
        };
        root.Children.Add(matte);


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

        // Thin dark rim that blends into the limb darkening (a bright rim here
        // would otherwise read as a glowing ring just outside the dark edge).
        var rim = new Ellipse
        {
            Width = size,
            Height = size,
            IsHitTestVisible = false,
            Stroke = new SolidColorBrush(Color.FromArgb(90, 0x12, 0x0A, 0x03)),
            StrokeThickness = 1.0,
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

            // Faster spin -> stronger motion blur. Below a small threshold the
            // blur is dropped entirely (Effect = null) so the perpetually
            // spinning disc isn't re-rasterised through the blur pipeline every
            // frame -- that constant per-frame cost is what made the panel feel
            // progressively "slow-motion" the longer it stayed open.
            double targetBlur = Math.Clamp(8.0 / secondsPerTurn, 0.0, 2.2);
            if (targetBlur < 0.4)
            {
                discBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, null);
                discBlur.Radius = 0;
                disc.Effect = null;
            }
            else
            {
                disc.Effect = discBlur;
                var blurAnim = new DoubleAnimation(targetBlur, TimeSpan.FromMilliseconds(380))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                discBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, blurAnim);
            }
        }

        StartSpin(PlanetSpinSeconds);            // gentle idle self-rotation
        root.MouseEnter += (_, _) => StartSpin(3.0);
        root.MouseLeave += (_, _) => StartSpin(PlanetSpinSeconds);

        root.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            RequestOpenSettings?.Invoke();
        };
        // Warm bloom halo behind the planet. Drawn as a separate static element
        // (not affected by the spinning disc) so its blur is cached once.
        double halo = size * 1.4;
        var bloom = new Ellipse
        {
            Width = halo,
            Height = halo,
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(120, 0xF2, 0xD4, 0x96), 0.0),
                    new GradientStop(Color.FromArgb(64, 0xDA, 0xB2, 0x72), 0.46),
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 1.0),
                },
            },
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 24 },
        };
        Panel.SetZIndex(bloom, 1999); // just under the planet (root is 2000)
        Canvas.SetLeft(bloom, _center.X - halo / 2);
        Canvas.SetTop(bloom, _center.Y - halo / 2);
        PanelCanvas.Children.Add(bloom);

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
        // Only the Saturn ring layout supports live reorder; the grid test theme
        // keeps drag-to-launch and drag-out-to-delete but no live reflow.
        if (_theme.IsSaturn && dist <= DeleteRadius)
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
                         _center.Y + radius * Math.Sin(angle) * RingTiltY);
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
        Panel.SetZIndex(ic, 3000); // above Saturn (root=2000) so the label isn't hidden
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
        var la = new DoubleAnimation(left, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease };
        var ta = new DoubleAnimation(top, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease };
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
        else if (_theme.IsSaturn)
        {
            CommitArrangement(icon.Entry, ring, pos, p);
        }
        else
        {
            // Grid test theme: no reorder — snap the dragged icon back.
            Rebuild();
        }
    }

    /// <summary>Distance past the outer ring beyond which a dropped icon is deleted.</summary>
    private double DeleteRadius => _theme.IsSaturn
        ? InnerRadius + RingStep + _config.Settings.IconSize * 1.25
        : _outerRadius + _config.Settings.IconSize * 0.8;

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
