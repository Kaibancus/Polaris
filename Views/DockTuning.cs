namespace Polaris.Views;

// Single source of truth for dock interaction-feel constants that are shared
// between the event-driven hover zoom (RadialIcon / SpreadNeighbours) and the
// continuous cursor-distance magnification loop (RadialWindow.Magnify). These
// values previously lived as separate literals in several files with comments
// asking the reader to keep them in sync by hand; centralizing them here removes
// that drift risk while keeping the exact same numbers (behavior unchanged).
internal static class DockTuning
{
    /// <summary>Peak magnification an icon reaches directly under the pointer.
    /// Shared so the per-icon hover zoom and the continuous magnify loop reach the
    /// same maximum size.</summary>
    public const double HoverScale = 1.7;

    /// <summary>Neighbour-spread "push" distance as a multiple of the icon size:
    /// how far the icon next to the hovered/focal one is shoved aside.</summary>
    public const double SpreadPush = 0.75;

    /// <summary>Neighbour-spread influence radius as a multiple of the icon size:
    /// icons further than this from the hovered/focal one are not pushed.</summary>
    public const double SpreadInfluence = 2.7;
}
