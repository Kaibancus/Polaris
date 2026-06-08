using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Polaris.Views;

// Saturn theme: the centre planet button (top-down globe with gas-giant
// banding, the polar hexagon storm, spherical shading and the idle/hover spin).
public partial class RadialWindow
{
    private void DrawCenterButton()
    {
        double size = PlanetDiameter;
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

        // Soft drop shadow under the planet for depth. Rendered as a STATIC
        // sibling ellipse behind the planet (added near the bloom halo below)
        // instead of an Effect on `root`: because the disc inside `root` spins
        // every frame, a DropShadowEffect on `root` would re-blur the whole
        // planet square on every frame -- the main cause of the frame-rate drop
        // after the Saturn enlarge. A static shadow's blur is rasterised once.

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
        var disc = new Canvas
        {
            Width = size,
            Height = size,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = discRotate,
            // The band/streak/hexagon geometry never changes -- only the disc's
            // rotation animates. Caching it as a GPU bitmap means the perpetual
            // spin is a cheap composited rotate instead of a per-frame vector
            // re-tessellation of dozens of shapes (which got ~1.6x heavier when
            // the Saturn theme was enlarged). NOTE: do NOT attach/detach an
            // Effect (e.g. motion blur) on this cached disc -- toggling an
            // Effect on a BitmapCache'd visual forces a re-rasterise that
            // flashes the disc (was visible on hover acceleration).
            CacheMode = new BitmapCache(),
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
        // slowly; hovering speeds it up. Rather than snapping straight to the
        // new constant rate (a jarring instantaneous velocity jump), the disc
        // eases from its current angular velocity to the target one over a
        // short ramp, then settles into the steady perpetual spin. -----------
        double currentSpinSeconds = PlanetSpinSeconds;
        int spinGen = 0;

        void StartSpin(double secondsPerTurn, double rampTime = 0.9)
        {
            int gen = ++spinGen;
            double cur = discRotate.Angle % 360;
            discRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            discRotate.Angle = cur;

            // Begins the never-ending constant-rate spin from the current angle.
            void Steady()
            {
                double a = discRotate.Angle % 360;
                discRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                discRotate.Angle = a;
                var loop = new DoubleAnimation(a, a + 360,
                    TimeSpan.FromSeconds(secondsPerTurn))
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                };
                discRotate.BeginAnimation(RotateTransform.AngleProperty, loop);
            }

            double vNow = 360.0 / currentSpinSeconds;     // deg/s before the change
            double vTarget = 360.0 / secondsPerTurn;      // deg/s after the change
            currentSpinSeconds = secondsPerTurn;

            // No meaningful speed change (e.g. the initial start): go steady now.
            if (Math.Abs(vNow - vTarget) < 0.01)
            {
                Steady();
            }
            else
            {
                // Ramp distance for a linear velocity change over the transition
                // time = average velocity x time. A custom easing makes the
                // disc's angular velocity vary linearly from vNow to vTarget so
                // the handoff into the steady spin has matching speed (no jerk).
                double T = rampTime;
                double dist = (vNow + vTarget) / 2.0 * T;
                var ramp = new DoubleAnimation(cur, cur + dist, TimeSpan.FromSeconds(T))
                {
                    EasingFunction = new VelocityRampEase { StartVel = vNow, EndVel = vTarget },
                    FillBehavior = FillBehavior.Stop,
                };
                ramp.Completed += (_, _) =>
                {
                    if (gen == spinGen)   // not superseded by a newer StartSpin
                        Steady();
                };
                discRotate.Angle = cur + dist;            // hold final angle when ramp stops
                discRotate.BeginAnimation(RotateTransform.AngleProperty, ramp);
            }
        }

        StartSpin(PlanetSpinSeconds);            // gentle idle self-rotation
        // Hover: snap up to speed quickly (short ramp) so the planet visibly
        // accelerates the instant the cursor lands on it. Leaving uses the long
        // gentle ramp to coast back down.
        root.MouseEnter += (_, _) => StartSpin(3.0, rampTime: 0.24);
        root.MouseLeave += (_, _) => StartSpin(PlanetSpinSeconds, rampTime: 0.72);

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

        // Static drop shadow (replaces the per-frame DropShadowEffect that used
        // to sit on `root`). A soft black disc the size of the planet, blurred
        // once and composited behind it for depth.
        var planetShadow = new Ellipse
        {
            Width = size,
            Height = size,
            IsHitTestVisible = false,
            Fill = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)),
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 12 },
        };
        Panel.SetZIndex(planetShadow, 1999); // drawn above the bloom, under root
        Canvas.SetLeft(planetShadow, _center.X - size / 2);
        Canvas.SetTop(planetShadow, _center.Y - size / 2);
        PanelCanvas.Children.Add(planetShadow);

        Panel.SetZIndex(root, 2000); // keep Saturn above the ring bands
        Canvas.SetLeft(root, _center.X - size / 2);
        Canvas.SetTop(root, _center.Y - size / 2);
        PanelCanvas.Children.Add(root);
    }

    /// <summary>
    /// Easing whose normalised progress makes the animated value's rate of
    /// change vary linearly from <see cref="StartVel"/> to <see cref="EndVel"/>.
    /// Used to ramp the planet's spin between two constant angular velocities so
    /// the disc accelerates/decelerates smoothly and the speed matches at the
    /// handoff into the steady spin (no velocity discontinuity / jerk).
    /// </summary>
    private sealed class VelocityRampEase : EasingFunctionBase
    {
        public double StartVel { get; set; }
        public double EndVel { get; set; }

        protected override double EaseInCore(double t)
        {
            double a = StartVel, b = EndVel;
            double s = a + b;
            if (s <= 0)
                return t;
            // p(t) = (2a·t + (b-a)·t²) / (a+b): p(0)=0, p(1)=1, and dp/dt has
            // ratio b:a between the endpoints, i.e. a linear velocity ramp.
            return (2 * a * t + (b - a) * t * t) / s;
        }

        protected override Freezable CreateInstanceCore() => new VelocityRampEase();
    }
}
