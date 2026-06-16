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
    private const int RunningMaxComplete = 10;     // at most 10 full running-app icons
    // Left dock icon scale relative to the main dock. Kept identical for every
    // theme so the side dock's icon size, cell pitch and gaps are consistent no
    // matter which theme is active (previously Saturn used a larger 0.60 scale,
    // which made its side-dock spacing differ from the glass theme's).
    // Sized so the rendered glyph (GIcon = EffectiveIconSize * GlassIconScale)
    // lands at exactly 0.70 * IconSize * _uiScale for both themes.
    private const double LeftDockScale = 0.70 / GlassIconScale;

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
    // Held true from the moment a launch is committed through to the dock being
    // fully hidden. While set, all hover interaction (wave magnify, hover label,
    // thumbnail preview) is suppressed so the only post-launch motion is the dock
    // dismiss fade — the icon must not re-magnify under a still-stationary cursor.
    private bool _dismissing;
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
    // Reusable per-frame scratch for the dark-dock flame silhouette so the wave
    // render loop allocates nothing each frame (no GC churn during a magnify
    // wave). The geometry is kept unfrozen and re-Open()ed in place every frame.
    private Point[]? _flameSilhouette;
    private StreamGeometry? _flameGeo;
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
    private TimeSpan _bounceFlameLastTick = TimeSpan.Zero; // render-clock throttle

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

    private Brush LabelBrush => new SolidColorBrush(ColorUtil.Parse(_config.Settings.FontColor, Colors.White));
    private Color AccentColor => ColorUtil.Parse(_config.Settings.AccentColor, Color.FromRgb(0x3D, 0x7E, 0xFF));

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

        // Refresh badges promptly when an app starts/stops flashing for attention.
        AttentionService.Changed += () =>
        {
            if (!_shown)
                return;
            Dispatcher.BeginInvoke(new Action(RefreshAttentionOnly));
        };
    }

    /// <summary>Lightweight badge-only refresh for the prompt flash event: resolves
    /// each pinned icon's windows to update its new-message badge without the heavy
    /// running-process snapshot, so the dot appears with minimal latency.</summary>
    private void RefreshAttentionOnly()
    {
        if (!_shown)
            return;
        var pinnedIcons = new List<RadialIcon>(_pinnedIcons);
        var flashing = AttentionService.SnapshotFlashing();
        System.Threading.Tasks.Task.Run(() =>
        {
            var attention = new Dictionary<RadialIcon, (bool flashing, int count)>();
            foreach (var icon in pinnedIcons)
            {
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
                foreach (var icon in pinnedIcons)
                    if (attention.TryGetValue(icon, out var a))
                        icon.SetAttention(a.flashing, a.count);
            });
        });
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
        if (want)
            _dismissing = false;   // any reason to stay/become visible ends a launch dismiss
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
            {
                PanelCanvas.Visibility = Visibility.Collapsed;
                _dismissing = false;
            }
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

}
