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
    // ---- External drop (add desktop shortcuts) ---------------------------

    private void OnDragOverPanel(object sender, DragEventArgs e)
    {
        e.Effects = (e.Data.GetDataPresent(DataFormats.FileDrop) || ShellNamespace.HasShellItems(e.Data))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropPanel(object sender, DragEventArgs e)
    {
        var entries = new List<AppEntry>();

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            foreach (var f in (string[])e.Data.GetData(DataFormats.FileDrop))
            {
                var entry = ShortcutResolver.CreateEntry(f);
                if (entry != null && !string.IsNullOrWhiteSpace(entry.Path))
                    entries.Add(entry);
            }
        }
        if (ShellNamespace.HasShellItems(e.Data))
            entries.AddRange(ShellNamespace.CreateEntries(e.Data));

        if (entries.Count == 0)
            return;

        // Insert the dropped app(s) at the grid slot nearest the pointer, so the
        // icon lands where it was dropped (on the side of the cursor) rather than
        // always at the end.
        Point drop = e.GetPosition(PanelCanvas);
        int insertIdx = _theme.SupportsGridReorder
            ? ComputeGridInsertIndex(GlassToContent(drop))
            : _config.Apps.Count;

        // Saturn: place the dropped icon on the ring the cursor is over. A drop on
        // the inner ring inserts into the resident region (the first Ring0Count
        // apps) and grows that count, so an icon can be added straight to the
        // inner ring while it still has room (rather than always appending to the
        // outer ring).
        bool intoInnerRing = false;
        if (_theme.IsSaturn)
        {
            int r0 = EffectiveRing0Count(_config.Apps.Count);
            var (ring, pos) = ComputeDragTarget(drop, -1);
            if (ring == 0)
            {
                intoInnerRing = true;
                insertIdx = Math.Clamp(pos, 0, r0);
            }
            else
            {
                insertIdx = Math.Clamp(r0 + pos, r0, _config.Apps.Count);
            }
        }

        // A glass drop inside the framed resident rows should add the icon as a
        // resident, growing the resident count (up to the cap) per icon added.
        bool intoResident = false;
        if (_theme.ShowGlassPanel)
        {
            int cols = LiquidGlassTheme.Columns;
            int resident = DockSync.ResidentCount(_config);
            int residentRows = Math.Max(1, (resident + cols - 1) / cols);
            int dropRow = GlassRowAt(GlassToContent(drop));
            intoResident = dropRow >= 0 && dropRow < residentRows;
        }

        bool added = false;
        bool rejected = false;
        int cap = _theme.MaxIcons;
        foreach (var entry in entries)
        {
            if (_config.Apps.Count >= cap)
            {
                rejected = true;
                continue;
            }
            insertIdx = Math.Clamp(insertIdx, 0, _config.Apps.Count);
            _config.Apps.Insert(insertIdx, entry);
            insertIdx++;
            if (intoResident && DockSync.ResidentCount(_config) < DockSync.MaxResidentCount)
                _config.Settings.Ring0Count = DockSync.ResidentCount(_config) + 1;
            else if (intoInnerRing && _config.Settings.Ring0Count > 0)
                // Grow the explicit inner-ring count to include the icon just
                // inserted into it. In auto mode (Ring0Count == 0) the inner ring
                // already fills first, so the icon lands there without a bump.
                _config.Settings.Ring0Count = Math.Min(Ring0Cap, _config.Settings.Ring0Count + 1);
            added = true;
        }

        if (added)
        {
            _persist();
            Rebuild();
            AppsChanged?.Invoke();
        }
        if (rejected)
        {
            System.Windows.MessageBox.Show(
                $"当前主题最多只能放置 {cap} 个图标，部分图标未添加。",
                "已达图标上限",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        e.Handled = true;
    }

    // ---- Drag & click handling -------------------------------------------

    private void Icon_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressedIcon = (RadialIcon)sender;
        _pressPoint = e.GetPosition(PanelCanvas);
        _dragging = false;
        _dragTargetRing = -1;
        _dragTargetPos = -1;
        _dragProspectiveResident = -2;
        PanelCanvas.CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_pressedIcon == null)
        {
            // No icon pressed: drive the High-mode cursor-distance magnification.
            UpdateMagnifyFromPointer(e.GetPosition(PanelCanvas));
            return;
        }

        Point p = e.GetPosition(PanelCanvas);
        if (!_dragging)
        {
            if ((p - _pressPoint).Length < DragThreshold)
                return;
            _dragging = true;
            // A drag has begun: settle the magnification wave so it doesn't fight
            // the drag/reflow animations.
            ResetMagnify();
            Panel.SetZIndex(_pressedIcon, 1000);
            // Stop any residual reflow animation on the dragged icon so it tracks
            // the cursor exactly.
            _pressedIcon.BeginAnimation(Canvas.LeftProperty, null);
            _pressedIcon.BeginAnimation(Canvas.TopProperty, null);
            // The floating hover label was shown while hovering this icon; hide it
            // so the name doesn't linger in place once the icon starts moving.
            if (_theme.ShowGlassPanel)
                HideGlassHoverLabel();
            // For the glass grid, lift the dragged icon out of the clipped scroll
            // layer into the top-level canvas so it can be dragged anywhere
            // (including up into the delete zone) without being clipped, and so
            // its position is in plain PanelCanvas coordinates.
            if (_theme.ShowGlassPanel && _glassScrollLayer != null &&
                ReferenceEquals(_pressedIcon.Parent, _glassScrollLayer))
            {
                _glassScrollLayer.Children.Remove(_pressedIcon);
                PanelCanvas.Children.Add(_pressedIcon);
                Panel.SetZIndex(_pressedIcon, 1000);
            }

            // Keep the left dock summoned for the whole drag so it is a clear,
            // forgiving drop target.
            if (_theme.ShowGlassPanel)
                GlassDragActiveChanged?.Invoke(true);
        }

        PlaceCentered(_pressedIcon, p);

        bool deleteZone = IsDeleteDrop(p);
        _pressedIcon.Opacity = deleteZone ? 0.4 : 1.0;

        // Push other icons aside to reveal the slot the dragged icon is over.
        // Skip while the icon is dragged out into the delete zone.
        // Only the Saturn ring layout supports live reorder; the grid test theme
        // keeps drag-to-launch and drag-out-to-delete but no live reflow.
        if (_theme.IsSaturn && !deleteZone)
        {
            int src = _iconElements.IndexOf(_pressedIcon);
            var (ring, pos) = ComputeDragTarget(p, src);
            if (ring != _dragTargetRing || pos != _dragTargetPos)
            {
                _dragTargetRing = ring;
                _dragTargetPos = pos;
                ReflowAround(ring, pos);
            }
        }
        else if (_theme.SupportsGridReorder && !deleteZone)
        {
            // Free-grid reorder: find the slot the cursor is over and make room
            // by shifting the other icons into the insertion arrangement. The
            // grid may be scrolled, so compare against scroll-corrected slots.
            int src = _iconElements.IndexOf(_pressedIcon);
            Point content = GlassToContent(p);
            int tgt = ComputeGridTarget(content, src);
            // For the glass dock, the resident block may grow/shrink as the icon
            // crosses the framed-rows boundary; reflow to that prospective layout
            // so the animation matches what the drop will actually commit.
            int prosp = _theme.ShowGlassPanel ? ProspectiveResidentCount(src, content) : -1;
            if (tgt != _dragTargetPos || prosp != _dragProspectiveResident)
            {
                _dragTargetPos = tgt;
                _dragProspectiveResident = prosp;
                ReflowGrid(src, tgt, prosp);
            }
        }
        else if (_dragTargetRing != -1 || _dragTargetPos != -1)
        {
            // Dragged into the delete zone — snap the others back to their slots.
            _dragTargetRing = -1;
            _dragTargetPos = -1;
            _dragProspectiveResident = -2;
            RestoreSlots();
        }
    }

    /// <summary>Maps a canvas point to the grid's un-scrolled "content" space by
    /// adding back the current scroll offset, so hit-tests against the static
    /// <see cref="_slotPositions"/> are correct while the grid is scrolled.</summary>
    private Point GlassToContent(Point p) =>
        _theme.ShowGlassPanel ? new Point(p.X, p.Y + _glassScroll) : p;

    /// <summary>0-based grid row of the glass cell at content point
    /// <paramref name="contentP"/> (row 0 is the first visible row). Used to tell
    /// whether a drop lands inside the framed resident rows.</summary>
    private int GlassRowAt(Point contentP)
    {
        double cellH = GlassCellH;
        double y0 = GlassDockCenterY - (LiquidGlassTheme.VisibleRows - 1) * cellH / 2.0;
        return (int)Math.Round((contentP.Y - y0) / cellH);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        OnGlassMouseWheel(this, e);
    }

}
