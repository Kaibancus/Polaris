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
    /// Capping the slow background loops (running-app sweep/glow) low frees the
    /// composition budget for the interactive hover/zoom to stay fluid.</summary>
    public static int GlassLoopFrameRate { get; private set; } = 30;

    /// <summary>Whether the one-shot global timeline frame-rate metadata override
    /// has been installed (it can only be set once per process).</summary>
    private static bool _frameMetadataApplied;

    /// <summary>Applies the user's frame-rate / animation profile by setting the
    /// three static rate fields (read whenever an animation is created) and, on
    /// the first call, the global timeline default frame rate.
    ///
    /// High: follows the display refresh. The default metadata under-samples short
    /// transitions and the display's ACTUAL refresh is never an exact integer (a
    /// "60 Hz" panel runs at 59.94 Hz); ticking at exactly 60 beats against it and
    /// drops/doubles frames -> visible judder. Over-sampling ~2x on 60 Hz panels
    /// masks that beat; high-refresh panels (>=90 Hz) present fast enough to run at
    /// their native rate. Loop animations run at 60 fps.
    ///
    /// Low: caps everything for minimal resource use — 60 fps motion, slow/loop
    /// animations throttled to 30 fps.</summary>
    internal static void ApplyPerformanceMode(Models.PerformanceMode mode)
    {
        int hz = (int)Math.Clamp(GetPrimaryRefreshRate(), 60, 240);
        int animHz;
        if (mode == Models.PerformanceMode.High)
        {
            animHz = hz < 90 ? Math.Min(hz * 2, 240) : hz;  // 60->120, 144->144
            AnimationFrameRate = animHz;
            AmbientFrameRate = 60;                 // full loop animations at 60 fps
            GlassLoopFrameRate = Math.Min(60, hz);
        }
        else
        {
            animHz = 60;
            AnimationFrameRate = 60;
            AmbientFrameRate = 30;                 // slow loop animations at 30 fps
            GlassLoopFrameRate = 30;
        }

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
    private RadialWindow? _panel;
    private SettingsWindow? _settings;
    // True from the moment a settings-open is requested until the window is
    // actually shown. During this window the docks are fading out, so the edge
    // poll must NOT re-summon the side dock (which would flash on screen).
    private bool _openingSettings;
    private Forms.NotifyIcon? _tray;
    private LeftDockWindow? _leftDock;
    private DispatcherTimer? _edgePollTimer;

    // Low-level mouse hook that masks the system auto-hide taskbar's reveal
    // trigger under the bottom side-dock's centre-50% activation band. It runs on
    // its OWN dedicated thread with a private message loop, isolated from the WPF
    // UI thread: a low-level hook proc that misses the LowLevelHooksTimeout
    // (~300ms) is silently skipped for that event, and the UI thread can stall
    // that long while compositing the fullscreen glass layer or animating — which
    // is exactly when (other windows open, system busier) the guard would leak.
    private Thread? _guardThread;
    private Thread? _guardPollThread;
    private uint _guardThreadId;
    private volatile bool _guardStop;
    private IntPtr _taskbarGuardHook = IntPtr.Zero;
    private LowLevelMouseProc? _taskbarGuardProc;   // keep alive while hooked
    // Hot-path state, refreshed on the UI thread (edge poll) so the hook proc only
    // reads simple volatile fields and never touches WPF objects across threads.
    private volatile bool _guardActive;       // taskbar auto-hide AND bottom dock

    // Snapshot of every monitor's bounds, refreshed periodically by the poll
    // thread (and once at install). The hot paths use it to decide whether a
    // monitor's bottom edge is a TRUE outer edge (guarded) or has another display
    // adjacent below it (not guarded, so the cursor can cross down). Volatile
    // reference swap keeps reads lock-free.
    private volatile RECT[] _monitorRects = Array.Empty<RECT>();
    private int _lastMonitorScanTick;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global safety net: a tray-resident app must survive an unexpected
        // exception on the UI thread instead of vanishing silently. Log the
        // fault, tell the user, and keep running where it is safe to do so.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Frame-rate / animation profile is applied from config (see
        // ApplyPerformanceMode) once settings are loaded below.

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

        // Apply the saved frame-rate / animation profile before any window opens.
        ApplyPerformanceMode(_config.Settings.PerformanceMode);

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

        _panel = new RadialWindow(_config, Persist);
        _panel.RequestOpenSettings += OpenSettings;
        _panel.Realize();   // realise once (stays shown, fully transparent) to avoid show/hide flicker

        // Second, vertical left-edge dock. Shares the config + persist callback;
        // a desktop shortcut dropped onto it (or a main-dock icon dragged into
        // it) mutates the shared app lists, so refresh the main dock when it does.
        // The left dock mirrors the main dock's resident region (top two rows),
        // so seed that mirror once before the docks build.
        DockSync.MirrorResidentToLeft(_config);
        _leftDock = new LeftDockWindow(_config, Persist);
        _leftDock.MainDockChanged += () => _panel?.RefreshFromConfig();
        // Clicking the Polaris tile in the left dock's running strip toggles the
        // pinned docks (equivalent to Ctrl+4).
        _leftDock.ToggleDocks = TogglePinnedDock;
        _leftDock.Realize();
        // Let the main dock hand an icon to the left dock when dragged onto it.
        _panel.DropToLeftDock = TryDropToLeftDock;
        // Lift the liquid-glass main dock clear of the side dock when the latter
        // is docked at the bottom (so they never overlap).
        _panel.BottomDockReserve = GetBottomDockReserve;
        // Keep the left dock in step when the main dock's resident region changes.
        _panel.AppsChanged = () =>
        {
            if (DockSync.MirrorResidentToLeft(_config))
                _leftDock?.RefreshFromConfig();
        };
        // Keep the left dock visible while a glass icon is being dragged.
        _panel.GlassDragActiveChanged = active => _leftDock?.SetDragActive(active);
        // Retract the left dock together with the main dock (e.g. when launching
        // an app from the main dock hides the panel). Clears every show reason —
        // including the Ctrl+4 "pinned" reason — so a launch always retracts both.
        _panel.PanelDismissed += () => _leftDock?.HideAll();
        StartEdgePoll();
        InstallTaskbarGuard();

        RebuildHook();
        SetupPinnedHooks();

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
                _panel.HidePanel();
                _leftDock?.SetPinnedShown(false);
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
            _panel.HidePanel();
            _leftDock?.SetPinnedShown(false);
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
            _leftDock?.SetPinnedShown(true);   // summon the left dock together
        }
    }

    private void OnHotkeyPressed()
    {
        // The hotkey-summoned dock should replace the settings window, not
        // overlap it.
        CloseSettingsIfOpen();
        SetActiveMonitorFromCursor();
        _panel?.ShowPanel();
        _leftDock?.SetMainShown(true);   // the left dock summons together with the main dock
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
        // Releasing the hotkey drops the "shown by main" reason; the left dock
        // stays only if the mouse is currently over its edge trigger (handled by
        // the edge poll, which sets the edge-shown reason).
        _leftDock?.SetMainShown(false);
    }

    /// <summary>Polls the cursor position so the left dock appears when the mouse
    /// reaches the left-centre screen edge and hides when it leaves the dock (and
    /// the trigger zone). Runs only the cheap GetCursorPos call on each tick.</summary>
    private void StartEdgePoll()
    {
        _edgePollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _edgePollTimer.Tick += (_, _) =>
        {
            if (_leftDock == null)
                return;
            // Refresh the cached state the mouse-guard hook reads (one cheap shell
            // query per tick, off the hot mouse path): only mask the taskbar when
            // it is auto-hide AND the side dock is anchored to the bottom.
            try
            {
                var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
                bool autoHide =
                    ((long)SHAppBarMessage(ABM_GETSTATE, ref abd) & ABS_AUTOHIDE) != 0;
                _guardActive = autoHide
                    && _leftDock.DockSidePosition == DockSide.Bottom;
            }
            catch { _guardActive = false; }
            // Suppress the left-edge auto-summon while the settings window is
            // open (or in the middle of opening), so the dock cannot re-appear
            // over it or flash during the dock fade-out.
            if (_settings != null || _openingSettings)
            {
                _leftDock.SetEdgeShown(false);
                return;
            }
            // Don't let the mouse summon the dock over a full-screen / borderless
            // app (typically a game running full-screen): the dock must not intrude.
            if (IsFullscreenForeground())
            {
                _leftDock.SetEdgeShown(false);
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
            DockSide side = _leftDock.DockSidePosition;
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
            if (_leftDock.DockVisible)
            {
                Rect b = _leftDock.GetDockScreenBounds();
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

            _leftDock.SetEdgeShown(inTrigger || inDock);
        };
        _edgePollTimer.Start();
    }

    /// <summary>
    /// Starts the dedicated thread that installs the low-level mouse hook masking
    /// the system auto-hide taskbar's reveal trigger under the bottom side-dock's
    /// centre-50% activation band. Windows offers no regional way to suppress the
    /// taskbar reveal, but the auto-hide bar only slides up when the cursor
    /// reaches the monitor's bottom-edge pixels; holding the cursor a few rows
    /// above that edge across the dock's centre band (and swallowing the edge
    /// move) lets the dock pop there while the taskbar stays hidden. The outer
    /// 50% at each end is untouched, so the taskbar can still be summoned there.
    /// The hook lives on its own thread + message loop so a busy UI thread can
    /// never make it miss the low-level-hook timeout and leak the edge event.
    /// </summary>
    private void InstallTaskbarGuard()
    {
        if (_guardThread != null)
            return;
        _guardStop = false;
        // Seed the monitor layout before the hook starts so the very first mouse
        // moves are evaluated against a valid snapshot.
        RefreshMonitorSnapshot();
        _guardThread = new Thread(TaskbarGuardThread)
        {
            IsBackground = true,
            Name = "PolarisTaskbarGuard",
        };
        _guardThread.SetApartmentState(ApartmentState.STA);
        _guardThread.Start();

        // A low-level hook is silently bypassed while an ELEVATED (admin) window
        // is in the foreground (UIPI) — e.g. an admin Terminal — so the clamp
        // would leak there. GetCursorPos/SetCursorPos are NOT UIPI-gated (they act
        // on the global cursor, not a window), so a parallel polling clamp covers
        // exactly that gap without elevating Polaris (which would break drag-add
        // from Explorer and launch every app elevated).
        _guardPollThread = new Thread(TaskbarGuardPollThread)
        {
            IsBackground = true,
            Name = "PolarisTaskbarGuardPoll",
        };
        _guardPollThread.Start();
    }

    private void TaskbarGuardPollThread()
    {
        bool clipped = false;
        void Release()
        {
            if (clipped)
            {
                ClipCursor(IntPtr.Zero);
                clipped = false;
            }
        }

        while (!_guardStop)
        {
            if (!_guardActive)
            {
                Release();
                Thread.Sleep(50);
                continue;
            }
            try
            {
                // Refresh the monitor layout at most ~once a second so display
                // hot-plug / rearrangement is reflected without per-tick cost.
                int tick = Environment.TickCount;
                if (_monitorRects.Length == 0 || tick - _lastMonitorScanTick >= 1000)
                {
                    RefreshMonitorSnapshot();
                    _lastMonitorScanTick = tick;
                }

                if (GetCursorPos(out POINT pt))
                {
                    IntPtr hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(hMon, ref mi))
                    {
                        // Guard a monitor's bottom edge only when it is a TRUE outer
                        // edge (no display connected below it in the centre band). If
                        // another monitor sits below, leave the edge open so the cursor
                        // can cross down into it.
                        if (!HasNeighborBelow(mi.rcMonitor))
                        {
                            int w = mi.rcMonitor.Right - mi.rcMonitor.Left;
                            double bandStart = mi.rcMonitor.Left + w * 0.25;
                            double bandEnd = mi.rcMonitor.Right - w * 0.25;
                            int floor = mi.rcMonitor.Bottom - TaskbarGuardRows;
                            // PROACTIVELY confine the cursor above the bottom edge
                            // whenever it is within the centre band's X range. A
                            // reactive SetCursorPos runs only AFTER the cursor has
                            // already touched the edge — too late, the taskbar has
                            // revealed and our bounce-back lands inside the now-shown
                            // bar. ClipCursor makes the bottom rows physically
                            // unreachable in the band, so the edge is never touched
                            // and the bar never reveals; it is not UIPI-gated, so it
                            // works even when an elevated window is in the
                            // foreground. The clip is full monitor width (only the
                            // bottom is cut) so horizontal motion stays free, and it
                            // is released the moment the cursor leaves the band so
                            // the outer 50% can still summon the taskbar.
                            //
                            // The clip's TOP is the virtual-screen top, not this
                            // monitor's top: a single ClipCursor rect would otherwise
                            // trap the cursor against the primary monitor's top edge
                            // in the centre band, blocking it from crossing UP into a
                            // monitor positioned above the primary. Extending the top
                            // to the whole virtual desktop leaves the upward path free
                            // (the cursor is still kept on real display surface) while
                            // only the bottom edge stays clamped.
                            if (pt.X >= bandStart && pt.X <= bandEnd)
                            {
                                const int SM_YVIRTUALSCREEN = 77;
                                var clip = new RECT
                                {
                                    Left = mi.rcMonitor.Left,
                                    Top = GetSystemMetrics(SM_YVIRTUALSCREEN),
                                    Right = mi.rcMonitor.Right,
                                    Bottom = floor,
                                };
                                ClipCursor(ref clip);
                                clipped = true;
                            }
                            else
                            {
                                Release();
                            }
                        }
                        else
                        {
                            Release();
                        }
                    }
                }
            }
            catch { }
            Thread.Sleep(10);
        }
        Release();
    }

    private void TaskbarGuardThread()
    {
        _guardThreadId = GetCurrentThreadId();
        _taskbarGuardProc = TaskbarGuardProc;   // keep alive while hooked
        IntPtr hMod = GetModuleHandle(null);
        _taskbarGuardHook = SetWindowsHookEx(WH_MOUSE_LL, _taskbarGuardProc, hMod, 0);
        if (_taskbarGuardHook == IntPtr.Zero)
            return;

        // Pump this thread's message queue so the hook callback is dispatched
        // independently of the WPF UI thread. WM_QUIT (posted on shutdown) ends it.
        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnhookWindowsHookEx(_taskbarGuardHook);
        _taskbarGuardHook = IntPtr.Zero;
        _taskbarGuardProc = null;
    }

    private IntPtr TaskbarGuardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == WM_MOUSEMOVE && _guardActive)
        {
            try
            {
                // Read only the fields we need straight from the struct (x@0, y@4,
                // flags@12) to keep the hot path allocation-free and fast.
                int px = Marshal.ReadInt32(lParam, 0);
                int py = Marshal.ReadInt32(lParam, 4);
                uint flags = (uint)Marshal.ReadInt32(lParam, 12);
                // Skip our own SetCursorPos-injected moves (prevents recursion).
                if ((flags & LLMHF_INJECTED) == 0)
                {
                    var pt = new POINT { X = px, Y = py };
                    IntPtr hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(hMon, ref mi))
                    {
                        // Mask the taskbar reveal only on a TRUE outer bottom edge.
                        // If another display is connected below this monitor, its
                        // bottom edge is left open so the cursor can cross down.
                        if (!HasNeighborBelow(mi.rcMonitor))
                        {
                            int w = mi.rcMonitor.Right - mi.rcMonitor.Left;
                            double bandStart = mi.rcMonitor.Left + w * 0.25;
                            double bandEnd = mi.rcMonitor.Right - w * 0.25;
                            // Hold the cursor clear of the very edge across the
                            // centre band, and SWALLOW the original edge event
                            // (return non-zero): on a fast flick a single move
                            // lands directly at the edge, and if the shell sees
                            // that coordinate it reveals the taskbar before our
                            // reposition registers. Discarding it means the shell
                            // only ever sees our injected, clamped position.
                            if (px >= bandStart && px <= bandEnd
                                && py >= mi.rcMonitor.Bottom - TaskbarGuardRows)
                            {
                                SetCursorPos(px, mi.rcMonitor.Bottom - TaskbarGuardRows);
                                return (IntPtr)1;
                            }
                        }
                    }
                }
            }
            catch { }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void RemoveTaskbarGuard()
    {
        _guardStop = true;
        var pt = _guardPollThread;
        var t = _guardThread;
        if (t == null && pt == null)
            return;
        if (_guardThreadId != 0)
            PostThreadMessage(_guardThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        t?.Join(500);
        pt?.Join(500);
        _guardThread = null;
        _guardPollThread = null;
        _guardThreadId = 0;
    }

    /// <summary>Re-reads the current monitor layout into <see cref="_monitorRects"/>.
    /// Cheap (a handful of monitors) and called at most ~once a second from the poll
    /// loop, plus once at install, so display hot-plug / rearrangement is picked up.</summary>
    private void RefreshMonitorSnapshot()
    {
        var rects = new System.Collections.Generic.List<RECT>(4);
        bool Collect(IntPtr hMon, IntPtr hdc, ref RECT lprc, IntPtr data)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMon, ref mi))
                rects.Add(mi.rcMonitor);
            return true;
        }
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Collect, IntPtr.Zero);
            _monitorRects = rects.ToArray();
        }
        catch { }
    }

    /// <summary>True when another display sits directly below <paramref name="m"/>'s
    /// bottom edge and overlaps the guarded centre band — i.e. the cursor should be
    /// allowed to cross downward there, so no taskbar-guard wall is placed. False for
    /// a true outer bottom edge (nothing below in the band), which IS guarded.</summary>
    private bool HasNeighborBelow(in RECT m)
    {
        int w = m.Right - m.Left;
        double bandStart = m.Left + w * 0.25;
        double bandEnd = m.Right - w * 0.25;
        const int tol = 2;   // tolerate a 1-2px seam between adjacent monitors
        var rects = _monitorRects;
        foreach (var r in rects)
        {
            // A monitor whose TOP meets this monitor's BOTTOM (self never matches,
            // since its own Top != its own Bottom) and whose horizontal span covers
            // any part of the guarded band counts as "connected below".
            if (Math.Abs(r.Top - m.Bottom) <= tol && r.Right > bandStart && r.Left < bandEnd)
                return true;
        }
        return false;
    }

    private const int WH_MOUSE_LL = 14;
    // Pixel rows above the monitor's bottom edge that the cursor is held clear of
    // inside the centre band, so the auto-hide taskbar's reveal tolerance never
    // fires there. Stays below the dock's edge reach so the dock still pops.
    private const int TaskbarGuardRows = 3;
    private const int WM_MOUSEMOVE = 0x0200;
    private const uint LLMHF_INJECTED = 0x00000001;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint MONITORINFOF_PRIMARY = 1;
    private const uint ABM_GETSTATE = 0x00000004;
    private const long ABS_AUTOHIDE = 0x0000001;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(ref RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(IntPtr lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprc, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    private const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Adds the dragged main-dock entry to the left dock if the drop
    /// point lands over the left dock. Returns true when it was added there.</summary>
    private bool TryDropToLeftDock(Point screenPoint, AppEntry entry)
    {
        // screenPoint is in DEVICE pixels (from Visual.PointToScreen). Let the
        // left dock convert it through its own PointFromScreen so the hit test is
        // DPI-correct without any manual scale math.
        return _leftDock?.TryAcceptDrop(screenPoint, entry) == true;
    }

    /// <summary>Height (DIP, measured up from the bottom screen edge) that the
    /// side dock occupies when it is docked at the BOTTOM, plus a small gap, so
    /// the liquid-glass main dock can lift itself clear of it. Returns 0 when the
    /// side dock is on any other edge.</summary>
    private double GetBottomDockReserve()
    {
        if (_leftDock == null || _leftDock.DockSidePosition != DockSide.Bottom)
            return 0.0;
        Rect b = _leftDock.GetDockScreenBounds();
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
        catch { /* fall through to the geometry test */ }

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
        _leftDock?.HideAll();   // dismiss the left dock too, not just the main dock
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
            // The performance profile may have changed; re-apply the frame-rate
            // fields so loop / transition animations re-created in the refresh
            // below tick at the new profile's rates.
            ApplyPerformanceMode(_config.Settings.PerformanceMode);
            // A theme switch can change the resident count (Ring0Count is stored
            // per theme), so re-mirror the resident region into the side dock
            // before relaying it — otherwise the side dock keeps the old theme's
            // icon count.
            DockSync.MirrorResidentToLeft(_config);
            // Re-anchor / re-lay the side dock FIRST so a changed dock position
            // (or any geometry-affecting setting) takes effect immediately and
            // its new bounds are available to the main dock below.
            _leftDock?.RefreshLayout();
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
        RemoveTaskbarGuard();
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
        RemoveTaskbarGuard();
        _tray?.Dispose();
        _hook?.Dispose();
        _pinnedHook?.Dispose();
        _escHook?.Dispose();
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
                "详情已记录到日志：\n" + LogPath + "\n\n" + ex.Message,
                "Polaris",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Warning);
        }
        catch
        {
            // Never let the notice itself throw.
        }
    }

    private static readonly string LogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Polaris", "errors.log");

    private static void LogException(string source, Exception ex)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir))
                System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(LogPath,
                $"[{DateTime.Now:o}] ({source}) {ex}\n\n");
        }
        catch
        {
            // Logging must never throw.
        }
    }
}
