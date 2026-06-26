namespace Polaris.Views;

/// <summary>Common surface for the drag ghost overlay, implemented by the GPU/
/// DirectComposition <see cref="DragGhostWindowGpu"/>.</summary>
internal interface IDragGhost
{
    /// <summary>Centres the ghost on a virtual-desktop point (in DIPs).</summary>
    void MoveCenterTo(double dipX, double dipY);

    /// <summary>Drag feedback opacity (fades while over the delete zone).</summary>
    double GhostOpacity { set; }

    /// <summary>Reuses the ghost for a new dragged icon without recreating its GPU host.</summary>
    void SetSnapshot(System.Windows.Media.ImageSource snapshot, double dipWidth, double dipHeight);

    void Show();

    /// <summary>Hides the ghost between drags (the host stays alive for reuse).</summary>
    void Hide();

    void Close();
}
