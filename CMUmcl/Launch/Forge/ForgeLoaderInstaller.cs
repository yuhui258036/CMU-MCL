// SPDX-License-Identifier: GPL-3.0-only
// 本文件移植/重实现自 Axolotl Launcher（YSUMCL fork）的 Forge 安装器逻辑
//（packages/app-lib/src/api/loader_metadata.rs 的 resolve_installer_profile，
//   GPL-3.0-only）。详见仓库根 LICENSE 与 NOTICE。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using CmlLib.Core;

namespace CMUmcl.Launch.Forge
{
    /// <summary>
    /// Forge 安装编排器：下载并校验安装器、解析、下载安装器库、运行 processors、
    /// 写版本档案与跳过标记，支持“跳过重装”本地校验。持有 HttpClient（IDisposable）。
    /// </summary>
    public sealed class ForgeLoaderInstaller : IDisposable
    {
        private const string ForgeMavenRoot = "https://maven.minecraftforge.net";
        private const string UserAgent = "CMUmcl/0.1.0";

        private const int MaxAttempts = 3;

        private static readonly TimeSpan[] RetryDelays =
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
        };

        private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "maven.minecraftforge.net",
            "files.minecraftforge.net",
            "libraries.minecraft.net",
            "repo1.maven.org",
            "repo.maven.apache.org",
        };

        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly HttpClient _http;

        public ForgeLoaderInstaller()
        {
            var handler = CMUmcl.Network.LauncherProxy.CreateHandler();
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }

        public static bool IsAllowedUrl(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && AllowedHosts.Contains(uri.Host);
        }

        public static void EnsureAllowedUri(Uri? uri)
        {
            if (uri == null || uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host))
                throw new LaunchException("Forge 下载被重定向到不允许的主机，已中止。");
        }

        /// <summary>
        /// True when the HTTP status code indicates a transient failure worth retrying:
        /// 408 (request timeout), 429 (rate limited), or a server error (500/502/503/504).
        /// </summary>
        public static bool IsRetryableStatus(int statusCode)
        {
            return statusCode == 408
                || statusCode == 429
                || statusCode == 500
                || statusCode == 502
                || statusCode == 503
                || statusCode == 504;
        }

        public async Task<string> InstallAsync(
            string gameVersion,
            string loaderVersion,
            JavaInfo java,
            MinecraftPath minecraftPath,
            IProgress<LaunchProgress>? progress,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(gameVersion) || string.IsNullOrWhiteSpace(loaderVersion))
                throw new LaunchException("缺少 Forge 版本信息，无法安装该加载器。");

            string versionId = $"{gameVersion}-forge-{loaderVersion}";
            string minecraftDir = minecraftPath.BasePath;
            string librariesDir = minecraftPath.Library;

            if (CanSkipInstall(versionId, gameVersion, minecraftDir, librariesDir))
            {
                progress?.Report(new LaunchProgress { Stage = "安装 Forge", Message = $"Forge {versionId} 已安装，跳过重装。" });
                return versionId;
            }

            string staging = Path.Combine(minecraftDir, ".forge-staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);

            try
            {
                string forgeVersion = $"{gameVersion}-{loaderVersion}";
                string installerUrl = $"{ForgeMavenRoot}/net/minecraftforge/forge/{forgeVersion}/forge-{forgeVersion}-installer.jar";

                // 1. 下载安装器 + .sha1 校验
                progress?.Report(new LaunchProgress { Stage = "安装 Forge", Message = "正在下载 Forge 安装器…" });
                string installerPath = Path.Combine(staging, "installer.jar");
                string expectedSha1 = await DownloadInstallerSha1Async(installerUrl + ".sha1", ct).ConfigureAwait(false);
                await DownloadToFileAsync(installerUrl, installerPath, 0, expectedSha1, "下载 Forge 安装器失败，已中止。", ct).ConfigureAwait(false);

                // 2. 解析
                progress?.Report(new LaunchProgress { Stage = "安装 Forge", Message = "正在解析 Forge 安装器…" });
                byte[] installerBytes = await File.ReadAllBytesAsync(installerPath, ct).ConfigureAwait(false);
                ParsedForgeInstaller parsed = ForgeInstallerParser.Parse(installerBytes, staging);

                // 3. 下载安装器库
                progress?.Report(new LaunchProgress { Stage = "安装 Forge", Message = "正在下载 Forge 依赖库…" });
                await InstallLibrariesAsync(parsed.InstallerLibraries, librariesDir, progress, ct).ConfigureAwait(false);

                // 4. 运行 processors
                string minecraftJar = Path.Combine(minecraftDir, "versions", gameVersion, gameVersion + ".jar");
                string javaExe = ResolveConsoleJava(java.JavaPath);
                progress?.Report(new LaunchProgress { Stage = "安装 Forge", Message = "正在运行 Forge 处理器…" });
                var runner = new ForgeProcessorRunner();
                await runner.RunAsync(javaExe, parsed.Processors, parsed.Data, minecraftJar, gameVersion, minecraftDir, librariesDir, installerPath, progress, ct).ConfigureAwait(false);

                // 5. 落盘版本档案 + install_profile.json + .forge-complete
                parsed.FinalVersionDocument["id"] = versionId;
                string versionDir = Path.Combine(minecraftDir, "versions", versionId);
                Directory.CreateDirectory(versionDir);

                string versionJsonText = parsed.FinalVersionDocument.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(Path.Combine(versionDir, versionId + ".json"), versionJsonText, ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(versionDir, "install_profile.json"), parsed.InstallProfileJson, ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(versionDir, ".forge-complete"), expectedSha1, ct).ConfigureAwait(false);

                progress?.Report(new LaunchProgress { Stage = "安装 Forge", Message = $"Forge {versionId} 安装完成。" });
                return versionId;
            }
            finally
            {
                TryDeleteDirectory(staging);
            }
        }

        public static bool CanSkipInstall(string versionId, string gameVersion, string minecraftDir, string librariesDir)
        {
            try
            {
                string versionDir = Path.Combine(minecraftDir, "versions", versionId);
                string versionJsonPath = Path.Combine(versionDir, versionId + ".json");
                string installProfilePath = Path.Combine(versionDir, "install_profile.json");
                string completePath = Path.Combine(versionDir, ".forge-complete");

                if (!File.Exists(versionJsonPath) || !File.Exists(installProfilePath) || !File.Exists(completePath))
                    return false;

                JsonNode? vn = JsonNode.Parse(File.ReadAllText(versionJsonPath));
                if (vn is not JsonObject vo)
                    return false;

                string inheritsFrom = vo["inheritsFrom"]?.GetValue<string>() ?? string.Empty;
                if (!string.Equals(inheritsFrom, gameVersion, StringComparison.Ordinal))
                    return false;

                string recorded = File.ReadAllText(completePath).Trim();
                if (recorded.Length != 40 || recorded.Any(c => !Uri.IsHexDigit(c)))
                    return false;

                InstallerProfile profile = JsonSerializer.Deserialize<InstallerProfile>(File.ReadAllText(installProfilePath), JsonOptions)
                    ?? new InstallerProfile();

                // 合并库（version.json ∪ install_profile.libraries），逐个校验存在 + sha1
                var libs = new List<ForgeLibrary>();
                if (vo["libraries"] is JsonArray arr)
                {
                    var vlibs = JsonSerializer.Deserialize<List<ForgeLibrary>>(arr.ToJsonString(), JsonOptions) ?? new List<ForgeLibrary>();
                    libs.AddRange(vlibs);
                }
                var names = new HashSet<string>(libs.Select(l => l.Name), StringComparer.Ordinal);
                foreach (ForgeLibrary l in profile.Libraries)
                    if (names.Add(l.Name))
                        libs.Add(l);

                foreach (ForgeLibrary lib in libs)
                {
                    string rel = string.IsNullOrWhiteSpace(lib.Downloads?.Artifact?.Path)
                        ? MavenArtifact.ToRelativePath(lib.Name)
                        : ForgePathSafety.SanitizeRelativePath(lib.Downloads.Artifact.Path);
                    string p = Path.Combine(librariesDir, rel);
                    if (!File.Exists(p))
                        return false;
                    string sha1 = lib.Downloads?.Artifact?.Sha1 ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(sha1)
                        && !MojangRuntimeInstaller.MatchesSha1AndSize(p, lib.Downloads?.Artifact?.Size ?? 0, sha1))
                        return false;
                }

                // outputs 校验（仅 client 处理器）
                var clientMap = ForgeProcessorRunner.BuildClientDataMap(profile.Data);
                foreach (ForgeProcessor proc in profile.Processors)
                {
                    if (!ForgeProcessorRunner.ShouldRun(proc))
                        continue;
                    ForgeProcessorRunner.ValidateOutputs(proc, clientMap, librariesDir);
                }

                return true;
            }
            catch (Exception ex) when (ex is LaunchException or IOException or JsonException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // 下载
        // ------------------------------------------------------------------

        private async Task<string> DownloadInstallerSha1Async(string sha1Url, CancellationToken ct)
        {
            return await WithRetryAsync(async () =>
            {
                if (!IsAllowedUrl(sha1Url))
                    throw new LaunchException("下载 Forge 安装器失败（.sha1 地址不在允许的主机列表内），已中止。");

                using var request = new HttpRequestMessage(HttpMethod.Get, sha1Url);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                EnsureAllowedUri(response.RequestMessage?.RequestUri);

                if (IsRetryableStatus((int)response.StatusCode))
                    throw new RetryableHttpException((int)response.StatusCode);
                if (!response.IsSuccessStatusCode)
                    throw new LaunchException($"下载 Forge 安装器失败（.sha1 缺失，HTTP {(int)response.StatusCode}），已中止。");

                string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                string hash = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                if (hash.Length != 40 || hash.Any(c => !Uri.IsHexDigit(c)))
                    throw new LaunchException("下载 Forge 安装器失败（.sha1 内容非法），已中止。");

                return hash.ToLowerInvariant();
            }, Sha1DownloadError, ct).ConfigureAwait(false);
        }

        private async Task InstallLibrariesAsync(List<ForgeLibrary> libraries, string librariesDir, IProgress<LaunchProgress>? progress, CancellationToken ct)
        {
            int done = 0;
            foreach (ForgeLibrary lib in libraries)
            {
                ct.ThrowIfCancellationRequested();

                ForgeLibraryDownload? artifact = lib.Downloads?.Artifact;
                string rel = string.IsNullOrWhiteSpace(artifact?.Path)
                    ? MavenArtifact.ToRelativePath(lib.Name)
                    : ForgePathSafety.SanitizeRelativePath(artifact.Path);
                string target = ForgePathSafety.ResolveUnder(librariesDir, rel);

                string url = artifact?.Url ?? string.Empty;
                string sha1 = artifact?.Sha1 ?? string.Empty;
                long size = artifact?.Size ?? 0;

                if (string.IsNullOrWhiteSpace(url))
                    throw new LaunchException($"安装 Forge 依赖库失败（{lib.Name}：缺少下载地址），已中止。");

                if (File.Exists(target) && !string.IsNullOrWhiteSpace(sha1) && MojangRuntimeInstaller.MatchesSha1AndSize(target, size, sha1))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await DownloadToFileAsync(url, target, size, sha1, $"安装 Forge 依赖库失败（{lib.Name}），已中止。", ct).ConfigureAwait(false);

                done++;
                progress?.Report(new LaunchProgress { Stage = "安装 Forge", ProgressedTasks = done, TotalTasks = libraries.Count, Message = $"已下载 Forge 依赖库 {done}/{libraries.Count}" });
            }
        }

        private async Task DownloadToFileAsync(string url, string targetPath, long expectedSize, string expectedSha1, string failureMessage, CancellationToken ct)
        {
            await WithRetryAsync(async () =>
            {
                if (!IsAllowedUrl(url))
                    throw new LaunchException(failureMessage);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                EnsureAllowedUri(response.RequestMessage?.RequestUri);

                if (IsRetryableStatus((int)response.StatusCode))
                    throw new RetryableHttpException((int)response.StatusCode);
                if (!response.IsSuccessStatusCode)
                    throw new LaunchException(failureMessage);

                using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                await using var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    sha1.AppendData(buffer, 0, read);
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    total += read;
                }

                string actual = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
                bool sizeOk = expectedSize <= 0 || total == expectedSize;
                bool sha1Ok = string.IsNullOrWhiteSpace(expectedSha1)
                    || string.Equals(actual, expectedSha1.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);

                if (!sizeOk || !sha1Ok)
                    throw new LaunchException(failureMessage);

                return true;
            }, ex => FileDownloadError(ex, failureMessage), ct).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------
        // 有界重试（Forge 专属，独立于 MojangRuntimeInstaller）
        // ------------------------------------------------------------------

        private static bool IsTransient(Exception ex, CancellationToken ct)
        {
            return ex is HttpRequestException
                || ex is IOException
                || ex is RetryableHttpException
                || (ex is TaskCanceledException && !ct.IsCancellationRequested);
        }

        private static TimeSpan RetryDelayFor(int attempt)
            => attempt - 1 < RetryDelays.Length ? RetryDelays[attempt - 1] : RetryDelays[^1];

        private static LaunchException Sha1DownloadError(Exception ex) => ex switch
        {
            RetryableHttpException h => new LaunchException($"下载 Forge 安装器失败（.sha1 缺失，HTTP {h.StatusCode}），已中止。", ex),
            TaskCanceledException => new LaunchException("下载 Forge 安装器失败（.sha1 下载超时），已中止。", ex),
            _ => new LaunchException("下载 Forge 安装器失败（.sha1 网络错误），已中止。", ex),
        };

        private static LaunchException FileDownloadError(Exception ex, string failureMessage)
        {
            string reason = ex switch
            {
                RetryableHttpException h => $"HTTP {h.StatusCode}",
                TaskCanceledException => "下载超时",
                _ => "网络错误",
            };
            return new LaunchException($"{failureMessage.TrimEnd('。')}（{reason}）。", ex);
        }

        private static async Task<T> WithRetryAsync<T>(
            Func<Task<T>> operation,
            Func<Exception, LaunchException> finalError,
            CancellationToken ct)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await operation().ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex, ct))
                {
                    await Task.Delay(RetryDelayFor(attempt), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsTransient(ex, ct))
                {
                    throw finalError(ex);
                }
            }
        }

        private sealed class RetryableHttpException : Exception
        {
            public RetryableHttpException(int statusCode) : base($"HTTP {statusCode}")
            {
                StatusCode = statusCode;
            }

            public int StatusCode { get; }
        }

        // ------------------------------------------------------------------
        // 工具
        // ------------------------------------------------------------------

        private static string ResolveConsoleJava(string javawPath)
        {
            string? dir = Path.GetDirectoryName(javawPath);
            if (!string.IsNullOrEmpty(dir))
            {
                string java = Path.Combine(dir, "java.exe");
                if (File.Exists(java))
                    return java;
            }
            return javawPath;
        }

        private static void TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch { /* best effort */ }
        }

        public void Dispose() => _http.Dispose();
    }
}
