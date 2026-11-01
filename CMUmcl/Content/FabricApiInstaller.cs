using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace CMUmcl.Content
{
    /// <summary>Progress reported while downloading the Fabric API mod.</summary>
    public sealed class FabricApiProgress
    {
        public string Stage { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public long? BytesDownloaded { get; set; }
        public long? TotalBytes { get; set; }
    }

    /// <summary>
    /// Outcome of a Fabric API install attempt. Failures are returned (never thrown) so that
    /// instance creation always continues; the caller surfaces <see cref="ErrorMessage"/> as a warning.
    /// </summary>
    public sealed class FabricApiResult
    {
        public bool Success { get; init; }
        public string FileName { get; init; } = string.Empty;
        public string FilePath { get; init; } = string.Empty;
        public string ErrorMessage { get; init; } = string.Empty;
    }

    /// <summary>
    /// Downloads the latest Fabric API build compatible with a Minecraft version from the
    /// Modrinth v2 API and installs it into <c>{instanceDir}\mods</c>.
    /// <para>
    /// Fabric API is a Fabric loader mod; it is queried with <c>loaders=["fabric"]</c> for both
    /// Fabric and Quilt instances because Quilt is Fabric-compatible and consumes the same jar.
    /// </para>
    /// Enforces an HTTPS host allowlist (api.modrinth.com / cdn.modrinth.com) and verifies the
    /// downloaded file against the SHA-1/SHA-512 hashes reported by Modrinth.
    /// </summary>
    public sealed class FabricApiInstaller : IDisposable
    {
        private const string UserAgent = "CMUmcl/0.1.0 (Fabric API installer)";
        private const string ApiBase = "https://api.modrinth.com/v2";
        private const string ProjectSlug = "fabric-api";

        private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "api.modrinth.com",
            "cdn.modrinth.com",
        };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly HttpClient _http;

        public FabricApiInstaller()
        {
            var handler = CMUmcl.Network.LauncherProxy.CreateHandler();

            // Mods are usually a few MB, but slow links exist; give the download room.
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }

        /// <summary>
        /// Installs Fabric API into <paramref name="instanceDirectory"/>. Returns a result object;
        /// never throws for network/timeout/verification failures (cancellation is honored).
        /// </summary>
        public async Task<FabricApiResult> InstallAsync(
            string gameVersion,
            string instanceDirectory,
            IProgress<FabricApiProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(gameVersion))
                return new FabricApiResult { ErrorMessage = "缺少 Minecraft 版本，无法下载 Fabric API。" };
            if (string.IsNullOrWhiteSpace(instanceDirectory))
                return new FabricApiResult { ErrorMessage = "实例目录为空，无法下载 Fabric API。" };

            try
            {
                progress?.Report(new FabricApiProgress { Stage = "下载 Fabric API", Message = "正在查询 Fabric API 版本…" });

                string url = BuildQueryUrl(gameVersion.Trim());
                var versions = await GetVersionsAsync(url, ct).ConfigureAwait(false);

                ModrinthVersion? selected = SelectVersion(versions, gameVersion.Trim());
                if (selected == null)
                    return new FabricApiResult { ErrorMessage = $"未找到适用于 Minecraft {gameVersion.Trim()} 的 Fabric API。" };

                ModrinthFile? file = selected.Files?
                    .OrderByDescending(f => f.Primary)
                    .FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.Url));
                if (file == null)
                    return new FabricApiResult { ErrorMessage = "Fabric API 版本缺少可下载的文件。" };

                if (!IsAllowedUrl(file.Url))
                    return new FabricApiResult { ErrorMessage = "Fabric API 下载地址不在允许的主机列表内。" };

                string modsDir = Path.Combine(instanceDirectory, "mods");
                Directory.CreateDirectory(modsDir);

                string fileName = SafeFileName(file.FileName, selected.VersionNumber);
                string targetPath = Path.Combine(modsDir, fileName);

                progress?.Report(new FabricApiProgress
                {
                    Stage = "下载 Fabric API",
                    Message = fileName,
                    TotalBytes = file.Size > 0 ? file.Size : null,
                });

                bool ok = await DownloadAndVerifyAsync(file, targetPath, progress, ct).ConfigureAwait(false);
                if (!ok)
                {
                    TryDelete(targetPath);
                    return new FabricApiResult { ErrorMessage = "Fabric API 校验失败（哈希或大小不匹配）。" };
                }

                return new FabricApiResult { Success = true, FileName = fileName, FilePath = targetPath };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new FabricApiResult { ErrorMessage = "下载 Fabric API 已取消。" };
            }
            catch (TaskCanceledException)
            {
                return new FabricApiResult { ErrorMessage = "下载 Fabric API 超时。" };
            }
            catch (HttpRequestException)
            {
                return new FabricApiResult { ErrorMessage = "网络错误，无法下载 Fabric API。" };
            }
            catch (Exception ex)
            {
                return new FabricApiResult { ErrorMessage = "下载 Fabric API 失败：" + ex.Message };
            }
        }

        /// <summary>True when the URL is an absolute HTTPS URL on an allowed host.</summary>
        public static bool IsAllowedUrl(string? url)
        {
            return !string.IsNullOrWhiteSpace(url)
                && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && AllowedHosts.Contains(uri.Host);
        }

        // ------------------------------------------------------------------
        // Internals
        // ------------------------------------------------------------------

        private static string BuildQueryUrl(string gameVersion)
        {
            // Fabric API is a Fabric-loader mod; Quilt consumes the same build (Fabric-compatible).
            string loaders = Uri.EscapeDataString("[\"fabric\"]");
            string versions = Uri.EscapeDataString("[\"" + gameVersion + "\"]");
            return $"{ApiBase}/project/{ProjectSlug}/version?loaders={loaders}&game_versions={versions}";
        }

        private async Task<List<ModrinthVersion>> GetVersionsAsync(string url, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!IsAllowedUrl(response.RequestMessage?.RequestUri?.AbsoluteUri))
                throw new InvalidOperationException("Modrinth API 重定向到了不允许的主机。");

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Modrinth API 返回 HTTP {(int)response.StatusCode}。");

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<ModrinthVersion>>(json, JsonOptions) ?? new List<ModrinthVersion>();
        }

        private static ModrinthVersion? SelectVersion(List<ModrinthVersion> versions, string gameVersion)
        {
            bool Matches(ModrinthVersion v) =>
                (v.GameVersions == null || v.GameVersions.Count == 0 || v.GameVersions.Contains(gameVersion, StringComparer.OrdinalIgnoreCase))
                && (v.Loaders == null || v.Loaders.Count == 0 || v.Loaders.Contains("fabric", StringComparer.OrdinalIgnoreCase))
                && v.Files != null
                && v.Files.Any(f => !string.IsNullOrWhiteSpace(f.Url));

            return versions
                .Where(Matches)
                .OrderByDescending(v => v.DatePublished)
                .FirstOrDefault();
        }

        private async Task<bool> DownloadAndVerifyAsync(
            ModrinthFile file,
            string targetPath,
            IProgress<FabricApiProgress>? progress,
            CancellationToken ct)
        {
            string tempPath = targetPath + ".tmp";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                // A redirect could land on a non-allowlisted host; reject it.
                if (!IsAllowedUrl(response.RequestMessage?.RequestUri?.AbsoluteUri))
                    return false;

                if (!response.IsSuccessStatusCode)
                    return false;

                using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);

                long total = 0;
                long lastReported = 0;

                // Stream to a temp file and hash incrementally. The file handle must be
                // released (using block ends) before the file can be moved into place.
                await using (var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        sha1.AppendData(buffer, 0, read);
                        sha512.AppendData(buffer, 0, read);
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        total += read;

                        if (progress != null && total - lastReported >= 131072)
                        {
                            lastReported = total;
                            progress.Report(new FabricApiProgress
                            {
                                Stage = "下载 Fabric API",
                                Message = file.FileName,
                                BytesDownloaded = total,
                                TotalBytes = file.Size > 0 ? file.Size : null,
                            });
                        }
                    }
                }

                if (file.Size > 0 && total != file.Size)
                    return false;

                if (!MatchesHash(sha1, file.Hashes?.Sha1))
                    return false;
                if (!MatchesHash(sha512, file.Hashes?.Sha512))
                    return false;

                if (File.Exists(targetPath))
                    File.Delete(targetPath);
                File.Move(tempPath, targetPath);
                return true;
            }
            finally
            {
                TryDelete(tempPath);
            }
        }

        private static bool MatchesHash(IncrementalHash hash, string? expectedHex)
        {
            if (string.IsNullOrWhiteSpace(expectedHex))
                return true;

            string actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            return string.Equals(actual, expectedHex.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeFileName(string? fileName, string? versionNumber)
        {
            string name = Path.GetFileName(fileName ?? string.Empty).Trim();
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                string token = SanitizeToken((versionNumber ?? "fabric-api").Trim());
                name = token.Length == 0 ? "fabric-api.jar" : $"fabric-api-{token}.jar";
            }
            return name;
        }

        private static string SanitizeToken(string token)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            return new string(token.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best effort.
            }
        }

        public void Dispose() => _http.Dispose();

        // ------------------------------------------------------------------
        // Modrinth v2 DTOs
        // ------------------------------------------------------------------

        private sealed class ModrinthVersion
        {
            [JsonPropertyName("id")]
            public string Id { get; set; } = string.Empty;

            [JsonPropertyName("version_number")]
            public string VersionNumber { get; set; } = string.Empty;

            [JsonPropertyName("loaders")]
            public List<string>? Loaders { get; set; }

            [JsonPropertyName("game_versions")]
            public List<string>? GameVersions { get; set; }

            [JsonPropertyName("date_published")]
            public DateTimeOffset DatePublished { get; set; }

            [JsonPropertyName("files")]
            public List<ModrinthFile>? Files { get; set; }
        }

        private sealed class ModrinthFile
        {
            [JsonPropertyName("url")]
            public string Url { get; set; } = string.Empty;

            [JsonPropertyName("filename")]
            public string FileName { get; set; } = string.Empty;

            [JsonPropertyName("size")]
            public long Size { get; set; }

            [JsonPropertyName("primary")]
            public bool Primary { get; set; }

            [JsonPropertyName("hashes")]
            public ModrinthHashes? Hashes { get; set; }
        }

        private sealed class ModrinthHashes
        {
            [JsonPropertyName("sha1")]
            public string Sha1 { get; set; } = string.Empty;

            [JsonPropertyName("sha512")]
            public string Sha512 { get; set; } = string.Empty;
        }
    }
}
