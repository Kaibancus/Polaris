using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DesktopPanel.Models;
using DesktopPanel.Services;

namespace DesktopPanel.Views;

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
        OpacitySlider.Value = s.PanelOpacity;
        IconSizeSlider.Value = s.IconSize;
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
        s.PanelOpacity = OpacitySlider.Value;
        s.IconSize = IconSizeSlider.Value;
        _persist();
        Changed?.Invoke();
    }

    // ---- App list operations ---------------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropFiles(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        foreach (var f in files)
        {
            var entry = ShortcutResolver.CreateEntry(f);
            if (entry != null && !string.IsNullOrWhiteSpace(entry.Path))
                Apps.Add(entry);
        }
        CommitApps();
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
        if (_loaded)
            CommitSettings();
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
            _config.Settings.Theme = id;
            _persist();
            Changed?.Invoke();
        }
    }

    private void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        // TODO: wire up real update-check logic.
        MessageBox.Show(this, "当前已是最新版本。", "检查更新",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
