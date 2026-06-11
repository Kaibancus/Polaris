using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Polaris.Models;

namespace Polaris.Services;

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

    /// <summary>True if the theme supports the generic free grid drag-reorder
    /// (with neighbour "push aside" animation). Saturn uses its own ring-based
    /// reorder via the host instead.</summary>
    public virtual bool SupportsGridReorder => false;

    /// <summary>True if the host should draw a rounded rectangular "liquid glass"
    /// panel behind the icons.</summary>
    public virtual bool ShowGlassPanel => false;

    /// <summary>Maximum number of icons this theme can display. Adding beyond
    /// this is rejected. Saturn (and most themes) impose no limit.</summary>
    public virtual int MaxIcons => int.MaxValue;

    /// <summary>Default panel transparency (0.0 opaque – 1.0 invisible) applied
    /// when the user first switches to this theme (before any customisation).</summary>
    public virtual double DefaultTransparency => 0.10;

    /// <summary>Default icon diameter (device-independent px) for this theme.</summary>
    public virtual double DefaultIconSize => 56;

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
    public override double DefaultTransparency => 0.10;
    // 50% of the settings icon-size slider range [40, 96]: 40 + 0.50 * 56 = 68.
    public override double DefaultIconSize => 68;

    public override IReadOnlyList<Point> ComputeSlots(
        int count, Point center, AppSettings settings, out double outerReach)
    {
        // Saturn uses the host's ring layout; never called for this theme.
        outerReach = 0;
        return Array.Empty<Point>();
    }
}

/// <summary>The "液态玻璃" theme: a translucent rounded-rectangle frosted-glass
/// panel with the icons laid out in a 7-column grid. The grid shows 4 rows
/// (7×4) at a time; once the icons overflow that, the surplus rows scroll into
/// view (vertical scrollbar / two-finger trackpad). Supports free drag-reorder
/// with the neighbour "push aside" animation. The glass panel's translucency is
/// driven by the user's panel-opacity setting.</summary>
public sealed class LiquidGlassTheme : PanelTheme
{
    public const int Columns = 7;

    /// <summary>Number of rows shown at once before the grid begins to scroll.</summary>
    public const int VisibleRows = 4;

    /// <summary>Maximum number of rows the grid can grow to (scrolling reveals
    /// the rows beyond <see cref="VisibleRows"/>).</summary>
    public const int MaxRows = 12;

    /// <summary>Maximum icons this theme can display (a full 7×12 grid).</summary>
    public const int Capacity = Columns * MaxRows;

    /// <summary>Number of grid rows needed to hold <paramref name="count"/> icons,
    /// at least <see cref="VisibleRows"/> (so the dock keeps its 4-row footprint
    /// even when sparsely filled) and capped at <see cref="MaxRows"/>.</summary>
    public static int RowsFor(int count)
    {
        int rows = (count + Columns - 1) / Columns;
        return Math.Clamp(rows, VisibleRows, MaxRows);
    }

    public override string Id => "liquidglass";
    public override string DisplayName => "液态玻璃";
    public override bool SupportsGridReorder => true;
    public override bool ShowGlassPanel => true;
    public override int MaxIcons => Capacity;
    public override Brush WindowBackground => Brushes.Transparent;
    // Liquid-glass dock defaults to 90% transparency (very see-through glass).
    public override double DefaultTransparency => 0.90;
    // 50% of the settings icon-size slider range [40, 96]: 40 + 0.50 * 56 = 68.
    public override double DefaultIconSize => 68.0;

    public override IReadOnlyList<Point> ComputeSlots(
        int count, Point center, AppSettings settings, out double outerReach)
    {
        var list = new List<Point>(Math.Max(0, count));
        double icon = settings.IconSize;
        double cellW = icon * 2.15;
        double cellH = icon * 2.35;   // extra height leaves room for the label

        // The first <resident> apps are the pinned region mirrored into the left
        // dock; the rest begin on a FRESH row beneath that block so the resident
        // frame never captures a non-resident icon (which would then look pinned
        // but be missing from the side dock). resident == 0/neg means "no special
        // block" — lay everything out as one continuous grid.
        int resident = settings.Ring0Count;
        resident = resident <= 0 ? 0 : Math.Clamp(resident, 0, count);
        int residentRows = resident > 0 ? (resident + Columns - 1) / Columns : 0;

        double gridW = (Columns - 1) * cellW;
        double x0 = center.X - gridW / 2.0;
        // center.Y is the centre of the first VISIBLE row block (VisibleRows tall);
        // row 0 starts at the top of that block.
        double visibleH = (VisibleRows - 1) * cellH;
        double y0 = center.Y - visibleH / 2.0;

        // Hard cap at the full grid; extra icons are never placed (the host
        // refuses to add beyond Capacity, so this is just a safety clamp).
        int max = Math.Min(count, Capacity);
        int totalRows = Math.Max(1, residentRows);
        for (int i = 0; i < max; i++)
        {
            int row, col;
            if (resident > 0 && i >= resident)
            {
                int j = i - resident;                 // index within the overflow block
                row = residentRows + j / Columns;
                col = j % Columns;
            }
            else
            {
                row = i / Columns;
                col = i % Columns;
            }
            list.Add(new Point(x0 + col * cellW, y0 + row * cellH));
            totalRows = Math.Max(totalRows, row + 1);
        }

        double gridH = (totalRows - 1) * cellH;
        outerReach = Math.Max(gridW, gridH) / 2.0 + icon;
        return list;
    }
}

/// <summary>Catalog of available themes.</summary>
public static class ThemeRegistry
{
    public static IReadOnlyList<PanelTheme> All { get; } = new PanelTheme[]
    {
        new SaturnRingTheme(),
        new LiquidGlassTheme(),
    };

    /// <summary>Resolves a theme by id, falling back to the Saturn theme.</summary>
    public static PanelTheme Get(string? id)
    {
        foreach (var t in All)
            if (string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
                return t;
        return All[0];
    }

    /// <summary>Loads the active panel transparency / icon size for the current
    /// theme: the value the user previously saved for that theme, or—if none—the
    /// theme's built-in defaults. Call after changing <see cref="AppSettings.Theme"/>.</summary>
    public static void LoadAppearance(AppSettings s)
    {
        var theme = Get(s.Theme);
        if (s.ThemeAppearances.TryGetValue(theme.Id, out var a))
        {
            s.PanelTransparency = a.Transparency;
            s.IconSize = a.IconSize;
            s.Ring0Count = a.Ring0Count;
        }
        else
        {
            s.PanelTransparency = theme.DefaultTransparency;
            s.IconSize = theme.DefaultIconSize;
            s.Ring0Count = 0;   // auto
        }
    }

    /// <summary>Persists the active transparency / icon size as the customised
    /// appearance for the current theme, so switching back restores it.</summary>
    public static void SaveAppearance(AppSettings s)
    {
        s.ThemeAppearances[Get(s.Theme).Id] = new ThemeAppearance
        {
            Transparency = s.PanelTransparency,
            IconSize = s.IconSize,
            Ring0Count = s.Ring0Count,
        };
    }
}
