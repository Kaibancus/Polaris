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
    private const int Ring0Cap = 14;
    private const int Ring1Cap = 22;

    // Reference screen height (DIPs) the layout was tuned for. On taller
    // displays everything is scaled up proportionally so the panel does not
    // look tiny on large/high-resolution monitors. Never scaled below 1.0.
    private const double ReferenceScreenHeight = 1080.0;

    // Global resolution scale factor, recomputed from the primary screen size
    // each time the overlay is sized. 1.0 on a 1080p (DIP) display.
    private double _uiScale = 1.0;

    // Extra per-theme scale applied on top of _uiScale. The Saturn theme is
    // drawn larger overall than the grid themes; 1.0 for everything else.
    private const double SaturnEnlarge = 1.10;
    private double _themeScale = 1.0;

    // Extra scale applied ONLY to the Saturn rings (disk) and centre planet —
    // NOT to the icons or the side dock — so the planet/disk can be enlarged
    // without changing icon size. 1.0 for non-Saturn themes.
    private const double SaturnDiskEnlarge = 1.05;
    private double _diskScale = 1.0;

    // Ring radii scaled by the current resolution + theme factors (plus the
    // Saturn-only disk enlargement).
    private double InnerRadius => BaseInnerRadius * _uiScale * _themeScale * _diskScale;
    private double RingStep => BaseRingStep * _uiScale * _themeScale * _diskScale;

    // User-chosen icon diameter scaled by the resolution (and theme) factors, so
    // icons (and the grid/ring geometry derived from them) grow on larger
    // displays and with the Saturn enlargement.
    private double EffectiveIconSize => _config.Settings.IconSize * _uiScale * _themeScale;

    // Saturn's centre planet uses a fixed base diameter (independent of the
    // user's icon-size setting) so adjusting icon size never resizes the planet;
    // it still scales with screen resolution and the Saturn enlargement.
    private const double PlanetIconBase = 56.0;
    private double PlanetDiameter => PlanetIconBase * _uiScale * _themeScale * _diskScale * 2.5;

    // Outer-ring icons are drawn slightly larger than inner-ring icons.
    private const double OuterIconScale = 1.0;

    // Inner-ring (resident) icons are drawn 10% smaller than the base icon size,
    // so the dense inner ring reads as a tidy "pinned" cluster.
    private const double InnerIconScale = 0.80;

    // Liquid-glass (grid) icons are drawn larger than the base icon size so they
    // fill the roomy grid cells more comfortably.
    private const double GlassIconScale = 1.32;

    // The glass dock's bottom edge sits ABOVE the system taskbar: the frosted
    // slab ends at the top of the taskbar (plus a small gap) rather than
    // covering down past it. When the side dock is docked at the BOTTOM, the
    // whole main dock lifts further so it never overlaps the side dock band.
    private double GlassDockBottomMargin
    {
        get
        {
            double baseMargin = SystemTaskbarHeight + EffectiveIconSize * 0.12;
            double sideReserve = BottomDockReserve?.Invoke() ?? 0.0;
            return Math.Max(baseMargin, sideReserve);
        }
    }

    /// <summary>Height (DIP) of the system taskbar when it is docked at the
    /// bottom of the primary screen; 0 when the taskbar is on another edge or
    /// auto-hidden. Used to keep the dock's bottom icon row clear of it.</summary>
    private double SystemTaskbarHeight
    {
        get
        {
            // Prefer the work-area delta (accurate for a normally-docked bar) on
            // the ACTIVE monitor — its full bounds minus its work area gives the
            // bottom taskbar thickness on that monitor (0 when the taskbar lives
            // on another monitor/edge).
            double sh = MonitorLayout.ActiveBounds.Height;
            double h = MonitorLayout.ActiveBounds.Bottom - MonitorLayout.ActiveWorkArea.Bottom;
            if (h > 1.0)
                return h;

            // The work area equals the full screen when the taskbar is
            // auto-hidden, so query the taskbar window's real thickness instead.
            try
            {
                IntPtr tray = FindWindow("Shell_TrayWnd", null);
                if (tray != IntPtr.Zero && GetWindowRect(tray, out var r))
                {
                    double scale = TaskbarDpiScale();
                    double thickPx = r.Bottom - r.Top;       // bottom/top bar thickness
                    double widthPx = r.Right - r.Left;
                    // Only treat it as a bottom bar (wide and thin). A side bar
                    // would be tall and narrow — ignore it (no bottom reserve).
                    if (thickPx > 0 && thickPx < widthPx)
                    {
                        double thickDip = thickPx / scale;
                        if (thickDip > 8.0 && thickDip < sh * 0.5)
                            return thickDip;
                    }
                }
            }
            catch { /* fall through */ }
            return 0.0;
        }
    }

    /// <summary>Device-pixels-per-DIP for this window (1.0 until it has a
    /// presentation source).</summary>
    private double TaskbarDpiScale()
    {
        var src = PresentationSource.FromVisual(this);
        double m = src?.CompositionTarget?.TransformToDevice.M22 ?? 0.0;
        return m > 0.1 ? m : 1.0;
    }

    /// <summary>Glass reserved at the bottom of the slab: a little breathing room
    /// below the lowest icon row. The slab's bottom edge already rests above the
    /// system taskbar (see <see cref="GlassDockBottomMargin"/>), so the taskbar
    /// height is no longer added here.</summary>
    private double GlassBottomReserve =>
        EffectiveIconSize * 0.30;

    // Current vertical scroll of the grid (device-independent px). 0 = first
    // row aligned to the top of the visible grid block; positive scrolls the
    // upper rows up out of view to reveal lower rows. Clamped to GlassScrollMax.
    private double _glassScroll;

    // The glass grid icons live inside a dedicated scroll layer so the entire
    // grid can be scrolled by translating ONE transform (GPU-composited, no
    // per-icon layout) instead of repositioning every icon each frame. The
    // clip layer (parent) is fixed to the viewport; the scroll layer (child,
    // holding the icons) carries the vertical TranslateTransform.
    private Canvas? _glassScrollLayer;
    private TranslateTransform? _glassScrollTransform;

    /// <summary>Height (px) of one grid cell — the column pitch's vertical twin,
    /// also the per-row scroll step.</summary>
    private double GlassCellH => EffectiveIconSize * 2.35;

    /// <summary>Height of the glass dock <b>body</b> (the panel that frames the
    /// icon grid), fixed to the <see cref="LiquidGlassTheme.VisibleRows"/> visible
    /// rows so the slab keeps a constant footprint no matter how many rows the
    /// grid actually has (surplus rows scroll inside it).</summary>
    private double GlassDockBodyHeight
    {
        get
        {
            double icon = EffectiveIconSize;
            double padY = icon * 1.15;
            double gridHVis = (LiquidGlassTheme.VisibleRows - 1) * GlassCellH;
            return gridHVis + icon + padY * 2;
        }
    }

    /// <summary>Total glass dock height: body + the top clock band + the bottom
    /// reserve that keeps the lowest icon row above the system taskbar (the
    /// frosted slab itself reaches the screen bottom).</summary>
    private double GlassDockTotalHeight =>
        GlassDockBodyHeight + EffectiveIconSize * 0.55 + GlassBottomReserve;

    /// <summary>Y of the centre of the visible grid block, computed so the whole
    /// dock (clock band + 4-row body + taskbar strip) sits flush against the
    /// bottom screen edge. This is the layout centre passed to ComputeSlots and
    /// used by DrawGlassPanel / the taskbar row.</summary>
    private double GlassDockCenterY
    {
        get
        {
            double sh = Height > 0 ? Height : ActualHeight;
            double topInset = EffectiveIconSize * 0.55;
            // Slab bottom flush to the screen (minus margin); work back to the
            // body top, then to the visible-grid-block centre.
            double slabBottom = sh - GlassDockBottomMargin;
            double slabTop = slabBottom - GlassDockTotalHeight;
            double bodyTop = slabTop + topInset;
            return bodyTop + GlassDockBodyHeight / 2.0;
        }
    }

    /// <summary>Centre used to lay out the glass dock (panel + grid icons),
    /// docked to the bottom of the screen.</summary>
    private Point GlassDockCenter => new(_center.X, GlassDockCenterY);

    /// <summary>Horizontal nudge applied to the glass icon grid (and its clip).
    /// The grid is centred on the dock centre (0) so the icons, the resident
    /// region box and the main dock slab all share one vertical axis; the
    /// right-side scrollbar is offset far enough that hover-zoomed icons still
    /// clear it.</summary>
    private double GlassGridShiftX => 0.0;

    /// <summary>Centre for the glass icon GRID specifically — the dock centre
    /// nudged left by <see cref="GlassGridShiftX"/>.</summary>
    private Point GlassGridCenter => new(_center.X - GlassGridShiftX, GlassDockCenterY);

    /// <summary>Total number of grid rows the current app set occupies. The
    /// non-resident apps start on a fresh row beneath the resident block, so the
    /// total is the resident rows plus the rows needed for the remainder.</summary>
    private int GlassTotalRows
    {
        get
        {
            int n = _config.Apps.Count;
            if (n <= 0)
                return 1;
            int cols = LiquidGlassTheme.Columns;
            int resident = Math.Min(DockSync.ResidentCount(_config), n);
            int residentRows = (resident + cols - 1) / cols;
            int rest = n - resident;
            int restRows = (rest + cols - 1) / cols;
            return Math.Max(1, residentRows + restRows);
        }
    }

    /// <summary>Maximum scroll offset (px): the height of the rows that overflow
    /// the visible block. 0 when everything fits in the visible rows.</summary>
    private double GlassScrollMax =>
        Math.Max(0, (GlassTotalRows - LiquidGlassTheme.VisibleRows)) * GlassCellH;

    /// <summary>True when the grid has more rows than fit in the visible block,
    /// so the scrollbar / wheel scrolling becomes active.</summary>
    private bool GlassScrollable => GlassScrollMax > 0.5;

    /// <summary>The on-screen rectangle the icon grid is clipped to (so rows
    /// scrolled out of the visible block are hidden behind the glass).</summary>
    private Rect GlassGridViewport
    {
        get
        {
            double icon = EffectiveIconSize;
            double cellW = icon * 2.15;
            double gridW = (LiquidGlassTheme.Columns - 1) * cellW;
            // Vertical slack must stay below the row pitch (icon*2.35) so that
            // rows scrolled out of the visible block remain hidden above/below.
            double vMargin = icon * 1.4;
            // Horizontal slack is generous: it only widens the clip sideways (no
            // rows to hide there), and it must clear the resident-region border,
            // which extends out to ~gridW/2 + 1.23*icon from the dock centre.
            double hMargin = icon * 1.9;
            double cx = _center.X - GlassGridShiftX;   // grid is nudged left
            double left = cx - gridW / 2.0 - hMargin;
            double w = gridW + hMargin * 2.0;
            double visibleH = (LiquidGlassTheme.VisibleRows - 1) * GlassCellH;
            double top = GlassDockCenterY - visibleH / 2.0 - vMargin;
            double h = visibleH + vMargin * 2.0;
            return new Rect(left, top, w, h);
        }
    }

    /// <summary>The on-screen rectangle of the whole glass dock slab (in
    /// PanelCanvas coordinates), matching the geometry drawn by
    /// <c>DrawGlassPanel</c>. Used so an icon dragged outside the slab counts as
    /// a delete.</summary>
    private Rect GlassSlabRect
    {
        get
        {
            double icon = EffectiveIconSize;
            double cellW = icon * 2.15;
            double gridW = (LiquidGlassTheme.Columns - 1) * cellW;
            double padX = icon * 1.15;
            double topInset = icon * 0.55;
            double w = gridW + icon + padX * 2;
            double h = GlassDockBodyHeight;
            double left = _center.X - w / 2.0;
            double gridTop = GlassDockCenter.Y - h / 2.0;
            double top = gridTop - topInset;
            double totalH = h + topInset + GlassBottomReserve;
            return new Rect(left, top, w, totalH);
        }
    }

    /// <summary>Height of the running-taskbar strip carved out of the bottom of    /// the single continuous glass panel: one magnified tile plus equal padding
    /// above and below. Reserved on the dock slab even before the (async)
    /// taskbar enumeration so dock and taskbar are one uninterrupted glass.</summary>
    private double GlassTaskbarStripHeight
    {
        get
        {
            double tile = EffectiveIconSize * GlassIconScale;
            double rowVPad = tile * 0.42;
            return tile + rowVPad * 2;
        }
    }

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

    // Static ring-band layers (the foreshortened ellipse bands, rims and speckle).
    // Split out from the rotating feature layer so the many band strokes can be
    // BitmapCached into a single bitmap — composited once per present instead of
    // re-drawing hundreds of vector strokes every frame. The bands never rotate,
    // so the cache is never resampled (no softening). The rotating features
    // (shimmer/spoke/blob/moon) stay vector on the orbit layer so their small,
    // sharp highlights are not blurred by a rotated cache.
    private Canvas? _innerBandLayer;
    private Canvas? _outerBandLayer;
    private Canvas? _ringBandLayer;

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

    // Clock labels in the glass dock's top row (rebuilt with the panel). Updated
    // by a 1 s timer while the panel is shown.
    private TextBlock? _glassClockTime;
    private TextBlock? _glassClockDate;
    private readonly System.Windows.Threading.DispatcherTimer _clockTimer;

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

    // Prospective resident count used for the live glass reflow animation, so the
    // neighbours animate to the SAME layout the drop will actually commit (e.g.
    // dragging a resident icon out shrinks the resident block during the drag).
    // -2 = not tracking a glass drag.
    private int _dragProspectiveResident = -2;

    // Icon the pointer is currently hovering (for the "spread apart" effect).
    private RadialIcon? _hoverIcon;

    // True while the Saturn continuous-orbit profiling scene is pushed.
    private bool _profilingSaturn;

    /// <summary>
    /// When true the panel stays open (opened from the tray) so the user can
    /// drag desktop shortcuts onto it. Key-release will not hide it.
    /// </summary>
    public bool IsPinned { get; private set; }

    public event Action? RequestOpenSettings;

    /// <summary>Raised whenever the main panel hides (key-release, icon launch,
    /// click-away…). The host uses it to dismiss the left-edge dock together with
    /// the main dock so a launch from the main dock retracts both.</summary>
    public event Action? PanelDismissed;

    /// <summary>Set by the host: when a glass icon is dragged and dropped onto
    /// the left-edge dock, this is invoked with the screen-space drop point and
    /// the entry. Returns true when the entry was pinned to the left dock (the
    /// main-dock entry is then left in place).</summary>
    public Func<Point, AppEntry, bool>? DropToLeftDock;

    /// <summary>Set by the host: returns the height (DIP, measured up from the
    /// bottom screen edge) that the side dock occupies when it is docked at the
    /// BOTTOM, so the liquid-glass main dock can lift itself clear of it. Returns
    /// 0 when the side dock is on another edge. Used by
    /// <see cref="GlassDockBottomMargin"/>.</summary>
    public Func<double>? BottomDockReserve;

    /// <summary>Raised after the main dock mutates its app list (add / delete /
    /// reorder), so the host can re-mirror the resident region into the left
    /// dock.</summary>
    public Action? AppsChanged;

    /// <summary>Raised while a glass icon is being dragged (true on drag start,
    /// false when it ends), so the host can keep the left dock visible as a drop
    /// target for the whole gesture.</summary>
    public Action<bool>? GlassDragActiveChanged;

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
        _runningTimer.Tick += (_, _) => { RefreshRunningStates(); RefreshTaskbarApps(); };

        // Warm the thumbnail cache in the background (even while the panel is
        // hidden) so we always hold a recent frame to show if a window gets
        // minimized before the user hovers its icon.
        _previewWarmTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5),
        };
        _previewWarmTimer.Tick += (_, _) => WarmPreviewCache();
        _previewWarmTimer.Start();

        // Ticks once a second to keep the glass dock clock current while shown.
        _clockTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _clockTimer.Tick += (_, _) => UpdateGlassClock();
    }

    /// <summary>Updates the glass dock's clock labels to the current local time.</summary>
    private void UpdateGlassClock()
    {
        if (_glassClockTime == null && _glassClockDate == null)
            return;
        var now = DateTime.Now;
        if (_glassClockTime != null)
            _glassClockTime.Text = now.ToString("yyyy年M月d日 ddd  H:mm",
                System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
        if (_glassClockDate != null)
            _glassClockDate.Text = now.ToString("M月d日 ddd",
                System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
    }

    private void WarmPreviewCache()
    {
        // Snapshot the app paths on the UI thread, then capture off-thread.
        var apps = new List<(string Path, string? Arguments)>(_config.Apps.Count);
        foreach (var a in _config.Apps)
            if (!string.IsNullOrWhiteSpace(a.Path))
                apps.Add((a.Path, a.Arguments));
        if (apps.Count == 0)
            return;

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                WindowPreviewService.WarmCache(apps, WindowPreviewPopup.PreviewThumbWidth);
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
        //
        // Sizes/positions are taken from the ACTIVE monitor (the primary one by
        // default, or the monitor the cursor was on when "show on all monitors"
        // is enabled), so the dock summons on whichever screen the user invoked
        // it from.
        Rect mon = MonitorLayout.ActiveBounds;
        double sw = mon.Width;
        double sh = mon.Height;
        // The liquid-glass dock is a per-pixel-alpha layered window whose
        // per-frame CPU compositing cost grows with its PHYSICAL pixel area. On
        // a high-DPI / 4K monitor the Windows display-scale factor already
        // enlarges the dock physically, so ALSO scaling it up by the
        // tall-display _uiScale ramp multiplies the composited area (and the
        // per-frame cost) for no real visual gain — and reads as "too big".
        // Keep the glass dock pinned at the base scale so it stays compact and
        // smooth on large/high-DPI displays. Vector themes (Saturn / planet)
        // rasterise cheaply, so they still scale up to fill tall screens.
        bool glass = ThemeRegistry.Get(_config.Settings.Theme).ShowGlassPanel;
        _uiScale = glass ? 1.0 : Math.Clamp(sh / ReferenceScreenHeight, 1.0, 2.0);

        bool saturn = ThemeRegistry.Get(_config.Settings.Theme).IsSaturn;
        // Saturn renders larger overall; the liquid-glass dock renders at 0.9 so
        // the whole dock (panel, taskbar strip and icons all derive from
        // EffectiveIconSize) is 10% more compact.
        _themeScale = saturn ? SaturnEnlarge : 0.9;
        // Enlarge only the Saturn rings/planet (not the icons) by SaturnDiskEnlarge.
        _diskScale = saturn ? SaturnDiskEnlarge : 1.0;

        double icon = EffectiveIconSize;

        // Extra clearance kept around the content so an icon dragged off the dock
        // stays visible (instead of being clipped by the content-sized window
        // edge) for a comfortable distance before it is dropped to delete. Added
        // only sideways and upward: the glass dock pins its bottom to the screen
        // edge and Saturn grows symmetrically, so this never shifts the layout.
        //
        // The headroom is EMPTY space (no dock content), yet it enlarges the
        // layered window and so the per-frame composite cost. Keep it a fixed
        // DIP distance derived from the BASE icon size (NOT scaled up by _uiScale
        // on large monitors) so 4K windows stay smaller without shrinking the
        // dock or reducing the drag-to-delete clearance on a 1080p display.
        double dragHeadroom = _config.Settings.IconSize * _themeScale * 5.0;

        double halfW, halfH;
        if (saturn)
        {
            // Ring disc radius + an outer icon, plus room for the 1.7x hover zoom.
            double discR = InnerRadius + RingStep + icon * OuterIconScale;
            double reach = discR + icon * OuterIconScale;
            halfW = reach + dragHeadroom;
            halfH = reach + dragHeadroom;
        }
        else
        {
            // Liquid-glass grid panel extents (mirrors DrawGlassPanel); size to
            // the largest possible 6×5 grid so the window fits when expanded.
            double cellW = icon * 2.15;
            double cellH = icon * 2.35;
            double gridW = (LiquidGlassTheme.Columns - 1) * cellW;
            double gridH = (LiquidGlassTheme.MaxRows - 1) * cellH;
            double panelHalfW = (gridW + icon + icon * 1.15 * 2) / 2.0;
            double panelHalfH = (gridH + icon + icon * 1.15 * 2) / 2.0;
            // The settings gear sits above the grid; keep it inside.
            double gearUp = 2 * (icon * 2.1) + icon * 0.7 + icon * 0.6;
            halfW = panelHalfW + icon * 1.7 + dragHeadroom;
            halfH = Math.Max(panelHalfH, gearUp) + icon * 1.7 + dragHeadroom;
        }

        // Generous fixed margin on top of the computed reach (ample headroom),
        // then clamp to the screen so we never exceed it.
        double margin = 180.0 * _uiScale;
        double w = Math.Min((halfW + margin) * 2.0, sw);
        double h = Math.Min((halfH + margin) * 2.0, sh);

        // Anchor flag: the glass dock is bottom-docked, everything else centred
        // (glass was computed above to pick the per-theme scale cap).

        // The liquid-glass dock is bottom-docked and can scroll. Rather than
        // spanning the WHOLE screen (a fullscreen per-pixel-alpha layered window
        // is software-composited and re-uploaded every frame — the dominant
        // cost that capped every glass animation well under 60 FPS), size the
        // overlay to JUST the dock content plus headroom for the slab shadow,
        // the hover-zoom of the top icon row, and the rise-up summon slide.
        if (glass)
        {
            double cellW = icon * 2.15;
            double dockW = (LiquidGlassTheme.Columns - 1) * cellW + icon + icon * 1.15 * 2;
            double shadowPad = 72.0 * _uiScale;        // slab drop shadow (blur 48 + depth)
            double scrollPad = icon * 1.6;             // scrollbar parked right of the grid
            double hoverHeadroom = icon * 2.4;         // 1.7x zoom + label above the top row
            // dragHeadroom is added sideways (both edges) and upward only — the
            // window's bottom stays pinned to the screen edge so the dock keeps
            // its position while drag-out clearance grows above and beside it.
            w = Math.Min(dockW + shadowPad * 2 + scrollPad + dragHeadroom * 2, sw);
            h = Math.Min(GlassDockTotalHeight + GlassDockBottomMargin + hoverHeadroom + shadowPad + dragHeadroom, sh);
        }

        Width = w;
        Height = h;
        Left = mon.Left + (sw - w) / 2.0;
        // Glass: pin the window's bottom edge to the active monitor's bottom so
        // the dock (which lays out flush to Height - margin) docks to the real
        // bottom of that monitor.
        Top = glass ? mon.Bottom - h : mon.Top + (sh - h) / 2.0;
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
        RootGrid.Opacity = 0;
        Show();                         // one-time; the window then stays shown
        _suppressRebuild = false;
        Rebuild();
        _runningTimer.Stop();           // nothing to poll while hidden
        // Collapse the content while dismissed so WPF stops rendering (and thus
        // ticking) every continuous animation — orbits, running sweeps, twinkle
        // — that would otherwise burn GPU/CPU behind the invisible (Opacity 0)
        // layered window. ShowFaded makes it Visible again before each Rebuild.
        PanelCanvas.Visibility = Visibility.Collapsed;
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

        PanelCanvas.Visibility = Visibility.Visible;   // resume rendering/animation
        SizeToActiveContent();
        _suppressRebuild = false;
        Rebuild();                      // pick up any config changes
        // Reset the content opacity to 0 before fading in so a stale frame from
        // the previous summon can never flash.
        RootGrid.BeginAnimation(OpacityProperty, null);
        RootGrid.Opacity = 0;
        // The liquid-glass dock no longer frosts the desktop behind it — the
        // panel sits on the clear wallpaper. (Desktop-blur capture removed.)
        if (_theme.ShowGlassPanel)
            AnimateGlassRise();         // slide the dock up from the screen bottom
        else
        {
            PanelCanvas.RenderTransform = Transform.Identity;  // clear any glass rise
            AnimateRingsExpand();       // grow the rings out from the centre
            // The Saturn theme orbits + spins continuously while shown; mark it
            // the base profiling scene until the panel hides.
            Polaris.Services.FpsProfiler.Push("SaturnIdle");
            _profilingSaturn = true;
        }
        Topmost = true;
        Activate();
        _runningTimer.Start();
        UpdateGlassClock();
        _clockTimer.Start();

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        RootGrid.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Liquid-glass summon: slide the whole dock up from below the
    /// bottom screen edge into its docked position, with a soft ease-out.</summary>
    private void AnimateGlassRise()
    {
        // Start fully below the dock's resting bottom so it rises into view.
        double rise = GlassDockTotalHeight + GlassDockBottomMargin + EffectiveIconSize;
        // Anchor at the bottom-centre so the squash/stretch grows out of the
        // bottom edge — reads as a fluid blob settling rather than a rigid slide.
        var tt = new TranslateTransform(0, rise);
        var sc = new ScaleTransform(1.0, 0.94);   // vertically compressed, springs to full height
        var grp = new TransformGroup();
        grp.Children.Add(sc);
        grp.Children.Add(tt);
        PanelCanvas.RenderTransformOrigin = new Point(0.5, 1.0);
        PanelCanvas.RenderTransform = grp;

        // Gentle overshoot so the dock eases past its resting line and settles.
        var slideEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.18 };
        var slide = new DoubleAnimation(rise, 0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = slideEase,
        };
        // Vertical stretch springs slightly beyond full and relaxes back.
        var stretchEase = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 4 };
        var stretch = new DoubleAnimation(0.94, 1.0, TimeSpan.FromMilliseconds(360))
        {
            EasingFunction = stretchEase,
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(slide, App.AnimationFrameRate);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(stretch, App.AnimationFrameRate);
        // The squash/stretch scales the whole panel; without a cache WPF would
        // re-rasterise every blurred glass-chrome layer each frame (heavy ->
        // dropped frames). Cache the entire panel to one GPU texture for the
        // duration so the scale just stretches that texture, then drop the cache
        // when the motion settles so live content (clock, hover) renders crisply.
        var riseCache = new System.Windows.Media.BitmapCache { SnapsToDevicePixels = false };
        PanelCanvas.CacheMode = riseCache;
        Polaris.Services.FpsProfiler.Push("GlassRise");
        slide.Completed += (_, _) =>
        {
            PanelCanvas.CacheMode = null;
            Polaris.Services.FpsProfiler.Pop("GlassRise");
        };
        tt.BeginAnimation(TranslateTransform.YProperty, slide);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, stretch);
    }

    /// <summary>Animates the two ring layers growing out from the planet, the
    /// inner band leading and the outer band following, for a "summon" feel.</summary>
    private void AnimateRingsExpand()
    {
        ExpandLayer(_innerBandLayer, 0.0);
        ExpandLayer(_innerOrbitLayer, 0.0);
        ExpandLayer(_outerBandLayer, 0.11);
        ExpandLayer(_outerOrbitLayer, 0.11);
        // The expand burst runs ~110ms delay + 280ms grow; pop a bit after.
        Polaris.Services.FpsProfiler.Push("RingsExpand");
        var done = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(420),
        };
        done.Tick += (s, _) =>
        {
            ((System.Windows.Threading.DispatcherTimer)s!).Stop();
            Polaris.Services.FpsProfiler.Pop("RingsExpand");
        };
        done.Start();
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
        var grow = new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(280))
        {
            BeginTime = begin,
            EasingFunction = ease,
        };
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, grow.Clone());

        var fadeIn = new DoubleAnimation(0, 0.78, TimeSpan.FromMilliseconds(280))
        {
            BeginTime = begin,
            EasingFunction = ease,
        };
        layer.BeginAnimation(OpacityProperty, fadeIn);
    }

    public void HidePanel()
    {
        HidePanel(null);
    }

    /// <summary>Hides the panel, optionally invoking <paramref name="onFaded"/>
    /// once the fade-out animation has fully completed (used so the settings
    /// window only appears after the dock has finished disappearing).</summary>
    public void HidePanel(Action? onFaded)
    {
        _shown = false;
        IsPinned = false;
        CancelDrag();
        _runningTimer.Stop();
        _clockTimer.Stop();

        // Let the host retract the left-edge dock together with the main dock
        // (e.g. when an icon launch hides the panel).
        PanelDismissed?.Invoke();

        // End the Saturn continuous-orbit profiling scene if it was active.
        if (_profilingSaturn)
        {
            _profilingSaturn = false;
            Polaris.Services.FpsProfiler.Pop("SaturnIdle");
        }

        // Reset the glass grid scroll so the next summon starts at the top row.
        StopGlassScrollAnimation();
        _glassScroll = 0;

        // Dismiss any open window-preview popups so they don't linger on screen.
        foreach (var ic in _iconElements)
            ic.ClosePreview();

        // Fade the content out, then collapse it so the fully-transparent
        // layered window passes clicks straight through to the desktop (a
        // layered window's transparent pixels are not hit-testable). Capture the
        // current (animated) opacity BEFORE replacing the animation so we start
        // the fade from what's on screen.
        double from = RootGrid.Opacity;
        var fade = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(170));
        fade.Completed += (_, _) =>
        {
            // Only collapse if still hidden (a fast re-show may have intervened).
            if (!_shown)
                PanelCanvas.Visibility = Visibility.Collapsed;
            // Defer the callback to a LATER dispatcher pass (Background) instead
            // of running it inline on the fade's final frame. The callback may
            // build a heavy window (the settings UI), and doing that synchronously
            // here blocks the UI thread before the collapsed/transparent frame is
            // presented — which reads as the dock flashing/stuttering as it
            // disappears. Letting the collapse render first keeps the fade smooth.
            if (onFaded != null)
                Dispatcher.BeginInvoke(onFaded, System.Windows.Threading.DispatcherPriority.Background);
        };
        RootGrid.BeginAnimation(OpacityProperty, fade);
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
        // Keep the always-shown overlay out of Alt+Tab and the taskbar. The
        // window uses AllowsTransparency=True (a layered window): its transparent
        // pixels are automatically NOT hit-testable, so when the panel is hidden
        // (fully transparent) clicks pass straight through to the desktop with no
        // extra work.
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

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct TaskbarRect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out TaskbarRect lpRect);

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
        _glassHoverLabel = null;
        _glassHoverLabelText = null;
        // PanelCanvas was just cleared, so the taskbar tiles are gone from the
        // tree; drop our tracking so RefreshTaskbarApps rebuilds them.
        _taskbarIcons.Clear();
        _taskbarTiles.Clear();
        _taskbarSignature = null;
        // Clock labels lived in the cleared canvas too.
        _glassClockTime = null;
        _glassClockDate = null;
        // The scrollbar control lived in the cleared canvas as well.
        _glassScrollBar = null;
        // The scroll layer + its transform were children of the cleared canvas.
        _glassScrollLayer = null;
        _glassScrollTransform = null;

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
            // For the glass grid, tell the layout how many apps form the pinned
            // resident block so the rest begin on a fresh row beneath it (keeps
            // the framed region and the left dock showing exactly the same set).
            if (_theme.ShowGlassPanel)
                scaled.Ring0Count = DockSync.ResidentCount(_config);
            // Only the glass dock is lifted (it has a taskbar row beneath it);
            // plain grid themes stay centred. The glass grid is also nudged
            // slightly left (GlassGridCenter) so it clears the right scrollbar.
            Point gridCenter = _theme.ShowGlassPanel ? GlassGridCenter : _center;
            _slotPositions.AddRange(_theme.ComputeSlots(
                _config.Apps.Count, gridCenter, scaled, out _outerRadius));
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
            Canvas MakeRingLayer(bool cached) => new Canvas
            {
                Width = pw,
                Height = ph,
                IsHitTestVisible = false,
                Opacity = 0.78,                   // match the planet's translucency
                CacheMode = cached ? new System.Windows.Media.BitmapCache() : null,
            };
            // Static band layers are BitmapCached (hundreds of strokes -> one
            // bitmap, composited once per present); the rotating feature layers
            // stay vector so the moving highlights remain crisp.
            _innerBandLayer = MakeRingLayer(cached: true);
            _outerBandLayer = MakeRingLayer(cached: true);
            _innerOrbitLayer = MakeRingLayer(cached: false);
            _outerOrbitLayer = MakeRingLayer(cached: false);
            // z order: each group's static band sits just below its moving
            // features; the inner group sits below the outer group.
            PanelCanvas.Children.Add(_innerBandLayer);
            PanelCanvas.Children.Add(_innerOrbitLayer);
            PanelCanvas.Children.Add(_outerBandLayer);
            PanelCanvas.Children.Add(_outerOrbitLayer);

            DrawBackingDisc();
        }
        else
        {
            _innerOrbitLayer = null;
            _outerOrbitLayer = null;
            _innerBandLayer = null;
            _outerBandLayer = null;
            if (_theme.ShowGlassPanel)
            {
                DrawGlassPanel();
            }
        }

        int r0 = _theme.IsSaturn ? EffectiveRing0Count(_config.Apps.Count) : int.MaxValue;
        // Clamp the scroll offset to the current content height (the app count
        // may have shrunk since the last scroll).
        if (_theme.ShowGlassPanel)
        {
            _glassScroll = Math.Clamp(_glassScroll, 0, GlassScrollMax);
            // Build the scroll container: a clip layer fixed to the viewport
            // holding a scroll layer that carries the vertical translate. Icons
            // go inside the scroll layer at their TRUE (un-scrolled) positions,
            // so scrolling is a single transform update — smooth and cheap.
            _glassScrollTransform = new TranslateTransform(0, -_glassScroll);
            _glassScrollLayer = new Canvas { RenderTransform = _glassScrollTransform };
            var clipLayer = new Canvas { Clip = new RectangleGeometry(GlassGridViewport) };
            clipLayer.Children.Add(_glassScrollLayer);
            PanelCanvas.Children.Add(clipLayer);
            // Frame the resident region (top two rows) so it reads as a distinct
            // "always-pinned" zone that mirrors the left dock. Drawn inside the
            // scroll layer (behind the icons) so it scrolls with the grid.
            DrawResidentRegionBorder(_glassScrollLayer);
        }
        for (int i = 0; i < _config.Apps.Count && i < _slotPositions.Count; i++)
        {
            var entry = _config.Apps[i];
            double size = _theme.IsSaturn
                ? (i >= r0 ? EffectiveIconSize * OuterIconScale
                           : EffectiveIconSize * InnerIconScale)
                : _theme.ShowGlassPanel
                    ? EffectiveIconSize * GlassIconScale
                    : EffectiveIconSize;
            var icon = CreateIcon(entry, size);
            if (_theme.ShowGlassPanel)
            {
                // True position inside the scroll layer; the layer's transform
                // applies the scroll and the clip layer hides off-screen rows.
                PlaceCentered(icon, _slotPositions[i]);
                _glassScrollLayer!.Children.Add(icon);
            }
            else
            {
                PlaceCentered(icon, _slotPositions[i]);
                PanelCanvas.Children.Add(icon);
            }
            _iconElements.Add(icon);
        }

        if (_theme.ShowGlassPanel && GlassScrollable)
            DrawGlassScrollBar();

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
        RefreshTaskbarApps();
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
            List<string> explorerTitles;
            try { explorerTitles = WindowPreviewService.GetExplorerWindowTitles(); }
            catch { explorerTitles = new List<string>(); }
            System.Collections.Generic.HashSet<string> runningAumids;
            try { runningAumids = WindowPreviewService.SnapshotRunningAumids(); }
            catch { runningAumids = new System.Collections.Generic.HashSet<string>(); }
            Dispatcher.BeginInvoke(() =>
            {
                foreach (var icon in icons)
                {
                    icon.IsRunning = RunningAppTracker.IsEntryRunning(
                        icon.Entry, running, explorerTitles, runningAumids);
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
        // Slow perpetual revolution: tick at the display's NATIVE rate, not the
        // 2x-oversampled rate used for short interactive transitions. The ring
        // layer re-rasterises its (many) vector features every tick, so on a 60 Hz
        // panel this halves the idle revolve cost (120 -> 60). At the tiny angular
        // step per frame of this slow turn, the un-oversampled beat against
        // 59.94 Hz is imperceptible, so the motion looks unchanged.
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(anim, App.AmbientFrameRate);
        rt.BeginAnimation(RotateTransform.AngleProperty, anim);
    }

    private RadialIcon CreateIcon(AppEntry entry, double iconSize)
    {
        var bmp = IconExtractor.GetCached(entry.EffectiveIconSource, _iconCache);
        // Saturn icons bloom with a very faint, high-transparency black halo
        // (so it reads as a soft shadow on the dark disc) instead of the accent
        // blue used by the glass theme.
        Color glow = _theme.IsSaturn ? Color.FromArgb(0x30, 0x00, 0x00, 0x00) : AccentColor;
        var icon = new RadialIcon(entry, bmp, iconSize, glow, LabelBrush, _theme.ShowGlassPanel);
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

        // Insert the dropped app(s) at the grid slot nearest the pointer, so the
        // icon lands where it was dropped (on the side of the cursor) rather than
        // always at the end.
        Point drop = e.GetPosition(PanelCanvas);
        int insertIdx = _theme.SupportsGridReorder
            ? ComputeGridInsertIndex(GlassToContent(drop))
            : _config.Apps.Count;

        // Saturn: place the dropped icon on the ring the cursor is over. A drop on
        // the inner ring inserts into the resident region (the first Ring0Count
        // apps) and grows that count, so an icon can be added straight to the
        // inner ring while it still has room (rather than always appending to the
        // outer ring).
        bool intoInnerRing = false;
        if (_theme.IsSaturn)
        {
            int r0 = EffectiveRing0Count(_config.Apps.Count);
            var (ring, pos) = ComputeDragTarget(drop, -1);
            if (ring == 0)
            {
                intoInnerRing = true;
                insertIdx = Math.Clamp(pos, 0, r0);
            }
            else
            {
                insertIdx = Math.Clamp(r0 + pos, r0, _config.Apps.Count);
            }
        }

        // A glass drop inside the framed resident rows should add the icon as a
        // resident, growing the resident count (up to the cap) per icon added.
        bool intoResident = false;
        if (_theme.ShowGlassPanel)
        {
            int cols = LiquidGlassTheme.Columns;
            int resident = DockSync.ResidentCount(_config);
            int residentRows = Math.Max(1, (resident + cols - 1) / cols);
            int dropRow = GlassRowAt(GlassToContent(drop));
            intoResident = dropRow >= 0 && dropRow < residentRows;
        }

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
            insertIdx = Math.Clamp(insertIdx, 0, _config.Apps.Count);
            _config.Apps.Insert(insertIdx, entry);
            insertIdx++;
            if (intoResident && DockSync.ResidentCount(_config) < DockSync.MaxResidentCount)
                _config.Settings.Ring0Count = DockSync.ResidentCount(_config) + 1;
            else if (intoInnerRing && _config.Settings.Ring0Count > 0)
                // Grow the explicit inner-ring count to include the icon just
                // inserted into it. In auto mode (Ring0Count == 0) the inner ring
                // already fills first, so the icon lands there without a bump.
                _config.Settings.Ring0Count = Math.Min(Ring0Cap, _config.Settings.Ring0Count + 1);
            added = true;
        }

        if (added)
        {
            _persist();
            Rebuild();
            AppsChanged?.Invoke();
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
        _dragProspectiveResident = -2;
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
            // The floating hover label was shown while hovering this icon; hide it
            // so the name doesn't linger in place once the icon starts moving.
            if (_theme.ShowGlassPanel)
                HideGlassHoverLabel();
            // For the glass grid, lift the dragged icon out of the clipped scroll
            // layer into the top-level canvas so it can be dragged anywhere
            // (including up into the delete zone) without being clipped, and so
            // its position is in plain PanelCanvas coordinates.
            if (_theme.ShowGlassPanel && _glassScrollLayer != null &&
                ReferenceEquals(_pressedIcon.Parent, _glassScrollLayer))
            {
                _glassScrollLayer.Children.Remove(_pressedIcon);
                PanelCanvas.Children.Add(_pressedIcon);
                Panel.SetZIndex(_pressedIcon, 1000);
            }

            // Keep the left dock summoned for the whole drag so it is a clear,
            // forgiving drop target.
            if (_theme.ShowGlassPanel)
                GlassDragActiveChanged?.Invoke(true);
        }

        PlaceCentered(_pressedIcon, p);

        bool deleteZone = IsDeleteDrop(p);
        _pressedIcon.Opacity = deleteZone ? 0.4 : 1.0;

        // Push other icons aside to reveal the slot the dragged icon is over.
        // Skip while the icon is dragged out into the delete zone.
        // Only the Saturn ring layout supports live reorder; the grid test theme
        // keeps drag-to-launch and drag-out-to-delete but no live reflow.
        if (_theme.IsSaturn && !deleteZone)
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
        else if (_theme.SupportsGridReorder && !deleteZone)
        {
            // Free-grid reorder: find the slot the cursor is over and make room
            // by shifting the other icons into the insertion arrangement. The
            // grid may be scrolled, so compare against scroll-corrected slots.
            int src = _iconElements.IndexOf(_pressedIcon);
            Point content = GlassToContent(p);
            int tgt = ComputeGridTarget(content, src);
            // For the glass dock, the resident block may grow/shrink as the icon
            // crosses the framed-rows boundary; reflow to that prospective layout
            // so the animation matches what the drop will actually commit.
            int prosp = _theme.ShowGlassPanel ? ProspectiveResidentCount(src, content) : -1;
            if (tgt != _dragTargetPos || prosp != _dragProspectiveResident)
            {
                _dragTargetPos = tgt;
                _dragProspectiveResident = prosp;
                ReflowGrid(src, tgt, prosp);
            }
        }
        else if (_dragTargetRing != -1 || _dragTargetPos != -1)
        {
            // Dragged into the delete zone — snap the others back to their slots.
            _dragTargetRing = -1;
            _dragTargetPos = -1;
            _dragProspectiveResident = -2;
            RestoreSlots();
        }
    }

    /// <summary>Maps a canvas point to the grid's un-scrolled "content" space by
    /// adding back the current scroll offset, so hit-tests against the static
    /// <see cref="_slotPositions"/> are correct while the grid is scrolled.</summary>
    private Point GlassToContent(Point p) =>
        _theme.ShowGlassPanel ? new Point(p.X, p.Y + _glassScroll) : p;

    /// <summary>0-based grid row of the glass cell at content point
    /// <paramref name="contentP"/> (row 0 is the first visible row). Used to tell
    /// whether a drop lands inside the framed resident rows.</summary>
    private int GlassRowAt(Point contentP)
    {
        double cellH = GlassCellH;
        double y0 = GlassDockCenterY - (LiquidGlassTheme.VisibleRows - 1) * cellH / 2.0;
        return (int)Math.Round((contentP.Y - y0) / cellH);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        OnGlassMouseWheel(this, e);
    }

    // ---- Free grid reorder (liquid-glass theme) --------------------------

    /// <summary>
    /// Returns the insertion slot index (0..n-1) the dragged icon
    /// <paramref name="src"/> is currently over. Uses a reading-order (row first,
    /// then column) hit test rather than a raw nearest-centre distance: the
    /// cursor's row is decided by which row band (one cell pitch tall, centred on
    /// the row) its Y falls in, so vertical moves switch rows reliably even
    /// though the cells are taller than wide and the resident block may break the
    /// grid onto a fresh row. The result is adjusted for the dragged icon being
    /// pulled out of the sequence.
    /// </summary>
    private int ComputeGridTarget(Point p, int src)
    {
        int n = _slotPositions.Count;
        if (n == 0)
            return 0;

        double half = GlassCellH / 2.0;
        int before = 0;
        bool srcBefore = false;
        for (int i = 0; i < n; i++)
        {
            Point s = _slotPositions[i];
            double dy = p.Y - s.Y;
            bool isBefore = dy > half          // slot sits on an earlier row
                ? true
                : dy < -half                   // slot sits on a later row
                    ? false
                    : p.X > s.X;               // same row band: left of the cursor
            if (isBefore)
            {
                before++;
                if (i == src)
                    srcBefore = true;
            }
        }

        int tgt = before;
        if (src >= 0 && src < n && srcBefore)
            tgt--;                             // the dragged icon was counted; it is removed first
        return Math.Clamp(tgt, 0, n - 1);
    }

    /// <summary>Returns the insertion index for a NEW icon dropped at content
    /// point <paramref name="p"/> using the same reading-order (row first, then
    /// column) hit test as the live reorder, so a dropped icon lands at the grid
    /// cell the pointer is actually over in both axes.</summary>
    private int ComputeGridInsertIndex(Point p)
    {
        int count = Math.Min(_slotPositions.Count, _config.Apps.Count);
        if (count == 0)
            return 0;

        // Reading-order insertion index: count the slots that come before the
        // cursor (earlier rows in full, plus same-row slots to the left). Row
        // bands are one cell pitch tall so vertical placement is precise.
        double half = GlassCellH / 2.0;
        int before = 0;
        for (int i = 0; i < count; i++)
        {
            Point s = _slotPositions[i];
            double dy = p.Y - s.Y;
            bool isBefore = dy > half
                ? true
                : dy < -half
                    ? false
                    : p.X > s.X;
            if (isBefore)
                before++;
        }
        return Math.Clamp(before, 0, _config.Apps.Count);
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
    /// grid arrangement, producing the neighbour "push aside" effect.
    /// <paramref name="prospectiveResident"/> is the resident count the drop will
    /// commit (glass dock only; -1 to use the current layout), so the neighbours
    /// reflow to the layout that the drop actually produces rather than letting a
    /// non-resident icon visually slide into a shrinking resident block.</summary>
    private void ReflowGrid(int src, int tgt, int prospectiveResident = -1)
    {
        int[] slotOfEntry = GridArrangement(src, tgt);
        // When the resident block is changing size mid-drag, animate against the
        // prospective slot layout instead of the (stale) current one.
        IReadOnlyList<Point> slots =
            (prospectiveResident >= 0 && _theme.ShowGlassPanel)
                ? ComputeGlassSlots(prospectiveResident)
                : _slotPositions;
        for (int i = 0; i < _iconElements.Count; i++)
        {
            if (_iconElements[i] == _pressedIcon)
                continue;
            int slot = slotOfEntry[i];
            if (slot >= 0 && slot < slots.Count)
            {
                // Icons live in the scroll layer at true positions, so reflow to
                // the plain slot centre — the layer transform + clip handle the
                // scroll and viewport masking automatically.
                AnimateTo(_iconElements[i], slots[slot]);
            }
        }
    }

    /// <summary>Computes the glass-dock slot centres for a hypothetical resident
    /// count, used to animate the live reorder to the prospective drop layout.</summary>
    private IReadOnlyList<Point> ComputeGlassSlots(int residentCount)
    {
        var scaled = new AppSettings
        {
            IconSize = EffectiveIconSize,
            Ring0Count = Math.Clamp(residentCount, 0, _config.Apps.Count),
        };
        return _theme.ComputeSlots(_config.Apps.Count, GlassGridCenter, scaled, out _);
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

        int tgt = targetPos >= 0 ? targetPos : ComputeGridTarget(GlassToContent(dropPoint), src);
        int[] slotOfEntry = GridArrangement(src, tgt);
        int n = _config.Apps.Count;

        var ordered = new AppEntry[n];
        for (int i = 0; i < n; i++)
            ordered[slotOfEntry[i]] = _config.Apps[i];

        _config.Apps.Clear();
        foreach (var a in ordered)
            _config.Apps.Add(a);

        // Glass theme: dropping an icon into the framed resident rows promotes it
        // to a resident (growing the count up to the cap) and dropping a resident
        // icon out of those rows demotes it, so the resident region tracks what
        // the user drags in/out instead of staying fixed.
        if (_theme.ShowGlassPanel)
            UpdateResidentCountForDrop(src, dropPoint);

        _persist();
        Rebuild();
        AppsChanged?.Invoke();
    }

    /// <summary>Adjusts <see cref="AppSettings.Ring0Count"/> after a glass-grid
    /// drop so the resident region follows the icon dragged in or out of the
    /// framed rows. <paramref name="srcBefore"/> is the dragged entry's index
    /// before the move (-1 for a brand-new icon).</summary>
    private void UpdateResidentCountForDrop(int srcBefore, Point dropPoint)
    {
        int resident = DockSync.ResidentCount(_config);
        int newResident = ProspectiveResidentCount(srcBefore, dropPoint);
        if (newResident != resident)
            _config.Settings.Ring0Count = newResident;
    }

    /// <summary>Computes the resident count a drop would commit: dragging an icon
    /// into the framed rows promotes it (+1), dragging a resident icon out of
    /// them demotes it (-1). <paramref name="srcBefore"/> is the dragged entry's
    /// index before the move (-1 for a brand-new icon).</summary>
    private int ProspectiveResidentCount(int srcBefore, Point dropPoint)
    {
        int cols = LiquidGlassTheme.Columns;
        int resident = DockSync.ResidentCount(_config);
        int residentRows = Math.Max(1, (resident + cols - 1) / cols);
        int dropRow = GlassRowAt(GlassToContent(dropPoint));
        bool inResident = dropRow >= 0 && dropRow < residentRows;
        bool wasResident = srcBefore >= 0 && srcBefore < resident;

        if (inResident && !wasResident && resident < DockSync.MaxResidentCount)
            return resident + 1;                 // promoted into the resident rows
        if (!inResident && wasResident && resident > 1)
            return resident - 1;                 // demoted out of the resident rows
        return resident;
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
    /// Number of icons on the inner ring for <paramref name="n"/> total icons.
    /// The Saturn inner ring is the resident-app region (the first apps, which
    /// are also mirrored into the left side dock). Its size is user-customizable
    /// up to <see cref="Ring0Cap"/> (12): the persisted
    /// <see cref="AppSettings.Ring0Count"/> is honoured (0 = auto, fill first),
    /// and the inner ring only grows beyond the user's choice when the outer
    /// ring would otherwise overflow its own cap.
    /// </summary>
    private int EffectiveRing0Count(int n)
    {
        if (n <= 0)
            return 0;

        int userR0 = _config.Settings.Ring0Count;
        int r0 = userR0 > 0
            // User picked a resident-region size — respect it, capped at the
            // ring limit and the available icon count (never forced to the cap).
            ? Math.Min(userR0, Math.Min(Ring0Cap, n))
            // Auto: fill the inner ring first, up to its cap.
            : Math.Min(Ring0Cap, n);

        // If the outer ring would overflow its cap, push the surplus inward.
        if (n - r0 > Ring1Cap)
            r0 = Math.Min(n, n - Ring1Cap);

        return Math.Clamp(r0, 1, n);
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

        // Glass grid: the icons live inside a clipped scroll layer, so a hovered
        // icon's name label (which hangs below the icon) is cut by the viewport
        // clip on the bottom row. Rather than reparent the icon (fragile — the
        // re-host fires synthetic enter/leave that flicker), show a floating
        // label in the unclipped PanelCanvas positioned under the hovered icon.
        if (_theme.ShowGlassPanel)
            ShowGlassHoverLabel(ic, idx);

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
    private const double HoverScaleConst = 1.7;

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
            };
            _glassHoverLabel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x26, 0x1A, 0x1A, 0x1A)),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 4, 10, 4),
                IsHitTestVisible = false,
                Child = _glassHoverLabelText,
                Opacity = 0,
            };
            Panel.SetZIndex(_glassHoverLabel, 4000);
            PanelCanvas.Children.Add(_glassHoverLabel);
        }

        // Match the built-in label's apparent size: that label lived inside the
        // icon's visual tree and was scaled up by the 1.7x hover zoom, so a fixed
        // 11.5pt read as ~11.5*1.7. Replicate that here (the floating label is
        // NOT scaled) so the font doesn't look shrunken.
        _glassHoverLabelText!.FontSize = 11.5 * HoverScaleConst;
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

        // Glass theme: dropping a dragged icon onto the left-edge dock pins it
        // there (the main-dock entry stays). Checked before delete/reorder so a
        // drag toward the left edge adds rather than deletes.
        if (_theme.ShowGlassPanel && DropToLeftDock != null)
        {
            Point screen = PointToScreen(p);
            if (DropToLeftDock(screen, icon.Entry))
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
