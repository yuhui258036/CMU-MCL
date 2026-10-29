using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using CmlLib.Core;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.ModLoaders.QuiltMC;
using CmlLib.Core.VersionMetadata;

namespace CMUmcl.Content
{
    /// <summary>
    /// PCL-like grouping used by the in-main create-instance flow (全部 / 正式版 / 快照 /
    /// 愚人节 / 远古版本 / 其他).
    /// </summary>
    public enum MinecraftVersionCategory
    {
        Release,
        Snapshot,
        AprilFools,
        Old,
        Other,
    }

    /// <summary>
    /// A Minecraft version with its raw Mojang metadata plus a UI-facing category. Release
    /// time is kept as a <see cref="DateTimeOffset"/> so the UI can format it (locally or
    /// otherwise) as it sees fit.
    /// </summary>
    public sealed class MinecraftVersionInfo
    {
        public MinecraftVersionInfo(string name, string type, DateTimeOffset releaseTime, MinecraftVersionCategory category)
        {
            Name = name;
            Type = type;
            ReleaseTime = releaseTime;
            Category = category;
        }

        public string Name { get; }

        /// <summary>Raw Mojang version type (e.g. <c>release</c>/<c>snapshot</c>/<c>old_alpha</c>).</summary>
        public string Type { get; }

        public DateTimeOffset ReleaseTime { get; }

        public MinecraftVersionCategory Category { get; }

        public string CategoryLabel => Category switch
        {
            MinecraftVersionCategory.Release => "正式版",
            MinecraftVersionCategory.Snapshot => "快照",
            MinecraftVersionCategory.AprilFools => "愚人节",
            MinecraftVersionCategory.Old => "远古版本",
            _ => "其他",
        };

        public bool IsRelease => Category == MinecraftVersionCategory.Release;
        public bool IsSnapshot => Category == MinecraftVersionCategory.Snapshot;
        public bool IsAprilFools => Category == MinecraftVersionCategory.AprilFools;
        public bool IsOld => Category == MinecraftVersionCategory.Old;
    }

    /// <summary>
    /// One loader (Fabric/Quilt/Forge/NeoForge) version with its Minecraft compatibility and
    /// a ready-to-show detail line ("稳定版", "构建 123" or "Minecraft 1.21.1").
    /// </summary>
    public sealed class LoaderVersionInfo
    {
        public LoaderVersionInfo(string version, string gameVersion, bool isStable, string detail)
        {
            Version = version;
            GameVersion = gameVersion;
            IsStable = isStable;
            Detail = detail;
        }

        /// <summary>Loader version string (Fabric/Quilt/NeoForge full version, Forge suffix).</summary>
        public string Version { get; }

        /// <summary>The Minecraft version this loader build targets.</summary>
        public string GameVersion { get; }

        /// <summary>True for stable loader builds (Fabric/Quilt); Forge/NeoForge entries are always stable.</summary>
        public bool IsStable { get; }

        public string Detail { get; }
    }

    /// <summary>
    /// Loads Minecraft / Fabric / Forge / NeoForge / Quilt version lists for the
    /// create-instance dialog. Every public method is defensive: network failures,
    /// timeouts and malformed responses return empty collections so the dialog stays
    /// usable with manual input even when the app is offline or behind a restrictive proxy.
    /// </summary>
    public sealed class VersionCatalogService : IDisposable
    {
        private const string UserAgent = "CMUmcl/0.1.0";

        /// <summary>Per-request timeout for the (small) version-list endpoints.</summary>
        private static readonly TimeSpan VersionListTimeout = TimeSpan.FromSeconds(15);

        private const string ForgeMetadataUrl = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";
        private const string NeoForgeMetadataUrl = "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";

        /// <summary>Hosts permitted for the Forge/NeoForge version-list endpoints.</summary>
        private static readonly HashSet<string> AllowedVersionListHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "maven.minecraftforge.net",
            "maven.neoforged.net",
        };

        private readonly HttpClient _http;
        private readonly MinecraftLauncher _launcher;
        private readonly FabricInstaller _fabric;
        private readonly QuiltInstaller _quilt;

        /// <summary>Cached full Forge version list (all MC versions), populated on first successful fetch.</summary>
        private IReadOnlyList<string>? _forgeVersionsCache;

        /// <summary>Cached full NeoForge version list (all MC versions), populated on first successful fetch.</summary>
        private IReadOnlyList<string>? _neoforgeVersionsCache;

        public VersionCatalogService()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            };

            _http = new HttpClient(handler) { Timeout = VersionListTimeout };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

            // The Mojang version list is fetched by the launcher's own client; our timeout
            // wrapper below bounds the overall call regardless of its internal settings.
            _launcher = new MinecraftLauncher();
            _fabric = new FabricInstaller(_http);
            _quilt = new QuiltInstaller(_http);
        }

        /// <summary>
        /// Every Minecraft version (release, snapshot, April Fools and ancient builds) mapped to
        /// a <see cref="MinecraftVersionInfo"/>, newest release time first. Empty on any
        /// network/timeout/parse failure; cancellation is propagated.
        /// </summary>
        public async Task<IReadOnlyList<MinecraftVersionInfo>> GetMinecraftVersionsAsync(CancellationToken ct = default)
        {
            try
            {
                var versions = await WithTimeoutAsync(
                    () => _launcher.GetAllVersionsAsync(ct).AsTask(), ct).ConfigureAwait(false);
                if (versions == null)
                    return Array.Empty<MinecraftVersionInfo>();

                return versions
                    .Select(ToMinecraftVersionInfo)
                    .OrderByDescending(v => v.ReleaseTime)
                    .ToList();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Array.Empty<MinecraftVersionInfo>();
            }
        }

        /// <summary>
        /// Minecraft release version names, newest first. Returns an empty list on any
        /// network/timeout/parse failure (the dialog then relies on manual input).
        /// </summary>
        public async Task<IReadOnlyList<string>> GetMinecraftReleaseVersionsAsync(CancellationToken ct = default)
        {
            IReadOnlyList<MinecraftVersionInfo> all = await GetMinecraftVersionsAsync(ct).ConfigureAwait(false);
            return all
                .Where(v => v.IsRelease)
                .Select(v => v.Name)
                .ToList();
        }

        /// <summary>
        /// Detailed loader versions for a loader (<c>fabric</c>/<c>quilt</c>/<c>forge</c>/
        /// <c>neoforge</c>) and a game version. Returns an empty list for vanilla/unknown
        /// loaders or on any failure; cancellation is propagated.
        /// </summary>
        public Task<IReadOnlyList<LoaderVersionInfo>> GetLoaderVersionsAsync(string loader, string gameVersion, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(loader) || string.IsNullOrWhiteSpace(gameVersion))
                return Task.FromResult<IReadOnlyList<LoaderVersionInfo>>(Array.Empty<LoaderVersionInfo>());

            string loaderKey = loader.Trim().ToLowerInvariant();
            string mc = gameVersion.Trim();

            return loaderKey switch
            {
                "fabric" => GetFabricLoaderVersionsDetailedAsync(mc, ct),
                "quilt" => GetQuiltLoaderVersionsDetailedAsync(mc, ct),
                "forge" => GetForgeLoaderVersionsDetailedAsync(mc, ct),
                "neoforge" => GetNeoForgeLoaderVersionsDetailedAsync(mc, ct),
                _ => Task.FromResult<IReadOnlyList<LoaderVersionInfo>>(Array.Empty<LoaderVersionInfo>()),
            };
        }

        /// <summary>Fabric loader versions for a game version, stable-first. Empty on any failure.</summary>
        public async Task<IReadOnlyList<string>> GetFabricLoaderVersionsAsync(string gameVersion, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(gameVersion))
                return Array.Empty<string>();

            try
            {
                IReadOnlyList<LoaderVersionInfo> detailed = await GetLoaderVersionsAsync("fabric", gameVersion, ct).ConfigureAwait(false);
                return detailed.Select(l => l.Version).ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>Quilt loader versions for a game version, stable-first. Empty on any failure.</summary>
        public async Task<IReadOnlyList<string>> GetQuiltLoaderVersionsAsync(string gameVersion, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(gameVersion))
                return Array.Empty<string>();

            try
            {
                IReadOnlyList<LoaderVersionInfo> detailed = await GetLoaderVersionsAsync("quilt", gameVersion, ct).ConfigureAwait(false);
                return detailed.Select(l => l.Version).ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Forge loader versions for a game version, newest first. Version format in the
        /// Forge maven metadata is <c>{minecraft}-{loader}</c> (e.g. <c>1.20.1-47.4.28</c> →
        /// <c>47.4.28</c>); the loader suffix is returned. Empty on any failure.
        /// </summary>
        public async Task<IReadOnlyList<string>> GetForgeLoaderVersionsAsync(string gameVersion, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(gameVersion))
                return Array.Empty<string>();

            try
            {
                IReadOnlyList<LoaderVersionInfo> detailed = await GetLoaderVersionsAsync("forge", gameVersion, ct).ConfigureAwait(false);
                return detailed.Select(l => l.Version).ToList();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// NeoForge loader versions for a game version, newest first. The NeoForge version's
        /// first segments encode the Minecraft version (<c>21.1.265</c> → <c>1.21.1</c>,
        /// <c>20.2.93</c> → <c>1.20.2</c>, <c>26.3.0.99</c> → <c>26.3</c>); snapshot <c>0.*</c>
        /// builds are excluded. The full NeoForge version string is returned. Empty on any failure.
        /// </summary>
        public async Task<IReadOnlyList<string>> GetNeoForgeLoaderVersionsAsync(string gameVersion, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(gameVersion))
                return Array.Empty<string>();

            try
            {
                IReadOnlyList<LoaderVersionInfo> detailed = await GetLoaderVersionsAsync("neoforge", gameVersion, ct).ConfigureAwait(false);
                return detailed.Select(l => l.Version).ToList();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        // ------------------------------------------------------------------
        // Internals
        // ------------------------------------------------------------------

        /// <summary>
        /// Races <paramref name="factory"/> against a timeout. Returns the default value when
        /// the timeout (or <paramref name="ct"/>) wins, observing any later fault on the
        /// abandoned task so it can never surface as an unobserved exception.
        /// </summary>
        private static async Task<T?> WithTimeoutAsync<T>(Func<Task<T>> factory, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(VersionListTimeout);

            Task<T> task = factory();
            Task completed = await Task.WhenAny(task, Task.Delay(Timeout.InfiniteTimeSpan, cts.Token)).ConfigureAwait(false);

            if (completed != task)
            {
                _ = task.ContinueWith(
                    t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                return default;
            }

            return await task.ConfigureAwait(false);
        }

        // ------------------------------------------------------------------
        // Detailed loader version lists
        // ------------------------------------------------------------------

        private async Task<IReadOnlyList<LoaderVersionInfo>> GetFabricLoaderVersionsDetailedAsync(string gameVersion, CancellationToken ct)
        {
            try
            {
                var loaders = await WithTimeoutAsync(
                    () => _fabric.GetLoaders(gameVersion), ct).ConfigureAwait(false);
                if (loaders == null)
                    return Array.Empty<LoaderVersionInfo>();

                return loaders
                    .Where(l => !string.IsNullOrWhiteSpace(l.Version))
                    .OrderByDescending(l => l.Stable)
                    .ThenByDescending(l => l.Build)
                    .ThenByDescending(l => l.Version, StringComparer.OrdinalIgnoreCase)
                    .Select(l => new LoaderVersionInfo(l.Version!, gameVersion, l.Stable, FabricLikeDetail(l.Stable, l.Build)))
                    .ToList();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Array.Empty<LoaderVersionInfo>();
            }
        }

        private async Task<IReadOnlyList<LoaderVersionInfo>> GetQuiltLoaderVersionsDetailedAsync(string gameVersion, CancellationToken ct)
        {
            try
            {
                var loaders = await WithTimeoutAsync(
                    () => _quilt.GetLoaders(gameVersion), ct).ConfigureAwait(false);
                if (loaders == null)
                    return Array.Empty<LoaderVersionInfo>();

                return loaders
                    .Where(l => !string.IsNullOrWhiteSpace(l.Version))
                    .OrderByDescending(l => l.Stable)
                    .ThenByDescending(l => l.Build ?? 0)
                    .ThenByDescending(l => l.Version, StringComparer.OrdinalIgnoreCase)
                    .Select(l => new LoaderVersionInfo(l.Version!, gameVersion, l.Stable, FabricLikeDetail(l.Stable, l.Build)))
                    .ToList();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Array.Empty<LoaderVersionInfo>();
            }
        }

        private async Task<IReadOnlyList<LoaderVersionInfo>> GetForgeLoaderVersionsDetailedAsync(string gameVersion, CancellationToken ct)
        {
            try
            {
                IReadOnlyList<string> all = await GetOrFetchForgeVersionsAsync(ct).ConfigureAwait(false);

                var result = new List<LoaderVersionInfo>();
                foreach (string version in all)
                {
                    int dash = version.IndexOf('-');
                    if (dash <= 0)
                        continue;

                    if (!string.Equals(version.Substring(0, dash), gameVersion, StringComparison.OrdinalIgnoreCase))
                        continue;

                    result.Add(new LoaderVersionInfo(version.Substring(dash + 1), gameVersion, true, $"Minecraft {gameVersion}"));
                }

                result.Sort((l, r) => CompareVersionStringsDescending(l.Version, r.Version));
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Array.Empty<LoaderVersionInfo>();
            }
        }

        private async Task<IReadOnlyList<LoaderVersionInfo>> GetNeoForgeLoaderVersionsDetailedAsync(string gameVersion, CancellationToken ct)
        {
            try
            {
                IReadOnlyList<string> all = await GetOrFetchNeoForgeVersionsAsync(ct).ConfigureAwait(false);

                var result = new List<LoaderVersionInfo>();
                foreach (string version in all)
                {
                    if (!TryMapNeoForgeMcVersion(version, out string? mapped))
                        continue;

                    if (!string.Equals(mapped, gameVersion, StringComparison.OrdinalIgnoreCase))
                        continue;

                    result.Add(new LoaderVersionInfo(version, gameVersion, true, $"Minecraft {gameVersion}"));
                }

                result.Sort((l, r) => CompareVersionStringsDescending(l.Version, r.Version));
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Array.Empty<LoaderVersionInfo>();
            }
        }

        /// <summary>Detail line for Fabric/Quilt: stable build vs "构建 {n}"/"开发版".</summary>
        private static string FabricLikeDetail(bool stable, int? build)
            => stable ? "稳定版" : (build is int b ? $"构建 {b}" : "开发版");

        // ------------------------------------------------------------------
        // Minecraft version classification
        // ------------------------------------------------------------------

        private static MinecraftVersionInfo ToMinecraftVersionInfo(IVersionMetadata v)
        {
            string name = v.Name ?? string.Empty;
            string type = v.Type ?? string.Empty;
            return new MinecraftVersionInfo(name, type, v.ReleaseTime, ClassifyMinecraftVersion(type, name, v.ReleaseTime));
        }

        /// <summary>
        /// Maps a Mojang version (raw type + id + release time) to a PCL-like category. The
        /// order matters: <c>release</c> wins over everything (so a real release shipped on
        /// April 1st stays a release), then ancient builds, then April Fools (known ids or an
        /// April-1st release time in a UTC+2 window), then snapshots.
        /// </summary>
        private static MinecraftVersionCategory ClassifyMinecraftVersion(string type, string name, DateTimeOffset releaseTime)
        {
            if (string.Equals(type, "release", StringComparison.OrdinalIgnoreCase))
                return MinecraftVersionCategory.Release;

            if (string.Equals(type, "old_alpha", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "old_beta", StringComparison.OrdinalIgnoreCase))
                return MinecraftVersionCategory.Old;

            if (string.IsNullOrWhiteSpace(type) && IsOldVersionId(name))
                return MinecraftVersionCategory.Old;

            if (IsAprilFoolsVersion(name, releaseTime))
                return MinecraftVersionCategory.AprilFools;

            if (string.Equals(type, "special", StringComparison.OrdinalIgnoreCase))
                return MinecraftVersionCategory.AprilFools;

            if (string.Equals(type, "snapshot", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "pending", StringComparison.OrdinalIgnoreCase))
                return MinecraftVersionCategory.Snapshot;

            return MinecraftVersionCategory.Other;
        }

        /// <summary>Known April Fools ids, plus the April-1st release-time fallback (UTC+2 window).</summary>
        private static bool IsAprilFoolsVersion(string name, DateTimeOffset releaseTime)
        {
            string id = name.ToLowerInvariant();

            if (id.StartsWith("2.0", StringComparison.Ordinal) ||
                id.StartsWith("2point0", StringComparison.Ordinal) ||
                id.StartsWith("20w14inf", StringComparison.Ordinal) ||
                id == "20w14∞")
            {
                return true;
            }

            switch (id)
            {
                case "15w14a":
                case "1.rv-pre1":
                case "3d shareware v1.34":
                case "22w13oneblockatatime":
                case "23w13a_or_b":
                case "24w14potato":
                case "25w14craftmine":
                case "26w14a":
                    return true;
            }

            // Release time on April 1st, considering a UTC+2 local window (mirrors PCL).
            DateTimeOffset d = releaseTime.ToUniversalTime().AddHours(2);
            return d.Month == 4 && d.Day == 1;
        }

        /// <summary>Defensive check for ancient ids when Mojang omits the type (should not happen today).</summary>
        private static bool IsOldVersionId(string name)
        {
            string id = name.ToLowerInvariant();
            return id.StartsWith("c0.", StringComparison.Ordinal)
                || id.StartsWith("a1.", StringComparison.Ordinal)
                || id.StartsWith("b1.", StringComparison.Ordinal)
                || id.StartsWith("rd-", StringComparison.Ordinal)
                || id.StartsWith("inf-", StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // Forge / NeoForge maven metadata fetching, mapping and sorting
        // ------------------------------------------------------------------

        /// <summary>Returns the full cached Forge version list, fetching it once on first use.</summary>
        private async Task<IReadOnlyList<string>> GetOrFetchForgeVersionsAsync(CancellationToken ct)
        {
            if (_forgeVersionsCache != null)
                return _forgeVersionsCache;

            IReadOnlyList<string> versions = await FetchMavenVersionsAsync(ForgeMetadataUrl, ct).ConfigureAwait(false);
            _forgeVersionsCache = versions;
            return versions;
        }

        /// <summary>Returns the full cached NeoForge version list, fetching it once on first use.</summary>
        private async Task<IReadOnlyList<string>> GetOrFetchNeoForgeVersionsAsync(CancellationToken ct)
        {
            if (_neoforgeVersionsCache != null)
                return _neoforgeVersionsCache;

            IReadOnlyList<string> versions = await FetchMavenVersionsAsync(NeoForgeMetadataUrl, ct).ConfigureAwait(false);
            _neoforgeVersionsCache = versions;
            return versions;
        }

        /// <summary>
        /// Fetches a maven-metadata.xml file and returns every <c>&lt;version&gt;</c> entry.
        /// Throws on allowlist violations, timeouts, HTTP errors or malformed XML.
        /// </summary>
        private async Task<IReadOnlyList<string>> FetchMavenVersionsAsync(string url, CancellationToken ct)
        {
            if (!IsAllowedVersionListUrl(url))
                throw new InvalidOperationException("版本列表地址不在允许的主机列表内。");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using HttpResponseMessage response = await WithTimeoutAsync(
                () => _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct), ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("获取版本列表超时。");

            if (!IsAllowedVersionListUrl(response.RequestMessage?.RequestUri?.AbsoluteUri))
                throw new InvalidOperationException("版本列表被重定向到不允许的主机。");

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"版本列表返回 HTTP {(int)response.StatusCode}。");

            string xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseMavenVersionList(xml);
        }

        private static bool IsAllowedVersionListUrl(string? url)
        {
            return !string.IsNullOrWhiteSpace(url)
                && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && AllowedVersionListHosts.Contains(uri.Host);
        }

        /// <summary>Parses all <c>&lt;version&gt;</c> entries from maven-metadata.xml (namespace-tolerant).</summary>
        private static IReadOnlyList<string> ParseMavenVersionList(string xml)
        {
            XDocument doc = XDocument.Parse(xml);
            XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            return doc.Descendants(ns + "version")
                .Select(e => e.Value.Trim())
                .Where(v => v.Length > 0)
                .ToList();
        }

        /// <summary>
        /// Maps a NeoForge version to its Minecraft version. Returns false (no match) for
        /// snapshot <c>0.*</c> builds or unparseable versions.
        /// <para>
        /// Mirrors the NeoForge versioning scheme (see Axolotl's <c>neoforge_game_version</c>):
        /// <c>20.2.93</c> → <c>1.20.2</c>, <c>21.0</c> → <c>1.21</c>, <c>21.11</c> → <c>1.21.11</c>,
        /// and for the new Minecraft numbering (major ≥ 26) <c>26.3.0.99</c> → <c>26.3</c>.
        /// </para>
        /// </summary>
        private static bool TryMapNeoForgeMcVersion(string version, out string? mcVersion)
        {
            mcVersion = null;

            if (string.IsNullOrWhiteSpace(version))
                return false;

            string v = version.Trim();
            if (v.StartsWith("0.", StringComparison.Ordinal))
                return false; // Snapshot build.

            string[] components = v.Split('.');
            if (components.Length < 2)
                return false;

            if (!int.TryParse(components[0], out int major))
                return false;
            if (!int.TryParse(components[1], out int minor))
                return false;

            if (major >= 26)
            {
                if (components.Length < 3 || !int.TryParse(components[2], out int patch))
                    return false;
                mcVersion = patch == 0 ? $"{major}.{minor}" : $"{major}.{minor}.{patch}";
            }
            else
            {
                mcVersion = minor == 0 ? $"1.{major}" : $"1.{major}.{minor}";
            }

            return true;
        }

        /// <summary>Sort comparer: newest version first (natural, digit-run numeric ordering).</summary>
        private static int CompareVersionStringsDescending(string left, string right)
            => CompareVersionStrings(right, left);

        /// <summary>
        /// Natural version comparison: consecutive digit runs are compared numerically (leading
        /// zeros stripped), other characters ordinally, so <c>47.4.28</c> &gt; <c>47.4.5</c> and
        /// <c>21.1.265</c> &gt; <c>21.1.9</c>.
        /// </summary>
        private static int CompareVersionStrings(string left, string right)
        {
            int li = 0, ri = 0;
            while (li < left.Length && ri < right.Length)
            {
                char lc = left[li], rc = right[ri];
                bool lDigit = char.IsDigit(lc), rDigit = char.IsDigit(rc);

                if (lDigit != rDigit)
                    return lc.CompareTo(rc);

                if (lDigit)
                {
                    int ls = li, rs = ri;
                    while (li < left.Length && char.IsDigit(left[li])) li++;
                    while (ri < right.Length && char.IsDigit(right[ri])) ri++;

                    string ln = left.Substring(ls, li - ls).TrimStart('0');
                    string rn = right.Substring(rs, ri - rs).TrimStart('0');

                    if (ln.Length != rn.Length)
                        return ln.Length.CompareTo(rn.Length);

                    int cmp = string.CompareOrdinal(ln, rn);
                    if (cmp != 0)
                        return cmp;
                }
                else
                {
                    if (lc != rc)
                        return lc.CompareTo(rc);
                    li++;
                    ri++;
                }
            }

            if (li == left.Length && ri == right.Length)
                return 0;
            return li == left.Length ? -1 : 1;
        }

        public void Dispose() => _http.Dispose();
    }
}
