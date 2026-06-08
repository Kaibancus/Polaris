using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Polaris.Services;

namespace Polaris.Views;

// Saturn theme: a concentric arc of currently-running taskbar apps that are NOT
// pinned into Polaris, drawn just below/outside the ring system following the
// same tilted-ellipse curvature.
public partial class RadialWindow
{
    private readonly List<FrameworkElement> _taskbarIcons = new();
    // Icons for taskbar apps are cached separately from the configured-app cache
    // (PruneIconCache would otherwise evict them every Rebuild).
    private readonly Dictionary<string, BitmapSource?> _taskbarIconCache = new();
    private int _taskbarToken;

    // Taskbar tile sizing / arc geometry, all relative to the icon size.
    private const double TaskbarIconScale = 0.72;   // tile size vs EffectiveIconSize
    private const double TaskbarArcHalfSpanDeg = 55; // max half-span around the bottom

    /// <summary>
    /// Refreshes the running-taskbar-app arc. Enumeration (process paths) runs on
    /// a background thread; the resulting tiles are drawn on the UI thread. A
    /// token guards against overlapping refreshes and a hide mid-flight.
    /// </summary>
    private void RefreshTaskbarApps()
    {
        if (!_theme.IsSaturn || !_shown)
        {
            ClearTaskbarIcons();
            return;
        }

        // Build the exclusion sets (configured apps) on the UI thread.
        var excludePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludeAumids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in _config.Apps)
        {
            if (string.IsNullOrWhiteSpace(a.Path))
                continue;
            string? aumid = WindowPreviewService.TryGetLauncherAumid(a.Path, a.Arguments);
            if (aumid != null)
            {
                excludeAumids.Add(aumid);
            }
            else
            {
                try { excludePaths.Add(System.IO.Path.GetFullPath(a.Path)); }
                catch { excludePaths.Add(a.Path); }
            }
        }

        int token = ++_taskbarToken;
        System.Threading.Tasks.Task.Run(() =>
        {
            List<TaskbarApp> apps;
            try { apps = WindowPreviewService.GetTaskbarApps(); }
            catch { return; }

            var filtered = new List<TaskbarApp>();
            foreach (var ta in apps)
            {
                string full;
                try { full = System.IO.Path.GetFullPath(ta.Path); }
                catch { full = ta.Path; }

                if (excludePaths.Contains(full))
                    continue;
                if (ta.Aumid != null && excludeAumids.Contains(ta.Aumid))
                    continue;
                filtered.Add(ta);
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (token != _taskbarToken || !_shown || !_theme.IsSaturn)
                    return;
                DrawTaskbarArc(filtered);
            });
        });
    }

    private void ClearTaskbarIcons()
    {
        foreach (var el in _taskbarIcons)
            PanelCanvas.Children.Remove(el);
        _taskbarIcons.Clear();
    }

    private void DrawTaskbarArc(List<TaskbarApp> apps)
    {
        ClearTaskbarIcons();
        if (apps.Count == 0)
            return;

        double icon = EffectiveIconSize;
        double tile = icon * TaskbarIconScale;

        // Place the arc just outside the rendered ring disc (which reaches
        // _outerRadius + an outer icon), with a small gap.
        double discR = _outerRadius + icon * OuterIconScale;
        double arcR = discR + tile * 0.75;

        int m = apps.Count;
        const double mid = Math.PI / 2;                  // bottom of the ellipse
        double maxHalfSpan = TaskbarArcHalfSpanDeg * Math.PI / 180.0;
        double desiredStep = (tile * 1.18) / arcR;       // even chord spacing
        double step = m > 1
            ? Math.Min(desiredStep, (2 * maxHalfSpan) / (m - 1))
            : 0;

        // Prune the taskbar icon cache to the apps we are about to draw.
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in apps)
            live.Add(a.Path);
        var stale = new List<string>();
        foreach (var key in _taskbarIconCache.Keys)
            if (!live.Contains(key))
                stale.Add(key);
        foreach (var key in stale)
            _taskbarIconCache.Remove(key);

        for (int k = 0; k < m; k++)
        {
            double angle = mid + (k - (m - 1) / 2.0) * step;
            double x = _center.X + arcR * Math.Cos(angle);
            double y = _center.Y + arcR * Math.Sin(angle) * RingTiltY;

            var el = BuildTaskbarTile(apps[k], tile);
            Canvas.SetLeft(el, x - tile / 2);
            Canvas.SetTop(el, y - tile / 2);
            PanelCanvas.Children.Add(el);
            _taskbarIcons.Add(el);
        }
    }

    private FrameworkElement BuildTaskbarTile(TaskbarApp app, double size)
    {
        if (!_taskbarIconCache.TryGetValue(app.Path, out var bmp))
        {
            bmp = IconExtractor.GetIcon(app.Path);
            _taskbarIconCache[app.Path] = bmp;
        }

        // Background plate at 80% transparency (20% opaque), per request.
        var idleBg = new SolidColorBrush(Color.FromArgb(0x33, 0x10, 0x12, 0x18));
        var hoverBg = new SolidColorBrush(Color.FromArgb(0x66, 0x2A, 0x2E, 0x3A));

        var image = new Image
        {
            Source = bmp,
            Stretch = Stretch.Uniform,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        var plate = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.22),
            Background = idleBg,
            Padding = new Thickness(size * 0.16),
            Cursor = Cursors.Hand,
            ToolTip = System.IO.Path.GetFileNameWithoutExtension(app.Path),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            Child = image,
        };

        IntPtr win = app.Window;
        var scale = (ScaleTransform)plate.RenderTransform;
        var dur = new Duration(TimeSpan.FromMilliseconds(110));

        plate.MouseEnter += (_, _) =>
        {
            plate.Background = hoverBg;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.18, dur));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.18, dur));
        };
        plate.MouseLeave += (_, _) =>
        {
            plate.Background = idleBg;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, dur));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, dur));
        };
        plate.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            WindowPreviewService.Activate(win);
            HidePanel();
        };

        return plate;
    }
}
