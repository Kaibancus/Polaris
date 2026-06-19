using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Polaris.Interop;
using Polaris.Models;
using Polaris.Services;
using Polaris.Views;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Polaris;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    /// <summary>The display's real (un-oversampled) refresh rate in Hz, used to
    /// cap slow always-on loop animations so they don't waste cycles being
    /// oversampled like short interactive transitions. Defaults to 60.</summary>
    public static int AmbientFrameRate { get; private set; } = 60;

    /// <summary>The oversampled animation tick rate (2x the real refresh on 60 Hz
    /// panels, native on high-refresh). Continuous loops as well as short
    /// transitions tick at this rate so motion stays smooth and low-latency.</summary>
    public static int AnimationFrameRate { get; private set; } = 120;

    /// <summary>Tick rate for always-on loop animations in the liquid-glass
    /// theme. That theme runs as a fullscreen per-pixel-alpha layered window, so
    /// every animation tick re-composites the whole screen — an expensive upload.
    /// Set to the display refresh (capped at 60), and throttled by
    /// <see cref="Services.RenderProfile"/> on weak machines so the background
    /// loops stay smooth without starving interaction.</summary>
    public static int GlassLoopFrameRate { get; private set; } = 60;

    /// <summary>Tick rate for slow ambient drifts (e.g. the glass orbit light
    /// that takes a full minute per revolution). Driven by
    /// <see cref="Services.RenderProfile"/>: 60 on capable hardware, 30 on weaker
    /// tiers (visually identical at that speed, half the layered-window uploads).</summary>
    public static int SlowDriftFrameRate => Services.RenderProfile.SlowDriftFrameRate;

    /// <summary>Whether the one-shot global timeline frame-rate metadata override
    /// has been installed (it can only be set once per process).</summary>
    private static bool _frameMetadataApplied;

    /// <summary>Applies the display-driven frame-rate / animation profile by
    /// setting the static rate fields (read whenever an animation is created)
    /// and, on the first call, the global timeline default frame rate.
    ///
    /// Interactive animations (hover/magnify, summon, drag) are beat-masked: the
    /// display's ACTUAL refresh is never an exact integer (a "60 Hz" panel runs
    /// at ~59 Hz), so ticking interactive motion at exactly 60 beats against the
    /// present and drops/doubles frames -> visible judder, which is most obvious
    /// on the expensive fullscreen liquid-glass dock. Over-sampling ~2x on
    /// &lt;=60 Hz panels masks that beat so interaction presents a smooth 60;
    /// high-refresh panels present fast enough to use their native rate.
    ///
    /// The always-on loops (planet/Saturn spin, running pulses, glass shimmer)
    /// run at 60 fps (capped to the refresh) for the smoothest backdrop.</summary>
    internal static void ApplyDisplayProfile()
    {
        Services.RenderProfile.Detect();
        int hz = (int)Math.Clamp(GetPrimaryRefreshRate(), 60, 240);
        // Follow the display for INTERACTIVE motion: oversample <=60 Hz to mask
        // the beat, native on high-refresh. Interaction is never degraded — weak
        // machines are helped by throttling the always-on LOOPS and shrinking the
        // composited-pixel area instead (see RenderProfile).
        int animHz = hz < 90 ? Math.Min(hz * 2, 240) : hz;  // 59->118, 144->144
        AnimationFrameRate = animHz;
        // Background loop rate: 60 on capable hardware, throttled by the quality
        // tier on weak machines (capped to the real refresh).
        int loopHz = Math.Min(Services.RenderProfile.LoopFrameRate, hz);
        AmbientFrameRate = loopHz;
        GlassLoopFrameRate = loopHz;

        if (!_frameMetadataApplied)
        {
            System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
                typeof(System.Windows.Media.Animation.Timeline),
                new FrameworkPropertyMetadata(animHz));
            _frameMetadataApplied = true;
        }
    }

    private AppConfig _config = new();
    private KeyboardHook? _hook;
    private KeyboardHook? _pinnedHook;
    private KeyboardHook? _escHook;
    private IMainDock? _panel;
    private SettingsWindow? _settings;
    // True from the moment a settings-open is requested until the window is
    // actually shown. During this window the docks are fading out, so the edge
    // poll must NOT re-summon the side dock (which would flash on screen).
    private bool _openingSettings;
    private Forms.NotifyIcon? _tray;
    private Polaris.Views.ISideDock? _sideDock;
    private DispatcherTimer? _edgePollTimer;

    // Idle working-set trimming. While both docks are hidden, Polaris's (almost
    // entirely unmanaged) footprint can be evicted to the standby list so the
    // physical RAM is returned to the system until the next summon. Tracked off
    // the existing 100&#160;ms edge poll: trim once the app has been idle for a
    // short grace period, then re-trim periodically so the figure stays low
    // across a long idle (background timers slowly fault a few pages back).
    private DateTime _idleSince = DateTime.MaxValue;
    private DateTime _lastIdleTrim = DateTime.MinValue;
    private static readonly TimeSpan IdleTrimDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleTrimInterval = TimeSpan.FromSeconds(30);

    // Cursor-movement tracking for the side dock's ambient-animation pause. The
    // dock is a large per-pixel-alpha layered window, so its breathing run-dots
    // re-composite the whole surface every tick. We freeze them whenever the
    // cursor is not actively moving over the dock (stationary or away), and resume
    // the instant it moves again — eliminating the idle render cost of a dock the
    // user is merely parked next to.
    private Point _lastCursorDip = new(double.NaN, double.NaN);
    private DateTime _lastCursorMoveUtc = DateTime.MinValue;
    private bool _cursorMovingRecently;
    private static readonly TimeSpan AmbientStillDelay = TimeSpan.FromMilliseconds(700);

    // Masks the system auto-hide taskbar's reveal trigger under the bottom
    // side-dock's centre-50% activation band. Self-contained subsystem (own hook
    // thread + message loop + poll thread); the edge poll only toggles its
    // Active flag. See Services/TaskbarGuard.cs.
    private readonly Polaris.Services.TaskbarGuard _taskbarGuard = new();

    // Dismisses both docks when the user left-clicks outside every Polaris window
    // while the main dock is open. Self-contained (own hook thread + message
    // loop); the owner only toggles its Active flag. See ClickAwayWatcher.cs.
    private readonly Polaris.Services.ClickAwayWatcher _clickAway = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // GPU-rendering spike benchmark: POLARIS_GPU_BENCH=gpu|wpf runs a large
        // animated per-pixel-alpha window in the chosen compositing path, samples
        // CPU to gpu-bench.csv, then exits — skipping normal tray startup.
        string? bench = Environment.GetEnvironmentVariable("POLARIS_GPU_BENCH");
        if (!string.IsNullOrEmpty(bench))
        {
            Polaris.Services.Gpu.GpuBenchmark.Run(bench);
            return;
        }

        // GPU-rendering spike — D2D/DirectWrite glass-slab visual-fidelity prototype:
        // shows a GPU-rendered glass slab + clock alongside normal startup so it can
        // be eyeballed against the real WPF glass dock.
        if (Environment.GetEnvironmentVariable("POLARIS_GLASS_PROTO") == "1")
            Dispatcher.BeginInvoke(new Action(Polaris.Services.Gpu.GlassPrototypeWindow.Show),
                DispatcherPriority.ApplicationIdle);

        // Global safety net: a tray-resident app must survive an unexpected
        // exception on the UI thread instead of vanishing silently. Log the
        // fault, tell the user, and keep running where it is safe to do so.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Frame-rate / animation profile is applied from the display refresh
        // (see ApplyDisplayProfile) once settings are loaded below.

        // Opt-in frame-rate profiler (POLARIS_FPS=1). No-op otherwise.
        FpsProfiler.StartIfRequested();

        // Single-instance guard: if another Polaris is already running,
        // notify the user and exit immediately.
        _singleInstanceMutex = new Mutex(true, @"Global\Polaris_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Forms.MessageBox.Show(
                "Polaris 已在运行（查看系统托盘图标）。",
                "Polaris",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Information);
            Shutdown();
            return;
        }

        _config = ConfigStore.Load();

        // Apply the display-driven frame-rate / animation profile before any window opens.
        ApplyDisplayProfile();
        // If the live governor later steps the quality tier down on a weak
        // machine, re-apply the frame-rate fields so newly created loop
        // animations tick at the throttled rate. Geometry-scaled knobs (Saturn
        // detail, cache scale) are re-read on the next summon's Rebuild.
        Services.RenderProfile.Changed += () => Dispatcher.Invoke(() =>
        {
            ApplyDisplayProfile();
            _panel?.RefreshFromConfig();
        });

        // Seed the global dock-text multiplier before any dock renders.
        Polaris.Services.FontScale.SetFromPercent(_config.Settings.FontSizePercent);

        // One-time migration: default "run at startup" to on for configs saved
        // before this became the default. Subsequent user changes are honored.
        if (!_config.Settings.StartupDefaultApplied)
        {
            _config.Settings.RunAtStartup = true;
            _config.Settings.StartupDefaultApplied = true;
            ConfigStore.Save(_config);
        }

        // Keep registry startup state in sync with config on launch.
        StartupManager.SetEnabled(_config.Settings.RunAtStartup);

        // One-time migration: the resident / inner-ring count used to be a single
        // shared value. Seed the currently-active theme's per-theme count from
        // the legacy shared value so it isn't lost; the other theme starts at
        // auto. From now on each theme keeps its own count.
        if (!_config.Settings.ResidentCountDecoupled)
        {
            ThemeRegistry.SaveAppearance(_config.Settings);
            _config.Settings.ResidentCountDecoupled = true;
            ConfigStore.Save(_config);
        }

        // Seed the active transparency / icon size from the current theme's
        // remembered values (or its defaults), so each theme opens with its own
        // look. Persist so the per-theme entry is captured on first run.
        ThemeRegistry.LoadAppearance(_config.Settings);
        ConfigStore.Save(_config);

        // Pick the main-dock implementation. The GPU (DirectComposition + Direct2D)
        // liquid-glass dock is now the DEFAULT; set POLARIS_GPU_MAINDOCK=0 to fall
        // back to the WPF dock. Both implement IMainDock so the host wiring is identical.
        _panel = Environment.GetEnvironmentVariable("POLARIS_GPU_MAINDOCK") == "0"
            ? new RadialWindow(_config, Persist)
            : new Polaris.Views.MainDockWindowGpu(_config);
        _panel.RequestOpenSettings += OpenSettings;
        _panel.Realize();   // realise once (stays shown, fully transparent) to avoid show/hide flicker

        // Second, vertical left-edge dock. Shares the config + persist callback;
        // a desktop shortcut dropped onto it (or a main-dock icon dragged into
        // it) mutates the shared app lists, so refresh the main dock when it does.
        // The left dock mirrors the main dock's resident region (top two rows),
        // so seed that mirror once before the docks build.
        DockSync.MirrorResidentToLeft(_config);
        // Pick the side-dock implementation. The GPU (DirectComposition + Direct2D)
        // dock is now the DEFAULT; set POLARIS_GPU_SIDEDOCK=0 to fall back to the WPF
        // one. Both implement ISideDock so the host wiring below is identical.
        _sideDock = Environment.GetEnvironmentVariable("POLARIS_GPU_SIDEDOCK") == "0"
            ? new SideDockWindow(_config, Persist)
            : new Polaris.Views.SideDockWindowGpu(_config);
        _sideDock.MainDockChanged += () => _panel?.RefreshFromConfig();
        // Clicking the Polaris tile in the left dock's running strip toggles the
        // pinned docks (equivalent to Ctrl+4).
        _sideDock.ToggleDocks = TogglePinnedDock;
        _sideDock.Realize();
        // Let the main dock hand an icon to the left dock when dragged onto it.
        _panel.DropToSideDock = TryDropToSideDock;
        // Lift the liquid-glass main dock clear of the side dock when the latter
        // is docked at the bottom (so they never overlap).
        _panel.BottomDockReserve = GetBottomDockReserve;
        // Keep the left dock in step when the main dock's resident region changes.
        _panel.AppsChanged = () =>
        {
            if (DockSync.MirrorResidentToLeft(_config))
                _sideDock?.RefreshFromConfig();
        };
        // Keep the left dock visible while a glass icon is being dragged.
        _panel.GlassDragActiveChanged = active => _sideDock?.SetDragActive(active);
        // Retract the left dock together with the main dock (e.g. when launching
        // an app from the main dock hides the panel). Clears every show reason —
        // including the Ctrl+4 "pinned" reason — so a launch always retracts both.
        _panel.PanelDismissed += () =>
        {
            _clickAway.Active = false;   // central disarm: covers every hide path
            _sideDock?.HideAll();
        };
        StartEdgePoll();
        _taskbarGuard.Start();

        // A left-click anywhere outside every Polaris window dismisses both docks
        // while the main dock is open. The hook fires on its own thread, so marshal
        // to the UI thread before hiding.
        _clickAway.ClickedOutside += () =>
        {
            _panel?.Dispatcher.BeginInvoke(() =>
            {
                if (_panel?.IsShown == true)
                {
                    _clickAway.Active = false;
                    _panel.HidePanel();   // also retracts the left dock via PanelDismissed
                }
            });
        };
        _clickAway.Start();

        RebuildHook();
        SetupPinnedHooks();

        // Listen for taskbar "needs attention" flashes so dock icons can show a
        // new-message badge mirroring the system taskbar.
        Polaris.Services.AttentionService.Start();

        SetupTray();
    }

    /// <summary>(Re)installs the keyboard hook using the current trigger key.</summary>
    private void RebuildHook()
    {
        _hook?.Dispose();
        _hook = new KeyboardHook(_config.Settings.TriggerKey);
        _hook.KeyPressed += OnHotkeyPressed;
        _hook.KeyReleased += OnHotkeyReleased;
        _hook.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _persistTimer;

    /// <summary>
    /// Coalesces rapid configuration changes (dragging a slider, reordering
    /// icons) into a single disk write ~300 ms after the last change, instead
    /// of serializing the whole config on every event. Pending writes are
    /// flushed on exit via <see cref="FlushPersist"/>.
    /// </summary>
    private void Persist()
    {
        if (_persistTimer == null)
        {
            _persistTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300),
            };
            _persistTimer.Tick += (_, _) =>
            {
                _persistTimer!.Stop();
                // Capture the active appearance (transparency / icon size /
                // resident count) into the current theme's per-theme record so
                // drag-changed values survive a restart, then save.
                ThemeRegistry.SaveAppearance(_config.Settings);
                ConfigStore.Save(_config);
            };
        }
        _persistTimer.Stop();
        _persistTimer.Start();
    }

    /// <summary>Writes any pending debounced config change immediately.</summary>
    private void FlushPersist()
    {
        if (_persistTimer is { IsEnabled: true })
        {
            _persistTimer.Stop();
            ThemeRegistry.SaveAppearance(_config.Settings);
            ConfigStore.Save(_config);
        }
    }

    /// <summary>
    /// Dedicated global hotkeys for the pinned (drag-to-add) panel:
    /// press Ctrl+<digit> (configurable, default Ctrl+4) to toggle it (open if
    /// hidden, close if shown), or press Esc to dismiss it. The digit key is
    /// swallowed only while Ctrl is held, so a normal digit keystroke is unaffected.
    /// </summary>
    private void SetupPinnedHooks()
    {
        const int VK_ESCAPE = 0x1B;

        // Ctrl+<digit> toggles the pinned panel (built from the current setting so
        // it can be rebuilt live when the user picks a different combination).
        RebuildPinnedHook();

        _escHook = new KeyboardHook(VK_ESCAPE);
        _escHook.KeyPressed += () =>
        {
            if (_panel?.IsShown == true)
            {
                _clickAway.Active = false;
                _panel.HidePanel();
                _sideDock?.SetPinnedShown(false);
            }
        };
        _escHook.Start();
    }

    /// <summary>(Re)installs the Ctrl+&lt;digit&gt; toggle hotkey from the current
    /// <see cref="AppSettings.ToggleKey"/> setting and refreshes the tray tooltip.</summary>
    private void RebuildPinnedHook()
    {
        _pinnedHook?.Dispose();
        _pinnedHook = new KeyboardHook(_config.Settings.ToggleKey, suppressKey: true, requireCtrl: true);
        _pinnedHook.KeyPressed += TogglePinnedDock;
        _pinnedHook.Start();
        if (_tray != null)
            _tray.Text = BuildTrayTooltip();
    }

    /// <summary>The tray tooltip, including the currently configured toggle combo.</summary>
    private string BuildTrayTooltip()
    {
        char digit = (char)_config.Settings.ToggleKey;
        return $"Polaris — 单击开关固定显示 / 长按触发键临时显示 / Ctrl+{digit} 开关（Esc关闭）";
    }

    /// <summary>Toggles the pinned (sticky) main + left dock: open both when
    /// hidden, close both when shown. Shared by the Ctrl+4 hotkey and the tray
    /// icon's left-click.</summary>
    private void TogglePinnedDock()
    {
        if (_panel?.IsShown == true)
        {
            _clickAway.Active = false;
            _panel.HidePanel();
            _sideDock?.SetPinnedShown(false);
        }
        else
        {
            // If the settings window is open, close it first so the summoned
            // dock does not stack on top of it and block interaction.
            CloseSettingsIfOpen();
            // Summon on whichever monitor the cursor is on (when multi-monitor
            // is enabled) so both docks appear where the user invoked them.
            SetActiveMonitorFromCursor();
            _panel?.ShowPinned();
            _sideDock?.SetPinnedShown(true);   // summon the left dock together
            _clickAway.Active = true;          // arm click-away dismiss
        }
    }

    private void OnHotkeyPressed()
    {
        // The hotkey-summoned dock should replace the settings window, not
        // overlap it.
        CloseSettingsIfOpen();
        SetActiveMonitorFromCursor();
        _panel?.ShowPanel();
        _sideDock?.SetMainShown(true);   // the left dock summons together with the main dock
        _clickAway.Active = true;        // arm click-away dismiss
    }

    /// <summary>Closes the settings window if it is open (or still in the middle
    /// of opening), so a freshly summoned dock never stacks on top of it.</summary>
    private void CloseSettingsIfOpen()
    {
        _openingSettings = false;   // cancel a pending deferred open
        if (_settings != null)
        {
            _settings.Close();
            _settings = null;
        }
    }

    /// <summary>Points the shared dock-positioning target at the monitor under
    /// the cursor when "show on all monitors" is enabled, or the primary monitor
    /// otherwise. Both docks read this when they lay themselves out.</summary>
    private void SetActiveMonitorFromCursor()
    {
        if (_config.Settings.DockOnAllMonitors && GetCursorPos(out POINT pt))
            Polaris.Services.MonitorLayout.SetTargetForPhysicalPoint(pt.X, pt.Y, GetDpiScale());
        else
            Polaris.Services.MonitorLayout.UsePrimary();
    }

    private void OnHotkeyReleased()
    {
        _panel?.HideIfNotPinned();
        // HideIfNotPinned is a no-op while pinned (Ctrl+4), so keep click-away
        // armed in that case; only disarm once the dock is actually hidden.
        _clickAway.Active = _panel?.IsShown == true;
        // Releasing the hotkey drops the "shown by main" reason; the left dock
        // stays only if the mouse is currently over its edge trigger (handled by
        // the edge poll, which sets the edge-shown reason).
        _sideDock?.SetMainShown(false);
    }

    /// <summary>Polls the cursor position so the left dock appears when the mouse
    /// reaches the left-centre screen edge and hides when it leaves the dock (and
    /// the trigger zone). Runs only the cheap GetCursorPos call on each tick.</summary>
    private void StartEdgePoll()
    {
        _edgePollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _edgePollTimer.Tick += (_, _) =>
        {
            if (_sideDock == null)
                return;
            // Refresh the cached state the mouse-guard hook reads (one cheap shell
            // query per tick, off the hot mouse path): only mask the taskbar when
            // it is auto-hide AND the side dock is anchored to the bottom.
            try
            {
                var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
                bool autoHide =
                    ((long)SHAppBarMessage(ABM_GETSTATE, ref abd) & ABS_AUTOHIDE) != 0;
                _taskbarGuard.Active = autoHide
                    && _sideDock.DockSidePosition == DockSide.Bottom;
            }
            catch { _taskbarGuard.Active = false; }
            // Suppress the left-edge auto-summon while the settings window is
            // open (or in the middle of opening), so the dock cannot re-appear
            // over it or flash during the dock fade-out.
            if (_settings != null || _openingSettings)
            {
                _sideDock.SetEdgeShown(false);
                return;
            }
            // Don't let the mouse summon the dock over a full-screen / borderless
            // app (typically a game running full-screen): the dock must not intrude.
            if (IsFullscreenForeground())
            {
                _sideDock.SetEdgeShown(false);
                return;
            }
            if (!GetCursorPos(out POINT pt))
                return;

            // Convert physical pixels to WPF DIPs (the dock works in DIPs).
            double scale = GetDpiScale();
            double x = pt.X / scale;
            double y = pt.Y / scale;

            // Evaluate the trigger against the monitor the dock will summon on:
            // the monitor under the cursor when "show on all monitors" is enabled
            // (also updating the shared target so the dock positions there), or
            // the primary monitor otherwise.
            Rect mon;
            if (_config.Settings.DockOnAllMonitors)
            {
                Polaris.Services.MonitorLayout.SetTargetForPhysicalPoint(pt.X, pt.Y, scale);
                mon = Polaris.Services.MonitorLayout.ActiveBounds;
            }
            else
            {
                Polaris.Services.MonitorLayout.UsePrimary();
                mon = new Rect(0, 0,
                    SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            }

            const double Reach = 8.0;        // edge sensitivity in DIPs
            const double Band = 0.25;        // exclude the outer 25% at each end
            double bandV = mon.Height * Band;
            double bandH = mon.Width * Band;

            // Trigger band: a strip along the dock's anchored edge, covering the
            // centre 50% of that edge. The threshold is generous (and a touch of
            // over-travel past the physical edge also counts) so the dock pops
            // without landing the pointer exactly on the edge.
            DockSide side = _sideDock.DockSidePosition;
            bool inTrigger = side switch
            {
                DockSide.Right => x >= mon.Right - Reach && y >= mon.Top + bandV && y <= mon.Bottom - bandV,
                DockSide.Top => y <= mon.Top + Reach && x >= mon.Left + bandH && x <= mon.Right - bandH,
                DockSide.Bottom => y >= mon.Bottom - Reach && x >= mon.Left + bandH && x <= mon.Right - bandH,
                _ => x <= mon.Left + Reach && y >= mon.Top + bandV && y <= mon.Bottom - bandV,
            };

            // Once shown by the edge, keep it shown while the cursor stays near
            // the dock. A modest interior margin lets the dock retract soon after
            // the pointer moves clear of the slab, without being so tight that it
            // flickers right at the slab boundary.
            bool inDock = false;
            if (_sideDock.DockVisible)
            {
                Rect b = _sideDock.GetDockScreenBounds();
                const double Far = 28;       // interior reach beyond the slab
                const double Slack = 14;     // slack along the edge
                inDock = side switch
                {
                    DockSide.Right => x >= b.Left - Far && y >= b.Top - Slack && y <= b.Bottom + Slack,
                    DockSide.Top => y <= b.Bottom + Far && x >= b.Left - Slack && x <= b.Right + Slack,
                    DockSide.Bottom => y >= b.Top - Far && x >= b.Left - Slack && x <= b.Right + Slack,
                    _ => x <= b.Right + Far && y >= b.Top - Slack && y <= b.Bottom + Slack,
                };
            }

            _sideDock.SetEdgeShown(inTrigger || inDock);
            // Freeze the dock's perpetual breathing animations unless the cursor is
            // actively moving over (or summoning) it. On this large layered window
            // each opacity tick re-composites the whole surface (~60% CPU + resident
            // RAM), so it is wasteful while the cursor is parked still or away. Any
            // cursor movement resumes them instantly; the magnify wave already stops
            // itself once it has converged, so a still cursor leaves the dock fully
            // static and the working-set trim can reclaim its memory.
            var curDip = new Point(x, y);
            if (double.IsNaN(_lastCursorDip.X) ||
                Math.Abs(curDip.X - _lastCursorDip.X) > 0.5 ||
                Math.Abs(curDip.Y - _lastCursorDip.Y) > 0.5)
            {
                _lastCursorDip = curDip;
                _lastCursorMoveUtc = DateTime.UtcNow;
            }
            bool cursorMovingRecently = DateTime.UtcNow - _lastCursorMoveUtc < AmbientStillDelay;
            _cursorMovingRecently = cursorMovingRecently;
            bool ambientAttended = _sideDock.DockVisible && (inTrigger || inDock) && cursorMovingRecently;
            _sideDock.SetAmbientPaused(!ambientAttended);
            // The main dock's orbit light + running glows re-composite its large
            // (often full-screen) layered window every tick; freeze them whenever the
            // cursor is not moving, so a parked main dock goes static (CPU drops and
            // the trim can reclaim its RAM) and resumes the instant the cursor moves.
            _panel?.SetAmbientPaused(!cursorMovingRecently);
            EvaluateIdleTrim();
        };
        _edgePollTimer.Start();
    }

    /// <summary>Called every edge-poll tick. Trims the process working set once
    /// Polaris has been "passive" for <see cref="IdleTrimDelay"/> (re-trimming
    /// every <see cref="IdleTrimInterval"/> while it stays passive), returning its
    /// (almost entirely unmanaged) RAM to the system. Passive means the user is not
    /// interacting with a Polaris surface AND no surface is continuously animating:
    /// <list type="bullet">
    /// <item>the main dock is hidden — while it is shown its running-app glows keep
    /// breathing, which re-composites its (full-screen, layered) surface every frame,
    /// so a trim would just churn pages straight back; AND</item>
    /// <item>the cursor is not moving over a visible secondary surface (side dock /
    /// settings), whose breathing freezes when the cursor stops.</item>
    /// </list>
    /// A visible window's on-screen pixels are held by DWM, so eviction never blanks
    /// it; the next interaction faults the few pages it needs back in.</summary>
    private void EvaluateIdleTrim()
    {
        // Main dock shown (its run-glows keep re-compositing the surface) or settings
        // mid-open: treat as active so we don't churn pages.
        if (_panel?.IsShown == true || _openingSettings)
        {
            _idleSince = DateTime.MaxValue;
            return;
        }

        // Active only while the cursor is actually MOVING over a visible secondary
        // surface — a still cursor freezes the side dock's breathing, so its pages
        // can be evicted and faulted back on the next move.
        bool active = false;
        if (_cursorMovingRecently && GetCursorPos(out POINT cp))
        {
            double scale = GetDpiScale();
            var cur = new Point(cp.X / scale, cp.Y / scale);

            if (_sideDock?.DockVisible == true)
            {
                Rect b = _sideDock.GetDockScreenBounds();
                b.Inflate(24, 24);   // a little slack so a near-edge hover counts
                if (b.Contains(cur))
                    active = true;
            }
            if (!active && _settings is { IsLoaded: true } s && s.ActualWidth > 0)
            {
                var sb = new Rect(s.Left, s.Top, s.ActualWidth, s.ActualHeight);
                sb.Inflate(16, 16);
                if (sb.Contains(cur))
                    active = true;
            }
        }
        if (active)
        {
            _idleSince = DateTime.MaxValue;
            return;
        }

        var now = DateTime.UtcNow;
        if (_idleSince == DateTime.MaxValue)
            _idleSince = now;
        if (now - _idleSince < IdleTrimDelay)
            return;
        if (now - _lastIdleTrim < IdleTrimInterval)
            return;

        MemoryTrimmer.TrimWorkingSet();
        _lastIdleTrim = now;
    }

    private const uint ABM_GETSTATE = 0x00000004;
    private const long ABS_AUTOHIDE = 0x0000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public int lParam;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    /// <summary>Adds the dragged main-dock entry to the left dock if the drop
    /// point lands over the left dock. Returns true when it was added there.</summary>
    private bool TryDropToSideDock(Point screenPoint, AppEntry entry)
    {
        // screenPoint is in DEVICE pixels (from Visual.PointToScreen). Let the
        // left dock convert it through its own PointFromScreen so the hit test is
        // DPI-correct without any manual scale math.
        return _sideDock?.TryAcceptDrop(screenPoint, entry) == true;
    }

    /// <summary>Height (DIP, measured up from the bottom screen edge) that the
    /// side dock occupies when it is docked at the BOTTOM, plus a small gap, so
    /// the liquid-glass main dock can lift itself clear of it. Returns 0 when the
    /// side dock is on any other edge.</summary>
    private double GetBottomDockReserve()
    {
        if (_sideDock == null || _sideDock.DockSidePosition != DockSide.Bottom)
            return 0.0;
        Rect b = _sideDock.GetDockScreenBounds();
        // For a bottom dock the slab's rect HEIGHT is its thickness, which is
        // independent of which monitor it sits on. Reserve that thickness plus a
        // small gap so the glass dock lifts just clear of it. (Deriving the
        // reserve from screen coordinates — e.g. primaryHeight - b.Top — breaks
        // on a secondary monitor whose bottom edge differs from the primary's,
        // producing a huge bogus reserve that shoves the glass dock off-screen.)
        const double gap = 18.0;
        double reserve = b.Height + gap;
        return reserve > 0 ? reserve : 0.0;
    }

    private static double GetDpiScale()
    {
        IntPtr hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
            return 1.0;
        try
        {
            const int LOGPIXELSX = 88;
            int dpi = GetDeviceCaps(hdc, LOGPIXELSX);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct FSRECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out FSRECT lpRect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    // The shell's "do not disturb / full-screen" signal — the same state the
    // taskbar and notification centre consult before showing themselves.
    private enum QUNS { NOT_PRESENT = 1, BUSY = 2, RUNNING_D3D_FULL_SCREEN = 3, PRESENTATION_MODE = 4, ACCEPTS_NOTIFICATIONS = 5, QUIET_TIME = 6, APP = 7 }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out QUNS state);

    /// <summary>True when a full-screen app (a game in exclusive or borderless
    /// full-screen, or presentation mode) owns the screen — so the edge-summon is
    /// suppressed and the dock never pops over it, exactly like the auto-hide
    /// taskbar. Mirrors the taskbar's two checks:
    ///   1. <c>SHQueryUserNotificationState</c> — the shell's authoritative
    ///      signal, but ONLY for the two unambiguous true-full-screen states:
    ///      an exclusive-mode D3D game (RUNNING_D3D_FULL_SCREEN) or presentation
    ///      mode (PRESENTATION_MODE). The BUSY and APP states are deliberately NOT
    ///      treated as full-screen: a merely MAXIMISED window — especially a
    ///      maximised Store / immersive (UWP) app — also reports APP/BUSY, and the
    ///      dock must still be summonable over those.
    ///   2. The "rude window" geometry test — foreground window covers the WHOLE
    ///      monitor rectangle (not just the work area) AND has no caption / sizing
    ///      frame, which is how the shell flags a borderless full-screen window. A
    ///      maximised window keeps its caption / frame (and, with a visible taskbar,
    ///      stops at the work area), so it is excluded.
    /// Our own windows, the desktop and the shell are always excluded.</summary>
    private static bool IsFullscreenForeground()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == GetDesktopWindow() || fg == GetShellWindow())
            return false;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId)
            return false;   // our own dock / overlay windows

        // 1. Shell notification state — but ONLY the unambiguous true-full-screen
        // states. BUSY and APP are intentionally excluded: a maximised window
        // (notably a maximised Store/immersive app) reports those too, and a
        // maximised window must NOT suppress the dock — only a real full-screen /
        // borderless one should. Borderless full-screen is caught by the geometry
        // test below instead.
        try
        {
            if (SHQueryUserNotificationState(out QUNS s) == 0 &&
                (s == QUNS.RUNNING_D3D_FULL_SCREEN || s == QUNS.PRESENTATION_MODE))
                return true;
        }
        catch (Exception ex) { Log.Debug("Fullscreen", "shell notification-state query failed; using geometry test", ex); }

        // 2. "Rude window" geometry test — a BORDERLESS window that covers the
        // whole monitor. The style test (no caption, no sizing frame) is essential:
        // with an auto-hide taskbar the work area equals the full monitor, so a
        // merely maximised browser / Explorer window also fills rcMonitor — but it
        // KEEPS its caption and thick frame, so it is correctly excluded here. Only
        // a true borderless full-screen window (WS_POPUP-style, no caption/frame)
        // trips this branch.
        if (!GetWindowRect(fg, out FSRECT wr))
            return false;
        const int GWL_STYLE = -16, WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
        int style = GetWindowLong(fg, GWL_STYLE);
        if ((style & (WS_CAPTION | WS_THICKFRAME)) != 0)
            return false;   // has a caption / sizing frame → ordinary (maybe maximised) window
        const uint MONITOR_DEFAULTTONEAREST = 2;
        IntPtr mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero)
            return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi))
            return false;
        const int T = 2;   // a couple of pixels of slack
        return wr.Left <= mi.rcMonitor.Left + T && wr.Top <= mi.rcMonitor.Top + T
            && wr.Right >= mi.rcMonitor.Right - T && wr.Bottom >= mi.rcMonitor.Bottom - T;
    }

    /// <summary>Primary monitor's vertical refresh rate in Hz (falls back to 60
    /// if the device-context query is unavailable).</summary>
    private static int GetPrimaryRefreshRate()
    {
        const int VREFRESH = 116;
        IntPtr hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
            return 60;
        try
        {
            int hz = GetDeviceCaps(hdc, VREFRESH);
            return hz > 1 ? hz : 60;   // 0/1 mean "default/unknown"
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    private void OpenSettings()
    {
        if (_settings != null)
        {
            _settings.Activate();
            return;
        }

        // Dismiss the main dock (and the left dock) FIRST, then show the
        // settings window only AFTER the dock's fade-out animation has fully
        // finished, so it never appears over a still-animating dock.
        _openingSettings = true;   // suppress edge re-summon during the fade
        _clickAway.Active = false;
        _sideDock?.HideAll();   // dismiss the left dock too, not just the main dock
        if (_panel != null)
            _panel.HidePanel(ShowSettingsWindow);
        else
            ShowSettingsWindow();
    }

    private void ShowSettingsWindow()
    {
        // A pending open can be cancelled (e.g. the user summoned the dock with
        // the hotkey during the dock's fade-out): CloseSettingsIfOpen clears the
        // flag, so bail here instead of popping the settings window over the dock.
        if (!_openingSettings)
            return;
        _openingSettings = false;
        if (_settings != null)
        {
            _settings.Activate();
            return;
        }

        _settings = new SettingsWindow(_config, Persist);
        _settings.Changed += () =>
        {
            // The "show on all monitors" toggle may have changed; re-point the
            // shared positioning target so the refresh below lands the docks on
            // the right monitor (the cursor's when enabled, the primary when not).
            SetActiveMonitorFromCursor();
            // A theme switch can change the resident count (Ring0Count is stored
            // per theme), so re-mirror the resident region into the side dock
            // before relaying it — otherwise the side dock keeps the old theme's
            // icon count.
            DockSync.MirrorResidentToLeft(_config);
            // Re-anchor / re-lay the side dock FIRST so a changed dock position
            // (or any geometry-affecting setting) takes effect immediately and
            // its new bounds are available to the main dock below.
            _sideDock?.RefreshLayout();
            // Then re-render the panel so theme / layout / size changes apply
            // live — and so the glass main dock reads the side dock's updated
            // bottom reserve and lifts clear of it.
            _panel?.RefreshFromConfig();
        };
        _settings.TriggerKeyChanged += RebuildHook;
        _settings.ToggleKeyChanged += RebuildPinnedHook;
        _settings.Closed += (_, _) => _settings = null;

        // The window cloaks itself on SourceInitialized (see SettingsWindow) so
        // DWM never shows its white first frame. Centre it on the active monitor,
        // then uncloak + fade in once its content has rendered.
        var settings = _settings;
        settings.Opacity = 0;
        settings.WindowStartupLocation = WindowStartupLocation.Manual;
        settings.ContentRendered += (_, _) =>
        {
            Rect wa = Polaris.Services.MonitorLayout.ActiveWorkArea;
            settings.Left = wa.Left + (wa.Width - settings.ActualWidth) / 2.0;
            settings.Top = wa.Top + (wa.Height - settings.ActualHeight) / 2.0;
            settings.Uncloak();
            settings.BeginAnimation(Window.OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(
                    0, 1, TimeSpan.FromMilliseconds(120)));
            settings.Activate();
        };
        _settings.Show();
    }

    private void SetupTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("设置", null, (_, _) => OpenSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());

        _tray = new Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Visible = true,
            Text = BuildTrayTooltip(),
            ContextMenuStrip = menu,
        };
        // Left-click toggles the pinned main + left dock (equivalent to Ctrl+4);
        // right-click shows the context menu (with 设置 / 退出).
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                TogglePinnedDock();
        };
    }

    private static Drawing.Icon LoadAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var info = Application.GetResourceStream(uri);
            if (info != null)
            {
                using var stream = info.Stream;
                return new Drawing.Icon(stream);
            }
        }
        catch
        {
        }
        return Drawing.SystemIcons.Application;
    }

    private void ExitApp()
    {
        FlushPersist();
        _taskbarGuard.Stop();
        _clickAway.Stop();
        _tray?.Dispose();
        _hook?.Dispose();
        _pinnedHook?.Dispose();
        _escHook?.Dispose();
        _panel?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        FpsProfiler.Stop();
        FlushPersist();
        _taskbarGuard.Stop();
        _clickAway.Stop();
        _tray?.Dispose();
        _hook?.Dispose();
        _pinnedHook?.Dispose();
        _escHook?.Dispose();
        Polaris.Services.AttentionService.Stop();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    // ---- Global exception handling ---------------------------------------

    private void OnDispatcherUnhandledException(object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        LogException("UI", e.Exception);
        // The UI thread can usually keep running after a handled fault; mark it
        // handled so the whole tray app does not terminate over one bad event.
        e.Handled = true;
        ShowFaultNotice(e.Exception);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Background/non-UI thread fault. The runtime tears down the process when
        // IsTerminating is true; we can only log here.
        if (e.ExceptionObject is Exception ex)
            LogException("Domain", ex);
    }

    private void OnUnobservedTaskException(object? sender,
        System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        LogException("Task", e.Exception);
        // Prevent the unobserved exception from escalating to a process kill.
        e.SetObserved();
    }

    private static bool _faultNoticeShown;

    /// <summary>Shows a single, non-fatal fault notice (only once per session so
    /// a repeating fault cannot spam the user with dialogs).</summary>
    private static void ShowFaultNotice(Exception ex)
    {
        if (_faultNoticeShown)
            return;
        _faultNoticeShown = true;
        try
        {
            Forms.MessageBox.Show(
                "Polaris 遇到一个错误，但仍在运行。\n" +
                "详情已记录到日志：\n" + Log.Path + "\n\n" + ex.Message,
                "Polaris",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Warning);
        }
        catch
        {
            // Never let the notice itself throw.
        }
    }

    private static void LogException(string source, Exception ex)
        => Log.Error(source, "unhandled exception", ex);
}
