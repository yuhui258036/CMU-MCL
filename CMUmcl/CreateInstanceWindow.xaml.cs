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
    /// <summary>One selectable loader option shown as a chip in the create-instance dialog.</summary>
    public sealed class LoaderChoice
    {
        /// <summary>Stable key stored in the instance metadata (<c>vanilla</c>/<c>fabric</c>/<c>forge</c>/<c>neoforge</c>/<c>quilt</c>).</summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>User-facing loader name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Short letter badge (<c>V</c>/<c>F</c>/<c>Fo</c>/<c>N</c>/<c>Q</c>).</summary>
        public string Badge { get; init; } = string.Empty;

        /// <summary>One-line description of the loader.</summary>
        public string Hint { get; init; } = string.Empty;
    }

    /// <summary>
    /// Dialog for creating a local (non-manifest) instance. Loads Minecraft / loader version
    /// lists on open (falling back to manual input when offline), provisions the base game and
    /// loader files at create time, and optionally downloads the Fabric API mod afterward.
    /// </summary>
    public partial class CreateInstanceWindow : Window
    {
        private static readonly IReadOnlyList<LoaderChoice> LoaderChoices = new List<LoaderChoice>
        {
            new() { Key = "vanilla",  Name = "原版",    Badge = "V",  Hint = "纯净原版，不安装模组加载器。" },
            new() { Key = "fabric",   Name = "Fabric",  Badge = "F",  Hint = "轻量快速的模组加载器，模组生态丰富。" },
            new() { Key = "forge",    Name = "Forge",   Badge = "Fo", Hint = "经典模组加载器，支持大量传统模组。" },
            new() { Key = "neoforge", Name = "NeoForge", Badge = "N", Hint = "Forge 的分支，面向新版 Minecraft。" },
            new() { Key = "quilt",    Name = "Quilt",   Badge = "Q",  Hint = "基于 Fabric 的社区加载器，兼容 Fabric 模组。" },
        };

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

            // Build the loader chips and default to 原版. Controls exist (InitializeComponent has
            // run), but guard against SelectionChanged firing during item population.
            LoaderChips.ItemsSource = new List<LoaderChoice>(LoaderChoices);
            LoaderChips.SelectedIndex = 0;

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private string SelectedLoader => (LoaderChips.SelectedItem as LoaderChoice)?.Key ?? "vanilla";

        private string CurrentGameVersion => GameVersionCombo.Text?.Trim() ?? string.Empty;

        private string CurrentLoaderVersion => LoaderVersionCombo.Text?.Trim() ?? string.Empty;

        // ------------------------------------------------------------------
        // Load / close
        // ------------------------------------------------------------------

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            WireEditableTextChanged();
            UpdateSummary();

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

        /// <summary>
        /// Hooks the editable text box of each editable combo so typed text updates the live
        /// summary (SelectionChanged only covers dropdown picks).
        /// </summary>
        private void WireEditableTextChanged()
        {
            var gameBox = GameVersionCombo.Template?.FindName("PART_EditableTextBox", GameVersionCombo) as TextBox;
            if (gameBox != null)
                gameBox.TextChanged += (_, _) => UpdateSummary();

            var versionBox = LoaderVersionCombo.Template?.FindName("PART_EditableTextBox", LoaderVersionCombo) as TextBox;
            if (versionBox != null)
                versionBox.TextChanged += (_, _) => UpdateSummary();
        }

        // ------------------------------------------------------------------
        // Selection handling
        // ------------------------------------------------------------------

        private void LoaderChips_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LoaderVersionCombo == null) return;

            UpdateLoaderUi();
            ShowError(null);
        }

        private void GameVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LoaderVersionCombo == null) return;

            ShowError(null);
            UpdateSummary();
            if (SelectedLoader is not "vanilla")
                _ = LoadLoaderVersionsAsync();
        }

        private void LoaderVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LoaderVersionCombo == null) return;

            ShowError(null);
            UpdateSummary();
        }

        private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SummaryNameText == null) return;

            UpdateSummary();
        }

        private void UpdateLoaderUi()
        {
            string loader = SelectedLoader;

            bool isFabricLike = loader is "fabric" or "quilt";
            bool isVanilla = loader == "vanilla";

            LoaderVersionCombo.IsEnabled = !isVanilla;
            FabricApiSection.Visibility = isFabricLike ? Visibility.Visible : Visibility.Collapsed;

            if (!isFabricLike)
                FabricApiCheck.IsChecked = false;

            UpdateLoaderVersionHint(loader, isVanilla, isFabricLike);
            UpdateLoaderHintText();
            UpdateSummary();

            if (isVanilla)
            {
                LoaderVersionCombo.Items.Clear();
                LoaderVersionCombo.Text = string.Empty;
                return;
            }

            _ = LoadLoaderVersionsAsync();
        }

        private void UpdateLoaderVersionHint(string loader, bool isVanilla, bool isFabricLike)
        {
            if (isVanilla)
                LoaderVersionHintText.Text = "原版实例无需加载器版本。";
            else if (isFabricLike)
                LoaderVersionHintText.Text = "可留空，创建时自动选择默认版本。";
            else
                LoaderVersionHintText.Text = "必填；从列表选择或手动输入版本。";
        }

        private void UpdateLoaderHintText()
        {
            LoaderHintText.Text = (LoaderChips.SelectedItem as LoaderChoice)?.Hint ?? string.Empty;
        }

        private void UpdateSummary()
        {
            string name = NameBox.Text?.Trim() ?? string.Empty;
            SummaryNameText.Text = name.Length > 0 ? name : "未命名实例";

            string game = CurrentGameVersion;
            SummaryGameText.Text = game.Length > 0 ? game : "未选择版本";

            var choice = LoaderChips.SelectedItem as LoaderChoice;
            string loaderName = choice?.Name ?? "原版";
            string loaderVersion = CurrentLoaderVersion;

            if (SelectedLoader == "vanilla")
                SummaryLoaderText.Text = "原版";
            else if (loaderVersion.Length > 0)
                SummaryLoaderText.Text = $"{loaderName} {loaderVersion}";
            else
                SummaryLoaderText.Text = $"{loaderName}（未选择版本）";
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

            SetCreatingState(true);
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

        private void SetCreatingState(bool creating)
        {
            _creating = creating;

            NameBox.IsEnabled = !creating;
            GameVersionCombo.IsEnabled = !creating;
            LoaderChips.IsEnabled = !creating;
            LoaderVersionCombo.IsEnabled = !creating && SelectedLoader != "vanilla";
            FabricApiCheck.IsEnabled = !creating;

            CreateButton.IsEnabled = !creating;
            CancelButton.IsEnabled = !creating;
        }

        private void ResetCreatingUi()
        {
            SetStatus(null);
            SetCreatingState(false);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
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
