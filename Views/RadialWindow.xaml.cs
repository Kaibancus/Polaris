using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    private const int Ring1Cap = 28;

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
    // (1.05 base disk enlarge, then a further +10% on the whole Saturn dock.)
    private const double SaturnDiskEnlarge = 1.3;
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

    // Inner-ring (resident) icons are drawn a touch smaller than the base icon
    // size, so the dense inner ring reads as a tidy "pinned" cluster. Sized so
    // the EFFECTIVE inner-icon scale (SaturnEnlarge * InnerIconScale) is 0.85.
    private const double InnerIconScale = 0.85 / SaturnEnlarge;

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
            // The slab bottom sits this far above the screen edge.
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
            catch (System.Exception ex) { Polaris.Services.Log.Debug("MainDock", "border-thickness probe failed", ex); }
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
        EffectiveIconSize * 0.22;

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
    private double GlassCellH => EffectiveIconSize * LiquidGlassTheme.RowPitch;

    /// <summary>Height of the glass dock <b>body</b> (the panel that frames the
    /// icon grid), fixed to the <see cref="LiquidGlassTheme.VisibleRows"/> visible
    /// rows so the slab keeps a constant footprint no matter how many rows the
    /// grid actually has (surplus rows scroll inside it).</summary>
    private double GlassDockBodyHeight
    {
        get
        {
            double icon = EffectiveIconSize;
            double padY = icon * 0.95;
            double gridHVis = (LiquidGlassTheme.VisibleRows - 1) * GlassCellH;
            // Reserve the resident gap so the visible rows pushed down below the
            // frame stay inside the slab and the dock keeps a constant footprint.
            return gridHVis + icon + padY * 2 + icon * LiquidGlassTheme.ResidentGap;
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
            double cellW = icon * LiquidGlassTheme.ColumnPitch;
            double gridW = (LiquidGlassTheme.Columns - 1) * cellW;
            // Vertical slack must stay below the row pitch (icon*RowPitch) so that
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
            double cellW = icon * LiquidGlassTheme.ColumnPitch;
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
    // Key-free weather + city shown after the clock; refreshes itself every ~20 min.
    private readonly Polaris.Services.WeatherService _weather = new();
    // Cached once instead of resolving the culture every clock tick (1/sec).
    private static readonly System.Globalization.CultureInfo ClockCulture =
        System.Globalization.CultureInfo.GetCultureInfo("zh-CN");

    // Set while showing the window so the SizeChanged fired by Show() does not
    // trigger a premature (wrong-centre) Rebuild that would flash the ring.
    private bool _suppressRebuild;

    // Intended visible state. Guards the deferred build callback so it does not
    // re-apply a fade after the panel was hidden again (fast key press/release).
    private bool _shown;

    // Whether the window has been realised (shown once) at startup.
    private bool _realized;

    // Phone-"notch"-style date/time panel shown at the screen top (or bottom when
    // the side dock is on the top edge) while the Saturn dock is summoned.
    private NotchClockWindow? _notch;

    private Point _center;
    private double _outerRadius;
    private readonly List<Point> _slotPositions = new();

    // Drag state
    private RadialIcon? _pressedIcon;
    private Point _pressPoint;
    private bool _dragging;
    // Independent overlay carrying the dragged icon so it stays visible anywhere
    // on the desktop, past the compact (clipped) main-dock window box.
    private DragGhostWindow? _dragGhost;

    // Icons in current _config.Apps order, parallel to the entries (and to
    // _slotPositions). Used to animate the non-dragged icons aside while
    // reordering. For the glass dock this list is kept FULL-LENGTH and 1:1 with
    // the entries, but off-screen rows are VIRTUALIZED: their slot holds null
    // (the RadialIcon object is discarded so WPF frees its software-rendered
    // visual subtree) and is recreated on demand when scrolled back into view.
    // Every index-based consumer therefore null-guards its slot.
    private readonly List<RadialIcon?> _iconElements = new();

    // Last-known running state per icon source, refreshed for EVERY configured
    // entry on each running-state poll (not just realized icons). Lets a glass
    // icon that was virtualized away show the correct green running light the
    // instant it is recreated on scroll-in, instead of waiting for the next poll.
    private readonly Dictionary<string, bool> _runStateCache = new();

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

    // True while the glass-panel idle profiling scene is pushed (the orbit light
    // + running-app sweeps that tick continuously whenever the glass dock is
    // shown but not being hovered). Symmetric with _profilingSaturn so a
    // POLARIS_FPS=1 session can read the glass idle frame rate as its own scene
    // instead of it being lumped into the generic "Idle" bucket.
    private bool _profilingGlass;

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
    public Func<Point, AppEntry, bool>? DropToSideDock;

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
        // Started on ShowPanel and stopped on HidePanel — there is no reason to keep
        // capturing window thumbnails (a BitBlt per running window) while the dock is
        // hidden, and doing so caused occasional background hitches.

        // Ticks once a second to keep the glass dock clock current while shown.
        _clockTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _clockTimer.Tick += (_, _) =>
        {
            UpdateGlassClock();
            // Cheap: the service self-throttles to one network fetch per ~20 min.
            _ = _weather.RefreshAsync();
        };
        // Repaint the clock line as soon as fresh weather arrives.
        _weather.Updated += OnWeatherUpdated;

        // Refresh badges promptly when an app starts/stops flashing for attention
        // (instead of waiting up to 1.5s for the next poll). Only while shown.
        AttentionService.Changed += OnAttentionChanged;
    }

    private void OnWeatherUpdated() => Dispatcher.BeginInvoke(new Action(UpdateGlassClock));

    protected override void OnClosed(EventArgs e)
    {
        // The window lives for the whole app session, but tear its subscriptions
        // down cleanly on close: the static AttentionService.Changed event would
        // otherwise root this instance, and the timers have no reason to keep
        // ticking once the dock is gone.
        AttentionService.Changed -= OnAttentionChanged;
        _weather.Updated -= OnWeatherUpdated;
        _runningTimer.Stop();
        _previewWarmTimer.Stop();
        _clockTimer.Stop();
        base.OnClosed(e);
    }

    private void OnAttentionChanged()
    {
        if (!_shown)
            return;
        Dispatcher.BeginInvoke(new Action(RefreshAttentionOnly));
    }

    /// <summary>Lightweight badge-only refresh used on the prompt flash event: it
    /// skips the (relatively slow) full running-process snapshot and only resolves
    /// each icon's windows to update its new-message badge, so the dot appears with
    /// minimal latency (the heavy IsRunning recompute still happens on the 1.5s
    /// poll). Runs the window scan on a background thread.</summary>
    private void RefreshAttentionOnly()
    {
        if (!_shown)
            return;
        var icons = _iconElements.OfType<RadialIcon>().ToList();
        var flashing = AttentionService.SnapshotFlashing();
        System.Threading.Tasks.Task.Run(() =>
        {
            var attention = new Dictionary<RadialIcon, (bool flashing, int count)>();
            foreach (var icon in icons)
                attention[icon] = AttentionBadges.ForIcon(icon, flashing, "MainDock");
            Dispatcher.BeginInvoke(() =>
            {
                foreach (var icon in icons)
                    if (attention.TryGetValue(icon, out var a))
                        icon.SetAttention(a.flashing, a.count);
            });
        });
    }

    /// <summary>Updates the glass dock's clock labels to the current local time,
    /// appending the key-free weather + city after the time when available.</summary>
    /// <summary>Shows the phone-notch date/time panel when the Saturn theme is
    /// active (hides it for any other theme). It sits on the active monitor's top
    /// edge, or its bottom edge when the side dock is anchored to the top.</summary>
    private void ShowNotchIfSaturn()
    {
        if (!_theme.IsSaturn)
        {
            _notch?.HideNotch();
            return;
        }
        _notch ??= new NotchClockWindow { Owner = this };
        bool atBottom = _config.Settings.DockPosition == Models.DockSide.Top;
        _notch.ShowNotch(atBottom);
    }

    private void UpdateGlassClock()
    {
        if (_glassClockTime == null && _glassClockDate == null)
            return;
        var now = DateTime.Now;
        var zh = ClockCulture;
        if (_glassClockTime != null)
        {
            string text = now.ToString("yyyy年M月d日  ddd   H:mm", zh);
            string? wx = _weather.Summary;
            if (!string.IsNullOrEmpty(wx))
                text += "     " + wx;
            _glassClockTime.Text = text;
        }
        if (_glassClockDate != null)
            _glassClockDate.Text = now.ToString("M月d日 ddd", zh);
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
            double cellW = icon * LiquidGlassTheme.ColumnPitch;
            double cellH = icon * LiquidGlassTheme.RowPitch;
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
            double cellW = icon * LiquidGlassTheme.ColumnPitch;
            double dockW = (LiquidGlassTheme.Columns - 1) * cellW + icon + icon * 1.15 * 2;
            double shadowPad = 72.0 * _uiScale;        // slab drop shadow (blur 48 + depth)
            double scrollPad = icon * 1.6;             // scrollbar parked right of the grid
            double hoverHeadroom = icon * 2.4;         // 1.7x zoom + label above the top row
            // The liquid-glass dock is a software-composited per-pixel-alpha
            // layered window: it has NO dirty-rect upload, so every animation
            // frame re-uploads the WHOLE window bitmap and the per-frame cost is
            // directly proportional to the window's physical pixel AREA. The
            // generic dragHeadroom (5x icon) is empty space that exists only to
            // keep an icon visible while it is flicked out past the slab to
            // delete it — yet it inflates EVERY frame's upload during the common
            // hover / summon / pulse paths. Deletion only needs the dragged icon
            // to clear the slab edge (IsDeleteDrop = outside GlassSlabRect), so a
            // tighter glass-specific headroom (still ~1.8 icon widths past the
            // slab) keeps the gesture comfortable while shrinking the composited
            // area further, which lifts the real glass frame rate.
            double glassDragHeadroom = _config.Settings.IconSize * _themeScale * 1.8;
            // dragHeadroom is added sideways (both edges) and upward only — the
            // window's bottom stays pinned to the screen edge so the dock keeps
            // its position while drag-out clearance grows above and beside it.
            w = Math.Min(dockW + shadowPad * 2 + scrollPad + glassDragHeadroom * 2, sw);
            h = Math.Min(GlassDockTotalHeight + GlassDockBottomMargin + hoverHeadroom + shadowPad + glassDragHeadroom, sh);
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

    private Brush LabelBrush => new SolidColorBrush(ColorUtil.Parse(_config.Settings.FontColor, Colors.White));
    private Color AccentColor => ColorUtil.Parse(_config.Settings.AccentColor, Color.FromRgb(0x3D, 0x7E, 0xFF));

    /// <summary>Tears down the entire dock visual tree and drops every reference
    /// into it, so the elements (and their BitmapCache bitmaps — the bulk of the
    /// dock's render memory) can be collected. Called at the top of <see
    /// cref="Rebuild"/> (which then recreates everything) and on hide (to release
    /// that memory while the dock is dismissed; the next show rebuilds anyway).</summary>
    private void ClearVisualTree()
    {
        PanelCanvas.Children.Clear();
        _iconElements.Clear();
        // The orbit-light spin / running-glow clocks lived on the cleared visuals.
        ClearAmbientLoops();
        // Icons are about to be discarded, so drop any in-flight magnification
        // wave state (its per-icon array indexes the old icon list).
        StopMagTicking();
        _magCur = Array.Empty<double>();
        _magCursor = new Point(double.NaN, double.NaN);
        _hoverIcon = null;
        _glassHoverLabel = null;
        _glassHoverLabelText = null;
        // The taskbar tiles were children of the cleared canvas.
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
    }

    private void Rebuild()
    {
        UpdateCenter();
        PruneIconCache();

        // Resolve the active theme from config each rebuild so switching the
        // theme in settings takes effect on the next render.
        _theme = ThemeRegistry.Get(_config.Settings.Theme);
        RootGrid.Background = _theme.WindowBackground;

        ClearVisualTree();

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

        // Detach the off-screen glass rows so only the visible window of icons
        // holds a (software-rendered) visual subtree. Runs after the full grid is
        // built so _slotPositions / _iconElements are complete and 1:1.
        if (_theme.ShowGlassPanel)
            UpdateGlassVirtualization();

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
        var icons = _iconElements.OfType<RadialIcon>().ToList();
        var entries = new List<AppEntry>(_config.Apps);
        var flashing = AttentionService.SnapshotFlashing();
        System.Threading.Tasks.Task.Run(() =>
        {
            var running = RunningAppTracker.SnapshotRunning();
            List<string> explorerTitles;
            try { explorerTitles = WindowPreviewService.GetExplorerWindowTitles(); }
            catch { explorerTitles = new List<string>(); }
            System.Collections.Generic.HashSet<string> runningAumids;
            try { runningAumids = WindowPreviewService.SnapshotRunningAumids(); }
            catch { runningAumids = new System.Collections.Generic.HashSet<string>(); }

            // Running state for EVERY configured entry (cheap, entry-based) so a
            // virtualized icon recreated on scroll-in can be lit instantly from
            // the cache rather than appearing dark until the next poll.
            var runMap = new Dictionary<string, bool>();
            foreach (var entry in entries)
                runMap[entry.EffectiveIconSource] = RunningAppTracker.IsEntryRunning(
                    entry, running, explorerTitles, runningAumids);

            // Resolve each running icon's windows to derive its new-message badge:
            // flashing if any of its windows is requesting attention, with a best-
            // effort unread count parsed from the window titles. Reusing
            // GetWindowsForEntry keeps the icon→window matching identical to the
            // hover previews (AUMID / folder / Office gating all handled there).
            var attention = new Dictionary<RadialIcon, (bool flashing, int count)>();
            foreach (var icon in icons)
            {
                bool isRunning = runMap.TryGetValue(icon.Entry.EffectiveIconSource, out var r) && r;
                if (!isRunning)
                {
                    attention[icon] = (false, 0);
                    continue;
                }
                attention[icon] = AttentionBadges.ForIcon(icon, flashing, "MainDock");
            }

            Dispatcher.BeginInvoke(() =>
            {
                foreach (var kv in runMap)
                    _runStateCache[kv.Key] = kv.Value;
                foreach (var icon in icons)
                {
                    icon.IsRunning = runMap.TryGetValue(icon.Entry.EffectiveIconSource, out var r) && r;
                    if (attention.TryGetValue(icon, out var a))
                        icon.SetAttention(a.flashing, a.count);
                }
            });
        });
    }

    /// <summary>Applies the cached running state to a single icon — used the
    /// moment a virtualized glass icon is recreated on scroll-in so its green
    /// light matches the rest of the dock without waiting for the next poll.</summary>
    private void RefreshIconState(RadialIcon icon)
    {
        if (_runStateCache.TryGetValue(icon.Entry.EffectiveIconSource, out var r))
            icon.IsRunning = r;
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
        icon.ExternalMagnify = true;   // dock drives a cursor-distance wave
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
        => IconExtractor.PruneCache(_iconCache, _config.Apps);

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
        // Always reserve the outer ring band so it stays visible like the inner
        // ring, even when no app currently occupies the outer ring.
        _outerRadius = InnerRadius + RingStep;

        if (count <= 0)
            return;

        int r0 = EffectiveRing0Count(count);
        _slotPositions.AddRange(SlotPositionsFor(count, r0));
    }

}
