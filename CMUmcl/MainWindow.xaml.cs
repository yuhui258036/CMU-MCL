using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CMUmcl.Auth;
using CMUmcl.Content;
using CMUmcl.Launch;

namespace CMUmcl
{
    public partial class MainWindow : Window
    {
        private readonly AuthStore _store;
        private readonly ResourceManifestClient _manifestClient;
        private readonly PackInstaller _installer;
        private readonly InstalledStateStore _installedState;
        private readonly LaunchService _launchService;
        private readonly SettingsStore _settingsStore;
        private readonly LocalInstanceService _localInstanceService;

        private AuthSession _session;
        private AppSettings _settings;

        private readonly ObservableCollection<PackCardViewModel> _cards = new();
        private readonly ObservableCollection<InstanceCardViewModel> _instances = new();

        private bool _loading;
        private bool _migrating;

        private enum MainPage { Packs, Instances, Settings }
        private enum PacksContentState { Loading, Cards, Empty, Error }

        private MainPage _currentPage = MainPage.Packs;
        private PacksContentState _packsState = PacksContentState.Cards;
        private string _packsHeaderStatus = string.Empty;
        private string _instancesHeaderStatus = string.Empty;

        /// <summary>Raised when the user clicks "退出登录" (before the window closes).</summary>
        public event Action? LogoutRequested;

        public MainWindow(
            AuthSession session,
            AuthStore store,
            ResourceManifestClient manifestClient,
            PackInstaller installer,
            InstalledStateStore installedState,
            LaunchService launchService,
            SettingsStore settingsStore,
            LocalInstanceService localInstanceService)
        {
            InitializeComponent();

            _session = session;
            _store = store;
            _manifestClient = manifestClient;
            _installer = installer;
            _installedState = installedState;
            _launchService = launchService;
            _settingsStore = settingsStore;
            _localInstanceService = localInstanceService;

            AccountNameText.Text = session.SelectedProfileName;
            CardsList.ItemsSource = _cards;
            InstancesList.ItemsSource = _instances;

            _settings = _settingsStore.Load();
            InitializeSettingsPage();
            ShowPacksView();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadManifestAsync();
        }

        // ------------------------------------------------------------------
        // Navigation
        // ------------------------------------------------------------------

        private void PacksNavButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage != MainPage.Packs)
                ShowPacksView();
        }

        private void InstancesNavButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage != MainPage.Instances)
                ShowInstancesView();
        }

        private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage != MainPage.Settings)
                ShowSettingsView();
        }

        private void ViewInstancesButton_Click(object sender, RoutedEventArgs e)
        {
            ShowInstancesView();
        }

        private void UpdateNavStyles()
        {
            PacksNavButton.Style = (Style)FindResource(_currentPage == MainPage.Packs ? "NavItemActive" : "NavItem");
            InstancesNavButton.Style = (Style)FindResource(_currentPage == MainPage.Instances ? "NavItemActive" : "NavItem");
            SettingsNavButton.Style = (Style)FindResource(_currentPage == MainPage.Settings ? "NavItemActive" : "NavItem");
        }

        private void ShowPacksView()
        {
            _currentPage = MainPage.Packs;
            UpdateNavStyles();

            HeaderTitleText.Text = "服务器整合包";
            HeaderStatusText.Text = _packsHeaderStatus;
            NewInstanceButton.Visibility = Visibility.Collapsed;
            RefreshButton.Visibility = Visibility.Visible;

            SettingsScroll.Visibility = Visibility.Collapsed;
            InstancesScroll.Visibility = Visibility.Collapsed;
            InstancesEmptyStateText.Visibility = Visibility.Collapsed;

            ApplyPacksVisibility();
        }

        private void ShowInstancesView()
        {
            _currentPage = MainPage.Instances;
            UpdateNavStyles();

            HeaderTitleText.Text = "实例";
            NewInstanceButton.Visibility = Visibility.Visible;
            RefreshButton.Visibility = Visibility.Visible;

            SettingsScroll.Visibility = Visibility.Collapsed;
            CardsScroll.Visibility = Visibility.Collapsed;
            EmptyStateText.Visibility = Visibility.Collapsed;
            LoadingState.Visibility = Visibility.Collapsed;
            ErrorState.Visibility = Visibility.Collapsed;

            RefreshInstances();
        }

        private void ShowSettingsView()
        {
            _currentPage = MainPage.Settings;
            UpdateNavStyles();

            HeaderTitleText.Text = "设置";
            HeaderStatusText.Text = string.Empty;
            RefreshButton.Visibility = Visibility.Collapsed;
            NewInstanceButton.Visibility = Visibility.Collapsed;

            CardsScroll.Visibility = Visibility.Collapsed;
            EmptyStateText.Visibility = Visibility.Collapsed;
            LoadingState.Visibility = Visibility.Collapsed;
            ErrorState.Visibility = Visibility.Collapsed;
            InstancesScroll.Visibility = Visibility.Collapsed;
            InstancesEmptyStateText.Visibility = Visibility.Collapsed;

            SettingsScroll.Visibility = Visibility.Visible;
            SettingsStatusText.Text = string.Empty;
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            switch (_currentPage)
            {
                case MainPage.Packs:
                    if (_loading) return;
                    await LoadManifestAsync();
                    break;

                case MainPage.Instances:
                    RefreshInstances();
                    break;

                default:
                    break;
            }
        }

        private void NewInstanceButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new CreateInstanceWindow(_localInstanceService)
            {
                Owner = this,
            };

            bool? ok = dialog.ShowDialog();
            if (ok == true && dialog.Result != null)
            {
                ShowInstancesView();
            }
        }

        // ------------------------------------------------------------------
        // 服务器整合包 page (catalog only: install/update, no launch)
        // ------------------------------------------------------------------

        private async Task LoadManifestAsync()
        {
            if (_loading) return;
            _loading = true;
            RefreshButton.IsEnabled = false;
            ShowLoading();

            try
            {
                ResourceManifest manifest = await _manifestClient.GetManifestAsync();

                // Sort instances by Sort (nulls last), then by id.
                var instances = manifest.Instances ?? new();
                instances.Sort((a, b) =>
                {
                    if (a.Sort == null && b.Sort == null) return string.CompareOrdinal(a.Id, b.Id);
                    if (a.Sort == null) return 1;
                    if (b.Sort == null) return -1;
                    return a.Sort.Value.CompareTo(b.Sort.Value);
                });

                _cards.Clear();
                foreach (var instance in instances)
                {
                    int? installedRevision = _installedState.GetInstalledRevision(instance.Id);
                    string? displayName = _installedState.GetDisplayName(instance.Id);
                    _cards.Add(new PackCardViewModel(instance, installedRevision, displayName));
                }

                ShowCards();
            }
            catch (ResourceException ex)
            {
                ShowError(ex.Message);
            }
            catch (Exception)
            {
                ShowError("获取整合包列表失败，发生了未知错误。");
            }
            finally
            {
                _loading = false;
                RefreshButton.IsEnabled = true;
            }
        }

        private async void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: PackCardViewModel vm })
            {
                await InstallAsync(vm);
            }
        }

        private async Task InstallAsync(PackCardViewModel vm)
        {
            vm.BeginInstall();

            var progress = new Progress<PackInstallProgress>(vm.ReportProgress);

            try
            {
                await _installer.InstallAsync(vm.Instance, progress);
                vm.Complete();
            }
            catch (ResourceException ex)
            {
                vm.Fail(ex.Message);
            }
            catch (OperationCanceledException)
            {
                vm.Fail("安装已取消。");
            }
            catch (Exception)
            {
                vm.Fail("安装失败，发生了未知错误。");
            }
        }

        /// <summary>Re-reads display names from the installed-state store (used after a migration).</summary>
        private void RefreshCardDisplayNames()
        {
            foreach (PackCardViewModel vm in _cards)
                vm.SetDisplayName(_installedState.GetDisplayName(vm.InstanceId));
        }

        // ------------------------------------------------------------------
        // 实例 page (all installed instances: server packs + local)
        // ------------------------------------------------------------------

        private void RefreshInstances()
        {
            _instances.Clear();
            foreach (var kv in _installedState.ListRecords())
            {
                _instances.Add(BuildInstanceCard(kv.Key, kv.Value));
            }

            _instancesHeaderStatus = $"共 {_instances.Count} 个实例";
            HeaderStatusText.Text = _instancesHeaderStatus;
            ApplyInstancesVisibility();
        }

        private static InstanceCardViewModel BuildInstanceCard(string id, InstanceRecord record)
        {
            bool isLocal = id.StartsWith("local:", StringComparison.Ordinal);

            string installPath = string.IsNullOrWhiteSpace(record.InstallPath) ? id : record.InstallPath!;
            string instanceDir = Path.Combine(LauncherPaths.InstancesDirectory, installPath);

            InstanceMetadata? metadata = null;
            string metaPath = Path.Combine(instanceDir, "instance.json");
            if (File.Exists(metaPath))
            {
                try
                {
                    metadata = System.Text.Json.JsonSerializer.Deserialize<InstanceMetadata>(File.ReadAllText(metaPath));
                }
                catch
                {
                    metadata = null;
                }
            }

            string displayName = record.DisplayName ?? metadata?.Name ?? id;
            string source = isLocal ? "本地实例" : "服务器整合包";

            return new InstanceCardViewModel(id, displayName, source, metadata, instanceDir);
        }

        private void ApplyInstancesVisibility()
        {
            bool empty = _instances.Count == 0;
            InstancesScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            InstancesEmptyStateText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: InstanceCardViewModel vm })
            {
                await LaunchInstanceAsync(vm);
            }
        }

        private async Task LaunchInstanceAsync(InstanceCardViewModel vm)
        {
            vm.BeginLaunch();

            var progress = new Progress<LaunchProgress>(vm.ReportLaunchProgress);

            try
            {
                LaunchResult result = await _launchService.LaunchLocalAsync(vm.InstanceId, _session, progress);

                // The session may have been refreshed during launch.
                _session = result.Session;

                vm.LaunchStarted();
            }
            catch (LaunchException ex)
            {
                vm.LaunchFail(ex.Message);
                MessageBox.Show(this, ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (OperationCanceledException)
            {
                vm.LaunchFail("启动已取消。");
            }
            catch (Exception ex)
            {
                vm.LaunchFail("启动失败，发生了未知错误。");
                MessageBox.Show(this, "启动失败，发生了未知错误。\n" + ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenInstanceDirButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: InstanceCardViewModel vm })
                return;

            try
            {
                string dir = vm.InstallPath;
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开目录：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            _store.ClearSession();
            LogoutRequested?.Invoke();
            Close();
        }

        // ------------------------------------------------------------------
        // Settings page
        // ------------------------------------------------------------------

        private void InitializeSettingsPage()
        {
            long totalMb = GetSystemTotalMemoryMb();
            SystemMemoryText.Text = totalMb > 0
                ? $"系统总内存：{FormatUtils.FormatBytes(totalMb * 1024L * 1024L)}"
                : "系统总内存：未知";

            IsolationComboBox.Items.Add(new ComboBoxItem { Content = "关闭" });
            IsolationComboBox.Items.Add(new ComboBoxItem { Content = "仅模组实例" });
            IsolationComboBox.Items.Add(new ComboBoxItem { Content = "全部实例" });

            RootPathText.Text = LauncherPaths.RootDirectory;

            LoadSettingsIntoControls();
        }

        private void LoadSettingsIntoControls()
        {
            bool manual = !string.IsNullOrWhiteSpace(_settings.JavaPath);
            JavaAutoRadio.IsChecked = !manual;
            JavaManualRadio.IsChecked = manual;

            JavaPathTextBox.Text = _settings.JavaPath ?? string.Empty;
            JavaPathTextBox.IsEnabled = manual;
            BrowseJavaButton.IsEnabled = manual;

            MemoryTextBox.Text = _settings.MaxMemoryMb.ToString();

            int index = (int)_settings.VersionIsolation;
            IsolationComboBox.SelectedIndex = index >= 0 && index <= 2 ? index : 1;

            InstancesDirectoryTextBox.Text = _settings.InstancesDirectory ?? string.Empty;
            UpdateInstancesDirectoryHint();
            ShowInstancesError(null);

            UpdateJavaDetectedText();
            ClearErrors();
        }

        private void JavaMode_Checked(object sender, RoutedEventArgs e)
        {
            if (JavaPathTextBox == null) return;

            bool manual = JavaManualRadio.IsChecked == true;
            JavaPathTextBox.IsEnabled = manual;
            BrowseJavaButton.IsEnabled = manual;
            ShowJavaError(null);
            UpdateJavaDetectedText();
        }

        private void JavaPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (JavaDetectedText == null) return;
            ShowJavaError(null);
            UpdateJavaDetectedText();
        }

        private void UpdateJavaDetectedText()
        {
            string path = JavaPathTextBox.Text;
            if (string.IsNullOrWhiteSpace(path))
            {
                JavaDetectedText.Text = "自动选择（启动时检测或自动下载）";
                return;
            }

            JavaInfo? info = JavaLocator.DetectExecutable(path);
            if (info != null && info.Major > 0)
                JavaDetectedText.Text = $"检测到 Java {info.Major}（{info.Version}）";
            else
                JavaDetectedText.Text = "未能检测到 Java 版本";
        }

        private void BrowseJavaButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 Java 运行环境",
                Filter = "javaw.exe|javaw.exe|java.exe|java.exe|所有文件|*.*",
                CheckFileExists = true,
            };

            if (dlg.ShowDialog(this) == true)
            {
                JavaPathTextBox.Text = dlg.FileName;
                UpdateJavaDetectedText();
            }
        }

        private void MemoryTextBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            foreach (char c in e.Text)
            {
                if (!char.IsDigit(c))
                {
                    e.Handled = true;
                    return;
                }
            }
        }

        private void IsolationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsolationHintText == null) return;

            IsolationHintText.Text = IsolationComboBox.SelectedIndex switch
            {
                0 => "所有实例共用同一个游戏目录（%LOCALAPPDATA%\\CMUmcl\\game）。",
                2 => "每个实例使用各自的独立目录。",
                _ => "仅模组实例使用独立目录；原版实例共用游戏目录。",
            };
        }

        private void OpenRootButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LauncherPaths.EnsureDirectories();
                Process.Start(new ProcessStartInfo
                {
                    FileName = LauncherPaths.RootDirectory,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开目录：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ------------------------------------------------------------------
        // Instance storage location
        // ------------------------------------------------------------------

        private void InstancesDirectoryTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (InstancesDirectoryHintText == null) return;
            ShowInstancesError(null);
            UpdateInstancesDirectoryHint();
        }

        private void UpdateInstancesDirectoryHint()
        {
            InstancesDirectoryHintText.Text = $"默认位置：{LauncherPaths.DefaultInstancesDirectory}";
        }

        private void BrowseInstancesButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择实例存放位置",
            };

            if (!string.IsNullOrWhiteSpace(InstancesDirectoryTextBox.Text))
                dlg.InitialDirectory = InstancesDirectoryTextBox.Text;

            if (dlg.ShowDialog(this) == true)
            {
                InstancesDirectoryTextBox.Text = dlg.FolderName;
                UpdateInstancesDirectoryHint();
            }
        }

        private void OpenInstancesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = LauncherPaths.InstancesDirectory;
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开实例目录：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowInstancesError(string? message)
        {
            InstancesDirectoryErrorText.Text = message ?? string.Empty;
            InstancesDirectoryErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Resolves the textbox value to a normalized directory, or null (default); returns an error message on invalid input.</summary>
        private string? ResolveInstancesDirectory(out string? error)
        {
            error = null;

            string input = InstancesDirectoryTextBox.Text?.Trim() ?? string.Empty;
            if (input.Length == 0)
                return null;

            string? normalized = LauncherPaths.NormalizeInstancesDirectory(input);
            if (normalized == null)
            {
                error = "实例目录无效。请选择一个绝对路径，且不能位于启动器自身目录（minecraft/cache/java/game/logs）内。";
                return null;
            }

            return normalized;
        }

        private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            ClearErrors();
            SettingsStatusText.Text = string.Empty;

            if (_migrating)
                return;

            if (!ValidateJavaPath(out string? javaError))
            {
                ShowJavaError(javaError);
                return;
            }

            if (!ValidateMemory(out string? memoryError))
            {
                ShowMemoryError(memoryError);
                return;
            }

            string? newInstancesDirectory = ResolveInstancesDirectory(out string? dirError);
            if (dirError != null)
            {
                ShowInstancesError(dirError);
                return;
            }

            bool manual = JavaManualRadio.IsChecked == true;
            string? path = JavaPathTextBox.Text?.Trim();
            int isolationIndex = IsolationComboBox.SelectedIndex;

            // Determine whether the instance storage location actually changed.
            string oldInstances = LauncherPaths.InstancesDirectory;
            string newInstances = newInstancesDirectory ?? LauncherPaths.DefaultInstancesDirectory;
            bool directoryChanged = !string.Equals(oldInstances, newInstances, StringComparison.OrdinalIgnoreCase);

            if (directoryChanged)
            {
                var records = _installedState.ListRecords();
                if (records.Count > 0)
                {
                    var confirm = MessageBox.Show(
                        this,
                        $"更改实例存放位置将迁移 {records.Count} 个已安装实例。\n\n源目录：{oldInstances}\n目标目录：{newInstances}\n\n是否继续？",
                        "迁移实例",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (confirm != MessageBoxResult.Yes)
                    {
                        SettingsStatusText.Text = "已取消，未做任何更改。";
                        return;
                    }

                    try
                    {
                        _migrating = true;
                        SaveSettingsButton.IsEnabled = false;

                        var progress = new Progress<MigrationProgress>(p => SettingsStatusText.Text = p.Message);
                        MigrationResult result = await InstanceMigrationService.MigrateAsync(
                            oldInstances,
                            newInstances,
                            records,
                            progress);

                        // Commit: persist settings + records, then remove the old directories.
                        _settings.JavaPath = manual && !string.IsNullOrWhiteSpace(path) ? path : null;
                        _settings.MaxMemoryMb = int.Parse(MemoryTextBox.Text.Trim());
                        _settings.VersionIsolation = isolationIndex >= 0 && isolationIndex <= 2
                            ? (VersionIsolation)isolationIndex
                            : VersionIsolation.ModdedOnly;
                        _settings.InstancesDirectory = newInstancesDirectory;

                        bool saved = _settingsStore.Save(_settings);
                        bool applied = saved && _installedState.ApplyMigration(result.UpdatedRecords);

                        if (saved && applied)
                        {
                            DeleteDirectories(result.SourceDirectories);
                            _settings = _settingsStore.Load();
                            SettingsStatusText.Text = "设置已保存，实例迁移完成。";
                            RefreshCardDisplayNames();
                        }
                        else
                        {
                            // Roll back the copies; the originals are intact.
                            DeleteDirectories(result.TargetDirectories);
                            SettingsStatusText.Text = "保存设置失败，已保留原实例目录。";
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        SettingsStatusText.Text = "迁移已取消，未做任何更改。";
                    }
                    catch (Exception ex)
                    {
                        SettingsStatusText.Text = "迁移失败：" + ex.Message;
                    }
                    finally
                    {
                        _migrating = false;
                        SaveSettingsButton.IsEnabled = true;
                    }

                    return;
                }
            }

            // No migration needed — save directly.
            _settings.JavaPath = manual && !string.IsNullOrWhiteSpace(path) ? path : null;
            _settings.MaxMemoryMb = int.Parse(MemoryTextBox.Text.Trim());
            _settings.VersionIsolation = isolationIndex >= 0 && isolationIndex <= 2
                ? (VersionIsolation)isolationIndex
                : VersionIsolation.ModdedOnly;
            _settings.InstancesDirectory = newInstancesDirectory;

            _settingsStore.Save(_settings);
            SettingsStatusText.Text = "设置已保存。";
        }

        private static void DeleteDirectories(IEnumerable<string> directories)
        {
            foreach (string dir in directories)
            {
                try
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // Best effort.
                }
            }
        }

        private bool ValidateJavaPath(out string? error)
        {
            error = null;

            if (JavaManualRadio.IsChecked != true)
                return true;

            string? path = JavaPathTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "请指定 Java 路径，或改选“自动选择”。";
                return false;
            }

            JavaInfo? info = JavaLocator.DetectExecutable(path);
            if (info == null || info.Major <= 0)
            {
                error = "无法从该路径读取 Java 版本信息。请选择有效的 javaw.exe。";
                return false;
            }

            return true;
        }

        private bool ValidateMemory(out string? error)
        {
            error = null;

            if (!int.TryParse(MemoryTextBox.Text?.Trim(), out int mb))
            {
                error = "内存值必须是数字。";
                return false;
            }

            const long min = 1024;
            if (mb < min)
            {
                error = "内存不能小于 1024 MB。";
                return false;
            }

            long totalMb = GetSystemTotalMemoryMb();
            if (totalMb > 0 && mb > totalMb)
            {
                error = $"内存不能超过系统总内存（约 {FormatUtils.FormatBytes(totalMb * 1024L * 1024L)}）。";
                return false;
            }

            return true;
        }

        private void ClearErrors()
        {
            ShowJavaError(null);
            ShowMemoryError(null);
        }

        private void ShowJavaError(string? message)
        {
            JavaErrorText.Text = message ?? string.Empty;
            JavaErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ShowMemoryError(string? message)
        {
            MemoryErrorText.Text = message ?? string.Empty;
            MemoryErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        // ------------------------------------------------------------------
        // Packs view visibility
        // ------------------------------------------------------------------

        private void ShowLoading()
        {
            _packsState = PacksContentState.Loading;
            ApplyPacksVisibility();
        }

        private void ShowError(string message)
        {
            _packsState = PacksContentState.Error;
            ApplyPacksVisibility();
            ErrorText.Text = message;
            _packsHeaderStatus = "加载失败";
            HeaderStatusText.Text = _packsHeaderStatus;
        }

        private void ShowCards()
        {
            _packsState = _cards.Count == 0 ? PacksContentState.Empty : PacksContentState.Cards;
            ApplyPacksVisibility();
            _packsHeaderStatus = $"共 {_cards.Count} 个整合包";
            HeaderStatusText.Text = _packsHeaderStatus;
        }

        private void ApplyPacksVisibility()
        {
            CardsScroll.Visibility = _packsState == PacksContentState.Cards ? Visibility.Visible : Visibility.Collapsed;
            EmptyStateText.Visibility = _packsState == PacksContentState.Empty ? Visibility.Visible : Visibility.Collapsed;
            LoadingState.Visibility = _packsState == PacksContentState.Loading ? Visibility.Visible : Visibility.Collapsed;
            ErrorState.Visibility = _packsState == PacksContentState.Error ? Visibility.Visible : Visibility.Collapsed;
        }

        // ------------------------------------------------------------------
        // System memory (best effort)
        // ------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

        private static long GetSystemTotalMemoryMb()
        {
            try
            {
                var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                if (GlobalMemoryStatusEx(ref status))
                    return (long)(status.ullTotalPhys / (1024L * 1024L));
            }
            catch
            {
                // Fall through to "unknown".
            }

            return 0;
        }
    }
}
