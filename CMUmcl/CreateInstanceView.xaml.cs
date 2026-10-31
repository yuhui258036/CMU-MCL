using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using CMUmcl.Content;
using CMUmcl.Launch;

namespace CMUmcl
{
    /// <summary>One selectable loader card shown on the loader step.</summary>
    public sealed class LoaderCard : INotifyPropertyChanged
    {
        public LoaderCard(string key, string name, string iconSource)
        {
            Key = key;
            Name = name;
            IconSource = iconSource;
        }

        public string Key { get; }
        public string Name { get; }
        public string IconSource { get; }

        private string _status = "选择版本";
        public string Status
        {
            get => _status;
            set { if (_status != value) { _status = value; Raise(nameof(Status)); } }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; Raise(nameof(IsSelected)); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>One Minecraft version row on the game-version step.</summary>
    public sealed class GameVersionRow
    {
        public GameVersionRow(MinecraftVersionInfo info)
        {
            Info = info;
            Name = info.Name;
            CategoryLabel = info.CategoryLabel;
            DateText = info.ReleaseTime.ToLocalTime()
                .ToString("yyyy年MM月dd日 HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        }

        public MinecraftVersionInfo Info { get; }
        public string Name { get; }
        public string CategoryLabel { get; }
        public string DateText { get; }
    }

    /// <summary>One loader version row on the loader-version step.</summary>
    public sealed class LoaderVersionRow
    {
        public LoaderVersionRow(LoaderVersionInfo info, string iconSource)
        {
            Info = info;
            IconSource = iconSource;
            Version = info.Version;
            Detail = info.Detail;
            GameText = $"Minecraft {info.GameVersion}";
        }

        public LoaderVersionInfo Info { get; }
        public string IconSource { get; }
        public string Version { get; }
        public string Detail { get; }
        public string GameText { get; }
    }

    /// <summary>
    /// In-main create-instance flow (YSUMCL/PCL/HMCL inspired): a light surface rendered inside the
    /// main window (no separate window). Walks landing → game version → loader → loader version →
    /// progress → success, creating and provisioning the instance via <see cref="LocalInstanceService"/>
    /// and <see cref="LaunchService"/>.
    /// </summary>
    public partial class CreateInstanceView : UserControl
    {
        private enum Step { Landing, Import, GameVersion, Loader, LoaderVersion, Progress, Success }
        private enum ImportKind { None, Folder, Mrpack }

        private readonly ObservableCollection<LoaderCard> _loaderCards = new();
        private readonly ObservableCollection<GameVersionRow> _gameVersionRows = new();
        private readonly ObservableCollection<LoaderVersionRow> _loaderVersionRows = new();

        private LocalInstanceService _service = null!;
        private LaunchService _launchService = null!;
        private VersionCatalogService _catalog = null!;
        private FabricApiInstaller _fabricApi = null!;
        private CancellationTokenSource _cts = new();

        private readonly InstanceImportService _importService = new(new InstalledStateStore());
        private MrpackImportService _mrpackImportService = null!;
        private string? _importSourcePath;
        private ImportKind _importKind;
        private bool _importing;

        private readonly List<MinecraftVersionInfo> _gameVersions = new();
        private readonly List<LoaderVersionInfo> _loaderVersions = new();

        private Step _step = Step.Landing;
        private GameVersionRow? _selectedGameVersion;
        private string _selectedLoader = "vanilla";
        private string _selectedLoaderVersion = string.Empty;
        private string _defaultName = string.Empty;
        private string? _fabricWarning;
        private LocalInstanceResult? _result;

        private bool _ready;
        private bool _creating;
        public bool HasActiveOperation => _creating || _importing;
        private bool _loadingGameVersions;
        private bool _loadingLoaderVersions;
        private bool _gameVersionsLoaded;
        private int _loaderVersionLoadToken;
        private int _sessionToken;

        /// <summary>Raised when the user backs out from the landing step (returns to the instance library).</summary>
        public event Action? BackRequested;

        /// <summary>Raised when the flow finishes (success or "返回实例列表" after failure).</summary>
        public event Action? InstanceCreated;

        public CreateInstanceView()
        {
            InitializeComponent();

            _loaderCards.Add(new LoaderCard("vanilla", "Minecraft", "pack://application:,,,/Assets/InstanceIcons/grass-block.png"));
            _loaderCards.Add(new LoaderCard("forge", "Forge", "pack://application:,,,/Assets/InstanceIcons/Forge.png"));
            _loaderCards.Add(new LoaderCard("neoforge", "NeoForge", "pack://application:,,,/Assets/InstanceIcons/NeoForge.png"));
            _loaderCards.Add(new LoaderCard("fabric", "Fabric", "pack://application:,,,/Assets/InstanceIcons/Fabric.png"));
            _loaderCards.Add(new LoaderCard("quilt", "Quilt", "pack://application:,,,/Assets/InstanceIcons/Quilt.png"));
            LoaderCards.ItemsSource = _loaderCards;

            GameVersionList.ItemsSource = _gameVersionRows;
            LoaderVersionList.ItemsSource = _loaderVersionRows;

            CategoryCombo.Items.Add(new ComboBoxItem { Content = "全部" });
            CategoryCombo.Items.Add(new ComboBoxItem { Content = "正式版" });
            CategoryCombo.Items.Add(new ComboBoxItem { Content = "快照" });
            CategoryCombo.Items.Add(new ComboBoxItem { Content = "愚人节" });
            CategoryCombo.Items.Add(new ComboBoxItem { Content = "远古版本" });
            CategoryCombo.SelectedIndex = 1;

            Visibility = Visibility.Collapsed;
            _ready = true;
        }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        public void Open(LocalInstanceService service, LaunchService launchService)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _launchService = launchService ?? throw new ArgumentNullException(nameof(launchService));

            DisposeServices();
            _catalog = new VersionCatalogService();
            _fabricApi = new FabricApiInstaller();
            _mrpackImportService = new MrpackImportService(new InstalledStateStore());
            _cts.Dispose();
            _cts = new CancellationTokenSource();

            ResetState();
            Visibility = Visibility.Visible;
            ShowStep(Step.Landing);
        }

        /// <summary>Cancels any in-flight operation and hides the view.</summary>
        public void Close()
        {
            _cts.Cancel();
            _creating = false;
            Visibility = Visibility.Collapsed;
            DisposeServices();
            ResetState();
        }

        private void DisposeServices()
        {
            try { _catalog?.Dispose(); } catch { /* best effort */ }
            try { _fabricApi?.Dispose(); } catch { /* best effort */ }
            try { _mrpackImportService?.Dispose(); } catch { /* best effort */ }
            _catalog = null!;
            _fabricApi = null!;
            _mrpackImportService = null!;
        }

        private void ResetState()
        {
            _sessionToken++;
            _result = null;
            _selectedGameVersion = null;
            _selectedLoader = "vanilla";
            _selectedLoaderVersion = string.Empty;
            _defaultName = string.Empty;
            _fabricWarning = null;

            _gameVersions.Clear();
            _loaderVersions.Clear();
            _gameVersionRows.Clear();
            _loaderVersionRows.Clear();
            _gameVersionsLoaded = false;
            _loadingGameVersions = false;
            _loadingLoaderVersions = false;

            NameBox.Text = string.Empty;
            GameSearchBox.Text = string.Empty;
            LoaderVersionSearchBox.Text = string.Empty;
            CategoryCombo.SelectedIndex = 1;

            _importSourcePath = null;
            _importKind = ImportKind.None;
            ImportNameBox.Text = string.Empty;
            ImportPathText.Text = "尚未选择文件夹或 .mrpack 文件";
            ImportStatusText.Text = string.Empty;
            ImportStatusText.Foreground = (Brush)FindResource("CivTextMuted");
            UpdateImportButton();

            RefreshLoaderStepUi();
        }

        // ------------------------------------------------------------------
        // Step / navigation
        // ------------------------------------------------------------------

        private void ShowStep(Step step)
        {
            _step = step;

            LandingPanel.Visibility = step == Step.Landing ? Visibility.Visible : Visibility.Collapsed;
            ImportPanel.Visibility = step == Step.Import ? Visibility.Visible : Visibility.Collapsed;
            GameVersionPanel.Visibility = step == Step.GameVersion ? Visibility.Visible : Visibility.Collapsed;
            LoaderPanel.Visibility = step == Step.Loader ? Visibility.Visible : Visibility.Collapsed;
            LoaderVersionPanel.Visibility = step == Step.LoaderVersion ? Visibility.Visible : Visibility.Collapsed;
            ProgressPanel.Visibility = step == Step.Progress ? Visibility.Visible : Visibility.Collapsed;
            SuccessPanel.Visibility = step == Step.Success ? Visibility.Visible : Visibility.Collapsed;

            bool hideBack = step is Step.Progress or Step.Success;
            BackButton.Visibility = hideBack ? Visibility.Collapsed : Visibility.Visible;

            TitleText.Text = step switch
            {
                Step.Import => "导入已有实例",
                Step.GameVersion => "选择游戏版本",
                Step.Loader => $"安装新游戏 - {_selectedGameVersion?.Name ?? string.Empty}",
                Step.LoaderVersion => $"安装新游戏 - 选择 {LoaderDisplay(_selectedLoader)} 版本",
                Step.Progress => "正在创建实例",
                Step.Success => "实例已创建",
                _ => "创建实例",
            };
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            switch (_step)
            {
                case Step.Landing:
                    BackRequested?.Invoke();
                    break;
                case Step.Import:
                    ShowStep(Step.Landing);
                    break;
                case Step.GameVersion:
                    ShowStep(Step.Landing);
                    break;
                case Step.Loader:
                    ShowStep(Step.GameVersion);
                    break;
                case Step.LoaderVersion:
                    ShowStep(Step.Loader);
                    break;
            }
        }

        private void LandingNewButton_Click(object sender, RoutedEventArgs e)
        {
            ShowStep(Step.GameVersion);
            if (!_gameVersionsLoaded)
                _ = LoadGameVersionsAsync();
            else
                ApplyGameVersionFilter();
        }

        private void LandingImportButton_Click(object sender, RoutedEventArgs e)
        {
            ShowStep(Step.Import);
        }

        private void LandingBackLink_Click(object sender, RoutedEventArgs e)
        {
            BackRequested?.Invoke();
        }

        // ------------------------------------------------------------------
        // Step: import existing
        // ------------------------------------------------------------------

        private void ImportChooseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择实例文件夹",
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return;

            string? folder = dialog.FolderName;
            if (string.IsNullOrWhiteSpace(folder))
                return;

            _importKind = ImportKind.Folder;
            _importSourcePath = folder;
            ImportPathText.Text = folder;

            string folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(folderName))
                ImportNameBox.Text = folderName;

            ImportStatusText.Text = string.Empty;
            ImportStatusText.Foreground = (Brush)FindResource("CivTextMuted");
            UpdateImportButton();
        }

        private void ImportChooseMrpackButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 Modrinth 整合包",
                Filter = "Modrinth 整合包 (*.mrpack)|*.mrpack",
                CheckFileExists = true,
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return;

            SelectMrpackFile(dialog.FileName);
        }

        /// <summary>
        /// Preselects a local <c>.mrpack</c> file for import (used by the picker and by the main
        /// window's drag-and-drop). Validates the extension, switches to the Import step, and shows
        /// an inline error (without creating an instance) for invalid files.
        /// </summary>
        public void SelectMrpackFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                _importSourcePath = null;
                _importKind = ImportKind.None;
                ShowImportError("未选择 .mrpack 文件。");
                return;
            }

            if (!File.Exists(path))
            {
                _importSourcePath = null;
                _importKind = ImportKind.None;
                ShowImportError("所选 .mrpack 文件不存在。");
                ShowStep(Step.Import);
                return;
            }

            if (!path.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
            {
                _importSourcePath = null;
                _importKind = ImportKind.None;
                ShowImportError("请选择 .mrpack 整合包文件。");
                ShowStep(Step.Import);
                return;
            }

            _importKind = ImportKind.Mrpack;
            _importSourcePath = path;
            ImportPathText.Text = path;

            string stem = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrWhiteSpace(stem))
                ImportNameBox.Text = stem;

            ImportStatusText.Text = string.Empty;
            ImportStatusText.Foreground = (Brush)FindResource("CivTextMuted");
            ShowStep(Step.Import);
            UpdateImportButton();
        }

        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_importing)
                return;

            string source = _importSourcePath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(source))
            {
                ShowImportError("请先选择要导入的实例文件夹或 .mrpack 文件。");
                return;
            }

            if (_importKind == ImportKind.Mrpack)
                await RunMrpackImportAsync();
            else
                await RunFolderImportAsync();
        }

        private async Task RunFolderImportAsync()
        {
            string source = _importSourcePath ?? string.Empty;
            if (!Directory.Exists(source))
            {
                ShowImportError("请先选择有效的实例文件夹。");
                return;
            }

            _importing = true;
            SetImportBusy(true);

            string name = ImportNameBox.Text?.Trim() ?? string.Empty;
            var progress = new Progress<ImportProgress>(ReportImportProgress);

            try
            {
                await _importService.ImportAsync(source, name, progress, _cts.Token);
                InstanceCreated?.Invoke();
            }
            catch (InstanceImportException ex)
            {
                ShowImportError(ex.Message);
            }
            catch (OperationCanceledException)
            {
                // Leaving the view; the partial target was already rolled back by the service.
            }
            catch (Exception ex)
            {
                ShowImportError("导入实例时发生未知错误：" + ex.Message);
            }
            finally
            {
                _importing = false;
                SetImportBusy(false);
            }
        }

        private async Task RunMrpackImportAsync()
        {
            string source = _importSourcePath ?? string.Empty;

            _importing = true;
            SetImportBusy(true);

            string name = ImportNameBox.Text?.Trim() ?? string.Empty;
            var progress = new Progress<ImportProgress>(ReportImportProgress);

            try
            {
                await _mrpackImportService.ImportAsync(source, name, progress, _cts.Token);
                InstanceCreated?.Invoke();
            }
            catch (InstanceImportException ex)
            {
                ShowImportError(ex.Message);
            }
            catch (OperationCanceledException)
            {
                // Leaving the view; the partial target was already rolled back by the service.
            }
            catch (Exception ex)
            {
                ShowImportError("导入 .mrpack 整合包时发生未知错误：" + ex.Message);
            }
            finally
            {
                _importing = false;
                SetImportBusy(false);
            }
        }

        private void ReportImportProgress(ImportProgress p)
        {
            if (string.IsNullOrWhiteSpace(p.Message))
                return;

            ImportStatusText.Text = p.Message;
            ImportStatusText.Foreground = (Brush)FindResource("CivTextMuted");
        }

        private void SetImportBusy(bool busy)
        {
            ImportChooseButton.IsEnabled = !busy;
            ImportChooseMrpackButton.IsEnabled = !busy;
            ImportNameBox.IsEnabled = !busy;
            ImportButton.Content = busy ? "正在导入…" : "导入";
            ImportButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(_importSourcePath);

            if (busy)
            {
                ImportStatusText.Text = "正在导入…";
                ImportStatusText.Foreground = (Brush)FindResource("CivTextMuted");
            }
        }

        private void ShowImportError(string message)
        {
            ImportStatusText.Text = message;
            ImportStatusText.Foreground = (Brush)FindResource("CivDanger");
            UpdateImportButton();
        }

        private void UpdateImportButton()
        {
            ImportButton.IsEnabled = !_importing && !string.IsNullOrWhiteSpace(_importSourcePath);
        }

        // ------------------------------------------------------------------
        // Step 1: game versions
        // ------------------------------------------------------------------

        private async void RefreshVersionsButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadGameVersionsAsync();
        }

        private async Task LoadGameVersionsAsync()
        {
            if (_loadingGameVersions) return;
            _loadingGameVersions = true;
            int token = _sessionToken;

            GameVersionLoading.Visibility = Visibility.Visible;
            GameVersionError.Visibility = Visibility.Collapsed;
            GameVersionList.Visibility = Visibility.Collapsed;
            GameVersionEmpty.Visibility = Visibility.Collapsed;

            IReadOnlyList<MinecraftVersionInfo> versions;
            try
            {
                versions = await _catalog.GetMinecraftVersionsAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                // View left / closed while loading; ResetState already reset the busy flag.
                return;
            }
            catch (Exception)
            {
                if (token != _sessionToken) return;
                _loadingGameVersions = false;
                _gameVersions.Clear();
                ApplyGameVersionFilter();
                GameVersionErrorText.Text = "获取 Minecraft 版本列表失败。请检查网络后重试。";
                GameVersionError.Visibility = Visibility.Visible;
                GameVersionLoading.Visibility = Visibility.Collapsed;
                return;
            }

            if (token != _sessionToken) return;
            _loadingGameVersions = false;
            _gameVersions.Clear();
            _gameVersions.AddRange(versions);
            _gameVersionsLoaded = true;
            ApplyGameVersionFilter();
            GameVersionLoading.Visibility = Visibility.Collapsed;
        }

        private void ApplyGameVersionFilter()
        {
            string query = GameSearchBox?.Text?.Trim() ?? string.Empty;
            int categoryIndex = CategoryCombo?.SelectedIndex ?? 1;

            IEnumerable<MinecraftVersionInfo> filtered = _gameVersions;

            filtered = categoryIndex switch
            {
                1 => filtered.Where(v => v.IsRelease),
                2 => filtered.Where(v => v.IsSnapshot),
                3 => filtered.Where(v => v.IsAprilFools),
                4 => filtered.Where(v => v.IsOld),
                _ => filtered,
            };

            if (query.Length > 0)
                filtered = filtered.Where(v => v.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

            _gameVersionRows.Clear();
            foreach (MinecraftVersionInfo version in filtered)
                _gameVersionRows.Add(new GameVersionRow(version));

            bool empty = _gameVersionRows.Count == 0;
            GameVersionList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            GameVersionEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        }

        private void GameSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (GameSearchPlaceholder == null) return;
            GameSearchPlaceholder.Visibility = string.IsNullOrEmpty(GameSearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            if (_ready) ApplyGameVersionFilter();
        }

        private void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CategoryCombo == null) return;
            if (_ready) ApplyGameVersionFilter();
        }

        private void GameVersionRow_Click(object sender, RoutedEventArgs e)
        {
            if (_step != Step.GameVersion) return;
            if (sender is Button { DataContext: GameVersionRow row })
            {
                SetGameVersion(row);
                ShowStep(Step.Loader);
            }
        }

        private void SetGameVersion(GameVersionRow row)
        {
            _selectedGameVersion = row;

            // Reset the loader selection for the new game version, then regenerate the
            // default instance name (game version + selected loader/version).
            SelectLoader("vanilla");
            RefreshDefaultName();
        }

        // ------------------------------------------------------------------
        // Step 2: loaders
        // ------------------------------------------------------------------

        private void LoaderCard_Click(object sender, RoutedEventArgs e)
        {
            if (_step != Step.Loader) return;
            if (sender is not Button { DataContext: LoaderCard card }) return;

            if (card.Key == "vanilla")
            {
                SelectLoader("vanilla");
                return;
            }

            SelectLoader(card.Key);
            OpenLoaderVersionStep();
        }

        private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Name is auto-defaulted on install when blank; nothing else to track.
        }

        private void SelectLoader(string key)
        {
            _selectedLoader = key;
            _selectedLoaderVersion = string.Empty;
            RefreshLoaderStepUi();
            RefreshDefaultName();
        }

        /// <summary>
        /// Builds the automatic instance name from the selected game, loader and loader version.
        /// Vanilla uses only the game version; modded instances append the loader name and version.
        /// </summary>
        private string BuildDefaultInstanceName()
        {
            string game = _selectedGameVersion?.Name?.Trim() ?? string.Empty;
            if (game.Length == 0)
                return string.Empty;

            if (_selectedLoader is "" or "vanilla")
                return game;

            string loader = LoaderDisplay(_selectedLoader);
            string version = _selectedLoaderVersion?.Trim() ?? string.Empty;
            return version.Length > 0 ? $"{game} {loader} {version}" : $"{game} {loader}";
        }

        /// <summary>
        /// Updates the name box only while the user has not overridden the automatic name.
        /// </summary>
        private void RefreshDefaultName()
        {
            if (NameBox == null)
                return;

            string newDefault = BuildDefaultInstanceName();
            string current = NameBox.Text?.Trim() ?? string.Empty;

            if (current.Length == 0 || string.Equals(current, _defaultName, StringComparison.Ordinal))
                NameBox.Text = newDefault;

            _defaultName = newDefault;
        }

        private void RefreshLoaderStepUi()
        {
            UpdateLoaderCards();
            UpdateFabricApiCard();
            UpdateInstallButton();
            UpdateInstallHint();
        }

        private void UpdateLoaderCards()
        {
            foreach (LoaderCard card in _loaderCards)
            {
                if (card.Key == "vanilla")
                {
                    card.IsSelected = _selectedLoader == "vanilla";
                    card.Status = card.IsSelected ? "已选择" : "不安装";
                    continue;
                }

                card.IsSelected = _selectedLoader == card.Key;
                card.Status = card.IsSelected && !string.IsNullOrWhiteSpace(_selectedLoaderVersion)
                    ? _selectedLoaderVersion
                    : "选择版本";
            }
        }

        private void UpdateFabricApiCard()
        {
            bool active = _selectedLoader is "fabric" or "quilt";
            FabricApiStatusText.Text = active ? "自动安装 Fabric API" : "不安装";
            FabricApiStatusText.Foreground = (Brush)FindResource(active ? "CivAccent" : "CivTextMuted");
            FabricApiCheckGlyph.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            FabricApiCard.Opacity = active ? 1.0 : 0.55;
        }

        private void UpdateInstallButton()
        {
            bool valid = _selectedLoader == "vanilla"
                || !string.IsNullOrWhiteSpace(_selectedLoaderVersion);
            InstallButton.IsEnabled = valid;
        }

        private void UpdateInstallHint()
        {
            InstallHintText.Text = _selectedLoader is "fabric" or "quilt"
                ? "将自动安装 Fabric API；实例名称冲突会自动处理。"
                : "实例名称冲突会自动处理。";
        }

        // ------------------------------------------------------------------
        // Step 3: loader versions
        // ------------------------------------------------------------------

        private void OpenLoaderVersionStep()
        {
            ShowStep(Step.LoaderVersion);
            _ = LoadLoaderVersionsAsync();
        }

        private async void RefreshLoaderVersionsButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadLoaderVersionsAsync();
        }

        private async Task LoadLoaderVersionsAsync()
        {
            if (_loadingLoaderVersions) return;

            string loader = _selectedLoader;
            string gameVersion = _selectedGameVersion?.Name ?? string.Empty;
            int token = ++_loaderVersionLoadToken;
            int session = _sessionToken;

            if (loader is not ("fabric" or "quilt" or "forge" or "neoforge"))
                return;
            if (string.IsNullOrWhiteSpace(gameVersion))
                return;

            _loadingLoaderVersions = true;

            LoaderVersionLoading.Visibility = Visibility.Visible;
            LoaderVersionError.Visibility = Visibility.Collapsed;
            LoaderVersionList.Visibility = Visibility.Collapsed;
            LoaderVersionEmpty.Visibility = Visibility.Collapsed;

            IReadOnlyList<LoaderVersionInfo> versions;
            try
            {
                versions = await _catalog.GetLoaderVersionsAsync(loader, gameVersion, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                if (token != _loaderVersionLoadToken || session != _sessionToken) return;
                _loadingLoaderVersions = false;
                _loaderVersions.Clear();
                ApplyLoaderVersionFilter();
                LoaderVersionErrorText.Text = $"获取 {LoaderDisplay(loader)} 版本列表失败。请检查网络后重试。";
                LoaderVersionError.Visibility = Visibility.Visible;
                LoaderVersionLoading.Visibility = Visibility.Collapsed;
                return;
            }

            if (token != _loaderVersionLoadToken || session != _sessionToken) return;
            _loadingLoaderVersions = false;
            _loaderVersions.Clear();
            _loaderVersions.AddRange(versions);
            ApplyLoaderVersionFilter();
            LoaderVersionLoading.Visibility = Visibility.Collapsed;
        }

        private void ApplyLoaderVersionFilter()
        {
            string query = LoaderVersionSearchBox?.Text?.Trim() ?? string.Empty;
            string icon = LoaderIconSource(_selectedLoader);

            _loaderVersionRows.Clear();
            foreach (LoaderVersionInfo version in _loaderVersions)
            {
                if (query.Length > 0 && !version.Version.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;
                _loaderVersionRows.Add(new LoaderVersionRow(version, icon));
            }

            bool empty = _loaderVersionRows.Count == 0;
            LoaderVersionList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            LoaderVersionEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        }

        private void LoaderVersionSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (LoaderVersionSearchPlaceholder == null) return;
            LoaderVersionSearchPlaceholder.Visibility = string.IsNullOrEmpty(LoaderVersionSearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            if (_ready) ApplyLoaderVersionFilter();
        }

        private void LoaderVersionRow_Click(object sender, RoutedEventArgs e)
        {
            if (_step != Step.LoaderVersion) return;
            if (sender is Button { DataContext: LoaderVersionRow row })
            {
                _selectedLoaderVersion = row.Version;
                RefreshLoaderStepUi();
                RefreshDefaultName();
                ShowStep(Step.Loader);
            }
        }

        // ------------------------------------------------------------------
        // Step 4: create + provision
        // ------------------------------------------------------------------

        private async void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (_creating) return;
            await InstallAsync();
        }

        private async void RetryDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_creating) return;
            await InstallAsync();
        }

        private async Task InstallAsync()
        {
            _creating = true;
            _fabricWarning = null;

            ShowStep(Step.Progress);
            ProgressErrorPanel.Visibility = Visibility.Collapsed;
            ProgressStageText.Text = "正在创建实例…";
            ProgressMessageText.Text = string.Empty;
            ProvisionBar.IsIndeterminate = true;

            try
            {
                string name = NameBox.Text?.Trim() ?? string.Empty;
                if (name.Length == 0)
                    name = _defaultName;
                if (name.Length == 0)
                    name = _selectedGameVersion?.Name ?? "新实例";

                string loader = _selectedLoader;
                string loaderVersion = loader == "vanilla" ? string.Empty : _selectedLoaderVersion;
                bool needsFabricApi = loader is "fabric" or "quilt";
                string gameVersion = _selectedGameVersion?.Name ?? string.Empty;

                if (_result == null)
                {
                    _result = _service.Create(new CreateLocalInstanceRequest
                    {
                        DisplayName = name,
                        GameVersion = gameVersion,
                        Loader = loader,
                        LoaderVersion = loaderVersion,
                    });
                }

                ProgressStageText.Text = "正在下载 Minecraft 与加载器文件…";
                var progress = new Progress<LaunchProgress>(ReportProvisionProgress);
                var manifest = new ManifestInstance
                {
                    Id = _result.Id,
                    Name = _result.DisplayName,
                    Revision = 1,
                };
                await _launchService.PrepareInstanceAsync(manifest, progress, _cts.Token);

                if (needsFabricApi)
                {
                    ProgressStageText.Text = "正在下载 Fabric API…";
                    ProvisionBar.IsIndeterminate = true;
                    var fabricProgress = new Progress<FabricApiProgress>(ReportFabricApiProgress);
                    FabricApiResult apiResult = await _fabricApi.InstallAsync(gameVersion, _result.InstanceDirectory, fabricProgress, _cts.Token);
                    if (!apiResult.Success)
                        _fabricWarning = apiResult.ErrorMessage;
                }

                ShowSuccess();
            }
            catch (LocalInstanceException ex)
            {
                ShowProvisionError(ex.Message, instanceCreated: false);
            }
            catch (LaunchException ex)
            {
                ShowProvisionError(ex.Message, instanceCreated: _result != null);
            }
            catch (OperationCanceledException)
            {
                // Leaving; any already-created instance is intentionally kept.
            }
            catch (Exception ex)
            {
                ShowProvisionError("创建实例时发生了未知错误：" + ex.Message, instanceCreated: _result != null);
            }
            finally
            {
                _creating = false;
            }
        }

        private void ShowProvisionError(string message, bool instanceCreated)
        {
            ShowStep(Step.Progress);
            ProgressStageText.Text = instanceCreated ? "下载失败" : "创建失败";
            ProgressMessageText.Text = string.Empty;
            ProgressErrorText.Text = message;
            ProgressErrorHint.Text = instanceCreated
                ? "实例已保留，首次启动时会自动重试下载。"
                : "实例尚未创建，可以重试。";
            ProgressErrorPanel.Visibility = Visibility.Visible;
            ProvisionBar.IsIndeterminate = false;
            ProvisionBar.Value = 0;
        }

        private void ReportProvisionProgress(LaunchProgress p)
        {
            string text = string.IsNullOrWhiteSpace(p.Stage) ? p.Message : $"{p.Stage}：{p.Message}";
            if (!string.IsNullOrWhiteSpace(text))
                ProgressMessageText.Text = text;

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
        }

        private void ReportFabricApiProgress(FabricApiProgress p)
        {
            if (p.TotalBytes is long total && p.BytesDownloaded is long done && total > 0)
            {
                ProgressMessageText.Text = $"正在下载 Fabric API：{FormatUtils.FormatBytes(done)} / {FormatUtils.FormatBytes(total)}";
                ProvisionBar.IsIndeterminate = false;
                ProvisionBar.Maximum = total;
                ProvisionBar.Value = done;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(p.Message))
                    ProgressMessageText.Text = p.Message;
                ProvisionBar.IsIndeterminate = true;
            }
        }

        // ------------------------------------------------------------------
        // Step 5: success
        // ------------------------------------------------------------------

        private void ShowSuccess()
        {
            string name = _result?.DisplayName ?? _defaultName;
            string game = _selectedGameVersion?.Name ?? string.Empty;

            SuccessNameText.Text = name;
            SuccessGameText.Text = $"Minecraft {game}";
            SuccessLoaderText.Text = LoaderDisplayFull(_selectedLoader, _selectedLoaderVersion);

            SuccessWarningText.Text = _fabricWarning != null
                ? $"实例已创建，但 Fabric API 下载失败：{_fabricWarning}。你可以在首次启动前手动放入 mods 文件夹。"
                : string.Empty;
            SuccessWarningText.Visibility = _fabricWarning != null ? Visibility.Visible : Visibility.Collapsed;

            ShowStep(Step.Success);
        }

        private void SuccessReturnListButton_Click(object sender, RoutedEventArgs e)
        {
            InstanceCreated?.Invoke();
        }

        private void ErrorReturnListButton_Click(object sender, RoutedEventArgs e)
        {
            InstanceCreated?.Invoke();
        }

        private void SuccessOpenDirButton_Click(object sender, RoutedEventArgs e)
        {
            string? dir = _result?.InstanceDirectory;
            if (string.IsNullOrWhiteSpace(dir)) return;

            try
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "无法打开目录：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static string LoaderDisplay(string loader) => loader switch
        {
            "vanilla" => "原版",
            "fabric" => "Fabric",
            "forge" => "Forge",
            "neoforge" => "NeoForge",
            "quilt" => "Quilt",
            _ => loader,
        };

        private static string LoaderDisplayFull(string loader, string version)
        {
            string label = LoaderDisplay(loader);
            return string.IsNullOrWhiteSpace(version) ? label : $"{label} {version}";
        }

        private static string LoaderIconSource(string loader) => loader switch
        {
            "fabric" => "pack://application:,,,/Assets/InstanceIcons/Fabric.png",
            "forge" => "pack://application:,,,/Assets/InstanceIcons/Forge.png",
            "neoforge" => "pack://application:,,,/Assets/InstanceIcons/NeoForge.png",
            "quilt" => "pack://application:,,,/Assets/InstanceIcons/Quilt.png",
            _ => "pack://application:,,,/Assets/InstanceIcons/grass-block.png",
        };
    }
}
