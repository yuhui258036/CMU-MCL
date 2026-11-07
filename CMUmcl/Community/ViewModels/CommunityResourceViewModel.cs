// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CMUmcl.Content;

namespace CMUmcl.Community
{
    internal sealed class CommunityResourceViewModel : ObservableObject
    {
        private readonly ResourceCatalog _catalog;
        private readonly ResourceTargets _targets;
        private readonly ResourceInstallService _install;
        private readonly ResourceIconLoader _icons;

        public static CommunityResourceViewModel CreateDefault(InstalledStateStore installedState)
        {
            // The local candidate uses the same source factory as the community page.
            var modrinth = new ModrinthSource();
            ResourceCatalog catalog;
            // Only the accepted local candidate enables the direct source.
#pragma warning disable CS0162
            if (CommunityFeatures.CurseForgeEnabled)
                catalog = new ResourceCatalog(modrinth, new CurseForgeSource());
            else
                catalog = new ResourceCatalog(modrinth);
#pragma warning restore CS0162
            var targets = new ResourceTargets(installedState);
            var mrpack = new MrpackImportService(installedState);
            var install = new ResourceInstallService(targets, mrpack);
            return new CommunityResourceViewModel(catalog, targets, install, new ResourceIconLoader());
        }

        public CommunityResourceViewModel(ResourceCatalog catalog, ResourceTargets targets, ResourceInstallService install, ResourceIconLoader icons)
        {
            _catalog = catalog;
            _targets = targets;
            _install = install;
            _icons = icons;

            SearchCommand = new RelayCommand(_ => _ = SearchAsync(), _ => !IsLoading);
            BackToResultsCommand = new RelayCommand(_ => GoBack());
            OpenVersionCommand = new RelayCommand(p => { if (p is ResourceVersion v) _ = OpenVersionAsync(v); });
            OpenDependencyCommand = new RelayCommand(p => { if (p is DependencyItemViewModel item) _ = OpenDependencyAsync(item); });
            CloseVersionDialogCommand = new RelayCommand(_ => { IsVersionDialogOpen = false; Detail?.CancelDependencyLoad(); });
            FirstPageCommand = new RelayCommand(_ => _ = SearchPageAsync(0), _ => !IsLoading && _page > 0);
            PreviousPageCommand = new RelayCommand(_ => _ = SearchPageAsync(_page - 1), _ => !IsLoading && _page > 0);
            NextPageCommand = new RelayCommand(_ => _ = SearchPageAsync(_page + 1), _ => !IsLoading && _hasMore);
            SelectKindCommand = new RelayCommand(p => { Kind = (ResourceKind)p!; _ = SearchAsync(); });
            OpenDetailCommand = new RelayCommand(p => { if (p is ResourceCardViewModel card) _ = OpenDetailAsync(card); });

            LoaderOptions.Add(new LoaderOption("全部", ""));
            LoaderOptions.Add(new LoaderOption("原版", "vanilla"));
            LoaderOptions.Add(new LoaderOption("Fabric", "fabric"));
            LoaderOptions.Add(new LoaderOption("Forge", "forge"));
            LoaderOptions.Add(new LoaderOption("NeoForge", "neoforge"));
            LoaderOptions.Add(new LoaderOption("Quilt", "quilt"));

            SortOptions.Add(new SortOption("推荐", ResourceSort.Recommended));
            SortOptions.Add(new SortOption("下载量", ResourceSort.Downloads));
            SortOptions.Add(new SortOption("更新时间", ResourceSort.LastUpdated));
            SortOptions.Add(new SortOption("名称", ResourceSort.Name));

            SourceOptions.Add(new SourceOption("全部来源", null));
            SourceOptions.Add(new SourceOption("Modrinth", ResourceSourceId.Modrinth));
            // const feature flag: the CurseForge option is added only once the flag is flipped to true.
#pragma warning disable CS0162
            if (CommunityFeatures.CurseForgeEnabled)
                SourceOptions.Add(new SourceOption("CurseForge", ResourceSourceId.CurseForge));
#pragma warning restore CS0162

            _selectedLoaderOption = LoaderOptions[0];
            _selectedSortOption = SortOptions[0];
            _selectedSourceOption = SourceOptions[0];
        }

        private int _page, _detailRequest, _searchRequest;
        private bool _hasMore;
        private bool _isVersionDialogOpen;
        public bool IsVersionDialogOpen { get => _isVersionDialogOpen; private set => Set(ref _isVersionDialogOpen, value); }
        private readonly Stack<(ResourceCardViewModel Card, ResourceDetailViewModel Detail)> _dependencyParents = new();
        public string BackLabel => _dependencyParents.Count == 0 ? "‹  返回搜索结果" : "‹  返回上级模组";
        public ICommand OpenDependencyCommand { get; }
        public ICommand OpenVersionCommand { get; }
        public async Task OpenVersionAsync(ResourceVersion version)
        {
            if (Detail == null) return;
            if (!Detail.IsInstalling) Detail.SelectedVersion = version;
            IsVersionDialogOpen = true;
            await Detail.EnsureDependenciesAsync().ConfigureAwait(true);
        }
        public async Task OpenDependencyAsync(DependencyItemViewModel item)
        {
            if (Detail == null || SelectedCard == null || Detail.IsInstalling || item.Resolution == null) return;
            var original = Detail;
            var resolved = item.Resolution;
            _dependencyParents.Push((SelectedCard, original));
            original.CancelDependencyLoad();
            SelectedTarget = TargetInstances.FirstOrDefault(t => t.Id == original.SelectedInstance?.Id);
            OnPropertyChanged(nameof(BackLabel));
            await OpenDetailAsync(new ResourceCardViewModel(resolved.Project, CompatibilityHint.Unknown), resolved.PinnedVersion).ConfigureAwait(true);
            if (resolved.PinnedVersion != null && Detail != null) await OpenVersionAsync(Detail.SelectedVersion!).ConfigureAwait(true);
        }
        private void GoBack()
        {
            if (_dependencyParents.Count > 0 && Detail?.IsInstalling == true) { IsVersionDialogOpen = true; return; }
            _detailRequest++; Detail?.CancelDependencyLoad(); IsDetailLoading = false;
            if (_dependencyParents.Count > 0)
            {
                var parent = _dependencyParents.Pop(); SelectedCard = parent.Card; Detail = parent.Detail;
                IsDetailPage = true; IsVersionDialogOpen = true;
                _ = Detail.EnsureDependenciesAsync();
            }
            else { IsDetailPage = false; IsVersionDialogOpen = false; }
            OnPropertyChanged(nameof(BackLabel));
        }
        public ICommand CloseVersionDialogCommand { get; }
        public ICommand BackToResultsCommand { get; }
        public ICommand FirstPageCommand { get; }
        public ICommand PreviousPageCommand { get; }
        public ICommand NextPageCommand { get; }
        public string PageLabel => $"第 {_page + 1} 页";
        public string ResultSummary => IsLoading && Results.Count > 0 ? $"本页 {Results.Count} 项资源 · 正在加载其他来源…" : $"本页 {Results.Count} 项资源";
        /// <summary>The large spinner only covers an empty list; partial results stay visible.</summary>
        public bool ShowLoadingOverlay => IsLoading && Results.Count == 0;
        private bool _isDetailPage;
        public bool IsDetailPage { get => _isDetailPage; private set { if (Set(ref _isDetailPage, value)) OnPropertyChanged(nameof(ShowResults)); } }
        public bool ShowResults => !IsDetailPage;
        private bool _isDetailLoading;
        public bool IsDetailLoading { get => _isDetailLoading; private set => Set(ref _isDetailLoading, value); }
        private string _detailError = "";
        public string DetailError { get => _detailError; private set { if (Set(ref _detailError, value)) OnPropertyChanged(nameof(HasDetailError)); } }
        public bool HasDetailError => !string.IsNullOrEmpty(DetailError);
        public ICommand SearchCommand { get; }
        public ICommand SelectKindCommand { get; }
        public ICommand OpenDetailCommand { get; }

        public ObservableCollection<ResourceCardViewModel> Results { get; } = new();
        public ObservableCollection<ResourceTarget> TargetInstances { get; } = new();

        private ResourceTarget? _selectedTarget;
        public ResourceTarget? SelectedTarget
        {
            get => _selectedTarget;
            set { if (Set(ref _selectedTarget, value)) OnPropertyChanged(nameof(CanFilterCompatibility)); }
        }
        public bool ShowInstanceFilters => Kind!=ResourceKind.Modpack;
        public bool CanFilterCompatibility => Kind!=ResourceKind.Modpack && SelectedTarget != null;

        /// <summary>Re-enumerates installable instances (broken targets excluded); selects the first.</summary>
        public void RefreshTargets()
        {
            string? selectedId = SelectedTarget?.Id;
            TargetInstances.Clear();
            foreach (ResourceTarget t in _targets.EnumerateInstances())
                if (t.Error == null)
                    TargetInstances.Add(t);
            SelectedTarget = TargetInstances.FirstOrDefault(t => t.Id == selectedId) ?? TargetInstances.FirstOrDefault();
        }

        private ResourceKind _kind = ResourceKind.Mod;
        public ResourceKind Kind { get => _kind; set { if (Set(ref _kind, value)) { OnPropertyChanged(nameof(ShowLoaderFilter));OnPropertyChanged(nameof(CanFilterCompatibility));OnPropertyChanged(nameof(ShowInstanceFilters)); } } }
        public bool ShowLoaderFilter => Kind == ResourceKind.Mod;

        private string _searchText = "";
        public string SearchText { get => _searchText; set => Set(ref _searchText, value); }

        private string _gameVersion = "";
        public string GameVersion { get => _gameVersion; set => Set(ref _gameVersion, value); }

        private string _loader = "";
        public string Loader { get => _loader; set => Set(ref _loader, value); }

        private ResourceSourceId? _sourceFilter;
        public ResourceSourceId? SourceFilter { get => _sourceFilter; set => Set(ref _sourceFilter, value); }

        private ResourceSort _sort = ResourceSort.Recommended;
        public ResourceSort Sort { get => _sort; set => Set(ref _sort, value); }

        private bool _onlyCompatible = true;
        public bool OnlyCompatible { get => _onlyCompatible; set => Set(ref _onlyCompatible, value); }

        private bool _isLoading;
        public bool IsLoading { get => _isLoading; private set { if (Set(ref _isLoading, value)) { OnPropertyChanged(nameof(HasEmptyMessage)); OnPropertyChanged(nameof(ShowLoadingOverlay)); OnPropertyChanged(nameof(ResultSummary)); foreach (var command in new[] { SearchCommand, FirstPageCommand, PreviousPageCommand, NextPageCommand }) (command as RelayCommand)?.RaiseCanExecuteChanged(); } } }

        private string _emptyMessage = "输入关键字或选择分类开始搜索";
        public string EmptyMessage { get => _emptyMessage; private set { if (Set(ref _emptyMessage, value)) OnPropertyChanged(nameof(HasEmptyMessage)); } }
        public bool HasEmptyMessage => !IsLoading && !HasError && !string.IsNullOrEmpty(_emptyMessage);

        private string _errorMessage = "";
        public string ErrorMessage { get => _errorMessage; private set { if (Set(ref _errorMessage, value)) { OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(HasEmptyMessage)); } } }
        public bool HasError => !string.IsNullOrEmpty(_errorMessage);

        private string _partialMessage = "";
        public string PartialMessage { get => _partialMessage; private set { if (Set(ref _partialMessage, value)) OnPropertyChanged(nameof(HasPartialMessage)); } }
        public bool HasPartialMessage => !string.IsNullOrEmpty(_partialMessage);

        private ResourceCardViewModel? _selectedCard;
        public ResourceCardViewModel? SelectedCard { get => _selectedCard; private set => Set(ref _selectedCard, value); }

        private ResourceDetailViewModel? _detail;
        public ResourceDetailViewModel? Detail
        {
            get => _detail;
            private set { if (Set(ref _detail, value)) OnPropertyChanged(nameof(HasDetail)); }
        }
        public bool HasDetail => _detail != null;

        // ---------------------------------------------------------------- XAML option lists

        public ObservableCollection<LoaderOption> LoaderOptions { get; } = new();
        public ObservableCollection<SortOption> SortOptions { get; } = new();
        public ObservableCollection<SourceOption> SourceOptions { get; } = new();

        private LoaderOption? _selectedLoaderOption;
        public LoaderOption? SelectedLoaderOption
        {
            get => _selectedLoaderOption;
            set { if (Set(ref _selectedLoaderOption, value)) Loader = value?.Value ?? ""; }
        }

        private SortOption? _selectedSortOption;
        public SortOption? SelectedSortOption
        {
            get => _selectedSortOption;
            set { if (Set(ref _selectedSortOption, value)) Sort = value?.Value ?? ResourceSort.Recommended; }
        }

        private SourceOption? _selectedSourceOption;
        public SourceOption? SelectedSourceOption
        {
            get => _selectedSourceOption;
            set { if (Set(ref _selectedSourceOption, value)) SourceFilter = value?.Value; }
        }

        // ---------------------------------------------------------------- search

        private bool _hasSearched;
        private Task? _firstLoad;
        public Task EnsureLoadedAsync()
        {
            if (_hasSearched) return _firstLoad ?? Task.CompletedTask;
            return _firstLoad = SearchAsync();
        }
        public Task SearchAsync() => SearchPageAsync(0);
        private async Task SearchPageAsync(int pageIndex)
        {
            _hasSearched = true;
            int request = ++_searchRequest;
            IsLoading = true;
            ErrorMessage = "";
            PartialMessage = "";
            Results.Clear();
            try
            {
                var query = new ResourceQuery
                {
                    SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
                    Kind = Kind,
                    GameVersion = string.IsNullOrWhiteSpace(GameVersion) ? null : GameVersion.Trim(),
                    Loader = string.IsNullOrWhiteSpace(Loader) ? null : Loader.Trim(),
                    Source = SourceFilter,
                    Sort = Sort,
                    Index = Math.Max(0, pageIndex) * (SourceFilter == null ? 25 : 50),
                    PageSize = 50,
                };
                ResourceTarget? target = Kind==ResourceKind.Modpack?null:SelectedTarget;
                var cards = new Dictionary<(ResourceSourceId, string), ResourceCardViewModel>();
                // Shows whatever has arrived; cards are reused so their icons are not fetched twice.
                void Show(IReadOnlyList<ResourceProject> projects)
                {
                    if (request != _searchRequest) return;
                    var next = new List<ResourceCardViewModel>();
                    foreach (ResourceProject p in projects)
                    {
                        CompatibilityHint hint = target == null
                            ? CompatibilityHint.Unknown
                            : CompatibilityEvaluator.ProjectHint(p, Kind, target.Loader, target.Game);
                        if (OnlyCompatible && target != null && hint == CompatibilityHint.Incompatible) continue;
                        if (!cards.TryGetValue((p.Source, p.Id), out var card))
                        {
                            card = new ResourceCardViewModel(p, hint, Kind!=ResourceKind.Modpack);
                            cards[(p.Source, p.Id)] = card;
                            _ = LoadCardIconAsync(card);
                        }
                        next.Add(card);
                    }
                    if (next.SequenceEqual(Results)) return;
                    Results.Clear();
                    foreach (var card in next) Results.Add(card);
                    OnPropertyChanged(nameof(ResultSummary));
                    OnPropertyChanged(nameof(ShowLoadingOverlay));
                }
                var ui = SynchronizationContext.Current;
                Action<IReadOnlyList<ResourceProject>>? partial = ui == null ? null : projects => ui.Post(_ => Show(projects), null);
                CatalogPage page = await _catalog.SearchAsync(query, target?.Loader, target?.Game, CancellationToken.None, partial).ConfigureAwait(true);

                if (request != _searchRequest) return;
                _page = Math.Max(0, pageIndex); _hasMore = page.HasMore;
                OnPropertyChanged(nameof(PageLabel));
                Show(page.Projects);

                OnPropertyChanged(nameof(ResultSummary));
                PartialMessage = BuildPartialMessage(page.Failures);
                EmptyMessage = Results.Count == 0 ? "未找到匹配的资源，请调整关键字或版本。" : "";
            }
            catch (Exception ex)
            {
                if (request == _searchRequest) ErrorMessage = ex.Message;
            }
            finally
            {
                if (request == _searchRequest) IsLoading = false;
            }
        }

        /// <summary>
        /// Builds a direct-source failure banner without asking the user to re-login to CSU.
        /// </summary>
        internal static string BuildPartialMessage(IReadOnlyList<CommunitySourceException> failures)
        {
            if (failures == null || failures.Count == 0)
                return "";

            foreach (CommunitySourceException failure in failures)
            {
                if (failure.SourceId != ResourceSourceId.CurseForge)
                    continue;

                if (failure.HttpStatus == 401)
                    return "CurseForge 访问凭据无效，请联系启动器维护者；已仅显示 Modrinth 结果。";
                if (failure.HttpStatus == 429)
                    return "CurseForge 请求过于频繁，请稍后再试；已仅显示 Modrinth 结果。";
                return "CurseForge 服务暂时不可用，已仅显示 Modrinth 结果。";
            }

            // Only non-CurseForge failures (e.g. Modrinth) → generic partial-source wording.
            return "部分来源暂时不可用，已仅显示可用来源的结果。";
        }

        private async Task LoadCardIconAsync(ResourceCardViewModel card)
        {
            try
            {
                BitmapSource? icon = await _icons.LoadAsync(card.IconUrl, CancellationToken.None).ConfigureAwait(true);
                card.SetIcon(icon);
            }
            catch
            {
                // Icon failures are non-fatal; the card keeps its placeholder.
            }
        }

        // ---------------------------------------------------------------- detail

        public async Task<IReadOnlyList<ResourceVersion>> LoadVersionsForAsync(ResourceCardViewModel card, CancellationToken ct)
        {
            var query = new ResourceQuery { Kind = Kind };
            return await _catalog.GetVersionsAsync(card.Project.Source, card.Project.Id, query, ct).ConfigureAwait(false);
        }

        public async Task OpenDetailAsync(ResourceCardViewModel card, ResourceVersion? pinnedVersion = null)
        {
            if (Detail?.IsInstalling == true) { IsDetailPage = true; IsVersionDialogOpen = true; return; }
            int request = ++_detailRequest;
            Detail?.CancelDependencyLoad();
            SelectedCard = card;
            Detail = null;
            IsVersionDialogOpen = false;
            IsDetailPage = true;
            IsDetailLoading = true;
            DetailError = "";
            try
            {
                ResourceKind kind = Kind;
                ResourceTarget? preferred = SelectedTarget;
                var all = pinnedVersion != null ? (IReadOnlyList<ResourceVersion>)new[] { pinnedVersion } : await LoadVersionsForAsync(card, CancellationToken.None).ConfigureAwait(true);
                if (request != _detailRequest) return;
                var detail = new ResourceDetailViewModel(card.Project, _install, _targets);
                detail.LoadInstances(kind, preferred);
                detail.LoadVersions(all, kind, detail.SelectedInstance);
                detail.BindInstall(kind, () => detail.SelectedVersion?.Files.OrderByDescending(f => f.Primary).FirstOrDefault(),
                    card.Project.Source == ResourceSourceId.CurseForge
                        ? (Func<ResourceVersion, ResourceFile, CancellationToken, Task<ResourceFile?>>)((v, f, ct) => _catalog.ResolveDownloadAsync(card.Project, v, f, ct))
                        : null);
                if (kind == ResourceKind.Mod)
                    detail.ConfigureDependencies((dependency, ct) => _catalog.ResolveDependencyAsync(card.Project.Source, dependency, ct), _icons.LoadAsync);
                detail.ConfirmOverwrite = path => LauncherPrompt.ShowForApp(
                    "文件已存在：\n" + path + "\n\n是否覆盖？", "确认覆盖", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                detail.InstallProgress = new Progress<CommunityInstallProgress>(detail.ReportProgress);
                Detail = detail;
            }
            catch (Exception ex) { if (request == _detailRequest) DetailError = ex.Message; }
            finally { if (request == _detailRequest) IsDetailLoading = false; }
        }

    }

    internal sealed class LoaderOption
    {
        public string Label { get; }
        public string Value { get; }
        public LoaderOption(string label, string value) { Label = label; Value = value; }
    }

    internal sealed class SortOption
    {
        public string Label { get; }
        public ResourceSort Value { get; }
        public SortOption(string label, ResourceSort value) { Label = label; Value = value; }
    }

    internal sealed class SourceOption
    {
        public string Label { get; }
        public ResourceSourceId? Value { get; }
        public SourceOption(string label, ResourceSourceId? value) { Label = label; Value = value; }
    }
}
