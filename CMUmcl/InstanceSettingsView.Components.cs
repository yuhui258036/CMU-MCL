// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CMUmcl.Content;
using CMUmcl.Launch;

namespace CMUmcl;

// "组件管理": what the instance runs now, what can be upgraded, and repair.
internal sealed partial class InstanceSettingsView
{
    private InstanceMetadata? _componentMetadata;
    private readonly TextBlock _upgradeHint = new(), _fabricApiState = new();
    private Button _upgradeLoader = null!, _fabricApiButton = null!, _cancelMaintenance = null!;
    private FrameworkElement? _fabricApiCard;
    private string? _latestLoader;

    private bool IsLocalInstance => _componentMetadata?.Id?.StartsWith("local:", StringComparison.Ordinal) == true;

    private void BuildComponentPage(InstanceMetadata? metadata)
    {
        _componentMetadata = metadata;
        var current = InstancePageUi.Section(_pages[4], "当前版本");
        var summary = InstancePageUi.Text(DescribeComponents(metadata), 15); summary.Margin = new Thickness(0, 0, 0, 10);
        current.Children.Add(summary);
        if (!IsLocalInstance)
            AddText(current, "这是服务器整合包，游戏和加载器版本由服务器维护，整合包更新时会自动调整。如需自行修改，可在“新建实例 → 导入已有”中选择此实例的目录，导入为本地实例后修改。");

        var versions = InstancePageUi.Section(_pages[4], "修改版本");
        AddText(versions, "修改前请备份存档；现有模组不会自动适配新版本。");
        AddField(versions, "Minecraft 版本", _gameVersion);
        AddField(versions, "平台", _loaderKind);
        AddField(versions, "加载器版本", _loaderVersion);
        SetUpVersionPickers(metadata);
        _upgradeHint.FontSize = 12; _upgradeHint.TextWrapping = TextWrapping.Wrap; _upgradeHint.Margin = new Thickness(0, 0, 0, 10);
        _upgradeHint.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); _upgradeHint.Visibility = Visibility.Collapsed;
        versions.Children.Add(_upgradeHint);
        var versionActions = new WrapPanel(); versions.Children.Add(versionActions);
        _upgradeLoader = Button("升级加载器", () => { if (_latestLoader != null) { _loaderVersion.Text = _latestLoader; StartInstallation(false); } });
        _upgradeLoader.Visibility = Visibility.Collapsed; versionActions.Children.Add(_upgradeLoader);
        versionActions.Children.Add(Button("应用版本修改", () => StartInstallation(false)));
        // Server packs follow the server; editing them here would only fail.
        if (!IsLocalInstance && versions.Parent is FrameworkElement versionCard) versionCard.Visibility = Visibility.Collapsed;

        var fabric = InstancePageUi.Section(_pages[4], "Fabric API");
        _fabricApiCard = fabric.Parent as FrameworkElement;
        AddText(fabric, "多数 Fabric / Quilt 模组需要 Fabric API。安装或更新时会替换旧版本，旧文件移到 .cmumcl-trash。");
        _fabricApiState.Margin = new Thickness(0, 0, 0, 10); _fabricApiState.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        fabric.Children.Add(_fabricApiState);
        _fabricApiButton = Button("安装 Fabric API", () => _ = InstallFabricApiAsync());
        fabric.Children.Add(_fabricApiButton);
        UpdateFabricApiVisibility();

        var repair = InstancePageUi.Section(_pages[4], "游戏文件");
        AddText(repair, "启动前会自动检查并补齐缺失的文件。如果游戏仍提示文件损坏，可在这里按哈希完整校验并修复，不会改动存档和模组。");
        var repairActions = new WrapPanel(); repair.Children.Add(repairActions);
        repairActions.Children.Add(Button("检查并修复游戏文件", () => StartInstallation(true)));
        _cancelMaintenance = Button("取消", () => _maintenanceCts?.Cancel());
        _cancelMaintenance.Visibility = Visibility.Collapsed; repairActions.Children.Add(_cancelMaintenance);
        _loaderKind.SelectionChanged += (_, _) => UpdateFabricApiVisibility();
    }

    private static string DescribeComponents(InstanceMetadata? metadata)
    {
        if (metadata == null) return "无法读取实例信息";
        string loader = (metadata.Loader ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "vanilla" => "原版",
            "fabric" => "Fabric", "forge" => "Forge", "neoforge" => "NeoForge", "quilt" => "Quilt",
            var other => other,
        };
        string version = string.IsNullOrWhiteSpace(metadata.LoaderVersion) || loader == "原版" ? "" : " " + metadata.LoaderVersion;
        return $"Minecraft {metadata.Game} · {loader}{version}";
    }

    private bool UsesFabricMods => (_componentMetadata?.Loader ?? "").Trim().ToLowerInvariant() is "fabric" or "quilt";

    private void UpdateFabricApiVisibility()
    {
        if (_fabricApiCard != null) _fabricApiCard.Visibility = IsLocalInstance && UsesFabricMods ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Runs when the components tab opens: loader upgrade hint and Fabric API state.</summary>
    private async Task RefreshComponentStateAsync()
    {
        var metadata = _componentMetadata;
        if (metadata == null || IsClosed || !IsLocalInstance) return;
        string loader = (metadata.Loader ?? "").Trim().ToLowerInvariant();
        if (loader is not ("" or "vanilla") && !string.IsNullOrWhiteSpace(metadata.Game))
        {
            string[] builds;
            try { builds = await VersionChoices.LoaderVersionsAsync(loader, metadata.Game!); } catch { builds = Array.Empty<string>(); }
            if (IsClosed) return;
            _latestLoader = builds.FirstOrDefault();
            bool newer = _latestLoader != null && !string.Equals(_latestLoader, metadata.LoaderVersion, StringComparison.OrdinalIgnoreCase)
                && Array.IndexOf(builds, metadata.LoaderVersion) > 0;
            _upgradeHint.Text = newer ? $"有新的加载器版本 {_latestLoader}（当前 {metadata.LoaderVersion}）。" : "";
            _upgradeHint.Visibility = _upgradeLoader.Visibility = newer ? Visibility.Visible : Visibility.Collapsed;
            if (newer) _upgradeLoader.Content = "升级到 " + _latestLoader;
        }
        if (UsesFabricMods) await RefreshFabricApiStateAsync();
    }

    private string GameDirectory() => GameDirectoryResolver.ResolveGameDirectory(_store.Load(), _directory, _componentMetadata?.Loader);

    private static IReadOnlyList<(string Path, string Version)> FindFabricApi(string gameDirectory)
    {
        string mods = Path.Combine(gameDirectory, "mods");
        if (!Directory.Exists(mods)) return Array.Empty<(string, string)>();
        return Directory.EnumerateFiles(mods, "*.jar")
            .Select(path => (path, info: InstanceContentFiles.ReadFabricModId(path)))
            .Where(x => x.info?.Id == "fabric-api")
            .Select(x => (x.path, x.info!.Value.Version)).ToArray();
    }

    private async Task RefreshFabricApiStateAsync()
    {
        IReadOnlyList<(string Path, string Version)> found;
        try { string game = GameDirectory(); found = await Task.Run(() => FindFabricApi(game)); }
        catch { found = Array.Empty<(string, string)>(); }
        if (IsClosed) return;
        _fabricApiState.Text = found.Count == 0 ? "未安装" : "已安装 " + string.Join("、", found.Select(x => x.Version)) + (found.Count > 1 ? "（存在多个版本，更新时会只保留最新版）" : "");
        _fabricApiButton.Content = found.Count == 0 ? "安装 Fabric API" : "更新到最新版本";
    }

    private async Task InstallFabricApiAsync()
    {
        string? gameVersion = _componentMetadata?.Game;
        if (string.IsNullOrWhiteSpace(gameVersion)) { _status.Text = "无法确定 Minecraft 版本。"; return; }
        await RunMaintenanceWithMessage(async ct =>
        {
            string game = GameDirectory();
            InstanceMaintenance.EnsureStopped(game);
            string staging = Path.Combine(Path.GetTempPath(), "cmumcl-fabric-api-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var installer = new FabricApiInstaller();
                var progress = new Progress<FabricApiProgress>(p => { if (!IsClosed) _status.Text = p.Stage + " · " + p.Message; });
                var result = await installer.InstallAsync(gameVersion!, staging, progress, ct);
                if (!result.Success) throw new IOException(result.ErrorMessage);
                var files = new InstanceContentFiles(game);
                var existing = FindFabricApi(game);
                if (existing.Any(x => string.Equals(Path.GetFileName(x.Path), result.FileName, StringComparison.OrdinalIgnoreCase)) && existing.Count == 1)
                    return "Fabric API 已是最新版本。";
                foreach (var old in existing) files.Remove(old.Path);
                files.Install("mods", result.FilePath, true);
                return "Fabric API 已更新为 " + result.FileName + "。";
            }
            finally { try { Directory.Delete(staging, true); } catch { /* best effort */ } }
        }, "Fabric API 已更新。");
        await RefreshFabricApiStateAsync();
    }
}
