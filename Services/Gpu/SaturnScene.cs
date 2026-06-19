using System;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Polaris.Services.Gpu;

/// <summary>Immediate-mode Direct2D port of the WPF Saturn theme
/// (<c>RadialWindow.Saturn.cs</c> + <c>RadialWindow.Planet.cs</c>): the photo-real
/// ring system (near-black disc, named D/C/B/Cassini/A/F/G/E rings, embedded
/// ringlets, icy speckle, starfield, outer bloom) and the centre planet (gas-giant
/// banding, zonal wind streaks, the north-polar hexagon storm and spherical
/// shading). Unlike the WPF version — which builds hundreds of cached shapes once
/// and composites a layered window every frame — the GPU dock re-draws the whole
/// scene each frame on the GPU, so no BitmapCache layering is needed.
///
/// Stage 1: static disc + rings + planet (no orbit/spin/twinkle yet; the rotating
/// local revolution cues — shimmer arcs, spokes, moons, density blobs — and the
/// planet spin land in later stages, driven by the <c>innerAngle/outerAngle/spinAngle</c>
/// parameters threaded through <see cref="Draw"/>).</summary>
internal static class SaturnScene
{
    /// <summary>Geometry for one Saturn render, all in window-local DIPs.</summary>
    internal struct Geom
    {
        public float Cx, Cy;          // panel centre
        public float InnerRadius;     // bright B ring (inner icon ring)
        public float RingStep;        // inner->outer ring spacing
        public float OuterRadius;     // InnerRadius + RingStep (outer icon ring / F ringlet)
        public float Icon;            // EffectiveIconSize
        public float OuterIcon;       // icon * OuterIconScale
        public float PlanetR;         // planet body radius
        public float TiltY;           // ring foreshorten (RingTiltY)
        public float DiscOpacity;     // 1 - PanelTransparency
    }

    // ---- colour helpers (mirror RadialWindow.Saturn.cs) ----
    private readonly struct Rgb
    {
        public readonly byte R, G, B;
        public Rgb(byte r, byte g, byte b) { R = r; G = g; B = b; }
        public Rgb(int r, int g, int b) { R = (byte)r; G = (byte)g; B = (byte)b; }
    }

    private static Color4 A(Rgb c, double opacity) =>
        new(c.R / 255f, c.G / 255f, c.B / 255f, (float)Math.Clamp(opacity, 0, 1));

    private static Color4 A(Rgb c, double opacity, byte a) =>
        new(c.R / 255f, c.G / 255f, c.B / 255f, (float)Math.Clamp(opacity * (a / 255.0), 0, 1));

    private static Rgb Lighten(Rgb c, double amt) => new(
        (byte)Math.Clamp(c.R + (255 - c.R) * amt, 0, 255),
        (byte)Math.Clamp(c.G + (255 - c.G) * amt, 0, 255),
        (byte)Math.Clamp(c.B + (255 - c.B) * amt, 0, 255));

    private static Rgb Darken(Rgb c, double amt) => new(
        (byte)Math.Clamp(c.R * (1 - amt), 0, 255),
        (byte)Math.Clamp(c.G * (1 - amt), 0, 255),
        (byte)Math.Clamp(c.B * (1 - amt), 0, 255));

    private static Rgb Lerp(Rgb a, Rgb b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Rgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private static double Hash01(double x)
    {
        double s = Math.Sin(x * 12.9898) * 43758.5453;
        return s - Math.Floor(s);
    }

    private static ID2D1GradientStopCollection Stops(ID2D1DeviceContext ctx, params (float pos, Color4 col)[] s)
    {
        var arr = new GradientStop[s.Length];
        for (int i = 0; i < s.Length; i++) arr[i] = new GradientStop { Position = s[i].pos, Color = s[i].col };
        return ctx.CreateGradientStopCollection(arr);
    }

    /// <summary>Draws the full Saturn scene in one pass (used for the uncached
    /// path / reference). Prefer the split <see cref="DrawStaticScene"/> +
    /// <see cref="DrawPlanetDisc"/> + <see cref="DrawPlanetShade"/> + <see cref="DrawTwinkle"/>
    /// so the static layers can be baked to bitmaps and only the spin/twinkle
    /// re-draw each frame.</summary>
    public static void Draw(ID2D1DeviceContext ctx, in Geom g,
        double innerAngle = 0, double outerAngle = 0, double spinAngle = 0, double time = 0)
    {
        DrawStaticScene(ctx, g);
        DrawPlanetDisc(ctx, g);
        DrawPlanetShade(ctx, g);
        DrawTwinkle(ctx, g, time);
    }

    // ===================================================================
    //  Static layer: disc + (non-twinkle) starfield + named rings + planet body.
    //  Baked once into a bitmap by the dock; never changes between frames.
    // ===================================================================
    public static void DrawStaticScene(ID2D1DeviceContext ctx, in Geom g)
    {
        DrawBackingDisc(ctx, g);
        DrawPlanetBody(ctx, g);
    }

    private static void DrawBackingDisc(ID2D1DeviceContext ctx, in Geom g)
    {
        float tilt = g.TiltY;
        float outerIcon = g.OuterIcon;
        float r = g.OuterRadius + outerIcon;
        var c = new Vector2(g.Cx, g.Cy);

        // Near-black disc background, foreshortened into the tilted ring plane.
        var discCol0 = new Rgb(0x05, 0x06, 0x0C);
        var discCol1 = new Rgb(0x02, 0x03, 0x07);
        var disc = new Ellipse(c, r, r * tilt);
        using (var stops = Stops(ctx,
            (0f, A(discCol0, g.DiscOpacity)),
            (0.72f, A(discCol1, g.DiscOpacity)),
            (0.96f, A(new Rgb(0, 0, 0), g.DiscOpacity)),
            (1f, A(new Rgb(0, 0, 0), g.DiscOpacity * (0xE4 / 255.0)))))
        using (var brush = ctx.CreateRadialGradientBrush(
            new RadialGradientBrushProperties
            {
                Center = c,
                GradientOriginOffset = new Vector2(0, -0.04f * 2f * r * tilt),
                RadiusX = r,
                RadiusY = r * tilt,
            }, stops))
            ctx.FillEllipse(disc, brush);

        // Faint starfield behind the rings (non-twinkling stars only; the
        // twinkling subset is re-drawn each frame by DrawTwinkle).
        DrawStarfieldBase(ctx, g);

        bool hasOuter = g.OuterRadius > g.InnerRadius + 0.5f;
        float icon = g.Icon;
        float rB = g.InnerRadius;
        float rF = g.InnerRadius + g.RingStep;
        float planetR = g.PlanetR;

        // Real Saturn ring radii (units of Saturn equatorial radius).
        const double Rplanet = 1.000;
        const double RDin = 1.110, RDout = 1.236;
        const double RCin = 1.239, RCout = 1.526;
        const double RBin = 1.526, RBmid = 1.739, RBout = 1.951;
        const double RCassIn = 1.951, RCassOut = 2.025;
        const double RAin = 2.025, REncke = 2.214, RAout = 2.269;
        const double RRoche = 2.320, RF = 2.324;

        double kIn = (rB - planetR) / (RBmid - Rplanet);
        double bOutPx = planetR + (RBout - Rplanet) * kIn;
        double kOut = (rF - bOutPx) / (RF - RBout);
        double MapR(double rr) => rr <= RBout
            ? planetR + (rr - Rplanet) * kIn
            : bOutPx + (rr - RBout) * kOut;

        var paleB = new Rgb(0xCB, 0xBC, 0x95);
        var tanA = new Rgb(0xAA, 0x8E, 0x64);
        var dimC = new Rgb(0x70, 0x5C, 0x3D);
        var faintD = new Rgb(0x54, 0x46, 0x30);
        var icyG = new Rgb(0x97, 0xA3, 0xA8);

        bool extra = Polaris.Services.RenderProfile.SaturnExtraDetail;

        // --- Inner group: D, C, B -------------------------------------------
        DrawRingZone(ctx, g, MapR(RDin), MapR(RDout), faintD, 0.12, 0.20, icon);
        DrawRingZone(ctx, g, MapR(RCin), MapR(RCout), dimC, 0.18, 0.35, icon);
        DrawRingZone(ctx, g, MapR(RBin), MapR(RBout), paleB, 0.60, 0.66, icon);

        if (extra)
        {
            double rbi = MapR(RBin), rbo = MapR(RBout), bw = rbo - rbi;
            AddRinglet(ctx, g, rbi + bw * 0.22, Lighten(paleB, 0.24), 0.30, 1.1);
            AddRinglet(ctx, g, rbi + bw * 0.46, Darken(paleB, 0.45), 0.26, 1.4);
            AddRinglet(ctx, g, rbi + bw * 0.70, Lighten(paleB, 0.20), 0.28, 1.1);
        }

        if (hasOuter)
        {
            DrawRingZone(ctx, g, MapR(RCassIn), MapR(RCassOut), faintD, 0.03, 0.04, icon);
            DrawRingZone(ctx, g, MapR(RAin), MapR(REncke - 0.004), tanA, 0.42, 0.50, icon);
            DrawRingZone(ctx, g, MapR(REncke + 0.004), MapR(RAout), tanA, 0.44, 0.48, icon);

            if (extra)
            {
                double rci = MapR(RCassIn), rco = MapR(RCassOut);
                AddRinglet(ctx, g, (rci + rco) / 2, Lighten(paleB, 0.20), 0.10, 1.0);
                double rai = MapR(RAin), rao = MapR(RAout), aw = rao - rai;
                AddRinglet(ctx, g, rai + aw * 0.34, Lighten(tanA, 0.20), 0.24, 1.1);
                AddRinglet(ctx, g, rai + aw * 0.68, Darken(tanA, 0.42), 0.22, 1.3);
            }

            DrawRingZone(ctx, g, MapR(RRoche), MapR(RF) - icon * 0.06, faintD, 0.03, 0.05, icon);
            DrawRingZone(ctx, g, rF - icon * 0.09, rF + icon * 0.09, paleB, 0.09, 0.12, icon * 0.42);

            double gIn = rF + outerIcon * 0.34;
            DrawRingZone(ctx, g, gIn, gIn + icon * 0.06, icyG, 0.05, 0.08, icon * 0.5);
            double eIn = gIn + outerIcon * 0.18;
            double eOut = r - icon * 0.04;
            DrawRingZone(ctx, g, eIn, eOut, icyG, 0.14, 0.012, icon, crispRim: false);

            // Soft outer bloom over the faint G/E rings.
            AddBloomRing(ctx, g, (gIn + eOut) / 2, (eOut - gIn) + icon * 0.7, icyG, 0.078);
        }
    }

    /// <summary>One named ring zone: a dense stack of concentric particle strokes
    /// with alpha ramp + multi-frequency granular density + sparse speckle.</summary>
    private static void DrawRingZone(ID2D1DeviceContext ctx, in Geom g,
        double rInner, double rOuter, Rgb color, double aInner, double aOuter,
        double iconSize, bool crispRim = true)
    {
        if (rOuter <= rInner || rOuter <= 1) return;
        var c = new Vector2(g.Cx, g.Cy);
        float tilt = g.TiltY;

        double spacing = Math.Max(1.4, iconSize * 0.020);
        float thickness = (float)(spacing * 1.7);
        int n = Math.Max(1, (int)Math.Round((rOuter - rInner) / spacing));

        for (int i = 0; i <= n; i++)
        {
            double t = n == 0 ? 0.5 : i / (double)n;
            double rr = rInner + (rOuter - rInner) * t;
            if (rr <= 1) continue;

            double grain =
                  0.64
                + 0.17 * Math.Sin(rr * 0.018 + 1.3)
                + 0.09 * Math.Sin(rr * 0.071)
                + 0.07 * Math.Sin(rr * 0.193 + 0.7)
                + 0.11 * (Hash01(rr) - 0.5);
            grain = Math.Clamp(grain, 0.32, 1.12);
            double alpha = Math.Clamp((aInner + (aOuter - aInner) * t) * grain, 0, 1);
            double shadeT = Math.Clamp(0.5 + 0.5 * Math.Sin(rr * 0.5)
                                       + 0.18 * (Hash01(rr * 3.1) - 0.5), 0, 1);
            Rgb shade = Lerp(Darken(color, 0.20), Lighten(color, 0.16), shadeT);

            var e = new Ellipse(c, (float)rr, (float)(rr * tilt));
            using var br = ctx.CreateSolidColorBrush(A(shade, alpha));
            ctx.DrawEllipse(e, br, thickness);
        }

        if (crispRim)
        {
            var e = new Ellipse(c, (float)rOuter, (float)(rOuter * tilt));
            using var br = ctx.CreateSolidColorBrush(A(Lighten(color, 0.25), aOuter * 0.5));
            ctx.DrawEllipse(e, br, 1.0f);
        }

        // Sparse icy-particle speckle (deterministic positions).
        int speckles = (int)Math.Clamp((rOuter - rInner) * 0.85 * Polaris.Services.RenderProfile.SaturnDetailFactor, 0, 60);
        for (int i = 0; i < speckles; i++)
        {
            double rr = rInner + (rOuter - rInner) * Hash01(rInner * 7.1 + i * 2.3);
            double ang = Hash01(rOuter * 3.7 + i * 5.9) * Math.PI * 2;
            double br = Hash01(i * 1.7 + rInner);
            double px = g.Cx + Math.Cos(ang) * rr;
            double py = g.Cy + Math.Sin(ang) * rr * tilt;
            double sa = (34 + 120 * br) / 255.0;
            Rgb sc = br > 0.5 ? Lighten(color, 0.45) : Darken(color, 0.45);
            double sz = 0.8 + 1.9 * Hash01(i * 9.3 + rOuter);
            var e = new Ellipse(new Vector2((float)px, (float)py), (float)(sz / 2), (float)(sz / 2));
            using var brush = ctx.CreateSolidColorBrush(A(sc, sa));
            ctx.FillEllipse(e, brush);
        }
    }

    private static void AddRinglet(ID2D1DeviceContext ctx, in Geom g, double radius, Rgb color, double alpha, double thickness)
    {
        if (radius <= 1) return;
        var e = new Ellipse(new Vector2(g.Cx, g.Cy), (float)radius, (float)(radius * g.TiltY));
        using var br = ctx.CreateSolidColorBrush(A(color, alpha));
        ctx.DrawEllipse(e, br, (float)thickness);
    }

    /// <summary>Approximate the WPF blurred halo with a few stacked translucent
    /// ellipse strokes of decreasing alpha (no per-frame BlurEffect needed).</summary>
    private static void AddBloomRing(ID2D1DeviceContext ctx, in Geom g, double rMid, double thickness, Rgb color, double alpha)
    {
        var c = new Vector2(g.Cx, g.Cy);
        int layers = 5;
        for (int i = 0; i < layers; i++)
        {
            double th = Math.Max(2, thickness) * (1.0 - i * 0.12);
            double a = alpha * (1.0 - i * 0.16);
            var e = new Ellipse(c, (float)rMid, (float)(rMid * g.TiltY));
            using var br = ctx.CreateSolidColorBrush(A(color, a));
            ctx.DrawEllipse(e, br, (float)th);
        }
    }

    private static void DrawStarfieldBase(ID2D1DeviceContext ctx, in Geom g)
    {
        float r = g.OuterRadius + g.OuterIcon;
        int count = (int)Math.Round(104 * Polaris.Services.RenderProfile.SaturnDetailFactor);
        const double twinkleGate = 0.55;
        for (int i = 0; i < count; i++)
        {
            if (Hash01(i * 11.1) > twinkleGate) continue;   // twinkler -> drawn dynamically
            double ang = Hash01(i * 2.17) * Math.PI * 2;
            double rad = Math.Sqrt(Hash01(i * 5.31)) * r * 0.96;
            double px = g.Cx + Math.Cos(ang) * rad;
            double py = g.Cy + Math.Sin(ang) * rad * g.TiltY;
            double sz = 0.6 + 1.9 * Hash01(i * 7.7);
            double br = (60 + 150 * Hash01(i * 3.3)) / 255.0;
            var e = new Ellipse(new Vector2((float)px, (float)py), (float)(sz / 2), (float)(sz / 2));
            using var brush = ctx.CreateSolidColorBrush(new Color4(1f, 1f, 250f / 255f, (float)br));
            ctx.FillEllipse(e, brush);
        }
    }

    /// <summary>Re-draws only the twinkling star subset each frame (~45 stars), so
    /// the static scene can stay baked. Opacity oscillates per-star.</summary>
    public static void DrawTwinkle(ID2D1DeviceContext ctx, in Geom g, double time)
    {
        float r = g.OuterRadius + g.OuterIcon;
        int count = (int)Math.Round(104 * Polaris.Services.RenderProfile.SaturnDetailFactor);
        const double twinkleGate = 0.55;
        for (int i = 0; i < count; i++)
        {
            if (Hash01(i * 11.1) <= twinkleGate) continue;
            double ang = Hash01(i * 2.17) * Math.PI * 2;
            double rad = Math.Sqrt(Hash01(i * 5.31)) * r * 0.96;
            double px = g.Cx + Math.Cos(ang) * rad;
            double py = g.Cy + Math.Sin(ang) * rad * g.TiltY;
            double sz = 0.6 + 1.9 * Hash01(i * 7.7);
            double full = (60 + 150 * Hash01(i * 3.3)) / 255.0;
            double period = 1.4 + 2.2 * Hash01(i * 4.9);
            double phase = 2.0 * Hash01(i * 6.2);
            double s = 0.5 + 0.5 * Math.Sin((time / period + phase) * Math.PI * 2);
            double br = full * (0.3 + 0.7 * s);
            var e = new Ellipse(new Vector2((float)px, (float)py), (float)(sz / 2), (float)(sz / 2));
            using var brush = ctx.CreateSolidColorBrush(new Color4(1f, 1f, 250f / 255f, (float)br));
            ctx.FillEllipse(e, brush);
        }
    }

    // ===================================================================
    //  Dynamic ring-revolution cues (port of AddShimmer/AddSpoke/AddRingBlob/
    //  AddMoon). A continuously rotating axisymmetric ring looks identical
    //  frame-to-frame, so the revolution is conveyed by *local* features that
    //  sweep along the ring orbits at the inner/outer rates: bright shimmer
    //  arcs, Voyager-style radial spokes, particle density clumps and faint
    //  shepherd moons. Drawn vector each frame (NOT cached) — a few dozen
    //  primitives, so cheap — between the static ring bitmap and the planet.
    // ===================================================================

    private static readonly Rgb PaleB = new(0xCB, 0xBC, 0x95);
    private static readonly Rgb TanA = new(0xAA, 0x8E, 0x64);
    private static readonly Rgb SpokeCol = new(0x14, 0x10, 0x08);

    /// <param name="innerAngle">inner-orbit (B ring) revolution, degrees.</param>
    /// <param name="outerAngle">outer-orbit (A/F ring) revolution, degrees.</param>
    /// <param name="baseXform">scene transform to compose under (slide offset / identity).</param>
    public static void DrawDynamic(ID2D1DeviceContext ctx, in Geom g,
        double innerAngle, double outerAngle, Matrix3x2 baseXform)
    {
        DrawInnerCues(ctx, g, innerAngle, baseXform);
        DrawOuterCues(ctx, g, outerAngle, baseXform);
        ctx.Transform = baseXform;
    }

    /// <summary>Inner B-ring revolution cues (shimmer, spokes, density clumps),
    /// revolved by <paramref name="orbitDeg"/>. Authored relative to the centre so
    /// the dock can bake this flat (TiltY=1, orbit=0) into a bitmap and re-revolve it.</summary>
    public static void DrawInnerCues(ID2D1DeviceContext ctx, in Geom g, double orbitDeg, Matrix3x2 baseXform)
    {
        bool extra = Polaris.Services.RenderProfile.SaturnExtraDetail;
        float rB = g.InnerRadius;
        float rF = g.InnerRadius + g.RingStep;
        float planetR = g.PlanetR;

        const double Rplanet = 1.000;
        const double RBin = 1.526, RBmid = 1.739, RBout = 1.951;
        const double RF = 2.324;
        double kIn = (rB - planetR) / (RBmid - Rplanet);
        double bOutPx = planetR + (RBout - Rplanet) * kIn;
        double kOut = (rF - bOutPx) / (RF - RBout);
        double MapR(double rr) => rr <= RBout ? planetR + (rr - Rplanet) * kIn : bOutPx + (rr - RBout) * kOut;
        double rBinPx = MapR(RBin), rBoutPx = MapR(RBout);

        AddShimmer(ctx, g, rB, orbitDeg, PaleB, 0, 1.0, 0.30, baseXform);
        AddSpoke(ctx, g, rBinPx, rBoutPx, orbitDeg, 24, 7.0, 0.30, baseXform);
        AddSpoke(ctx, g, rBinPx, rBoutPx, orbitDeg, 256, 6.0, 0.26, baseXform);
        AddRingBlob(ctx, g, rB, orbitDeg, 60, rB * 0.16, rB * 0.05, Lighten(PaleB, 0.30), 0.22, baseXform);
        if (extra)
        {
            AddShimmer(ctx, g, rB, orbitDeg, PaleB, 168, 0.55, 0.24, baseXform);
            AddSpoke(ctx, g, rBinPx, rBoutPx, orbitDeg, 140, 5.0, 0.18, baseXform);
            AddRingBlob(ctx, g, rB, orbitDeg, 300, rB * 0.12, rB * 0.04, Lighten(PaleB, 0.26), 0.16, baseXform);
        }
    }

    /// <summary>Outer A/F-ring revolution cues (shimmer, spokes, clumps, shepherd
    /// moons), revolved by <paramref name="orbitDeg"/>. Bakeable like the inner cues.</summary>
    public static void DrawOuterCues(ID2D1DeviceContext ctx, in Geom g, double orbitDeg, Matrix3x2 baseXform)
    {
        if (g.OuterRadius <= g.InnerRadius + 0.5f) return;
        bool extra = Polaris.Services.RenderProfile.SaturnExtraDetail;
        float icon = g.Icon;
        float rB = g.InnerRadius;
        float rF = g.InnerRadius + g.RingStep;
        float planetR = g.PlanetR;

        const double Rplanet = 1.000;
        const double RBmid = 1.739, RBout = 1.951;
        const double RAin = 2.025, REncke = 2.214, RAout = 2.269;
        const double RF = 2.324;
        double kIn = (rB - planetR) / (RBmid - Rplanet);
        double bOutPx = planetR + (RBout - Rplanet) * kIn;
        double kOut = (rF - bOutPx) / (RF - RBout);
        double MapR(double rr) => rr <= RBout ? planetR + (rr - Rplanet) * kIn : bOutPx + (rr - RBout) * kOut;

        double rAmid = (MapR(RAin) + MapR(RAout)) / 2;
        double rAin = MapR(RAin), rAout = MapR(RAout);
        AddShimmer(ctx, g, rAmid, orbitDeg, PaleB, 0, 0.8, 0.26, baseXform);
        AddSpoke(ctx, g, rAin, rAout, orbitDeg, 80, 6.0, 0.20, baseXform);
        AddRingBlob(ctx, g, rAmid, orbitDeg, 150, rAmid * 0.13, rAmid * 0.04, Lighten(TanA, 0.30), 0.16, baseXform);
        if (extra)
        {
            AddShimmer(ctx, g, rAmid, orbitDeg, PaleB, 200, 0.45, 0.22, baseXform);
            AddSpoke(ctx, g, rAin, rAout, orbitDeg, 290, 5.0, 0.14, baseXform);
            AddRingBlob(ctx, g, rAmid, orbitDeg, 40, rAmid * 0.10, rAmid * 0.035, Lighten(TanA, 0.26), 0.12, baseXform);
        }

        double moonD = Math.Max(2.2, icon * 0.05);
        AddMoon(ctx, g, MapR(REncke), orbitDeg, 18, moonD, baseXform);              // Pan (Encke gap)
        AddMoon(ctx, g, MapR(RAout) - icon * 0.05, orbitDeg, 104, moonD * 0.85, baseXform); // Daphnis
        AddMoon(ctx, g, MapR(RAout) + icon * 0.09, orbitDeg, 200, moonD * 1.05, baseXform); // Atlas
        AddMoon(ctx, g, rF - icon * 0.10, orbitDeg, 286, moonD * 1.15, baseXform);  // Prometheus
        AddMoon(ctx, g, rF + icon * 0.10, orbitDeg, 330, moonD, baseXform);         // Pandora
    }

    /// <summary>Builds the transform that revolves a feature authored at angle 0
    /// (point (Cx+radius, Cy)) by <paramref name="totalDeg"/> about the centre, then
    /// squashes the circular orbit into the tilted ring ellipse.</summary>
    private static Matrix3x2 RevolveXform(in Geom g, double totalDeg, Matrix3x2 baseXform)
    {
        var c = new Vector2(g.Cx, g.Cy);
        return Matrix3x2.CreateRotation((float)(totalDeg * Math.PI / 180.0), c)
             * Matrix3x2.CreateScale(1f, g.TiltY, c)
             * baseXform;
    }

    /// <summary>Soft radial-gradient ellipse on the ring at <paramref name="totalDeg"/>,
    /// revolved and tilted into the ring plane.</summary>
    private static void AddRevolvedEllipse(ID2D1DeviceContext ctx, in Geom g, double radius,
        double totalDeg, double rx, double ry, Color4 core, Color4 fade, Matrix3x2 baseXform)
    {
        ctx.Transform = RevolveXform(g, totalDeg, baseXform);
        var ec = new Vector2(g.Cx + (float)radius, g.Cy);
        var ell = new Ellipse(ec, (float)rx, (float)ry);
        using var stops = Stops(ctx, (0f, core), (1f, fade));
        using var brush = ctx.CreateRadialGradientBrush(
            new RadialGradientBrushProperties { Center = ec, RadiusX = (float)rx, RadiusY = (float)ry }, stops);
        ctx.FillEllipse(ell, brush);
    }

    /// <summary>Shimmer arc: a bright crest flanked by a cool leading and warm
    /// trailing half for a subtle Doppler hint.</summary>
    private static void AddShimmer(ID2D1DeviceContext ctx, in Geom g, double radius,
        double orbitDeg, Rgb baseColor, double phaseDeg, double intensity, double arcSpan,
        Matrix3x2 baseXform)
    {
        double rx = Math.Max(26, radius * arcSpan);
        double ry = Math.Max(5, radius * 0.06);
        double a = orbitDeg + phaseDeg;
        // Warm trailing half.
        AddRevolvedEllipse(ctx, g, radius, a - 6, rx * 0.9, ry,
            A(Lighten(WarmShift(baseColor), 0.45), 0.18 * intensity), A(baseColor, 0), baseXform);
        // Cool leading half.
        AddRevolvedEllipse(ctx, g, radius, a + 6, rx * 0.9, ry,
            A(Lighten(CoolShift(baseColor), 0.55), 0.21 * intensity), A(baseColor, 0), baseXform);
        // Bright central crest.
        AddRevolvedEllipse(ctx, g, radius, a, rx, ry,
            A(Lighten(baseColor, 0.70), 0.35 * intensity), A(baseColor, 0), baseXform);
    }

    /// <summary>Tangentially-elongated brighter "density clump" that revolves with the ring.</summary>
    private static void AddRingBlob(ID2D1DeviceContext ctx, in Geom g, double radius,
        double orbitDeg, double phaseDeg, double rx, double ry, Rgb color, double alpha,
        Matrix3x2 baseXform)
    {
        AddRevolvedEllipse(ctx, g, radius, orbitDeg + phaseDeg, rx, ry,
            A(color, alpha), A(color, 0), baseXform);
    }

    /// <summary>Voyager/Cassini-style radial spoke: a soft dark wedge spanning
    /// <paramref name="rInner"/>..<paramref name="rOuter"/>, revolving with the ring.
    /// Soft angular flanks come from a tangential gradient brush (no per-frame blur).</summary>
    private static void AddSpoke(ID2D1DeviceContext ctx, in Geom g, double rInner, double rOuter,
        double orbitDeg, double phaseDeg, double widthDeg, double alpha, Matrix3x2 baseXform)
    {
        if (rOuter <= rInner) return;
        double half = widthDeg * Math.PI / 360.0;
        float cx = g.Cx, cy = g.Cy;
        Vector2 P(double rr, double ang) =>
            new((float)(cx + Math.Cos(ang) * rr), (float)(cy + Math.Sin(ang) * rr));

        using var geo = ctx.Factory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.BeginFigure(P(rInner, -half * 0.7), FigureBegin.Filled);
            sink.AddLine(P(rOuter, -half));
            sink.AddLine(P(rOuter, half));
            sink.AddLine(P(rInner, half * 0.7));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }

        // Tangential dark-in-the-middle gradient across the wedge's angular width.
        double hh = rOuter * Math.Sin(half);
        var start = new Vector2(g.Cx, (float)(g.Cy - hh));
        var end = new Vector2(g.Cx, (float)(g.Cy + hh));
        ctx.Transform = RevolveXform(g, orbitDeg + phaseDeg, baseXform);
        using var stops = Stops(ctx,
            (0f, A(SpokeCol, 0)), (0.5f, A(SpokeCol, alpha)), (1f, A(SpokeCol, 0)));
        using var brush = ctx.CreateLinearGradientBrush(
            new LinearGradientBrushProperties { StartPoint = start, EndPoint = end }, stops);
        ctx.FillGeometry(geo, brush);
    }

    /// <summary>Faint shepherd-moon point: a tiny bright core wrapped in a soft glow,
    /// revolved with the ring and tilted into the ring plane.</summary>
    private static void AddMoon(ID2D1DeviceContext ctx, in Geom g, double radius,
        double orbitDeg, double phaseDeg, double dia, Matrix3x2 baseXform)
    {
        ctx.Transform = RevolveXform(g, orbitDeg + phaseDeg, baseXform);
        var center = new Vector2(g.Cx + (float)radius, g.Cy);
        // Soft glow halo.
        var halo = new Ellipse(center, (float)(dia * 1.9), (float)(dia * 1.9));
        using (var stops = Stops(ctx,
            (0f, new Color4(1f, 0xF6 / 255f, 0xE2 / 255f, 95f / 255f)),
            (1f, new Color4(1f, 0xF6 / 255f, 0xE2 / 255f, 0f))))
        using (var brush = ctx.CreateRadialGradientBrush(
            new RadialGradientBrushProperties { Center = center, RadiusX = (float)(dia * 1.9), RadiusY = (float)(dia * 1.9) }, stops))
            ctx.FillEllipse(halo, brush);
        // Bright core.
        var core = new Ellipse(center, (float)(dia * 0.5), (float)(dia * 0.5));
        using (var br = ctx.CreateSolidColorBrush(new Color4(1f, 0xFB / 255f, 0xF0 / 255f, 200f / 255f)))
            ctx.FillEllipse(core, br);
    }

    /// <summary>Shifts a colour slightly toward cool (blue) for the leading edge.</summary>
    private static Rgb CoolShift(Rgb c) => new(
        (byte)Math.Clamp(c.R - 14, 0, 255),
        (byte)Math.Clamp(c.G - 4, 0, 255),
        (byte)Math.Clamp(c.B + 18, 0, 255));

    /// <summary>Shifts a colour slightly toward warm (amber) for the trailing edge.</summary>
    private static Rgb WarmShift(Rgb c) => new(
        (byte)Math.Clamp(c.R + 16, 0, 255),
        (byte)Math.Clamp(c.G + 4, 0, 255),
        (byte)Math.Clamp(c.B - 16, 0, 255));

    // ===================================================================
    //  Centre planet (port of DrawCenterButton), split for bitmap caching:
    //   - DrawPlanetBody:  static lit-sphere gradient (bottom layer)
    //   - DrawPlanetDisc:  rotating gas-giant bands + wind streaks + hexagon
    //   - DrawPlanetShade: static limb / terminator / highlight / rim (top)
    // ===================================================================
    private static readonly Rgb Amber = new(0xE2, 0xBE, 0x82);
    private static readonly Rgb AmberDark = new(0x7A, 0x5C, 0x36);
    private static readonly Rgb AmberLight = new(0xFC, 0xEF, 0xCC);

    private static void DrawPlanetBody(ID2D1DeviceContext ctx, in Geom g)
    {
        float size = g.PlanetR * 2f;
        float r = g.PlanetR;
        var c = new Vector2(g.Cx, g.Cy);
        using (var stops = Stops(ctx,
            (0f, A(AmberLight, 1.0)),
            (0.5f, A(Amber, 1.0)),
            (0.82f, A(Darken(Amber, 0.25), 1.0)),
            (1f, A(AmberDark, 1.0))))
        using (var body = ctx.CreateRadialGradientBrush(
            new RadialGradientBrushProperties
            {
                Center = c,
                GradientOriginOffset = new Vector2(-0.14f * size, -0.20f * size),
                RadiusX = r * 1.44f,
                RadiusY = r * 1.44f,
            }, stops))
            ctx.FillEllipse(new Ellipse(c, r, r), body);
    }

    /// <summary>The spinning polar disc (bands + streaks + hexagon). Authored at
    /// rest; the caller bakes this into a bitmap and rotates the bitmap by the
    /// spin angle each frame.</summary>
    public static void DrawPlanetDisc(ID2D1DeviceContext ctx, in Geom g)
    {
        float size = g.PlanetR * 2f;
        float r = g.PlanetR;
        var c = new Vector2(g.Cx, g.Cy);
        DrawPlanetBands(ctx, c, r, Amber, AmberDark, AmberLight, size);
        DrawWindStreaks(ctx, c, r, Amber, size);
        DrawHexagon(ctx, c, r);
    }

    public static void DrawPlanetShade(ID2D1DeviceContext ctx, in Geom g)
    {
        float size = g.PlanetR * 2f;
        float r = g.PlanetR;
        var c = new Vector2(g.Cx, g.Cy);

        // Limb darkening hugging the edge.
        using (var stops = Stops(ctx,
            (0f, new Color4(0, 0, 0, 0)),
            (0.74f, new Color4(0, 0, 0, 0)),
            (0.92f, new Color4(0x16 / 255f, 0x0D / 255f, 0x04 / 255f, 110 / 255f)),
            (1f, new Color4(0x0C / 255f, 0x07 / 255f, 0x02 / 255f, 235 / 255f))))
        using (var limb = ctx.CreateRadialGradientBrush(
            new RadialGradientBrushProperties { Center = c, RadiusX = r, RadiusY = r }, stops))
            ctx.FillEllipse(new Ellipse(c, r, r), limb);

        // Terminator shadow lower-right.
        var sc = new Vector2(g.Cx + 0.18f * size, g.Cy + 0.22f * size);
        using (var stops = Stops(ctx,
            (0f, new Color4(0, 0, 0, 150 / 255f)),
            (0.5f, new Color4(0, 0, 0, 40 / 255f)),
            (0.85f, new Color4(0, 0, 0, 0))))
        using (var sh = ctx.CreateRadialGradientBrush(
            new RadialGradientBrushProperties { Center = sc, RadiusX = r * 0.85f, RadiusY = r * 0.85f }, stops))
            ctx.FillEllipse(new Ellipse(c, r, r), sh);

        // Specular highlight upper-left.
        var hc = new Vector2(g.Cx - 0.20f * size, g.Cy - 0.28f * size);
        using (var stops = Stops(ctx,
            (0f, new Color4(1f, 1f, 1f, 150 / 255f)),
            (0.35f, new Color4(1f, 1f, 1f, 30 / 255f)),
            (0.6f, new Color4(1f, 1f, 1f, 0f))))
        using (var hl = ctx.CreateRadialGradientBrush(
            new RadialGradientBrushProperties { Center = hc, RadiusX = r * 0.6f, RadiusY = r * 0.6f }, stops))
            ctx.FillEllipse(new Ellipse(c, r * 0.9f, r * 0.9f), hl);

        // Thin dark rim.
        using (var rim = ctx.CreateSolidColorBrush(new Color4(0x12 / 255f, 0x0A / 255f, 0x03 / 255f, 90 / 255f)))
            ctx.DrawEllipse(new Ellipse(c, r, r), rim, 1.0f);
    }

    private static void DrawPlanetBands(ID2D1DeviceContext ctx, Vector2 c, float r,
        Rgb amber, Rgb amberDark, Rgb amberLight, float size)
    {
        const int bandCount = 11;
        var bandEdges = new double[bandCount + 1];
        for (int i = 0; i <= bandCount; i++) bandEdges[i] = r * (i / (double)bandCount);

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
        for (int b = bandCount - 1; b >= 0; b--)
        {
            double s = 0.5 + 0.5 * Math.Sin(b * 1.45 + 0.6);
            Rgb shade = s < 0.5 ? Lerp(amberDark, amber, s * 2.0) : Lerp(amber, amberLight, (s - 0.5) * 2.0);
            double a = (70 + 28 * Math.Sin(b * 1.9)) / 255.0;

            using var geo = ctx.Factory.CreatePathGeometry();
            using (var sink = geo.Open())
            {
                bool started = false;
                for (int k = 0; k <= beltSeg; k++)
                {
                    double th = k / (double)beltSeg * Math.PI * 2;
                    double rr = EdgeRadius(b + 1, th);
                    var p = new Vector2((float)(c.X + Math.Cos(th) * rr), (float)(c.Y + Math.Sin(th) * rr));
                    if (!started) { sink.BeginFigure(p, FigureBegin.Filled); started = true; }
                    else sink.AddLine(p);
                }
                for (int k = beltSeg; k >= 0; k--)
                {
                    double th = k / (double)beltSeg * Math.PI * 2;
                    double rr = EdgeRadius(b, th);
                    sink.AddLine(new Vector2((float)(c.X + Math.Cos(th) * rr), (float)(c.Y + Math.Sin(th) * rr)));
                }
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }
            using var br = ctx.CreateSolidColorBrush(A(shade, a));
            ctx.FillGeometry(geo, br);
        }
    }

    private static void DrawWindStreaks(ID2D1DeviceContext ctx, Vector2 c, float r, Rgb amber, float size)
    {
        const int windStreaks = 30;
        for (int sN = 0; sN < windStreaks; sN++)
        {
            double rad0 = r * (0.14 + 0.82 * Hash01(sN * 1.7 + 0.3));
            double a0 = Hash01(sN * 2.9) * Math.PI * 2;
            double arc = 0.5 + 1.9 * Hash01(sN * 4.1);
            bool light = Hash01(sN * 3.7) > 0.5;
            Rgb scol = light ? Lighten(amber, 0.30) : Darken(amber, 0.30);
            double sa = (24 + 38 * Hash01(sN * 6.1)) / 255.0;
            double amp = size * (0.004 + 0.011 * Hash01(sN * 5.3));

            using var geo = ctx.Factory.CreatePathGeometry();
            using (var sink = geo.Open())
            {
                const int ss = 44;
                bool started = false;
                for (int k = 0; k <= ss; k++)
                {
                    double th = a0 + arc * (k / (double)ss);
                    double rr = rad0 + amp * Math.Sin(th * 9 + sN) + amp * 0.5 * Math.Sin(th * 17 - sN);
                    var p = new Vector2((float)(c.X + Math.Cos(th) * rr), (float)(c.Y + Math.Sin(th) * rr));
                    if (!started) { sink.BeginFigure(p, FigureBegin.Hollow); started = true; }
                    else sink.AddLine(p);
                }
                sink.EndFigure(FigureEnd.Open);
                sink.Close();
            }
            float sw = (float)Math.Max(1.0, size * (0.005 + 0.009 * Hash01(sN * 7.7)));
            using var br = ctx.CreateSolidColorBrush(A(scol, sa));
            ctx.DrawGeometry(geo, br, sw);
        }
    }

    private static void DrawHexagon(ID2D1DeviceContext ctx, Vector2 c, float r)
    {
        double hexR = r * 0.16;
        var hexBase = new Rgb(0x66, 0x6E, 0x72);
        var hexLight = new Rgb(0x93, 0x9A, 0x98);
        var hexDark = new Rgb(0x45, 0x49, 0x4C);

        using var hexGeo = ctx.Factory.CreatePathGeometry();
        using (var sink = hexGeo.Open())
        {
            for (int k = 0; k < 6; k++)
            {
                double ang = -Math.PI / 2 + k * Math.PI / 3;
                var p = new Vector2((float)(c.X + Math.Cos(ang) * hexR), (float)(c.Y + Math.Sin(ang) * hexR));
                if (k == 0) sink.BeginFigure(p, FigureBegin.Filled);
                else sink.AddLine(p);
            }
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }

        // Base storm fill + rim (faint, tinting the pole), overall opacity 0.62.
        using (var fill = ctx.CreateSolidColorBrush(A(hexBase, 0.62)))
            ctx.FillGeometry(hexGeo, fill);

        double HexEdge(double baseR, double theta, double seed)
        {
            double amp = hexR * 0.10;
            return Math.Clamp(baseR
                + amp * Math.Sin(theta * 3 + seed * 1.7)
                + amp * 0.6 * Math.Sin(theta * 6 - seed * 2.1)
                + amp * 0.4 * (Hash01(seed * 5.1 + Math.Floor(theta * 5)) - 0.5), 0, hexR);
        }

        // Wavy concentric storm bands inside the polar hexagon (overall ~0.62).
        const int hexBands = 4;
        for (int b = hexBands; b >= 1; b--)
        {
            double baseOut = hexR * (b / (double)hexBands);
            double baseIn = hexR * ((b - 1) / (double)hexBands);
            double s = 0.5 + 0.5 * Math.Sin(b * 1.7);
            Rgb shade = Lerp(hexDark, hexLight, s);
            double a = 0.62 * (120 + 70 * Math.Sin(b * 1.3)) / 255.0;

            using var geo = ctx.Factory.CreatePathGeometry();
            using (var sink = geo.Open())
            {
                const int seg = 70;
                bool started = false;
                for (int k = 0; k <= seg; k++)
                {
                    double th = k / (double)seg * Math.PI * 2;
                    double rr = HexEdge(baseOut, th, b + 1);
                    var p = new Vector2((float)(c.X + Math.Cos(th) * rr), (float)(c.Y + Math.Sin(th) * rr));
                    if (!started) { sink.BeginFigure(p, FigureBegin.Filled); started = true; }
                    else sink.AddLine(p);
                }
                for (int k = seg; k >= 0; k--)
                {
                    double th = k / (double)seg * Math.PI * 2;
                    double rr = HexEdge(baseIn, th, b);
                    sink.AddLine(new Vector2((float)(c.X + Math.Cos(th) * rr), (float)(c.Y + Math.Sin(th) * rr)));
                }
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }
            using var br = ctx.CreateSolidColorBrush(A(shade, a));
            ctx.FillGeometry(geo, br);
        }

        using (var stroke = ctx.CreateSolidColorBrush(A(new Rgb(0xAE, 0xB2, 0xA8), 0.62 * (180 / 255.0))))
            ctx.DrawGeometry(hexGeo, stroke, (float)Math.Max(1.0, r * 2 * 0.012));
    }
}
