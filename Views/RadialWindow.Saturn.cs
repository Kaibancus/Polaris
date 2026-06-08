using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Polaris.Views;

// Saturn theme: the ring system (disc, particle ring zones, revolution cues,
// shepherd moons and the starfield). The centre planet lives in
// RadialWindow.Planet.cs; ring/orbit animation lives in the main file.
public partial class RadialWindow
{
    private void DrawBackingDisc()
    {
        double icon = EffectiveIconSize;
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
            Opacity = 1.0 - Math.Clamp(_config.Settings.PanelTransparency, 0.0, 1.0),
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

        // Planet body radius (must match DrawCenterButton: fixed base, scaled
        // by resolution only — independent of the user's icon-size setting).
        double planetR = PlanetDiameter / 2.0;

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
            double eIn = gIn + outerIcon * 0.18;
            double eOut = r - icon * 0.04;
            DrawRingZone(eIn, eOut, icyG, 0.147, 0.012, icon, crispRim: false);     // E ring (broad halo, fades out)

            // Soft outer bloom: a blurred icy halo over the faint G/E rings so
            // they glow and fade out rather than ending abruptly.
            AddBloomRing((gIn + eOut) / 2, (eOut - gIn) + icon * 0.7, icyG, 0.06);
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
        // Deliberately NOT added to the rotating _ringLayer: this halo is an
        // axis-symmetric ellipse centred on the panel, so revolving it looks
        // identical frame-to-frame yet forces WPF to re-rasterise a large blur
        // kernel every frame (the dominant per-frame cost, and it grows with the
        // theme-enlarge factor). On the static canvas the blur is computed once.
        PanelCanvas.Children.Add(glow);
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

        // Soft angular edges are baked into a tangential gradient brush (dark in
        // the middle, fading to transparent at the two angular flanks) instead of
        // a per-frame BlurEffect. Because this Path revolves on the animated orbit
        // layer, a BlurEffect would force WPF to re-rasterise the blur kernel every
        // frame; a gradient fill is rasterised once, so the soft look is preserved
        // with no per-frame cost.
        Color spokeCol = Color.FromRgb(0x14, 0x10, 0x08);
        var spokeBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),   // one angular flank (tangential)
            EndPoint = new Point(0.5, 1),     // the other angular flank
            GradientStops =
            {
                new GradientStop(WithAlpha(spokeCol, 0.0), 0.0),
                new GradientStop(WithAlpha(spokeCol, alpha), 0.5),
                new GradientStop(WithAlpha(spokeCol, 0.0), 1.0),
            },
        };
        var spoke = new System.Windows.Shapes.Path
        {
            IsHitTestVisible = false,
            Fill = spokeBrush,
            Data = geo,
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
        double aInner, double aOuter, double iconSize, bool crispRim = true)
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
                  0.64
                + 0.17 * Math.Sin(rr * 0.018 + 1.3)   // broad brightness envelope
                + 0.09 * Math.Sin(rr * 0.071)         // medium undulation
                + 0.07 * Math.Sin(rr * 0.193 + 0.7)   // fine ripple
                + 0.11 * (Hash01(rr) - 0.5);          // high-frequency speckle
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

        // Crisp bright edge on the outer rim of the zone. Skipped for the broad
        // outermost halo so it dissolves softly into the disc instead of ending
        // on a hard ring.
        if (crispRim)
        {
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
}
