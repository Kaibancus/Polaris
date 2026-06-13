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
    // ---- Right-click context menus ---------------------------------------

    /// <summary>Right-click menu for a PINNED icon: always offers "unpin from the
    /// resident region", plus "close window(s)" when the app is currently
    /// running.</summary>
    private void ShowPinnedIconMenu(RadialIcon icon)
    {
        var entry = icon.Entry;
        var items = new List<(string text, Action action)>
        {
            ("从常驻区取消固定", () => RemoveFromLeftDock(entry)),
        };
        if (icon.IsRunning)
            items.Add(("关闭窗口", () => CloseEntryWindows(entry)));
        ShowDockMenu(icon, items, onDone => icon.TryFadePreview(onDone));
    }

    /// <summary>Right-click menu for a RUNNING-strip tile: pin the app to the
    /// resident region (when it has a real exe path) and/or close its window(s).</summary>
    private void ShowRunTileMenu(FrameworkElement tile, TaskbarApp app)
    {
        var items = new List<(string text, Action action)>();
        if (!string.IsNullOrWhiteSpace(app.Path))
            items.Add(("固定到常驻区", () => PinRunningApp(app)));
        items.Add(("关闭窗口", () => CloseTaskbarAppWindows(app)));
        if (items.Count > 0)
            ShowDockMenu(tile, items, FadeOpenRunPreview);
    }

    /// <summary>Fades out any open running-strip thumbnail preview, invoking
    /// <paramref name="onDone"/> after the animation; returns false when none is
    /// open.</summary>
    private bool FadeOpenRunPreview(Action onDone)
    {
        WindowPreviewPopup? open = null;
        foreach (var p in _runPopups)
            if (p.IsOpen) { open = p; break; }
        if (open == null)
            return false;
        // Dismiss the rest instantly; fade the representative one.
        foreach (var p in _runPopups)
            if (p.IsOpen && !ReferenceEquals(p, open))
                p.Close();
        open.CloseAnimated(onDone);
        return true;
    }

    /// <summary>Builds a small dark, dock-styled popup menu anchored to
    /// <paramref name="target"/> and opening toward the screen interior. The dock
    /// is held visible while it is open. If a hover thumbnail preview is currently
    /// shown (resolved via <paramref name="fadePreview"/>), it is faded out first
    /// and the menu only appears once that animation has finished, so the two
    /// never overlap.</summary>
    private void ShowDockMenu(UIElement target, List<(string text, Action action)> items,
        Func<Action, bool>? fadePreview = null)
    {
        CloseDockMenu();
        if (items.Count == 0)
            return;

        if (fadePreview != null)
        {
            var pendingItems = items;
            var pendingTarget = target;
            // Hold the dock visible across the fade so it doesn't auto-hide in
            // the gap between the preview closing and the menu opening.
            _menuHold = true;
            UpdateVisibility();
            if (fadePreview(() => BuildAndShowDockMenu(pendingTarget, pendingItems)))
                return;
            // No preview was actually open — fall through and show immediately.
        }

        BuildAndShowDockMenu(target, items);
    }

    private void BuildAndShowDockMenu(UIElement target, List<(string text, Action action)> items)
    {
        CloseDockMenu();
        if (items.Count == 0)
        {
            _menuHold = false;
            UpdateVisibility();
            return;
        }

        var panel = new StackPanel();
        foreach (var (text, action) in items)
        {
            var label = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)),
            };
            var row = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Padding = new Thickness(13, 7, 18, 7),
                Cursor = Cursors.Hand,
                Child = label,
            };
            row.MouseEnter += (_, _) =>
                row.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            var act = action;
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                CloseDockMenu();
                act();
            };
            panel.Children.Add(row);
        }

        var shell = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1E, 0x1E, 0x22)),
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 3,
                Direction = 270,
                Opacity = 0.5,
                Color = Colors.Black,
            },
            Child = panel,
        };

        double half = GIcon / 2.0;       // visible glyph half-extent
        const double gap = 4.0;          // small breathing space from the icon
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            Child = shell,
            StaysOpen = false,
            AllowsTransparency = true,
            PlacementTarget = target,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Custom,
            CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            {
                // Anchor to the icon's visible glyph rather than the control's
                // (larger) render bounds, so the menu sits snug against the icon
                // instead of floating far out past its whitespace.
                double hMain = Math.Min(half, targetSize.Width / 2.0);
                double vMain = Math.Min(half, targetSize.Height / 2.0);
                double cx = targetSize.Width / 2.0;
                double cy = targetSize.Height / 2.0;
                double x, y;
                System.Windows.Controls.Primitives.PopupPrimaryAxis axis;
                switch (_side)
                {
                    case DockSide.Left:   // open toward screen interior (right)
                        x = cx + hMain + gap;
                        y = cy - popupSize.Height / 2.0;
                        axis = System.Windows.Controls.Primitives.PopupPrimaryAxis.Vertical;
                        break;
                    case DockSide.Right:  // open left
                        x = cx - hMain - gap - popupSize.Width;
                        y = cy - popupSize.Height / 2.0;
                        axis = System.Windows.Controls.Primitives.PopupPrimaryAxis.Vertical;
                        break;
                    case DockSide.Top:    // open below
                        x = cx - popupSize.Width / 2.0;
                        y = cy + vMain + gap;
                        axis = System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal;
                        break;
                    default:              // Bottom → open above
                        x = cx - popupSize.Width / 2.0;
                        y = cy - vMain - gap - popupSize.Height;
                        axis = System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal;
                        break;
                }
                return new[]
                {
                    new System.Windows.Controls.Primitives.CustomPopupPlacement(
                        new Point(x, y), axis),
                };
            },
            PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade,
        };
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_dockMenu, popup))
                _dockMenu = null;
            _menuHold = false;
            UpdateVisibility();
        };
        _dockMenu = popup;
        _menuHold = true;
        UpdateVisibility();   // ensure the dock is held visible before opening
        popup.IsOpen = true;
    }

    private void CloseDockMenu()
    {
        if (_dockMenu != null)
        {
            _dockMenu.IsOpen = false;   // raises Closed → clears _menuHold
            _dockMenu = null;
        }
    }

    /// <summary>Closes every window of a pinned app (used by its right-click
    /// "关闭窗口"). Resolves the app's windows the same way the running strip and
    /// hover preview do.</summary>
    private void CloseEntryWindows(AppEntry entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Path))
            return;
        List<WindowPreview> wins;
        try
        {
            // GetWindowsForEntry already tries the AUMID first and then falls back
            // to the resolved exe / install-folder match. Non-packaged AppsFolder
            // launchers (e.g. iQiyi) carry no window AUMID, so calling
            // GetWindowsByAumid alone would find nothing and close no window.
            wins = WindowPreviewService.GetWindowsForEntry(entry.Path, entry.Arguments);
        }
        catch { wins = new List<WindowPreview>(); }
        foreach (var w in wins)
            WindowPreviewService.CloseWindow(w.Handle);
        ScheduleRunningRefresh();
    }

    /// <summary>Closes every window of a running-strip tile.</summary>
    private void CloseTaskbarAppWindows(TaskbarApp app)
    {
        List<WindowPreview> wins;
        try
        {
            wins = app.Aumid != null
                ? WindowPreviewService.GetWindowsByAumid(app.Aumid)
                : string.IsNullOrEmpty(app.Path)
                    ? WindowPreviewService.GetWindowsByHandle(app.Window)
                    : WindowPreviewService.GetWindowsForEntry(app.Path, null);
        }
        catch { wins = new List<WindowPreview>(); }
        if (wins.Count == 0 && app.Window != IntPtr.Zero)
        {
            WindowPreviewService.CloseWindow(app.Window);
        }
        else
        {
            foreach (var w in wins)
                WindowPreviewService.CloseWindow(w.Handle);
        }
        ScheduleRunningRefresh();
    }

    /// <summary>Pins a running app to the resident region from its run-tile menu.</summary>
    private void PinRunningApp(TaskbarApp app)
    {
        if (string.IsNullOrWhiteSpace(app.Path))
            return;
        var entry = Polaris.Services.ShortcutResolver.CreateEntry(app.Path);
        if (entry == null)
            return;
        if (!string.IsNullOrWhiteSpace(app.Title) && string.IsNullOrWhiteSpace(entry.Name))
            entry.Name = app.Title!;
        AddFromMainDock(entry);
    }

    /// <summary>Re-polls running windows shortly after a close so the strip drops
    /// the closed tile (the window takes a moment to actually disappear).</summary>
    private void ScheduleRunningRefresh()
    {
        var t = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        t.Tick += (s, _) =>
        {
            ((System.Windows.Threading.DispatcherTimer)s!).Stop();
            if (_shown)
                RefreshRunning();
        };
        t.Start();
    }

    /// <summary>Raised when the left dock mutates the shared main-dock app list
    /// (e.g. a desktop shortcut dropped here), so the main dock can refresh.</summary>
    public event Action? MainDockChanged;

}
