namespace Polaris.Views;

/// <summary>Common surface for the Saturn notch clock so the WPF
/// (<see cref="NotchClockWindow"/>) and GPU/DirectComposition
/// (<see cref="NotchClockWindowGpu"/>) implementations are interchangeable for the
/// per-window GPU migration.</summary>
internal interface INotchClock
{
    /// <summary>Positions the notch centred on the active monitor's top edge (or
    /// its bottom edge when <paramref name="atBottom"/> is true) and shows it.</summary>
    void ShowNotch(bool atBottom);

    void HideNotch();
}
