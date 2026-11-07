// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace CMUmcl.Community
{
    internal sealed class ResourceDetailViewModel : ObservableObject
    {
        private readonly ResourceInstallService _install;
        private readonly ResourceTargets _targets;
        private readonly ResourceProject _project;

        private IReadOnlyList<ResourceVersion> _allVersions = Array.Empty<ResourceVersion>();
        private ResourceKind _kind;
        private ResourceTarget? _target;
        private Func<ResourceVersion, ResourceFile, CancellationToken, Task<ResourceFile?>>? _resolveDownload;
        public string SourceLabel => _project.SourceLabel;

        public ResourceDetailViewModel(ResourceProject project, ResourceInstallService install, ResourceTargets targets)
        {
            _project = project;
            _install = install;
            _targets = targets;
        }

        public string Title => _project.Title;
        public string Description => _project.Description;
        public string? ProjectUrl => CommunityHosts.IsAllowedProjectLink(_project.ProjectUrl) ? _project.ProjectUrl : null;
        public bool HasProjectUrl => !string.IsNullOrEmpty(ProjectUrl);

        internal bool HasPlatformProjectUrl(ResourceSourceId source)
        {
            if (ProjectUrl == null) return false;
            var host = new Uri(ProjectUrl).Host;
            return source == ResourceSourceId.Modrinth ? host.Equals("modrinth.com", StringComparison.OrdinalIgnoreCase)
                : host.Equals("www.curseforge.com", StringComparison.OrdinalIgnoreCase) || host.Equals("legacy.curseforge.com", StringComparison.OrdinalIgnoreCase);
        }
        internal string GetPlatformUrl(ResourceSourceId source)
        {
            if (HasPlatformProjectUrl(source)) return ProjectUrl!;
            string query = Uri.EscapeDataString(Title);
            string category = _kind switch { ResourceKind.Modpack => "modpacks", ResourceKind.ResourcePack => "resourcepacks", ResourceKind.Shader => "shaders", ResourceKind.Datapack => "datapacks", _ => "mods" };
            return source == ResourceSourceId.Modrinth ? "https://modrinth.com/" + category + "?q=" + query
                : "https://www.curseforge.com/minecraft/search?search=" + query;
        }

        public ObservableCollection<ResourceVersion> Versions { get; } = new();
        public ObservableCollection<ResourceTarget> Instances { get; } = new();
        public ObservableCollection<string> Worlds { get; } = new();

        private ResourceVersion? _selectedVersion;
        public ResourceVersion? SelectedVersion { get => _selectedVersion; set { if (Set(ref _selectedVersion, value)) { bool refresh = _dependencyViewRequested; CancelDependencyLoad(); _dependencyViewRequested = refresh; Dependencies.Clear(); _checkedDependencyVersion = null; Recompute(); OnPropertyChanged(nameof(HasDependencies)); OnPropertyChanged(nameof(DependencyNotice)); if (refresh && value != null) _ = EnsureDependenciesAsync(); } } }

        private ResourceTarget? _selectedInstance;
        public ResourceTarget? SelectedInstance
        {
            get => _selectedInstance;
            set
            {
                if (Set(ref _selectedInstance, value))
                {
                    RefreshWorlds();
                    ApplyVersionFilter();
                    Recompute();
                }
            }
        }

        private string? _selectedWorld;
        public string? SelectedWorld { get => _selectedWorld; set { if (Set(ref _selectedWorld, value)) Recompute(); } }

        private bool _showAllVersions;
        public bool ShowAllVersions
        {
            get => _showAllVersions;
            set
            {
                if (Set(ref _showAllVersions, value))
                {
                    ApplyVersionFilter();
                    Recompute();
                }
            }
        }

        private string _status = "";
        public string Status { get => _status; private set => Set(ref _status, value); }

        private bool _canInstall;
        public bool CanInstall { get => _canInstall && !IsInstalling && !IsLoadingDependencies && (_loadDependency == null || SelectedVersion?.Dependencies.Count == 0 || ReferenceEquals(_checkedDependencyVersion, SelectedVersion)); private set => Set(ref _canInstall, value); }

        private string _hardBlock = "";
        public string HardBlock { get => _hardBlock; private set { if (Set(ref _hardBlock, value)) OnPropertyChanged(nameof(HasHardBlock)); } }
        public bool HasHardBlock => !string.IsNullOrEmpty(HardBlock);

        private IReadOnlyList<string> _softWarnings = Array.Empty<string>();
        public IReadOnlyList<string> SoftWarnings
        {
            get => _softWarnings;
            private set
            {
                if (Set(ref _softWarnings, value))
                {
                    OnPropertyChanged(nameof(HasSoftWarnings));
                    OnPropertyChanged(nameof(SoftWarningText));
                }
            }
        }
        public bool HasSoftWarnings => _softWarnings.Count > 0;
        public string SoftWarningText => string.Join(Environment.NewLine, _softWarnings);

        private bool _noCompatibleVersion;
        public bool NoCompatibleVersion { get => _noCompatibleVersion; private set => Set(ref _noCompatibleVersion, value); }

        private bool _isInstalling;
        public bool IsInstalling { get => _isInstalling; private set { if (Set(ref _isInstalling, value)) { OnPropertyChanged(nameof(CanInstall)); OnPropertyChanged(nameof(CanChangeSelection)); } } }

        public bool CanChangeSelection => !IsInstalling;
        private double _progressValue;
        public double ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }

        private string _progressText = "";
        public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

        public ObservableCollection<DependencyItemViewModel> Dependencies { get; } = new();
        private Func<ResourceDependency, CancellationToken, Task<ResolvedDependency>>? _loadDependency;
        private Func<string?, CancellationToken, Task<System.Windows.Media.Imaging.BitmapSource?>>? _loadDependencyIcon;
        private CancellationTokenSource? _dependencyLifetime;
        private Task? _dependencyTask;
        private bool _dependencyViewRequested;
        private int _dependencyGeneration;
        private ResourceVersion? _checkedDependencyVersion;
        private bool _isLoadingDependencies;
        public bool IsLoadingDependencies { get => _isLoadingDependencies; private set { if (Set(ref _isLoadingDependencies, value)) OnPropertyChanged(nameof(CanInstall)); } }
        public bool HasDependencies => _loadDependency != null && SelectedVersion?.Dependencies.Count > 0;
        public string DependencyNotice => SelectedVersion?.Dependencies.Any(d => d.Kind == DependencyKind.Required) == true
            ? "安装按钮只安装当前模组；缺少的必需前置需另行安装。"
            : "相关资源";
        private string _dependencyError = "";
        public string DependencyError { get => _dependencyError; private set { if (Set(ref _dependencyError, value)) OnPropertyChanged(nameof(HasDependencyError)); } }
        public bool HasDependencyError => !string.IsNullOrEmpty(DependencyError);
        public ICommand RetryDependenciesCommand { get; private set; } = null!;
        public void ConfigureDependencies(Func<ResourceDependency, CancellationToken, Task<ResolvedDependency>> loader,
            Func<string?, CancellationToken, Task<System.Windows.Media.Imaging.BitmapSource?>> icons)
        {
            _loadDependency = loader; _loadDependencyIcon = icons;
            RetryDependenciesCommand = new RelayCommand(_ => { CancelDependencyLoad(); _checkedDependencyVersion = null; _ = EnsureDependenciesAsync(); });
            OnPropertyChanged(nameof(CanInstall)); OnPropertyChanged(nameof(HasDependencies)); OnPropertyChanged(nameof(DependencyNotice));
        }
        public void CancelDependencyLoad()
        {
            _dependencyViewRequested = false;
            _dependencyGeneration++;
            _dependencyLifetime?.Cancel(); _dependencyLifetime?.Dispose(); _dependencyLifetime = null;
            _dependencyTask = null; IsLoadingDependencies = false;
        }
        public Task EnsureDependenciesAsync()
        {
            var version = SelectedVersion;
            if (_loadDependency == null || version == null) return Task.CompletedTask;
            _dependencyViewRequested = true;
            if (ReferenceEquals(_checkedDependencyVersion, version)) return Task.CompletedTask;
            if (_dependencyTask != null) return _dependencyTask;
            return _dependencyTask = LoadDependenciesAsync(version);
        }
        private async Task LoadDependenciesAsync(ResourceVersion version)
        {
            int generation = ++_dependencyGeneration;
            _dependencyLifetime = new CancellationTokenSource(); var ct = _dependencyLifetime.Token;
            IsLoadingDependencies = true; DependencyError = ""; Dependencies.Clear();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var rows = version.Dependencies.Where(d => seen.Add(d.Identity)).OrderBy(d => d.Kind).Select(d => new DependencyItemViewModel(d)).ToArray();
            foreach (var row in rows) Dependencies.Add(row);
            using var parallel = new SemaphoreSlim(4);
            try
            {
                await Task.WhenAll(rows.Select(async row => {
                    await parallel.WaitAsync(ct).ConfigureAwait(true);
                    try
                    {
                        ResolvedDependency resolved = await _loadDependency!(row.Reference, ct).ConfigureAwait(true);
                        if (generation != _dependencyGeneration) return;
                        row.Complete(resolved);
                        if (_loadDependencyIcon != null) _ = LoadDependencyIconAsync(row, resolved, generation, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { if (generation == _dependencyGeneration) row.Complete(null); }
                    finally { parallel.Release(); }
                })).ConfigureAwait(true);
                if (generation != _dependencyGeneration) return;
                _checkedDependencyVersion = version;
                if (rows.Any(r => r.HasError)) DependencyError = "部分前置加载失败，请重试。";
            }
            catch (OperationCanceledException) { }
            finally { if (generation == _dependencyGeneration) { IsLoadingDependencies = false; OnPropertyChanged(nameof(CanInstall)); } }
        }

        private async Task LoadDependencyIconAsync(DependencyItemViewModel row, ResolvedDependency resolved, int generation, CancellationToken ct)
        {
            try
            {
                var icon = await _loadDependencyIcon!(resolved.Project.IconUrl, ct).ConfigureAwait(true);
                if (generation == _dependencyGeneration) row.Icon = icon;
            }
            catch { /* An optional icon never changes dependency or install availability. */ }
        }

        private string _newInstanceName = "";
        public string NewInstanceName { get => _newInstanceName; set => Set(ref _newInstanceName, value); }

        private string _modpackSavePath = "";
        public string ModpackSavePath { get => _modpackSavePath; set => Set(ref _modpackSavePath, value); }

        public bool IsModrinthModpack => _kind == ResourceKind.Modpack && _project.Source == ResourceSourceId.Modrinth;
        public bool IsCurseForgeModpack => _kind == ResourceKind.Modpack && _project.Source == ResourceSourceId.CurseForge;
        public bool ShowTargetSelectors => _kind != ResourceKind.Modpack;
        public bool ShowWorldSelector => _kind == ResourceKind.Datapack;
        public bool ShowModpackName => IsModrinthModpack;
        public bool ShowModpackSavePath => IsCurseForgeModpack;

        /// <summary>Conflict confirmation callback (injected by the view; may be null).</summary>
        public Func<string, bool>? ConfirmOverwrite { get; set; }

        /// <summary>Install progress sink; when null the VM installs its own UI-thread reporter.</summary>
        public IProgress<CommunityInstallProgress>? InstallProgress { get; set; }

        public ICommand InstallCommand { get; private set; } = null!;

        /// <summary>
        /// Loads the instance selector (broken targets are excluded) and selects the preferred target by id or the first valid one.
        /// Never throws when no instances exist.
        /// </summary>
        public void LoadInstances(ResourceKind kind, ResourceTarget? preferred)
        {
            _kind = kind;
            Instances.Clear();
            foreach (ResourceTarget t in _targets.EnumerateInstances())
                if (t.Error == null)
                    Instances.Add(t);

            ResourceTarget? selected = preferred != null
                ? Instances.FirstOrDefault(i => i.Id == preferred.Id)
                : null;
            selected ??= Instances.FirstOrDefault();

            if (!ReferenceEquals(_selectedInstance, selected))
            {
                _selectedInstance = selected;
                OnPropertyChanged(nameof(SelectedInstance));
            }
            RefreshWorlds();
            OnKindChanged();
        }

        /// <summary>
        /// Loads all versions and applies the version filter for the current kind/target.
        /// </summary>
        public void LoadVersions(IReadOnlyList<ResourceVersion> allVersions, ResourceKind kind, ResourceTarget? preferredTarget)
        {
            _allVersions = allVersions ?? Array.Empty<ResourceVersion>();
            _kind = kind;
            _target = preferredTarget;

            ResourceTarget? selected = preferredTarget != null
                ? Instances.FirstOrDefault(i => i.Id == preferredTarget.Id)
                : null;
            selected ??= Instances.FirstOrDefault();

            if (!ReferenceEquals(_selectedInstance, selected))
            {
                _selectedInstance = selected;
                OnPropertyChanged(nameof(SelectedInstance));
                RefreshWorlds();
            }

            // Modpacks show every version (no per-version instance compatibility filtering).
            // Set the field directly to avoid the setter's recursive ApplyVersionFilter call.
            _showAllVersions = kind == ResourceKind.Modpack;
            OnPropertyChanged(nameof(ShowAllVersions));

            ApplyVersionFilter();
            OnKindChanged();
        }

        public void RefreshWorlds()
        {
            ResourceTarget? instance = _selectedInstance;
            Worlds.Clear();
            string? gameDir = instance?.GameDir;
            if (string.IsNullOrWhiteSpace(gameDir))
            {
                _selectedWorld = null;
                OnPropertyChanged(nameof(SelectedWorld));
                return;
            }
            foreach (string w in _targets.EnumerateWorlds(gameDir!))
                Worlds.Add(w);
            _selectedWorld = Worlds.Count > 0 ? Worlds[0] : null;
            OnPropertyChanged(nameof(SelectedWorld));
        }

        /// <summary>Reports install progress to the UI (called on the UI thread via Progress).</summary>
        public void ReportProgress(CommunityInstallProgress p)
        {
            if (p == null) return;
            if (!string.IsNullOrEmpty(p.Message))
                ProgressText = p.Message;
            if (p.Stage == "下载" || p.Stage == "下载整合包")
            {
                if (p.TotalBytes is long total && total > 0 && p.BytesDownloaded is long done)
                {
                    double percent = done * 100.0 / total;
                    if (!double.IsNaN(percent) && !double.IsInfinity(percent))
                        ProgressValue = Math.Max(0.0, Math.Min(100.0, percent));
                }
            }
        }

        /// <summary>
        /// Binds the install command. Must be callable after <see cref="LoadVersions"/>; the command re-reads the
        /// current selection at execution time.
        /// </summary>
        public void BindInstall(ResourceKind kind, Func<ResourceFile?> fileSelector, Func<ResourceVersion, ResourceFile, CancellationToken, Task<ResourceFile?>>? resolveDownload = null)
        {
            _resolveDownload = resolveDownload;
            Recompute();
            InstallCommand = new RelayCommand(async _ =>
            {
                if (IsInstalling || !CanInstall) return;
                ResourceVersion? version = SelectedVersion;
                if (version == null) return;
                ResourceFile? file = fileSelector();
                if (file == null) return;

                var req = new CommunityInstallRequest
                {
                    Project = _project,
                    Version = version,
                    File = file,
                    Kind = kind,
                    TargetInstanceId = kind == ResourceKind.Modpack ? null : SelectedInstance?.Id,
                    WorldName = kind == ResourceKind.Datapack ? SelectedWorld : null,
                    ModpackDisplayName = kind == ResourceKind.Modpack && _project.Source == ResourceSourceId.Modrinth ? NewInstanceName : null,
                    ModpackSavePath = kind == ResourceKind.Modpack && _project.Source == ResourceSourceId.CurseForge ? ModpackSavePath : null,
                    ConfirmOverwrite = ConfirmOverwrite,
                };

                IProgress<CommunityInstallProgress> progress = InstallProgress
                    ?? new Progress<CommunityInstallProgress>(ReportProgress);

                IsInstalling = true;
                Status = "";
                ProgressValue = 0;
                ProgressText = "";
                try
                {
                    if (_resolveDownload != null)
                    {
                        ProgressText = "正在获取官方下载授权…";
                        ResourceFile? resolved = await _resolveDownload(version, file, CancellationToken.None).ConfigureAwait(true);
                        if (resolved == null || string.IsNullOrWhiteSpace(resolved.Url))
                        { Status = "作者未允许分发或没有可用的官方下载地址。"; return; }
                        req.File = resolved;
                    }
                    CommunityInstallResult result = await _install.InstallAsync(req, progress, CancellationToken.None).ConfigureAwait(true);
                    if (result.SoftWarnings != null)
                        SoftWarnings = result.SoftWarnings;

                    if (result.Success)
                    {
                        Status = "安装完成";
                    }
                    else if (result.Skipped)
                    {
                        Status = result.Message;
                    }
                    else if (result.Cancelled)
                    {
                        Status = result.Message;
                    }
                    else if (result.HardBlocked)
                    {
                        Status = result.Message;
                        HardBlock = result.Message;
                        CanInstall = false;
                    }
                    else
                    {
                        Status = result.Message;
                    }
                }
                catch (Exception ex)
                {
                    Status = ex.Message;
                }
                finally
                {
                    IsInstalling = false;
                }
            });
        }

        /// <summary>Filters the loaded versions by the selected target and re-populates the version list.</summary>
        private void ApplyVersionFilter()
        {
            IReadOnlyList<ResourceVersion> source;
            bool noCompatible = false;

            if (_showAllVersions || _kind == ResourceKind.Modpack)
            {
                source = _allVersions;
            }
            else
            {
                ResourceTarget? target = _selectedInstance ?? _target;
                List<ResourceVersion> filtered = FilterVersions(_allVersions, _kind, target);
                if (filtered.Count == 0)
                {
                    source = _allVersions; // fall back so the UI is never empty
                    noCompatible = true;
                }
                else
                {
                    source = filtered;
                }
            }

            NoCompatibleVersion = noCompatible;

            List<ResourceVersion> sorted = source
                .OrderByDescending(v => v.PublishedAt ?? DateTimeOffset.MinValue)
                .ToList();
            Versions.Clear();
            foreach (ResourceVersion v in sorted)
                Versions.Add(v);

            ResourceVersion? latest = sorted.FirstOrDefault();
            if (!ReferenceEquals(_selectedVersion, latest))
                SelectedVersion = latest;

            // Default the modpack instance name once a version is known.
            if (IsModrinthModpack && string.IsNullOrWhiteSpace(_newInstanceName))
                NewInstanceName = (_project.Title + " " + (latest?.VersionNumber ?? "")).Trim();
        }

        private static List<ResourceVersion> FilterVersions(IReadOnlyList<ResourceVersion> versions, ResourceKind kind, ResourceTarget? target)
        {
            if (kind == ResourceKind.Modpack) return versions.ToList();
            var result = new List<ResourceVersion>();
            foreach (ResourceVersion v in versions)
                if (VersionMatchesTarget(v, kind, target))
                    result.Add(v);
            return result;
        }

        private static bool VersionMatchesTarget(ResourceVersion v, ResourceKind kind, ResourceTarget? target)
        {
            if (target == null) return true;
            switch (kind)
            {
                case ResourceKind.Mod:
                    return ModVersionMatches(v, target);
                case ResourceKind.ResourcePack:
                case ResourceKind.Shader:
                case ResourceKind.Datapack:
                    return GameVersionMatches(v, target);
                default:
                    return true;
            }
        }

        private static bool ModVersionMatches(ResourceVersion v, ResourceTarget target)
        {
            bool hasLoaders = v.Loaders != null && v.Loaders.Count > 0;
            bool hasGames = v.GameVersions != null && v.GameVersions.Count > 0;

            // Loader filter applies only when the target loader is Known; empty version loader lists are kept (soft).
            bool loaderOk = true;
            if (target.LoaderState == LoaderState.Known && hasLoaders)
                loaderOk = CompatibilityEvaluator.AnyLoaderMatches(target.Loader, v.Loaders);

            // Game-version filter applies only when the target game is known and the version declares versions.
            bool gameOk = true;
            if (!string.IsNullOrWhiteSpace(target.Game) && hasGames)
                gameOk = v.GameVersions.Contains(target.Game, StringComparer.OrdinalIgnoreCase);

            return loaderOk && gameOk;
        }

        private static bool GameVersionMatches(ResourceVersion v, ResourceTarget target)
        {
            bool hasGames = v.GameVersions != null && v.GameVersions.Count > 0;
            if (!hasGames) return true; // empty -> keep (soft)
            if (string.IsNullOrWhiteSpace(target.Game)) return true; // unknown -> keep
            return v.GameVersions.Contains(target.Game, StringComparer.OrdinalIgnoreCase);
        }

        private void Recompute()
        {
            ResourceVersion? version = SelectedVersion;
            if (version == null)
            {
                CanInstall = false;
                HardBlock = "请选择版本。";
                SoftWarnings = Array.Empty<string>();
                return;
            }

            IReadOnlyList<ResourceFile> files = version.Files ?? Array.Empty<ResourceFile>();
            // Prefer the primary-most file whose URL is already resolved (CurseForge URLs are resolved before this).
            ResourceFile? primary = files.OrderByDescending(f => f.Primary).FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.Url))
                ?? files.OrderByDescending(f => f.Primary).FirstOrDefault();

            if (primary == null)
            {
                CanInstall = false;
                HardBlock = "该版本无可下载文件。";
                SoftWarnings = Array.Empty<string>();
                return;
            }

            if (_kind == ResourceKind.Modpack)
            {
                // Source-specific extension + hash + resolved URL; no target-instance rules.
                string expectedExt = _project.Source == ResourceSourceId.Modrinth ? ".mrpack" : ".zip";
                string ext = Path.GetExtension(primary.FileName ?? "").ToLowerInvariant();
                var blocks = new List<string>();
                if (ext != expectedExt) blocks.Add("文件类型与所选分类不符。");
                if (!primary.HasAnyHash) blocks.Add("来源未提供 SHA-1/SHA-512 哈希，无法校验，禁止安装。");
                if (string.IsNullOrWhiteSpace(primary.Url) && _resolveDownload == null) blocks.Add("来源未提供可用的文件下载地址，请稍后重试。");
                HardBlock = blocks.FirstOrDefault() ?? "";
                SoftWarnings = Array.Empty<string>();
                CanInstall = blocks.Count == 0;
                return;
            }

            ResourceTarget? instance = SelectedInstance;
            if (instance == null)
            {
                CanInstall = false;
                HardBlock = "请选择目标实例。";
                SoftWarnings = Array.Empty<string>();
                return;
            }

            bool datapackWorldSelected = _kind == ResourceKind.Datapack ? SelectedWorld != null : true;
            CompatibilityEvaluator.InstallDecision decision = CompatibilityEvaluator.EvaluateInstall(
                _kind,
                instance.LoaderState,
                instance.Loader,
                instance.Game,
                version.Loaders,
                version.GameVersions,
                primary,
                datapackWorldSelected);
            HardBlock = decision.HardBlocks.FirstOrDefault() ?? "";
            SoftWarnings = decision.SoftWarnings;
            CanInstall = decision.HardBlocks.Count == 0;
        }

        private void OnKindChanged()
        {
            OnPropertyChanged(nameof(ShowTargetSelectors));
            OnPropertyChanged(nameof(ShowWorldSelector));
            OnPropertyChanged(nameof(IsModrinthModpack));
            OnPropertyChanged(nameof(IsCurseForgeModpack));
            OnPropertyChanged(nameof(ShowModpackName));
            OnPropertyChanged(nameof(ShowModpackSavePath));
        }
    }
}
