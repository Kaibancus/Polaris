using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Polaris.Views;

// Liquid-glass ambient light shared by both docks: a single cool light source
// sits just outside the glass slab and casts its glow inward toward the centre.
// The source orbits the centre clockwise, one revolution per minute, so the lit
// side of the glass drifts slowly around. Built for low-end machines — the glow
// is one pre-rendered, GPU-cached sprite that is only rotated (no per-frame
// re-raster), clipped to the slab and capped at the glass loop's frame rate.
internal static class GlassOrbitLight
{
    private const double OrbitSeconds = 60.0;

    /// <summary>Adds the orbiting cool light over a glass slab.</summary>
    /// <param name="panel">Canvas the light layer is added to (slab coords).</param>
    /// <param name="slab">The glass slab rect in <paramref name="panel"/> coords.</param>
    /// <param name="radius">The slab's corner radius.</param>
    /// <param name="iconSize">Icon size used to size the lamp's orbit / glow reach.</param>
    /// <param name="zIndex">Z-order for the clip layer (above the slab glow, below icons).</param>
    public static void Build(Canvas panel, Rect slab, double radius, double iconSize, int zIndex)
    {
        double cx = slab.X + slab.Width / 2.0;
        double cy = slab.Y + slab.Height / 2.0;

        // The lamp rides well outside the slab's longer half-extent and is large
        // and soft enough that its glow still reaches the dock centre.
        double orbitR = Math.Max(slab.Width, slab.Height) * 0.5 + iconSize * 1.4;
        double lampD = orbitR * 2.6;

        var lamp = new Ellipse
        {
            Width = lampD,
            Height = lampD,
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x32, 0xCF, 0xEC, 0xFF), 0.0),   // cool-white core
                    new GradientStop(Color.FromArgb(0x1A, 0x76, 0xC4, 0xFF), 0.34),  // cyan-blue body
                    new GradientStop(Color.FromArgb(0x07, 0x4C, 0x9E, 0xF0), 0.62),
                    new GradientStop(Color.FromArgb(0x00, 0x3A, 0x86, 0xE0), 1.0),
                },
            },
            // Bake the soft gradient once at half resolution: it is only rotated,
            // so a cached low-res texture stays smooth while costing little memory.
            CacheMode = new BitmapCache { RenderAtScale = 0.5 },
        };
        Canvas.SetLeft(lamp, cx - lampD / 2.0);
        Canvas.SetTop(lamp, cy - orbitR - lampD / 2.0);

        // Rotate the lamp about the dock centre. Positive angle is clockwise on
        // screen (y points down), which is the direction asked for.
        var rot = new RotateTransform(0, cx, cy);
        var orbit = new Canvas { IsHitTestVisible = false, RenderTransform = rot };
        orbit.Children.Add(lamp);

        // Clip the glow to the glass slab so only the light landing ON the glass
        // shows; the source itself sweeps round outside, unseen. The clip lives on
        // a static (un-rotated) parent so it stays aligned to the slab.
        var clipLayer = new Canvas
        {
            IsHitTestVisible = false,
            Clip = new RectangleGeometry(slab, radius, radius),
        };
        clipLayer.Children.Add(orbit);
        Panel.SetZIndex(clipLayer, zIndex);
        panel.Children.Add(clipLayer);

        var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(OrbitSeconds))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        // A 60-second revolution is ~0.1°/frame even at 30 fps, so cap this very
        // slow drift mode-independently — running it at the 60 fps loop rate (High)
        // would just double its full-frame layered-window uploads for no visible
        // gain. (In low-perf mode the orbit light is skipped entirely upstream.)
        Timeline.SetDesiredFrameRate(spin, Math.Min(App.GlassLoopFrameRate, App.SlowDriftFrameRate));
        rot.BeginAnimation(RotateTransform.AngleProperty, spin);
    }
}
