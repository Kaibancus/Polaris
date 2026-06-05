using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using DesktopPanel.Models;

namespace DesktopPanel.Services;

/// <summary>
/// A panel theme bundles together the icon <b>layout</b> and the panel
/// <b>background / animation</b>. Switching the theme (via the settings combo)
/// changes how icons are arranged and what is drawn behind them.
///
/// Global effects — the icon hover zoom and the running-app flowing glow — live
/// on <c>RadialIcon</c> and are intentionally <b>not</b> part of a theme, so they
/// apply uniformly no matter which theme is active.
/// </summary>
public abstract class PanelTheme
{
    /// <summary>Stable identifier persisted in settings.</summary>
    public abstract string Id { get; }

    /// <summary>Human-readable name shown in the settings theme picker.</summary>
    public abstract string DisplayName { get; }

    /// <summary>True for the Saturn theme, which draws the planet, rings, ring
    /// revolution cues and uses the concentric-ring icon layout.</summary>
    public virtual bool IsSaturn => false;

    /// <summary>Brush painted behind everything (the overlay window background).
    /// Transparent lets the desktop show through (Saturn theme); an opaque brush
    /// gives a solid backdrop (test theme).</summary>
    public virtual Brush WindowBackground => Brushes.Transparent;

    /// <summary>
    /// Computes the centre point of each icon slot for <paramref name="count"/>
    /// icons around <paramref name="center"/>. <paramref name="outerReach"/>
    /// returns the farthest layout radius (used for the drag-out delete zone).
    /// Saturn returns an empty list here — its ring layout is produced by the
    /// host so the drag-reorder math stays in one place.
    /// </summary>
    public abstract IReadOnlyList<Point> ComputeSlots(
        int count, Point center, AppSettings settings, out double outerReach);
}

/// <summary>The default "土星环" theme: concentric-ring layout plus the animated
/// Saturn planet and ring system. Layout is produced by the host (RadialWindow),
/// so <see cref="ComputeSlots"/> is unused for this theme.</summary>
public sealed class SaturnRingTheme : PanelTheme
{
    public override string Id => "saturn";
    public override string DisplayName => "土星环";
    public override bool IsSaturn => true;
    public override Brush WindowBackground => Brushes.Transparent;

    public override IReadOnlyList<Point> ComputeSlots(
        int count, Point center, AppSettings settings, out double outerReach)
    {
        // Saturn uses the host's ring layout; never called for this theme.
        outerReach = 0;
        return Array.Empty<Point>();
    }
}

/// <summary>Catalog of available themes.</summary>
public static class ThemeRegistry
{
    public static IReadOnlyList<PanelTheme> All { get; } = new PanelTheme[]
    {
        new SaturnRingTheme(),
    };

    /// <summary>Resolves a theme by id, falling back to the Saturn theme.</summary>
    public static PanelTheme Get(string? id)
    {
        foreach (var t in All)
            if (string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
                return t;
        return All[0];
    }
}
