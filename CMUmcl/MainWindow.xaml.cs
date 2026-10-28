using System;
using System.Collections.ObjectModel;
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

        private AuthSession _session;

        private readonly ObservableCollection<PackCardViewModel> _cards = new();

        private bool _loading;

        /// <summary>Raised when the user clicks "退出登录" (before the window closes).</summary>
        public event Action? LogoutRequested;

        public MainWindow(
            AuthSession session,
            AuthStore store,
            ResourceManifestClient manifestClient,
            PackInstaller installer,
            InstalledStateStore installedState,
            LaunchService launchService)
        {
            InitializeComponent();

            _session = session;
            _store = store;
            _manifestClient = manifestClient;
            _installer = installer;
            _installedState = installedState;
            _launchService = launchService;

            AccountNameText.Text = session.SelectedProfileName;
            CardsList.ItemsSource = _cards;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadManifestAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            await LoadManifestAsync();
        }

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
                    _cards.Add(new PackCardViewModel(instance, installedRevision));
                }

                HeaderStatusText.Text = $"共 {_cards.Count} 个整合包";
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
                await HandleCardActionAsync(vm);
            }
        }

        /// <summary>Dispatches the card button action: launch if installed/current, otherwise install/update.</summary>
        private async Task HandleCardActionAsync(PackCardViewModel vm)
        {
            if (vm.IsLaunchable)
            {
                await LaunchAsync(vm);
            }
            else
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

        private async Task LaunchAsync(PackCardViewModel vm)
        {
            vm.BeginLaunch();

            var progress = new Progress<LaunchProgress>(vm.ReportLaunchProgress);

            try
            {
                LaunchResult result = await _launchService.LaunchAsync(vm.Instance, _session, progress);

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

        private void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            _store.ClearSession();
            LogoutRequested?.Invoke();
            Close();
        }

        private void ShowLoading()
        {
            CardsScroll.Visibility = Visibility.Collapsed;
            LoadingState.Visibility = Visibility.Visible;
            ErrorState.Visibility = Visibility.Collapsed;
            EmptyStateText.Visibility = Visibility.Collapsed;
        }

        private void ShowError(string message)
        {
            CardsScroll.Visibility = Visibility.Collapsed;
            LoadingState.Visibility = Visibility.Collapsed;
            ErrorState.Visibility = Visibility.Visible;
            EmptyStateText.Visibility = Visibility.Collapsed;
            ErrorText.Text = message;
            HeaderStatusText.Text = "加载失败";
        }

        private void ShowCards()
        {
            LoadingState.Visibility = Visibility.Collapsed;
            ErrorState.Visibility = Visibility.Collapsed;

            if (_cards.Count == 0)
            {
                CardsScroll.Visibility = Visibility.Collapsed;
                EmptyStateText.Visibility = Visibility.Visible;
            }
            else
            {
                CardsScroll.Visibility = Visibility.Visible;
                EmptyStateText.Visibility = Visibility.Collapsed;
            }
        }
    }
}
