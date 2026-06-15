using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Polaris.Views;

// Liquid-glass ambient light for the side dock: a cool light source orbits the
// slab's geometric centre clockwise, one revolution per minute, casting its glow
// inward so the lit side of the glass drifts slowly around. Mirrors the main
// dock — one GPU-cached gradient sprite, only rotated, clipped to the slab and
// capped at the glass loop's frame rate, so low-end machines stay smooth.
public partial class LeftDockWindow
{
    private const double GlassLightOrbitSeconds = 60.0;

    /// <summary>Adds the orbiting cool light over the side-dock glass slab
    /// (rect in PanelCanvas coords; radius = the slab's corner radius).</summary>
    private void BuildGlassOrbitLight(Rect slab, double radius)
    {
        double cx = slab.X + slab.Width / 2.0;
        double cy = slab.Y + slab.Height / 2.0;

        double orbitR = Math.Max(slab.Width, slab.Height) * 0.5 + GIcon * 1.4;
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
                    new GradientStop(Color.FromArgb(0x32, 0xCF, 0xEC, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x1A, 0x76, 0xC4, 0xFF), 0.34),
                    new GradientStop(Color.FromArgb(0x07, 0x4C, 0x9E, 0xF0), 0.62),
                    new GradientStop(Color.FromArgb(0x00, 0x3A, 0x86, 0xE0), 1.0),
                },
            },
            CacheMode = new BitmapCache { RenderAtScale = 0.5 },
        };
        Canvas.SetLeft(lamp, cx - lampD / 2.0);
        Canvas.SetTop(lamp, cy - orbitR - lampD / 2.0);

        var rot = new RotateTransform(0, cx, cy);
        var orbit = new Canvas { IsHitTestVisible = false, RenderTransform = rot };
        orbit.Children.Add(lamp);

        var clipLayer = new Canvas
        {
            IsHitTestVisible = false,
            Clip = new RectangleGeometry(slab, radius, radius),
        };
        clipLayer.Children.Add(orbit);
        Panel.SetZIndex(clipLayer, -3);   // above the glass chrome, below the icons
        PanelCanvas.Children.Add(clipLayer);

        var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(GlassLightOrbitSeconds))
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
