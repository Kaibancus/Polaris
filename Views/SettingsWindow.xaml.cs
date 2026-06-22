using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using Polaris.Models;
using Polaris.Services;

namespace Polaris.Views;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly Action _persist;
    private bool _loaded;

    /// <summary>Raised whenever a setting changes, so an open panel can refresh.</summary>
    public event Action? Changed;

    /// <summary>Raised when the trigger key changes, so the hook can be reinstalled.</summary>
    public event Action? TriggerKeyChanged;

    /// <summary>Raised when the toggle key changes, so the Ctrl+digit hook is reinstalled.</summary>
    public event Action? ToggleKeyChanged;

    /// <summary>Selectable trigger keys: display name + virtual-key code.</summary>
    private static readonly (string Name, int Vk)[] TriggerKeyOptions =
    {
        ("右 Alt", 0xA5),
        ("右 Ctrl", 0xA3),
        ("右 Shift", 0xA1),
        ("Caps Lock 大写锁", 0x14),
        ("左 Win", 0x5B),
        ("Scroll Lock", 0x91),
        ("Pause 暂停", 0x13),
        ("F9", 0x78),
        ("F10", 0x79),
        ("F12", 0x7B),
    };

    /// <summary>Selectable toggle hotkeys: Ctrl+0 .. Ctrl+9 (digit virtual-key 0x30..0x39).</summary>
    private static readonly (string Name, int Vk)[] ToggleKeyOptions =
        Enumerable.Range(0, 10).Select(d => ($"Ctrl+{d}", 0x30 + d)).ToArray();


    public SettingsWindow(AppConfig config, Action persist)
    {
        _config = config;
        _persist = persist;
        InitializeComponent();
        ApplyColorMode();

        LoadSettingsIntoUi();
        _loaded = true;
    }

    /// <summary>Fills the window's brush resources from a light or dark palette
    /// chosen by the current Windows app theme, so the settings window matches
    /// the system appearance. The XAML references every colour via
    /// DynamicResource, so overwriting the keys here restyles the whole window
    /// (background, cards, fields, text, accent) at construction time.</summary>
    private void ApplyColorMode()
    {
        bool light = Polaris.Services.SystemTheme.IsLight;
        void Set(string key, string hex) =>
            Resources[key] = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(hex));

        if (light)
        {
            Set("WindowBg",        "#FFF3F3F6");
            Set("CardBrush",       "#FFFFFFFF");
            Set("FieldBrush",      "#FFECECEF");
            Set("FieldHoverBrush", "#FFE2E2E8");
            Set("LineBrush",       "#FFD7D7DE");
            Set("TextBrush",       "#FF1B1B1F");
            Set("SubtleBrush",     "#FF6A6A73");
            Set("AccentBrush",     "#FF3D6FE0");
            Set("AccentHoverBrush", "#FF2F5FD0");
        }
        else
        {
            Set("WindowBg",        "#FF1B1B20");
            Set("CardBrush",       "#FF26262E");
            Set("FieldBrush",      "#FF32323C");
            Set("FieldHoverBrush", "#FF3D3D49");
            Set("LineBrush",       "#FF3A3A46");
            Set("TextBrush",       "#FFECECEC");
            Set("SubtleBrush",     "#FF9A9AA6");
            Set("AccentBrush",     "#FF5B8CFF");
            Set("AccentHoverBrush", "#FF6F9BFF");
        }
    }

    private void LoadSettingsIntoUi()
    {
        var s = _config.Settings;
        TransparencySlider.Value = s.PanelTransparency;
        IconSizeSlider.Value = s.IconSize;
        FontSizeSlider.Value = s.FontSizePercent;
        UpdateSliderLabels();
        StartupCheck.IsChecked = StartupManager.IsEnabled() || s.RunAtStartup;

        foreach (var theme in ThemeRegistry.All)
            ThemeCombo.Items.Add(new ComboBoxItem { Content = theme.DisplayName, Tag = theme.Id });
        SelectTheme(s.Theme);

        foreach (var opt in TriggerKeyOptions)
            TriggerKeyCombo.Items.Add(new ComboBoxItem { Content = opt.Name, Tag = opt.Vk });
        SelectTriggerKey(s.TriggerKey);

        foreach (var opt in ToggleKeyOptions)
            ToggleKeyCombo.Items.Add(new ComboBoxItem { Content = opt.Name, Tag = opt.Vk });
        SelectToggleKey(s.ToggleKey);

        DockPositionCombo.Items.Add(new ComboBoxItem { Content = "左侧", Tag = DockSide.Left });
        DockPositionCombo.Items.Add(new ComboBoxItem { Content = "右侧", Tag = DockSide.Right });
        DockPositionCombo.Items.Add(new ComboBoxItem { Content = "顶部", Tag = DockSide.Top });
        DockPositionCombo.Items.Add(new ComboBoxItem { Content = "底部", Tag = DockSide.Bottom });
        SelectDockPosition(s.DockPosition);
        MultiMonitorCheck.IsChecked = s.DockOnAllMonitors;
    }

    private void SelectDockPosition(DockSide side)
    {
        foreach (ComboBoxItem item in DockPositionCombo.Items)
        {
            if (item.Tag is DockSide d && d == side)
            {
                DockPositionCombo.SelectedItem = item;
                return;
            }
        }
        if (DockPositionCombo.Items.Count > 0)
            DockPositionCombo.SelectedIndex = 0;
    }

    private void SelectTheme(string id)
    {
        foreach (ComboBoxItem item in ThemeCombo.Items)
        {
            if (item.Tag is string tid &&
                string.Equals(tid, id, StringComparison.OrdinalIgnoreCase))
            {
                ThemeCombo.SelectedItem = item;
                return;
            }
        }
        if (ThemeCombo.Items.Count > 0)
            ThemeCombo.SelectedIndex = 0;
    }

    private void SelectTriggerKey(int vk)
    {
        foreach (ComboBoxItem item in TriggerKeyCombo.Items)
        {
            if (item.Tag is int v && v == vk)
            {
                TriggerKeyCombo.SelectedItem = item;
                return;
            }
        }
        // Unknown stored key: default to first option (Right Alt).
        if (TriggerKeyCombo.Items.Count > 0)
            TriggerKeyCombo.SelectedIndex = 0;
    }

    private void SelectToggleKey(int vk)
    {
        foreach (ComboBoxItem item in ToggleKeyCombo.Items)
        {
            if (item.Tag is int v && v == vk)
            {
                ToggleKeyCombo.SelectedItem = item;
                return;
            }
        }
        // Unknown stored key: default to Ctrl+4 (the fifth item, 0x34).
        foreach (ComboBoxItem item in ToggleKeyCombo.Items)
        {
            if (item.Tag is int v && v == 0x34)
            {
                ToggleKeyCombo.SelectedItem = item;
                return;
            }
        }
        if (ToggleKeyCombo.Items.Count > 0)
            ToggleKeyCombo.SelectedIndex = 0;
    }

    private void CommitSettings()
    {
        var s = _config.Settings;
        s.PanelTransparency = TransparencySlider.Value;
        s.IconSize = IconSizeSlider.Value;
        s.FontSizePercent = FontSizeSlider.Value;
        Services.FontScale.SetFromPercent(s.FontSizePercent);
        // Remember these values for the current theme so switching back restores them.
        ThemeRegistry.SaveAppearance(s);
        _persist();
        Changed?.Invoke();
    }

    // ---- Settings operations ---------------------------------------------

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        UpdateSliderLabels();
        if (!_loaded)
            return;
        // A slider drag fires ValueChanged every pixel, and CommitSettings writes
        // config.json to disk + refreshes the dock. Persisting on every tick janks
        // the drag, so coalesce the commit to ~140 ms after the last change (the %
        // label above still updates live). Click-to-point (IsMoveToPointEnabled)
        // and final values are flushed on close.
        _commitTimer ??= NewCommitTimer();
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _commitTimer;

    private System.Windows.Threading.DispatcherTimer NewCommitTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        t.Tick += (_, _) => { _commitTimer!.Stop(); CommitSettings(); };
        return t;
    }

    /// <summary>Appends the live percentage to the transparency / icon-size
    /// slider labels. Transparency is shown directly as a percentage; icon size
    /// as a percentage of its adjustable range.</summary>
    private void UpdateSliderLabels()
    {
        // Slider value coercion during XAML parse can fire ValueChanged before
        // every named element exists; bail until the tree is fully built.
        if (!IsInitialized)
            return;
        int transPct = (int)Math.Round(TransparencySlider.Value * 100.0);
        double iRange = IconSizeSlider.Maximum - IconSizeSlider.Minimum;
        int iconPct = iRange > 0
            ? (int)Math.Round((IconSizeSlider.Value - IconSizeSlider.Minimum) / iRange * 100.0)
            : 0;
        TransparencyLabel.Text = $"面板透明度 {transPct}%";
        IconSizeLabel.Text = $"图标大小 {iconPct}%";
        FontSizeLabel.Text = $"字体大小 {(int)Math.Round(FontSizeSlider.Value)}%";
    }

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
            return;
        bool enabled = StartupCheck.IsChecked == true;
        StartupManager.SetEnabled(enabled);
        _config.Settings.RunAtStartup = enabled;
        _persist();
    }

    private void OnTriggerKeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded)
            return;
        if (TriggerKeyCombo.SelectedItem is ComboBoxItem item && item.Tag is int vk)
        {
            _config.Settings.TriggerKey = vk;
            _persist();
            TriggerKeyChanged?.Invoke();
        }
    }

    private void OnToggleKeyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded)
            return;
        if (ToggleKeyCombo.SelectedItem is ComboBoxItem item && item.Tag is int vk)
        {
            _config.Settings.ToggleKey = vk;
            _persist();
            ToggleKeyChanged?.Invoke();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnDockPositionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded)
            return;
        if (DockPositionCombo.SelectedItem is ComboBoxItem item && item.Tag is DockSide side)
        {
            _config.Settings.DockPosition = side;
            _persist();
            Changed?.Invoke();
        }
    }

    private void OnMultiMonitorChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
            return;
        _config.Settings.DockOnAllMonitors = MultiMonitorCheck.IsChecked == true;
        _persist();
        Changed?.Invoke();
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded)
            return;
        if (ThemeCombo.SelectedItem is ComboBoxItem item && item.Tag is string id)
        {
            var s = _config.Settings;
            // Remember the outgoing theme's appearance, switch, then load the
            // incoming theme's saved values (or its built-in defaults).
            ThemeRegistry.SaveAppearance(s);
            s.Theme = id;
            ThemeRegistry.LoadAppearance(s);

            // Reflect the loaded values in the sliders without re-committing.
            _loaded = false;
            TransparencySlider.Value = s.PanelTransparency;
            IconSizeSlider.Value = s.IconSize;
            UpdateSliderLabels();
            _loaded = true;

            _persist();
            Changed?.Invoke();
        }
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        string originalText = CheckUpdateButton.Content?.ToString() ?? "检查更新";
        CheckUpdateButton.Content = "检查中…";
        try
        {
            UpdateService.ReleaseInfo? latest;
            try
            {
                latest = await UpdateService.GetLatestReleaseAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "检查更新失败：" + ex.Message, "检查更新",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (latest == null)
            {
                MessageBox.Show(this, "无法获取更新信息，请稍后重试。", "检查更新",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!UpdateService.IsNewer(latest))
            {
                MessageBox.Show(this,
                    $"当前已是最新版本（v{UpdateService.CurrentVersion.ToString(3)}）。",
                    "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var choice = MessageBox.Show(this,
                $"发现新版本：{latest.TagName}\n当前版本：v{UpdateService.CurrentVersion.ToString(3)}\n\n是否现在下载并更新？",
                "检查更新", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (choice != MessageBoxResult.Yes)
                return;

            if (string.IsNullOrEmpty(latest.ZipAssetUrl))
            {
                // No downloadable asset — open the release page so the user can
                // grab it manually.
                if (!string.IsNullOrEmpty(latest.HtmlUrl))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = latest.HtmlUrl,
                        UseShellExecute = true,
                    });
                }
                return;
            }

            CheckUpdateButton.Content = "下载中…";
            bool ok;
            try
            {
                ok = await UpdateService.DownloadAndApplyAsync(latest);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "下载或安装更新失败：" + ex.Message, "检查更新",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!ok)
            {
                MessageBox.Show(this, "更新失败，请前往发布页手动下载。", "检查更新",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(this,
                "更新已就绪，应用将关闭并自动重启为新版本。",
                "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
            Application.Current.Shutdown();
        }
        finally
        {
            CheckUpdateButton.Content = originalText;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    // --- First-frame white-flash suppression via DWM cloaking ---------------
    // A freshly created top-level WPF window has DWM paint one frame with the
    // window-class (white) background brush BEFORE the WPF render thread submits
    // its first dark frame — Opacity=0 and off-screen positioning cannot hide it
    // because the flash happens at the DWM composition layer. Cloaking the window
    // (the same mechanism Visual Studio / Office use) keeps it fully invisible
    // until the host calls Uncloak() after the content has rendered.
    private const int DWMWA_CLOAK = 13;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref on, sizeof(int));
    }

    /// <summary>Reveals the window once its content has rendered, so the white
    /// first frame is never shown.</summary>
    public void Uncloak()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int off = 0;
        DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref off, sizeof(int));
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Flush a pending debounced slider commit so a value changed right before
        // closing the window is not lost.
        if (_commitTimer is { IsEnabled: true })
        {
            _commitTimer.Stop();
            CommitSettings();
        }
        base.OnClosing(e);
    }
}
