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

/// <summary>
/// Transparent, top-most radial launcher overlay. Shows app icons on concentric
/// rings with a center settings button. Supports hover animation, click-to-launch,
/// drag-to-reorder and drag-out-to-delete.
/// </summary>
public partial class RadialWindow : Window
{
    private const double DragThreshold = 6.0;
    private const double BaseInnerRadius = 140.0;
    private const double BaseRingStep = 88.0;
    private const int Ring0Cap = 12;
    private const int Ring1Cap = 24;

    // Reference screen height (DIPs) the layout was tuned for. On taller
    // displays everything is scaled up proportionally so the panel does not
    // look tiny on large/high-resolution monitors. Never scaled below 1.0.
    private const double ReferenceScreenHeight = 1080.0;

    // Global resolution scale factor, recomputed from the primary screen size
    // each time the overlay is sized. 1.0 on a 1080p (DIP) display.
    private double _uiScale = 1.0;

    // Extra per-theme scale applied on top of _uiScale. The Saturn theme is
    // drawn larger overall than the grid themes; 1.0 for everything else.
    private const double SaturnEnlarge = 1.25;
    private double _themeScale = 1.0;

    // Ring radii scaled by the current resolution + theme factors.
    private double InnerRadius => BaseInnerRadius * _uiScale * _themeScale;
    private double RingStep => BaseRingStep * _uiScale * _themeScale;

    // User-chosen icon diameter scaled by the resolution (and theme) factors, so
    // icons (and the grid/ring geometry derived from them) grow on larger
    // displays and with the Saturn enlargement.
    private double EffectiveIconSize => _config.Settings.IconSize * _uiScale * _themeScale;

    // Saturn's centre planet uses a fixed base diameter (independent of the
    // user's icon-size setting) so adjusting icon size never resizes the planet;
    // it still scales with screen resolution and the Saturn enlargement.
    private const double PlanetIconBase = 56.0;
    private double PlanetDiameter => PlanetIconBase * _uiScale * _themeScale * 2.5;

    // Outer-ring icons are drawn slightly larger than inner-ring icons.
    private const double OuterIconScale = 1.18;

    // Liquid-glass (grid) icons are drawn larger than the base icon size so they
    // fill the roomy grid cells more comfortably.
    private const double GlassIconScale = 1.32;

    // Saturn's self-rotation period (seconds per turn) used for the centre
    // planet's idle spin and as the reference for the ring revolution speeds.
    private const double PlanetSpinSeconds = 60.0;

    // Ring revolution periods are set in real Saturn proportion to the planet's
    // self-rotation. With Saturn rotating in ~10.656 h, Keplerian orbital
    // periods give: B ring (inner icons, 1.739 Rs) ~9.62 h and F ring (outer
    // icons, 2.324 Rs) ~14.86 h, i.e. 0.902x and 1.394x the rotation period.
    private const double InnerOrbitRatio = 0.9023;
    private const double OuterOrbitRatio = 1.3941;

    // Vertical squash applied to the ring plane so it reads as a tilted disc
    // seen in perspective (1.0 = top-down circle, smaller = more edge-on). The
    // planet body stays a circle (a sphere looks round from any angle); only
    // the flat ring plane and the icon orbits are foreshortened into ellipses.
    private const double RingTiltY = 0.97;

    // Orbit-angle transforms. They no longer rotate the (now elliptical) ring
    // layers — spinning a concentric ellipse would make it tumble — instead
    // they drive the two "shimmer" highlights that sweep along the elliptical
    // ring orbits, which is what now conveys the revolution.
    private readonly RotateTransform _innerOrbit = new RotateTransform(0);
    private readonly RotateTransform _outerOrbit = new RotateTransform(0);

    // The two ring layers the bands are drawn into (kept static now that the
    // bands are foreshortened ellipses). null = the static PanelCanvas.
    private Canvas? _innerOrbitLayer;
    private Canvas? _outerOrbitLayer;
    private Canvas? _ringLayer;

    // Current per-ring vertical radius factor used by StackCentered so a band
    // can be stacked as an ellipse (height = width * factor). 1.0 = circle.
    private double _stackTiltY = 1.0;

    private readonly AppConfig _config;
    private readonly Action _persist;
    private readonly Dictionary<string, BitmapSource?> _iconCache = new();

    // Active visual theme (layout + background). Resolved from config on Rebuild.
    private PanelTheme _theme = ThemeRegistry.Get("saturn");

    // Periodically refreshes each icon's running indicator while the panel is shown.
    private readonly System.Windows.Threading.DispatcherTimer _runningTimer;

    // Always-on background timer that pre-captures each running window's thumbnail
    // so a "last view before minimize" frame is available for hover previews.
    private readonly System.Windows.Threading.DispatcherTimer _previewWarmTimer;

    // Set while showing the window so the SizeChanged fired by Show() does not
    // trigger a premature (wrong-centre) Rebuild that would flash the ring.
    private bool _suppressRebuild;

    // Intended visible state. Guards the deferred build callback so it does not
    // re-apply a fade after the panel was hidden again (fast key press/release).
    private bool _shown;

    // Whether the window has been realised (shown once) at startup.
    private bool _realized;

    private Point _center;
    private double _outerRadius;
    private readonly List<Point> _slotPositions = new();

    // Drag state
    private RadialIcon? _pressedIcon;
    private Point _pressPoint;
    private bool _dragging;

    // Icons in current _config.Apps order, parallel to the entries. Used to
    // animate the non-dragged icons aside while reordering.
    private readonly List<RadialIcon> _iconElements = new();

    // Slot the dragged icon is currently hovering toward, expressed as a target
    // ring (0 = inner, 1 = outer, -1 = none) and angular position within it.
    private int _dragTargetRing = -1;
    private int _dragTargetPos = -1;

    // Icon the pointer is currently hovering (for the "spread apart" effect).
    private RadialIcon? _hoverIcon;

    /// <summary>
    /// When true the panel stays open (opened from the tray) so the user can
    /// drag desktop shortcuts onto it. Key-release will not hide it.
    /// </summary>
    public bool IsPinned { get; private set; }

    public event Action? RequestOpenSettings;

    public RadialWindow(AppConfig config, Action persist)
    {
        _config = config;
        _persist = persist;
        InitializeComponent();

        SizeToActiveContent();
        Loaded += (_, _) => Rebuild();
        SizeChanged += (_, _) => { if (!_suppressRebuild) Rebuild(); };

        _runningTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5),
        };
        _runningTimer.Tick += (_, _) => RefreshRunningStates();

        // Warm the thumbnail cache in the background (even while the panel is
        // hidden) so we always hold a recent frame to show if a window gets
        // minimized before the user hovers its icon.
        _previewWarmTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5),
        };
        _previewWarmTimer.Tick += (_, _) => WarmPreviewCache();
        _previewWarmTimer.Start();
    }

    private void WarmPreviewCache()
    {
        // Snapshot the app paths on the UI thread, then capture off-thread.
        var paths = new List<string>(_config.Apps.Count);
        foreach (var a in _config.Apps)
            if (!string.IsNullOrWhiteSpace(a.Path))
                paths.Add(a.Path);
        if (paths.Count == 0)
            return;

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                WindowPreviewService.WarmCache(paths, RadialIcon.PreviewThumbWidth);
            }
            catch
            {
                // Best effort — a transient capture failure must never crash.
            }
        });
    }

    private void SizeToActiveContent()
    {
        // Instead of spanning the whole screen, size the overlay to a generously
        // padded box around the active theme's content and centre it. A smaller
        // AllowsTransparency (layered) window means far less per-frame CPU-side
        // composition, which lifts the real animation frame rate. The large
        // margin keeps hover zoom, drop shadows, the settings gear and the
        // drag-to-delete ring comfortably inside the window.
        double sw = SystemParameters.PrimaryScreenWidth;
        double sh = SystemParameters.PrimaryScreenHeight;
        // Scale the whole panel up on taller displays (never below 1.0) so it
        // does not look tiny on large monitors.
        _uiScale = Math.Clamp(sh / ReferenceScreenHeight, 1.0, 2.0);

        bool saturn = ThemeRegistry.Get(_config.Settings.Theme).IsSaturn;
        // The Saturn theme is rendered larger overall than the grid themes.
        _themeScale = saturn ? SaturnEnlarge : 1.0;

        double icon = EffectiveIconSize;

        double halfW, halfH;
        if (saturn)
        {
            // Ring disc radius + an outer icon, plus room for the 1.7x hover zoom.
            double discR = InnerRadius + RingStep + icon * OuterIconScale;
            double reach = discR + icon * OuterIconScale;
            halfW = reach;
            halfH = reach;
        }
        else
        {
            // Liquid-glass grid panel extents (mirrors DrawGlassPanel); size to
            // the largest possible 5×5 grid so the window fits when expanded.
            double cellW = icon * 2.15;
            double cellH = icon * 2.35;
            double gridW = (LiquidGlassTheme.Columns - 1) * cellW;
            double gridH = (LiquidGlassTheme.MaxRows - 1) * cellH;
            double panelHalfW = (gridW + icon + icon * 1.15 * 2) / 2.0;
            double panelHalfH = (gridH + icon + icon * 1.15 * 2) / 2.0;
            // The settings gear sits above the grid; keep it inside.
            double gearUp = 2 * (icon * 2.1) + icon * 0.7 + icon * 0.6;
            halfW = panelHalfW + icon * 1.7;
            halfH = Math.Max(panelHalfH, gearUp) + icon * 1.7;
        }

        // Generous fixed margin on top of the computed reach (ample headroom),
        // then clamp to the screen so we never exceed it.
        double margin = 180.0 * _uiScale;
        double w = Math.Min((halfW + margin) * 2.0, sw);
        double h = Math.Min((halfH + margin) * 2.0, sh);

        Width = w;
        Height = h;
        Left = (sw - w) / 2.0;
        Top = (sh - h) / 2.0;
        UpdateCenter();
    }

    /// <summary>
    /// Centre of the overlay in canvas coordinates. The borderless window's
    /// client area equals its logical Width/Height (both in DIPs), and the
    /// PanelCanvas fills it, so the window centre is the layout centre. Using
    /// Width/Height (rather than ActualWidth, which lags a frame after a resize)
    /// keeps the layout correctly centred the instant the window is resized.
    /// </summary>
    private void UpdateCenter()
    {
        double w = (!double.IsNaN(Width) && Width > 0) ? Width
                 : RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth
                 : ActualWidth;
        double h = (!double.IsNaN(Height) && Height > 0) ? Height
                 : RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight
                 : ActualHeight;
        _center = new Point(w / 2.0, h / 2.0);
    }

    public void ShowPanel()
    {
        IsPinned = false;
        ShowFaded(pinned: false);
    }

    /// <summary>
    /// Shows the panel in pinned mode (stays open until explicitly closed),
    /// so the user can drag desktop shortcuts onto the ring.
    /// </summary>
    public void ShowPinned()
    {
        ShowFaded(pinned: true);
    }

    /// <summary>True while the overlay is faded in and interactive.</summary>
    public bool IsShown => _shown;

    /// <summary>
    /// Realises the transparent overlay once at startup and keeps it shown but
    /// fully transparent. Re-showing an <c>AllowsTransparency</c> window with
    /// Hide()/Show() recreates its layered surface and flashes a frame every
    /// time; staying shown and only fading the opacity avoids that flicker.
    /// While Opacity is 0 the layered window is fully transparent, so mouse
    /// clicks pass straight through to the desktop beneath.
    /// </summary>
    public void Realize()
    {
        if (_realized) return;
        _realized = true;
        _suppressRebuild = true;
        SizeToActiveContent();
        Opacity = 0;
        Show();                         // one-time; the window then stays shown
        _suppressRebuild = false;
        Rebuild();
        _runningTimer.Stop();           // nothing to poll while hidden
    }

    /// <summary>
    /// Fades the already-realised overlay in. No Hide()/Show() is involved, so
    /// there is no layered-surface flash.
    /// </summary>
    private void ShowFaded(bool pinned)
    {
        Realize();                      // safety: ensure the window exists
        _shown = true;
        if (pinned)
            IsPinned = true;

        SizeToActiveContent();
        _suppressRebuild = false;
        Rebuild();                      // pick up any config changes
        AnimateRingsExpand();           // grow the rings out from the centre
        Topmost = true;
        Activate();
        _runningTimer.Start();

        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(210));
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Animates the two ring layers growing out from the planet, the
    /// inner band leading and the outer band following, for a "summon" feel.</summary>
    private void AnimateRingsExpand()
    {
        ExpandLayer(_innerOrbitLayer, 0.0);
        ExpandLayer(_outerOrbitLayer, 0.11);
    }

    private void ExpandLayer(Canvas? layer, double delaySeconds)
    {
        if (layer == null)
            return;

        layer.RenderTransformOrigin = new Point(0.5, 0.5);
        var sc = new ScaleTransform(0.55, 0.55);
        layer.RenderTransform = sc;

        var begin = TimeSpan.FromSeconds(delaySeconds);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var grow = new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(380))
        {
            BeginTime = begin,
            EasingFunction = ease,
        };
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, grow.Clone());

        var fadeIn = new DoubleAnimation(0, 0.78, TimeSpan.FromMilliseconds(380))
        {
            BeginTime = begin,
            EasingFunction = ease,
        };
        layer.BeginAnimation(OpacityProperty, fadeIn);
    }

    public void HidePanel()
    {
        _shown = false;
        IsPinned = false;
        CancelDrag();
        _runningTimer.Stop();

        // Dismiss any open window-preview popups so they don't linger on screen.
        foreach (var ic in _iconElements)
            ic.ClosePreview();

        // Fade out instead of hiding; at Opacity 0 the window is click-through.
        // Capture the current (animated) opacity BEFORE replacing the animation
        // so we start the fade from what's on screen. Do NOT clear the animation
        // first: clearing snaps Opacity back to its base value (0, set in
        // ShowFaded), which would make the fade run 0->0 and the panel vanish.
        double from = Opacity;
        var fade = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(240));
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Hides the panel only if it is not pinned (used on key-release).</summary>
    public void HideIfNotPinned()
    {
        if (!IsPinned)
            HidePanel();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Keep the always-shown overlay out of Alt+Tab and the taskbar.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            HidePanel();
            e.Handled = true;
        }
    }

    private Brush LabelBrush => new SolidColorBrush(ParseColor(_config.Settings.FontColor, Colors.White));
    private Color AccentColor => ParseColor(_config.Settings.AccentColor, Color.FromRgb(0x3D, 0x7E, 0xFF));

    private void Rebuild()
    {
        UpdateCenter();
        PruneIconCache();

        // Resolve the active theme from config each rebuild so switching the
        // theme in settings takes effect on the next render.
        _theme = ThemeRegistry.Get(_config.Settings.Theme);
        RootGrid.Background = _theme.WindowBackground;

        PanelCanvas.Children.Clear();
        _iconElements.Clear();
        _hoverIcon = null;

        // --- Layout (theme-driven) -------------------------------------------
        if (_theme.IsSaturn)
        {
            ComputeLayout(_config.Apps.Count);
        }
        else
        {
            _slotPositions.Clear();
            // Lay the grid out using the resolution-scaled icon size so the
            // panel grows on large displays. The theme only reads IconSize.
            var scaled = new AppSettings { IconSize = EffectiveIconSize };
            _slotPositions.AddRange(_theme.ComputeSlots(
                _config.Apps.Count, _center, scaled, out _outerRadius));
        }

        // --- Background / animation (Saturn only) ----------------------------
        if (_theme.IsSaturn)
        {
            // Two rotating layers that carry the ring bands so the inner and
            // outer ring groups revolve about the planet at real Saturn
            // proportions. The layers span the whole panel so their rotation
            // centre (0.5,0.5) coincides with the panel centre. Icons are NOT
            // placed on these layers.
            double pw = _center.X * 2.0;
            double ph = _center.Y * 2.0;
            _innerOrbitLayer = new Canvas
            {
                Width = pw,
                Height = ph,
                IsHitTestVisible = false,
                Opacity = 0.78,                   // match the planet's translucency
            };
            _outerOrbitLayer = new Canvas
            {
                Width = pw,
                Height = ph,
                IsHitTestVisible = false,
                Opacity = 0.78,                   // match the planet's translucency
            };
            PanelCanvas.Children.Add(_innerOrbitLayer);
            PanelCanvas.Children.Add(_outerOrbitLayer);

            DrawBackingDisc();
        }
        else
        {
            _innerOrbitLayer = null;
            _outerOrbitLayer = null;
            if (_theme.ShowGlassPanel)
                DrawGlassPanel();
        }

        int r0 = _theme.IsSaturn ? EffectiveRing0Count(_config.Apps.Count) : int.MaxValue;
        for (int i = 0; i < _config.Apps.Count && i < _slotPositions.Count; i++)
        {
            var entry = _config.Apps[i];
            double size = (_theme.IsSaturn && i >= r0)
                ? EffectiveIconSize * OuterIconScale
                : _theme.ShowGlassPanel
                    ? EffectiveIconSize * GlassIconScale
                    : EffectiveIconSize;
            var icon = CreateIcon(entry, size);
            PlaceCentered(icon, _slotPositions[i]);
            PanelCanvas.Children.Add(icon);
            _iconElements.Add(icon);
        }

        if (_theme.IsSaturn)
        {
            DrawCenterButton();
            StartOrbits();
        }
        else if (!_theme.ShowGlassPanel)
        {
            DrawSimpleCenterButton();
        }

        RefreshRunningStates();
    }

    /// <summary>Refreshes the panel from the current config (e.g. after the
    /// theme or icon size changes in settings).</summary>
    public void RefreshFromConfig()
    {
        if (IsLoaded)
        {
            // Recompute the window size / scale factors so a live theme or
            // icon-size change is reflected immediately, then redraw.
            SizeToActiveContent();
            Rebuild();
        }
    }

    private void RefreshRunningStates()
    {
        // Enumerate processes on a background thread so the (relatively slow)
        // snapshot never blocks the UI thread and stutters the light animation.
        var icons = new List<RadialIcon>(_iconElements);
        System.Threading.Tasks.Task.Run(() =>
        {
            var running = RunningAppTracker.SnapshotRunning();
            Dispatcher.BeginInvoke(() =>
            {
                foreach (var icon in icons)
                {
                    try
                    {
                        icon.IsRunning = RunningAppTracker.IsRunningInSnapshot(icon.Entry.Path, running);
                    }
                    catch
                    {
                        icon.IsRunning = false;
                    }
                }
            });
        });
    }

    /// <summary>Starts (or restarts) the inner/outer ring revolution at periods
    /// proportional to the planet's self-rotation, matching real Saturn ratios.</summary>
    private void StartOrbits()
    {
        StartOrbit(_innerOrbit, PlanetSpinSeconds * InnerOrbitRatio);
        StartOrbit(_outerOrbit, PlanetSpinSeconds * OuterOrbitRatio);
    }

    private static void StartOrbit(RotateTransform rt, double secondsPerTurn)
    {
        rt.BeginAnimation(RotateTransform.AngleProperty, null);
        rt.Angle = 0;
        var anim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(secondsPerTurn))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        rt.BeginAnimation(RotateTransform.AngleProperty, anim);
    }

    private RadialIcon CreateIcon(AppEntry entry, double iconSize)
    {
        if (!_iconCache.TryGetValue(entry.EffectiveIconSource, out var bmp))
        {
            bmp = IconExtractor.GetIcon(entry.EffectiveIconSource);
            _iconCache[entry.EffectiveIconSource] = bmp;
        }
        var icon = new RadialIcon(entry, bmp, iconSize, AccentColor, LabelBrush);
        icon.PreviewMouseLeftButtonDown += Icon_PreviewMouseLeftButtonDown;
        icon.HoverStarted += OnIconHoverStarted;
        icon.HoverEnded += OnIconHoverEnded;
        icon.WindowActivated += HidePanel;
        return icon;
    }

    /// <summary>Drops cached icon bitmaps no longer referenced by any configured
    /// app, so the cache cannot grow unbounded as the user adds and removes
    /// entries over a long session.</summary>
    private void PruneIconCache()
    {
        if (_iconCache.Count == 0)
            return;

        var live = new HashSet<string>();
        foreach (var entry in _config.Apps)
            live.Add(entry.EffectiveIconSource);

        List<string>? stale = null;
        foreach (var key in _iconCache.Keys)
        {
            if (!live.Contains(key))
                (stale ??= new List<string>()).Add(key);
        }
        if (stale != null)
            foreach (var key in stale)
                _iconCache.Remove(key);
    }

    private void PlaceCentered(FrameworkElement el, Point center)
    {
        double s = el is RadialIcon ri ? ri.IconSize : _config.Settings.IconSize;
        Canvas.SetLeft(el, center.X - s / 2);
        Canvas.SetTop(el, center.Y - s / 2);
    }

    /// <summary>Computes ring slot positions for <paramref name="count"/> icons,
    /// split across up to two rings per the inner-ring count.</summary>
    private void ComputeLayout(int count)
    {
        _slotPositions.Clear();
        _outerRadius = InnerRadius;

        if (count <= 0)
            return;

        int r0 = EffectiveRing0Count(count);
        _slotPositions.AddRange(SlotPositionsFor(count, r0));
        _outerRadius = (count - r0 > 0) ? InnerRadius + RingStep : InnerRadius;
    }

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
            _config.Apps.Add(entry);
            added = true;
        }

        if (added)
        {
            _persist();
            Rebuild();
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
        PanelCanvas.CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressedIcon == null)
            return;

        Point p = e.GetPosition(PanelCanvas);
        if (!_dragging)
        {
            if ((p - _pressPoint).Length < DragThreshold)
                return;
            _dragging = true;
            Panel.SetZIndex(_pressedIcon, 1000);
            // Stop any residual reflow animation on the dragged icon so it tracks
            // the cursor exactly.
            _pressedIcon.BeginAnimation(Canvas.LeftProperty, null);
            _pressedIcon.BeginAnimation(Canvas.TopProperty, null);
        }

        PlaceCentered(_pressedIcon, p);

        double dist = (p - _center).Length;
        _pressedIcon.Opacity = dist > DeleteRadius ? 0.4 : 1.0;

        // Push other icons aside to reveal the slot the dragged icon is over.
        // Skip while the icon is dragged out past the outer ring (delete zone).
        // Only the Saturn ring layout supports live reorder; the grid test theme
        // keeps drag-to-launch and drag-out-to-delete but no live reflow.
        if (_theme.IsSaturn && dist <= DeleteRadius)
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
        else if (_theme.SupportsGridReorder && dist <= DeleteRadius)
        {
            // Free-grid reorder: find the slot the cursor is over and make room
            // by shifting the other icons into the insertion arrangement.
            int src = _iconElements.IndexOf(_pressedIcon);
            int tgt = ComputeGridTarget(p, src);
            if (tgt != _dragTargetPos)
            {
                _dragTargetPos = tgt;
                ReflowGrid(src, tgt);
            }
        }
        else if (_dragTargetRing != -1 || _dragTargetPos != -1)
        {
            // Dragged into the delete zone — snap the others back to their slots.
            _dragTargetRing = -1;
            _dragTargetPos = -1;
            RestoreSlots();
        }
    }

    // ---- Free grid reorder (liquid-glass theme) --------------------------

    /// <summary>
    /// Returns the insertion slot index (0..n-1) the dragged icon
    /// <paramref name="src"/> is currently over, chosen as the nearest grid slot
    /// to the cursor.
    /// </summary>
    private int ComputeGridTarget(Point p, int src)
    {
        int n = _slotPositions.Count;
        if (n == 0)
            return 0;

        int best = 0;
        double bestD = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            double d = (p - _slotPositions[i]).LengthSquared;
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return Math.Clamp(best, 0, n - 1);
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
    /// grid arrangement, producing the neighbour "push aside" effect.</summary>
    private void ReflowGrid(int src, int tgt)
    {
        int[] slotOfEntry = GridArrangement(src, tgt);
        for (int i = 0; i < _iconElements.Count; i++)
        {
            if (_iconElements[i] == _pressedIcon)
                continue;
            int slot = slotOfEntry[i];
            if (slot >= 0 && slot < _slotPositions.Count)
                AnimateTo(_iconElements[i], _slotPositions[slot]);
        }
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

        int tgt = targetPos >= 0 ? targetPos : ComputeGridTarget(dropPoint, src);
        int[] slotOfEntry = GridArrangement(src, tgt);
        int n = _config.Apps.Count;

        var ordered = new AppEntry[n];
        for (int i = 0; i < n; i++)
            ordered[slotOfEntry[i]] = _config.Apps[i];

        _config.Apps.Clear();
        foreach (var a in ordered)
            _config.Apps.Add(a);

        _persist();
        Rebuild();
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
    /// Number of icons on the inner ring for <paramref name="n"/> total icons,
    /// derived from the persisted <c>Ring0Count</c> (0 = auto) and clamped to the
    /// per-ring caps (inner ≤ 12, outer ≤ 24).
    /// </summary>
    private int EffectiveRing0Count(int n)
    {
        if (n <= 0)
            return 0;

        int r0 = _config.Settings.Ring0Count;
        if (r0 <= 0 || r0 > n)
            r0 = Math.Min(Ring0Cap, n); // auto: fill the inner ring first

        r0 = Math.Clamp(r0, 1, Math.Min(Ring0Cap, n));

        // If the outer ring would overflow its cap, push more onto the inner ring.
        if (n - r0 > Ring1Cap)
            r0 = Math.Min(Ring0Cap, n);
        return r0;
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
        SpreadNeighbours(idx);
    }

    private void OnIconHoverEnded(RadialIcon ic)
    {
        if (_pressedIcon != null)
            return;

        int idx = _iconElements.IndexOf(ic);
        if (idx >= 0)
            Panel.SetZIndex(ic, 0);

        if (_hoverIcon == ic)
            _hoverIcon = null;

        RestoreSlots();
    }

    /// <summary>
    /// Pushes icons near <paramref name="hovered"/> away from it, with the shift
    /// falling off by distance, so closer neighbours move more.
    /// </summary>
    private void SpreadNeighbours(int hovered)
    {
        double iconSize = _config.Settings.IconSize;
        double push = iconSize * 0.75;
        double influence = iconSize * 2.7;
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

        double dist = (p - _center).Length;
        if (dist > DeleteRadius)
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
    }

    private void CancelDrag()
    {
        if (_pressedIcon != null)
        {
            PanelCanvas.ReleaseMouseCapture();
            _pressedIcon = null;
            _dragging = false;
            _dragTargetRing = -1;
            _dragTargetPos = -1;
        }
    }

    private void Launch(AppEntry entry)
    {
        HidePanel();

        // Shell-namespace objects (This PC, Recycle Bin…) open through explorer.
        if (entry.IsShellItem)
        {
            try
            {
                ShellNamespace.Launch(entry.Path);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开 {entry.Name}:\n{ex.Message}", "Polaris",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        // If the program is already running, bring its existing window to the
        // foreground instead of starting a second instance.
        try
        {
            if (RunningAppTracker.ActivateExisting(entry.Path))
                return;
        }
        catch
        {
            // Fall through to a normal launch if activation fails.
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = entry.Path,
                Arguments = entry.Arguments,
                UseShellExecute = true,
            };
            if (!string.IsNullOrWhiteSpace(entry.WorkingDirectory))
                psi.WorkingDirectory = entry.WorkingDirectory;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法启动 {entry.Name}:\n{ex.Message}", "Polaris",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
                return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
        }
        return fallback;
    }
}
