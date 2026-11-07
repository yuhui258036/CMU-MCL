// SPDX-License-Identifier: GPL-3.0-only
namespace CMUmcl.Content;

/// <summary>A game version as listed in dropdowns: id plus a short type tag and release date.</summary>
internal sealed record VersionChoice(string Name, string Tag, string Date)
{
    public string Detail => Date.Length == 0 ? Tag : Tag + " · " + Date;
    public override string ToString() => Name;
}

/// <summary>
/// Session-wide cache of the version lists shown in dropdowns, so opening a list is instant after
/// the first load and several editors share one request. Failed or empty loads are retried.
/// </summary>
internal static class VersionChoices
{
    private static readonly object Gate = new();
    private static VersionCatalogService? _catalog;
    private static Task<VersionChoice[]>? _games;
    private static readonly Dictionary<string, Task<string[]>> Loaders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Test hooks for the game list and for loader builds (loader, game version).</summary>
    internal static Func<Task<VersionChoice[]>>? GameOverride { get; set; }
    internal static Func<string, string, Task<string[]>>? Override { get; set; }

    private static VersionCatalogService Catalog { get { lock (Gate) return _catalog ??= new VersionCatalogService(); } }

    /// <summary>Every version (release, snapshot, April Fools, old alpha/beta), newest first.</summary>
    internal static Task<VersionChoice[]> GameVersionsAsync()
    {
        if (GameOverride != null) return GameOverride();
        lock (Gate)
        {
            if (_games == null || _games.IsFaulted || _games.IsCanceled || (_games.Status == TaskStatus.RanToCompletion && _games.Result.Length == 0))
                _games = LoadGamesAsync();
            return _games;
        }
    }

    /// <summary>Loader builds compatible with a game version, recommended first.</summary>
    internal static Task<string[]> LoaderVersionsAsync(string loader, string gameVersion)
    {
        if (Override != null) return Override(loader, gameVersion);
        string key = loader + "|" + gameVersion;
        lock (Gate)
        {
            if (!Loaders.TryGetValue(key, out var task) || Unusable(task)) Loaders[key] = task = LoadLoadersAsync(loader, gameVersion);
            return task;
        }
    }

    internal static void Reset() { lock (Gate) { _games = null; Loaders.Clear(); } }

    private static bool Unusable(Task<string[]> task) => task.IsFaulted || task.IsCanceled || (task.Status == TaskStatus.RanToCompletion && task.Result.Length == 0);

    private static async Task<VersionChoice[]> LoadGamesAsync()
    {
        var all = await Catalog.GetMinecraftVersionsAsync().ConfigureAwait(false);
        return all.Select(v => new VersionChoice(v.Name, v.CategoryLabel, v.ReleaseTime == default ? "" : v.ReleaseTime.ToLocalTime().ToString("yyyy-MM-dd"))).ToArray();
    }

    private static async Task<string[]> LoadLoadersAsync(string loader, string gameVersion)
    {
        var all = await Catalog.GetLoaderVersionsAsync(loader, gameVersion).ConfigureAwait(false);
        return all.Select(v => v.Version).Distinct(StringComparer.Ordinal).ToArray();
    }
}
