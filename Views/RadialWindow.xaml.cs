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

    private readonly AppConfig _config;
    private readonly Action _persist;
    private readonly Dictionary<string, BitmapSource?> _iconCache = new();

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
        SizeChanged += (_, _) => Rebuild();
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
        SizeToPrimaryScreen();
        Rebuild();
        Opacity = 0;
        Show();
        Activate();
        Topmost = true;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// Shows the panel in pinned mode (stays open until explicitly closed),
    /// so the user can drag desktop shortcuts onto the ring.
    /// </summary>
    public void ShowPinned()
    {
        SizeToPrimaryScreen();
        Rebuild();
        Opacity = 0;
        Show();
        Activate();
        Topmost = true;
        IsPinned = true;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));
        BeginAnimation(OpacityProperty, fade);
    }

    public void HidePanel()
    {
        IsPinned = false;
        CancelDrag();
        BeginAnimation(OpacityProperty, null);
        Hide();
    }

    /// <summary>Hides the panel only if it is not pinned (used on key-release).</summary>
    public void HideIfNotPinned()
    {
        if (!IsPinned)
            HidePanel();
    }

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

        DrawBackingDisc();

        for (int i = 0; i < _config.Apps.Count; i++)
        {
            var entry = _config.Apps[i];
            var icon = CreateIcon(entry);
            PlaceCentered(icon, _slotPositions[i]);
            PanelCanvas.Children.Add(icon);
            _iconElements.Add(icon);
        }

        DrawCenterButton();
    }

    private RadialIcon CreateIcon(AppEntry entry)
    {
        if (!_iconCache.TryGetValue(entry.EffectiveIconSource, out var bmp))
        {
            bmp = IconExtractor.GetIcon(entry.EffectiveIconSource);
            _iconCache[entry.EffectiveIconSource] = bmp;
        }

        var icon = new RadialIcon(entry, bmp, _config.Settings.IconSize, AccentColor, LabelBrush);
        icon.PreviewMouseLeftButtonDown += Icon_PreviewMouseLeftButtonDown;
        icon.HoverStarted += OnIconHoverStarted;
        icon.HoverEnded += OnIconHoverEnded;
        return icon;
    }

    private void DrawBackingDisc()
    {
        double r = _outerRadius + _config.Settings.IconSize;
        double d = r * 2;
        Color baseColor = ParseColor(_config.Settings.PanelColor, Color.FromRgb(0x1E, 0x1E, 0x1E));
        Color accent = AccentColor;
        double op = _config.Settings.PanelOpacity;

        // --- Liquid-glass disc: translucent tinted base + glossy top highlight
        //     + soft inner glow + bright rim. Layered ellipses stacked at center.

        // 0) Hit-test layer. The window background is null so empty regions let
        //    mouse clicks fall through to the desktop (so you can click desktop
        //    icons or grab a shortcut to drag in). This transparent-but-hittable
        //    disc makes only the disc area interactive / a valid drop target.
        var hit = new Ellipse
        {
            Width = d,
            Height = d,
            Fill = Brushes.Transparent,
        };
        StackCentered(hit, r);

        // 1) Frosted translucent body — radial gradient from a lighter center to
        //    the base tint at the edge, kept semi-transparent so the desktop
        //    shows through like real glass.
        var body = new Ellipse
        {
            Width = d,
            Height = d,
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.42),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.6,
                RadiusY = 0.6,
                GradientStops =
                {
                    new GradientStop(WithAlpha(Lighten(baseColor, 0.22), op * 0.82), 0.0),
                    new GradientStop(WithAlpha(baseColor, op * 0.78), 0.72),
                    new GradientStop(WithAlpha(Darken(baseColor, 0.18), op * 0.92), 1.0),
                },
            },
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 0.6 },
            IsHitTestVisible = false,
        };
        StackCentered(body, r);

        // 2) Accent inner glow ring near the edge for a colored liquid sheen.
        var glow = new Ellipse
        {
            Width = d,
            Height = d,
            Fill = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 0.78),
                    new GradientStop(Color.FromArgb(70, accent.R, accent.G, accent.B), 0.96),
                    new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1.0),
                },
            },
            IsHitTestVisible = false,
        };
        StackCentered(glow, r);

        // 3) Glossy top highlight — an off-center white sheen, like light on glass.
        var gloss = new Ellipse
        {
            Width = d,
            Height = d,
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.12),
                Center = new Point(0.5, 0.0),
                RadiusX = 0.75,
                RadiusY = 0.55,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(90, 255, 255, 255), 0.0),
                    new GradientStop(Color.FromArgb(28, 255, 255, 255), 0.30),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.55),
                },
            },
            IsHitTestVisible = false,
        };
        StackCentered(gloss, r);

        // 4) Bright rim stroke for the crisp glass edge.
        var rim = new Ellipse
        {
            Width = d,
            Height = d,
            Stroke = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(150, 255, 255, 255), 0.0),
                    new GradientStop(Color.FromArgb(40, 255, 255, 255), 0.5),
                    new GradientStop(Color.FromArgb(110, accent.R, accent.G, accent.B), 1.0),
                },
            },
            StrokeThickness = 1.4,
            IsHitTestVisible = false,
        };
        StackCentered(rim, r);
    }

    private void StackCentered(FrameworkElement el, double r)
    {
        Canvas.SetLeft(el, _center.X - r);
        Canvas.SetTop(el, _center.Y - r);
        PanelCanvas.Children.Add(el);
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
        double size = _config.Settings.IconSize * 1.4;
        double r = size / 2;

        // Skeuomorphic metal gear built from vector layers, hosted in a Grid so
        // the whole thing can scale/rotate around its centre.
        var root = new Grid
        {
            Width = size,
            Height = size,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent, // keep the full square hit-testable
            ToolTip = "Settings",
            RenderTransformOrigin = new Point(0.5, 0.5),
        };

        // Soft drop shadow under the whole gear for depth.
        root.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 14,
            ShadowDepth = 2,
            Opacity = 0.45,
        };

        // --- Rotating gear group (teeth + body) -----------------------------
        var gearRotate = new RotateTransform(0);
        var gear = new Grid
        {
            Width = size,
            Height = size,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = gearRotate,
        };

        Color metal = Color.FromRgb(0xAB, 0xB4, 0xC0);
        Color metalDark = Darken(metal, 0.45);
        Color metalLight = Lighten(metal, 0.55);

        // Gear teeth + disc as a single filled geometry.
        var gearShape = new System.Windows.Shapes.Path
        {
            Data = BuildGearGeometry(r, r, r * 0.96, r * 0.74, 9, 0.42),
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.4, 0.34),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.62,
                RadiusY = 0.62,
                GradientStops =
                {
                    new GradientStop(metalLight, 0.0),
                    new GradientStop(metal, 0.55),
                    new GradientStop(metalDark, 1.0),
                },
            },
            Stroke = new SolidColorBrush(Darken(metal, 0.6)),
            StrokeThickness = 1.0,
        };
        gear.Children.Add(gearShape);

        // Recessed hub ring (darker) for a machined look.
        double hubR = r * 0.5;
        var hub = new Ellipse
        {
            Width = hubR * 2,
            Height = hubR * 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
                GradientStops =
                {
                    new GradientStop(metalDark, 0.0),
                    new GradientStop(Darken(metal, 0.25), 0.72),
                    new GradientStop(metal, 1.0),
                },
            },
            Stroke = new SolidColorBrush(Lighten(metal, 0.3)),
            StrokeThickness = 1.0,
        };
        gear.Children.Add(hub);

        // Center bore.
        double boreR = r * 0.22;
        var bore = new Ellipse
        {
            Width = boreR * 2,
            Height = boreR * 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.35),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
                GradientStops =
                {
                    new GradientStop(Darken(metal, 0.7), 0.0),
                    new GradientStop(metalDark, 1.0),
                },
            },
        };
        gear.Children.Add(bore);

        root.Children.Add(gear);

        // --- Static glossy highlight (does NOT rotate) ----------------------
        var gloss = new Ellipse
        {
            Width = size * 0.9,
            Height = size * 0.9,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.1),
                Center = new Point(0.5, -0.05),
                RadiusX = 0.75,
                RadiusY = 0.75,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(150, 255, 255, 255), 0.0),
                    new GradientStop(Color.FromArgb(40, 255, 255, 255), 0.3),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.6),
                },
            },
        };
        root.Children.Add(gloss);

        // --- Hover: spin the gear continuously; settle when leaving ----------
        root.MouseEnter += (_, _) =>
        {
            var spin = new DoubleAnimation
            {
                By = 360,
                Duration = TimeSpan.FromSeconds(2.2),
                RepeatBehavior = RepeatBehavior.Forever,
            };
            gearRotate.BeginAnimation(RotateTransform.AngleProperty, spin);
        };
        root.MouseLeave += (_, _) =>
        {
            // Freeze at the current angle, then ease back smoothly to 0..360.
            double current = gearRotate.Angle % 360;
            gearRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            gearRotate.Angle = current;
            var settle = new DoubleAnimation(current, current + (360 - current % 360),
                TimeSpan.FromMilliseconds(600))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            gearRotate.BeginAnimation(RotateTransform.AngleProperty, settle);
        };

        root.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            RequestOpenSettings?.Invoke();
        };
        Canvas.SetLeft(root, _center.X - size / 2);
        Canvas.SetTop(root, _center.Y - size / 2);
        PanelCanvas.Children.Add(root);
    }

    /// <summary>
    /// Builds a gear outline: <paramref name="teeth"/> trapezoidal teeth around a
    /// disc. <paramref name="outerR"/> is the tooth tip radius, <paramref name="rootR"/>
    /// the valley radius, <paramref name="toothFraction"/> the tip width as a
    /// fraction of one tooth pitch.
    /// </summary>
    private static Geometry BuildGearGeometry(double cx, double cy, double outerR,
        double rootR, int teeth, double toothFraction)
    {
        var fig = new PathFigure { IsClosed = true, IsFilled = true };
        double step = 2 * Math.PI / teeth;
        double half = step / 2.0;
        double tipHalf = half * toothFraction;
        double flank = half * 0.18; // slight slope on tooth flanks

        Point P(double radius, double ang) =>
            new(cx + radius * Math.Cos(ang), cy + radius * Math.Sin(ang));

        bool first = true;
        for (int i = 0; i < teeth; i++)
        {
            double c = -Math.PI / 2 + i * step; // tooth centre angle (start at top)

            Point valleyStart = P(rootR, c - half + flank);
            Point tipStart = P(outerR, c - tipHalf);
            Point tipEnd = P(outerR, c + tipHalf);
            Point valleyEnd = P(rootR, c + half - flank);

            if (first)
            {
                fig.StartPoint = valleyStart;
                first = false;
            }
            else
            {
                fig.Segments.Add(new LineSegment(valleyStart, true));
            }
            fig.Segments.Add(new LineSegment(tipStart, true));
            fig.Segments.Add(new LineSegment(tipEnd, true));
            fig.Segments.Add(new LineSegment(valleyEnd, true));
        }

        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return geo;
    }

    private void PlaceCentered(FrameworkElement el, Point center)
    {
        double s = _config.Settings.IconSize;
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
        int o0 = (src < r0) ? r0 - 1 : r0;
        int m = n - 1;

        var others = new List<int>(m);
        for (int i = 0; i < n; i++)
            if (i != src)
                others.Add(i);

        int insertAt;
        int newR0;
        if (ring == 0)
        {
            insertAt = Math.Clamp(pos, 0, o0);
            newR0 = o0 + 1;
        }
        else
        {
            int ring1Len = m - o0;
            insertAt = o0 + Math.Clamp(pos, 0, ring1Len);
            newR0 = o0;
        }

        var newOrder = new List<int>(n);
        newOrder.AddRange(others.GetRange(0, insertAt));
        newOrder.Add(src);
        newOrder.AddRange(others.GetRange(insertAt, m - insertAt));

        int[] slotOfEntry = new int[n];
        for (int slot = 0; slot < n; slot++)
            slotOfEntry[newOrder[slot]] = slot;
        return (slotOfEntry, newR0);
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
        double s = _config.Settings.IconSize;
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
