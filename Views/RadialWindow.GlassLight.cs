using System.Windows;

namespace Polaris.Views;

public partial class RadialWindow
{
    /// <summary>Adds the orbiting cool light over the glass slab (left, top, w, h
    /// in PanelCanvas coords; radius = the slab's corner radius). Shared with the
    /// side dock via <see cref="GlassOrbitLight"/>; z-index 2 sits above the slab
    /// glow (1) and below the icons.</summary>
    private void BuildGlassOrbitLight(double left, double top, double w, double h, double radius)
        => GlassOrbitLight.Build(
            PanelCanvas, new Rect(left, top, w, h), radius, EffectiveIconSize, zIndex: 2);
}
