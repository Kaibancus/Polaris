using System;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Polaris.Services.Gpu;

/// <summary>Reusable Direct2D drawing of the liquid-glass (and Saturn dark) slab,
/// mirroring <c>Services.GlassChrome</c>'s layer stack: soft drop shadow, clear-glass
/// radial body, frost veil, edge vignette, centre bloom and a luminous rim. Tuned in
/// the GPU spike against the real WPF dock. Shared by the GPU side dock / main dock
/// ports. All layers are scaled by <paramref name="opacity"/> (= 1 - PanelTransparency).</summary>
internal static class GlassSlab
{
    private static Color4 C(byte a, byte r, byte g, byte b, float op) =>
        new(r / 255f, g / 255f, b / 255f, a / 255f * op);

    private static RadialGradientBrushProperties Radial(float cx, float cy, float rx, float ry) =>
        new() { Center = new Vector2(cx, cy), GradientOriginOffset = new Vector2(0, 0), RadiusX = rx, RadiusY = ry };

    private static ID2D1GradientStopCollection Stops(ID2D1DeviceContext ctx, params (float pos, Color4 col)[] s)
    {
        var arr = new GradientStop[s.Length];
        for (int i = 0; i < s.Length; i++) arr[i] = new GradientStop { Position = s[i].pos, Color = s[i].col };
        return ctx.CreateGradientStopCollection(arr);
    }

    /// <summary>Draws the liquid-glass slab. Caller is inside BeginDraw/EndDraw.
    /// <paramref name="shadowExtent"/> = soft drop-shadow reach in px (0 = none, e.g.
    /// for an edge-flush side dock whose shadow would fall off-screen anyway).</summary>
    public static void DrawGlass(ID2D1DeviceContext ctx, float x, float y, float w, float h,
        float radius, float opacity, float frostStrength, float shadowExtent = 0f)
    {
        float op = Math.Clamp(opacity, 0f, 1f);
        var slab = new RoundedRectangle { Rect = new Rect(x, y, w, h), RadiusX = radius, RadiusY = radius };
        float cx = x + w / 2f, cy = y + h / 2f;

        // Optional soft drop shadow (centred faint halo). Only for floating slabs;
        // an edge-flush dock passes 0 so no halo bleeds past its border.
        if (shadowExtent > 0.5f)
        {
            int rings = 8;
            for (int i = rings; i >= 1; i--)
            {
                float grow = shadowExtent * i / rings;
                var sRect = new RoundedRectangle
                {
                    Rect = new Rect(x - grow, y - grow + shadowExtent * 0.4f, w + grow * 2, h + grow * 2),
                    RadiusX = radius + grow,
                    RadiusY = radius + grow,
                };
                using var sb = ctx.CreateSolidColorBrush(C(0x06, 0x06, 0x0B, 0x16, 1f));
                ctx.FillRoundedRectangle(sRect, sb);
            }
        }

        // Body: clear-glass radial (centre-bright cool -> clearer rim).
        using (var body = ctx.CreateRadialGradientBrush(Radial(cx, cy, w * 0.72f, h * 0.72f),
            Stops(ctx, (0f, C(0x2E, 0xDA, 0xEC, 0xFF, op)), (0.48f, C(0x1A, 0xEA, 0xF2, 0xFF, op)),
                       (0.8f, C(0x12, 0xCE, 0xDE, 0xF2, op)), (1f, C(0x0A, 0xAE, 0xC2, 0xDC, op)))))
            ctx.FillRoundedRectangle(slab, body);

        // Frost veil: milky diffusion centred upper-third, peak = frostStrength*0xC8.
        byte Peak(double mul) => (byte)Math.Clamp(frostStrength * 0xC8 * mul, 0, 255);
        using (var frost = ctx.CreateRadialGradientBrush(Radial(cx, y + h * 0.34f, w * 0.95f, h * 1.05f),
            Stops(ctx, (0f, C(Peak(1.0), 0xFF, 0xFF, 0xFF, op)), (0.55f, C(Peak(0.92), 0xF2, 0xF6, 0xFF, op)),
                       (1f, C(Peak(0.86), 0xE4, 0xEC, 0xF8, op)))))
            ctx.FillRoundedRectangle(slab, frost);

        // Edge vignette.
        using (var edge = ctx.CreateRadialGradientBrush(Radial(cx, cy, w * 0.72f, h * 0.72f),
            Stops(ctx, (0f, C(0x00, 0x0A, 0x12, 0x20, op)), (0.6f, C(0x00, 0x0A, 0x12, 0x20, op)),
                       (1f, C(0x20, 0x0A, 0x12, 0x20, op)))))
            ctx.FillRoundedRectangle(slab, edge);

        // Centre specular bloom.
        using (var bloom = ctx.CreateRadialGradientBrush(Radial(cx, cy, w * 0.5f, h * 0.62f),
            Stops(ctx, (0f, C(0x32, 0xFF, 0xFF, 0xFF, op)), (0.5f, C(0x12, 0xFF, 0xFF, 0xFF, op)),
                       (1f, C(0x00, 0xFF, 0xFF, 0xFF, op)))))
            ctx.FillRoundedRectangle(
                new RoundedRectangle { Rect = new Rect(x + w * 0.07f, cy - h * 0.275f, w * 0.86f, h * 0.55f), RadiusX = w * 0.43f, RadiusY = w * 0.43f },
                bloom);

        // Luminous rim hairline.
        using (var rim = ctx.CreateLinearGradientBrush(
            new LinearGradientBrushProperties { StartPoint = new Vector2(x, y), EndPoint = new Vector2(x + w, y + h) },
            Stops(ctx, (0f, C(0xF2, 0xFF, 0xFF, 0xFF, op)), (0.4f, C(0x59, 0xFF, 0xFF, 0xFF, op)),
                       (0.62f, C(0x30, 0xC8, 0xDA, 0xF5, op)), (1f, C(0x9C, 0xFF, 0xFF, 0xFF, op)))))
            ctx.DrawRoundedRectangle(slab, rim, 1.1f);
    }

    /// <summary>Draws the Saturn dark dock slab (near-black smoked glass).</summary>
    public static void DrawDark(ID2D1DeviceContext ctx, float x, float y, float w, float h, float radius)
    {
        var slab = new RoundedRectangle { Rect = new Rect(x, y, w, h), RadiusX = radius, RadiusY = radius };
        float cx = x + w / 2f, cy = y + h * 0.42f;
        using (var body = ctx.CreateRadialGradientBrush(Radial(cx, cy, w * 0.62f, h * 0.62f),
            Stops(ctx, (0f, C(0xFF, 0x05, 0x06, 0x0C, 1f)), (0.72f, C(0xFF, 0x02, 0x03, 0x07, 1f)),
                       (1f, C(0xFF, 0x00, 0x00, 0x00, 1f)))))
            ctx.FillRoundedRectangle(slab, body);
        using (var rim = ctx.CreateLinearGradientBrush(
            new LinearGradientBrushProperties { StartPoint = new Vector2(x, y), EndPoint = new Vector2(x + w, y + h) },
            Stops(ctx, (0f, C(0x66, 0x16, 0x18, 0x1E, 1f)), (0.4f, C(0x22, 0x0A, 0x0B, 0x10, 1f)),
                       (0.62f, C(0x14, 0x05, 0x06, 0x0A, 1f)), (1f, C(0x4A, 0x00, 0x00, 0x00, 1f)))))
            ctx.DrawRoundedRectangle(slab, rim, 1.1f);
    }
}
