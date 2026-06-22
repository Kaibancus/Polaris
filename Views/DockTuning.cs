namespace Polaris.Views;

using System;

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

    /// <summary>Maps the raw panel-transparency setting (0 = opaque … 1 = invisible)
    /// to a DENSER effective opacity for the <b>Saturn</b> black panel (disc / slab /
    /// flame) only — the liquid-glass theme keeps the plain <c>1 − transparency</c>.
    /// The user wanted Saturn more solid: a 50% transparency setting should read like
    /// the old 30% (opacity 0.7). A power curve hits that anchor (0.5 → 0.3 effective
    /// transparency) while preserving the slider extremes (0 → fully opaque, 1 → fully
    /// transparent). opacity = 1 − raw^k, with k so that 0.5^k = 0.3 (k = ln0.3 / ln0.5
    /// ≈ 1.737).</summary>
    public const double SaturnDensityExponent = 1.737;
    public static float SaturnPanelOpacity(double rawTransparency)
    {
        double t = Math.Clamp(rawTransparency, 0.0, 1.0);
        return (float)(1.0 - Math.Pow(t, SaturnDensityExponent));
    }
}
