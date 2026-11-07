// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CMUmcl.Community;

/// <summary>A newer Modrinth release of an installed mod file, matched by its exact SHA-1.</summary>
internal sealed record ModUpdate(string Path, string CurrentVersion, string LatestVersion, string FileName, string Url, string Sha1, string Sha512, long Size);

/// <summary>
/// Checks installed jars against Modrinth by file hash (never by name), restricted to the
/// instance's loader and game version, and downloads replacements with hash verification.
/// Files Modrinth does not know (for example CurseForge-only mods) are left alone.
/// </summary>
internal sealed class ModUpdateService : IDisposable
{
    private const string Api = "https://api.modrinth.com/v2";
    private const long MaxDownloadBytes = 512L * 1024 * 1024;
    private readonly HttpClient _api, _cdn;

    internal ModUpdateService() : this(CMUmcl.Network.LauncherProxy.CreateHandler(false, false), CMUmcl.Network.LauncherProxy.CreateHandler(false, false)) { }
    internal ModUpdateService(HttpMessageHandler api, HttpMessageHandler cdn)
    {
        _api = CommunityHttp.CreateApiClient(api, h => h == CommunityHosts.ModrinthApi, true);
        _cdn = CommunityHttp.CreateApiClient(cdn, h => CommunityHosts.ModrinthCdn.Contains(h), true);
        _cdn.Timeout = TimeSpan.FromMinutes(5);
    }

    /// <summary>Modrinth loader names to accept for an instance loader; null when mods do not apply.</summary>
    internal static string[]? LoadersFor(string? loader) => (loader ?? "").Trim().ToLowerInvariant() switch
    {
        "fabric" => new[] { "fabric" },
        "quilt" => new[] { "quilt", "fabric" },
        "forge" => new[] { "forge" },
        "neoforge" => new[] { "neoforge" },
        _ => null,
    };

    internal async Task<IReadOnlyList<ModUpdate>> CheckAsync(IEnumerable<string> jars, string gameVersion, string loader, CancellationToken ct)
    {
        string[] loaders = LoadersFor(loader) ?? throw new InvalidOperationException("原版实例没有可更新的模组。");
        var byHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string jar in jars)
        {
            ct.ThrowIfCancellationRequested();
            byHash[await Task.Run(() => Hash(jar, SHA1.Create()), ct).ConfigureAwait(false)] = jar;
        }
        if (byHash.Count == 0) return Array.Empty<ModUpdate>();

        string[] hashes = byHash.Keys.ToArray();
        JsonElement current = await PostAsync("/version_files", new { hashes, algorithm = "sha1" }, ct).ConfigureAwait(false);
        JsonElement latest = await PostAsync("/version_files/update", new { hashes, algorithm = "sha1", loaders, game_versions = new[] { gameVersion } }, ct).ConfigureAwait(false);

        var updates = new List<ModUpdate>();
        if (current.ValueKind != JsonValueKind.Object || latest.ValueKind != JsonValueKind.Object) return updates;
        foreach (var pair in byHash)
        {
            string hash = pair.Key, path = pair.Value;
            if (!current.TryGetProperty(hash, out JsonElement now) || !latest.TryGetProperty(hash, out JsonElement next)) continue;
            if (Text(now, "id") == Text(next, "id")) continue;
            // The update endpoint returns the newest match; never offer an older build as an update.
            if (Date(next) <= Date(now)) continue;
            if (PrimaryFile(next) is not { } file) continue;
            string url = Text(file, "url"), name = Text(file, "filename");
            JsonElement fileHashes = file.TryGetProperty("hashes", out var h) ? h : default;
            string sha1 = fileHashes.ValueKind == JsonValueKind.Object ? Text(fileHashes, "sha1") : "";
            string sha512 = fileHashes.ValueKind == JsonValueKind.Object ? Text(fileHashes, "sha512") : "";
            if (sha1.Length == 0 || !IsSafeJarName(name)) continue;
            if (!CommunityHosts.TryGetHttpsHost(url, out string? host) || !CommunityHosts.ModrinthCdn.Contains(host!)) continue;
            long size = file.TryGetProperty("size", out var s) && s.TryGetInt64(out long n) ? n : 0;
            updates.Add(new ModUpdate(path, Text(now, "version_number"), Text(next, "version_number"), name, url, sha1, sha512, size));
        }
        return updates.OrderBy(u => System.IO.Path.GetFileName(u.Path), StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Downloads the new file into a private temporary folder and verifies size and hashes.</summary>
    internal async Task<string> DownloadAsync(ModUpdate update, CancellationToken ct)
    {
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cmumcl-mod-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string target = System.IO.Path.Combine(folder, update.FileName);
        try
        {
            using var response = await _cdn.GetAsync(update.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long limit = update.Size > 0 ? update.Size : MaxDownloadBytes;
            using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > limit) throw new IOException("下载内容超过预期大小。");
                    await output.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                }
            }
            if (update.Size > 0 && new FileInfo(target).Length != update.Size) throw new IOException("下载文件大小不符。");
            if (!string.Equals(Hash(target, SHA1.Create()), update.Sha1, StringComparison.OrdinalIgnoreCase)
                || (update.Sha512.Length > 0 && !string.Equals(Hash(target, SHA512.Create()), update.Sha512, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("下载文件校验失败。");
            return target;
        }
        catch { DeleteDownload(target); throw; }
    }

    internal static void DeleteDownload(string downloaded)
    {
        try { Directory.Delete(System.IO.Path.GetDirectoryName(downloaded)!, true); } catch { /* best effort */ }
    }

    private async Task<JsonElement> PostAsync(string path, object body, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _api.PostAsync(Api + path, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var value = await CommunityHttp.ReadJsonAsync<JsonElement>(response, ResourceSourceId.Modrinth, ct).ConfigureAwait(false);
        return value.ValueKind == JsonValueKind.Object ? value : default;
    }

    private static JsonElement? PrimaryFile(JsonElement version)
    {
        if (!version.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return null;
        JsonElement? first = null;
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object) continue;
            first ??= file;
            if (file.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True) return file;
        }
        return first;
    }

    private static string Text(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static DateTimeOffset Date(JsonElement version)
        => DateTimeOffset.TryParse(Text(version, "date_published"), out var date) ? date : DateTimeOffset.MinValue;

    private static bool IsSafeJarName(string name)
        => name.Length > 4 && name.Length <= 200 && name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
           && name == System.IO.Path.GetFileName(name) && name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0;

    private static string Hash(string path, HashAlgorithm algorithm)
    {
        using (algorithm)
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    public void Dispose() { _api.Dispose(); _cdn.Dispose(); }
}
