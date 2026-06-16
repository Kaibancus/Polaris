using System;
using System.Collections.Generic;
using Polaris.Services;

namespace Polaris.Views;

// Shared new-message badge logic for both docks. The per-window flashing /
// unread-count derivation is identical for the main (Saturn / glass) dock and
// the side dock, so it lives here instead of being copy-pasted into each dock's
// running-state and attention-only refreshers.
internal static class AttentionBadges
{
    /// <summary>Derives one icon's (flashing, unread-count) badge state from its
    /// live windows: flashing if any of the icon's windows is in
    /// <paramref name="flashing"/> (the SnapshotFlashing handle set), with a best-
    /// effort unread count parsed from the window titles. Reuses
    /// GetWindowsForEntry so the icon→window matching stays identical to the hover
    /// previews. Runs off the UI thread and never throws.</summary>
    public static (bool flashing, int count) ForIcon(
        RadialIcon icon, HashSet<IntPtr> flashing, string logArea)
    {
        bool flash = false;
        int count = 0;
        try
        {
            var wins = WindowPreviewService.GetWindowsForEntry(
                icon.Entry.Path, icon.Entry.Arguments);
            foreach (var w in wins)
            {
                if (flashing.Contains(w.Handle))
                    flash = true;
                int c = AttentionService.ParseUnread(w.Title);
                if (c > count)
                    count = c;
            }
        }
        catch (Exception ex) { Log.Debug(logArea, "attention badge computation failed", ex); }
        return (flash, count);
    }
}
