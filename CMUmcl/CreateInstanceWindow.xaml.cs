using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

using CMUmcl.Content;
using CMUmcl.Launch;

namespace CMUmcl
{
    /// <summary>
    /// Dialog for creating a local (non-manifest) instance. Loads Minecraft / loader version
    /// lists on open (falling back to manual input when offline), provisions the base game and
    /// loader files at create time, and optionally downloads the Fabric API mod afterward.
    /// </summary>
    public partial class CreateInstanceWindow : Window
    {
        private readonly LocalInstanceService _service;
        private readonly LaunchService _launchService;
        private readonly VersionCatalogService _catalog;
        private readonly FabricApiInstaller _fabricApi;
        private readonly CancellationTokenSource _cts = new();

        /// <summary>The created instance on success, or null.</summary>
        public LocalInstanceResult? Result { get; private set; }

        private bool _creating;
        private int _loaderVersionLoadToken;

        public CreateInstanceWindow(LocalInstanceService service, LaunchService launchService)
        {
            InitializeComponent();
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _launchService = launchService ?? throw new ArgumentNullException(nameof(launchService));
            _catalog = new VersionCatalogService();
            _fabricApi = new FabricApiInstaller();

            LoaderCombo.Items.Add(new ComboBoxItem { Content = "原版", Tag = "vanilla" });
            LoaderCombo.Items.Add(new ComboBoxItem { Content = "Fabric", Tag = "fabric" });
            LoaderCombo.Items.Add(new ComboBoxItem { Content = "Forge", Tag = "forge" });
            LoaderCombo.Items.Add(new ComboBoxItem { Content = "NeoForge", Tag = "neoforge" });
            LoaderCombo.Items.Add(new ComboBoxItem { Content = "Quilt", Tag = "quilt" });
            LoaderCombo.SelectedIndex = 0;

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private string SelectedLoader => (LoaderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "vanilla";

        private string CurrentGameVersion => GameVersionCombo.Text?.Trim() ?? string.Empty;

        private string CurrentLoaderVersion => LoaderVersionCombo.Text?.Trim() ?? string.Empty;

        // ------------------------------------------------------------------
        // Load / close
        // ------------------------------------------------------------------

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            SetStatus("正在加载 Minecraft 版本列表…");
            try
            {
                IReadOnlyList<string> versions = await _catalog.GetMinecraftReleaseVersionsAsync(_cts.Token);
                if (versions.Count > 0)
                {
                    foreach (string version in versions)
                        GameVersionCombo.Items.Add(version);

                    GameVersionCombo.SelectedIndex = 0; // Releases are newest-first.
                    SetStatus($"已加载 {versions.Count} 个 Minecraft 版本。");
                }
                else
                {
                    SetStatus("无法加载 Minecraft 版本列表，可手动输入版本号。");
                }
            }
            catch (OperationCanceledException)
            {
                // Window closed before the list finished loading; nothing to do.
            }
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _cts.Cancel();
            _catalog.Dispose();
            _fabricApi.Dispose();
            _cts.Dispose();
        }

        // ------------------------------------------------------------------
        // Selection handling
        // ------------------------------------------------------------------

        private void LoaderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LoaderVersionCombo == null) return;

            UpdateLoaderUi();
            ShowError(null);
        }

        private void GameVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LoaderVersionCombo == null) return;

            ShowError(null);
            if (SelectedLoader is not "vanilla")
                _ = LoadLoaderVersionsAsync();
        }

        private void LoaderVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LoaderVersionCombo == null) return;

            ShowError(null);
        }

        private void UpdateLoaderUi()
        {
            string loader = SelectedLoader;

            bool isFabricLike = loader is "fabric" or "quilt";
            bool isVanilla = loader == "vanilla";

            LoaderVersionCombo.IsEnabled = !isVanilla;
            FabricApiCheck.Visibility = isFabricLike ? Visibility.Visible : Visibility.Collapsed;

            if (!isFabricLike)
                FabricApiCheck.IsChecked = false;

            if (isVanilla)
            {
                LoaderVersionCombo.Items.Clear();
                LoaderVersionCombo.Text = string.Empty;
                return;
            }

            _ = LoadLoaderVersionsAsync();
        }

        private async Task LoadLoaderVersionsAsync()
        {
            string loader = SelectedLoader;
            string gameVersion = CurrentGameVersion;
            int token = ++_loaderVersionLoadToken;

            if (loader is not ("fabric" or "quilt" or "forge" or "neoforge"))
            {
                LoaderVersionCombo.Items.Clear();
                return;
            }

            if (string.IsNullOrWhiteSpace(gameVersion))
            {
                LoaderVersionCombo.Items.Clear();
                SetStatus("请先选择 Minecraft 版本。");
                return;
            }

            string previous = CurrentLoaderVersion;
            SetStatus($"正在加载 {LoaderDisplay(loader)} 版本…");

            IReadOnlyList<string> versions = loader switch
            {
                "fabric" => await _catalog.GetFabricLoaderVersionsAsync(gameVersion, _cts.Token),
                "quilt" => await _catalog.GetQuiltLoaderVersionsAsync(gameVersion, _cts.Token),
                "forge" => await _catalog.GetForgeLoaderVersionsAsync(gameVersion, _cts.Token),
                "neoforge" => await _catalog.GetNeoForgeLoaderVersionsAsync(gameVersion, _cts.Token),
                _ => Array.Empty<string>(),
            };

            if (token != _loaderVersionLoadToken)
                return; // A newer load superseded this one.

            if (SelectedLoader is not ("fabric" or "quilt" or "forge" or "neoforge"))
                return; // The user switched loader while we were fetching.

            LoaderVersionCombo.Items.Clear();
            foreach (string version in versions)
                LoaderVersionCombo.Items.Add(version);

            if (versions.Count > 0)
            {
                // Preserve the user's current (possibly manual) value when it still appears in the list.
                int idx = string.IsNullOrEmpty(previous) ? 0 : IndexOfVersion(versions, previous);
                LoaderVersionCombo.SelectedIndex = idx >= 0 ? idx : 0;
                SetStatus($"已加载 {versions.Count} 个 {LoaderDisplay(loader)} 版本。");
            }
            else
            {
                LoaderVersionCombo.Text = previous;
                string hint = loader is "forge" or "neoforge"
                    ? $"无法加载 {LoaderDisplay(loader)} 版本，可手动输入版本。"
                    : $"无法加载 {LoaderDisplay(loader)} 版本，可手动输入或留空（自动选择默认版本）。";
                SetStatus(hint);
            }
        }

        private static string LoaderDisplay(string loader) => loader switch
        {
            "fabric" => "Fabric",
            "quilt" => "Quilt",
            "forge" => "Forge",
            "neoforge" => "NeoForge",
            _ => loader,
        };

        private static int IndexOfVersion(IReadOnlyList<string> versions, string value)
        {
            for (int i = 0; i < versions.Count; i++)
            {
                if (string.Equals(versions[i], value, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        // ------------------------------------------------------------------
        // Create
        // ------------------------------------------------------------------

        private async void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_creating) return;

            string name = NameBox.Text?.Trim() ?? string.Empty;
            string gameVersion = CurrentGameVersion;
            string loader = SelectedLoader;
            bool needsVersion = loader is "fabric" or "forge" or "neoforge" or "quilt";
            string loaderVersion = needsVersion ? CurrentLoaderVersion : string.Empty;
            bool installFabricApi = loader is "fabric" or "quilt" && FabricApiCheck.IsChecked == true;

            if (name.Length == 0)
            {
                ShowError("请输入实例名称。");
                NameBox.Focus();
                return;
            }

            if (gameVersion.Length == 0)
            {
                ShowError("请输入 Minecraft 游戏版本。");
                GameVersionCombo.Focus();
                return;
            }

            if (loader is "forge" or "neoforge" && loaderVersion.Length == 0)
            {
                ShowError("Forge / NeoForge 需要填写加载器版本。");
                LoaderVersionCombo.Focus();
                return;
            }

            _creating = true;
            CreateButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            ShowError(null);
            SetStatus("正在创建实例…");

            try
            {
                Result = _service.Create(new CreateLocalInstanceRequest
                {
                    DisplayName = name,
                    GameVersion = gameVersion,
                    Loader = loader,
                    LoaderVersion = loaderVersion,
                });
            }
            catch (LocalInstanceException ex)
            {
                ShowError(ex.Message);
                ResetCreatingUi();
                return;
            }
            catch (Exception)
            {
                ShowError("创建本地实例失败，发生了未知错误。");
                ResetCreatingUi();
                return;
            }

            // Provision the base game + loader files at create time. The instance is already
            // created and recorded, so any failure here must not delete it — warn and close.
            bool provisioned = await ProvisionAsync();
            if (!provisioned)
            {
                // On cancellation the window is already closing. On failure a warning was shown
                // and the instance is kept; close so the instance appears in the list.
                if (!_cts.IsCancellationRequested)
                    DialogResult = true;
                return;
            }

            if (installFabricApi)
            {
                var progress = new Progress<FabricApiProgress>(p =>
                {
                    if (p.TotalBytes is long total && p.BytesDownloaded is long done && total > 0)
                        SetStatus($"正在下载 Fabric API：{FormatUtils.FormatBytes(done)} / {FormatUtils.FormatBytes(total)}");
                    else
                        SetStatus($"正在下载 Fabric API：{p.Message}");
                });

                FabricApiResult apiResult = await _fabricApi.InstallAsync(gameVersion, Result.InstanceDirectory, progress, _cts.Token);

                // The user may have closed the window while the download was in flight;
                // the instance is already created and recorded, so just stop.
                if (_cts.IsCancellationRequested)
                    return;

                if (apiResult.Success)
                {
                    SetStatus("实例已创建，Fabric API 已安装。");
                }
                else
                {
                    MessageBox.Show(
                        this,
                        $"实例已创建，但 Fabric API 下载失败：{apiResult.ErrorMessage}\n\n你可以在首次启动前手动将 Fabric API 放入实例的 mods 文件夹。",
                        "Fabric API 下载失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    SetStatus("实例已创建，但 Fabric API 下载失败。");
                }
            }
            else
            {
                SetStatus("实例已创建。");
            }

            DialogResult = true;
        }

        /// <summary>
        /// Provisions the instance by downloading Minecraft + loader files. Returns true on
        /// success. On failure the instance is kept and a warning is shown (the first launch
        /// retries the download); returns false so the dialog closes without success.
        /// </summary>
        private async Task<bool> ProvisionAsync()
        {
            var progress = new Progress<LaunchProgress>(ReportProvisionProgress);

            var manifest = new ManifestInstance
            {
                Id = Result!.Id,
                Name = Result.DisplayName,
                Revision = 1,
            };

            try
            {
                SetStatus("正在下载 Minecraft 与加载器文件…");
                ProvisionBar.Visibility = Visibility.Visible;
                ProvisionBar.IsIndeterminate = true;

                string versionName = await _launchService.PrepareInstanceAsync(manifest, progress, _cts.Token);
                return true;
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                // Window closed while provisioning; the instance is already created and recorded.
                return false;
            }
            catch (LaunchException ex)
            {
                MessageBox.Show(
                    this,
                    $"实例已创建，但下载 Minecraft / 加载器文件失败：{ex.Message}\n\n首次启动时会自动重试下载。",
                    "下载失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                SetStatus("实例已创建，但下载 Minecraft / 加载器文件失败。首次启动时会自动重试。");
                return false;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"实例已创建，但下载 Minecraft / 加载器文件时发生了未知错误：{ex.Message}\n\n首次启动时会自动重试下载。",
                    "下载失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                SetStatus("实例已创建，但下载 Minecraft / 加载器文件时发生了未知错误。首次启动时会自动重试。");
                return false;
            }
        }

        private void ReportProvisionProgress(LaunchProgress p)
        {
            string text = string.IsNullOrWhiteSpace(p.Stage) ? p.Message : $"{p.Stage}：{p.Message}";

            if (p.TotalTasks > 0)
            {
                ProvisionBar.IsIndeterminate = false;
                ProvisionBar.Maximum = p.TotalTasks;
                ProvisionBar.Value = p.ProgressedTasks;
            }
            else if (p.TotalBytes > 0)
            {
                ProvisionBar.IsIndeterminate = false;
                ProvisionBar.Maximum = p.TotalBytes;
                ProvisionBar.Value = p.ProgressedBytes;
            }
            else
            {
                ProvisionBar.IsIndeterminate = true;
            }

            SetStatus(text);
        }

        private void ResetCreatingUi()
        {
            SetStatus(null);
            CreateButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            _creating = false;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        // ------------------------------------------------------------------
        // UI helpers
        // ------------------------------------------------------------------

        private void ShowError(string? message)
        {
            ErrorText.Text = message ?? string.Empty;
        }

        private void SetStatus(string? message)
        {
            StatusText.Text = message ?? string.Empty;
        }
    }
}
