using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Polaris.Services;

namespace Polaris.Views;

// Non-Saturn theme rendering: the translucent "liquid glass" panel and the
// minimal centre button used by plain grid themes.
public partial class RadialWindow
{
    /// <summary>A minimal centre control used by non-Saturn themes: a small
    /// circular settings button placed above the grid.</summary>
    private void DrawSimpleCenterButton()
    {
        double s = Math.Max(40, EffectiveIconSize * 0.82);
        var btn = new Border
        {
            Width = s,
            Height = s,
            CornerRadius = new CornerRadius(s / 2),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x2A, 0x6C, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1.6),
            Cursor = System.Windows.Input.Cursors.Hand,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.55,
                Color = Color.FromRgb(0x0A, 0x10, 0x1C),
            },
            Child = new TextBlock
            {
                Text = "⚙",
                FontSize = s * 0.54,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        btn.MouseLeftButtonUp += (_, e) => { e.Handled = true; RequestOpenSettings?.Invoke(); };

        double gridTop = _center.Y - 2 * (EffectiveIconSize * 2.1);
        Canvas.SetLeft(btn, _center.X - s / 2);
        Canvas.SetTop(btn, gridTop - s - EffectiveIconSize * 0.6);
        Panel.SetZIndex(btn, 2000);
        PanelCanvas.Children.Add(btn);
    }

    /// <summary>
    /// Draws the "液态玻璃" backdrop: a translucent, frosted rounded-rectangle
    /// panel sized to enclose the 7-column icon grid. The slab body is fixed to
    /// the 4 visible rows (surplus rows scroll inside it) and the whole dock is
    /// docked to the bottom of the screen. Its overall opacity follows the user's
    /// panel-transparency setting.
    /// </summary>
    private void DrawGlassPanel()
    {
        double icon = EffectiveIconSize;
        double cellW = icon * LiquidGlassTheme.ColumnPitch;
        double gridW = (LiquidGlassTheme.Columns - 1) * cellW;

        double padX = icon * 1.15;
        // Extra band reserved at the very top of the slab for the clock / gear.
        // The icon grid is anchored to GlassDockCenter (the visible 4-row block);
        // the slab keeps a constant height so the dock stays bottom-docked.
        double topInset = icon * 0.55;
        double w = gridW + icon + padX * 2;
        double h = GlassDockBodyHeight;                 // fixed 4-row body height
        double left = _center.X - w / 2.0;
        double gridTop = GlassDockCenter.Y - h / 2.0;   // panel top around the visible grid
        double top = gridTop - topInset;                // actual slab top (includes clock band)

        double opacity = 1.0 - Math.Clamp(_config.Settings.PanelTransparency, 0.0, 1.0);
        double frostStrength = GlassChrome.FrostStrengthFor(_config.Settings.PanelTransparency);
        const double radius = 28;

        // One continuous clear-glass panel that reaches the bottom screen edge.
        // The slab extends below the icon grid by GlassBottomReserve so the glass
        // covers down past the system taskbar while the lowest icon row stays
        // above it. (The running-app taskbar strip + its seam were removed.)
        double totalH = h + topInset + GlassBottomReserve;
        DrawGlassSlab(left, top, w, totalH, radius, opacity, frosted: false, frostStrength: frostStrength);

        // A pronounced "stereoscopic" rim around the whole slab (mirrors the
        // resident-region border) so the dock stays clearly visible even on a
        // plain white desktop. A soft cool glow plus a crisp dark+bright double
        // rim reads as a raised glass edge.
        var slabGlow = new System.Windows.Shapes.Rectangle
        {
            Width = w,
            Height = totalH,
            RadiusX = radius,
            RadiusY = radius,
            IsHitTestVisible = false,
            Stroke = new SolidColorBrush(Color.FromArgb(0x73, 0xBF, 0xE0, 0xFF)),
            StrokeThickness = 5.0,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 8 },
            // Bake the blurred glow stroke to a texture once: it is static, so
            // without a cache WPF keeps a live blur node that is re-rasterised
            // whenever the panel composites (e.g. during the rise scale).
            CacheMode = new System.Windows.Media.BitmapCache(),
        };
        var slabShade = new System.Windows.Shapes.Rectangle
        {
            Width = w,
            Height = totalH,
            RadiusX = radius,
            RadiusY = radius,
            IsHitTestVisible = false,
            Stroke = new SolidColorBrush(Color.FromArgb(0x80, 0x06, 0x0B, 0x16)),
            StrokeThickness = 2.4,
        };
        var slabRim = new System.Windows.Shapes.Rectangle
        {
            Width = w,
            Height = totalH,
            RadiusX = radius,
            RadiusY = radius,
            IsHitTestVisible = false,
            Stroke = new SolidColorBrush(Color.FromArgb(0xE6, 0xEA, 0xF4, 0xFF)),
            StrokeThickness = 1.4,
        };
        foreach (var r in new[] { slabGlow, slabShade, slabRim })
        {
            Canvas.SetLeft(r, left);
            Canvas.SetTop(r, top);
            Panel.SetZIndex(r, 1);
            PanelCanvas.Children.Add(r);
        }

        // Cool light source orbiting the dock centre, lighting the glass from a
        // slowly drifting direction (one revolution per minute). This is an
        // always-on animated layer; skip it in low-performance mode where every
        // tick re-composites the layered window, the effect being subtle enough
        // (peak alpha ~20%) that its absence is near-imperceptible.
        if (_config.Settings.PerformanceMode == Models.PerformanceMode.High)
            BuildGlassOrbitLight(left, top, w, totalH, radius);

        // Settings gear in the panel's top-right corner.
        double gs = Math.Max(40, icon * 0.72);
        var gear = new Border
        {
            Width = gs,
            Height = gs,
            CornerRadius = new CornerRadius(gs / 2),
            // Frosted-glass bead: a translucent milky-white pane (no black fill)
            // so the gear reads as a piece of the same liquid glass as the dock.
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x42, 0xEA, 0xF2, 0xFF), 0.5),
                    new GradientStop(Color.FromArgb(0x5C, 0xD6, 0xE4, 0xF6), 1.0),
                },
            },
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xEA, 0xF4, 0xFF)),
            BorderThickness = new Thickness(2.0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 3,
                Direction = 270,
                Opacity = 0.7,
                Color = Color.FromRgb(0x00, 0x00, 0x00),
            },
            Child = new TextBlock
            {
                Text = "⚙",
                FontSize = gs * 0.58,
                FontWeight = FontWeights.SemiBold,
                // Frosted-glass gear: a soft translucent cool-white glyph (no
                // metal) so it reads as the same frosted glass as the bead.
                Foreground = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF), 0.0),
                        new GradientStop(Color.FromArgb(0xE2, 0xEA, 0xF2, 0xFF), 0.55),
                        new GradientStop(Color.FromArgb(0xD2, 0xCF, 0xDF, 0xF0), 1.0),
                    },
                },
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                // Soft, even shade (no metallic bevel) so the frosted glyph keeps
                // a gentle outline on the light bead.
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 3,
                    ShadowDepth = 0,
                    Opacity = 0.32,
                    Color = Color.FromRgb(0x2A, 0x33, 0x40),
                },
            },
            // Bake the bead + blurred drop-shadow to a texture once: the gear is
            // static apart from a RenderTransform spin/press, and a transform does
            // not invalidate a BitmapCache, so the (expensive) blur is rasterised a
            // single time instead of on every layered-window recomposite driven by
            // the always-on orbit light / magnify wave. The shadow already rotates
            // with the element (the RenderTransform maps the whole visual including
            // its effect), so caching is pixel-identical.
            CacheMode = new System.Windows.Media.BitmapCache(Math.Max(1.0, DeviceScale))
            {
                SnapsToDevicePixels = true,
            },
        };
        // Spin the gear while hovered and shrink it on press like a real
        // physical button.
        var gearRotate = new RotateTransform(0);
        var gearScale = new ScaleTransform(1, 1);
        var gearXf = new TransformGroup();
        gearXf.Children.Add(gearScale);
        gearXf.Children.Add(gearRotate);
        gear.RenderTransform = gearXf;
        gear.RenderTransformOrigin = new Point(0.5, 0.5);
        gear.MouseEnter += (_, _) =>
        {
            var spin = new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.7))
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(spin, App.AnimationFrameRate);
            gearRotate.BeginAnimation(RotateTransform.AngleProperty, spin);
        };
        gear.MouseLeave += (_, _) =>
        {
            double cur = gearRotate.Angle % 360.0;
            gearRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            gearRotate.Angle = cur;
            var settle = new System.Windows.Media.Animation.DoubleAnimation(cur, 0, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = EasingMode.EaseOut },
            };
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(settle, App.AnimationFrameRate);
            gearRotate.BeginAnimation(RotateTransform.AngleProperty, settle);
        };
        gear.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            var press = new System.Windows.Media.Animation.DoubleAnimation(0.8, TimeSpan.FromMilliseconds(90))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = EasingMode.EaseOut },
            };
            gearScale.BeginAnimation(ScaleTransform.ScaleXProperty, press);
            gearScale.BeginAnimation(ScaleTransform.ScaleYProperty, press.Clone());
        };
        gear.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var release = new System.Windows.Media.Animation.DoubleAnimation(1.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new System.Windows.Media.Animation.BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 },
            };
            gearScale.BeginAnimation(ScaleTransform.ScaleXProperty, release);
            gearScale.BeginAnimation(ScaleTransform.ScaleYProperty, release.Clone());
            RequestOpenSettings?.Invoke();
        };
        Canvas.SetLeft(gear, left + w - gs - 14);
        Canvas.SetTop(gear, top + 6);
        Panel.SetZIndex(gear, 2000);
        PanelCanvas.Children.Add(gear);

        // System date / time in the panel's top-left (the "settings row"),
        // year-month-day and time all on one line.
        var clockTime = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Semibold, Microsoft YaHei UI, Segoe UI"),
            FontSize = Math.Max(18, icon * 0.36) * Polaris.Services.FontScale.Current,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            // Frosted-glass glyphs: a soft, milky translucent fill (semi-opaque
            // white easing to a cooler, slightly more transparent tint) rather
            // than a flat opaque white, so the text reads like ground glass.
            Foreground = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0xD2, 0xF2, 0xF6, 0xFF), 0.5),
                    new GradientStop(Color.FromArgb(0xBE, 0xD8, 0xE2, 0xF2), 1.0),
                },
            },
            // A dark blurred halo (no offset) wraps the light glyphs so they stay
            // legible even when the glass is highly transparent over a white
            // desktop.
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 10,
                ShadowDepth = 0,
                Opacity = 0.95,
                Color = Color.FromRgb(0x03, 0x06, 0x0E),
            },
            // Cache the glyphs + blurred halo to a texture so the always-on orbit
            // light / magnify wave don't re-rasterise the blur on every composite.
            // The text changes only once per second (UpdateGlassClock), which simply
            // rebuilds the cache then; rendered at device scale to stay crisp.
            CacheMode = new System.Windows.Media.BitmapCache(Math.Max(1.0, DeviceScale))
            {
                SnapsToDevicePixels = true,
            },
        };
        Canvas.SetLeft(clockTime, left + 18);
        Canvas.SetTop(clockTime, top + 14);
        Panel.SetZIndex(clockTime, 2000);
        PanelCanvas.Children.Add(clockTime);
        _glassClockTime = clockTime;
        _glassClockDate = null;
        UpdateGlassClock();
    }

    /// <summary>Draws a soft, glass-friendly rounded border around the resident
    /// region — the top two rows of the grid — so it reads as a distinct
    /// "always-pinned" zone that mirrors the left dock.</summary>
    private void DrawResidentRegionBorder(Canvas layer)
    {
        if (_config.Apps.Count == 0)
            return;

        double icon = EffectiveIconSize;
        double cellW = icon * LiquidGlassTheme.ColumnPitch;
        double cellH = icon * LiquidGlassTheme.RowPitch;
        double gridW = (LiquidGlassTheme.Columns - 1) * cellW;
        Point center = GlassGridCenter;
        double y0 = center.Y - (LiquidGlassTheme.VisibleRows - 1) * cellH / 2.0;  // row 0 centre

        double glyph = icon * GlassIconScale;

        // The resident region is user-customizable within the two-row cap, so
        // the frame wraps only the rows actually occupied by resident apps
        // (1 or 2 rows) rather than always spanning both.
        int resident = Math.Min(DockSync.ResidentCount(_config), _config.Apps.Count);
        int residentRows = Math.Clamp(
            (resident + LiquidGlassTheme.Columns - 1) / LiquidGlassTheme.Columns,
            1, 2);
        double lastRowY = y0 + (residentRows - 1) * cellH;   // last resident row centre

        double padY = icon * 0.56;
        double top = y0 - glyph / 2.0 - padY;
        double bottom = lastRowY + glyph / 2.0 + padY;

        // Centre the box on the icon GRID (which is nudged left to clear the
        // scrollbar) with symmetric horizontal padding so the icons sit centred
        // inside the frame.
        double padX = icon * 0.82;
        double half = gridW / 2.0 + glyph / 2.0 + padX;
        double left = center.X - half;
        double right = center.X + half;

        double w = right - left;
        double h = bottom - top;
        double radius = icon * 0.42;   // matches the left-dock tray corner radius

        // Frame fill at ~99% transparency (barely-there interior), but the edge
        // is still drawn so the resident region reads as a framed zone.
        var fill = new System.Windows.Shapes.Rectangle
        {
            Width = w,
            Height = h,
            RadiusX = radius,
            RadiusY = radius,
            IsHitTestVisible = false,
            Fill = new SolidColorBrush(Color.FromArgb(0x03, 0xFF, 0xFF, 0xFF)),
        };
        // A soft outer glow + a bright cool rim read as etched glass. Kept
        // thinner than the main-dock slab border so it reads as a lighter inner
        // frame.
        var glow = new System.Windows.Shapes.Rectangle
        {
            Width = w,
            Height = h,
            RadiusX = radius,
            RadiusY = radius,
            IsHitTestVisible = false,
            Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0xBF, 0xE0, 0xFF)),
            StrokeThickness = 2.0,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 4 },
            // Static blurred glow: bake it once so it isn't re-rasterised on every
            // layered-window recomposite (driven by the orbit light / magnify wave).
            CacheMode = new System.Windows.Media.BitmapCache(),
        };
        var rim = new System.Windows.Shapes.Rectangle
        {
            Width = w,
            Height = h,
            RadiusX = radius,
            RadiusY = radius,
            IsHitTestVisible = false,
            Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0xEA, 0xF4, 0xFF)),
            StrokeThickness = 1.0,
        };
        foreach (var r in new[] { fill, glow, rim })
        {
            Canvas.SetLeft(r, left);
            Canvas.SetTop(r, top);
            Panel.SetZIndex(r, -10);
            layer.Children.Add(r);
        }
    }

    /// <summary>Draws an Apple-style "Liquid Glass" slab (translucent body, edge
    /// rim, specular dome, glare and base shade) onto the main PanelCanvas. When
    /// <paramref name="track"/> is supplied, every created element is added to it
    /// so the caller can remove the slab later. Delegates to the shared
    /// <see cref="GlassChrome.DrawSlab"/> so the left dock matches exactly.</summary>
    internal void DrawGlassSlab(double left, double top, double w, double h, double radius, double opacity,
        System.Collections.Generic.List<FrameworkElement>? track = null, bool frosted = false,
        double frostStrength = 0.0)
    {
        GlassChrome.DrawSlab(PanelCanvas, left, top, w, h, radius, opacity, track, frosted, frostStrength: frostStrength);
    }

    /// <summary>
    /// Engraves a thin horizontal seam across the single glass panel at
    /// <paramref name="seamY"/>, dividing the dock from the taskbar strip without
    /// breaking the glass (a dark recess hairline plus a bright highlight below).
    /// </summary>
    private void DrawGlassSeam(double left, double seamY, double w, double opacity)
    {
        double inset = 16;
        double lineW = Math.Max(1, w - inset * 2);
        double lineLeft = left + inset;

        // Dark recess line.
        var groove = new System.Windows.Shapes.Rectangle
        {
            Width = lineW,
            Height = 1.5,
            Opacity = opacity,
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x00, 0x06, 0x0B, 0x16), 0.0),
                    new GradientStop(Color.FromArgb(0x99, 0x06, 0x0B, 0x16), 0.5),
                    new GradientStop(Color.FromArgb(0x00, 0x06, 0x0B, 0x16), 1.0),
                },
            },
        };
        Canvas.SetLeft(groove, lineLeft);
        Canvas.SetTop(groove, seamY - 0.75);
        Panel.SetZIndex(groove, -5);
        PanelCanvas.Children.Add(groove);

        // Bright highlight catching the lip just below the recess.
        var highlight = new System.Windows.Shapes.Rectangle
        {
            Width = lineW,
            Height = 1.5,
            Opacity = opacity,
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF), 0.5),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0),
                },
            },
        };
        Canvas.SetLeft(highlight, lineLeft);
        Canvas.SetTop(highlight, seamY + 0.75);
        Panel.SetZIndex(highlight, -5);
        PanelCanvas.Children.Add(highlight);
    }

    // ---- Grid scrolling (liquid-glass dock) ------------------------------

    // A real WPF ScrollBar control drives the grid scroll (native thumb drag,
    // page-on-track-click, accessibility) instead of a hand-drawn rectangle.
    private ScrollBar? _glassScrollBar;
    // Guards the two-way sync between _glassScroll and the ScrollBar's Value so
    // updating one does not recursively drive the other.
    private bool _syncingScrollBar;
    // Identifies the latest scroll animation so a stale Completed handler does
    // not tear down the GPU cache while a newer scroll is still running.
    private int _glassScrollAnimToken;
    // True while the "GlassScroll" profiling scene is pushed.
    private bool _glassScrollProfiled;

    /// <summary>Pushes the GlassScroll profiling scene once per scroll burst.</summary>
    private void BeginGlassScrollProfile()
    {
        if (_glassScrollProfiled)
            return;
        _glassScrollProfiled = true;
        FpsProfiler.Push("GlassScroll");
    }

    /// <summary>Pops the GlassScroll profiling scene if it was pushed.</summary>
    private void EndGlassScrollProfile()
    {
        if (!_glassScrollProfiled)
            return;
        _glassScrollProfiled = false;
        FpsProfiler.Pop("GlassScroll");
    }

    // Device pixel ratio of the window, used so the BitmapCache renders the
    // grid crisply at the monitor's DPI instead of at 1.0 logical scale.
    private double DeviceScale =>
        PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

    /// <summary>Toggles a GPU <see cref="BitmapCache"/> on the scroll layer. The
    /// icons carry per-element drop-shadow/blur effects; on an
    /// <c>AllowsTransparency</c> (layered) window those would be re-rasterised
    /// every frame while scrolling, killing the frame rate. Caching bakes the
    /// whole grid (effects included) into one texture so scrolling only moves a
    /// cached bitmap. The cache is removed when idle so hover-zoom stays crisp
    /// and cheap.</summary>
    private void EnableGlassScrollCache(bool on)
    {
        if (_glassScrollLayer == null)
            return;
        if (on)
        {
            if (_glassScrollLayer.CacheMode == null)
                _glassScrollLayer.CacheMode = new BitmapCache(DeviceScale)
                {
                    SnapsToDevicePixels = true,
                };
        }
        else
        {
            _glassScrollLayer.CacheMode = null;
        }
    }

    // White, rounded, button-less vertical scrollbar template (parsed once).
    private static ControlTemplate? _glassScrollBarTemplate;

    private static ControlTemplate GlassScrollBarTemplate =>
        _glassScrollBarTemplate ??= (ControlTemplate)XamlReader.Parse(@"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
                 xmlns:p='clr-namespace:System.Windows.Controls.Primitives;assembly=PresentationFramework'
                 TargetType='{x:Type p:ScrollBar}'>
  <Grid>
    <Border Background='#22FFFFFF' CornerRadius='5'/>
    <Track x:Name='PART_Track' IsDirectionReversed='True'>
      <Track.DecreaseRepeatButton>
        <RepeatButton Command='ScrollBar.PageUpCommand' Focusable='False' IsTabStop='False'>
          <RepeatButton.Template>
            <ControlTemplate TargetType='{x:Type RepeatButton}'>
              <Border Background='Transparent'/>
            </ControlTemplate>
          </RepeatButton.Template>
        </RepeatButton>
      </Track.DecreaseRepeatButton>
      <Track.IncreaseRepeatButton>
        <RepeatButton Command='ScrollBar.PageDownCommand' Focusable='False' IsTabStop='False'>
          <RepeatButton.Template>
            <ControlTemplate TargetType='{x:Type RepeatButton}'>
              <Border Background='Transparent'/>
            </ControlTemplate>
          </RepeatButton.Template>
        </RepeatButton>
      </Track.IncreaseRepeatButton>
      <Track.Thumb>
        <Thumb>
          <Thumb.Template>
            <ControlTemplate TargetType='{x:Type Thumb}'>
              <Border Background='#FFFFFFFF' CornerRadius='5'/>
            </ControlTemplate>
          </Thumb.Template>
        </Thumb>
      </Track.Thumb>
    </Track>
  </Grid>
</ControlTemplate>");

    /// <summary>Adds a real white vertical <see cref="ScrollBar"/> in the dock's
    /// right padding strip — clear of the icon columns — and wires its value to
    /// the grid scroll offset.</summary>
    private void DrawGlassScrollBar()
    {
        double icon = EffectiveIconSize;
        double cellW = icon * LiquidGlassTheme.ColumnPitch;
        double gridW = (LiquidGlassTheme.Columns - 1) * cellW;
        double barW = Math.Max(7, icon * 0.13);

        Rect vp = GlassGridViewport;
        double trackTop = vp.Top + icon * 0.5;
        double trackH = vp.Height - icon * 1.0;
        if (trackH <= 0)
            return;

        // Park the bar in the right padding band, beyond the last icon column
        // (column centre = _center.X + gridW/2; a glass tile is ~icon*0.66 wide),
        // so it never overlaps the icons even when one zooms on hover.
        double barCenterX = _center.X + gridW / 2.0 + icon * 1.30;
        double left = barCenterX - barW / 2.0;

        double viewport = LiquidGlassTheme.VisibleRows * GlassCellH;
        var bar = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            Width = barW,
            Height = trackH,
            Minimum = 0,
            Maximum = GlassScrollMax,
            ViewportSize = viewport,      // thumb size = visible / total fraction
            SmallChange = GlassCellH,
            LargeChange = viewport,
            Value = _glassScroll,
            Template = GlassScrollBarTemplate,
            Cursor = Cursors.Hand,
            Focusable = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 6,
                ShadowDepth = 0,
                Opacity = 0.45,
                Color = Color.FromRgb(0x06, 0x0B, 0x16),
            },
        };
        bar.Scroll += OnGlassScrollBarScroll;
        Canvas.SetLeft(bar, left);
        Canvas.SetTop(bar, trackTop);
        Panel.SetZIndex(bar, 2500);
        PanelCanvas.Children.Add(bar);
        _glassScrollBar = bar;
    }

    private void OnGlassScrollBarScroll(object sender, ScrollEventArgs e)
    {
        if (_syncingScrollBar)
            return;
        // The floating hover label is pinned to the screen; hide it while the
        // grid scrolls so it doesn't look detached from its icon.
        HideGlassHoverLabel();
        // Dragging the thumb / clicking the track should track the pointer with
        // no easing lag, so jump straight there (cancel any wheel animation).
        StopGlassScrollAnimation();
        BeginGlassScrollProfile();
        SetGlassScroll(e.NewValue);
        // Drop the cache once the drag / page action finishes so idle hover
        // zoom renders at full crispness.
        if (e.ScrollEventType == ScrollEventType.EndScroll)
        {
            EndGlassScrollProfile();
        }
    }

    /// <summary>Cancels any in-progress smooth-scroll animation, freezing the
    /// grid transform at its current visual offset.</summary>
    private void StopGlassScrollAnimation()
    {
        // Invalidate any pending Completed handler so it can't clear a cache we
        // may immediately re-enable for the next gesture.
        _glassScrollAnimToken++;
        if (_glassScrollTransform == null)
            return;
        // Capture the live animated value, then re-assert it as the base value so
        // the transform holds still after the animation handle is removed.
        double current = _glassScrollTransform.Y;
        _glassScrollTransform.BeginAnimation(TranslateTransform.YProperty, null);
        _glassScrollTransform.Y = current;
    }

    /// <summary>Smoothly animates the whole grid to <paramref name="target"/> by
    /// animating the single scroll-layer transform (GPU-composited). The logical
    /// scroll offset commits immediately so hit-testing and the scrollbar track
    /// the destination while the visual glides into place.</summary>
    private void AnimateGlassScrollTo(double target)
    {
        double clamped = Math.Clamp(target, 0, GlassScrollMax);
        _glassScroll = clamped;
        SyncGlassScrollBar();

        if (_glassScrollTransform == null)
            return;

        BeginGlassScrollProfile();
        int token = ++_glassScrollAnimToken;

        double from = _glassScrollTransform.Y;   // current (possibly animated) value
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var anim = new DoubleAnimation(from, -clamped, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        };
        // Pin the animation clock to the oversampled animation rate so the glide
        // updates every vsync instead of defaulting to a lower timeline rate.
        Timeline.SetDesiredFrameRate(anim, App.AnimationFrameRate);
        anim.Completed += (_, _) =>
        {
            if (token == _glassScrollAnimToken)
            {
                EndGlassScrollProfile();
            }
        };
        // Pin the base value to the destination so the offset persists once the
        // animation (FillBehavior.Stop) releases the property.
        _glassScrollTransform.Y = -clamped;
        _glassScrollTransform.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    /// <summary>Sets the grid scroll offset instantly (no animation) and moves
    /// the scroll-layer transform + scrollbar thumb to match.</summary>
    private void SetGlassScroll(double offset)
    {
        double clamped = Math.Clamp(offset, 0, GlassScrollMax);
        _glassScroll = clamped;
        if (_glassScrollTransform != null)
        {
            _glassScrollTransform.BeginAnimation(TranslateTransform.YProperty, null);
            _glassScrollTransform.Y = -clamped;
        }
        SyncGlassScrollBar();
    }

    /// <summary>Pushes the current scroll offset onto the scrollbar's thumb,
    /// guarded so it does not recursively re-drive the scroll.</summary>
    private void SyncGlassScrollBar()
    {
        if (_glassScrollBar == null)
            return;
        _syncingScrollBar = true;
        _glassScrollBar.Value = _glassScroll;
        _syncingScrollBar = false;
    }

    /// <summary>Mouse-wheel / two-finger trackpad scroll of the glass grid.
    /// Windows delivers precision-touchpad two-finger pans as wheel deltas.</summary>
    private void OnGlassMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_theme.ShowGlassPanel || !GlassScrollable)
            return;
        // The floating hover label is pinned to a fixed on-screen spot; while the
        // grid scrolls underneath it would look detached, so dismiss it.
        HideGlassHoverLabel();
        // One notch (120) scrolls roughly one row; trackpad sends smaller deltas
        // for a smooth pan. Wheel-up (positive delta) reveals upper rows. Animate
        // toward the new target (accumulating across rapid notches, since the
        // logical offset commits immediately) so the whole grid glides.
        double step = GlassCellH * (e.Delta / 120.0);
        AnimateGlassScrollTo(_glassScroll - step);
        e.Handled = true;
    }
}
