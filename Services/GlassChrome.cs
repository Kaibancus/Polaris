using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Polaris.Services;

/// <summary>
/// Shared "Liquid Glass" chrome rendering, used by both the main radial/grid
/// dock and the left-edge vertical dock so the two share one identical look.
/// Every layer is added to the supplied <see cref="Canvas"/> at fixed Z-indices
/// and is hit-test invisible (pure decoration).
/// </summary>
internal static class GlassChrome
{
    /// <summary>Transparency at/above which the panel is perfectly clear glass.
    /// The frosted (ground-glass) diffusion ramps in linearly across the whole
    /// slider, reaching full strength at transparency 0, so moving the slider
    /// scrubs evenly from frosted glass to fully clear.</summary>
    public const double FrostThreshold = 1.0;

    /// <summary>Maps the user's panel-transparency (0 = opaque, 1 = clear) to a
    /// frost strength (1 = fully ground glass at transparency 0, 0 = clear once
    /// transparency reaches <see cref="FrostThreshold"/>).</summary>
    public static double FrostStrengthFor(double transparency)
    {
        double t = Math.Clamp(transparency, 0.0, 1.0);
        return Math.Clamp((FrostThreshold - t) / FrostThreshold, 0.0, 1.0);
    }

    /// <summary>Draws an Apple-style "Liquid Glass" slab (translucent body, edge
    /// rim, specular dome, glare and base shade) onto <paramref name="target"/>.
    /// When <paramref name="track"/> is supplied, every created element is added
    /// to it so the caller can remove the slab later.</summary>
    public static void DrawSlab(Canvas target, double left, double top, double w, double h, double radius, double opacity,
        List<FrameworkElement>? track = null, bool frosted = false, bool dark = false, bool featherMask = true,
        double frostStrength = 0.0)
    {
        // Body: clear, lightly cool-tinted glass sheet with a soft floating shadow.
        // When frosted, the fill is milkier (higher white alpha) to read as
        // ground / frosted glass rather than clear glass. When dark, the body is
        // near-black smoked glass (used by the Saturn theme's side dock).
        var bodyBrush = dark
            ? (GradientBrush)new RadialGradientBrush
            {
                // Matches the Saturn main-dock disc material: an opaque near-black
                // radial fill (0x05060C → 0x000000). The slab's own Opacity is set
                // to 1 - PanelTransparency by the caller, identical to the disc.
                GradientOrigin = new Point(0.5, 0.42),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.62,
                RadiusY = 0.62,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xFF, 0x05, 0x06, 0x0C), 0.0),
                    new GradientStop(Color.FromArgb(0xFF, 0x02, 0x03, 0x07), 0.72),
                    new GradientStop(Color.FromArgb(0xFF, 0x00, 0x00, 0x00), 1.0),
                },
            }
            : frosted
            ? new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x7A, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x68, 0xF0, 0xF5, 0xFF), 0.5),
                    new GradientStop(Color.FromArgb(0x72, 0xDD, 0xE6, 0xF6), 1.0),
                },
            }
            : new RadialGradientBrush
            {
                // Clear glass lit from its geometric centre: a bright core melting
                // outward into a cooler, dimmer rim (centre-bright, edge-dark).
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.72,
                RadiusY = 0.72,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x2E, 0xDA, 0xEC, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x1A, 0xEA, 0xF2, 0xFF), 0.48),
                    new GradientStop(Color.FromArgb(0x12, 0xCE, 0xDE, 0xF2), 0.8),
                    new GradientStop(Color.FromArgb(0x0A, 0xAE, 0xC2, 0xDC), 1.0),
                },
            };
        var glass = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(radius),
            Opacity = opacity,
            Background = bodyBrush,
            // The downward drop shadow expands the element's render bounds only
            // toward the bottom; combined with an OpacityMask (which is mapped to
            // those expanded bounds) it would make the feather look top-bottom
            // asymmetric. The dark dock relies on the edge feather instead of a
            // shadow, so it is omitted there to keep the fade symmetric.
            Effect = dark ? null : new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 48,
                ShadowDepth = 10,
                Direction = 270,
                Opacity = 0.42,
                Color = Color.FromRgb(0x06, 0x0B, 0x16),
            },
            IsHitTestVisible = false,
            CacheMode = new System.Windows.Media.BitmapCache(),
        };
        if (dark && featherMask)
        {
            // Dark Saturn dock: feather the whole slab so its edges dissolve
            // gradually into the desktop rather than ending on a hard rim. The
            // mask follows the rounded-rectangle outline (not an ellipse) so the
            // fade hugs the same shape as the border. The right edge feathers
            // less (0.6) than the left/top/bottom so the dock's inner edge reads
            // a touch crisper than the screen-hugging left edge.
            glass.OpacityMask = BuildRoundedFeatherMask(w, h, radius, rightFeatherScale: 0.6);
        }
        Canvas.SetLeft(glass, left);
        Canvas.SetTop(glass, top);
        Panel.SetZIndex(glass, -12);
        target.Children.Add(glass);
        track?.Add(glass);

        // Continuous "ground / frosted glass" diffusion. Unlike the binary
        // `frosted` body brush above, this is a milky white sheet whose strength
        // is driven by <paramref name="frostStrength"/> (0 = perfectly clear glass,
        // 1 = fully ground glass), letting the panel-transparency setting scrub
        // smoothly from frosted (at transparency 0) to clear. A near-uniform fill
        // (high alpha edge-to-edge, only a gentle centre lift) scatters light like
        // real frosted glass; it sits above the body but below the specular
        // highlights so the liquid-glass glints still read on top.
        if (!dark && frostStrength > 0.001)
        {
            double fs = Math.Clamp(frostStrength, 0.0, 1.0);
            byte Peak(double mul) => (byte)Math.Clamp(fs * 0xC8 * mul, 0, 255);
            var frost = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(radius),
                Opacity = opacity,
                IsHitTestVisible = false,
                CacheMode = new System.Windows.Media.BitmapCache(),
                Background = new RadialGradientBrush
                {
                    GradientOrigin = new Point(0.5, 0.34),
                    Center = new Point(0.5, 0.34),
                    RadiusX = 0.95,
                    RadiusY = 1.05,
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(Peak(1.0), 0xFF, 0xFF, 0xFF), 0.0),
                        new GradientStop(Color.FromArgb(Peak(0.92), 0xF2, 0xF6, 0xFF), 0.55),
                        new GradientStop(Color.FromArgb(Peak(0.86), 0xE4, 0xEC, 0xF8), 1.0),
                    },
                },
            };
            Canvas.SetLeft(frost, left);
            Canvas.SetTop(frost, top);
            Panel.SetZIndex(frost, -11);
            target.Children.Add(frost);
            track?.Add(frost);
        }

        // Frosted diffusion veil: an extra soft milky sheet that lifts the
        // body's luminosity uniformly, mimicking the way frosted glass scatters
        // light. Only added in frosted mode.
        if (frosted)
        {
            var veil = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(radius),
                Opacity = opacity,
                IsHitTestVisible = false,
                CacheMode = new System.Windows.Media.BitmapCache(),
                Background = new RadialGradientBrush
                {
                    GradientOrigin = new Point(0.5, 0.32),
                    Center = new Point(0.5, 0.32),
                    RadiusX = 0.75,
                    RadiusY = 0.9,
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF), 0.0),
                        new GradientStop(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 0.6),
                        new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0),
                    },
                },
            };
            Canvas.SetLeft(veil, left);
            Canvas.SetTop(veil, top);
            Panel.SetZIndex(veil, -11);
            target.Children.Add(veil);
            track?.Add(veil);
        }

        // Edge shade: a soft dark vignette around the rim (centre stays clear) so
        // the slab reads as brightest at its geometric centre, fading to a darker
        // edge. Skipped in dark mode (the Saturn slab is near-black already).
        if (!dark)
        {
            var edgeShade = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(radius),
                Opacity = opacity,
                IsHitTestVisible = false,
                Background = new RadialGradientBrush
                {
                    GradientOrigin = new Point(0.5, 0.5),
                    Center = new Point(0.5, 0.5),
                    RadiusX = 0.72,
                    RadiusY = 0.72,
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0x00, 0x0A, 0x12, 0x20), 0.0),
                        new GradientStop(Color.FromArgb(0x00, 0x0A, 0x12, 0x20), 0.6),
                        new GradientStop(Color.FromArgb(0x20, 0x0A, 0x12, 0x20), 1.0),
                    },
                },
            };
            Canvas.SetLeft(edgeShade, left);
            Canvas.SetTop(edgeShade, top);
            Panel.SetZIndex(edgeShade, -11);
            target.Children.Add(edgeShade);
            track?.Add(edgeShade);
        }

        // Centre specular bloom: blurred bright highlight pooled at the slab's
        // centre (reinforcing the centre-bright, edge-dark look). Skipped in dark
        // mode so the slab reads as the flat near-black Saturn disc material.
        if (!dark)
        {
            var topCap = new Border
            {
                Width = w * 0.86,
                Height = h * 0.55,
                CornerRadius = new CornerRadius(w * 0.43),
                Opacity = opacity,
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = Math.Max(22, h * 0.07) },
                CacheMode = new System.Windows.Media.BitmapCache(),
                Background = new RadialGradientBrush
                {
                    GradientOrigin = new Point(0.5, 0.5),
                    Center = new Point(0.5, 0.5),
                    RadiusX = 0.58,
                    RadiusY = 0.72,
                    GradientStops =
                    {
                        // Centred bloom: brightest at the geometric centre, fading
                        // out toward the rim.
                        new GradientStop(Color.FromArgb(0x32, 0xFF, 0xFF, 0xFF), 0.0),
                        new GradientStop(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), 0.5),
                        new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0),
                    },
                },
            };
            Canvas.SetLeft(topCap, left + w * 0.07);
            Canvas.SetTop(topCap, top + (h - h * 0.55) / 2.0);
            Panel.SetZIndex(topCap, -9);
            target.Children.Add(topCap);
            track?.Add(topCap);

            // Diagonal glare streak: a faint tilted bright bar clipped to the panel.
            var glareClip = new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(radius),
                Opacity = opacity,
                IsHitTestVisible = false,
                ClipToBounds = true,
                CacheMode = new System.Windows.Media.BitmapCache(),
                Clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius),
            };
            var glareCanvas = new Canvas { Width = w, Height = h };
            var glare = new System.Windows.Shapes.Rectangle
            {
                Width = w * 1.7,
                Height = h * 0.16,
                RadiusX = h * 0.08,
                RadiusY = h * 0.08,
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = Math.Max(18, h * 0.05) },
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 0),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.0),
                        new GradientStop(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 0.5),
                        new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0),
                    },
                },
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(-20),
            };
            Canvas.SetLeft(glare, -w * 0.35);
            Canvas.SetTop(glare, h * 0.06);
            glareCanvas.Children.Add(glare);
            glareClip.Child = glareCanvas;
            Canvas.SetLeft(glareClip, left);
            Canvas.SetTop(glareClip, top);
            Panel.SetZIndex(glareClip, -8);
            target.Children.Add(glareClip);
            track?.Add(glareClip);
        }

        // Luminous edge rim: bright hairline catching light around the slab,
        // brightest top-left. On top of every other layer so the edge stays crisp.
        // In dark mode the rim becomes a soft, faded near-black edge instead of a
        // bright white hairline.
        var rim = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(radius),
            Opacity = opacity,
            IsHitTestVisible = false,
            BorderThickness = new Thickness(1.1),
            BorderBrush = dark
                ? new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0x66, 0x16, 0x18, 0x1E), 0.0),
                        new GradientStop(Color.FromArgb(0x22, 0x0A, 0x0B, 0x10), 0.4),
                        new GradientStop(Color.FromArgb(0x14, 0x05, 0x06, 0x0A), 0.62),
                        new GradientStop(Color.FromArgb(0x4A, 0x00, 0x00, 0x00), 1.0),
                    },
                }
                : new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF), 0.4),
                    new GradientStop(Color.FromArgb(0x30, 0xC8, 0xDA, 0xF5), 0.62),
                    new GradientStop(Color.FromArgb(0x9C, 0xFF, 0xFF, 0xFF), 1.0),
                },
            },
        };
        if (dark)
        {
            // Feather the rim with the same rounded-rectangle mask as the body so
            // the dark edge dissolves gradually following the border's shape
            // instead of ringing the slab with a continuous hard line. Match the
            // body's weaker right-edge feather.
            rim.OpacityMask = BuildRoundedFeatherMask(w, h, radius, rightFeatherScale: 0.6);
        }
        Canvas.SetLeft(rim, left);
        Canvas.SetTop(rim, top);
        Panel.SetZIndex(rim, -6);
        target.Children.Add(rim);
        track?.Add(rim);

        // Inner refraction glow: soft bright ring just inside the rim. Skipped in
        // dark mode so the edge stays a quiet dark fade rather than a bright halo.
        if (!dark)
        {
            var innerGlow = new Border
            {
                Width = w - 2,
                Height = h - 2,
                CornerRadius = new CornerRadius(radius - 1),
                Opacity = opacity,
                IsHitTestVisible = false,
                BorderThickness = new Thickness(2.2),
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 4 },
                CacheMode = new System.Windows.Media.BitmapCache(),
                BorderBrush = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1),
                    GradientStops =
                    {
                        // Stronger inner refraction, like a liquid lens at the rim.
                        new GradientStop(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), 0.0),
                        new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.5),
                        new GradientStop(Color.FromArgb(0x48, 0xDC, 0xEA, 0xFF), 1.0),
                    },
                },
            };
            Canvas.SetLeft(innerGlow, left + 1);
            Canvas.SetTop(innerGlow, top + 1);
            Panel.SetZIndex(innerGlow, -7);
            target.Children.Add(innerGlow);
            track?.Add(innerGlow);
        }
    }

    /// <summary>Builds an opacity mask that feathers a rounded-rectangle slab:
    /// a white rounded rectangle (matching the slab's corner radius) inset and
    /// blurred so the edges dissolve to transparent while following the rounded
    /// outline rather than an ellipse. The mask is baked to a fixed w×h bitmap so
    /// the fade is pixel-exact and perfectly symmetric on every edge (a live
    /// VisualBrush + BlurEffect would expand its render bounds and skew the
    /// top/bottom mapping).</summary>
    private static Brush BuildRoundedFeatherMask(double w, double h, double radius, double rightFeatherScale = 1.0)
    {
        if (w <= 0 || h <= 0)
            return Brushes.White;

        double feather = Math.Max(10.0, Math.Min(w, h) * 0.16);
        double rightInset = feather * Math.Clamp(rightFeatherScale, 0.0, 1.0);
        var host = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(0, w - feather - rightInset),
            Height = Math.Max(0, h - feather * 2),
            RadiusX = Math.Max(0, radius - feather * 0.5),
            RadiusY = Math.Max(0, radius - feather * 0.5),
            Fill = Brushes.White,
            Effect = new System.Windows.Media.Effects.BlurEffect
            {
                Radius = feather,
                KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
            },
        };
        // Inset the shape: left/top/bottom by the full feather; the right inset
        // can be reduced (rightFeatherScale < 1) for a weaker, crisper right edge.
        var container = new Canvas { Width = w, Height = h };
        Canvas.SetLeft(host, feather);
        Canvas.SetTop(host, feather);
        container.Children.Add(host);
        container.Measure(new Size(w, h));
        container.Arrange(new Rect(0, 0, w, h));
        container.UpdateLayout();

        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(w), (int)Math.Ceiling(h), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(container);
        rtb.Freeze();

        return new ImageBrush(rtb)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewbox = new Rect(0, 0, 1, 1),
        };
    }
}
