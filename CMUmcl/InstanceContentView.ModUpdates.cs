// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using CMUmcl.Community;
using CMUmcl.Content;

namespace CMUmcl;

internal sealed partial class InstanceContentView
{
    internal Func<ModUpdateService>? ModUpdatesFactory { get; set; }
    // Test and preview hook for views created indirectly (for example inside instance management).
    internal static Func<ModUpdateService>? DefaultModUpdatesFactory { get; set; }
    private readonly Dictionary<string, ModUpdate> _modUpdates = new(StringComparer.OrdinalIgnoreCase);
    private bool _modUpdateBusy;

    private (string Game, string Loader)? ModUpdateTarget()
    {
        try
        {
            var metadata = InstanceMaintenance.Read(_instanceDirectory);
            if (string.IsNullOrWhiteSpace(metadata.Game) || ModUpdateService.LoadersFor(metadata.Loader) == null) return null;
            return (metadata.Game!, metadata.Loader!);
        }
        catch { return null; }
    }

    private async Task CheckModUpdatesAsync(IReadOnlyList<InstanceContentFile> items, Action render)
    {
        if (_modUpdateBusy || Closed) return;
        if (ModUpdateTarget() is not { } target) { _status.Text = "无法确定实例的加载器和游戏版本，不能检查更新。"; return; }
        int generation = _generation; _modUpdateBusy = true; _status.Text = "正在检查模组更新…";
        try
        {
            using var service = (ModUpdatesFactory ?? DefaultModUpdatesFactory)?.Invoke() ?? new ModUpdateService();
            string[] jars = items.Where(i => !i.Disabled && i.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && File.Exists(i.Path)).Select(i => i.Path).ToArray();
            var found = await service.CheckAsync(jars, target.Game, target.Loader, CancellationToken.None);
            if (Closed || generation != _generation) return;
            _modUpdates.Clear();
            foreach (var update in found) _modUpdates[update.Path] = update;
            render();
            _status.Text = found.Count == 0
                ? $"已检查 {jars.Length} 个模组，没有可用更新（仅检查 Modrinth 收录的模组）。"
                : $"发现 {found.Count} 个可更新的模组。";
        }
        catch (Exception ex) { if (!Closed && generation == _generation) _status.Text = "检查更新失败：" + ex.Message; }
        finally { _modUpdateBusy = false; }
    }

    private async Task ApplyModUpdatesAsync(IReadOnlyList<ModUpdate> updates, Action<string?> refresh)
    {
        if (updates.Count == 0 || _modUpdateBusy || !CanChange()) return;
        if (!await _confirm("更新模组", $"将更新 {updates.Count} 个模组，旧文件会移到游戏目录的 .cmumcl-trash 中。继续？") || !CanChange()) return;
        int generation = _generation, done = 0; var errors = new List<string>();
        _modUpdateBusy = true;
        try
        {
            using var service = (ModUpdatesFactory ?? DefaultModUpdatesFactory)?.Invoke() ?? new ModUpdateService();
            for (int i = 0; i < updates.Count; i++)
            {
                var update = updates[i]; string? downloaded = null;
                _status.Text = $"正在更新 {i + 1}/{updates.Count}：{Path.GetFileName(update.Path)}";
                try
                {
                    downloaded = await service.DownloadAsync(update, CancellationToken.None);
                    if (Closed || generation != _generation) return;
                    if (!CanChange()) throw new IOException("实例正在使用。");
                    // Install the verified file first so a failure never leaves the mod missing,
                    // except when the new file has the same name and must replace the old one.
                    if (string.Equals(Path.GetFileName(update.Path), update.FileName, StringComparison.OrdinalIgnoreCase))
                    { _files.Remove(update.Path); _files.Install("mods", downloaded, true); }
                    else { _files.Install("mods", downloaded, true); _files.Remove(update.Path); }
                    _modUpdates.Remove(update.Path); done++;
                }
                catch (Exception ex) { errors.Add(Path.GetFileName(update.Path) + "：" + ex.Message); }
                finally { if (downloaded != null) ModUpdateService.DeleteDownload(downloaded); }
            }
        }
        finally { _modUpdateBusy = false; }
        if (Closed || generation != _generation) return;
        refresh(errors.Count == 0 ? $"已更新 {done} 个模组。" : $"已更新 {done} 个，未完成 {errors.Count} 个。" + string.Join("；", errors.Take(3)));
    }
}
