using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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

    // Per-tile arc layout so a hovered tile can push its neighbours aside
    // (dock-style magnification).
    private sealed class TaskbarTile
    {
        public FrameworkElement Element = null!;
        public double BaseAngle;          // arc layout (Saturn)
        public Point BaseCenter;          // row layout (Glass)
    }
    private readonly List<TaskbarTile> _taskbarTiles = new();
    private double _taskbarArcR;
    private double _taskbarTileSize;
    private bool _taskbarIsRow;           // true => Glass row, false => Saturn arc
    // Hover-thumbnail popups, one per tile, closed when the tile set is cleared.
    private readonly List<WindowPreviewPopup> _taskbarPopups = new();
    // Signature of the currently drawn tile set + geometry. Used to skip a full
    // rebuild (which would restart every tile's sweep animation) when nothing
    // relevant changed between 1.5 s refresh ticks.
    private string? _taskbarSignature;
    private const double TaskbarMidAngle = Math.PI / 2;

    // Taskbar tile sizing / arc geometry, all relative to the icon size.
    private const double TaskbarArcHalfSpanDeg = 55; // max half-span around the bottom

    /// <summary>
    /// Refreshes the running-taskbar-app arc. Enumeration (process paths) runs on
    /// a background thread; the resulting tiles are drawn on the UI thread. A
    /// token guards against overlapping refreshes and a hide mid-flight.
    /// </summary>
    private void RefreshTaskbarApps()
    {
        // The bottom running-app arc has been removed: running apps for every
        // theme (Saturn included) are now surfaced in the left side dock's
        // running strip instead, so this method only ever clears any stale tiles.
        ClearTaskbarIcons();
    }

    private void ClearTaskbarIcons()
    {
        foreach (var p in _taskbarPopups)
            p.Close();
        _taskbarPopups.Clear();
        foreach (var el in _taskbarIcons)
            PanelCanvas.Children.Remove(el);
        _taskbarIcons.Clear();
        _taskbarTiles.Clear();
        _taskbarSignature = null;
    }

    private void DrawTaskbarArc(List<TaskbarApp> apps)
    {
        double icon = EffectiveIconSize;
        double tile = icon;   // same footprint as an inner-ring icon

        // Place the arc just outside the rendered ring disc (which reaches
        // _outerRadius + an outer icon), with a small gap.
        double discR = _outerRadius + icon * OuterIconScale;
        double arcR = discR + tile * 0.75;

        // Skip the rebuild when neither the app set nor the geometry changed, so
        // each tile's continuous sweep animation is not restarted every tick.
        var sb = new System.Text.StringBuilder();
        sb.Append(arcR.ToString("0.0")).Append('|').Append(tile.ToString("0.0"))
          .Append('|').Append(_center.X.ToString("0.0")).Append(',').Append(_center.Y.ToString("0.0"))
          .Append('#');
        foreach (var a in apps)
            sb.Append(a.Aumid ?? a.Path).Append(';');
        string signature = sb.ToString();
        if (signature == _taskbarSignature && _taskbarTiles.Count == apps.Count)
            return;

        ClearTaskbarIcons();
        if (apps.Count == 0)
            return;

        int m = apps.Count;
        double maxHalfSpan = TaskbarArcHalfSpanDeg * Math.PI / 180.0;
        double desiredStep = (tile * 1.18) / arcR;       // even chord spacing
        double step = m > 1
            ? Math.Min(desiredStep, (2 * maxHalfSpan) / (m - 1))
            : 0;

        _taskbarArcR = arcR;
        _taskbarTileSize = tile;
        _taskbarSignature = signature;
        _taskbarIsRow = false;

        PruneTaskbarIconCache(apps);

        for (int k = 0; k < m; k++)
        {
            double angle = TaskbarMidAngle + (k - (m - 1) / 2.0) * step;
            double x = _center.X + arcR * Math.Cos(angle);
            double y = _center.Y + arcR * Math.Sin(angle) * RingTiltY;

            int index = k;
            var el = BuildTaskbarTile(apps[k], tile, index);
            Canvas.SetLeft(el, x - tile / 2);
            Canvas.SetTop(el, y - tile / 2);
            PanelCanvas.Children.Add(el);
            _taskbarIcons.Add(el);
            _taskbarTiles.Add(new TaskbarTile { Element = el, BaseAngle = angle, BaseCenter = new Point(x, y) });
        }
    }

    /// <summary>Glass theme: a horizontal row of running taskbar apps centred
    /// just below the liquid-glass panel.</summary>
    private void DrawTaskbarRow(List<TaskbarApp> apps)
    {
        double icon = EffectiveIconSize;
        double tile = icon * GlassIconScale;   // same footprint as a glass grid icon
        double cellW = icon * LiquidGlassTheme.ColumnPitch;   // same column pitch as the grid

        // The dock body is a fixed 4-row block centred on GlassDockCenter; the
        // taskbar strip is carved at its bottom (see GlassTaskbarStripHeight /
        // DrawGlassPanel). dock and strip are ONE continuous glass panel split
        // only by an engraved seam, so the row draws no slab of its own — it
        // just centres its tiles in the reserved strip below the dock's bottom.
        double dockBottom = GlassDockCenterY + GlassDockBodyHeight / 2.0;
        double rowVPad = tile * 0.42;
        double rowY = dockBottom + rowVPad + tile / 2.0;

        // Cap the row at 6 tiles. When more apps are running, show the first 5
        // and a trailing "+N" tile standing in for the rest.
        const int maxTiles = 6;
        List<TaskbarApp> display = apps;
        int overflow = 0;
        if (apps.Count > maxTiles)
        {
            display = apps.GetRange(0, maxTiles - 1);
            overflow = apps.Count - (maxTiles - 1);
        }
        int m = display.Count + (overflow > 0 ? 1 : 0);

        // Skip the rebuild when neither the app set nor the geometry changed.
        var sb = new System.Text.StringBuilder();
        sb.Append("row|").Append(rowY.ToString("0.0")).Append('|').Append(tile.ToString("0.0"))
          .Append('|').Append(_center.X.ToString("0.0")).Append('#');
        foreach (var a in display)
            sb.Append(a.Aumid ?? a.Path).Append(';');
        if (overflow > 0)
            sb.Append("+").Append(overflow);
        string signature = sb.ToString();
        if (signature == _taskbarSignature && _taskbarTiles.Count == m)
            return;

        ClearTaskbarIcons();
        if (m == 0)
            return;

        _taskbarTileSize = tile;
        _taskbarSignature = signature;
        _taskbarIsRow = true;

        PruneTaskbarIconCache(display);

        double rowW = (m - 1) * cellW;
        double x0 = _center.X - rowW / 2.0;

        for (int k = 0; k < m; k++)
        {
            double x = x0 + k * cellW;
            double y = rowY;

            bool isOverflowTile = overflow > 0 && k == m - 1;
            var el = isOverflowTile
                ? BuildOverflowTile(overflow, tile)
                : BuildTaskbarTile(display[k], tile, k);
            Canvas.SetLeft(el, x - tile / 2);
            Canvas.SetTop(el, y - tile / 2);
            PanelCanvas.Children.Add(el);
            _taskbarIcons.Add(el);
            _taskbarTiles.Add(new TaskbarTile { Element = el, BaseCenter = new Point(x, y) });
        }
    }

    /// <summary>Builds the trailing "+N" tile shown when more than the row's
    /// six-tile cap are running. Mirrors the glass tiles' plate and hover zoom
    /// but carries no icon, running animation or preview popup.</summary>
    private FrameworkElement BuildOverflowTile(int extra, double size)
    {
        var (idleBg, hoverBg, radius) = TaskbarTileChrome(size);

        var label = new TextBlock
        {
            Text = "+" + extra,
            FontSize = size * 0.34,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var plate = new Border
        {
            CornerRadius = new CornerRadius(radius),
            Background = idleBg,
            Child = label,
        };

        var root = new Grid
        {
            Width = size,
            Height = size,
            Background = Brushes.Transparent,
            ToolTip = $"另有 {extra} 个正在运行",
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
        };
        root.Children.Add(plate);

        var scale = (ScaleTransform)root.RenderTransform;
        int index = _taskbarTiles.Count;
        AttachTaskbarHover(root, plate, scale, idleBg, hoverBg, index);

        return root;
    }

    /// <summary>Dock-style magnification: slide tiles away from the hovered one
    /// so the enlarged tile has room. The gap is largest next to the hovered
    /// tile and tapers off further out.</summary>
    private void MagnifyTaskbar(int hovered)
    {
        if (_taskbarTiles.Count == 0)
            return;

        const double decay = 0.5;
        // Extra room the 1.7x zoom needs on each side (~0.35 half-widths). For
        // the arc this is an angular delta; for the row it is a pixel delta.
        double push = _taskbarIsRow
            ? _taskbarTileSize * 0.35
            : (_taskbarTileSize * 0.35) / _taskbarArcR;

        for (int j = 0; j < _taskbarTiles.Count; j++)
        {
            int d = j - hovered;
            double offset = 0;
            if (d != 0)
            {
                int dist = Math.Abs(d);
                // Cumulative, decreasing increments -> farther tiles move more,
                // so neighbours never overlap.
                double mag = 0;
                for (int i = 1; i <= dist; i++)
                    mag += push * Math.Pow(decay, i - 1);
                offset = Math.Sign(d) * mag;
            }
            MoveTile(_taskbarTiles[j], offset);
        }
    }

    private void ResetTaskbarMagnify()
    {
        foreach (var t in _taskbarTiles)
            MoveTile(t, 0);
    }

    private void MoveTile(TaskbarTile t, double offset)
    {
        double x, y;
        if (_taskbarIsRow)
        {
            x = t.BaseCenter.X + offset;
            y = t.BaseCenter.Y;
        }
        else
        {
            double angle = t.BaseAngle + offset;
            x = _center.X + _taskbarArcR * Math.Cos(angle);
            y = _center.Y + _taskbarArcR * Math.Sin(angle) * RingTiltY;
        }
        var dur = new Duration(TimeSpan.FromMilliseconds(140));
        t.Element.BeginAnimation(Canvas.LeftProperty,
            new DoubleAnimation(x - _taskbarTileSize / 2, dur) { AccelerationRatio = 0.3, DecelerationRatio = 0.5 });
        t.Element.BeginAnimation(Canvas.TopProperty,
            new DoubleAnimation(y - _taskbarTileSize / 2, dur) { AccelerationRatio = 0.3, DecelerationRatio = 0.5 });
    }

    private FrameworkElement BuildTaskbarTile(TaskbarApp app, double size, int index)
    {
        if (!_taskbarIconCache.TryGetValue(app.Path, out var bmp))
        {
            bmp = IconExtractor.GetIcon(app.Path);
            _taskbarIconCache[app.Path] = bmp;
        }

        var (idleBg, hoverBg, radius) = TaskbarTileChrome(size);
        double pad = _theme.ShowGlassPanel ? size * 0.125 : size * 0.16;

        var image = new Image
        {
            Source = bmp,
            Stretch = Stretch.Uniform,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        var plate = new Border
        {
            CornerRadius = new CornerRadius(radius),
            Background = idleBg,
            Padding = new Thickness(pad),
            Child = image,
            // Bake the (HighQuality-scaled) icon to a texture so the always-on running
            // sweep/glow — and, in glass mode, the orbit light — don't re-sample the
            // image on every layered-window recomposite. The hover zoom is a transform
            // on the parent (cache stays valid); cached at 2.0 like the pinned grid
            // icons so it remains crisp when magnified to 1.7x.
            CacheMode = new System.Windows.Media.BitmapCache(2.0),
        };

        // Soft, breathing glow (blurred, behind the sweep). Matches the ring
        // icons' running indicator colour. BitmapCache rasterises the blur once
        // so the breathing Opacity pulse is a pure GPU composite.
        var glow = new Border
        {
            CornerRadius = new CornerRadius(radius),
            BorderThickness = new Thickness(2.5),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xA9, 0xFF)),
            IsHitTestVisible = false,
            Opacity = 0,
            Effect = new BlurEffect { Radius = 10 },
            CacheMode = new System.Windows.Media.BitmapCache(),
        };

        // Flowing running sweep matching the ring icons: a bright spot rotating
        // around the border via a RelativeTransform on the gradient brush.
        var sweepTransform = new RotateTransform(0) { CenterX = 0.5, CenterY = 0.5 };
        var sweepBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            RelativeTransform = sweepTransform,
        };
        sweepBrush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#103DA9FF"), 0.0));
        sweepBrush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#6657C8FF"), 0.28));
        sweepBrush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#FF6FD3FF"), 0.5));
        sweepBrush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#6657C8FF"), 0.72));
        sweepBrush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#103DA9FF"), 1.0));
        var sweep = new Border
        {
            CornerRadius = new CornerRadius(radius),
            BorderThickness = new Thickness(2.5),
            BorderBrush = sweepBrush,
            IsHitTestVisible = false,
        };

        // The whole tile is the hit-test target AND the element that zooms. A
        // transparent background makes the entire square hit-testable; because a
        // centred scale-up only grows the geometry outward, an interior cursor
        // always stays inside, so the zoom never oscillates.
        var root = new Grid
        {
            Width = size,
            Height = size,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = System.IO.Path.GetFileNameWithoutExtension(app.Path),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
        };
        root.Children.Add(glow);
        root.Children.Add(plate);
        root.Children.Add(sweep);

        // Always-on running animation (every taskbar app is, by definition, running).
        // Tick at the oversampled rate so the slow rotation / pulse stay smooth
        // and don't beat against the 59.94 Hz present (which reads as judder).
        var sweepAnim = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(4.2)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Timeline.SetDesiredFrameRate(sweepAnim, App.AmbientFrameRate);
        sweepTransform.BeginAnimation(RotateTransform.AngleProperty, sweepAnim);
        var glowAnim = new DoubleAnimation(0.35, 0.8, new Duration(TimeSpan.FromSeconds(2.2)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Timeline.SetDesiredFrameRate(glowAnim, App.AmbientFrameRate);
        glow.BeginAnimation(OpacityProperty, glowAnim);

        IntPtr win = app.Window;
        var scale = (ScaleTransform)root.RenderTransform;

        // Hover-thumbnail popup: show a live window preview even with a single
        // open window so the user can peek/pick any running app.
        string? aumid = app.Aumid;
        string appPath = app.Path;
        var preview = new WindowPreviewPopup(
            root,
            () => aumid != null
                ? WindowPreviewService.GetWindowsByAumid(aumid)
                : WindowPreviewService.GetWindows(appPath),
            minWindows: 1,
            onActivated: HidePanel);
        _taskbarPopups.Add(preview);

        AttachTaskbarHover(root, plate, scale, idleBg, hoverBg, index,
            preview.OnPointerEnter, preview.OnPointerLeave);
        root.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            preview.Close();
            WindowPreviewService.Activate(win);
            HidePanel();
        };

        return root;
    }

    /// <summary>Removes cached taskbar icons for apps that are no longer being
    /// drawn, keying on each app's launcher path.</summary>
    private void PruneTaskbarIconCache(IEnumerable<TaskbarApp> apps)
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in apps)
            live.Add(a.Path);
        var stale = new List<string>();
        foreach (var key in _taskbarIconCache.Keys)
            if (!live.Contains(key))
                stale.Add(key);
        foreach (var key in stale)
            _taskbarIconCache.Remove(key);
    }

    /// <summary>Idle/hover plate brushes and corner radius shared by every
    /// taskbar tile. Saturn uses a dark plate; glass matches the pinned grid
    /// icons' liquid-glass background.</summary>
    private (SolidColorBrush idle, SolidColorBrush hover, double radius) TaskbarTileChrome(double size)
    {
        bool glass = _theme.ShowGlassPanel;
        var idle = glass
            ? new SolidColorBrush(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Color.FromArgb(0x33, 0x10, 0x12, 0x18));
        var hover = glass
            ? new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Color.FromArgb(0x66, 0x2A, 0x2E, 0x3A));
        double radius = glass ? 12 : size * 0.22;
        return (idle, hover, radius);
    }

    /// <summary>Wires the shared dock-style hover behaviour (plate highlight,
    /// 1.7x zoom, neighbour magnify) onto a taskbar tile. Optional callbacks let
    /// a tile hook extra enter/leave work (e.g. its preview popup).</summary>
    private void AttachTaskbarHover(Grid root, Border plate, ScaleTransform scale,
        SolidColorBrush idleBg, SolidColorBrush hoverBg, int index,
        Action? onEnter = null, Action? onLeave = null)
    {
        var dur = new Duration(TimeSpan.FromMilliseconds(110));
        root.MouseEnter += (_, _) =>
        {
            plate.Background = hoverBg;
            Panel.SetZIndex(root, 100);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.7, dur));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.7, dur));
            MagnifyTaskbar(index);
            onEnter?.Invoke();
        };
        root.MouseLeave += (_, _) =>
        {
            plate.Background = idleBg;
            Panel.SetZIndex(root, 0);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, dur));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, dur));
            ResetTaskbarMagnify();
            onLeave?.Invoke();
        };
    }
}
