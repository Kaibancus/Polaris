using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Polaris.Models;
using Polaris.Services;

namespace Polaris.Views;

public partial class RadialWindow
{
    // ---- Free grid reorder (liquid-glass theme) --------------------------

    /// <summary>
    /// Returns the insertion slot index (0..n-1) the dragged icon
    /// <paramref name="src"/> is currently over. Uses a reading-order (row first,
    /// then column) hit test rather than a raw nearest-centre distance: the
    /// cursor's row is decided by which row band (one cell pitch tall, centred on
    /// the row) its Y falls in, so vertical moves switch rows reliably even
    /// though the cells are taller than wide and the resident block may break the
    /// grid onto a fresh row. The result is adjusted for the dragged icon being
    /// pulled out of the sequence.
    /// </summary>
    private int ComputeGridTarget(Point p, int src)
    {
        int n = _slotPositions.Count;
        if (n == 0)
            return 0;

        double half = GlassCellH / 2.0;
        int before = 0;
        bool srcBefore = false;
        for (int i = 0; i < n; i++)
        {
            Point s = _slotPositions[i];
            double dy = p.Y - s.Y;
            bool isBefore = dy > half          // slot sits on an earlier row
                ? true
                : dy < -half                   // slot sits on a later row
                    ? false
                    : p.X > s.X;               // same row band: left of the cursor
            if (isBefore)
            {
                before++;
                if (i == src)
                    srcBefore = true;
            }
        }

        int tgt = before;
        if (src >= 0 && src < n && srcBefore)
            tgt--;                             // the dragged icon was counted; it is removed first
        return Math.Clamp(tgt, 0, n - 1);
    }

    /// <summary>Returns the insertion index for a NEW icon dropped at content
    /// point <paramref name="p"/> using the same reading-order (row first, then
    /// column) hit test as the live reorder, so a dropped icon lands at the grid
    /// cell the pointer is actually over in both axes.</summary>
    private int ComputeGridInsertIndex(Point p)
    {
        int count = Math.Min(_slotPositions.Count, _config.Apps.Count);
        if (count == 0)
            return 0;

        // Reading-order insertion index: count the slots that come before the
        // cursor (earlier rows in full, plus same-row slots to the left). Row
        // bands are one cell pitch tall so vertical placement is precise.
        double half = GlassCellH / 2.0;
        int before = 0;
        for (int i = 0; i < count; i++)
        {
            Point s = _slotPositions[i];
            double dy = p.Y - s.Y;
            bool isBefore = dy > half
                ? true
                : dy < -half
                    ? false
                    : p.X > s.X;
            if (isBefore)
                before++;
        }
        return Math.Clamp(before, 0, _config.Apps.Count);
    }

    /// <summary>
    /// Produces the "make room" arrangement when the dragged entry
    /// <paramref name="src"/> is inserted at slot <paramref name="tgt"/>: returns,
    /// for each entry index, the slot it should occupy.
    /// </summary>
    private int[] GridArrangement(int src, int tgt)
    {
        int n = _config.Apps.Count;
        var order = new List<int>(n);
        for (int i = 0; i < n; i++)
            order.Add(i);

        if (src >= 0 && src < n)
        {
            order.Remove(src);
            int insertAt = Math.Clamp(tgt, 0, order.Count);
            order.Insert(insertAt, src);
        }

        int[] slotOfEntry = new int[n];
        for (int slot = 0; slot < order.Count; slot++)
            slotOfEntry[order[slot]] = slot;
        return slotOfEntry;
    }

    /// <summary>Animates every non-dragged icon to its slot in the prospective
    /// grid arrangement, producing the neighbour "push aside" effect.
    /// <paramref name="prospectiveResident"/> is the resident count the drop will
    /// commit (glass dock only; -1 to use the current layout), so the neighbours
    /// reflow to the layout that the drop actually produces rather than letting a
    /// non-resident icon visually slide into a shrinking resident block.</summary>
    private void ReflowGrid(int src, int tgt, int prospectiveResident = -1)
    {
        int[] slotOfEntry = GridArrangement(src, tgt);
        // When the resident block is changing size mid-drag, animate against the
        // prospective slot layout instead of the (stale) current one.
        IReadOnlyList<Point> slots =
            (prospectiveResident >= 0 && _theme.ShowGlassPanel)
                ? ComputeGlassSlots(prospectiveResident)
                : _slotPositions;
        for (int i = 0; i < _iconElements.Count; i++)
        {
            var el = _iconElements[i];
            if (el == null || el == _pressedIcon)
                continue;
            int slot = slotOfEntry[i];
            if (slot >= 0 && slot < slots.Count)
            {
                // Icons live in the scroll layer at true positions, so reflow to
                // the plain slot centre — the layer transform + clip handle the
                // scroll and viewport masking automatically.
                AnimateTo(el, slots[slot]);
            }
        }
    }

    /// <summary>Computes the glass-dock slot centres for a hypothetical resident
    /// count, used to animate the live reorder to the prospective drop layout.</summary>
    private IReadOnlyList<Point> ComputeGlassSlots(int residentCount)
    {
        var scaled = new AppSettings
        {
            IconSize = EffectiveIconSize,
            Ring0Count = Math.Clamp(residentCount, 0, _config.Apps.Count),
        };
        return _theme.ComputeSlots(_config.Apps.Count, GlassGridCenter, scaled, out _);
    }

    /// <summary>Commits a free-grid reorder: reorders the app entries so entry
    /// i maps to slot i on the next rebuild, then persists and rebuilds.</summary>
    private void CommitGridArrangement(AppEntry entry, int targetPos, Point dropPoint)
    {
        int src = _config.Apps.IndexOf(entry);
        if (src < 0)
        {
            Rebuild();
            return;
        }

        int tgt = targetPos >= 0 ? targetPos : ComputeGridTarget(GlassToContent(dropPoint), src);
        int[] slotOfEntry = GridArrangement(src, tgt);
        int n = _config.Apps.Count;

        var ordered = new AppEntry[n];
        for (int i = 0; i < n; i++)
            ordered[slotOfEntry[i]] = _config.Apps[i];

        _config.Apps.Clear();
        foreach (var a in ordered)
            _config.Apps.Add(a);

        // Glass theme: dropping an icon into the framed resident rows promotes it
        // to a resident (growing the count up to the cap) and dropping a resident
        // icon out of those rows demotes it, so the resident region tracks what
        // the user drags in/out instead of staying fixed.
        if (_theme.ShowGlassPanel)
            UpdateResidentCountForDrop(src, dropPoint);

        _persist();
        Rebuild();
        AppsChanged?.Invoke();
    }

    /// <summary>Adjusts <see cref="AppSettings.Ring0Count"/> after a glass-grid
    /// drop so the resident region follows the icon dragged in or out of the
    /// framed rows. <paramref name="srcBefore"/> is the dragged entry's index
    /// before the move (-1 for a brand-new icon).</summary>
    private void UpdateResidentCountForDrop(int srcBefore, Point dropPoint)
    {
        int resident = DockSync.ResidentCount(_config);
        int newResident = ProspectiveResidentCount(srcBefore, dropPoint);
        if (newResident != resident)
            _config.Settings.Ring0Count = newResident;
    }

    /// <summary>Computes the resident count a drop would commit: dragging an icon
    /// into the framed rows promotes it (+1), dragging a resident icon out of
    /// them demotes it (-1). <paramref name="srcBefore"/> is the dragged entry's
    /// index before the move (-1 for a brand-new icon).</summary>
    private int ProspectiveResidentCount(int srcBefore, Point dropPoint)
    {
        int cols = LiquidGlassTheme.Columns;
        int resident = DockSync.ResidentCount(_config);
        int residentRows = Math.Max(1, (resident + cols - 1) / cols);
        int dropRow = GlassRowAt(GlassToContent(dropPoint));
        bool inResident = dropRow >= 0 && dropRow < residentRows;
        bool wasResident = srcBefore >= 0 && srcBefore < resident;

        if (inResident && !wasResident && resident < DockSync.MaxResidentCount)
            return resident + 1;                 // promoted into the resident rows
        if (!inResident && wasResident && resident > 1)
            return resident - 1;                 // demoted out of the resident rows
        return resident;
    }

    /// <summary>
    /// Determines which ring (0 inner / 1 outer) and angular position the dragged
    /// icon is targeting, honouring the per-ring caps and creating the outer ring
    /// when the icon is dragged out to that distance.
    /// </summary>
    private (int ring, int pos) ComputeDragTarget(Point p, int src)
    {
        int n = _config.Apps.Count;
        int r0 = EffectiveRing0Count(n);
        int o0 = (src >= 0 && src < r0) ? r0 - 1 : r0; // other icons on the inner ring
        int m = Math.Max(0, n - 1);
        int ring1Others = m - o0;

        double dist = (p - _center).Length;
        double ringMid = (InnerRadius + (InnerRadius + RingStep)) / 2.0;
        int ring = dist <= ringMid ? 0 : 1;

        // Respect caps: redirect to the other ring if the chosen one is full.
        if (ring == 0 && o0 + 1 > Ring0Cap)
            ring = 1;
        if (ring == 1 && ring1Others + 1 > Ring1Cap)
            ring = 0;

        int slotsAfter = ring == 0 ? o0 + 1 : ring1Others + 1;
        double ang = Math.Atan2(p.Y - _center.Y, p.X - _center.X);
        double fromTop = ang + Math.PI / 2.0; // 0 at 12 o'clock, clockwise
        fromTop = ((fromTop % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
        int pos = (int)Math.Round(fromTop / (2 * Math.PI) * slotsAfter);
        pos = Math.Clamp(pos, 0, Math.Max(0, slotsAfter - 1));
        return (ring, pos);
    }

    /// <summary>
    /// Number of icons on the inner ring for <paramref name="n"/> total icons.
    /// The Saturn inner ring is the resident-app region (the first apps, which
    /// are also mirrored into the left side dock). Its size is user-customizable
    /// up to <see cref="Ring0Cap"/> (12): the persisted
    /// <see cref="AppSettings.Ring0Count"/> is honoured (0 = auto, fill first),
    /// and the inner ring only grows beyond the user's choice when the outer
    /// ring would otherwise overflow its own cap.
    /// </summary>
    private int EffectiveRing0Count(int n)
    {
        if (n <= 0)
            return 0;

        int userR0 = _config.Settings.Ring0Count;
        int r0 = userR0 > 0
            // User picked a resident-region size — respect it, capped at the
            // ring limit and the available icon count (never forced to the cap).
            ? Math.Min(userR0, Math.Min(Ring0Cap, n))
            // Auto: fill the inner ring first, up to its cap.
            : Math.Min(Ring0Cap, n);

        // If the outer ring would overflow its cap, push the surplus inward.
        if (n - r0 > Ring1Cap)
            r0 = Math.Min(n, n - Ring1Cap);

        return Math.Clamp(r0, 1, n);
    }

    /// <summary>Builds the slot centres for a layout of <paramref name="n"/> icons
    /// with <paramref name="r0"/> of them on the inner ring.</summary>
    private List<Point> SlotPositionsFor(int n, int r0)
    {
        var list = new List<Point>(Math.Max(0, n));
        if (n <= 0)
            return list;

        r0 = Math.Clamp(r0, 1, n);
        int ring1 = n - r0;
        for (int k = 0; k < r0; k++)
            list.Add(RingPoint(InnerRadius, k, r0));
        for (int k = 0; k < ring1; k++)
            list.Add(RingPoint(InnerRadius + RingStep, k, ring1));
        return list;
    }

    private Point RingPoint(double radius, int k, int count)
    {
        double angle = -Math.PI / 2 + 2 * Math.PI * k / Math.Max(1, count);
        return new Point(_center.X + radius * Math.Cos(angle),
                         _center.Y + radius * Math.Sin(angle) * RingTiltY);
    }

    /// <summary>
    /// Maps each entry index to its flat slot when the dragged entry
    /// <paramref name="src"/> is inserted into <paramref name="ring"/> at angular
    /// position <paramref name="pos"/>; also returns the resulting inner-ring count.
    /// </summary>
    private (int[] slotOfEntry, int newR0) ComputeArrangement(int src, int ring, int pos)
    {
        int n = _config.Apps.Count;
        int r0 = EffectiveRing0Count(n);
        int srcRing = src < r0 ? 0 : 1;

        // Current angular sequences per ring. Rebuild() places entry i at flat
        // slot i, so the inner ring is entries 0..r0-1 and the outer ring is the
        // rest, both already in angular (clockwise-from-top) order.
        var inner = new List<int>();
        for (int i = 0; i < r0; i++)
            inner.Add(i);
        var outer = new List<int>();
        for (int i = r0; i < n; i++)
            outer.Add(i);

        int newR0;
        if (ring == srcRing)
        {
            // Same ring: shift only the icons on the shorter arc between the
            // dragged icon's current angular slot and the target slot, so
            // crossing the 12 o'clock boundary nudges neighbours instead of
            // rotating the whole ring.
            var seq = ring == 0 ? inner : outer;
            int len = seq.Count;
            int cur = seq.IndexOf(src);
            int tgt = Math.Clamp(pos, 0, Math.Max(0, len - 1));
            int[] newIdx = ShortestArcShift(len, cur, tgt);

            var newSeq = new int[len];
            for (int j = 0; j < len; j++)
                newSeq[newIdx[j]] = seq[j];

            seq.Clear();
            seq.AddRange(newSeq);
            newR0 = r0;
        }
        else
        {
            // Cross ring: remove from the source ring and insert into the target
            // ring at the angular position. Both rings re-space by their new
            // counts, which is the expected behaviour when moving between rings.
            if (srcRing == 0)
                inner.Remove(src);
            else
                outer.Remove(src);

            var tgt = ring == 0 ? inner : outer;
            int insertAt = Math.Clamp(pos, 0, tgt.Count);
            tgt.Insert(insertAt, src);
            newR0 = inner.Count;
        }

        int[] slotOfEntry = new int[n];
        int slot = 0;
        foreach (int e in inner)
            slotOfEntry[e] = slot++;
        foreach (int e in outer)
            slotOfEntry[e] = slot++;
        return (slotOfEntry, newR0);
    }

    /// <summary>
    /// For a ring of <paramref name="len"/> slots, returns the new angular index
    /// of each current index when the icon at <paramref name="cur"/> moves to
    /// <paramref name="tgt"/>, shifting only the shorter arc between them by one.
    /// </summary>
    private static int[] ShortestArcShift(int len, int cur, int tgt)
    {
        int[] newIdx = new int[Math.Max(0, len)];
        if (len <= 0)
            return newIdx;

        cur = Math.Clamp(cur, 0, len - 1);
        tgt = Math.Clamp(tgt, 0, len - 1);
        newIdx[cur] = tgt;

        int df = ((tgt - cur) % len + len) % len; // forward steps cur -> tgt
        int db = len - df;                          // backward steps
        bool forward = df <= db;

        for (int j = 0; j < len; j++)
        {
            if (j == cur)
                continue;
            int ns = j;
            if (forward)
            {
                int rel = ((j - cur) % len + len) % len; // 1..len-1
                if (rel >= 1 && rel <= df)
                    ns = (j - 1 + len) % len;
            }
            else
            {
                int relT = ((j - tgt) % len + len) % len; // 0..len-1
                if (relT >= 0 && relT < db)
                    ns = (j + 1) % len;
            }
            newIdx[j] = ns;
        }
        return newIdx;
    }

}
