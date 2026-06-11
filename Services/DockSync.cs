using System;
using System.Collections.Generic;
using Polaris.Models;

namespace Polaris.Services;

/// <summary>
/// Keeps the left dock in lock-step with the main dock's "resident" region —
/// the top two rows of the glass grid (the first <see cref="ResidentCount"/>
/// entries of <see cref="AppConfig.Apps"/>). The left dock always shows exactly
/// those apps; adding/removing/reordering in either place mirrors to the other.
/// </summary>
public static class DockSync
{
    /// <summary>Resident apps = the top two rows of the main glass grid.</summary>
    public const int ResidentCount = LiquidGlassTheme.Columns * 2;

    /// <summary>Two entries refer to the same app (same target + arguments).</summary>
    public static bool Matches(AppEntry a, AppEntry b) =>
        string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Arguments ?? "", b.Arguments ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>Rebuilds <see cref="AppConfig.LeftDockApps"/> so it mirrors the
    /// first <see cref="ResidentCount"/> entries of <see cref="AppConfig.Apps"/>.
    /// Returns true when the left list actually changed.</summary>
    public static bool MirrorResidentToLeft(AppConfig cfg)
    {
        int n = Math.Min(ResidentCount, cfg.Apps.Count);
        var resident = new List<AppEntry>(n);
        for (int i = 0; i < n; i++)
            resident.Add(cfg.Apps[i]);

        if (resident.Count == cfg.LeftDockApps.Count)
        {
            bool same = true;
            for (int i = 0; i < resident.Count; i++)
                if (!ReferenceEquals(resident[i], cfg.LeftDockApps[i]))
                {
                    same = false;
                    break;
                }
            if (same)
                return false;
        }

        cfg.LeftDockApps = resident;
        return true;
    }

    /// <summary>Inserts a new app into the resident region (so it appears in the
    /// left dock). When the region is already full the last resident app is
    /// pushed down into the regular grid.</summary>
    public static void AppendResident(AppConfig cfg, AppEntry entry)
    {
        int max = Math.Min(cfg.Apps.Count, ResidentCount);
        int pos = max >= ResidentCount ? ResidentCount - 1 : max;
        cfg.Apps.Insert(pos, entry);
    }

    /// <summary>Inserts a new app into the resident region at (or near) the
    /// requested <paramref name="index"/> within the top two rows, so a dropped
    /// icon lands where the pointer was. Clamped to the resident region.</summary>
    public static void InsertResident(AppConfig cfg, AppEntry entry, int index)
    {
        int max = Math.Min(cfg.Apps.Count, ResidentCount);
        int cap = max >= ResidentCount ? ResidentCount - 1 : max;
        int pos = Math.Clamp(index, 0, cap);
        cfg.Apps.Insert(pos, entry);
    }
}
