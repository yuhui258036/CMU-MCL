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

namespace CMUmcl.Content
{
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
        /// Minecraft release version names, newest first. Returns an empty list on any
        /// network/timeout/parse failure (the dialog then relies on manual input).
        /// </summary>
        public async Task<IReadOnlyList<string>> GetMinecraftReleaseVersionsAsync(CancellationToken ct = default)
        {
            try
            {
                var versions = await WithTimeoutAsync(
                    () => _launcher.GetAllVersionsAsync(ct).AsTask(), ct).ConfigureAwait(false);
                if (versions == null)
                    return Array.Empty<string>();

                return versions
                    .Where(v => string.Equals(v.Type, "release", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(v => v.ReleaseTime)
                    .Select(v => v.Name!)
                    .ToList();
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

        /// <summary>Fabric loader versions for a game version, stable-first. Empty on any failure.</summary>
        public async Task<IReadOnlyList<string>> GetFabricLoaderVersionsAsync(string gameVersion, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(gameVersion))
                return Array.Empty<string>();

            try
            {
                var loaders = await WithTimeoutAsync(
                    () => _fabric.GetLoaders(gameVersion.Trim()), ct).ConfigureAwait(false);
                if (loaders == null)
                    return Array.Empty<string>();

                return loaders
                    .Where(l => !string.IsNullOrWhiteSpace(l.Version))
                    .OrderByDescending(l => l.Stable)
                    .ThenByDescending(l => l.Build)
                    .ThenByDescending(l => l.Version, StringComparer.OrdinalIgnoreCase)
                    .Select(l => l.Version!)
                    .ToList();
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
                var loaders = await WithTimeoutAsync(
                    () => _quilt.GetLoaders(gameVersion.Trim()), ct).ConfigureAwait(false);
                if (loaders == null)
                    return Array.Empty<string>();

                return loaders
                    .Where(l => !string.IsNullOrWhiteSpace(l.Version))
                    .OrderByDescending(l => l.Stable)
                    .ThenByDescending(l => l.Build ?? 0)
                    .ThenByDescending(l => l.Version, StringComparer.OrdinalIgnoreCase)
                    .Select(l => l.Version!)
                    .ToList();
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
                IReadOnlyList<string> all = await GetOrFetchForgeVersionsAsync(ct).ConfigureAwait(false);
                string mc = gameVersion.Trim();

                var result = new List<string>();
                foreach (string version in all)
                {
                    int dash = version.IndexOf('-');
                    if (dash <= 0)
                        continue;

                    if (!string.Equals(version.Substring(0, dash), mc, StringComparison.OrdinalIgnoreCase))
                        continue;

                    result.Add(version.Substring(dash + 1));
                }

                result.Sort(CompareVersionStringsDescending);
                return result;
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
                IReadOnlyList<string> all = await GetOrFetchNeoForgeVersionsAsync(ct).ConfigureAwait(false);
                string mc = gameVersion.Trim();

                var result = new List<string>();
                foreach (string version in all)
                {
                    if (!TryMapNeoForgeMcVersion(version, out string? mapped))
                        continue;

                    if (!string.Equals(mapped, mc, StringComparison.OrdinalIgnoreCase))
                        continue;

                    result.Add(version);
                }

                result.Sort(CompareVersionStringsDescending);
                return result;
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
