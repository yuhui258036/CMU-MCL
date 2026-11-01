using System.IO;
using System.Windows;
using System.Windows.Controls;
using CMUmcl.Appearance;
using CMUmcl.Content;
using CMUmcl.Launch;

namespace CMUmcl;

public partial class MainWindow
{
    private bool _loadingPreferences, _syncMemory, _scanningJava;
    private bool _javaScanStarted;
    private int _javaProbeGeneration;
    private JavaInfo? _detectedJava;
    private string _manualMemoryText = "4096";

    private void LoadPreferenceControls()
    {
        _manualMemoryText = _settings.MaxMemoryMb.ToString();
        MemoryAutoRadio.IsChecked = _settings.AutoMemory;
        MemoryManualRadio.IsChecked = !_settings.AutoMemory;
        MemoryTextBox.Text = _manualMemoryText;
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
        if (sender is not Button button) return;
        string tab = button.Name switch { "AppearanceTab" => "Appearance", "ProxyTab" => "Proxy", "StorageTab" => "Storage", "UpdateTab" => "Update", _ => "Runtime" };
        foreach (var (element, group) in new (FrameworkElement, string)[] {
            (JavaCard,"Runtime"),(MemoryCard,"Runtime"),(AppearanceCard,"Appearance"),
            (IsolationCard,"Storage"),(DirectoryCard,"Storage"),(InstanceDirectoryCard,"Storage"),
            (PackUpdateCard,"Update"),(LauncherUpdateCard,"Update"),(ProxyCard,"Proxy") })
            element.Visibility = group == tab ? Visibility.Visible : Visibility.Collapsed;
        foreach (Button item in new[] { RuntimeTab, AppearanceTab, ProxyTab, StorageTab, UpdateTab })
            item.Style = (Style)FindResource(item == button ? "NavItemActive" : "NavItem");
        SettingsContentScroll.ScrollToTop();
    }

    private async void ScanJava_Click(object sender, RoutedEventArgs e) => await ScanJavaAsync();

    private async Task ScanJavaAsync()
    {
        if (_scanningJava || _windowClosed) return;
        _javaScanStarted = _scanningJava = true;
        ScanJavaButton.IsEnabled = false;
        JavaScanStatus.Text = "正在检测本机 Java…";
        try
        {
            var found = await Task.Run(() => JavaLocator.Scan(_updateLifetime.Token), _updateLifetime.Token);
            if (_windowClosed) return;
            JavaCandidatesCombo.ItemsSource = found.OrderByDescending(j => j.Major).ThenByDescending(j => j.Bits).ToArray();
            JavaScanStatus.Text = found.Count == 0
                ? "未发现本机 Java；自动模式会在启动时尝试下载游戏指定的官方运行时。"
                : $"找到 {found.Count} 个运行时 · 选择后可查看完整路径";
        }
        catch (OperationCanceledException) { }
        catch { if (!_windowClosed) JavaScanStatus.Text = "检测未完成。可以重试，或浏览本机 Java 路径。"; }
        finally { _scanningJava = false; if (!_windowClosed) ScanJavaButton.IsEnabled = true; }
    }

    private void JavaCandidate_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (JavaCandidatesCombo.SelectedItem is not JavaInfo info) return;
        JavaManualRadio.IsChecked = true;
        JavaPathTextBox.Text = info.JavaPath;
        JavaPathTextBox.ToolTip = info.JavaPath;
    }

    private async void UpdateJavaDetectedText()
    {
        if (JavaDetectedText == null || _windowClosed) return;
        int generation = ++_javaProbeGeneration;
        _detectedJava = null;
        if (JavaManualRadio.IsChecked != true)
        {
            JavaDetectedText.Text = "启动时优先选择匹配主版本的本机 Java；缺少时下载官方运行时。";
            UpdateMemoryPreview();
            return;
        }
        string path = JavaPathTextBox.Text.Trim();
        if (path.Length == 0) { JavaDetectedText.Text = "从列表选择，或浏览 Java 可执行文件。"; return; }
        JavaDetectedText.Text = "正在验证 Java…";
        try
        {
            await Task.Delay(350, _updateLifetime.Token);
            if (generation != _javaProbeGeneration) return;
            var info = await Task.Run(() => JavaLocator.DetectExecutable(path), _updateLifetime.Token);
            if (_windowClosed || generation != _javaProbeGeneration) return;
            _detectedJava = info;
            JavaDetectedText.Text = info is { Major: > 0 } ? info.DisplayName + (info.IsSupportedArchitecture ? "" : " · 与本机架构不兼容") : "无法识别此 Java，请检查路径。";
            UpdateMemoryPreview();
            if (info is { Major: > 0, IsSupportedArchitecture: true })
            {
                ShowJavaError(null);
                SavePreference(s => s.JavaPath = info.JavaPath);
            }
            else ShowJavaError("Java 路径不正确");
        }
        catch (OperationCanceledException) { }
        catch { if (!_windowClosed && generation == _javaProbeGeneration) JavaDetectedText.Text = "Java 检测失败，请检查路径。"; }
    }

    private void MemoryMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingPreferences || MemoryTextBox == null || MemoryAdviceText == null) return;
        if (MemoryManualRadio.IsChecked == true)
        {
            _syncMemory = true;
            MemoryTextBox.Text = _manualMemoryText;
            _syncMemory = false;
        }
        UpdateMemoryPreview();
        SaveMemoryPreference();
    }
    private void MemoryTextBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_syncMemory)
        {
            if (MemoryManualRadio?.IsChecked == true) _manualMemoryText = MemoryTextBox.Text;
            UpdateMemoryPreview();
            SaveMemoryPreference();
        }
    }
    private void MemorySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncMemory || _loadingPreferences || MemoryTextBox == null || MemoryAutoRadio.IsChecked == true) return;
        MemoryTextBox.Text = ((int)e.NewValue).ToString();
    }

    private void UpdateMemoryPreview()
    {
        if (_loadingPreferences || _syncMemory || MemoryAdviceText == null) return;
        _syncMemory = true;
        try
        {
            var memory = MemorySnapshot.Read();
            bool automatic = MemoryAutoRadio.IsChecked == true;
            int bits = _detectedJava?.Bits ?? 64;
            int recommended = MemoryAllocation.Recommend(memory, bits);
            int requested = int.TryParse(MemoryTextBox.Text, out int mb) ? mb : 0;
            int displayed = automatic ? recommended : requested;
            if (automatic) MemoryTextBox.Text = recommended.ToString();
            MemoryTextBox.IsEnabled = MemorySlider.IsEnabled = !automatic;
            MemorySlider.Maximum = Math.Max(512, memory.TotalMb > 0 ? memory.TotalMb : 32768);
            MemorySlider.Value = Math.Clamp(displayed, 512, MemorySlider.Maximum);
            SystemMemoryText.Text = memory.TotalMb > 0 ? $"已使用 {(memory.TotalMb - memory.AvailableMb) / 1024.0:F1} GiB / 总内存 {memory.TotalMb / 1024.0:F1} GiB" : "暂时无法读取系统内存";
            MemoryUsageBar.Value = memory.TotalMb > 0 ? Math.Clamp(displayed * 100.0 / memory.TotalMb, 0, 100) : 0;
            MemoryAdviceText.Text = automatic
                ? $"游戏分配 {recommended / 1024.0:F1} GiB；启动时根据可用内存重新计算。"
                : $"游戏分配 {displayed / 1024.0:F1} GiB。";
            if (automatic && recommended < 1024) MemoryAdviceText.Text += " 可用内存偏低，建议关闭其他程序。";
            if (!automatic && memory.AvailableMb > 0 && displayed > memory.AvailableMb - 768)
                MemoryAdviceText.Text += " 分配接近或超过可用内存，可能导致系统卡顿。";
        }
        finally { _syncMemory = false; }
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
