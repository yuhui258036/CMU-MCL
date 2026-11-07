using System.IO;
using System.Windows;
using System.Windows.Controls;
using CMUmcl.Appearance;
using CMUmcl.Content;
using CMUmcl.Launch;

namespace CMUmcl;

public partial class MainWindow
{
    private InstanceSettingsView? _globalSettingsInline;
    private void EnsureInlineGameSettings()
    {
        if(_globalSettingsInline==null)
        {
            _globalSettingsInline=new InstanceSettingsView(LauncherPaths.InstancesDirectory,"全局游戏设置",_settingsStore,true,inlineGlobal:true);
            // Autosave fires often; game defaults only change the settings copy, not instances or community targets.
            _globalSettingsInline.SettingsApplied+=()=>{if(!_windowClosed)_settings=_settingsStore.Load();};
            GameDefaultsCard.Content=_globalSettingsInline;
        }
        else _globalSettingsInline.RefreshGlobalIfUnchanged();
    }
    private bool _loadingPreferences;

    private void LoadPreferenceControls()
    {
        ThemeCombo.SelectedIndex = (int)_settings.Theme;
        BackgroundModeCombo.SelectedIndex = (int)(_settings.BackgroundMode ?? BackgroundKind.Default);
        BackgroundUrlTextBox.Text = _settings.BackgroundUrl;
        BackgroundOpacitySlider.Value = _settings.BackgroundOpacity * 100;
        BackgroundAutoSwitchCheck.IsChecked = _settings.BackgroundAutoSwitch;
        BackgroundShuffleCheck.IsChecked = _settings.BackgroundShuffle;
        BackgroundIntervalTextBox.Text = _settings.BackgroundIntervalSeconds.ToString();
        RefreshBackgroundControls();
        LoadProxyControls();
    }

    private void SettingsTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) ShowSettingsTab(button);
    }

    private void ShowSettingsTab(Button button)
    {
        string tab = button.Name switch { "AppearanceTab" => "Appearance", "ProxyTab" => "Proxy", "StorageTab" => "Storage", "UpdateTab" => "Update", _ => "Runtime" };
        if(tab=="Runtime")EnsureInlineGameSettings();
        foreach (var (element, group) in new (FrameworkElement, string)[] {
            (GameDefaultsCard,"Runtime"),(AppearanceCard,"Appearance"),
            (IsolationCard,"Storage"),(DirectoryCard,"Storage"),(InstanceDirectoryCard,"Storage"),
            (PackUpdateCard,"Update"),(LauncherUpdateCard,"Update"),(ProxyCard,"Proxy") })
            element.Visibility = group == tab ? Visibility.Visible : Visibility.Collapsed;
        foreach (Button item in new[] { RuntimeTab, AppearanceTab, ProxyTab, StorageTab, UpdateTab })
            item.Style = (Style)FindResource(item == button ? "NavItemActive" : "NavItem");
        SettingsContentScroll.ScrollToTop();
    }

    private void Appearance_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo.SelectedIndex >= 0) SavePreference(s => s.Theme = (LauncherTheme)ThemeCombo.SelectedIndex);
    }
    private void BackgroundOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BackgroundOpacityText == null) return;
        BackgroundOpacityText.Text = $"{BackgroundOpacitySlider.Value:F0}%";
        SavePreference(s => s.BackgroundOpacity = BackgroundOpacitySlider.Value / 100);
    }
    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        // Changing appearance must not change page, tab, selection, or scroll position.
        ThemeCombo.SelectedIndex = (int)(AppearanceManager.IsDark ? LauncherTheme.Light : LauncherTheme.Dark);
    }
    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();
}
