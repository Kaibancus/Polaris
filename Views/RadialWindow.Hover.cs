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
    // ---- Hover "spread apart" --------------------------------------------

    /// <summary>
    /// On hover, raise the icon above its neighbours and push the nearby icons
    /// radially away so the enlarged icon + its name label have room to breathe.
    /// </summary>
    private void OnIconHoverStarted(RadialIcon ic)
    {
        // Ignore hover effects while a drag is in progress.
        if (_pressedIcon != null)
            return;

        int idx = _iconElements.IndexOf(ic);
        if (idx < 0)
            return;

        _hoverIcon = ic;
        Panel.SetZIndex(ic, 3000); // above Saturn (root=2000) so the label isn't hidden

        // Glass grid: the icons live inside a clipped scroll layer, so a hovered
        // icon's name label (which hangs below the icon) is cut by the viewport
        // clip on the bottom row. Rather than reparent the icon (fragile — the
        // re-host fires synthetic enter/leave that flicker), show a floating
        // label in the unclipped PanelCanvas positioned under the hovered icon.
        if (_theme.ShowGlassPanel)
            ShowGlassHoverLabel(ic, idx);

        // High mode drives a continuous cursor-distance magnification wave which
        // owns spacing/scale, so skip the binary "spread neighbours" shove.
        if (!MainMagnifyEnabled)
            SpreadNeighbours(idx);
    }

    private void OnIconHoverEnded(RadialIcon ic)
    {
        if (_pressedIcon != null)
            return;

        int idx = _iconElements.IndexOf(ic);
        if (idx >= 0)
            Panel.SetZIndex(ic, 0);

        if (_theme.ShowGlassPanel)
            HideGlassHoverLabel();

        if (_hoverIcon == ic)
            _hoverIcon = null;

        // In High mode the magnification wave restores spacing as the cursor
        // moves away, so the legacy slot-restore (which fights it) is skipped.
        if (!MainMagnifyEnabled)
            RestoreSlots();
    }

    /// <summary>Floating name label shown under a hovered glass icon, hosted on
    /// the unclipped PanelCanvas so the bottom row's label is not cut by the
    /// scroll-viewport clip.</summary>
    private Border? _glassHoverLabel;
    private TextBlock? _glassHoverLabelText;

    /// <summary>Must match <c>RadialIcon.HoverScale</c> — the hover zoom factor,
    /// used to place the floating label below the zoomed icon and to size its
    /// font to match the (formerly icon-scaled) built-in label.</summary>
    private const double HoverScaleConst = DockTuning.HoverScale;

    private void ShowGlassHoverLabel(RadialIcon ic, int idx)
    {
        if (idx >= _slotPositions.Count)
            return;

        if (_glassHoverLabel == null)
        {
            _glassHoverLabelText = new TextBlock
            {
                Foreground = LabelBrush,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                // Dark drop shadow → depth ("3D") plus a halo that keeps the light
                // text legible even when a white window shows through the label.
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 5,
                    ShadowDepth = 1.4,
                    Direction = 315,
                    Opacity = 0.9,
                },
            };
            _glassHoverLabel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x05, 0x1A, 0x1A, 0x1A)),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(3, 4, 3, 4),
                IsHitTestVisible = false,
                Child = _glassHoverLabelText,
                Opacity = 0,
                // The label fades in/out via an Opacity animation; cache the
                // text + drop-shadow as one bitmap so each fade frame just blits
                // a texture instead of re-rasterizing the blurred glyphs. The
                // cache rebuilds once per hover when the name/font changes — a
                // negligible cost next to the per-frame fade recomposite.
                CacheMode = new BitmapCache(2.0),
            };
            Panel.SetZIndex(_glassHoverLabel, 4000);
            PanelCanvas.Children.Add(_glassHoverLabel);
        }

        // Match the built-in label's apparent size: that label lived inside the
        // icon's visual tree and was scaled up by the 1.7x hover zoom, so a fixed
        // 11.5pt read as ~11.5*1.7. Replicate that here (the floating label is
        // NOT scaled) so the font doesn't look shrunken.
        _glassHoverLabelText!.FontSize = 11.5 * HoverScaleConst * Polaris.Services.FontScale.Current;
        _glassHoverLabelText.Text = ic.Entry.Name;

        // Position centred BELOW the hovered icon. The icon zooms to 1.7x about
        // its centre, so its visible bottom is at center + IconSize/2*1.7. Place
        // the label just past that. Slot is in content coords; subtract scroll.
        Point slot = _slotPositions[idx];
        double cx = slot.X;
        double cy = slot.Y - _glassScroll;
        double zoomedHalf = ic.IconSize / 2.0 * HoverScaleConst;
        // Force a fresh measure so DesiredSize reflects the new text + font (a
        // brand-new or reused badge may carry a stale size, which would offset
        // the centring).
        _glassHoverLabel.InvalidateMeasure();
        _glassHoverLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double lw = _glassHoverLabel.DesiredSize.Width;
        Canvas.SetLeft(_glassHoverLabel, cx - lw / 2.0);
        Canvas.SetTop(_glassHoverLabel, cy + zoomedHalf + 6);

        _glassHoverLabel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(110))));
    }

    private void HideGlassHoverLabel()
    {
        _glassHoverLabel?.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(110))));
    }

    /// <summary>
    /// Pushes icons near <paramref name="hovered"/> away from it, with the shift
    /// falling off by distance, so closer neighbours move more.
    /// </summary>
    private void SpreadNeighbours(int hovered)
    {
        double iconSize = _config.Settings.IconSize;
        double push = iconSize * DockTuning.SpreadPush;
        double influence = iconSize * DockTuning.SpreadInfluence;
        Point hp = _slotPositions[hovered];

        for (int i = 0; i < _iconElements.Count; i++)
        {
            if (i == hovered)
                continue;

            Vector v = _slotPositions[i] - hp;
            double d = v.Length;
            if (d > 0.01 && d < influence)
            {
                double amount = push * (1 - d / influence);
                Point np = _slotPositions[i] + (v / d) * amount;
                AnimateTo(_iconElements[i], np);
            }
            else
            {
                AnimateTo(_iconElements[i], _slotPositions[i]);
            }
        }
    }

    /// <summary>Animates all icons back to their home ring slots.</summary>
    private void RestoreSlots()
    {
        for (int i = 0; i < _iconElements.Count && i < _slotPositions.Count; i++)
        {
            if (_iconElements[i] == _pressedIcon)
                continue;
            AnimateTo(_iconElements[i], _slotPositions[i]);
        }
    }

    /// <summary>
    /// Animates every non-dragged icon to its slot in the prospective layout
    /// where the dragged icon occupies (<paramref name="ring"/>, <paramref name="pos"/>),
    /// producing the "make room" effect across both rings.
    /// </summary>
    private void ReflowAround(int ring, int pos)
    {
        int src = _iconElements.IndexOf(_pressedIcon!);
        if (src < 0)
            return;

        var (slotOfEntry, newR0) = ComputeArrangement(src, ring, pos);
        int n = _config.Apps.Count;
        var positions = SlotPositionsFor(n, newR0);
        for (int i = 0; i < _iconElements.Count; i++)
        {
            if (_iconElements[i] == _pressedIcon)
                continue;
            int slot = slotOfEntry[i];
            if (slot >= 0 && slot < positions.Count)
                AnimateTo(_iconElements[i], positions[slot]);
        }
    }

    /// <summary>Smoothly slides an icon to a new slot center.</summary>
    private void AnimateTo(FrameworkElement el, Point center)
    {
        double s = el is RadialIcon ri ? ri.IconSize : _config.Settings.IconSize;
        double left = center.X - s / 2;
        double top = center.Y - s / 2;
        // A longer, gently overshooting glide reads as more "elegant" than a
        // quick snap. BackEase eases in and out with a soft settle at the end;
        // the duration (not the frame rate) is what sets the perceived pace.
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.18 };
        var dur = TimeSpan.FromMilliseconds(340);
        var la = new DoubleAnimation(left, dur) { EasingFunction = ease };
        var ta = new DoubleAnimation(top, dur) { EasingFunction = ease };
        el.BeginAnimation(Canvas.LeftProperty, la);
        el.BeginAnimation(Canvas.TopProperty, ta);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_pressedIcon == null)
        {
            // Clicking empty space no longer closes the pinned panel — it stays
            // open for drag-to-add; use Esc to dismiss it.
            return;
        }

        var icon = _pressedIcon;
        bool wasDragging = _dragging;
        Point p = e.GetPosition(PanelCanvas);
        int ring = _dragTargetRing;
        int pos = _dragTargetPos;

        PanelCanvas.ReleaseMouseCapture();
        _pressedIcon = null;
        _dragging = false;
        _dragTargetRing = -1;
        _dragTargetPos = -1;

        if (!wasDragging)
        {
            Launch(icon.Entry);
            return;
        }

        // Drag finished: dismiss the overlay ghost (a Rebuild below recreates the
        // real icon on every drop path).
        EndDragGhost(icon);

        // Glass theme: dropping a dragged icon onto the left-edge dock pins it
        // there (the main-dock entry stays). Checked before delete/reorder so a
        // drag toward the left edge adds rather than deletes.
        if (_theme.ShowGlassPanel && DropToSideDock != null)
        {
            Point screen = PointToScreen(p);
            if (DropToSideDock(screen, icon.Entry))
            {
                GlassDragActiveChanged?.Invoke(false);
                Rebuild();   // snap the main-dock icon back into its slot
                return;
            }
        }

        if (_theme.ShowGlassPanel)
            GlassDragActiveChanged?.Invoke(false);

        if (IsDeleteDrop(p))
        {
            DeleteEntry(icon.Entry);
        }
        else if (_theme.IsSaturn)
        {
            CommitArrangement(icon.Entry, ring, pos, p);
        }
        else if (_theme.SupportsGridReorder)
        {
            CommitGridArrangement(icon.Entry, pos, p);
        }
        else
        {
            // No reorder support — snap the dragged icon back.
            Rebuild();
        }
    }

    /// <summary>Distance past the outer ring beyond which a dropped icon is deleted.</summary>
    private double DeleteRadius => _theme.IsSaturn
        ? InnerRadius + RingStep + _config.Settings.IconSize * 1.25
        : _outerRadius + _config.Settings.IconSize * 0.8;

    /// <summary>True when an icon dragged to PanelCanvas point <paramref name="p"/>
    /// should be deleted on drop. For the glass dock this is simply "outside the
    /// slab" so flicking the icon off the dock removes it; the ring themes use a
    /// radial threshold.</summary>
    private bool IsDeleteDrop(Point p) =>
        _theme.ShowGlassPanel
            ? !GlassSlabRect.Contains(p)
            : (p - _center).Length > DeleteRadius;

    private void CommitArrangement(AppEntry entry, int ring, int pos, Point dropPoint)
    {
        int src = _config.Apps.IndexOf(entry);
        if (src < 0)
        {
            Rebuild();
            return;
        }

        if (ring < 0)
            (ring, pos) = ComputeDragTarget(dropPoint, src);

        var (slotOfEntry, newR0) = ComputeArrangement(src, ring, pos);
        int n = _config.Apps.Count;

        // Reorder the entries by their new slot so Rebuild() (entry i -> slot i)
        // reproduces the arrangement, and persist the new inner-ring count.
        var ordered = new AppEntry[n];
        for (int i = 0; i < n; i++)
            ordered[slotOfEntry[i]] = _config.Apps[i];

        _config.Apps.Clear();
        foreach (var a in ordered)
            _config.Apps.Add(a);

        _config.Settings.Ring0Count = Math.Clamp(newR0, 0, n);

        _persist();
        Rebuild();
        AppsChanged?.Invoke();
    }

    private void DeleteEntry(AppEntry entry)
    {
        int n = _config.Apps.Count;
        int r0 = EffectiveRing0Count(n);
        int idx = _config.Apps.IndexOf(entry);

        _config.Apps.Remove(entry);

        // If an inner-ring icon was removed, keep the inner-ring count in step.
        if (idx >= 0 && idx < r0)
            _config.Settings.Ring0Count = Math.Max(0, r0 - 1);

        _persist();
        Rebuild();
        AppsChanged?.Invoke();
    }

    private void CancelDrag()
    {
        if (_pressedIcon != null)
        {
            bool wasDragging = _dragging;
            PanelCanvas.ReleaseMouseCapture();
            EndDragGhost(_pressedIcon);
            _pressedIcon = null;
            _dragging = false;
            _dragTargetRing = -1;
            _dragTargetPos = -1;
            if (wasDragging && _theme.ShowGlassPanel)
                GlassDragActiveChanged?.Invoke(false);
        }
    }

    private void Launch(AppEntry entry)
    {
        AppLauncher.Launch(entry, HidePanel);
    }
}
