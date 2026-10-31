using System.IO;
using System.Windows;
using System.Windows.Controls;
using CMUmcl.Appearance;
using CMUmcl.Content;

namespace CMUmcl;

public partial class MainWindow
{
    private void BackgroundMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (BackgroundModeCombo.SelectedIndex < 0 || LocalBackgroundPanel == null) return;
        SavePreference(s => s.BackgroundMode = (BackgroundKind)BackgroundModeCombo.SelectedIndex);
        RefreshBackgroundControls();
    }
    private void RefreshBackgroundControls()
    {
        if (LocalBackgroundPanel == null) return;
        var preferences = _unsavedSettings ?? _settings;
        var kind = (BackgroundKind)Math.Max(0, BackgroundModeCombo.SelectedIndex);
        LocalBackgroundPanel.Visibility = kind == BackgroundKind.Local ? Visibility.Visible : Visibility.Collapsed;
        NetworkBackgroundPanel.Visibility = kind == BackgroundKind.Network ? Visibility.Visible : Visibility.Collapsed;
        BackgroundRotationPanel.Visibility = kind != BackgroundKind.Default ? Visibility.Visible : Visibility.Collapsed;
        BackgroundShuffleCheck.Visibility = kind == BackgroundKind.Local ? Visibility.Visible : Visibility.Collapsed;
        BackgroundImagesList.ItemsSource = preferences.BackgroundImages;
        BackgroundImageCount.Text = preferences.BackgroundImages.Count == 0 ? "未选择图片" : $"已选择 {preferences.BackgroundImages.Count} 张图片";
        BackgroundOpacityText.Text = $"{BackgroundOpacitySlider.Value:F0}%";
        UpdateBackgroundStatus();
    }
    private void BackgroundUrl_Changed(object sender, TextChangedEventArgs e)
    {
        if (BackgroundUrlError == null || _loadingPreferences) return;
        string url = BackgroundUrlTextBox.Text.Trim();
        if (url.Length == 0 || BackgroundLoader.TryGetUri(url, out _))
        {
            BackgroundUrlError.Text = "";
            SavePreference(s => s.BackgroundUrl = url);
        }
        else BackgroundUrlError.Text = "请输入有效的 HTTP 或 HTTPS 图片地址。";
    }
    private void BackgroundRotation_Changed(object sender, RoutedEventArgs e)
    {
        if (BackgroundAutoSwitchCheck == null || BackgroundShuffleCheck == null) return;
        SavePreference(s => { s.BackgroundAutoSwitch = BackgroundAutoSwitchCheck.IsChecked == true; s.BackgroundShuffle = BackgroundShuffleCheck.IsChecked == true; });
    }
    private void BackgroundInterval_Changed(object sender, TextChangedEventArgs e)
    {
        if (BackgroundIntervalError == null || _loadingPreferences) return;
        if (int.TryParse(BackgroundIntervalTextBox.Text, out int seconds) && seconds is >= 10 and <= 3600)
        {
            BackgroundIntervalError.Text = "";
            SavePreference(s => s.BackgroundIntervalSeconds = seconds);
        }
        else BackgroundIntervalError.Text = "切换间隔应为 10～3600 秒。";
    }
    private async void ChooseBackground_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择背景图片", Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true, Multiselect = true };
        if (dialog.ShowDialog(this) == true) await AddBackgroundImagesAsync(dialog.FileNames);
    }
    private async Task AddBackgroundImagesAsync(IEnumerable<string> paths)
    {
        AddBackgroundButton.IsEnabled = false;
        var added = new List<BackgroundImageEntry>();
        var errors = new List<string>();
        try
        {
            int available = 50 - (_unsavedSettings ?? _settings).BackgroundImages.Count;
            foreach (string file in paths.Take(Math.Max(0, available)))
            {
                try
                {
                    string imported = await Task.Run(() => AppearanceManager.ImportBackground(file));
                    if (_windowClosed) return;
                    added.Add(new(imported, Path.GetFileName(file)));
                }
                catch { errors.Add(Path.GetFileName(file)); }
            }
            if (added.Count > 0)
            {
                SavePreference(s => { s.BackgroundImages = s.BackgroundImages.Concat(added).DistinctBy(i => i.Path).Take(50).ToList(); s.BackgroundMode = BackgroundKind.Local; });
                _loadingPreferences = true;
                BackgroundModeCombo.SelectedIndex = (int)BackgroundKind.Local;
                _loadingPreferences = false;
                RefreshBackgroundControls();
            }
            if (available <= 0) BackgroundImportError.Text = "最多选择 50 张背景图片。";
            else BackgroundImportError.Text = errors.Count == 0 ? "" : "无法读取：" + string.Join("、", errors);
        }
        finally { if (!_windowClosed) AddBackgroundButton.IsEnabled = true; }
    }
    private void RemoveBackground_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BackgroundImageEntry entry }) RemoveBackground(entry);
    }
    private void RemoveBackground(BackgroundImageEntry entry)
    {
        SavePreference(s => { s.BackgroundImages.RemoveAll(i => i.Path == entry.Path); });
        RefreshBackgroundControls();
    }
    private async void NextBackground_Click(object sender, RoutedEventArgs e) => await AppearanceManager.NextBackgroundAsync();
    private void UpdateBackgroundStatus()
    {
        if (AppearanceStatus == null || _windowClosed) return;
        AppearanceStatus.Text = AppearanceManager.BackgroundLoading ? "正在加载背景图片…" : AppearanceManager.BackgroundError ?? "";
        ThemeToggleButton.ToolTip = AppearanceManager.IsDark ? "浅色模式" : "深色模式";
        ThemeIcon.Data = (System.Windows.Media.Geometry)FindResource(AppearanceManager.IsDark ? "SunIconGeometry" : "MoonIconGeometry");
    }
}
