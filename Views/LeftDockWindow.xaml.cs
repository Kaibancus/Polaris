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
    private const double HoverScale = 1.5;
    private const double DragThreshold = 6.0;
    private const int RunningMaxComplete = 6;     // at most 6 full running-app icons
    // Left dock icon scale relative to the main dock. Kept identical for every
    // theme so the side dock's icon size, cell pitch and gaps are consistent no
    // matter which theme is active (previously Saturn used a larger 0.60 scale,
    // which made its side-dock spacing differ from the glass theme's).
    private const double LeftDockScale = 0.50;

    private readonly AppConfig _config;
    private readonly Action _persist;
    private readonly Dictionary<string, BitmapSource?> _iconCache = new();
    private readonly Dictionary<string, BitmapSource?> _runIconCache = new();

    // Known launcher → helper exe names. A pinned launcher whose taskbar window is
    // actually owned by a differently-named helper process (so it can't be matched
    // by the launcher's own path/name) lists its helper exe file name(s) here, so
    // the helper window is folded into the launcher tile instead of showing as a
    // separate running app. Keyed on the pinned launcher's exe file name.
    private static readonly Dictionary<string, string[]> LauncherHelperExeNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["steam.exe"] = new[] { "steamwebhelper.exe" },
        };

    private double _uiScale = 1.0;

    // Screen edge this dock is anchored to. Drives every geometry decision
    // through the main/cross axis helpers below: the dock lays its icons out
    // along the MAIN axis (the length of the screen edge) and pops them / draws
    // its slab along the CROSS axis (the thickness, growing toward the screen
    // interior). For Left/Right the main axis is vertical (Y) and the cross axis
    // horizontal (X); for Top/Bottom they swap.
    private DockSide _side = DockSide.Left;
    private bool IsVertical => _side == DockSide.Left || _side == DockSide.Right;
    private double WinW => ActualWidth > 0 ? ActualWidth : Width;
    private double WinH => ActualHeight > 0 ? ActualHeight : Height;

    /// <summary>Window-local point for a logical (main, cross) pair. cross = 0
    /// sits on the screen edge and grows toward the screen interior.</summary>
    private Point ToLocal(double main, double cross) => _side switch
    {
        DockSide.Left => new Point(cross, main),
        DockSide.Right => new Point(WinW - cross, main),
        DockSide.Top => new Point(main, cross),
        DockSide.Bottom => new Point(main, WinH - cross),
        _ => new Point(cross, main),
    };

    /// <summary>Main-axis coordinate of a window-local point.</summary>
    private double MainOf(Point p) => IsVertical ? p.Y : p.X;

    /// <summary>Cross-axis coordinate (distance from the screen edge inward).</summary>
    private double CrossOf(Point p) => _side switch
    {
        DockSide.Left => p.X,
        DockSide.Right => WinW - p.X,
        DockSide.Top => p.Y,
        DockSide.Bottom => WinH - p.Y,
        _ => p.X,
    };

    /// <summary>Window-local rect for a logical block spanning [crossStart,
    /// crossStart+crossLen] across and [mainStart, mainStart+mainLen] along.</summary>
    private Rect LogicalRect(double mainStart, double crossStart, double mainLen, double crossLen) => _side switch
    {
        DockSide.Left => new Rect(crossStart, mainStart, crossLen, mainLen),
        DockSide.Right => new Rect(WinW - crossStart - crossLen, mainStart, crossLen, mainLen),
        DockSide.Top => new Rect(mainStart, crossStart, mainLen, crossLen),
        DockSide.Bottom => new Rect(mainStart, WinH - crossStart - crossLen, mainLen, crossLen),
        _ => new Rect(crossStart, mainStart, crossLen, mainLen),
    };

    /// <summary>Window-local point for a logical (main, cross) coordinate.</summary>
    private Point LogicalPoint(double main, double cross) => _side switch
    {
        DockSide.Left => new Point(cross, main),
        DockSide.Right => new Point(WinW - cross, main),
        DockSide.Top => new Point(main, cross),
        DockSide.Bottom => new Point(main, WinH - cross),
        _ => new Point(cross, main),
    };

    /// <summary>The (dx, dy) window-local translation for a cross-axis pop of
    /// <paramref name="pop"/> toward the screen interior.</summary>
    private (double dx, double dy) PopOffset(double pop) => _side switch
    {
        DockSide.Left => (pop, 0.0),
        DockSide.Right => (-pop, 0.0),
        DockSide.Top => (0.0, pop),
        DockSide.Bottom => (0.0, -pop),
        _ => (pop, 0.0),
    };

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
    // Held true while a clicked icon plays its launch bounce, so the dock can't
    // dismiss out from under the animation before it finishes.
    private bool _bounceHold;
    // Held true while a right-click context menu is open, so the dock stays put
    // even when the pointer moves off it onto the menu.
    private bool _menuHold;
    private System.Windows.Controls.Primitives.Popup? _dockMenu;
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
    // actual cursor MAIN-axis position and smoothed per render frame.
    private double _waveCursorY = double.NaN;        // cursor main-axis coord; NaN = inactive
    private int _labelIdx = -1;                       // icon the hover label is showing for (-1 none)
    private double[] _waveCur = Array.Empty<double>(); // smoothed per-icon scale
    private bool _waveTicking;                        // CompositionTarget.Rendering hooked?
    private TimeSpan _waveLastTick;

    // Saturn-only: a black overlay whose interior edge bulges outward to follow
    // the magnification wave, so the dark dock background "breathes" with the
    // icons instead of staying a rigid rectangle. Rebuilt each wave frame.
    private System.Windows.Shapes.Path? _waveBulge;
    private bool _darkDock;
    private double _bulgeOpacity = 1.0;
    private double _flameFeather = 10.0;   // flame outline feather, matched to the slab edge fade

    // Launch-bounce flame: while a clicked icon hops, the wave loop is stopped, so
    // we feed the Saturn flame a synthetic contributor (a single tongue centred on
    // the bouncing icon whose intensity tracks the live hop height) so the flame
    // leaps up with the icon. Driven by OnBounceFlameTick; ignored by glass themes.
    private bool _bounceFlameActive;
    private double _bounceFlameAmp;        // 0..1 hop progress → flame intensity
    private double _bounceFlameCenterMain; // main-axis centre of the bouncing item
    private TranslateTransform? _bounceFlameTrans; // live transform we read the hop from
    private bool _bounceFlameAxisY = true; // read the hop from Y (Left/Right) or X (Top/Bottom)
    private double _bounceFlameMaxLift = 1.0;

    // Saturn debris belt: scattered rocks rendered across the dark slab. Each rock
    // keeps its resting (main, cross) and a live TranslateTransform so the wave can
    // shove it toward the screen interior as the magnification bulge sweeps past —
    // the rubble field "parts" around the cursor. Rebuilt with the slab.
    private sealed class DebrisRock
    {
        public double Main;          // resting main-axis coord
        public double Parallax;      // 0..1 displacement factor (depth feel)
        public TranslateTransform Tr = new();
        public double Cur;           // current cross-axis push (eased)
    }
    private readonly List<DebrisRock> _debris = new();

    private Border? _hoverLabel;
    private TextBlock? _hoverLabelText;
    private bool _runLabelShown;

    private readonly System.Windows.Threading.DispatcherTimer _runningTimer;
    private string? _runSignature;

    // Cached layout geometry (logical main/cross), recomputed each Rebuild.
    // *Cross = thickness direction (toward screen interior); *Main = along edge.
    private double _slabCross, _slabMain, _slabCrossLen, _slabMainLen, _colCenterCross;
    // Visible glass/dark body bounds (narrower than the full _slabCrossLen hit
    // area), used for both the slab draw and the running-strip seam.
    private double _bodyCross, _bodyCrossLen;
    private int _pinnedVisible;
    private double _pinnedAreaMain;   // main coord of the first pinned slot's cell start
    private double _runAreaMain;      // main coord where the running strip begins
    private double _seamMain;         // main coord of the divider between pinned + running
    private bool _hasRunningArea;
    private Rect _pinnedViewport;    // window-local clip rect for the scroll layer
    private Rect _waveHitRect;       // window-local region that activates the wave
                                     // (pinned column + running strip, so the wave
                                     // is continuous across the seam)

    private double EffectiveIconSize => _config.Settings.IconSize * _uiScale * LeftDockScale;
    private double GIcon => EffectiveIconSize * GlassIconScale;
    // Cell pitch along the column. Tightened from 1.58 so icons sit closer
    // together (smaller gaps); identical for every theme via LeftDockScale.
    private double DefaultCellH => EffectiveIconSize * 1.46;
    private double CellH => _cellH > 0 ? _cellH : DefaultCellH;
    // Running-strip tiles use the SAME vertical step as the pinned cells so the
    // icon spacing is identical above and below the divider.
    private double RunStep => CellH;
    private double _cellH;

    private Brush LabelBrush => new SolidColorBrush(ParseColor(_config.Settings.FontColor, Colors.White));
    private Color AccentColor => ParseColor(_config.Settings.AccentColor, Color.FromRgb(0x3D, 0x7E, 0xFF));

    public bool DockVisible => _shown;

    /// <summary>The screen edge this dock is currently anchored to (from
    /// settings). Used by the host's edge poll to test the correct trigger band.</summary>
    public DockSide DockSidePosition => _config.Settings.DockPosition;

    /// <summary>Invoked when the user clicks the Polaris tile in the running
    /// strip. Wired by App to toggle the pinned docks (equivalent to Ctrl+4).</summary>
    public Action? ToggleDocks { get; set; }

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
        // Size and position against the ACTIVE monitor's WORK AREA (which
        // excludes the taskbar) so the dock never covers or crowds the taskbar.
        // The active monitor is the primary one by default, or the monitor the
        // cursor was on when "show on all monitors" is enabled.
        var wa = MonitorLayout.ActiveWorkArea;
        _uiScale = Math.Clamp(MonitorLayout.ActiveBounds.Height / 1080.0, 1.0, 2.0);
        _side = _config.Settings.DockPosition;

        // The window covers a band along the anchored edge: it spans the full
        // length of the work area along the MAIN axis and is just thick enough
        // (along the CROSS axis) for the glass slab plus the hover pop-out and
        // the floating name label. Vertical docks are narrow + tall; horizontal
        // docks are wide + short.
        //
        // The cross THICKNESS differs by orientation. A vertical dock's name
        // label extends sideways (along the cross axis) by its full WIDTH (a long
        // app name can be 150+ px), so it needs the generous 240 reserve. A
        // horizontal dock's label instead hangs above/below the icon and extends
        // by its (single-line) HEIGHT, so it needs far less cross room — only the
        // hover-enlarged icon, the interior wave pop-out, and one label line.
        // Keeping a horizontal dock thin shrinks its layered (AllowsTransparency)
        // surface, which otherwise overlaps the full-screen main dock at the
        // bottom of the screen and forces a costly double per-frame composite on
        // the render thread (visible as a frame-rate drop on summon).
        double thickness = IsVertical
            ? GIcon * HoverScale + 240 * _uiScale
            : GIcon * HoverScale + 130 * _uiScale;
        switch (_side)
        {
            case DockSide.Right:
                Left = wa.Right - thickness;
                Top = wa.Top;
                Width = thickness;
                Height = wa.Height;
                break;
            case DockSide.Top:
            {
                // A horizontal dock is sized to hug its centred content rather
                // than spanning the full work-area width. A full-width layered
                // (AllowsTransparency) window forces a large per-frame software
                // composite that competes with the main dock's full-screen
                // summon animation and visibly drops the frame rate; a snug,
                // screen-centred window keeps that surface small. The icon
                // cluster stays centred on the work area regardless (Layout
                // centres it within the window, and the window is centred here).
                double winW = Math.Min(DesiredContentMain(), wa.Width);
                Left = wa.Left + (wa.Width - winW) / 2.0;
                Top = wa.Top;
                Width = winW;
                Height = thickness;
                break;
            }
            case DockSide.Bottom:
            {
                double winW = Math.Min(DesiredContentMain(), wa.Width);
                Left = wa.Left + (wa.Width - winW) / 2.0;
                Top = wa.Bottom - thickness;
                Width = winW;
                Height = thickness;
                break;
            }
            case DockSide.Left:
            default:
                Left = wa.Left;
                Top = wa.Top;
                Width = thickness;
                Height = wa.Height;
                break;
        }
    }

    /// <summary>Desired length (DIP) of the dock window along its MAIN axis for a
    /// HORIZONTAL dock: just enough for the pinned column plus the running strip
    /// at the default cell pitch, with the running strip reserved at its MAXIMUM
    /// slot count so the window never has to resize (and flicker) as apps come
    /// and go. Capped to the work-area width by the caller. Mirrors the slab
    /// length computed in <see cref="Layout"/> (DefaultCellH, no shrink) so the
    /// content fits without the cell-shrink path triggering.</summary>
    private double DesiredContentMain()
    {
        double icon = EffectiveIconSize;
        double cell = DefaultCellH;
        double pad = icon * 0.7;                              // startPad / endPad
        double seam = icon * 0.55;                            // running area always present
        int pinned = _config.LeftDockApps.Count;
        const int maxRunSlots = 1 + RunningMaxComplete + 1;   // Polaris + full tiles + overflow
        double reserve = 12 * _uiScale;                       // horizontal: symmetric end reserves
        return reserve + pad + pinned * cell + seam + maxRunSlots * cell + pad + reserve;
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
        bool want = _shownByMain || _shownByEdge || _shownByDrag || _shownByPinned || _bounceHold || _menuHold;
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

        // Slide in from the anchored edge and fade up.
        double slide = _slabCrossLen + 40 * _uiScale;
        var (sdx, sdy) = PopOffset(-slide);   // start offset toward the screen edge
        var slfrom = new TranslateTransform(sdx, sdy);
        PanelCanvas.RenderTransform = slfrom;
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var slideAnim = new DoubleAnimation(IsVertical ? sdx : sdy, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(slideAnim, App.AnimationFrameRate);
        slfrom.BeginAnimation(IsVertical ? TranslateTransform.XProperty : TranslateTransform.YProperty, slideAnim);

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
        CloseDockMenu();
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
        // Slab is logical (main/cross); convert to the window-local rect, then
        // offset by the window's screen position.
        Rect local = LogicalRect(_slabMain, _slabCross, _slabMainLen, _slabCrossLen);
        return new Rect(Left + local.X, Top + local.Y, local.Width, local.Height);
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
        // Accept a drop only over the DOCK SLAB (plus a modest slack so blank
        // space around the icons still counts), NOT the whole window. The window
        // is a band that, for a top/bottom dock, spans the full screen width and
        // a few hundred px of height — testing the whole window would let the
        // side dock hijack drops that land over the centred main dock's
        // non-resident region, blocking the main dock's resident<->non-resident
        // drag. Constraining to the slab keeps the drop zone hugging the screen
        // edge on every side (left/right behaviour is unchanged).
        double cross = CrossOf(local);
        double main = MainOf(local);
        if (cross < -icon || cross > _slabCross + _slabCrossLen + icon)
            return false;
        if (main < _slabMain - icon || main > _slabMain + _slabMainLen + icon)
            return false;

        // Land the icon at the pointer's position along the column. Use the
        // insertion-GAP index (round to the nearest boundary between icons), not
        // the nearest icon centre — the latter biases every drop half a cell
        // toward the leading end, so an icon dropped on a slot's lower half would
        // land one position too early.
        double contentMain = main + _pinnedScroll;
        int dropIdx = (int)Math.Round((contentMain - _pinnedAreaMain) / CellH);
        AddFromMainDock(entry, dropIdx);
        return true;
    }

    // ---- Layout + build --------------------------------------------------

    private void Layout()
    {
        double icon = EffectiveIconSize;
        // Length available along the anchored edge (the main axis).
        double mainExtent = IsVertical
            ? (Height > 0 ? Height : MonitorLayout.ActiveBounds.Height)
            : (Width > 0 ? Width : MonitorLayout.ActiveBounds.Width);

        double crossGap = 1 * _uiScale;
        // Slab is only as thick as the icon at its hover-enlarged size (plus a
        // hair of breathing room), so the glass background is a snug strip.
        double padCross = GIcon * (HoverScale - 1.0) / 2.0 + icon * 0.12;
        _slabCrossLen = GIcon + padCross * 2.0;
        _slabCross = crossGap;
        // Bias the resting icon column toward the screen edge so the icons hug
        // it. The hover wave pops icons toward the interior, so the edge-side
        // half of the slab's hover-reserve is unused at rest — shifting the
        // column edge-ward there gives the pop-out more room without clipping.
        double edgeBias = GIcon * (HoverScale - 1.0) * 0.30;
        _colCenterCross = _slabCross + _slabCrossLen / 2.0 - edgeBias;

        double startPad = icon * 0.7;
        double endPad = icon * 0.7;
        double seam = _hasRunningArea ? icon * 0.55 : 0;

        // Keep clear of the edges and the taskbar. WorkArea already excludes a
        // docked taskbar, but an AUTO-HIDE taskbar leaves WorkArea at full size,
        // so reserve an explicit band at each end the dock never enters. The
        // taskbar runs along the bottom, i.e. ACROSS a vertical dock's main
        // (top→bottom) axis, so only a vertical dock needs the larger end
        // reserve; a horizontal dock's main axis runs left→right with no taskbar
        // in its path, so it uses symmetric reserves to stay truly centred
        // (an asymmetric band would shift the slab toward the smaller end).
        double startReserve = 12 * _uiScale;
        double endReserve = IsVertical ? 56 * _uiScale : 12 * _uiScale;
        double usableMain = mainExtent - startReserve - endReserve;

        // The dock shows the resident region (up to 14 icons) AND the running
        // strip (up to RunningMaxComplete + overflow tiles), with one uniform
        // cell size for both so the spacing is identical above and below the
        // divider. Size that shared cell from the comfortable default and, only
        // if the combined column would overflow the usable band, shrink it just
        // enough to fit every row (down to a snug floor so icons never overlap).
        int pinnedCount = _config.LeftDockApps.Count;
        int runSlots = _hasRunningArea ? CurrentRunSlots() : 0;
        int totalCells = pinnedCount + runSlots;
        double fixedChrome = startPad + endPad + (_hasRunningArea ? seam : 0);
        double availForCells = usableMain - fixedChrome;

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

        _slabMainLen = startPad + pinnedBlockH
               + (_hasRunningArea ? seam + runningBlockH : 0)
               + endPad;

        // Centre the VISIBLE ICON CLUSTER (not the slab box) on the usable band.
        // The seam gap between the pinned and running groups, combined with their
        // unequal icon counts, pulls the icons' centre of mass off the slab's
        // geometric centre — so centring the slab box alone leaves the icons
        // looking shifted toward the larger (usually pinned) group, i.e. slightly
        // toward the leading side on a top/bottom dock. Position the slab so the
        // icon centroid lands on the band centre instead, then clamp to the
        // reserved end margins so the dock never spills past them.
        int visibleCells = _pinnedVisible + runSlots;
        double centroidFromSlab = startPad
            + (visibleCells > 0 ? CellH * visibleCells / 2.0 : 0)
            + (_hasRunningArea && visibleCells > 0 ? seam * runSlots / (double)visibleCells : 0);
        _slabMain = (startReserve + usableMain / 2.0) - centroidFromSlab;
        if (_slabMain + _slabMainLen > mainExtent - endReserve)
            _slabMain = mainExtent - endReserve - _slabMainLen;
        if (_slabMain < startReserve)
            _slabMain = startReserve;

        _pinnedAreaMain = _slabMain + startPad;
        _runAreaMain = _pinnedAreaMain + pinnedBlockH + seam;

        // Divider line: the true midpoint between the LAST pinned icon's far edge
        // and the FIRST running tile's near edge, so the drawn seam matches where
        // the icons actually break.
        double lastPinnedEnd = _pinnedAreaMain + pinnedBlockH - (CellH - GIcon) / 2.0;
        double firstRunStart = _runAreaMain + (RunStep - GIcon) / 2.0;
        _seamMain = _pinnedVisible > 0
            ? (lastPinnedEnd + firstRunStart) / 2.0
            : _runAreaMain - seam / 2.0;

        // Clip rect for the pinned scroll layer. Kept INSIDE the glass slab at
        // the near end (so icons never spill past the glass while scrolling) yet
        // wide/long enough that a hovered icon's wave (1.7x zoom + pop-out toward
        // the interior) is not cut.
        double margin = icon * 0.9;
        double clipMainLo = _slabMain + icon * 0.12;
        double clipMainHi = _hasRunningArea
            ? _seamMain - icon * 0.05
            : _slabMain + _slabMainLen - icon * 0.12;
        // The hovered icon pops out toward the interior; extend the clip's
        // interior cross edge to cover its enlarged half-width plus the pop.
        double popInterior = GIcon / 2.0 * HoverScale + WavePop(HoverScale) + icon * 0.2;
        double clipCrossLo = _colCenterCross - GIcon / 2.0 - margin;
        double clipCrossHi = _colCenterCross + popInterior;
        _pinnedViewport = LogicalRect(
            clipMainLo,
            clipCrossLo,
            Math.Max(0, clipMainHi - clipMainLo),
            Math.Max(0, clipCrossHi - clipCrossLo));

        // The wave activates over the WHOLE dock body — pinned column AND running
        // strip — so dragging the cursor across the seam keeps one continuous wave
        // instead of two disjoint ones. Spans to the slab's far main end (which
        // already includes the running block) with the same cross band as above.
        double waveMainHi = _slabMain + _slabMainLen - icon * 0.12;
        _waveHitRect = LogicalRect(
            clipMainLo,
            clipCrossLo,
            Math.Max(0, waveMainHi - clipMainLo),
            Math.Max(0, clipCrossHi - clipCrossLo));
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
        _runScale.Clear();
        _runTrans.Clear();
        _runCenterMain.Clear();
        _runWaveCur = Array.Empty<double>();
        _runSignature = null;
        _debris.Clear();
        ClearRunPopups();
        PruneIconCache();

        double opacity = 1.0 - Math.Clamp(_config.Settings.PanelTransparency, 0.0, 1.0);
        // Corner radius matches the main dock's resident-region border
        // (main-icon * 0.42); EffectiveIconSize / LeftDockScale recovers the
        // main dock's icon size from this scaled-down tray.
        double trayRadius = EffectiveIconSize / LeftDockScale * 0.42;
        // The Saturn theme uses a black smoked-glass side dock; every other
        // theme keeps the clear "liquid glass" body.
        bool darkSlab = ThemeRegistry.Get(_config.Settings.Theme).IsSaturn;
        _darkDock = darkSlab;
        _waveBulge = null;
        if (darkSlab)
        {
            // Draw the black tray snug around the icon column, bleeding its
            // edge-side feather off-screen so the solid black sits flush against
            // the screen edge.
            double darkPad = GIcon * 0.55;
            double darkBleed = GIcon * 0.4;
            _bodyCross = _slabCross - darkBleed;
            _bodyCrossLen = (_colCenterCross - _bodyCross) + GIcon / 2.0 + darkPad;
            var r = LogicalRect(_slabMain, _bodyCross, _slabMainLen, _bodyCrossLen);

            // Slab + flame share ONE opacity group AND one feather: each is drawn
            // fully opaque with hard edges inside the group, then a single blur is
            // applied to the whole group and the panel transparency once. Feathering
            // the union (rather than each element separately) is what makes the
            // flame's edge softness identical to the dock's — they are literally the
            // same blurred silhouette — and fuses them into one black mass instead
            // of two stacked semi-transparent layers.
            double slabFeather = Math.Max(12.0, Math.Min(_slabMainLen, _bodyCrossLen) * 0.19);
            _flameFeather = slabFeather;
            var darkGroup = new Canvas
            {
                Opacity = opacity,
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.BlurEffect
                {
                    Radius = Math.Max(8.0, slabFeather),
                    KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                },
            };
            Panel.SetZIndex(darkGroup, -12);
            PanelCanvas.Children.Add(darkGroup);

            // Slab drawn opaque with hard edges (no per-slab feather mask) — the
            // group blur above feathers it uniformly with the flame.
            GlassChrome.DrawSlab(darkGroup, r.X, r.Y, r.Width, r.Height, trayRadius, 1.0, track: null, frosted: false, dark: true, featherMask: false);

            // Dynamic "black flame" tongue that licks up from the slab and rides
            // the magnification wave. Opaque, hard-edged, same black as the slab
            // rim; the group blur gives it the dock's exact edge feather.
            _bulgeOpacity = opacity;
            double maxBulge = WavePop(HoverScale) + GIcon * HoverScale / 2.0 + GIcon;
            double baseEdge = _bodyCross + _bodyCrossLen;
            double bulgeCrossHi = _colCenterCross + maxBulge;
            // Clip the flame so it can never spill past the dock's ROUNDED corners.
            // The lower band (the buried skirt) is clipped to the slab's exact rounded
            // rectangle; the upper band (the tongue) is clipped to an inset rectangle
            // so the tip stays clear of the corner radius. The union of the two is the
            // dock silhouette grown upward only in the middle.
            var slabRound = new RectangleGeometry(r, trayRadius, trayRadius);
            var upperRect = LogicalRect(
                _slabMain + trayRadius, baseEdge,
                Math.Max(0, _slabMainLen - 2.0 * trayRadius),
                Math.Max(0, bulgeCrossHi - baseEdge));
            var clipGeo = new GeometryGroup { FillRule = FillRule.Nonzero };
            clipGeo.Children.Add(slabRound);
            clipGeo.Children.Add(new RectangleGeometry(upperRect));

            _waveBulge = new System.Windows.Shapes.Path
            {
                // Pure black to match the slab body's rim colour; fully opaque and
                // hard-edged — feathering is done once by the group blur.
                Fill = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00)),
                Opacity = 1.0,
                IsHitTestVisible = false,
                Clip = clipGeo,
            };
            Panel.SetZIndex(_waveBulge, -10);
            darkGroup.Children.Add(_waveBulge);

            // Saturn's signature: scatter a belt of tiny asteroids/rubble along the
            // dock's interior edge so the dark slab reads as a ring of space debris.
            DrawDebrisBelt(baseEdge);

            // Faint starfield over the black slab, matching the main dock's planet
            // backdrop, so the side dock feels like the same patch of space.
            DrawDockStarfield(baseEdge);
        }
        else
        {
            // Hug the icon column with only a modest interior margin instead of
            // the full hover-reserve thickness, so the liquid-glass panel doesn't
            // leave a large empty block beside the icons.
            double glassPad = GIcon * 0.30;
            _bodyCross = _slabCross;
            _bodyCrossLen = (_colCenterCross - _bodyCross) + GIcon / 2.0 + glassPad;
            var r = LogicalRect(_slabMain, _bodyCross, _slabMainLen, _bodyCrossLen);
            GlassChrome.DrawSlab(PanelCanvas, r.X, r.Y, r.Width, r.Height, trayRadius, opacity, track: null, frosted: false, dark: false);
            // Give the clear-glass side dock a raised, chiselled edge so it reads
            // as a 3-D slab rather than a flat sheet.
            DrawGlassBevel(r.X, r.Y, r.Width, r.Height, trayRadius, opacity);
        }
        if (_hasRunningArea)
            DrawSeam(_seamMain, opacity);

        // Pinned column inside a clipped scrolling layer.
        _pinnedScroll = Math.Clamp(_pinnedScroll, 0, PinnedScrollMax);
        _scrollTransform = IsVertical ? new TranslateTransform(0, -_pinnedScroll) : new TranslateTransform(-_pinnedScroll, 0);
        _scrollLayer = new Canvas { RenderTransform = _scrollTransform };
        var clip = new Canvas { Clip = new RectangleGeometry(_pinnedViewport) };
        clip.Children.Add(_scrollLayer);
        PanelCanvas.Children.Add(clip);

        var apps = _config.LeftDockApps;
        for (int i = 0; i < apps.Count; i++)
        {
            double mainC = _pinnedAreaMain + i * CellH + CellH / 2.0;
            var slot = new Point(_colCenterCross, mainC);   // X = cross, Y = main
            _pinnedSlots.Add(slot);

            var icon = CreateIcon(apps[i], GIcon);
            PlaceLogical(icon, slot);
            _scrollLayer.Children.Add(icon);
            _pinnedIcons.Add(icon);
        }

        RefreshRunning();
    }

    /// <summary>Centres an icon at a window-local point (used while dragging).</summary>
    private void PlaceCentered(RadialIcon icon, Point center)
    {
        Canvas.SetLeft(icon, center.X - icon.IconSize / 2);
        Canvas.SetTop(icon, center.Y - icon.IconSize / 2);
    }

    /// <summary>Centres an icon at a logical (X = cross, Y = main) slot.</summary>
    private void PlaceLogical(RadialIcon icon, Point logical)
        => PlaceCentered(icon, ToLocal(logical.Y, logical.X));

    private RadialIcon CreateIcon(AppEntry entry, double size)
    {
        var bmp = IconExtractor.GetCached(entry.EffectiveIconSource, _iconCache);
        var icon = new RadialIcon(entry, bmp, size, AccentColor, LabelBrush, dropletHover: true, leftDockStyle: true);
        icon.ApplyDockEdge(_side);
        icon.ExternalMagnify = true;   // the dock drives a coordinated macOS-style wave
        icon.PreviewMouseLeftButtonDown += Icon_PreviewMouseLeftButtonDown;
        icon.PreviewMouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowPinnedIconMenu(icon);
        };
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

    private void DrawSeam(double seamMain, double opacity)
    {
        // The divider runs perpendicular to the main axis (across the body), at
        // main = seamMain. Compute its two window-local endpoints.
        Point pa = ToLocal(seamMain, _bodyCross + 10 * _uiScale);
        Point pb = ToLocal(seamMain, _bodyCross + _bodyCrossLen - 10 * _uiScale);

        bool isSaturn = ThemeRegistry.Get(_config.Settings.Theme).IsSaturn;

        // A soft cool glow plus a bright glassy highlight form the divider. The
        // old near-black groove line is omitted so the seam reads as a light
        // split with no dark edge.
        double glowThk   = isSaturn ? 2.2  : 4.0;
        byte   glowA     = isSaturn ? (byte)0x55 : (byte)0xB0;
        int    glowBlur  = isSaturn ? 4    : 5;
        double shineThk  = isSaturn ? 0.5  : 0.9;
        byte   shineA    = isSaturn ? (byte)0x80 : (byte)0xDD;

        var glow = new System.Windows.Shapes.Line
        {
            X1 = pa.X, X2 = pb.X, Y1 = pa.Y, Y2 = pb.Y,
            StrokeThickness = glowThk,
            Stroke = new SolidColorBrush(Color.FromArgb(glowA, 0xBF, 0xE0, 0xFF)),
            Opacity = opacity,
            IsHitTestVisible = false,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = glowBlur },
        };
        var shine = new System.Windows.Shapes.Line
        {
            X1 = pa.X, X2 = pb.X, Y1 = pa.Y, Y2 = pb.Y,
            StrokeThickness = shineThk,
            Stroke = new SolidColorBrush(Color.FromArgb(shineA, 0xEA, 0xF4, 0xFF)),
            Opacity = opacity,
            IsHitTestVisible = false,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        Panel.SetZIndex(glow, -5);
        Panel.SetZIndex(shine, -4);
        PanelCanvas.Children.Add(glow);
        PanelCanvas.Children.Add(shine);
    }

    /// <summary>Overlays a raised, chiselled bevel on the clear-glass side dock so
    /// its edge reads as a 3-D slab. A directional rim (bright along the top/left,
    /// shaded along the bottom/right) fakes a single top-left light source, an
    /// inner soft groove adds depth (ambient occlusion just inside the rim), and a
    /// thin bright top-left hairline crisps the lit corner. Glass theme only.</summary>
    private void DrawGlassBevel(double left, double top, double w, double h, double radius, double opacity)
    {
        if (w <= 1 || h <= 1)
            return;

        // Directional bevel rim: bright top-left highlight melting into a dark
        // bottom-right shade, which is what makes the slab look raised.
        var bevel = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(radius),
            Opacity = opacity,
            IsHitTestVisible = false,
            BorderThickness = new Thickness(1.8),
            BorderBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0.0),  // lit top-left
                    new GradientStop(Color.FromArgb(0x46, 0xFF, 0xFF, 0xFF), 0.34),
                    new GradientStop(Color.FromArgb(0x28, 0x0A, 0x14, 0x24), 0.6),
                    new GradientStop(Color.FromArgb(0x96, 0x04, 0x09, 0x12), 1.0),  // shaded bottom-right
                },
            },
        };
        Canvas.SetLeft(bevel, left);
        Canvas.SetTop(bevel, top);
        Panel.SetZIndex(bevel, -5);
        PanelCanvas.Children.Add(bevel);

        // Inner occlusion groove: a soft dark ring just inside the rim, biased to
        // the bottom-right, deepening the sense that the surface sits proud of the
        // desktop behind it.
        var groove = new Border
        {
            Width = w - 3,
            Height = h - 3,
            CornerRadius = new CornerRadius(Math.Max(0, radius - 1.5)),
            Opacity = opacity,
            IsHitTestVisible = false,
            BorderThickness = new Thickness(2.4),
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 3 },
            CacheMode = new System.Windows.Media.BitmapCache(),
            BorderBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x00, 0x05, 0x0A, 0x14), 0.0),
                    new GradientStop(Color.FromArgb(0x00, 0x05, 0x0A, 0x14), 0.45),
                    new GradientStop(Color.FromArgb(0x4C, 0x04, 0x08, 0x12), 1.0),
                },
            },
        };
        Canvas.SetLeft(groove, left + 1.5);
        Canvas.SetTop(groove, top + 1.5);
        Panel.SetZIndex(groove, -5);
        PanelCanvas.Children.Add(groove);
    }

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
                    catch { /* ignore */ }
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
                catch { /* ignore */ }
                AddLauncherHelpers(a.Path);
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
                catch { /* ignore */ }
                filtered.Add(ta);
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (!_shown)
                    return;
                ApplyPinnedRunning(snapshot, explorerTitles, runningAumids);
                ApplyRunning(filtered);
            });
        });
    }

    /// <summary>Lights up the flowing blue border on each pinned icon whose
    /// target program is currently running (mirrors the main dock).</summary>
    private void ApplyPinnedRunning(RunningAppTracker.RunningSnapshot snapshot, List<string> explorerTitles,
        System.Collections.Generic.HashSet<string> runningAumids)
    {
        foreach (var icon in _pinnedIcons)
        {
            icon.IsRunning = RunningAppTracker.IsEntryRunning(
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
            catch { /* fall through to no icon */ }
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
        string iconKey = pathless ? "win:" + app.Window : app.Path;
        if (!_runIconCache.TryGetValue(iconKey, out var bmp))
        {
            bmp = pathless
                ? WindowPreviewService.GetWindowIconImage(app.Window)
                : IconExtractor.GetIcon(app.Path);
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
        double dot = Math.Max(3.0, iconSize * 0.075);
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

    // ---- macOS-dock magnification wave (continuous + frame-smoothed) -------

    /// <summary>Drives the wave + hover label from the live window pointer
    /// position. Activation is tested against the STATIC pinned viewport rect —
    /// never against the per-icon hit areas — so the pop-out (which slides an
    /// icon out from under the cursor) cannot create an enter/leave feedback
    /// loop. That loop was the source of the hover flicker/jump.</summary>
    private void UpdateWaveFromPointer(Point p)
    {
        // While a launch bounce is playing, keep the wave fully quiescent — a
        // wave restart would call SetMagnify and cancel the bounce animation.
        if (_bounceHold)
            return;

        bool valid = !double.IsNaN(p.X) && !double.IsNaN(p.Y);
        // The wave activates over the whole dock body (pinned column + running
        // strip) so it stays continuous as the cursor crosses the seam.
        bool inDock = valid
            && (_pinnedIcons.Count > 0 || _runCenterMain.Count > 0)
            && _waveHitRect.Contains(p);
        if (inDock)
        {
            _waveCursorY = MainOf(p);
            EnsureWaveTicking();
        }
        else
        {
            _waveCursorY = double.NaN;   // the tick loop eases everything back to rest
        }

        // The pinned hover label is still gated on the pinned viewport only; the
        // running tiles drive their own label/preview from their fixed hit areas.
        bool inColumn = valid && _pinnedIcons.Count > 0 && _pinnedViewport.Contains(p);
        if (inColumn)
        {
            int idx = NearestVisibleIconIndex(MainOf(p));
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

    // Black-flame geometry (in cross-axis units): how far the tallest tongue
    // licks past the resting edge. The base roots at the slab's edge side so the
    // opaque pedestal spans the full slab thickness and fuses with the body.
    private double FlameMaxHeight => GIcon * 1.05;

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
            var (pdx, pdy) = PopOffset(WavePop(cur));
            _pinnedIcons[i].SetMagnify(cur, pdx, pdy);
            // Larger icons sit on top so the bulge overlaps its neighbours cleanly.
            Panel.SetZIndex(_pinnedIcons[i], cur > 1.001 ? 3000 + (int)(cur * 1000) : 0);
        }

        // Same wave, applied to the running strip below the seam, so a hovered
        // running icon magnifies — and its neighbours ripple — exactly like the
        // pinned column. The cursor coordinate is shared, so the bulge glides
        // seamlessly across the divider.
        int rn = _runCenterMain.Count;
        if (_runWaveCur.Length != rn)
        {
            var old = _runWaveCur;
            _runWaveCur = new double[rn];
            for (int j = 0; j < rn; j++)
                _runWaveCur[j] = j < old.Length ? old[j] : 1.0;
        }
        for (int j = 0; j < rn; j++)
        {
            double target = active ? WaveScaleAt(_waveCursorY, _runCenterMain[j]) : 1.0;
            double cur = _runWaveCur[j] + (target - _runWaveCur[j]) * k;
            _runWaveCur[j] = cur;
            maxDelta = Math.Max(maxDelta, Math.Abs(target - cur));
            var (pdx, pdy) = PopOffset(WavePop(cur));
            _runScale[j].ScaleX = _runScale[j].ScaleY = cur;
            _runTrans[j].X = pdx;
            _runTrans[j].Y = pdy;
            Panel.SetZIndex(_runTiles[j], cur > 1.001 ? 3000 + (int)(cur * 1000) : 60);
        }

        // Deform the black Saturn background so its interior edge follows the wave.
        maxDelta = Math.Max(maxDelta, UpdateDebrisWave(k));
        UpdateWaveBulge();

        // Once the wave has fully settled back to rest, stop the loop.
        if (!active && maxDelta < 0.0015)
        {
            for (int i = 0; i < n; i++)
            {
                _pinnedIcons[i].SetMagnify(1.0, 0.0, 0.0);
                Panel.SetZIndex(_pinnedIcons[i], 0);
            }
            for (int j = 0; j < rn; j++)
            {
                _runScale[j].ScaleX = _runScale[j].ScaleY = 1.0;
                _runTrans[j].X = _runTrans[j].Y = 0.0;
                _runWaveCur[j] = 1.0;
                Panel.SetZIndex(_runTiles[j], 60);
            }
            foreach (var d in _debris)
            {
                d.Cur = 0.0;
                d.Tr.X = 0.0;
                d.Tr.Y = 0.0;
            }
            UpdateWaveBulge();   // flatten the background bulge back to rest
            CompositionTarget.Rendering -= OnWaveTick;
            _waveTicking = false;
        }
    }

    /// <summary>
    /// Rebuilds the Saturn dark dock's "black flame": a single large tongue that
    /// rides the magnification wave (centred on the wave's weighted peak, sized by
    /// its intensity) rather than one spike per icon. The silhouette is a smooth,
    /// Catmull-Rom flame profile — wide bellied base tapering to a leaning,
    /// flickering tip — rooted deep in the solid slab so its base fuses with the
    /// background and dissolves at the tip via the fill gradient. Pinches to
    /// nothing at rest; no-op for every non-Saturn (clear-glass) theme.
    /// </summary>
    private void UpdateWaveBulge()
    {
        var path = _waveBulge;
        if (path == null || !_darkDock)
            return;

        int n = _pinnedIcons.Count;
        bool pinnedOk = n > 0 && _waveCur.Length == n;
        bool runOk = _runCenterMain.Count > 0 && _runWaveCur.Length == _runCenterMain.Count;
        if (!pinnedOk && !runOk && !_bounceFlameActive)
        {
            path.Data = null;
            return;
        }

        double denom = Math.Max(0.0001, HoverScale - 1.0);

        // Collapse the per-icon wave into a single flame: peak intensity and the
        // magnification-weighted centre, so one tongue glides between icons as the
        // cursor sweeps instead of several fixed spikes. The pinned column AND the
        // running strip feed the SAME accumulator, so the tongue flows continuously
        // across the seam exactly as the cursor crosses it.
        double peak = 0.0, wsum = 0.0, csum = 0.0;
        if (pinnedOk)
        {
            for (int i = 0; i < n; i++)
            {
                double a = Math.Clamp((_waveCur[i] - 1.0) / denom, 0.0, 1.0);
                if (a <= 0.0)
                    continue;
                double w = a * a;
                double main = _pinnedSlots[i].Y - _pinnedScroll;
                wsum += w;
                csum += w * main;
                if (a > peak)
                    peak = a;
            }
        }
        if (runOk)
        {
            for (int j = 0; j < _runCenterMain.Count; j++)
            {
                double a = Math.Clamp((_runWaveCur[j] - 1.0) / denom, 0.0, 1.0);
                if (a <= 0.0)
                    continue;
                double w = a * a;
                double main = _runCenterMain[j];   // running tiles are not scrolled
                wsum += w;
                csum += w * main;
                if (a > peak)
                    peak = a;
            }
        }

        if (peak < 0.05 || wsum <= 0.0)
        {
            // No wave, but a launch bounce may still want its own tongue.
            if (_bounceFlameActive && _bounceFlameAmp > 0.01)
            {
                double ba = Math.Clamp(_bounceFlameAmp, 0.0, 1.0);
                peak = ba;
                wsum = ba * ba;
                csum = wsum * _bounceFlameCenterMain;
            }
            else
            {
                path.Data = null;
                return;
            }
        }
        else if (_bounceFlameActive && _bounceFlameAmp > 0.01)
        {
            // Blend the hop tongue in alongside any residual wave.
            double ba = Math.Clamp(_bounceFlameAmp, 0.0, 1.0);
            double w = ba * ba;
            wsum += w;
            csum += w * _bounceFlameCenterMain;
            if (ba > peak)
                peak = ba;
        }

        double cm = csum / wsum;                       // flame centre (main axis)
        double baseEdge = _bodyCross + _bodyCrossLen;  // resting interior edge
        // Root just inside the solid slab core — below the interior feather band
        // (so the blurred base is buried in solid black and leaves no seam) but
        // NOT down at the screen edge (so the flame never spills past the dock).
        double rootC = Math.Max(_bodyCross, baseEdge - _flameFeather - GIcon * 0.55);
        double t = _waveLastTick.TotalSeconds;         // flicker clock

        double half = CellH * (2.05 + 1.85 * peak);   // wide footprint: long, gradual flanks
        double flick = Math.Sin(t * 8.5) * 0.5 + Math.Sin(t * 5.3 + 1.1) * 0.5;
        double H = Math.Pow(peak, 1.1) * FlameMaxHeight * 0.90 * (0.88 + 0.12 * flick);
        double lean = (0.18 * Math.Sin(t * 3.7) + 0.10 * Math.Sin(t * 6.1 + 0.7)) * H;

        // Sample the flame silhouette as a height profile over the footprint. The
        // envelope blends a sharp central peak with a wide bell, raised to a gamma so
        // the flanks ease down to zero with a flat tangent. Because the height never
        // dips below the resting edge, the flanks GRAZE the dock surface tangentially
        // (no transversal crossing, no visible corner) and merge into it. A small
        // surface ripple and a height-weighted lateral lean make the tip curl and
        // flicker organically; the buried base anchors the shape inside the slab.
        const int M = 30;
        // Smooth taper so the flame fades out before the dock's rounded main-axis
        // ends — it must never jut past the corner where there's no slab beneath it.
        double mainLo = _slabMain, mainHi = _slabMain + _slabMainLen;
        double endPad = GIcon * 0.45, endRamp = GIcon * 1.00;
        static double SS(double e) { e = Math.Clamp(e, 0.0, 1.0); return e * e * (3.0 - 2.0 * e); }
        var q = new Point[M + 1];   // logical (main, cross) silhouette points
        for (int k = 0; k <= M; k++)
        {
            double x = -1.0 + 2.0 * k / M;
            double b = 0.5 * (1.0 + Math.Cos(Math.PI * x));
            double env = Math.Pow(0.40 * b * b + 0.60 * b, 1.6);   // sharp peak; flanks ease flat into the dock
            double protUp = H * env * (1.0 + 0.05 * Math.Sin(3.5 * Math.PI * x + t * 5.0));
            double up = Math.Pow(Math.Clamp(protUp / Math.Max(1e-6, H), 0.0, 1.0), 1.3);
            double m = cm + x * half + lean * up;
            double edgeFac = SS((m - (mainLo + endPad)) / endRamp) * SS(((mainHi - endPad) - m) / endRamp);
            protUp *= edgeFac;
            q[k] = new Point(m, baseEdge + protUp);
        }

        // Build a closed figure: straight up the left base from the buried root,
        // a smooth Catmull-Rom curve over the silhouette, straight down the right
        // base, and an implicit straight base across the slab interior.
        Point CR1(int i) => new Point(
            q[i].X + (q[Math.Min(M, i + 1)].X - q[Math.Max(0, i - 1)].X) / 6.0,
            q[i].Y + (q[Math.Min(M, i + 1)].Y - q[Math.Max(0, i - 1)].Y) / 6.0);
        Point CR2(int i) => new Point(
            q[i + 1].X - (q[Math.Min(M, i + 2)].X - q[i].X) / 6.0,
            q[i + 1].Y - (q[Math.Min(M, i + 2)].Y - q[i].Y) / 6.0);

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(ToLocal(cm - half, rootC), true, true);
            ctx.LineTo(ToLocal(q[0].X, q[0].Y), false, false);
            for (int i = 0; i < M; i++)
                ctx.BezierTo(
                    ToLocal(CR1(i).X, CR1(i).Y),
                    ToLocal(CR2(i).X, CR2(i).Y),
                    ToLocal(q[i + 1].X, q[i + 1].Y),
                    false, true);
            ctx.LineTo(ToLocal(cm + half, rootC), false, false);
        }
        geo.Freeze();
        path.Data = geo;
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
            ic.SetMagnify(1.0, 0.0, 0.0);
            Panel.SetZIndex(ic, 0);
        }
        for (int j = 0; j < _runScale.Count; j++)
        {
            _runScale[j].ScaleX = _runScale[j].ScaleY = 1.0;
            _runTrans[j].X = _runTrans[j].Y = 0.0;
            if (j < _runTiles.Count)
                Panel.SetZIndex(_runTiles[j], 60);
        }
        for (int j = 0; j < _runWaveCur.Length; j++)
            _runWaveCur[j] = 1.0;
        if (_waveTicking)
        {
            CompositionTarget.Rendering -= OnWaveTick;
            _waveTicking = false;
        }
        _waveCur = Array.Empty<double>();
        if (_waveBulge != null)
            _waveBulge.Data = null;
    }

    private void ShowHoverLabel(RadialIcon ic, int idx)
    {
        double mainC = _pinnedSlots[idx].Y - _pinnedScroll;
        double crossExtent = GIcon / 2.0 * HoverScale + WavePop(HoverScale);
        ShowHoverLabelCore(ic.Entry.Name, mainC, crossExtent);
    }

    /// <summary>Shared hover-label renderer used by both the pinned icons and the
    /// running strip, so both use the same font, size and styling.</summary>
    private void ShowHoverLabelCore(string name, double mainCenter, double crossExtent)
    {
        if (_hoverLabel == null)
        {
            _hoverLabelText = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                Foreground = LabelBrush,
                TextAlignment = TextAlignment.Left,
                TextWrapping = TextWrapping.NoWrap,
                // A dark drop shadow gives the name depth (more "3D") AND a dark
                // halo so light text stays legible even over a white window behind
                // the translucent label.
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 5,
                    ShadowDepth = 1.4,
                    Direction = 315,
                    Opacity = 0.9,
                },
            };
            _hoverLabel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x05, 0x1A, 0x1A, 0x1A)),
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

        // Cross position of the label's near edge: just past the hover-enlarged
        // icon, toward the screen interior.
        double crossPos = _colCenterCross + crossExtent + 8 * _uiScale;
        double thickness = IsVertical ? WinW : WinH;
        double mainExtent = IsVertical ? WinH : WinW;

        // Auto-shrink the font so the WHOLE name fits without ellipsis. For
        // vertical docks the label grows along the cross axis toward the far
        // edge; for horizontal docks it grows along the main axis, centred on
        // the icon, so the budget is whichever side has less room.
        double maxFont = 10.5 * HoverScale;
        double minFont = 7.5 * HoverScale;
        double horizPad = 20 * _uiScale;                 // matches Border padding (10 + 10)
        double avail = IsVertical
            ? Math.Max(40 * _uiScale, thickness - crossPos - horizPad - 6 * _uiScale)
            : Math.Max(40 * _uiScale, 2 * Math.Min(mainCenter, mainExtent - mainCenter) - horizPad - 6 * _uiScale);
        _hoverLabelText.FontSize = FitFontSize(name, maxFont, minFont, avail);

        _hoverLabel.InvalidateMeasure();
        _hoverLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double lw = _hoverLabel.DesiredSize.Width;
        double lh = _hoverLabel.DesiredSize.Height;

        // Anchor per edge so the label hangs off the icon toward the interior,
        // centred on the icon along the main axis.
        double left, top;
        switch (_side)
        {
            case DockSide.Right:
                left = WinW - crossPos - lw;       // grows left (interior)
                top = mainCenter - lh / 2.0;
                break;
            case DockSide.Top:
                left = mainCenter - lw / 2.0;
                top = crossPos;                    // grows down (interior)
                break;
            case DockSide.Bottom:
                left = mainCenter - lw / 2.0;
                top = WinH - crossPos - lh;        // grows up (interior)
                break;
            case DockSide.Left:
            default:
                left = crossPos;                   // grows right (interior)
                top = mainCenter - lh / 2.0;
                break;
        }
        Canvas.SetLeft(_hoverLabel, left);
        Canvas.SetTop(_hoverLabel, top);

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
        if (idx < 0)
        {
            AfterSharedChange();
            return;
        }

        // Number of apps currently sitting in the resident region.
        int resident = Math.Min(DockSync.ResidentCount(_config), _config.Apps.Count);
        bool wasResident = idx < resident;
        _config.Apps.RemoveAt(idx);

        // Every left-dock icon lives in the resident region, so unpinning one
        // must shrink the resident count too. Otherwise the first non-resident
        // app is pulled up into the freed slot and auto-replenishes the side
        // dock — exactly what the user does NOT want. (Mirrors DeleteEntry.)
        if (wasResident)
            _config.Settings.Ring0Count = Math.Max(0, resident - 1);

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

    /// <summary>Re-reads the dock-position setting, re-anchors the window to the
    /// (possibly new) screen edge and rebuilds the layout. Called by the host
    /// after the settings window changes the dock position.</summary>
    public void RefreshLayout()
    {
        PositionAndSize();
        if (_realized)
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

        // Insertion index from the pointer's main-axis position, so the dropped
        // icon lands where the cursor is rather than always at the end. Use the
        // insertion-GAP index (nearest boundary between icons) so a drop on a
        // slot's lower half lands after it, not half a cell too early.
        var drop = e.GetPosition(PanelCanvas);
        double contentMain = MainOf(drop) + _pinnedScroll;
        int dropIdx = (int)Math.Round((contentMain - _pinnedAreaMain) / CellH);
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

    // ---- Launch -----------------------------------------------------------

    /// <summary>The (dx, dy) "hop" vector for a launch bounce: the icon leaps up
    /// off the dock surface and falls back. Vertical (Left/Right) and bottom docks
    /// jump up the screen; a top dock jumps down into the screen.</summary>
    private (double x, double y) BounceLift()
    {
        double amp = GIcon * 0.6;
        return _side == DockSide.Top ? (0.0, amp) : (0.0, -amp);
    }

    private void Launch(AppEntry entry, RadialIcon? icon = null)
    {
        if (icon != null)
        {
            // Play the macOS-style hop FIRST while the dock is held visible, then
            // launch + dismiss when it finishes. Launching first would bring the
            // target window to the foreground over the dock, hiding the bounce.
            ResetWave();   // settle the wave so it can't fight the bounce transform
            _bounceHold = true;
            Panel.SetZIndex(icon, 5000);   // hop above its neighbours
            var (lx, ly) = BounceLift();
            int idx = _pinnedIcons.IndexOf(icon);
            double centerMain = idx >= 0 && idx < _pinnedSlots.Count
                ? _pinnedSlots[idx].Y - _pinnedScroll
                : _colCenterCross;
            double maxLift = ly != 0 ? ly : lx;
            StartBounceFlame(icon.HopTransform, ly != 0, maxLift, centerMain);
            icon.PlayLaunchBounce(lx, ly, () =>
            {
                _bounceHold = false;
                Panel.SetZIndex(icon, 0);
                StopBounceFlame();
                AppLauncher.Launch(entry, null);
                SetEdgeShown(false);
            });
        }
        else
        {
            AppLauncher.Launch(entry, () => SetEdgeShown(false));
        }
    }

    /// <summary>Plays the launch bounce on a running-strip tile (a plain Grid
    /// whose inner visual carries the wave's scale + translate transforms), then
    /// runs <paramref name="onDone"/>. Falls back to running it immediately if the
    /// tile has no transform group.</summary>
    private void PlayRunTileBounce(FrameworkElement tileRoot, Action onDone)
    {
        ScaleTransform? scale = null;
        TranslateTransform? trans = null;
        if (tileRoot.Tag is FrameworkElement visual && visual.RenderTransform is TransformGroup tg)
        {
            foreach (var t in tg.Children)
            {
                if (t is ScaleTransform st) scale = st;
                else if (t is TranslateTransform tt) trans = tt;
            }
        }
        if (scale == null || trans == null)
        {
            onDone();
            return;
        }
        ResetWave();   // settle the wave so it can't fight the bounce transform
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        trans.BeginAnimation(TranslateTransform.XProperty, null);
        trans.BeginAnimation(TranslateTransform.YProperty, null);
        scale.ScaleX = scale.ScaleY = 1.0;
        trans.X = trans.Y = 0.0;

        Panel.SetZIndex(tileRoot, 5000);   // hop above its neighbours
        var (lx, ly) = BounceLift();
        int ridx = _runTiles.IndexOf(tileRoot);
        double centerMain = ridx >= 0 && ridx < _runCenterMain.Count
            ? _runCenterMain[ridx]
            : _colCenterCross;
        double maxLift = ly != 0 ? ly : lx;
        StartBounceFlame(trans, ly != 0, maxLift, centerMain);
        var tx = DockBounce.BuildTranslate(lx);
        var ty = DockBounce.BuildTranslate(ly);
        var sx = DockBounce.BuildScale();
        var sy = DockBounce.BuildScale();
        _bounceHold = true;
        sy.Completed += (_, _) =>
        {
            _bounceHold = false;
            Panel.SetZIndex(tileRoot, 60);
            StopBounceFlame();
            onDone();
        };
        trans.BeginAnimation(TranslateTransform.XProperty, tx);
        trans.BeginAnimation(TranslateTransform.YProperty, ty);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
    }

    /// <summary>Starts feeding the Saturn flame from a live launch-bounce hop:
    /// each frame it reads how far the bouncing item has jumped and drives the
    /// flame intensity to match, so the tongue leaps up with the icon. No-op for
    /// non-Saturn (glass) docks. Stopped via StopBounceFlame when the bounce ends.</summary>
    private void StartBounceFlame(TranslateTransform trans, bool axisY, double maxLift, double centerMain)
    {
        if (!_darkDock || _waveBulge == null)
            return;
        _bounceFlameTrans = trans;
        _bounceFlameAxisY = axisY;
        _bounceFlameMaxLift = Math.Max(0.0001, Math.Abs(maxLift));
        _bounceFlameCenterMain = centerMain;
        _bounceFlameAmp = 0.0;
        if (!_bounceFlameActive)
        {
            _bounceFlameActive = true;
            CompositionTarget.Rendering += OnBounceFlameTick;
        }
    }

    private void StopBounceFlame()
    {
        if (!_bounceFlameActive)
            return;
        _bounceFlameActive = false;
        _bounceFlameAmp = 0.0;
        _bounceFlameTrans = null;
        CompositionTarget.Rendering -= OnBounceFlameTick;
        UpdateWaveBulge();   // collapse the flame back to rest
    }

    private void OnBounceFlameTick(object? sender, EventArgs e)
    {
        var trans = _bounceFlameTrans;
        if (trans == null)
        {
            StopBounceFlame();
            return;
        }
        double cur = _bounceFlameAxisY ? trans.Y : trans.X;
        _bounceFlameAmp = Math.Clamp(Math.Abs(cur) / _bounceFlameMaxLift, 0.0, 1.0);
        UpdateWaveBulge();
    }

    /// <summary>Sprinkles a small, faint starfield across the Saturn dock's black
    /// slab (pinned column + running strip), a few of them slowly twinkling — the
    /// same look as the main dock's planet backdrop, so the side dock reads as the
    /// same patch of space. Purely decorative; rebuilt with the slab each Layout.</summary>
    private void DrawDockStarfield(double baseEdge)
    {
        if (_slabMainLen <= 0)
            return;
        double s = Math.Max(0.5, _uiScale);
        double mainLo = _slabMain + GIcon * 0.1;
        double mainHi = _slabMain + _slabMainLen - GIcon * 0.1;
        double mainSpan = mainHi - mainLo;
        double crossLo = _bodyCross + GIcon * 0.08;
        double crossSpan = Math.Max(1.0, baseEdge - crossLo);
        if (mainSpan <= 0)
            return;

        var layer = new Canvas { IsHitTestVisible = false };
        Panel.SetZIndex(layer, -9);   // on the black slab, beneath the rubble + icons
        PanelCanvas.Children.Add(layer);

        var rng = new Random(0x2B17F3);
        int count = Math.Max(12, (int)(mainSpan / (22.0 * s)));
        for (int i = 0; i < count; i++)
        {
            double main = mainLo + rng.NextDouble() * mainSpan;
            double cross = crossLo + rng.NextDouble() * crossSpan;
            var p = LogicalPoint(main, cross);
            double sz = (0.6 + 1.7 * rng.NextDouble()) * s;
            byte br = (byte)(50 + 140 * rng.NextDouble());
            var star = new System.Windows.Shapes.Ellipse
            {
                Width = sz,
                Height = sz,
                Fill = new SolidColorBrush(Color.FromArgb(br, 255, 255, 250)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(star, p.X - sz / 2.0);
            Canvas.SetTop(star, p.Y - sz / 2.0);
            layer.Children.Add(star);

            if (rng.NextDouble() > 0.62)   // a subset slowly twinkles
            {
                double full = br / 255.0;
                var tw = new DoubleAnimation(full * 0.28, full,
                    TimeSpan.FromSeconds(1.5 + 2.4 * rng.NextDouble()))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromSeconds(2.2 * rng.NextDouble()),
                };
                Timeline.SetDesiredFrameRate(tw, App.AmbientFrameRate);
                star.BeginAnimation(OpacityProperty, tw);
            }
        }
    }

    // ---- Saturn debris belt ----------------------------------------------

    /// <summary>Scatters a field of tiny irregular asteroids/rubble across the
    /// Saturn dock — densest as a belt along the interior edge, but also strewn
    /// through the dock body (centre) and down toward the screen-edge side — so the
    /// dark slab reads as a band of space debris, like a slice of Saturn's rings.
    /// Each rock registers a live transform so the magnification wave shoves it
    /// outward as the bulge sweeps past. Rebuilt with the slab each Layout.</summary>
    private void DrawDebrisBelt(double baseEdge)
    {
        _debris.Clear();
        if (_slabMainLen <= 0)
            return;
        double s = Math.Max(0.5, _uiScale);
        double mainLo = _slabMain + GIcon * 0.15;
        double mainHi = _slabMain + _slabMainLen - GIcon * 0.15;
        double span = mainHi - mainLo;
        if (span <= 0)
            return;

        double opacity = 1.0 - Math.Clamp(_config.Settings.PanelTransparency, 0.0, 1.0);
        var layer = new Canvas
        {
            IsHitTestVisible = false,
            Opacity = Math.Clamp(opacity, 0.0, 1.0),
            // A whisper of blur softens the rubble so it sits in the dock's haze
            // rather than reading as crisp UI shapes.
            Effect = new System.Windows.Media.Effects.BlurEffect
            {
                Radius = 0.7,
                KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
            },
        };
        Panel.SetZIndex(layer, -8);   // above slab + flame, below the icons
        PanelCanvas.Children.Add(layer);

        // Fixed seed → the belt is stable across rebuilds (no distracting reshuffle).
        var rng = new Random(0x9C34A1);
        double innerCross = _bodyCross + GIcon * 0.08;   // toward the screen edge
        double beltCross = baseEdge;                     // the dock's interior edge

        // Two populations: a dense rubble BELT straddling the interior edge, and a
        // sparser scattering of grains spread through the whole dock body so the
        // centre and the screen-edge side aren't bare.
        int beltCount = Math.Max(14, (int)(span / (9.0 * s)));
        int bodyCount = Math.Max(14, (int)(span / (9.0 * s)));

        for (int i = 0; i < beltCount; i++)
        {
            double main = mainLo + rng.NextDouble() * span;
            double g = (rng.NextDouble() + rng.NextDouble() + rng.NextDouble()) / 3.0 - 0.5;
            double cross = beltCross + g * GIcon * 1.05 - GIcon * 0.05;
            double r = (1.2 + rng.NextDouble() * rng.NextDouble() * 5.2) * s;
            double alpha = 0.16 + rng.NextDouble() * 0.44;
            AddDebrisRock(layer, main, cross, r, alpha, rng);
        }
        for (int i = 0; i < bodyCount; i++)
        {
            double main = mainLo + rng.NextDouble() * span;
            // Uniform across the body, from the screen-edge side out to the belt.
            double cross = innerCross + rng.NextDouble() * Math.Max(1.0, beltCross - innerCross);
            double r = (1.0 + rng.NextDouble() * rng.NextDouble() * 3.6) * s;
            double alpha = 0.12 + rng.NextDouble() * 0.34;
            AddDebrisRock(layer, main, cross, r, alpha, rng);
        }

        // Almost-imperceptible drift along the belt so it feels alive, like rubble
        // slowly orbiting in space.
        var drift = new TranslateTransform();
        layer.RenderTransform = drift;
        var prop = IsVertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        var anim = new DoubleAnimation(-1.6 * s, 1.6 * s, new Duration(TimeSpan.FromSeconds(16)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        drift.BeginAnimation(prop, anim);
    }

    /// <summary>Builds one rock at logical (<paramref name="main"/>,
    /// <paramref name="cross"/>), adds it to <paramref name="layer"/>, and registers
    /// it for wave displacement. Smaller/fainter grains get less parallax so the
    /// field has depth.</summary>
    private void AddDebrisRock(Canvas layer, double main, double cross, double r, double alpha, Random rng)
    {
        var rock = MakeRock(LogicalPoint(main, cross), r, rng, alpha);
        var tr = new TranslateTransform();
        rock.RenderTransform = tr;
        layer.Children.Add(rock);
        _debris.Add(new DebrisRock
        {
            Main = main,
            // Bigger, bolder rocks ride the bulge further (foreground parallax).
            Parallax = 0.35 + Math.Clamp(r / (7.0 * Math.Max(0.5, _uiScale)), 0.0, 1.0) * 0.65,
            Tr = tr,
        });
    }

    /// <summary>Eases every debris rock toward the cross-axis push implied by the
    /// magnification wave at its main coordinate, so the rubble field bulges out
    /// under the cursor and relaxes behind it. Returns the largest pending delta so
    /// the wave loop keeps ticking until the rubble has settled.</summary>
    private double UpdateDebrisWave(double k)
    {
        if (_debris.Count == 0)
            return 0.0;
        bool active = !double.IsNaN(_waveCursorY);
        double denom = Math.Max(0.0001, HoverScale - 1.0);
        double maxPush = GIcon * 0.5;
        double maxDelta = 0.0;
        foreach (var d in _debris)
        {
            double target = 0.0;
            if (active)
            {
                double a = Math.Clamp((WaveScaleAt(_waveCursorY, d.Main) - 1.0) / denom, 0.0, 1.0);
                target = a * maxPush * d.Parallax;
            }
            double cur = d.Cur + (target - d.Cur) * k;
            d.Cur = cur;
            maxDelta = Math.Max(maxDelta, Math.Abs(target - cur));
            var (dx, dy) = PopOffset(cur);
            d.Tr.X = dx;
            d.Tr.Y = dy;
        }
        return maxDelta;
    }

    /// <summary>Builds one irregular, faceted "rock": a jittered polygon shaded from
    /// a lit upper-left facet to a dark lower-right so it reads as a 3D pebble.</summary>
    private static System.Windows.Shapes.Path MakeRock(Point c, double r, Random rng, double alpha)
    {
        int verts = 6 + rng.Next(3);
        var fig = new PathFigure { IsClosed = true, IsFilled = true };
        double a0 = rng.NextDouble() * Math.PI * 2.0;
        for (int k = 0; k < verts; k++)
        {
            double ang = a0 + (Math.PI * 2.0) * k / verts + (rng.NextDouble() - 0.5) * 0.55;
            double rad = r * (0.58 + rng.NextDouble() * 0.42);
            var p = new Point(c.X + Math.Cos(ang) * rad, c.Y + Math.Sin(ang) * rad);
            if (k == 0)
                fig.StartPoint = p;
            else
                fig.Segments.Add(new LineSegment(p, true));
        }
        var geo = new PathGeometry();
        geo.Figures.Add(fig);

        byte b = (byte)(70 + rng.Next(60));   // mid-grey base value
        byte Lit(int add) => (byte)Math.Min(255, b + add);
        byte Dark(double mul) => (byte)Math.Max(0, b * mul);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.2, 0.1),
            EndPoint = new Point(0.85, 0.95),
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(Lit(58), Lit(54), Lit(50)), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(b, b, Lit(6)), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(Dark(0.32), Dark(0.32), Dark(0.4)), 1.0));
        brush.Freeze();
        return new System.Windows.Shapes.Path
        {
            Data = geo,
            Fill = brush,
            Opacity = Math.Clamp(alpha, 0.0, 1.0),
            IsHitTestVisible = false,
        };
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
