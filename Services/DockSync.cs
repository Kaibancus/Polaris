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
    /// <summary>Hard cap on the resident region = the top two rows of the main
    /// glass grid.</summary>
    public const int MaxResidentCount = LiquidGlassTheme.Columns * 2;

    /// <summary>Effective number of resident apps (the first apps, mirrored into
    /// the left dock). User-customizable within <see cref="MaxResidentCount"/>
    /// via <see cref="AppSettings.Ring0Count"/>: 0 = auto (fill both rows, the
    /// historical default), otherwise the EXACT chosen count clamped to the cap.
    ///
    /// The count is honoured verbatim for every theme — the left dock mirrors
    /// precisely this many apps, no more. The liquid-glass grid keeps the side
    /// dock and the framed resident region consistent by starting the
    /// non-resident apps on a fresh row beneath the resident block (see
    /// <see cref="LiquidGlassTheme.ComputeSlots"/>), so no "orphan" icon ever
    /// sits inside the resident frame without also appearing in the side
    /// dock.</summary>
    public static int ResidentCount(AppConfig cfg)
    {
        int desired = cfg.Settings.Ring0Count;
        return desired <= 0
            ? MaxResidentCount
            : Math.Clamp(desired, 1, MaxResidentCount);
    }

    /// <summary>Two entries refer to the same app (same target + arguments).</summary>
    public static bool Matches(AppEntry a, AppEntry b) =>
        string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Arguments ?? "", b.Arguments ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>Rebuilds <see cref="AppConfig.LeftDockApps"/> so it mirrors the
    /// first <see cref="ResidentCount(AppConfig)"/> entries of
    /// <see cref="AppConfig.Apps"/>.
    /// Returns true when the left list actually changed.</summary>
    public static bool MirrorResidentToLeft(AppConfig cfg)
    {
        int n = Math.Min(ResidentCount(cfg), cfg.Apps.Count);
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
    /// <summary>Inserts a new app into the resident region (so it appears in the
    /// left dock). When the region has not yet reached the hard cap
    /// (<see cref="MaxResidentCount"/>) the resident count grows by one so the
    /// app is genuinely <i>added</i>; only once the cap is reached does the last
    /// resident get pushed down into the regular grid.</summary>
    public static void AppendResident(AppConfig cfg, AppEntry entry)
    {
        int resident = GrowResidentIfFull(cfg);
        int pos = cfg.Apps.Count >= resident ? resident - 1 : cfg.Apps.Count;
        cfg.Apps.Insert(pos, entry);
    }

    /// <summary>Inserts a new app into the resident region at (or near) the
    /// requested <paramref name="index"/> within the top two rows, so a dropped
    /// icon lands where the pointer was. Grows the resident count (up to the hard
    /// cap) so dropping at the end genuinely adds the app rather than evicting
    /// the current last resident.</summary>
    public static void InsertResident(AppConfig cfg, AppEntry entry, int index)
    {
        int resident = GrowResidentIfFull(cfg);
        int cap = cfg.Apps.Count >= resident ? resident - 1 : cfg.Apps.Count;
        int pos = Math.Clamp(index, 0, cap);
        cfg.Apps.Insert(pos, entry);
    }

    /// <summary>When the resident region is full (as many apps as the current
    /// resident count) but still below the hard cap, bumps
    /// <see cref="AppSettings.Ring0Count"/> by one so the next insert adds a new
    /// resident slot instead of evicting the last one. Returns the (possibly
    /// grown) resident count.</summary>
    private static int GrowResidentIfFull(AppConfig cfg)
    {
        int resident = ResidentCount(cfg);
        if (cfg.Apps.Count >= resident && resident < MaxResidentCount)
        {
            cfg.Settings.Ring0Count = resident + 1;
            resident = ResidentCount(cfg);
        }
        return resident;
    }
}
