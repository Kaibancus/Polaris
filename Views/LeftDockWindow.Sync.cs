using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using Polaris.Models;
using Polaris.Services;

namespace Polaris.Views;

public partial class LeftDockWindow
{
    // ---- Add / remove -----------------------------------------------------

    /// <summary>Adds a main-dock app to the left dock (called when an icon is
    /// dragged from the main dock onto this dock). Because the left dock mirrors
    /// the resident region, this promotes the entry into the top two rows of the
    /// main dock so it appears in both places. <paramref name="index"/> is the
    /// desired position within the resident region (-1 = append).</summary>
    public void AddFromMainDock(AppEntry entry, int index = -1)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Path))
            return;
        int idx = _config.Apps.FindIndex(e => DockSync.Matches(e, entry));
        if (idx >= 0 && idx < DockSync.ResidentCount(_config))
            return;   // already resident — nothing to do.

        if (idx >= 0)
        {
            var e = _config.Apps[idx];
            _config.Apps.RemoveAt(idx);
            if (index >= 0) DockSync.InsertResident(_config, e, index);
            else DockSync.AppendResident(_config, e);
        }
        else
        {
            if (index >= 0) DockSync.InsertResident(_config, entry, index);
            else DockSync.AppendResident(_config, entry);
        }
        AfterSharedChange();
    }

    private void RemoveFromLeftDock(AppEntry entry)
    {
        // The left dock mirrors the resident region, so removing an icon here
        // removes the app from the main dock's resident apps as well.
        int idx = _config.Apps.FindIndex(e => DockSync.Matches(e, entry));
        if (idx < 0)
        {
            AfterSharedChange();
            return;
        }

        // Number of apps currently sitting in the resident region.
        int resident = Math.Min(DockSync.ResidentCount(_config), _config.Apps.Count);
        bool wasResident = idx < resident;
        _config.Apps.RemoveAt(idx);

        // Every left-dock icon lives in the resident region, so unpinning one
        // must shrink the resident count too. Otherwise the first non-resident
        // app is pulled up into the freed slot and auto-replenishes the side
        // dock — exactly what the user does NOT want. (Mirrors DeleteEntry.)
        if (wasResident)
            _config.Settings.Ring0Count = Math.Max(0, resident - 1);

        AfterSharedChange();
    }

    /// <summary>Re-mirrors the resident region into the left dock, persists, and
    /// refreshes both docks. Call after any change that mutates _config.Apps
    /// from the left dock side.</summary>
    private void AfterSharedChange()
    {
        DockSync.MirrorResidentToLeft(_config);
        _persist();
        Rebuild();
        MainDockChanged?.Invoke();
    }

    /// <summary>Re-syncs the left dock after the main dock changed its app list
    /// (resident region). Called by the host when the main dock mutates.</summary>
    public void RefreshFromConfig()
    {
        DockSync.MirrorResidentToLeft(_config);
        Rebuild();
    }

    /// <summary>Re-reads the dock-position setting, re-anchors the window to the
    /// (possibly new) screen edge and rebuilds the layout. Called by the host
    /// after the settings window changes the dock position.</summary>
    public void RefreshLayout()
    {
        PositionAndSize();
        if (_realized)
            Rebuild();
    }

    // ---- External drop (desktop shortcut -> both docks) -------------------

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = (e.Data.GetDataPresent(DataFormats.FileDrop) || ShellNamespace.HasShellItems(e.Data))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        var entries = new List<AppEntry>();
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var f in files)
            {
                var entry = ShortcutResolver.CreateEntry(f);
                if (entry != null)
                    entries.Add(entry);
            }
        }
        if (ShellNamespace.HasShellItems(e.Data))
            entries.AddRange(ShellNamespace.CreateEntries(e.Data));

        // Insertion index from the pointer's main-axis position, so the dropped
        // icon lands where the cursor is rather than always at the end. Use the
        // insertion-GAP index (nearest boundary between icons) so a drop on a
        // slot's lower half lands after it, not half a cell too early.
        var drop = e.GetPosition(PanelCanvas);
        double contentMain = MainOf(drop) + _pinnedScroll;
        int dropIdx = (int)Math.Round((contentMain - _pinnedAreaMain) / CellH);
        dropIdx = Math.Clamp(dropIdx, 0, DockSync.ResidentCount(_config));

        bool changed = false;
        foreach (var entry in entries)
        {
            // Dropping on the left dock makes the app resident: insert it into
            // the top two rows of the main dock (the left dock mirrors those) at
            // the pointer position.
            int idx = _config.Apps.FindIndex(e => DockSync.Matches(e, entry));
            if (idx < 0)
            {
                DockSync.InsertResident(_config, entry, dropIdx);
                dropIdx++;
                changed = true;
            }
            else if (idx >= DockSync.ResidentCount(_config))
            {
                var moved = _config.Apps[idx];
                _config.Apps.RemoveAt(idx);
                DockSync.InsertResident(_config, moved, dropIdx);
                dropIdx++;
                changed = true;
            }
        }
        if (changed)
            AfterSharedChange();
        e.Handled = true;
    }

}
