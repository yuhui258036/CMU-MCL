using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using CmlLib.Core;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.ModLoaders.QuiltMC;

namespace CMUmcl.Content
{
    /// <summary>
    /// Loads Minecraft / Fabric / Quilt version lists for the create-instance dialog.
    /// Every public method is defensive: network failures, timeouts and malformed
    /// responses return empty collections so the dialog stays usable with manual
    /// input even when the app is offline or behind a restrictive proxy.
    /// </summary>
    public sealed class VersionCatalogService : IDisposable
    {
        private const string UserAgent = "CMUmcl/0.1.0";

        /// <summary>Per-request timeout for the (small) version-list endpoints.</summary>
        private static readonly TimeSpan VersionListTimeout = TimeSpan.FromSeconds(15);

        private readonly HttpClient _http;
        private readonly MinecraftLauncher _launcher;
        private readonly FabricInstaller _fabric;
        private readonly QuiltInstaller _quilt;

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

        public void Dispose() => _http.Dispose();
    }
}
