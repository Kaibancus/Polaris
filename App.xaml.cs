using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DesktopPanel.Interop;
using DesktopPanel.Models;
using DesktopPanel.Services;
using DesktopPanel.Views;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace DesktopPanel;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    private AppConfig _config = new();
    private KeyboardHook? _hook;
    private KeyboardHook? _pinnedHook;
    private KeyboardHook? _escHook;
    private DispatcherTimer? _capsHoldTimer;
    private RadialWindow? _panel;
    private SettingsWindow? _settings;
    private Forms.NotifyIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Render WPF animations at a high steady frame rate. The default
        // metadata caps interpolation sampling; bumping DesiredFrameRate
        // globally makes short transitions (hover scale, neighbour spread)
        // feel smooth instead of "slow-motion".
        System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
            typeof(System.Windows.Media.Animation.Timeline),
            new FrameworkPropertyMetadata(120));

        // Single-instance guard: if another DesktopPanel is already running,
        // notify the user and exit immediately.
        _singleInstanceMutex = new Mutex(true, @"Global\DesktopPanel_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Forms.MessageBox.Show(
                "DesktopPanel 已在运行（查看系统托盘图标）。",
                "DesktopPanel",
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

        _panel = new RadialWindow(_config, Persist);
        _panel.RequestOpenSettings += OpenSettings;
        _panel.Realize();   // realise once (stays shown, fully transparent) to avoid show/hide flicker

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

    private void Persist() => ConfigStore.Save(_config);

    /// <summary>
    /// Dedicated global hotkeys for the pinned (drag-to-add) panel:
    /// hold Caps Lock to summon it, press Esc to dismiss it. Caps is NOT
    /// swallowed, so a quick tap still toggles Caps Lock normally; only a
    /// deliberate long press (>= the hold delay) pops the panel, and we undo the
    /// single toggle that the physical key-down caused so Caps state is unchanged.
    /// </summary>
    private void SetupPinnedHooks()
    {
        const int VK_CAPITAL = 0x14;
        const int VK_ESCAPE = 0x1B;

        // Caps must be held this long before the pinned panel pops up.
        _capsHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _capsHoldTimer.Tick += (_, _) =>
        {
            _capsHoldTimer!.Stop();
            // The physical key-down already toggled Caps Lock; flip it back so a
            // long hold leaves the Caps state untouched.
            ToggleCapsLock();
            _panel?.ShowPinned();
        };

        _pinnedHook = new KeyboardHook(VK_CAPITAL);
        _pinnedHook.KeyPressed += () =>
        {
            _capsHoldTimer!.Stop();
            _capsHoldTimer.Start();
        };
        _pinnedHook.KeyReleased += () => _capsHoldTimer!.Stop();
        _pinnedHook.Start();

        _escHook = new KeyboardHook(VK_ESCAPE);
        _escHook.KeyPressed += () =>
        {
            if (_panel?.IsShown == true)
                _panel.HidePanel();
        };
        _escHook.Start();
    }

    /// <summary>Sends one Caps Lock keystroke to flip the lock state.</summary>
    private static void ToggleCapsLock()
    {
        const byte VK_CAPITAL = 0x14;
        const uint KEYEVENTF_KEYUP = 0x0002;
        keybd_event(VK_CAPITAL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_CAPITAL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private void OnHotkeyPressed() => _panel?.ShowPanel();
    private void OnHotkeyReleased() => _panel?.HideIfNotPinned();

    private void OpenSettings()
    {
        _panel?.HidePanel();

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
            Text = "DesktopPanel — 长按呼出键临时显示 / 长按Caps固定显示（Esc关闭）",
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
        _tray?.Dispose();
        _hook?.Dispose();
        _pinnedHook?.Dispose();
        _escHook?.Dispose();
        _panel?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _hook?.Dispose();
        _pinnedHook?.Dispose();
        _escHook?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
