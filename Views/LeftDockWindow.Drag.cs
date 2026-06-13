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
    // ---- Drag (reorder within column / drag out to remove) ----------------

    private void Icon_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressedIcon = sender as RadialIcon;
        _pressPoint = e.GetPosition(PanelCanvas);
        _dragging = false;
        // Pin the dock for the whole press-drag-release gesture immediately.
        // The edge poll fires every 100 ms; if we waited until the 6 px drag
        // threshold to pin it, the cursor could leave the narrow slab bounds
        // first, the poll would hide the dock and CancelDrag would abort the
        // press before the drag ever started.
        SetDragActive(true);
        PanelCanvas.CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressedIcon == null)
        {
            // Drive the magnification wave + hover label from the live pointer
            // position against the static column rect (see UpdateWaveFromPointer).
            UpdateWaveFromPointer(e.GetPosition(PanelCanvas));
            return;
        }

        var p = e.GetPosition(PanelCanvas);
        if (!_dragging)
        {
            if ((p - _pressPoint).Length < DragThreshold)
                return;
            _dragging = true;
            HideHoverLabel();
            // Settle any magnification wave so neighbouring icons return to rest
            // while the dragged icon is lifted out.
            ResetWave();
            Panel.SetZIndex(_pressedIcon, 5000);
            // Lift the dragged icon out of the clipped scroll layer so it can be
            // dragged anywhere (including out of the dock to remove it).
            if (_scrollLayer != null && _scrollLayer.Children.Contains(_pressedIcon))
            {
                _scrollLayer.Children.Remove(_pressedIcon);
                PanelCanvas.Children.Add(_pressedIcon);
            }
            // The gap starts at the dragged icon's own slot (no shift yet).
            _dragInsertIdx = _pinnedIcons.IndexOf(_pressedIcon);
        }

        PlaceCentered(_pressedIcon, p);
        // Fade while dragged outside the column (marks for removal).
        bool outside = Math.Abs(CrossOf(p) - _colCenterCross) > _slabCrossLen * 0.85;
        _pressedIcon.Opacity = outside ? 0.4 : 1.0;

        // macOS-style "push aside": slide the other icons open to reveal a gap at
        // the insertion point the drop would land on. Only re-arrange when that
        // index actually changes so the eases run to completion smoothly.
        if (!outside)
        {
            double contentMain = MainOf(p) + _pinnedScroll;
            int tgt = (int)Math.Round((contentMain - _pinnedAreaMain - CellH / 2.0) / CellH);
            tgt = Math.Clamp(tgt, 0, Math.Max(0, _pinnedIcons.Count - 1));
            if (tgt != _dragInsertIdx)
            {
                _dragInsertIdx = tgt;
                ArrangeForDrag(tgt);
            }
        }
        else if (_dragInsertIdx != int.MaxValue)
        {
            // Dragged out of the column → close the gap (neighbours fill in).
            _dragInsertIdx = int.MaxValue;
            ArrangeForDrag(int.MaxValue);
        }
    }

    /// <summary>Animates every non-dragged pinned icon to make room for the
    /// dragged icon at insertion index <paramref name="gap"/> — the macOS dock
    /// "push the neighbours aside" effect.</summary>
    private void ArrangeForDrag(int gap)
    {
        int src = _pinnedIcons.IndexOf(_pressedIcon!);
        int compact = 0;
        for (int i = 0; i < _pinnedIcons.Count; i++)
        {
            if (i == src)
                continue;
            int visual = compact < gap ? compact : compact + 1;
            if (visual < _pinnedSlots.Count)
                AnimateIconTo(_pinnedIcons[i], _pinnedSlots[visual]);
            compact++;
        }
    }

    /// <summary>Smoothly slides an icon's Canvas position toward a logical
    /// (X = cross, Y = main) slot centre.</summary>
    private void AnimateIconTo(RadialIcon icon, Point logical)
    {
        Point center = ToLocal(logical.Y, logical.X);
        double left = center.X - icon.IconSize / 2.0;
        double top = center.Y - icon.IconSize / 2.0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(190);
        var ax = new DoubleAnimation(left, dur) { EasingFunction = ease };
        var ay = new DoubleAnimation(top, dur) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(ax, App.AnimationFrameRate);
        Timeline.SetDesiredFrameRate(ay, App.AnimationFrameRate);
        icon.BeginAnimation(Canvas.LeftProperty, ax);
        icon.BeginAnimation(Canvas.TopProperty, ay);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        // Pointer left the dock window entirely: settle the wave back to rest
        // (only when not mid-drag, which captures the mouse anyway).
        if (_pressedIcon == null)
            UpdateWaveFromPointer(new Point(double.NaN, double.NaN));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_pressedIcon == null)
            return;

        var icon = _pressedIcon;
        var p = e.GetPosition(PanelCanvas);
        bool wasDragging = _dragging;
        _pressedIcon = null;
        _dragging = false;
        _dragInsertIdx = -1;
        PanelCanvas.ReleaseMouseCapture();
        SetDragActive(false);   // release the press-drag hold; edge poll resumes

        if (!wasDragging)
        {
            Launch(icon.Entry, icon);
            return;
        }

        bool outside = Math.Abs(CrossOf(p) - _colCenterCross) > _slabCrossLen * 0.85;
        if (outside)
        {
            RemoveFromLeftDock(icon.Entry);
            return;
        }

        // Reorder: drop into the slot nearest the cursor's (scroll-adjusted)
        // main-axis position. The left dock mirrors the main dock's resident
        // region, so reorder the matching entries in the resident slice directly.
        double contentMain = MainOf(p) + _pinnedScroll;
        int tgt = (int)Math.Round((contentMain - _pinnedAreaMain - CellH / 2.0) / CellH);
        tgt = Math.Clamp(tgt, 0, _config.LeftDockApps.Count - 1);
        int src = _config.LeftDockApps.IndexOf(icon.Entry);
        if (src >= 0 && tgt != src && src < _config.Apps.Count && tgt < _config.Apps.Count)
        {
            var e2 = _config.Apps[src];
            _config.Apps.RemoveAt(src);
            _config.Apps.Insert(tgt, e2);
            AfterSharedChange();
            return;
        }
        Rebuild();
    }

    private void CancelDrag()
    {
        if (_pressedIcon != null)
        {
            _pressedIcon = null;
            _dragging = false;
            _dragInsertIdx = -1;
            // Clear the drag-hold flag directly (no UpdateVisibility) because
            // CancelDrag is also invoked from DoHide and must not recurse.
            _shownByDrag = false;
            if (PanelCanvas.IsMouseCaptured)
                PanelCanvas.ReleaseMouseCapture();
            Rebuild();
        }
    }

    // ---- Wheel scroll -----------------------------------------------------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_shown || !PinnedScrollable || _scrollTransform == null)
            return;
        HideHoverLabel();
        double step = CellH * (e.Delta / 120.0);
        _pinnedScroll = Math.Clamp(_pinnedScroll - step, 0, PinnedScrollMax);
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        // The scroll layer translates along the MAIN axis (Y for vertical docks,
        // X for horizontal docks).
        var prop = IsVertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        double from = IsVertical ? _scrollTransform.Y : _scrollTransform.X;
        var anim = new DoubleAnimation(from, -_pinnedScroll, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        };
        Timeline.SetDesiredFrameRate(anim, App.AnimationFrameRate);
        if (IsVertical)
            _scrollTransform.Y = -_pinnedScroll;
        else
            _scrollTransform.X = -_pinnedScroll;
        _scrollTransform.BeginAnimation(prop, anim);
        e.Handled = true;
    }

}
