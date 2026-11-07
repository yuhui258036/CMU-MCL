using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using CMUmcl.Auth;
using CMUmcl.Content;
using CMUmcl.Launch;
using CMUmcl.Update;

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
        private bool _handlingDrop;
        private readonly LauncherUpdateClient _launcherUpdates;
        private readonly CancellationTokenSource _updateLifetime = new();
        private readonly Community.CommunityResourceViewModel _community;
        private readonly Community.ResourceIconLoader _packIcons = new();
        internal Func<string, Task<BitmapSource?>>? LoadPackIconOverride { get; set; }
        private CancellationTokenSource? _updateDownload;
        private (UpdateRelease Release, byte[] Envelope)? _availableUpdate;
        private bool _checkingLauncherUpdate;
        private string? _promptedLauncherUpdateVersion;
        private bool _updatingLauncher;
        private bool _windowClosed;
        private int _activeOperations;
        private readonly bool _prefetchCommunity;

        private enum MainPage { Packs, Community, Instances, Settings, InstanceSettings, InstanceContent }
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
            : this(session, store, manifestClient, installer, installedState, launchService,
                settingsStore, localInstanceService, new LauncherUpdateClient())
        { }

        internal MainWindow(
            AuthSession session, AuthStore store, ResourceManifestClient manifestClient,
            PackInstaller installer, InstalledStateStore installedState, LaunchService launchService,
            SettingsStore settingsStore, LocalInstanceService localInstanceService,
            LauncherUpdateClient launcherUpdates, Community.CommunityResourceViewModel? communityOverride = null)
        {
            _launcherUpdates = launcherUpdates;
            InitializeComponent();
            StateChanged+=(_,_)=>{var chrome=System.Windows.Shell.WindowChrome.GetWindowChrome(this);if(chrome!=null)chrome.ResizeBorderThickness=new Thickness(WindowState==WindowState.Maximized?0:6);};

            _session = session;
            _store = store;
            _manifestClient = manifestClient;
            _installer = installer;
            _installedState = installedState;
            _launchService = launchService;
            _settingsStore = settingsStore;
            _localInstanceService = localInstanceService;

            _community = communityOverride ?? Community.CommunityResourceViewModel.CreateDefault(_installedState);
            _prefetchCommunity = communityOverride == null;
            CommunityResourceView.DataContext = _community;

            AccountNameText.Text = session.SelectedProfileName;
            CardsList.ItemsSource = _cards;
            InstancesList.ItemsSource = _instances;

            CreateInstanceView.BackRequested += CreateInstanceView_BackRequested;
            CreateInstanceView.InstanceCreated += CreateInstanceView_InstanceCreated;
            CreateInstanceView.OperationStateChanged += UpdateCreateActivity;
            CreateInstanceView.IsVisibleChanged += (_, _) => UpdateCreateActivity();

            _settings = _settingsStore.Load();
            Appearance.AppearanceManager.Initialize(_settings);
            Appearance.AppearanceManager.TrackWindow(this);
            InitializeSettingsPage();
            _preferencesReady = true;
            Appearance.AppearanceManager.BackgroundChanged += UpdateBackgroundStatus;
            Closing += Preferences_Closing;
            UpdateBackgroundStatus();
            LauncherUpdateStatus.Text = "当前版本 " + LauncherUpdateClient.CurrentVersion.ToString(3);
            Closed += (_, _) =>
            {
                _windowClosed = true;
                _globalSettingsInline?.Close();
                CancelInlinePrompt();CloseEmbeddedUiForNavigation();
                foreach(var request in _launchRequests.Values.ToArray())request.Cancel();
                foreach(var editor in _settingsWindows.Values.ToArray())editor.Close();
                _avatarLifetime.Cancel();
                _avatarService?.Dispose();
                CreateInstanceView.Close();
                Appearance.AppearanceManager.BackgroundChanged -= UpdateBackgroundStatus;
                _updateLifetime.Cancel();
                _launcherUpdates.Dispose();
                _packIcons.Dispose();
            };
            ShowPacksView();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _ = LoadAccountAvatarAsync();
            _ = CheckLauncherUpdateAsync(false);
            await LoadManifestAsync();
            // Fetch the first community page in the background so opening that page is instant.
            if (_prefetchCommunity)
            {
                await Task.Delay(1500);
                if (!_windowClosed) _ = _community.EnsureLoadedAsync();
            }
        }

        private async void CheckLauncherUpdate_Click(object sender, RoutedEventArgs e) => await CheckLauncherUpdateAsync(true);

        private async Task CheckLauncherUpdateAsync(bool userInitiated)
        {
            if (_checkingLauncherUpdate || _updatingLauncher) return;
            _checkingLauncherUpdate = true;
            CheckLauncherUpdateButton.IsEnabled = false;
            InstallLauncherUpdateButton.IsEnabled = false;
            LauncherUpdateStatus.Text = "\u6B63\u5728\u68C0\u67E5\u5BA2\u6237\u7AEF\u66F4\u65B0...";
            try
            {
                _availableUpdate = await _launcherUpdates.CheckAsync(_updateLifetime.Token);
                if (_windowClosed) return;
                LauncherUpdateStatus.Text = _availableUpdate is { } available
                    ? "\u53D1\u73B0\u65B0\u7248\u672C " + available.Release.Version + "\u3002" + available.Release.Notes
                    : "\u5F53\u524D\u7248\u672C " + LauncherUpdateClient.CurrentVersion.ToString(3) + " \u5DF2\u662F\u6700\u65B0\u3002";
                InstallLauncherUpdateButton.IsEnabled = _availableUpdate != null;
                if (_availableUpdate is { } update &&
                    (userInitiated || _promptedLauncherUpdateVersion != update.Release.Version) &&
                    !_windowClosed && IsVisible && _inlinePrompt is null &&
                    CreateInstanceView.Visibility != Visibility.Visible)
                {
                    _promptedLauncherUpdateVersion = update.Release.Version;
                    await PromptForLauncherUpdateAsync(update);
                }
            }
            catch (OperationCanceledException) when (_updateLifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _availableUpdate = null;
                if (!_windowClosed) LauncherUpdateStatus.Text = "\u6682\u65F6\u65E0\u6CD5\u68C0\u67E5\u5BA2\u6237\u7AEF\u66F4\u65B0\uFF1A" + ex.Message;
            }
            finally
            {
                _checkingLauncherUpdate = false;
                if (!_windowClosed) CheckLauncherUpdateButton.IsEnabled = true;
            }
        }

        private async Task PromptForLauncherUpdateAsync((UpdateRelease Release, byte[] Envelope) update)
        {
            if (_windowClosed) return;
            var choice = await ShowInlinePromptAsync(
                "\u53D1\u73B0\u5BA2\u6237\u7AEF\u66F4\u65B0",
                "\u65B0\u7248\u672C " + update.Release.Version + " \u5DF2\u53D1\u5E03\u3002\u662F\u5426\u73B0\u5728\u4E0B\u8F7D\u5E76\u5B89\u88C5\uFF1F\r\n\r\n" + update.Release.Notes,
                "\u4E0B\u8F7D\u5E76\u5B89\u88C5");
            if (_windowClosed) return;
            if (choice == InlineChoice.Continue)
                await InstallLauncherUpdateAsync();
            else
                LauncherUpdateStatus.Text = "\u53D1\u73B0\u65B0\u7248\u672C " + update.Release.Version + "\uFF0C\u53EF\u5728\u8BBE\u7F6E\u4E2D\u66F4\u65B0\u3002";
        }
        private void CancelLauncherUpdate_Click(object sender, RoutedEventArgs e) => _updateDownload?.Cancel();

        private async void InstallLauncherUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_checkingLauncherUpdate || _updatingLauncher) return;
            await InstallLauncherUpdateAsync();
        }

        private async Task InstallLauncherUpdateAsync()
        {
            if (_updatingLauncher || _availableUpdate is not { } available) return;
            if (_activeOperations != 0 || _migrating || CreateInstanceView.HasActiveOperation)
            {
                LauncherUpdateStatus.Text = "\u8BF7\u7B49\u5F85\u5F53\u524D\u4E0B\u8F7D\u3001\u542F\u52A8\u6216\u8FC1\u79FB\u5B8C\u6210\u540E\u518D\u66F4\u65B0\u5BA2\u6237\u7AEF\u3002";
                return;
            }
            _updatingLauncher = true;
            CheckLauncherUpdateButton.IsEnabled = false;
            InstallLauncherUpdateButton.IsEnabled = false;
            CancelLauncherUpdateButton.Visibility = Visibility.Visible;
            LauncherUpdateProgress.Visibility = Visibility.Visible;
            _updateDownload = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
            try
            {
                var progress = new Progress<double>(value =>
                {
                    if (_windowClosed) return;
                    LauncherUpdateProgress.Value = value;
                    LauncherUpdateStatus.Text = "\u6B63\u5728\u4E0B\u8F7D\u5BA2\u6237\u7AEF " + available.Release.Version + "\uFF1A" + value.ToString("F1") + "%";
                });
                var stage = await _launcherUpdates.StageAsync(available.Release, available.Envelope, progress, _updateDownload.Token);
                _updateDownload.Token.ThrowIfCancellationRequested();
                if (_windowClosed) return;
                LauncherUpdateStatus.Text = "\u4E0B\u8F7D\u4E0E\u6821\u9A8C\u5B8C\u6210\uFF0C\u51C6\u5907\u5B89\u88C5...";
                var choice = await ShowInlinePromptAsync(
                    "\u5B89\u88C5\u5BA2\u6237\u7AEF\u66F4\u65B0",
                    "\u5DF2\u4E0B\u8F7D\u5E76\u9A8C\u8BC1 " + available.Release.Version + "\u3002\u73B0\u5728\u5173\u95ED\u5BA2\u6237\u7AEF\u3001\u5B89\u88C5\u66F4\u65B0\u5E76\u91CD\u65B0\u6253\u5F00\uFF1F",
                    "\u7ACB\u5373\u5B89\u88C5");
                if (_windowClosed) return;
                if (choice == InlineChoice.Continue)
                {
                    LauncherUpdateClient.StartUpdater(stage);
                    Application.Current.Shutdown();
                }
                else LauncherUpdateStatus.Text = "\u5DF2\u53D6\u6D88\u5B89\u88C5\uFF1B\u5BA2\u6237\u7AEF\u4FDD\u6301\u5F53\u524D\u7248\u672C\u3002";
            }
            catch (OperationCanceledException)
            {
                if (!_windowClosed) LauncherUpdateStatus.Text = "\u5DF2\u53D6\u6D88\u5BA2\u6237\u7AEF\u66F4\u65B0\u3002";
            }
            catch (Exception ex)
            {
                if (!_windowClosed) LauncherUpdateStatus.Text = "\u5BA2\u6237\u7AEF\u66F4\u65B0\u5931\u8D25\uFF1A" + ex.Message;
            }
            finally
            {
                _updateDownload.Dispose();
                _updateDownload = null;
                _updatingLauncher = false;
                if (!_windowClosed)
                {
                    CheckLauncherUpdateButton.IsEnabled = true;
                    InstallLauncherUpdateButton.IsEnabled = _availableUpdate != null;
                    CancelLauncherUpdateButton.Visibility = Visibility.Collapsed;
                    LauncherUpdateProgress.Visibility = Visibility.Collapsed;
                }
            }
        }
        // ------------------------------------------------------------------
        // Drag-and-drop (.mrpack import)
        // ------------------------------------------------------------------

        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            e.Effects = GetMrpackDropEffects(e.Data);
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;

            if (_updatingLauncher) return;

            if (_handlingDrop)
                return;

            string[] files = (e.Data.GetData(DataFormats.FileDrop) as string[]) ?? Array.Empty<string>();
            if (files.Length == 1 && files[0].EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
            {
                _handlingDrop = true;
                try
                {
                    if (CreateInstanceView.HasPendingTask) { CreateInstanceView.Resume(); return; }
                    CreateInstanceView.Open(_localInstanceService, _launchService);
                    CreateInstanceView.SelectMrpackFile(files[0]);
                    e.Effects = DragDropEffects.Copy;
                    return;
                }
                finally
                {
                    _handlingDrop = false;
                }
            }

            // Reject multiple files / non-mrpack drops without opening the flow.
            e.Effects = DragDropEffects.None;
            LauncherPrompt.Show(this, "仅支持拖入单个 .mrpack 整合包文件。", "无法导入", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static DragDropEffects GetMrpackDropEffects(IDataObject data)
        {
            if (!data.GetDataPresent(DataFormats.FileDrop))
                return DragDropEffects.None;

            string[] files = (data.GetData(DataFormats.FileDrop) as string[]) ?? Array.Empty<string>();
            if (files.Length != 1 || !files[0].EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
                return DragDropEffects.None;

            return DragDropEffects.Copy;
        }

        // ------------------------------------------------------------------
        // Navigation
        // ------------------------------------------------------------------

        private void PacksNavButton_Click(object sender, RoutedEventArgs e)
        {
            CloseCreateInstanceView();
            if (_currentPage != MainPage.Packs)
                ShowPacksView();
        }

        private void CommunityNavButton_Click(object sender, RoutedEventArgs e)
        {
            CloseCreateInstanceView();
            if (_currentPage != MainPage.Community)
                ShowCommunityView();
        }

        private void InstancesNavButton_Click(object sender, RoutedEventArgs e)
        {
            CloseCreateInstanceView();
            if (_currentPage != MainPage.Instances)
                ShowInstancesView();
        }

        private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
        {
            CloseCreateInstanceView();
            if (_currentPage != MainPage.Settings)
                ShowSettingsView();
        }

        /// <summary>Only hides the create flow during navigation; the task remains owned by this window.</summary>
        private void CloseCreateInstanceView()
        {
            if (CreateInstanceView.Visibility == Visibility.Visible)
                CreateInstanceView.HideForNavigation();
        }

        private void UpdateCreateActivity()
        {
            if (_windowClosed) return;
            CreateActivityBanner.Visibility = CreateInstanceView.HasPendingTask && !CreateInstanceView.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            CreateActivityText.Text = CreateInstanceView.TaskStatus;
            if (!CreateInstanceView.HasActiveOperation)
            {
                if (_currentPage == MainPage.Instances) RefreshInstances();
                _community.RefreshTargets();
            }
        }

        private void ResumeCreateActivity_Click(object sender, RoutedEventArgs e) {CloseEmbeddedUiForNavigation();CreateInstanceView.Resume();}

        private void CreateInstanceView_BackRequested()
        {
            CreateInstanceView.Close();
            ShowInstancesView();
        }

        private void CreateInstanceView_InstanceCreated()
        {
            bool visible = CreateInstanceView.IsVisible;
            CreateInstanceView.Close();
            if (visible) ShowInstancesView();
            else if (_currentPage == MainPage.Instances) RefreshInstances();
        }

        private void ViewInstancesButton_Click(object sender, RoutedEventArgs e)
        {
            ShowInstancesView();
        }

        private void UpdateNavStyles()
        {
            PacksNavButton.Style = (Style)FindResource(_currentPage == MainPage.Packs ? "NavItemActive" : "NavItem");
            InstancesNavButton.Style = (Style)FindResource(_currentPage is MainPage.Instances or MainPage.InstanceSettings or MainPage.InstanceContent ? "NavItemActive" : "NavItem");
            SettingsNavButton.Style = (Style)FindResource(_currentPage == MainPage.Settings ? "NavItemActive" : "NavItem");
            CommunityNavButton.Style = (Style)FindResource(_currentPage == MainPage.Community ? "NavItemActive" : "NavItem");
        }

        private void ShowPacksView()
        {
            CloseEmbeddedUiForNavigation();
            _currentPage = MainPage.Packs;
            UpdateNavStyles();

            HeaderTitleText.Text = "服务器整合包";
            HeaderStatusText.Text = _packsHeaderStatus;
            NewInstanceButton.Visibility = Visibility.Collapsed;
            RefreshButton.Visibility = Visibility.Visible;

            SettingsScroll.Visibility = Visibility.Collapsed;
            InstancesScroll.Visibility = Visibility.Collapsed;
            InstancesEmptyStateText.Visibility = Visibility.Collapsed;
            CommunityResourceView.Visibility = Visibility.Collapsed;

            ApplyPacksVisibility();
        }

        private void ShowInstancesView()
        {
            CloseEmbeddedUiForNavigation();
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
            CommunityResourceView.Visibility = Visibility.Collapsed;

            RefreshInstances();
        }

        private void ShowSettingsView()
        {
            CloseEmbeddedUiForNavigation();
            EnsureInlineGameSettings();
            _currentPage = MainPage.Settings;
            UpdateNavStyles();

            HeaderTitleText.Text = "设置";
            HeaderStatusText.Text = "启动器设置";
            RefreshButton.Visibility = Visibility.Collapsed;
            NewInstanceButton.Visibility = Visibility.Collapsed;

            CardsScroll.Visibility = Visibility.Collapsed;
            EmptyStateText.Visibility = Visibility.Collapsed;
            LoadingState.Visibility = Visibility.Collapsed;
            ErrorState.Visibility = Visibility.Collapsed;
            InstancesScroll.Visibility = Visibility.Collapsed;
            InstancesEmptyStateText.Visibility = Visibility.Collapsed;
            CommunityResourceView.Visibility = Visibility.Collapsed;

            SettingsScroll.Visibility = Visibility.Visible;
        }

        private void ShowCommunityView()
        {
            CloseEmbeddedUiForNavigation();
            _currentPage = MainPage.Community;
            UpdateNavStyles();

            HeaderTitleText.Text = "社区资源";
            HeaderStatusText.Text = "搜索并安装模组、整合包、资源包和光影";
            NewInstanceButton.Visibility = Visibility.Collapsed;
            RefreshButton.Visibility = Visibility.Visible;

            CardsScroll.Visibility = Visibility.Collapsed;
            EmptyStateText.Visibility = Visibility.Collapsed;
            LoadingState.Visibility = Visibility.Collapsed;
            ErrorState.Visibility = Visibility.Collapsed;
            InstancesScroll.Visibility = Visibility.Collapsed;
            InstancesEmptyStateText.Visibility = Visibility.Collapsed;
            SettingsScroll.Visibility = Visibility.Collapsed;

            _community.RefreshTargets();
            CommunityResourceView.Visibility = Visibility.Visible;
            _ = _community.EnsureLoadedAsync();
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

                case MainPage.Community:
                    _ = _community.SearchAsync();
                    break;

                default:
                    break;
            }
        }

        private void NewInstanceButton_Click(object sender, RoutedEventArgs e)
        {
            if (_updatingLauncher) return;
            CloseEmbeddedUiForNavigation();
            CreateInstanceView.Open(_localInstanceService, _launchService);
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
                    var card = new PackCardViewModel(instance, installedRevision, displayName);
                    _cards.Add(card);
                    _ = LoadPackIconAsync(card);
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

        private async Task LoadPackIconAsync(PackCardViewModel card)
        {
            if (_windowClosed || string.IsNullOrWhiteSpace(card.IconUrl)) return;
            try
            {
                BitmapSource? icon = LoadPackIconOverride is { } load
                    ? await load(card.IconUrl!)
                    : await _packIcons.LoadAsync(card.IconUrl, _updateLifetime.Token);
                if (_windowClosed || !_cards.Contains(card)) return;
                card.SetIcon(icon);
            }
            catch (OperationCanceledException) { }
            catch { /* Offline/blocked icon keeps the built-in fallback. */ }
        }

        private async void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (_updatingLauncher) return;
            if (sender is Button { DataContext: PackCardViewModel vm })
            {
                if(!CanInstallPack(vm.InstanceId)){LauncherPrompt.Show(this,"此实例正在启动、运行或维护，请完成后再安装或更新。","实例正在使用");return;}
                _activeOperations++;
                try { await InstallAsync(vm); }
                finally { _activeOperations--; }
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
            var existing = _instances.ToDictionary(card => card.InstanceId, StringComparer.Ordinal);
            var next = new List<InstanceCardViewModel>();
            // Managed/server-pack instances stay above local custom instances.
            foreach (var kv in _installedState.ListRecords()
                         .OrderBy(kv => kv.Key.StartsWith(PackAutoUpdate.LocalIdPrefix, StringComparison.Ordinal) ? 1 : 0)
                         .ThenBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var latest=BuildInstanceCard(kv.Key,kv.Value);
                if(existing.TryGetValue(kv.Key,out InstanceCardViewModel? card) && string.Equals(card.InstallPath,latest.InstallPath,StringComparison.OrdinalIgnoreCase))
                { card.RefreshPresentation(latest);next.Add(card); }
                else next.Add(latest);
            }

            _instances.Clear();
            foreach (InstanceCardViewModel card in next)
                _instances.Add(card);

            _instancesHeaderStatus = $"共 {_instances.Count} 个实例";
            if(_currentPage==MainPage.Instances)
            {
                HeaderStatusText.Text = _instancesHeaderStatus;
                ApplyInstancesVisibility();
            }
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
            if (_updatingLauncher) return;
            if (sender is Button { DataContext: InstanceCardViewModel vm })
            {
                if (IsInstanceBusy(vm) || _settingsWindows.Values.Any(x=>x.MaintenanceBusy)) return;
                _activeOperations++;
                try { await LaunchInstanceAsync(vm); }
                finally { _activeOperations--; }
            }
        }

        private async Task LaunchInstanceAsync(InstanceCardViewModel vm)
        {
            if (_launchRequests.ContainsKey(vm.InstanceId)) return;
            using var cancellation = new CancellationTokenSource();
            _launchRequests.Add(vm.InstanceId,cancellation);
            _crashes.Remove(vm.InstanceId);
            vm.BeginLaunch();
            var progress = new Progress<LaunchProgress>(p=>{if(!cancellation.IsCancellationRequested)vm.ReportLaunchProgress(p);});
            try
            {
                if (!await TryAutoUpdateBeforeLaunchAsync(vm,cancellation.Token)) return;
                cancellation.Token.ThrowIfCancellationRequested();
                LaunchResult result = await LaunchWithLoginRecoveryAsync(vm, progress, cancellation.Token);
                TrackGame(vm,result.Process,result.Diagnostics,CreateCrashContext(vm,result));

                if (result.WindowDetected)
                {
                    vm.LaunchStarted();
                    ApplyAfterLaunch();
                }
                else
                {
                    vm.LaunchWindowPending();
                }
            }
            catch (LaunchException ex)
            {
                vm.LaunchFail(ex.Message);
                // A connection failure to the skin site: work out whose side it is on before saying so.
                CMUmcl.Network.ConnectionProblem? problem = null;
                if (ex.InnerException is YggdrasilException auth && !auth.RequiresLogin && auth.Failure != YggdrasilFailure.Other)
                    problem = await CMUmcl.Network.ConnectionDiagnosis.DiagnoseAsync(auth);
                if (_windowClosed) return;
                if (problem is { } found)
                {
                    ShowConnectionProblem(found, ex.Message);
                    // A confirmed outage is stated plainly; the raw message would wrongly suggest checking the user's network.
                    string message = found == CMUmcl.Network.ConnectionProblem.SkinSiteDown ? ConnectionProblemExplanation(found) : ex.Message + "\n" + ConnectionProblemExplanation(found);
                    LauncherPrompt.Show(this, message, ConnectionProblemTitle(found), MessageBoxButton.OK, MessageBoxImage.Warning,
                        found == CMUmcl.Network.ConnectionProblem.SkinSiteDown ? SkinSitePicture : null);
                }
                else LauncherPrompt.Show(this, ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (OperationCanceledException)
            {
                vm.LaunchFail("启动已取消。");
            }
            catch (Exception ex)
            {
                vm.LaunchFail("启动失败，发生了未知错误。");
                LauncherPrompt.Show(this, "启动失败，发生了未知错误。\n" + LaunchErrorDiagnostics.Describe(ex), "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { _launchRequests.Remove(vm.InstanceId); vm.ClearLaunchActionIfIdle(_gameProcesses.ContainsKey(vm.InstanceId)); }
        }

        // ------------------------------------------------------------------
        // Pre-launch auto-update (managed server packs only)
        // ------------------------------------------------------------------

        /// <summary>
        /// Performs the launch-time auto-update check. Returns false when the launch must not
        /// proceed (check/update failed). Local instances and disabled auto-update return true
        /// immediately so the installed revision is launched directly.
        /// </summary>
        private async Task<bool> TryAutoUpdateBeforeLaunchAsync(InstanceCardViewModel vm, CancellationToken ct = default)
        {
            string id = vm.InstanceId;

            // Local custom instances are never auto-updated.
            if (id.StartsWith(PackAutoUpdate.LocalIdPrefix, StringComparison.Ordinal))
                return true;

            // Auto-update disabled: launch the installed revision directly.
            if (!_settings.AutoUpdatePacks)
                return true;

            int? installedRevision = _installedState.GetInstalledRevision(id);
            ManifestInstance? manifest = await TryGetManifestInstanceAsync(id,ct);
            ct.ThrowIfCancellationRequested();

            PackAutoUpdateAction action = PackAutoUpdate.Decide(id, autoUpdateEnabled: true, installedRevision, manifest);

            if (action == PackAutoUpdateAction.FailClosed)
            {
                vm.LaunchFail("无法检查整合包更新，未启动。");
                LauncherPrompt.Show(
                    this,
                    "无法检查整合包更新，已取消启动。\n要启动已安装版本，请关闭设置中的“启动前自动更新整合包”。",
                    "自动更新失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (action == PackAutoUpdateAction.LaunchInstalled)
                return true;

            // Update required: install the manifest revision before launching.
            return await UpdatePackBeforeLaunchAsync(vm, manifest!,ct);
        }

        /// <summary>Installs the manifest revision before launch; returns false (and does not launch) on failure.</summary>
        private async Task<bool> UpdatePackBeforeLaunchAsync(InstanceCardViewModel vm, ManifestInstance manifest, CancellationToken ct = default)
        {
            vm.BeginPackUpdate(manifest.Revision);

            var progress = new Progress<PackInstallProgress>(vm.ReportPackUpdateProgress);

            try
            {
                await _installer.InstallAsync(manifest, progress, ct);

                // InstallAsync already recorded the new revision; refresh the matching pack card
                // (if present) so the 服务器整合包 page is not stale.
                foreach (PackCardViewModel card in _cards)
                {
                    if (string.Equals(card.InstanceId, manifest.Id, StringComparison.Ordinal))
                        card.UpdateButtonState(_installedState.GetInstalledRevision(manifest.Id));
                }

                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (ResourceException ex)
            {
                vm.LaunchFail("自动更新失败：" + ex.Message);
                LauncherPrompt.Show(
                    this,
                    "自动更新失败，未启动旧版本：\n" + ex.Message + "\n\n如要离线启动已安装版本，请在设置中关闭“启动前自动更新整合包”。",
                    "自动更新失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
            catch (Exception ex)
            {
                vm.LaunchFail("自动更新失败：发生了未知错误。");
                LauncherPrompt.Show(
                    this,
                    "自动更新失败，未启动旧版本。\n" + ex.Message + "\n\n如要离线启动已安装版本，请在设置中关闭“启动前自动更新整合包”。",
                    "自动更新失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// Resolves a manifest entry by id from a fresh signed manifest, so a revision published
        /// while the launcher stays open is still applied before launch. Falls back to the entry
        /// verified when the pack list was loaded; returns null when neither is available.
        /// </summary>
        private async Task<ManifestInstance?> TryGetManifestInstanceAsync(string id, CancellationToken ct = default)
        {
            try
            {
                ResourceManifest manifest = await _manifestClient.GetManifestAsync(ct);
                ManifestInstance? fresh = manifest.Instances?.FirstOrDefault(instance => string.Equals(instance.Id, id, StringComparison.Ordinal));
                if (fresh != null) return fresh;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                // Fetch failure (network/signature/allowlist): use the list loaded earlier, if any.
            }

            return _cards.FirstOrDefault(card => string.Equals(card.InstanceId, id, StringComparison.Ordinal))?.Instance;
        }

        private void InstanceSettings_Click(object sender, RoutedEventArgs e)
        {
            if(sender is not Button button || button.DataContext is not InstanceCardViewModel card)return;
            OpenInstanceSettings(card);
        }

        private void RefreshAfterInstanceSettings()
        {
            _settings=_settingsStore.Load();
            _community.RefreshTargets();
            _loadingPreferences=true;try{LoadPreferenceControls();}finally{_loadingPreferences=false;}
            RefreshInstances();
        }

        private void OpenInstanceDirButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: InstanceCardViewModel vm })
                return;

            try
            {
                OpenInstanceDirectories(vm);
            }
            catch (Exception ex)
            {
                LauncherPrompt.Show(this, "无法打开目录：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            if (_updatingLauncher) return;
            // Resolve pending game defaults first so a cancelled prompt leaves the session intact.
            if (!await ResolveUnsavedGameDefaultsAsync("退出登录") || _windowClosed) return;
            if (!_store.TryClearSession())
            {
                LauncherPrompt.Show(this, "无法清除已保存的登录信息，请检查数据目录权限后重试。", "退出登录失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            LogoutRequested?.Invoke();
            _closingAfterFlush = true;
            Close();
        }

        // ------------------------------------------------------------------
        // Settings page
        // ------------------------------------------------------------------

        private void InitializeSettingsPage()
        {
            IsolationComboBox.Items.Add(new ComboBoxItem { Content = "关闭" });
            IsolationComboBox.Items.Add(new ComboBoxItem { Content = "仅模组实例" });
            IsolationComboBox.Items.Add(new ComboBoxItem { Content = "全部实例" });

            RootPathText.Text = LauncherPaths.RootDirectory;

            LoadSettingsIntoControls();
        }

        private void LoadSettingsIntoControls()
        {
            _loadingPreferences = true;
            try
            {
                int index = (int)_settings.VersionIsolation;
                IsolationComboBox.SelectedIndex = index >= 0 && index <= 2 ? index : 1;

                InstancesDirectoryTextBox.Text = _settings.InstancesDirectory ?? string.Empty;
                UpdateInstancesDirectoryHint();
                ShowInstancesError(null);

                AutoUpdatePacksCheck.IsChecked = _settings.AutoUpdatePacks;

                LoadPreferenceControls();
            }
            finally { _loadingPreferences = false; }
        }

        private void DigitsOnly_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
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
                0 => "所有实例共用游戏目录。",
                2 => "每个实例使用各自的独立目录。",
                _ => "仅模组实例使用独立目录；原版实例共用游戏目录。",
            };
            if (IsolationComboBox.SelectedIndex >= 0) SavePreference(s => s.VersionIsolation = (VersionIsolation)IsolationComboBox.SelectedIndex);
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
                LauncherPrompt.Show(this, "无法打开目录：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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

        private async void BrowseInstancesButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择实例存放位置",
            };

            if (!string.IsNullOrWhiteSpace(InstancesDirectoryTextBox.Text))
                dlg.InitialDirectory = InstancesDirectoryTextBox.Text;

            if (dlg.ShowDialog(this) == true)
            {
                string? directory = LauncherPaths.NormalizeInstancesDirectory(dlg.FolderName);
                if (directory == null) { ShowInstancesError("游戏目录无效，请选择其他目录。"); return; }
                await ChangeInstancesDirectoryAsync(directory);
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
                LauncherPrompt.Show(this, "无法打开实例目录：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
    }
}
