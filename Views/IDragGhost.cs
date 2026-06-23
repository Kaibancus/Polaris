namespace Polaris.Views;

/// <summary>Common surface for the drag ghost overlay, implemented by the GPU/
/// DirectComposition <see cref="DragGhostWindowGpu"/>.</summary>
internal interface IDragGhost
{
    /// <summary>Centres the ghost on a virtual-desktop point (in DIPs).</summary>
    void MoveCenterTo(double dipX, double dipY);

    /// <summary>Drag feedback opacity (fades while over the delete zone).</summary>
    double GhostOpacity { set; }

    void Show();
    void Close();
}
