using System.Windows;

namespace Polaris.Views;

public partial class LeftDockWindow
{
    /// <summary>Adds the orbiting cool light over the side-dock glass slab (rect in
    /// PanelCanvas coords; radius = the slab's corner radius). Shared with the main
    /// dock via <see cref="GlassOrbitLight"/>; z-index -3 sits above the glass
    /// chrome and below the icons.</summary>
    private void BuildGlassOrbitLight(Rect slab, double radius)
        => GlassOrbitLight.Build(PanelCanvas, slab, radius, GIcon, zIndex: -3);
}
