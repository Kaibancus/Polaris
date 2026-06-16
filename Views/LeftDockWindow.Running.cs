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
    // ---- Running-but-unpinned strip --------------------------------------

    private readonly List<FrameworkElement> _runTiles = new();
    // Per-running-tile magnification-wave state, parallel to _runTiles, so the
    // macOS-style wave flows continuously from the pinned column into the running
    // strip (same field driven by the cursor for both halves of the dock).
    private readonly List<ScaleTransform> _runScale = new();
    private readonly List<TranslateTransform> _runTrans = new();
    private readonly List<double> _runCenterMain = new();   // un-scrolled slot centres
    private double[] _runWaveCur = Array.Empty<double>();
    // Hover-thumbnail popups for the running-strip tiles, torn down whenever the
    // strip is rebuilt so they never leak or linger over a stale tile.
    private readonly List<WindowPreviewPopup> _runPopups = new();

    /// <summary>Direction a hover preview opens for this dock edge: toward the
    /// screen interior (Left dock → right, Right dock → left, Top dock → down).</summary>
    private PreviewPlacement PreviewPlacementForSide() => _side switch
    {
        DockSide.Top => PreviewPlacement.Below,
        DockSide.Left => PreviewPlacement.Right,
        DockSide.Right => PreviewPlacement.Left,
        _ => PreviewPlacement.Above,
    };

    private void ClearRunPopups()
    {
        foreach (var p in _runPopups)
            p.Close();
        _runPopups.Clear();
    }

    private void RefreshRunning()
    {
        if (!_shown)
            return;

        var excludePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludeAumids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludeFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Some multi-process apps own their taskbar window from a HELPER process
        // whose exe name differs from the pinned launcher's, so the window can't be
        // matched to the pinned exe by path or name. We special-case the known
        // helpers by name (keyed on the pinned launcher's exe file name) instead of
        // excluding the launcher's whole folder subtree — the latter wrongly hides
        // unrelated apps installed underneath it (e.g. Steam games in
        // ...\Steam\steamapps\common\…).
        void AddLauncherHelpers(string exePath)
        {
            string launcher;
            try { launcher = System.IO.Path.GetFileName(exePath); }
            catch { return; }
            if (string.IsNullOrWhiteSpace(launcher))
                return;
            if (LauncherHelperExeNames.TryGetValue(launcher, out var helpers))
            {
                foreach (var h in helpers)
                    excludeFileNames.Add(h);
            }
        }
        // The running strip lists apps that are running but NOT in the resident
        // region (the side dock's pinned column = _config.LeftDockApps, which
        // mirrors the main dock's first ResidentCount entries). Non-resident
        // pinned apps are intentionally NOT excluded, so they surface in the
        // running strip while running (they don't otherwise appear on the side
        // dock). Only the resident apps — already shown as pinned tiles here — are
        // excluded to avoid duplicating them.
        var pinned = new List<AppEntry>(_config.LeftDockApps);
        foreach (var a in pinned)
        {
            if (string.IsNullOrWhiteSpace(a.Path))
                continue;
            string? aumid = WindowPreviewService.TryGetLauncherAumid(a.Path, a.Arguments);
            if (aumid != null)
            {
                excludeAumids.Add(aumid);
                // Non-packaged AppsFolder launchers (VS Code, File Explorer…) run
                // a plain exe whose windows carry no AUMID, so the running strip
                // lists them by exe path. Resolve that path so they are excluded
                // here too (otherwise the pinned app ALSO shows in the strip).
                string? exe = WindowPreviewService.TryResolveAppsFolderExe(aumid);
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    try { excludePaths.Add(System.IO.Path.GetFullPath(exe)); }
                    catch { excludePaths.Add(exe); }
                    try
                    {
                        string fn = System.IO.Path.GetFileName(exe);
                        if (!string.IsNullOrWhiteSpace(fn))
                            excludeFileNames.Add(fn);
                    }
                    catch (System.Exception ex) { Polaris.Services.Log.Debug("SideDock", "self-exe filename resolve failed", ex); }
                    AddLauncherHelpers(exe);
                }
            }
            else
            {
                try { excludePaths.Add(System.IO.Path.GetFullPath(a.Path)); }
                catch { excludePaths.Add(a.Path); }
                try
                {
                    string fn = System.IO.Path.GetFileName(a.Path);
                    if (!string.IsNullOrWhiteSpace(fn))
                        excludeFileNames.Add(fn);
                }
                catch (System.Exception ex) { Polaris.Services.Log.Debug("SideDock", "pinned-entry filename resolve failed", ex); }
                AddLauncherHelpers(a.Path);
            }
        }

        var pinnedIcons = new List<RadialIcon>(_pinnedIcons);
        var flashing = AttentionService.SnapshotFlashing();
        System.Threading.Tasks.Task.Run(() =>
        {
            // Process snapshot drives the running blue-glow on pinned icons,
            // exactly like the main dock.
            RunningAppTracker.RunningSnapshot snapshot;
            try { snapshot = RunningAppTracker.SnapshotRunning(); }
            catch { snapshot = null!; }

            List<string> explorerTitles;
            try { explorerTitles = WindowPreviewService.GetExplorerWindowTitles(); }
            catch { explorerTitles = new List<string>(); }

            System.Collections.Generic.HashSet<string> runningAumids;
            try { runningAumids = WindowPreviewService.SnapshotRunningAumids(); }
            catch { runningAumids = new System.Collections.Generic.HashSet<string>(); }

            List<TaskbarApp> apps;
            try { apps = WindowPreviewService.GetTaskbarApps(); }
            catch { apps = new List<TaskbarApp>(); }

            var filtered = new List<TaskbarApp>();
            foreach (var ta in apps)
            {
                string full;
                try { full = System.IO.Path.GetFullPath(ta.Path); }
                catch { full = ta.Path; }
                if (excludePaths.Contains(full))
                    continue;
                if (ta.Aumid != null)
                {
                    bool excluded = excludeAumids.Contains(ta.Aumid);
                    if (!excluded)
                    {
                        foreach (var ex in excludeAumids)
                        {
                            if (WindowPreviewService.AumidFamilyMatches(ta.Aumid, ex))
                            {
                                excluded = true;
                                break;
                            }
                        }
                    }
                    if (excluded)
                        continue;
                }
                try
                {
                    string fn = System.IO.Path.GetFileName(ta.Path);
                    if (!string.IsNullOrWhiteSpace(fn) && excludeFileNames.Contains(fn))
                        continue;
                }
                catch (System.Exception ex) { Polaris.Services.Log.Debug("SideDock", "running-app exclude filter failed", ex); }
                filtered.Add(ta);
            }

            // New-message attention badges for the pinned icons (mirrors the main
            // dock): flashing if any of the app's windows requests attention, with
            // a best-effort unread count parsed from the window titles.
            var attention = new Dictionary<RadialIcon, (bool flashing, int count)>();
            foreach (var icon in pinnedIcons)
            {
                bool isRunning = snapshot != null && RunningAppTracker.IsEntryRunning(
                    icon.Entry, snapshot, explorerTitles, runningAumids);
                if (!isRunning)
                {
                    attention[icon] = (false, 0);
                    continue;
                }
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
                catch (System.Exception ex) { Polaris.Services.Log.Debug("SideDock", "attention badge computation failed", ex); }
                attention[icon] = (flash, count);
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (!_shown)
                    return;
                ApplyPinnedRunning(snapshot, explorerTitles, runningAumids);
                foreach (var icon in pinnedIcons)
                {
                    if (attention.TryGetValue(icon, out var a))
                        icon.SetAttention(a.flashing, a.count);
                }
                ApplyRunning(filtered);
            });
        });
    }

    /// <summary>Lights up the flowing blue border on each pinned icon whose
    /// target program is currently running (mirrors the main dock).</summary>
    private void ApplyPinnedRunning(RunningAppTracker.RunningSnapshot? snapshot, List<string> explorerTitles,
        System.Collections.Generic.HashSet<string> runningAumids)
    {
        foreach (var icon in _pinnedIcons)
        {
            icon.IsRunning = snapshot != null && RunningAppTracker.IsEntryRunning(
                icon.Entry, snapshot, explorerTitles, runningAumids);
        }
    }

    private void ApplyRunning(List<TaskbarApp> apps)
    {
        // Up to RunningMaxComplete full tiles; if more are running, show that
        // many plus a trailing "+N" overflow marker (taskbar-style).
        List<TaskbarApp> display = apps;
        int overflow = 0;
        if (apps.Count > RunningMaxComplete)
        {
            display = apps.GetRange(0, RunningMaxComplete);
            overflow = apps.Count - RunningMaxComplete;
        }
        // Polaris itself always occupies the first running-strip slot, so the
        // running area is always present (even with no other apps running).
        int slots = 1 + display.Count + (overflow > 0 ? 1 : 0);

        // Signature so we skip a needless rebuild (and the dock re-layout that a
        // changed slot count forces).
        var sb = new System.Text.StringBuilder();
        sb.Append("polaris;");   // fixed leading Polaris tile
        foreach (var a in display)
            sb.Append(a.Aumid ?? a.Path).Append(';');
        if (overflow > 0)
            sb.Append('+').Append(overflow);
        string sig = sb.ToString();

        bool hadArea = _hasRunningArea;
        bool wantArea = slots > 0;   // always true now (Polaris tile)
        if (sig == _runSignature && hadArea == wantArea && _runSlotsCached == slots)
            return;
        _runSignature = sig;

        // The running strip's presence / size changes the slab height, so a
        // changed slot count requires a full re-layout. Only the tiles change
        // when the slab geometry is unaffected.
        bool layoutChanged = hadArea != wantArea || _runSlotsCached != slots;
        _hasRunningArea = wantArea;
        _runSlotsCached = slots;

        if (layoutChanged)
        {
            Rebuild();   // re-lays the slab + pinned column for the new height
        }

        // Clear previous running tiles and redraw.
        foreach (var t in _runTiles)
            PanelCanvas.Children.Remove(t);
        _runTiles.Clear();
        _runScale.Clear();
        _runTrans.Clear();
        _runCenterMain.Clear();
        _runWaveCur = Array.Empty<double>();
        ClearRunPopups();

        if (slots == 0)
            return;

        double tile = GIcon;
        for (int k = 0; k < slots; k++)
        {
            double mainC = _runAreaMain + k * RunStep + RunStep / 2.0;
            FrameworkElement el;
            if (k == 0)
            {
                // Polaris itself as the first running-strip tile.
                el = BuildPolarisRunTile(tile);
            }
            else
            {
                int appIdx = k - 1;   // shift past the leading Polaris tile
                bool isOverflow = overflow > 0 && k == slots - 1;
                el = isOverflow
                    ? BuildRunOverflowTile(overflow, tile)
                    : BuildRunTile(display[appIdx], tile);
            }
            // Wire this tile into the magnification wave: its inner visual (tagged
            // by the build methods) carries a scale + pop-out transform the wave
            // tick drives, so the running strip magnifies exactly like the pinned
            // column above it.
            var scale = new ScaleTransform(1, 1);
            var trans = new TranslateTransform(0, 0);
            if (el.Tag is FrameworkElement visual)
            {
                visual.RenderTransformOrigin = new Point(0.5, 0.5);
                visual.RenderTransform = new TransformGroup { Children = { scale, trans } };
            }
            _runScale.Add(scale);
            _runTrans.Add(trans);
            _runCenterMain.Add(mainC);
            Point c = ToLocal(mainC, _colCenterCross);
            Canvas.SetLeft(el, c.X - tile / 2.0);
            Canvas.SetTop(el, c.Y - tile / 2.0);
            Panel.SetZIndex(el, 60);
            PanelCanvas.Children.Add(el);
            _runTiles.Add(el);
        }
    }

    /// <summary>Builds the Polaris self-tile shown at the top of the running
    /// strip. Clicking it toggles the pinned docks (equivalent to Ctrl+4) via
    /// <see cref="ToggleDocks"/>.</summary>
    private FrameworkElement BuildPolarisRunTile(double size)
    {
        string exe = Environment.ProcessPath ?? "";
        if (!string.IsNullOrEmpty(exe) && !_runIconCache.TryGetValue(exe, out _))
        {
            try { _runIconCache[exe] = IconExtractor.GetIcon(exe); }
            catch (System.Exception ex) { Polaris.Services.Log.Debug("SideDock", "self run-tile icon extraction failed", ex); }
        }
        _runIconCache.TryGetValue(exe, out var bmp);

        var image = new Image { Source = bmp, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        var plate = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = Brushes.Transparent,
            Padding = new Thickness(8),
            Child = image,
        };

        var visual = new Grid { Background = Brushes.Transparent };
        visual.Children.Add(plate);
        // Polaris is always "running", so show the same breathing green dot.
        visual.Children.Add(BuildRunningDot(size));

        var root = new Grid
        {
            Width = size,
            Height = size,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };
        root.Children.Add(visual);
        // The magnification wave drives this tile's scale + pop-out via the inner
        // visual (see ApplyRunning); the root stays a fixed-size, stable hit area
        // so the pop-out can't create a hover enter/leave feedback loop.
        root.Tag = visual;

        root.MouseEnter += (_, _) =>
        {
            Panel.SetZIndex(root, 100);
            double mainC = MainOf(new Point(Canvas.GetLeft(root), Canvas.GetTop(root))) + size / 2.0;
            _runLabelShown = true;
            ShowHoverLabelCore("Polaris", mainC, size / 2.0 * HoverScale + WavePop(HoverScale));
        };
        root.MouseLeave += (_, _) =>
        {
            Panel.SetZIndex(root, 60);
            _runLabelShown = false;
            HideHoverLabel();
        };
        root.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            PlayRunTileBounce(root, () => ToggleDocks?.Invoke());
        };
        return root;
    }

    private FrameworkElement BuildRunTile(TaskbarApp app, double size)
    {
        // Elevated / protected apps (e.g. a game launched as administrator) expose
        // no readable executable path. Fall back to the window's own icon and
        // title — exactly how the taskbar represents such windows.
        bool pathless = string.IsNullOrEmpty(app.Path);
        // Packaged (UWP) apps such as the system Calculator either expose no
        // readable WindowsApps exe path, or one whose .exe carries no embedded
        // icon — so a path-based extract yields nothing. Resolve their icon from
        // the shell AppsFolder via the window's AUMID instead.
        string iconKey = !string.IsNullOrEmpty(app.Aumid)
            ? "aumid:" + app.Aumid
            : (pathless ? "win:" + app.Window : app.Path);
        if (!_runIconCache.TryGetValue(iconKey, out var bmp))
        {
            if (!string.IsNullOrEmpty(app.Aumid))
            {
                bmp = IconExtractor.GetIcon(ShellNamespace.NormalizeAppsFolderPath(app.Aumid));
                // Fall back to the window icon if the AppsFolder lookup misses.
                if (bmp == null && app.Window != IntPtr.Zero)
                    bmp = WindowPreviewService.GetWindowIconImage(app.Window);
            }
            else
            {
                bmp = pathless
                    ? WindowPreviewService.GetWindowIconImage(app.Window)
                    : IconExtractor.GetIcon(app.Path);
            }
            // Never cache a null result: a transient cold-boot miss (the shell
            // icon services aren't ready yet) would otherwise be frozen blank for
            // the whole session. Cache only a real bitmap so the next tick retries.
            if (bmp != null)
                _runIconCache[iconKey] = bmp;
        }

        var image = new Image { Source = bmp, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        // No tray — consistent with the pinned column above. The icon floats
        // free and its "running" state reads from the green breathing dot.
        // Padding matches RadialIcon's fixed 8-DIP plate inset so the running
        // glyph is exactly the same size as the pinned icons above.
        var plate = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = Brushes.Transparent,
            Padding = new Thickness(8),
            Child = image,
        };

        var visual = new Grid { Background = Brushes.Transparent };
        visual.Children.Add(plate);
        // Every tile here is by definition running → always show the breathing
        // green dot, exactly like the pinned icons.
        visual.Children.Add(BuildRunningDot(size));

        var root = new Grid
        {
            Width = size,
            Height = size,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };
        root.Children.Add(visual);
        // The wave drives scale + pop-out via the inner visual (see ApplyRunning);
        // the root is a fixed-size, stable hit area so the pop-out never triggers a
        // hover enter/leave feedback loop.
        root.Tag = visual;

        IntPtr win = app.Window;
        string label = pathless
            ? (app.Title ?? "")
            : System.IO.Path.GetFileNameWithoutExtension(app.Path);

        // Hover-thumbnail preview, opening toward the screen interior like the
        // pinned icons. Shows even for a single open window. Anchored to the inner
        // `visual` (which carries the wave's centred scale + pop-out transform,
        // exactly like a pinned RadialIcon's own scale) rather than the fixed
        // `root`, so the popup's vertical position lines up with the pinned-icon
        // previews instead of sitting lower.
        string? taAumid = app.Aumid;
        string taPath = app.Path;
        var preview = new WindowPreviewPopup(
            visual,
            () => taAumid != null
                ? WindowPreviewService.GetWindowsByAumid(taAumid)
                : pathless
                    ? WindowPreviewService.GetWindowsByHandle(win)
                    : WindowPreviewService.GetWindowsForEntry(taPath, null),
            minWindows: 1,
            onActivated: () => SetEdgeShown(false))
        {
            Placement = PreviewPlacementForSide(),
        };
        _runPopups.Add(preview);

        root.MouseEnter += (_, _) =>
        {
            Panel.SetZIndex(root, 100);
            // Same hover label / font as the pinned icons; the wave pops the tile
            // toward the interior, so the label clears the enlarged + popped icon.
            double mainC = MainOf(new Point(Canvas.GetLeft(root), Canvas.GetTop(root))) + size / 2.0;
            _runLabelShown = true;
            ShowHoverLabelCore(label, mainC, size / 2.0 * HoverScale + WavePop(HoverScale));
            preview.OnPointerEnter();
        };
        root.MouseLeave += (_, _) =>
        {
            Panel.SetZIndex(root, 60);
            _runLabelShown = false;
            HideHoverLabel();
            preview.OnPointerLeave();
        };
        root.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            // Hop first (dock held visible), then bring the window forward and
            // dismiss — activating first would cover the dock and hide the bounce.
            PlayRunTileBounce(root, () =>
            {
                WindowPreviewService.Activate(win);
                SetEdgeShown(false);
            });
        };
        root.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowRunTileMenu(root, app);
        };
        return root;
    }

    /// <summary>A small breathing green dot hugging the left edge of a tile —
    /// the shared "running" indicator used by both the pinned icons and the
    /// running strip. Returns a zero-impact Canvas with its pulse already
    /// animating.</summary>
    private FrameworkElement BuildRunningDot(double iconSize)
    {
        // Keep these EXACTLY in step with RadialIcon's running dot (the pinned
        // icons) — factor 0.07 / min 2.6 / glow 2.3 — so the breathing indicator
        // is the same size in the pinned column and the running strip.
        double dot = Math.Max(2.6, iconSize * 0.07);
        double glow = dot * 2.3;
        // Hug the screen-edge side of the tile so the indicator always sits on
        // the outer edge regardless of which screen edge the dock is on.
        double near = dot * 0.05;
        double far = iconSize - dot * 0.05;
        double mid = iconSize / 2.0;
        (double cx, double cy) = _side switch
        {
            DockSide.Left => (near, mid),
            DockSide.Right => (far, mid),
            DockSide.Top => (mid, near),
            DockSide.Bottom => (mid, far),
            _ => (near, mid),
        };

        var glowEllipse = new System.Windows.Shapes.Ellipse
        {
            Width = glow,
            Height = glow,
            Fill = new SolidColorBrush(Color.FromRgb(0x5C, 0xFF, 0x7A)),
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 6 },
            IsHitTestVisible = false,
            Opacity = 0,
        };
        Canvas.SetLeft(glowEllipse, cx - glow / 2.0);
        Canvas.SetTop(glowEllipse, cy - glow / 2.0);

        var core = new System.Windows.Shapes.Ellipse
        {
            Width = dot,
            Height = dot,
            IsHitTestVisible = false,
            Opacity = 0,
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.4, 0.35),
                Center = new Point(0.4, 0.35),
                RadiusX = 0.65,
                RadiusY = 0.65,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xFF, 0xB6, 0xFF, 0xC4), 0.0),
                    new GradientStop(Color.FromArgb(0xFF, 0x4C, 0xE0, 0x6B), 0.55),
                    new GradientStop(Color.FromArgb(0xFF, 0x22, 0xB2, 0x4C), 1.0),
                },
            },
        };
        Canvas.SetLeft(core, cx - dot / 2.0);
        Canvas.SetTop(core, cy - dot / 2.0);

        var canvas = new Canvas { IsHitTestVisible = false };
        canvas.Children.Add(glowEllipse);
        canvas.Children.Add(core);

        int loopFps = App.AmbientFrameRate;
        var pulse = new DoubleAnimation(0.55, 1.0, new Duration(TimeSpan.FromSeconds(2.0)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Timeline.SetDesiredFrameRate(pulse, loopFps);
        core.BeginAnimation(OpacityProperty, pulse);
        var glowPulse = new DoubleAnimation(0.25, 0.65, new Duration(TimeSpan.FromSeconds(2.0)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Timeline.SetDesiredFrameRate(glowPulse, loopFps);
        glowEllipse.BeginAnimation(OpacityProperty, glowPulse);

        return canvas;
    }

    private FrameworkElement BuildRunOverflowTile(int extra, double size)
    {
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
            CornerRadius = new CornerRadius(12),
            Background = Brushes.Transparent,
            Child = label,
        };
        var visual = new Grid { Background = Brushes.Transparent, Children = { plate } };
        return new Grid
        {
            Width = size,
            Height = size,
            Background = Brushes.Transparent,
            ToolTip = $"另有 {extra} 个正在运行",
            Tag = visual,
            Children = { visual },
        };
    }

}
