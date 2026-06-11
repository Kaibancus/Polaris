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

    private AppConfig _config = new();
    private KeyboardHook? _hook;
    private KeyboardHook? _pinnedHook;
    private KeyboardHook? _escHook;
    private RadialWindow? _panel;
    private SettingsWindow? _settings;
    private Forms.NotifyIcon? _tray;
    private LeftDockWindow? _leftDock;
    private DispatcherTimer? _edgePollTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global safety net: a tray-resident app must survive an unexpected
        // exception on the UI thread instead of vanishing silently. Log the
        // fault, tell the user, and keep running where it is safe to do so.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Render WPF animations at the display's native refresh rate. The
        // default metadata under-samples short transitions (they look "slow
        // motion"), but the choice of value interacts subtly with the display's
        // ACTUAL refresh, which is never an exact integer (a "60 Hz" panel runs
        // at 59.94 Hz). Ticking the timeline at exactly 60 against a 59.94 Hz
        // present makes the two beat against each other, periodically dropping or
        // doubling a frame -> visible judder that reads as a frame-rate drop.
        // Over-sampling at ~2x the refresh puts two animation samples in every
        // presented frame, halving that sampling error and masking the beat, so
        // motion looks markedly smoother on 60 Hz panels. Genuine high-refresh
        // panels (>=90 Hz) already present fast enough that the beat is invisible,
        // so we drive the timeline at their native rate there.
        int hz = (int)Math.Clamp(GetPrimaryRefreshRate(), 60, 240);
        int refreshHz = hz < 90 ? Math.Min(hz * 2, 240) : hz;  // 60->120, 144->144
        System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
            typeof(System.Windows.Media.Animation.Timeline),
            new FrameworkPropertyMetadata(refreshHz));
        AmbientFrameRate = hz;   // un-oversampled present rate for slow loops
        AnimationFrameRate = refreshHz;  // oversampled tick rate for smooth motion
        GlassLoopFrameRate = Math.Min(30, hz);  // throttled loops for the fullscreen glass overlay

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
        _leftDock.Realize();
        // Let the main dock hand an icon to the left dock when dragged onto it.
        _panel.DropToLeftDock = TryDropToLeftDock;
        // Keep the left dock in step when the main dock's resident region changes.
        _panel.AppsChanged = () =>
        {
            if (DockSync.MirrorResidentToLeft(_config))
                _leftDock?.RefreshFromConfig();
        };
        // Keep the left dock visible while a glass icon is being dragged.
        _panel.GlassDragActiveChanged = active => _leftDock?.SetDragActive(active);
        StartEdgePoll();

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
            ConfigStore.Save(_config);
        }
    }

    /// <summary>
    /// Dedicated global hotkeys for the pinned (drag-to-add) panel:
    /// press Ctrl+4 to toggle it (open if hidden, close if shown), or press Esc
    /// to dismiss it. The '4' key is swallowed only while Ctrl is held, so a
    /// normal '4' keystroke is unaffected.
    /// </summary>
    private void SetupPinnedHooks()
    {
        const int VK_4 = 0x34;
        const int VK_ESCAPE = 0x1B;

        // Ctrl+4 toggles the pinned panel: open when hidden, close when shown.
        _pinnedHook = new KeyboardHook(VK_4, suppressKey: true, requireCtrl: true);
        _pinnedHook.KeyPressed += () =>
        {
            if (_panel?.IsShown == true)
            {
                _panel.HidePanel();
                _leftDock?.SetPinnedShown(false);
            }
            else
            {
                _panel?.ShowPinned();
                _leftDock?.SetPinnedShown(true);   // summon the left dock together
            }
        };
        _pinnedHook.Start();

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

    private void OnHotkeyPressed()
    {
        _panel?.ShowPanel();
        _leftDock?.SetMainShown(true);   // the left dock summons together with the main dock
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
            // Suppress the left-edge auto-summon while the settings window is
            // open, so the dock cannot immediately re-appear over it.
            if (_settings != null)
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

            double sh = SystemParameters.PrimaryScreenHeight;
            // Trigger band: a strip at the left edge, vertically the centre 40%
            // of the screen. The threshold is generous (and negative x — i.e. the
            // cursor pushed a touch past the physical left edge, as happens with
            // mouse over-travel or a monitor to the left — also counts) so the
            // dock pops without having to land the pointer exactly on x = 0.
            bool inTrigger = x <= 8.0 && y >= sh * 0.30 && y <= sh * 0.70;

            // Once shown by the edge, keep it shown while the cursor stays near
            // the dock. The right-hand margin is deliberately generous so the
            // dock only retracts once the pointer has moved well clear of it
            // (rather than the instant it leaves the slab).
            bool inDock = false;
            if (_leftDock.DockVisible)
            {
                Rect b = _leftDock.GetDockScreenBounds();
                // No left bound: the dock hugs the screen's left edge, so any
                // cursor position from the edge up to the dock's right edge (plus
                // margin) keeps it shown. Requiring x >= b.Left previously left a
                // few-pixel dead zone between the trigger strip (x <= 3) and the
                // slab, so moving the mouse rightwards into the dock could
                // intermittently retract it.
                inDock = x <= b.Right + 150 &&
                         y >= b.Top - 60 && y <= b.Bottom + 60;
            }

            _leftDock.SetEdgeShown(inTrigger || inDock);
        };
        _edgePollTimer.Start();
    }

    /// <summary>Adds the dragged main-dock entry to the left dock if the drop
    /// point lands over the left dock. Returns true when it was added there.</summary>
    private bool TryDropToLeftDock(Point screenPoint, AppEntry entry)
    {
        // screenPoint is in DEVICE pixels (from Visual.PointToScreen). Let the
        // left dock convert it through its own PointFromScreen so the hit test is
        // DPI-correct without any manual scale math.
        return _leftDock?.TryAcceptDrop(screenPoint, entry) == true;
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
        _panel?.HidePanel();
        _leftDock?.HideAll();   // dismiss the left dock too, not just the main dock
        if (_settings != null)
        {
            _settings.Activate();
            return;
        }

        _settings = new SettingsWindow(_config, Persist);
        _settings.Changed += () =>
        {
            // Re-render the panel so theme / layout / size changes apply live.
            _panel?.RefreshFromConfig();
        };
        _settings.TriggerKeyChanged += RebuildHook;
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
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
            Text = "Polaris — 长按呼出键临时显示 / Ctrl+4 开关固定显示（Esc关闭）",
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
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
