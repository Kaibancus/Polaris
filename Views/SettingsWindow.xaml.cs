using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Polaris.Models;
using Polaris.Services;

namespace Polaris.Views;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly Action _persist;
    private bool _loaded;

    public ObservableCollection<AppEntry> Apps { get; }

    /// <summary>Raised whenever apps or settings change, so an open panel can refresh.</summary>
    public event Action? Changed;

    /// <summary>Raised when the trigger key changes, so the hook can be reinstalled.</summary>
    public event Action? TriggerKeyChanged;

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


    public SettingsWindow(AppConfig config, Action persist)
    {
        _config = config;
        _persist = persist;
        Apps = new ObservableCollection<AppEntry>(_config.Apps);
        InitializeComponent();

        AppList.ItemsSource = Apps;
        LoadSettingsIntoUi();
        _loaded = true;
    }

    private void LoadSettingsIntoUi()
    {
        var s = _config.Settings;
        TransparencySlider.Value = s.PanelTransparency;
        IconSizeSlider.Value = s.IconSize;
        UpdateSliderLabels();
        StartupCheck.IsChecked = StartupManager.IsEnabled() || s.RunAtStartup;

        foreach (var theme in ThemeRegistry.All)
            ThemeCombo.Items.Add(new ComboBoxItem { Content = theme.DisplayName, Tag = theme.Id });
        SelectTheme(s.Theme);

        foreach (var opt in TriggerKeyOptions)
            TriggerKeyCombo.Items.Add(new ComboBoxItem { Content = opt.Name, Tag = opt.Vk });
        SelectTriggerKey(s.TriggerKey);
        UpdateHint();
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

    private void UpdateHint()
    {
        if (TriggerKeyCombo.SelectedItem is ComboBoxItem item)
            HintText.Text = $"提示：长按 {item.Content} 在屏幕中心呼出圆盘";
    }

    private void CommitApps()
    {
        _config.Apps.Clear();
        _config.Apps.AddRange(Apps);
        _persist();
        Changed?.Invoke();
    }

    private void CommitSettings()
    {
        var s = _config.Settings;
        s.PanelTransparency = TransparencySlider.Value;
        s.IconSize = IconSizeSlider.Value;
        // Remember these values for the current theme so switching back restores them.
        ThemeRegistry.SaveAppearance(s);
        _persist();
        Changed?.Invoke();
    }

    // ---- App list operations ---------------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = (e.Data.GetDataPresent(DataFormats.FileDrop) || ShellNamespace.HasShellItems(e.Data))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropFiles(object sender, DragEventArgs e)
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

        int cap = ThemeRegistry.Get(_config.Settings.Theme).MaxIcons;
        bool rejected = false;
        foreach (var entry in entries)
        {
            if (Apps.Count >= cap)
            {
                rejected = true;
                continue;
            }
            Apps.Add(entry);
        }
        CommitApps();
        if (rejected)
        {
            MessageBox.Show(
                $"当前主题最多只能放置 {cap} 个图标，部分图标未添加。",
                "已达图标上限",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is AppEntry entry)
        {
            Apps.Remove(entry);
            CommitApps();
        }
    }

    private void OnMoveUp(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is AppEntry entry)
        {
            int i = Apps.IndexOf(entry);
            if (i > 0)
            {
                Apps.Move(i, i - 1);
                CommitApps();
            }
        }
    }

    private void OnMoveDown(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is AppEntry entry)
        {
            int i = Apps.IndexOf(entry);
            if (i >= 0 && i < Apps.Count - 1)
            {
                Apps.Move(i, i + 1);
                CommitApps();
            }
        }
    }

    // ---- Settings operations ---------------------------------------------

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        UpdateSliderLabels();
        if (_loaded)
            CommitSettings();
    }

    /// <summary>Appends the live percentage to the transparency / icon-size
    /// slider labels. Transparency is shown directly as a percentage; icon size
    /// as a percentage of its adjustable range.</summary>
    private void UpdateSliderLabels()
    {
        int transPct = (int)Math.Round(TransparencySlider.Value * 100.0);
        double iRange = IconSizeSlider.Maximum - IconSizeSlider.Minimum;
        int iconPct = iRange > 0
            ? (int)Math.Round((IconSizeSlider.Value - IconSizeSlider.Minimum) / iRange * 100.0)
            : 0;
        TransparencyLabel.Text = $"面板透明度 {transPct}%";
        IconSizeLabel.Text = $"图标大小 {iconPct}%";
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
            UpdateHint();
            TriggerKeyChanged?.Invoke();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

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
}
