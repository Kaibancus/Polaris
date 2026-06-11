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

/// <summary>
/// A second, vertical "liquid glass" dock pinned to the left edge of the screen.
/// Shares the icon / glass-chrome look of the main radial dock but lays its
/// pinned apps out as a single scrolling column, with a running-but-unpinned
/// apps strip at the bottom. Summoned by moving the mouse to the left-centre
/// edge, or together with the main dock via the global hotkey.
/// </summary>
public partial class LeftDockWindow : Window
{
    private const double GlassIconScale = 1.32;   // match the main dock's grid icon size
    private const double HoverScale = 2.1;
    private const double DragThreshold = 6.0;
    private const int RunningMaxComplete = 4;     // at most 4 full running-app icons
    private const double LeftDockScale = 0.5;     // left dock is half the main dock's size

    private readonly AppConfig _config;
    private readonly Action _persist;
    private readonly Dictionary<string, BitmapSource?> _iconCache = new();
    private readonly Dictionary<string, BitmapSource?> _runIconCache = new();

    private double _uiScale = 1.0;

    // Scroll container for the pinned column (mirrors the main dock: one
    // translate transform on a clipped layer instead of per-icon repositioning).
    private double _pinnedScroll;
    private Canvas? _scrollLayer;
    private TranslateTransform? _scrollTransform;

    // Visibility is driven by two independent sources; the dock is shown while
    // either is active and hidden only when both clear.
    private bool _shownByMain;
    private bool _shownByEdge;
    private bool _shownByDrag;
    private bool _shownByPinned;
    private bool _shown;
    private bool _realized;

    private readonly List<RadialIcon> _pinnedIcons = new();
    private readonly List<Point> _pinnedSlots = new();   // window-local, un-scrolled centres

    // Drag state for reordering / removing a pinned icon.
    private RadialIcon? _pressedIcon;
    private Point _pressPoint;
    private bool _dragging;
    private int _dragInsertIdx = -1;   // gap position while dragging; -1 = not dragging

    // Continuous (macOS-dock) magnification state. The wave is driven by the
    // actual cursor Y position and smoothed per render frame for fluid motion.
    private double _waveCursorY = double.NaN;        // cursor Y in PanelCanvas coords; NaN = inactive
    private int _labelIdx = -1;                       // icon the hover label is showing for (-1 none)
    private double[] _waveCur = Array.Empty<double>(); // smoothed per-icon scale
    private bool _waveTicking;                        // CompositionTarget.Rendering hooked?
    private TimeSpan _waveLastTick;

    private Border? _hoverLabel;
    private TextBlock? _hoverLabelText;
    private bool _runLabelShown;

    private readonly System.Windows.Threading.DispatcherTimer _runningTimer;
    private string? _runSignature;

    // Cached layout geometry (window-local), recomputed each Rebuild.
    private double _slabLeft, _slabTop, _slabW, _slabH, _colCenterX;
    private int _pinnedVisible;
    private double _pinnedAreaTop;   // y of the first pinned slot's cell top
    private double _runAreaTop;      // y where the running strip begins
    private double _seamY;           // y of the divider between pinned + running
    private bool _hasRunningArea;
    private Rect _pinnedViewport;    // window-local clip rect for the scroll layer

    private double EffectiveIconSize => _config.Settings.IconSize * _uiScale * LeftDockScale;
    private double GIcon => EffectiveIconSize * GlassIconScale;
    private double DefaultCellH => EffectiveIconSize * 1.58;
    private double CellH => _cellH > 0 ? _cellH : DefaultCellH;
    // Running-strip tiles use the SAME vertical step as the pinned cells so the
    // icon spacing is identical above and below the divider.
    private double RunStep => CellH;
    private double _cellH;

    private Brush LabelBrush => new SolidColorBrush(ParseColor(_config.Settings.FontColor, Colors.White));
    private Color AccentColor => ParseColor(_config.Settings.AccentColor, Color.FromRgb(0x3D, 0x7E, 0xFF));

    public bool DockVisible => _shown;

    public LeftDockWindow(AppConfig config, Action persist)
    {
        _config = config;
        _persist = persist;
        InitializeComponent();

        _runningTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5),
        };
        _runningTimer.Tick += (_, _) => RefreshRunning();
    }

    // ---- Window setup ----------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        // Exclude from Alt+Tab and the taskbar like the main overlay.
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>Re-asserts the window's top-most z-order without activating it,
    /// so showing the main dock (which calls Activate) does not push the left
    /// dock behind it.</summary>
    private void ReassertTopmost()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Realises the window once (shown fully transparent) at startup so
    /// later show/hide is a pure opacity fade with no flicker.</summary>
    public void Realize()
    {
        if (_realized)
            return;
        _realized = true;

        // A null window background leaves transparent regions non-hit-testable,
        // so external drops (desktop shortcut -> dock) only land on the drawn
        // slab. A transparent-but-non-null background makes the whole window a
        // valid drop / hit-test surface while staying visually invisible.
        RootGrid.Background = System.Windows.Media.Brushes.Transparent;

        PositionAndSize();
        RootGrid.Opacity = 0;
        Show();
        Rebuild();
        PanelCanvas.Visibility = Visibility.Collapsed;
    }

    private void PositionAndSize()
    {
        // Size and position against the WORK AREA (which excludes the taskbar)
        // so the dock never covers or crowds the taskbar.
        var wa = SystemParameters.WorkArea;
        _uiScale = Math.Clamp(SystemParameters.PrimaryScreenHeight / 1080.0, 1.0, 2.0);

        // The window hugs the left edge of the work area and is just tall enough
        // for the dock to sit vertically centred. It is wider than the glass slab
        // so the hover name label (drawn to the right of an icon) is not clipped.
        Left = wa.Left;
        Top = wa.Top;
        Width = GIcon * HoverScale + 240 * _uiScale;
        Height = wa.Height;
    }

    // ---- Visibility ------------------------------------------------------

    /// <summary>Shows / hides the dock in step with the main hotkey dock.</summary>
    public void SetMainShown(bool shown)
    {
        _shownByMain = shown;
        UpdateVisibility();
    }

    /// <summary>Shows / hides the dock together with the Ctrl+4 pinned panel
    /// toggle (a sticky open/close, unlike the hold-to-show hotkey).</summary>
    public void SetPinnedShown(bool shown)
    {
        _shownByPinned = shown;
        UpdateVisibility();
    }

    /// <summary>Shows / hides the dock from the left-edge mouse trigger.</summary>
    public void SetEdgeShown(bool shown)
    {
        _shownByEdge = shown;
        UpdateVisibility();
    }

    /// <summary>Keeps the dock visible while an icon is being dragged from the
    /// main dock, so it is always a clear drop target (independent of the edge
    /// trigger or hotkey state).</summary>
    public void SetDragActive(bool active)
    {
        _shownByDrag = active;
        UpdateVisibility();
    }

    /// <summary>Force-dismisses the dock by clearing every show reason at once
    /// (used when the settings window opens, so the dock does not linger on top
    /// of it).</summary>
    public void HideAll()
    {
        _shownByMain = false;
        _shownByEdge = false;
        _shownByDrag = false;
        _shownByPinned = false;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        bool want = _shownByMain || _shownByEdge || _shownByDrag || _shownByPinned;
        if (want == _shown)
            return;
        if (want)
            DoShow();
        else
            DoHide();
    }

    private void DoShow()
    {
        Realize();
        _shown = true;
        PositionAndSize();
        PanelCanvas.Visibility = Visibility.Visible;
        Rebuild();

        // Slide in from the left edge and fade up.
        double slide = _slabW + 40 * _uiScale;
        var slfrom = new TranslateTransform(-slide, 0);
        PanelCanvas.RenderTransform = slfrom;
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var slideAnim = new DoubleAnimation(-slide, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(slideAnim, App.AnimationFrameRate);
        slfrom.BeginAnimation(TranslateTransform.XProperty, slideAnim);

        RootGrid.Opacity = 0;
        RootGrid.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));

        Topmost = true;
        ReassertTopmost();   // keep above the main dock that the hotkey just activated
        _runningTimer.Start();
        RefreshRunning();
    }

    private void DoHide()
    {
        _shown = false;
        CancelDrag();
        _runningTimer.Stop();
        _pinnedScroll = 0;
        HideHoverLabel();

        double from = RootGrid.Opacity;
        var fade = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) =>
        {
            if (!_shown)
                PanelCanvas.Visibility = Visibility.Collapsed;
        };
        RootGrid.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Screen-coordinate rectangle of the visible glass slab, used by
    /// the edge-trigger poll and the main dock's "drop into left dock" test.</summary>
    public Rect GetDockScreenBounds()
    {
        if (!_realized)
            PositionAndSize();
        // Slab is in window-local coords; window is at (Left, Top).
        return new Rect(Left + _slabLeft, Top + _slabTop, _slabW, _slabH);
    }

    /// <summary>Tests a DEVICE-pixel screen point (from another window's
    /// PointToScreen) against this dock and, if it lands over the dock column,
    /// pins the entry here. Converting through this window's own PointFromScreen
    /// keeps the hit test correct on any DPI without manual scaling.</summary>
    public bool TryAcceptDrop(Point screenDevicePoint, AppEntry entry)
    {
        if (!_realized)
        {
            PositionAndSize();
            Realize();
        }
        Point local;
        try { local = PointFromScreen(screenDevicePoint); }
        catch { return false; }

        double icon = EffectiveIconSize;
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;
        // Accept a drop anywhere over the dock window (full height, generous
        // horizontal slack) so blank space counts, not just the icons.
        if (local.X < -icon || local.X > w + icon)
            return false;
        if (local.Y < -icon || local.Y > h + icon)
            return false;

        // Land the icon at the pointer's vertical position within the column.
        double contentY = (local.Y - _slabTop) + _pinnedScroll;
        int dropIdx = (int)Math.Round((contentY - (_pinnedAreaTop - _slabTop) - CellH / 2.0) / CellH);
        AddFromMainDock(entry, dropIdx);
        return true;
    }

    // ---- Layout + build --------------------------------------------------

    private void Layout()
    {
        double icon = EffectiveIconSize;
        double sh = Height > 0 ? Height : SystemParameters.PrimaryScreenHeight;

        double leftGap = 5 * _uiScale;
        // Slab is only as wide as the icon at its hover-enlarged size (plus a
        // hair of breathing room), so the glass background is a snug column.
        double padX = GIcon * (HoverScale - 1.0) / 2.0 + icon * 0.12;
        _slabW = GIcon + padX * 2.0;
        _slabLeft = leftGap;
        _colCenterX = _slabLeft + _slabW / 2.0;

        double topPad = icon * 0.7;
        double botPad = icon * 0.7;
        double seam = _hasRunningArea ? icon * 0.55 : 0;

        // Keep clear of the screen edges and the taskbar. WorkArea already
        // excludes a docked taskbar, but an AUTO-HIDE taskbar leaves WorkArea at
        // full-screen, so reserve an explicit bottom band the dock never enters.
        double topReserve = 12 * _uiScale;
        double bottomReserve = 56 * _uiScale;
        double usableH = sh - topReserve - bottomReserve;

        // The dock shows the resident region (up to 14 icons) AND the running
        // strip (up to RunningMaxComplete + overflow tiles), with one uniform
        // cell height for both so the spacing is identical above and below the
        // divider. Size that shared cell from the comfortable default and, only
        // if the combined column would overflow the usable band, shrink it just
        // enough to fit every row (down to a snug floor so icons never overlap).
        int pinnedCount = _config.LeftDockApps.Count;
        int runSlots = _hasRunningArea ? CurrentRunSlots() : 0;
        int totalCells = pinnedCount + runSlots;
        double fixedChrome = topPad + botPad + (_hasRunningArea ? seam : 0);
        double availForCells = usableH - fixedChrome;

        _cellH = DefaultCellH;
        if (totalCells > 0 && totalCells * _cellH > availForCells)
        {
            double floorCell = GIcon * 1.04;   // keep a hair of gap between icons
            _cellH = Math.Max(floorCell, availForCells / totalCells);
        }

        double runningBlockH = runSlots * CellH;

        // Cap visible pinned rows only if the floor was hit and the column still
        // overflows (so the dock never spills past the usable band).
        int maxVisible = Math.Max(1, (int)Math.Floor((availForCells - runningBlockH) / CellH));
        _pinnedVisible = Math.Min(pinnedCount, maxVisible);

        double pinnedBlockH = _pinnedVisible * CellH;

        _slabH = topPad + pinnedBlockH
               + (_hasRunningArea ? seam + runningBlockH : 0)
               + botPad;
        // Centre within the usable band, then clamp so the bottom edge always
        // stays above the reserved taskbar margin.
        _slabTop = topReserve + Math.Max(0, (usableH - _slabH) / 2.0);
        if (_slabTop + _slabH > sh - bottomReserve)
            _slabTop = sh - bottomReserve - _slabH;
        if (_slabTop < topReserve)
            _slabTop = topReserve;

        _pinnedAreaTop = _slabTop + topPad;
        _runAreaTop = _pinnedAreaTop + pinnedBlockH + seam;

        // Divider line: the true midpoint between the LAST pinned icon's bottom
        // edge and the FIRST running tile's top edge, so the drawn seam matches
        // where the icons actually break.
        double lastPinnedBottom = _pinnedAreaTop + pinnedBlockH - (CellH - GIcon) / 2.0;
        double firstRunTop = _runAreaTop + (RunStep - GIcon) / 2.0;
        _seamY = _pinnedVisible > 0
            ? (lastPinnedBottom + firstRunTop) / 2.0
            : _runAreaTop - seam / 2.0;

        // Clip rect for the pinned scroll layer. Kept INSIDE the glass slab at
        // the top (so icons never spill above the glass while scrolling) yet
        // wide/low enough that a hovered icon's wave (1.7x zoom + pop-out to the
        // right) is not cut.
        double margin = icon * 0.9;
        double clipTop = _slabTop + icon * 0.12;
        double clipBottom = _hasRunningArea
            ? _seamY - icon * 0.05
            : _slabTop + _slabH - icon * 0.12;
        // The hovered icon pops out to the right; extend the clip's right edge to
        // cover its enlarged half-width plus the pop offset.
        double popRight = GIcon / 2.0 * HoverScale + WavePop(HoverScale) + icon * 0.2;
        double clipLeft = _colCenterX - GIcon / 2.0 - margin;
        double clipRight = _colCenterX + popRight;
        _pinnedViewport = new Rect(
            clipLeft,
            clipTop,
            Math.Max(0, clipRight - clipLeft),
            Math.Max(0, clipBottom - clipTop));
    }

    private int _runSlotsCached;
    private int CurrentRunSlots() => _runSlotsCached;

    private double PinnedScrollMax =>
        Math.Max(0, (_config.LeftDockApps.Count - _pinnedVisible)) * CellH;

    private bool PinnedScrollable => PinnedScrollMax > 0.5;

    private void Rebuild()
    {
        Layout();

        // The pinned icon set is about to be rebuilt; tear down any live wave so
        // it can't reference stale icons / slot positions.
        ResetWave();

        PanelCanvas.Children.Clear();
        _pinnedIcons.Clear();
        _pinnedSlots.Clear();
        _scrollLayer = null;
        _scrollTransform = null;
        _hoverLabel = null;
        _hoverLabelText = null;
        // The canvas was just wiped, so any previously-drawn running tiles are
        // gone; drop their references and force ApplyRunning to redraw them
        // (otherwise the signature guard would skip the redraw and leave the
        // reserved running area blank).
        _runTiles.Clear();
        _runSignature = null;
        PruneIconCache();

        double opacity = 1.0 - Math.Clamp(_config.Settings.PanelTransparency, 0.0, 1.0);
        // Corner radius matches the main dock's resident-region border
        // (main-icon * 0.42); EffectiveIconSize / LeftDockScale recovers the
        // main dock's icon size from this scaled-down tray.
        double trayRadius = EffectiveIconSize / LeftDockScale * 0.42;
        // The Saturn theme uses a black smoked-glass side dock; every other
        // theme keeps the clear "liquid glass" body.
        bool darkSlab = ThemeRegistry.Get(_config.Settings.Theme).IsSaturn;
        GlassChrome.DrawSlab(PanelCanvas, _slabLeft, _slabTop, _slabW, _slabH, trayRadius, opacity, track: null, frosted: false, dark: darkSlab);
        if (_hasRunningArea)
            DrawSeam(_seamY, opacity);

        // Pinned column inside a clipped, vertically-scrolling layer.
        _pinnedScroll = Math.Clamp(_pinnedScroll, 0, PinnedScrollMax);
        _scrollTransform = new TranslateTransform(0, -_pinnedScroll);
        _scrollLayer = new Canvas { RenderTransform = _scrollTransform };
        var clip = new Canvas { Clip = new RectangleGeometry(_pinnedViewport) };
        clip.Children.Add(_scrollLayer);
        PanelCanvas.Children.Add(clip);

        var apps = _config.LeftDockApps;
        for (int i = 0; i < apps.Count; i++)
        {
            double cy = _pinnedAreaTop + i * CellH + CellH / 2.0;
            var slot = new Point(_colCenterX, cy);
            _pinnedSlots.Add(slot);

            var icon = CreateIcon(apps[i], GIcon);
            PlaceCentered(icon, slot);
            _scrollLayer.Children.Add(icon);
            _pinnedIcons.Add(icon);
        }

        RefreshRunning();
    }

    private void PlaceCentered(RadialIcon icon, Point center)
    {
        Canvas.SetLeft(icon, center.X - icon.IconSize / 2);
        Canvas.SetTop(icon, center.Y - icon.IconSize / 2);
    }

    private RadialIcon CreateIcon(AppEntry entry, double size)
    {
        if (!_iconCache.TryGetValue(entry.EffectiveIconSource, out var bmp))
        {
            bmp = IconExtractor.GetIcon(entry.EffectiveIconSource);
            _iconCache[entry.EffectiveIconSource] = bmp;
        }
        var icon = new RadialIcon(entry, bmp, size, AccentColor, LabelBrush, dropletHover: true, leftDockStyle: true);
        icon.ExternalMagnify = true;   // the dock drives a coordinated macOS-style wave
        icon.PreviewMouseLeftButtonDown += Icon_PreviewMouseLeftButtonDown;
        return icon;
    }

    private void PruneIconCache()
    {
        if (_iconCache.Count == 0)
            return;
        var live = new HashSet<string>();
        foreach (var e in _config.LeftDockApps)
            live.Add(e.EffectiveIconSource);
        var stale = new List<string>();
        foreach (var key in _iconCache.Keys)
            if (!live.Contains(key))
                stale.Add(key);
        foreach (var key in stale)
            _iconCache.Remove(key);
    }

    private void DrawSeam(double seamY, double opacity)
    {
        double x1 = _slabLeft + 10 * _uiScale;
        double x2 = _slabLeft + _slabW - 10 * _uiScale;

        // A subtle, thin glass-seam divider: a faint cool glow, a soft thin
        // groove, and a barely-there glassy highlight — a quiet hairline split
        // between the dock and the running strip rather than a bold groove.
        var glow = new System.Windows.Shapes.Line
        {
            X1 = x1, X2 = x2, Y1 = seamY, Y2 = seamY,
            StrokeThickness = 5.0,
            Stroke = new SolidColorBrush(Color.FromArgb(0x3A, 0xBF, 0xE0, 0xFF)),
            Opacity = opacity,
            IsHitTestVisible = false,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 5 },
        };
        var groove = new System.Windows.Shapes.Line
        {
            X1 = x1, X2 = x2, Y1 = seamY, Y2 = seamY,
            StrokeThickness = 1.4,
            Stroke = new SolidColorBrush(Color.FromArgb(0x88, 0x02, 0x05, 0x0D)),
            Opacity = opacity,
            IsHitTestVisible = false,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        var shine = new System.Windows.Shapes.Line
        {
            X1 = x1, X2 = x2, Y1 = seamY + 1.6, Y2 = seamY + 1.6,
            StrokeThickness = 1.0,
            Stroke = new SolidColorBrush(Color.FromArgb(0x55, 0xEA, 0xF4, 0xFF)),
            Opacity = opacity,
            IsHitTestVisible = false,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        Panel.SetZIndex(glow, -5);
        Panel.SetZIndex(groove, -5);
        Panel.SetZIndex(shine, -4);
        PanelCanvas.Children.Add(glow);
        PanelCanvas.Children.Add(groove);
        PanelCanvas.Children.Add(shine);
    }

    // ---- Running-but-unpinned strip --------------------------------------

    private readonly List<FrameworkElement> _runTiles = new();

    private void RefreshRunning()
    {
        if (!_shown)
            return;

        var excludePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludeAumids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludeFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Same logic as the main dock's running strip: hide any app that is
        // pinned in EITHER dock, so both strips show the identical set of
        // "running but not pinned anywhere" apps.
        var pinned = new List<AppEntry>(_config.Apps);
        pinned.AddRange(_config.LeftDockApps);
        foreach (var a in pinned)
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
                try
                {
                    string fn = System.IO.Path.GetFileName(a.Path);
                    if (!string.IsNullOrWhiteSpace(fn))
                        excludeFileNames.Add(fn);
                }
                catch { /* ignore */ }
            }
        }

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
                if (ta.Aumid != null && excludeAumids.Contains(ta.Aumid))
                    continue;
                try
                {
                    string fn = System.IO.Path.GetFileName(ta.Path);
                    if (!string.IsNullOrWhiteSpace(fn) && excludeFileNames.Contains(fn))
                        continue;
                }
                catch { /* ignore */ }
                filtered.Add(ta);
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (!_shown)
                    return;
                ApplyPinnedRunning(snapshot, explorerTitles);
                ApplyRunning(filtered);
            });
        });
    }

    /// <summary>Lights up the flowing blue border on each pinned icon whose
    /// target program is currently running (mirrors the main dock).</summary>
    private void ApplyPinnedRunning(RunningAppTracker.RunningSnapshot snapshot, List<string> explorerTitles)
    {
        foreach (var icon in _pinnedIcons)
        {
            try
            {
                icon.IsRunning = RunningAppTracker.IsRunningInSnapshot(icon.Entry.Path, snapshot)
                    || RunningAppTracker.IsShellItemRunning(icon.Entry.Name, icon.Entry.Path, explorerTitles);
            }
            catch { icon.IsRunning = false; }
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
        int slots = display.Count + (overflow > 0 ? 1 : 0);

        // Signature so we skip a needless rebuild (and the dock re-layout that a
        // changed slot count forces).
        var sb = new System.Text.StringBuilder();
        foreach (var a in display)
            sb.Append(a.Aumid ?? a.Path).Append(';');
        if (overflow > 0)
            sb.Append('+').Append(overflow);
        string sig = sb.ToString();

        bool hadArea = _hasRunningArea;
        bool wantArea = slots > 0;
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

        if (slots == 0)
            return;

        double tile = GIcon;
        for (int k = 0; k < slots; k++)
        {
            double cy = _runAreaTop + k * RunStep + RunStep / 2.0;
            bool isOverflow = overflow > 0 && k == slots - 1;
            FrameworkElement el = isOverflow
                ? BuildRunOverflowTile(overflow, tile)
                : BuildRunTile(display[k], tile);
            Canvas.SetLeft(el, _colCenterX - tile / 2.0);
            Canvas.SetTop(el, cy - tile / 2.0);
            Panel.SetZIndex(el, 60);
            PanelCanvas.Children.Add(el);
            _runTiles.Add(el);
        }
    }

    private FrameworkElement BuildRunTile(TaskbarApp app, double size)
    {
        if (!_runIconCache.TryGetValue(app.Path, out var bmp))
        {
            bmp = IconExtractor.GetIcon(app.Path);
            _runIconCache[app.Path] = bmp;
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

        var root = new Grid
        {
            Width = size,
            Height = size,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
        };
        root.Children.Add(plate);
        // Every tile here is by definition running → always show the breathing
        // green dot, exactly like the pinned icons.
        root.Children.Add(BuildRunningDot(size));

        var scale = (ScaleTransform)root.RenderTransform;
        var dur = new Duration(TimeSpan.FromMilliseconds(110));
        IntPtr win = app.Window;
        string label = System.IO.Path.GetFileNameWithoutExtension(app.Path);
        root.MouseEnter += (_, _) =>
        {
            Panel.SetZIndex(root, 100);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(HoverScale, dur));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(HoverScale, dur));
            // Same hover label / font as the pinned icons. Running tiles scale
            // about their centre (no rightward pop), so the label sits just past
            // the icon's enlarged half-width.
            double cy = Canvas.GetTop(root) + size / 2.0;
            _runLabelShown = true;
            ShowHoverLabelCore(label, cy, size / 2.0 * HoverScale);
        };
        root.MouseLeave += (_, _) =>
        {
            Panel.SetZIndex(root, 60);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, dur));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, dur));
            _runLabelShown = false;
            HideHoverLabel();
        };
        root.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            WindowPreviewService.Activate(win);
            SetEdgeShown(false);
        };
        return root;
    }

    /// <summary>A small breathing green dot hugging the left edge of a tile —
    /// the shared "running" indicator used by both the pinned icons and the
    /// running strip. Returns a zero-impact Canvas with its pulse already
    /// animating.</summary>
    private FrameworkElement BuildRunningDot(double iconSize)
    {
        double dot = Math.Max(3.0, iconSize * 0.075);
        double glow = dot * 2.3;
        double cy = iconSize / 2.0;
        double cx = dot * 0.05;

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
        return new Grid
        {
            Width = size,
            Height = size,
            Background = Brushes.Transparent,
            ToolTip = $"另有 {extra} 个正在运行",
            Children = { plate },
        };
    }

    // ---- macOS-dock magnification wave (continuous + frame-smoothed) -------

    /// <summary>Drives the wave + hover label from the live window pointer
    /// position. Activation is tested against the STATIC pinned viewport rect —
    /// never against the per-icon hit areas — so the pop-out (which slides an
    /// icon out from under the cursor) cannot create an enter/leave feedback
    /// loop. That loop was the source of the hover flicker/jump.</summary>
    private void UpdateWaveFromPointer(Point p)
    {
        bool inColumn = _pinnedIcons.Count > 0 && _pinnedViewport.Contains(p);
        if (inColumn)
        {
            _waveCursorY = p.Y;
            EnsureWaveTicking();
            int idx = NearestVisibleIconIndex(p.Y);
            if (idx != _labelIdx)
            {
                _labelIdx = idx;
                if (idx >= 0)
                    ShowHoverLabel(_pinnedIcons[idx], idx);
                else
                    HideHoverLabel();
            }
        }
        else
        {
            _waveCursorY = double.NaN;   // the tick loop eases everything back to rest
            if (_labelIdx >= 0)
            {
                _labelIdx = -1;
                // Don't fight a running-strip tile that is currently showing its
                // own hover label (the pointer is over the running area below the
                // pinned viewport).
                if (!_runLabelShown)
                    HideHoverLabel();
            }
        }
    }

    /// <summary>Pinned icon whose (scrolled) centre is closest to <paramref name="y"/>.</summary>
    private int NearestVisibleIconIndex(double y)
    {
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < _pinnedIcons.Count; i++)
        {
            double d = Math.Abs((_pinnedSlots[i].Y - _pinnedScroll) - y);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }

    // Influence radius of the wave, in icon-cell units. Icons within this many
    // cells of the cursor are magnified; the falloff is a smooth raised cosine.
    // A smaller support makes the wave sharper/peakier (neighbours fall off to
    // rest faster), so the bulge is concentrated under the pointer.
    private const double WaveSupport = 2.3;

    /// <summary>Continuous magnification for an icon, as a smooth function of its
    /// distance (in cells) from the cursor — peaks at <see cref="HoverScale"/>
    /// directly under the pointer and eases to 1.0 at the support edge.</summary>
    private double WaveScaleAt(double cursorY, double iconCenterY)
    {
        double d = Math.Abs(cursorY - iconCenterY) / CellH;
        if (d >= WaveSupport)
            return 1.0;
        double f = 0.5 * (1.0 + Math.Cos(Math.PI * d / WaveSupport));
        return 1.0 + (HoverScale - 1.0) * f;
    }

    /// <summary>Horizontal pop-out (toward the screen) for a given magnification,
    /// growing with the scale so larger icons jut out further — the macOS feel.</summary>
    private double WavePop(double scale) => (scale - 1.0) * GIcon * 1.18;

    private void EnsureWaveTicking()
    {
        if (_waveTicking)
            return;
        _waveTicking = true;
        _waveLastTick = TimeSpan.Zero;
        CompositionTarget.Rendering += OnWaveTick;
    }

    /// <summary>Per-frame smoothing of the wave toward the cursor-driven target.
    /// Running this off the render clock (rather than restarting a per-icon
    /// animation on every MouseMove) is what makes the slide fluid: the cursor
    /// supplies the target, the frame loop eases every icon toward it.</summary>
    private void OnWaveTick(object? sender, EventArgs e)
    {
        int n = _pinnedIcons.Count;
        if (_waveCur.Length != n)
        {
            var old = _waveCur;
            _waveCur = new double[n];
            for (int i = 0; i < n; i++)
                _waveCur[i] = i < old.Length ? old[i] : 1.0;
        }

        // Frame-rate-independent easing (tau = 45 ms) so the feel is identical
        // on 60 Hz and high-refresh panels.
        double dt = 1.0 / 60.0;
        if (e is RenderingEventArgs rea)
        {
            if (_waveLastTick > TimeSpan.Zero)
                dt = Math.Clamp((rea.RenderingTime - _waveLastTick).TotalSeconds, 0.001, 0.05);
            _waveLastTick = rea.RenderingTime;
        }
        double k = 1.0 - Math.Exp(-dt / 0.045);

        bool active = !double.IsNaN(_waveCursorY);
        double maxDelta = 0.0;
        for (int i = 0; i < n; i++)
        {
            double iconCenterY = _pinnedSlots[i].Y - _pinnedScroll;
            double target = active ? WaveScaleAt(_waveCursorY, iconCenterY) : 1.0;
            double cur = _waveCur[i] + (target - _waveCur[i]) * k;
            _waveCur[i] = cur;
            maxDelta = Math.Max(maxDelta, Math.Abs(target - cur));
            _pinnedIcons[i].SetMagnify(cur, WavePop(cur));
            // Larger icons sit on top so the bulge overlaps its neighbours cleanly.
            Panel.SetZIndex(_pinnedIcons[i], cur > 1.001 ? 3000 + (int)(cur * 1000) : 0);
        }

        // Once the wave has fully settled back to rest, stop the loop.
        if (!active && maxDelta < 0.0015)
        {
            for (int i = 0; i < n; i++)
            {
                _pinnedIcons[i].SetMagnify(1.0, 0.0);
                Panel.SetZIndex(_pinnedIcons[i], 0);
            }
            CompositionTarget.Rendering -= OnWaveTick;
            _waveTicking = false;
        }
    }

    /// <summary>Hard, immediate reset of the wave (used on rebuild / drag start).</summary>
    private void ResetWave()
    {
        _waveCursorY = double.NaN;
        if (_labelIdx >= 0)
        {
            _labelIdx = -1;
            HideHoverLabel();
        }
        foreach (var ic in _pinnedIcons)
        {
            ic.SetMagnify(1.0, 0.0);
            Panel.SetZIndex(ic, 0);
        }
        if (_waveTicking)
        {
            CompositionTarget.Rendering -= OnWaveTick;
            _waveTicking = false;
        }
        _waveCur = Array.Empty<double>();
    }

    private void ShowHoverLabel(RadialIcon ic, int idx)
    {
        double cy = _pinnedSlots[idx].Y - _pinnedScroll;
        double rightExtent = GIcon / 2.0 * HoverScale + WavePop(HoverScale);
        ShowHoverLabelCore(ic.Entry.Name, cy, rightExtent);
    }

    /// <summary>Shared hover-label renderer used by both the pinned icons and the
    /// running strip, so both use the same font, size and styling.</summary>
    private void ShowHoverLabelCore(string name, double cy, double rightExtent)
    {
        if (_hoverLabel == null)
        {
            _hoverLabelText = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                Foreground = LabelBrush,
                TextAlignment = TextAlignment.Left,
                TextWrapping = TextWrapping.NoWrap,
            };
            _hoverLabel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x26, 0x1A, 0x1A, 0x1A)),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 4, 10, 4),
                IsHitTestVisible = false,
                Opacity = 0,
                Child = _hoverLabelText,
            };
            Panel.SetZIndex(_hoverLabel, 4000);
            PanelCanvas.Children.Add(_hoverLabel);
        }

        _hoverLabelText!.Text = name;

        // Centre vertically on the icon, floated to its right (clear of the
        // hover-enlarged icon — pinned icons also pop out to the right).
        double labelX = _colCenterX + rightExtent + 8 * _uiScale;

        // Auto-shrink the font so the WHOLE name fits between the label's left
        // edge and the screen's right edge (no ellipsis). Start from the regular
        // size and step down only as far as needed, with a sensible floor.
        double maxFont = 10.5 * HoverScale;
        double minFont = 7.5 * HoverScale;
        double horizPad = 20 * _uiScale;                 // matches Border padding (10 + 10)
        double screenW = ActualWidth > 0 ? ActualWidth : Width;
        double avail = Math.Max(40 * _uiScale, screenW - labelX - horizPad - 6 * _uiScale);
        _hoverLabelText.FontSize = FitFontSize(name, maxFont, minFont, avail);

        _hoverLabel.InvalidateMeasure();
        _hoverLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double lh = _hoverLabel.DesiredSize.Height;
        Canvas.SetLeft(_hoverLabel, labelX);
        Canvas.SetTop(_hoverLabel, cy - lh / 2.0);

        _hoverLabel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(_hoverLabel.Opacity, 1, TimeSpan.FromMilliseconds(110)));
    }

    private void HideHoverLabel()
    {
        if (_hoverLabel == null)
            return;
        _hoverLabel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(_hoverLabel.Opacity, 0, TimeSpan.FromMilliseconds(110)));
    }

    /// <summary>Largest font size in [<paramref name="minFont"/>,
    /// <paramref name="maxFont"/>] at which <paramref name="text"/> renders within
    /// <paramref name="availW"/> device-independent pixels, so the whole name
    /// shows without ellipsis. Falls back to the floor when even that overflows.</summary>
    private double FitFontSize(string text, double maxFont, double minFont, double availW)
    {
        if (string.IsNullOrEmpty(text) || availW <= 0)
            return maxFont;

        var tf = new Typeface(
            _hoverLabelText!.FontFamily, _hoverLabelText.FontStyle,
            _hoverLabelText.FontWeight, _hoverLabelText.FontStretch);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        double Width(double fs)
        {
            var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, tf, fs, Brushes.White, dpi);
            return ft.WidthIncludingTrailingWhitespace;
        }

        if (Width(maxFont) <= availW)
            return maxFont;

        // Binary search for the largest fitting size.
        double lo = minFont, hi = maxFont;
        for (int i = 0; i < 12; i++)
        {
            double mid = (lo + hi) / 2.0;
            if (Width(mid) <= availW)
                lo = mid;
            else
                hi = mid;
        }
        return lo;
    }

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
        bool outside = Math.Abs(p.X - _colCenterX) > _slabW * 0.85;
        _pressedIcon.Opacity = outside ? 0.4 : 1.0;

        // macOS-style "push aside": slide the other icons open to reveal a gap at
        // the insertion point the drop would land on. Only re-arrange when that
        // index actually changes so the eases run to completion smoothly.
        if (!outside)
        {
            double contentY = p.Y + _pinnedScroll;
            int tgt = (int)Math.Round((contentY - _pinnedAreaTop - CellH / 2.0) / CellH);
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

    /// <summary>Smoothly slides an icon's Canvas position toward a slot centre.</summary>
    private void AnimateIconTo(RadialIcon icon, Point center)
    {
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
            Launch(icon.Entry);
            return;
        }

        bool outside = Math.Abs(p.X - _colCenterX) > _slabW * 0.85;
        if (outside)
        {
            RemoveFromLeftDock(icon.Entry);
            return;
        }

        // Reorder: drop into the slot nearest the cursor's (scroll-adjusted) Y.
        // The left dock mirrors the main dock's resident region, so reorder the
        // matching entries in the resident slice of _config.Apps directly.
        double contentY = p.Y + _pinnedScroll;
        int tgt = (int)Math.Round((contentY - _pinnedAreaTop - CellH / 2.0) / CellH);
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
        var anim = new DoubleAnimation(_scrollTransform.Y, -_pinnedScroll, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        };
        Timeline.SetDesiredFrameRate(anim, App.AnimationFrameRate);
        _scrollTransform.Y = -_pinnedScroll;
        _scrollTransform.BeginAnimation(TranslateTransform.YProperty, anim);
        e.Handled = true;
    }

    // ---- Add / remove -----------------------------------------------------

    /// <summary>Adds a main-dock app to the left dock (called when an icon is
    /// dragged from the main dock onto this dock). Because the left dock mirrors
    /// the resident region, this promotes the entry into the top two rows of the
    /// main dock so it appears in both places. <paramref name="index"/> is the
    /// desired position within the resident region (-1 = append).</summary>
    public void AddFromMainDock(AppEntry entry, int index = -1)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Path))
            return;
        int idx = _config.Apps.FindIndex(e => DockSync.Matches(e, entry));
        if (idx >= 0 && idx < DockSync.ResidentCount(_config))
            return;   // already resident — nothing to do.

        if (idx >= 0)
        {
            var e = _config.Apps[idx];
            _config.Apps.RemoveAt(idx);
            if (index >= 0) DockSync.InsertResident(_config, e, index);
            else DockSync.AppendResident(_config, e);
        }
        else
        {
            if (index >= 0) DockSync.InsertResident(_config, entry, index);
            else DockSync.AppendResident(_config, entry);
        }
        AfterSharedChange();
    }

    private void RemoveFromLeftDock(AppEntry entry)
    {
        // The left dock mirrors the resident region, so removing an icon here
        // removes the app from the main dock's resident apps as well.
        int idx = _config.Apps.FindIndex(e => DockSync.Matches(e, entry));
        if (idx >= 0)
            _config.Apps.RemoveAt(idx);
        AfterSharedChange();
    }

    /// <summary>Re-mirrors the resident region into the left dock, persists, and
    /// refreshes both docks. Call after any change that mutates _config.Apps
    /// from the left dock side.</summary>
    private void AfterSharedChange()
    {
        DockSync.MirrorResidentToLeft(_config);
        _persist();
        Rebuild();
        MainDockChanged?.Invoke();
    }

    /// <summary>Re-syncs the left dock after the main dock changed its app list
    /// (resident region). Called by the host when the main dock mutates.</summary>
    public void RefreshFromConfig()
    {
        DockSync.MirrorResidentToLeft(_config);
        Rebuild();
    }

    // ---- External drop (desktop shortcut -> both docks) -------------------

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = (e.Data.GetDataPresent(DataFormats.FileDrop) || ShellNamespace.HasShellItems(e.Data))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        var entries = new List<AppEntry>();
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var f in files)
            {
                var entry = ShortcutResolver.CreateEntry(f);
                if (entry != null)
                    entries.Add(entry);
            }
        }
        if (ShellNamespace.HasShellItems(e.Data))
            entries.AddRange(ShellNamespace.CreateEntries(e.Data));

        // Insertion index from the pointer Y, so the dropped icon lands where the
        // cursor is rather than always at the bottom of the resident region.
        var drop = e.GetPosition(PanelCanvas);
        double contentY = drop.Y + _pinnedScroll;
        int dropIdx = (int)Math.Round((contentY - _pinnedAreaTop - CellH / 2.0) / CellH);
        dropIdx = Math.Clamp(dropIdx, 0, DockSync.ResidentCount(_config));

        bool changed = false;
        foreach (var entry in entries)
        {
            // Dropping on the left dock makes the app resident: insert it into
            // the top two rows of the main dock (the left dock mirrors those) at
            // the pointer position.
            int idx = _config.Apps.FindIndex(e => DockSync.Matches(e, entry));
            if (idx < 0)
            {
                DockSync.InsertResident(_config, entry, dropIdx);
                dropIdx++;
                changed = true;
            }
            else if (idx >= DockSync.ResidentCount(_config))
            {
                var moved = _config.Apps[idx];
                _config.Apps.RemoveAt(idx);
                DockSync.InsertResident(_config, moved, dropIdx);
                dropIdx++;
                changed = true;
            }
        }
        if (changed)
            AfterSharedChange();
        e.Handled = true;
    }

    /// <summary>Raised when the left dock mutates the shared main-dock app list
    /// (e.g. a desktop shortcut dropped here), so the main dock can refresh.</summary>
    public event Action? MainDockChanged;

    // ---- Launch -----------------------------------------------------------

    private void Launch(AppEntry entry)
    {
        SetEdgeShown(false);

        if (entry.IsShellItem)
        {
            try { ShellNamespace.Launch(entry.Path); }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开 {entry.Name}:\n{ex.Message}", "Polaris",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        if (!IsFileExplorer(entry.Path, entry.Arguments))
        {
            try
            {
                if (RunningAppTracker.ActivateExisting(entry.Path))
                    return;
            }
            catch { /* fall through */ }
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = entry.Path,
                Arguments = entry.Arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(entry.WorkingDirectory)
                    ? System.IO.Path.GetDirectoryName(entry.Path) ?? ""
                    : entry.WorkingDirectory,
                UseShellExecute = true,
            };
            var started = Process.Start(psi);
            RunningAppTracker.EnsureRestoredWhenReady(started);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法启动 {entry.Name}:\n{ex.Message}", "Polaris",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static bool IsFileExplorer(string path, string args)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        bool isExplorer = path.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase);
        bool hasShellArg = !string.IsNullOrWhiteSpace(args) &&
            args.Contains("shell:AppsFolder", StringComparison.OrdinalIgnoreCase);
        return isExplorer && !hasShellArg;
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) &&
                ColorConverter.ConvertFromString(hex) is Color c)
                return c;
        }
        catch { /* ignore */ }
        return fallback;
    }
}
