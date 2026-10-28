using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using CMUmcl.Content;

namespace CMUmcl.Launch
{
    /// <summary>
    /// Downloads, verifies and installs a single Mojang official Java runtime for
    /// <c>windows-x64</c>. Local JDKs are always preferred by the caller; this is the
    /// last-resort path. Enforces a strict URL host allowlist, SHA-1/size verification,
    /// zip-slip-safe path handling, and staging + atomic move so a half-downloaded
    /// runtime never remains.
    /// </summary>
    public sealed class MojangRuntimeInstaller : IDisposable
    {
        private const string IndexUrl =
            "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

        private const string PlatformKey = "windows-x64";
        private const string CompleteMarker = ".complete";

        private const int MaxAttempts = 3;

        private static readonly TimeSpan[] RetryDelays =
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
        };

        private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "piston-meta.mojang.com",
            "piston-data.mojang.com",
        };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly HttpClient _http;

        public MojangRuntimeInstaller()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("CMUmcl/0.1.0");
        }

        /// <summary>
        /// Ensures the given Mojang runtime component (e.g. java-runtime-epsilon) is
        /// installed at %LOCALAPPDATA%\CMUmcl\java\runtime\{component} and returns the
        /// detected JavaInfo. Reuses an existing valid install (marked by .complete).
        /// </summary>
        public async Task<JavaInfo> EnsureRuntimeAsync(
            string component,
            int requiredMajor,
            IProgress<LaunchProgress>? progress,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(component))
                throw new LaunchException("缺少 Java 运行时组件名。");

            ValidateComponentName(component);

            if (!IsWindowsX64())
                throw new LaunchException($"当前仅支持 64 位 Windows，无法自动下载 Java 运行时，请手动安装 Java {requiredMajor}。");

            string finalDir = Path.Combine(LauncherPaths.JavaRuntimeDirectory, component);

            JavaInfo? existing = TryReuse(finalDir, requiredMajor);
            if (existing != null)
                return existing;

            string staging = LauncherPaths.CreateStagingDirectory();
            try
            {
                // 1. Fetch and locate the component in the windows-x64 index.
                string indexJson = await DownloadTextAsync(IndexUrl, ct).ConfigureAwait(false);
                var index = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<RuntimeEntry>>>>(indexJson, JsonOptions)
                    ?? throw new LaunchException("Java 运行时索引为空或格式无效。");

                if (!index.TryGetValue(PlatformKey, out Dictionary<string, List<RuntimeEntry>>? group))
                    throw new LaunchException($"当前平台不支持自动下载该 Java 运行时，请手动安装 Java {requiredMajor}。");

                if (!group.TryGetValue(component, out List<RuntimeEntry>? entries) || entries == null || entries.Count == 0)
                    throw new LaunchException($"当前平台不支持自动下载该 Java 运行时，请手动安装 Java {requiredMajor}。");

                RuntimeEntry entry = entries[0];

                string manifestUrl = entry.Manifest?.Url ?? string.Empty;
                long manifestSize = entry.Manifest?.Size ?? 0;
                string manifestSha1 = entry.Manifest?.Sha1 ?? string.Empty;
                string versionName = entry.Version?.Name ?? string.Empty;

                if (string.IsNullOrWhiteSpace(manifestUrl))
                    throw new LaunchException("Java 运行时清单地址缺失，无法下载。");

                // 2. Download + verify the manifest.
                string manifestPath = Path.Combine(staging, "manifest.json");
                await DownloadToFileAsync(manifestUrl, manifestPath, manifestSize, manifestSha1,
                    "Java 运行时清单校验失败，已中止。", progress, 0, 0, null, 0, ct).ConfigureAwait(false);

                var manifest = JsonSerializer.Deserialize<RuntimeManifest>(
                        await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false), JsonOptions)
                    ?? throw new LaunchException("Java 运行时清单为空或格式无效。");

                Dictionary<string, RuntimeFileEntry> files = manifest.Files ?? new Dictionary<string, RuntimeFileEntry>();

                // 3. Pre-compute totals for progress.
                int totalFiles = files.Count(f => f.Value?.Type == "file");
                long totalBytes = files.Where(f => f.Value?.Type == "file")
                    .Sum(f => f.Value?.Downloads?.Raw?.Size ?? 0);

                var counter = new ByteCounter();
                int done = 0;

                foreach (var kv in files)
                {
                    ct.ThrowIfCancellationRequested();

                    string relative = SanitizeRelativePath(kv.Key);
                    RuntimeFileEntry? fe = kv.Value;

                    if (fe == null)
                        throw new LaunchException("Java 运行时清单条目无效，已中止。");

                    if (fe.Type == "directory")
                    {
                        Directory.CreateDirectory(ResolveUnder(staging, relative));
                        continue;
                    }

                    if (fe.Type != "file")
                        continue; // Ignore unknown types / links.

                    string fileUrl = fe.Downloads?.Raw?.Url ?? string.Empty;
                    long fileSize = fe.Downloads?.Raw?.Size ?? 0;
                    string fileSha1 = fe.Downloads?.Raw?.Sha1 ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(fileUrl))
                        throw new LaunchException($"Java 运行时文件缺少下载地址（{relative}），已中止。");

                    string targetPath = ResolveUnder(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                    await DownloadToFileAsync(fileUrl, targetPath, fileSize, fileSha1,
                        $"Java 运行时文件校验失败（{relative}），已中止。", progress, done, totalFiles, counter, totalBytes, ct).ConfigureAwait(false);

                    done++;
                }

                // 4. Mark complete and atomically move to the final directory.
                File.WriteAllText(Path.Combine(staging, CompleteMarker), string.Empty);

                if (Directory.Exists(finalDir))
                    TryDeleteDirectory(finalDir);

                Directory.CreateDirectory(LauncherPaths.JavaRuntimeDirectory);
                Directory.Move(staging, finalDir);

                // 5. Verify the installed runtime major version.
                JavaInfo? detected = JavaLocator.Detect(finalDir);
                if (detected == null || detected.Major <= 0 || detected.Major < requiredMajor)
                {
                    TryDeleteDirectory(finalDir);
                    throw new LaunchException("Java 运行时校验失败，已清除不完整的下载。");
                }

                return new JavaInfo
                {
                    JavaPath = detected.JavaPath,
                    Version = versionName,
                    Major = detected.Major,
                };
            }
            catch
            {
                TryDeleteDirectory(staging);
                throw;
            }
        }

        // ------------------------------------------------------------------
        // Public static security / verification helpers (unit-testable)
        // ------------------------------------------------------------------

        /// <summary>True when the URL is an absolute HTTPS URL on an allowed host.</summary>
        public static bool IsAllowedUrl(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && AllowedHosts.Contains(uri.Host);
        }

        /// <summary>Throws when a (possibly redirected) final URI is not allowed.</summary>
        public static void EnsureAllowedUri(Uri? uri)
        {
            if (uri == null || uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host))
                throw new LaunchException("Java 运行时下载被重定向到不允许的主机，已中止。");
        }

        /// <summary>
        /// True when the HTTP status code indicates a transient failure worth retrying:
        /// 408 (request timeout), 429 (rate limited), or a 5xx server error (500/502/503/504).
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

        /// <summary>
        /// Normalizes a manifest relative path and rejects traversal. Returns the
        /// normalized relative path using the platform directory separator.
        /// </summary>
        public static string SanitizeRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new LaunchException("Java 运行时清单包含空路径，已中止。");

            string normalized = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

            if (Path.IsPathRooted(normalized))
                throw new LaunchException($"检测到非法路径：{path}");

            string[] segments = normalized.Split(Path.DirectorySeparatorChar);
            foreach (string segment in segments)
            {
                if (segment == ".." || segment == ".")
                    throw new LaunchException($"检测到路径越界：{path}");
                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new LaunchException($"检测到非法路径字符：{path}");
            }

            return normalized;
        }

        /// <summary>Resolves a relative path under a base directory and rejects escapes.</summary>
        public static string ResolveUnder(string baseDir, string relativePath)
        {
            string fullBase = Path.GetFullPath(baseDir);
            string fullTarget = Path.GetFullPath(Path.Combine(fullBase, relativePath));

            string baseWithSep = fullBase.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? fullBase
                : fullBase + Path.DirectorySeparatorChar;

            if (!fullTarget.StartsWith(baseWithSep, StringComparison.OrdinalIgnoreCase))
                throw new LaunchException($"目标路径超出运行时目录：{relativePath}");

            return fullTarget;
        }

        /// <summary>True when the file's SHA-1 and size match the expected values.</summary>
        public static bool MatchesSha1AndSize(string filePath, long expectedSize, string expectedSha1)
        {
            try
            {
                var info = new FileInfo(filePath);
                if (expectedSize > 0 && info.Length != expectedSize)
                    return false;

                if (string.IsNullOrWhiteSpace(expectedSha1))
                    return true;

                using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                using var fs = File.OpenRead(filePath);
                var buffer = new byte[81920];
                int read;
                while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                    sha1.AppendData(buffer, 0, read);

                string actual = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
                return string.Equals(actual, expectedSha1.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Internals
        // ------------------------------------------------------------------

        private static bool IsWindowsX64()
            => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
               && Environment.Is64BitOperatingSystem
               && RuntimeInformation.OSArchitecture == Architecture.X64;

        private static void ValidateComponentName(string component)
        {
            if (component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || component.Contains('/')
                || component.Contains('\\')
                || component.Contains(':')
                || component == "."
                || component == "..")
                throw new LaunchException("Java 运行时组件名非法。");
        }

        private static JavaInfo? TryReuse(string finalDir, int requiredMajor)
        {
            if (!Directory.Exists(finalDir))
                return null;

            if (!File.Exists(Path.Combine(finalDir, CompleteMarker)))
                return null;

            JavaInfo? detected = JavaLocator.Detect(finalDir);
            if (detected == null || detected.Major <= 0 || detected.Major < requiredMajor)
                return null;

            return detected;
        }

        private async Task<string> DownloadTextAsync(string url, CancellationToken ct)
        {
            if (!IsAllowedUrl(url))
                throw new LaunchException("Java 运行时下载地址不在允许的主机列表内，已中止。");

            return await WithRetryAsync(async () =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                EnsureAllowedUri(response.RequestMessage?.RequestUri);

                if (IsRetryableStatus((int)response.StatusCode))
                    throw new RetryableHttpException((int)response.StatusCode);

                if (!response.IsSuccessStatusCode)
                    throw new LaunchException($"获取 Java 运行时索引失败（HTTP {(int)response.StatusCode}）。");

                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }, IndexDownloadError, ct).ConfigureAwait(false);
        }

        private async Task DownloadToFileAsync(
            string url,
            string targetPath,
            long expectedSize,
            string expectedSha1,
            string failureMessage,
            IProgress<LaunchProgress>? progress,
            int doneFiles,
            int totalFiles,
            ByteCounter? counter,
            long totalBytes,
            CancellationToken ct)
        {
            if (!IsAllowedUrl(url))
                throw new LaunchException("Java 运行时下载地址不在允许的主机列表内，已中止。");

            await WithRetryAsync(async () =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                EnsureAllowedUri(response.RequestMessage?.RequestUri);

                if (IsRetryableStatus((int)response.StatusCode))
                    throw new RetryableHttpException((int)response.StatusCode);

                if (!response.IsSuccessStatusCode)
                    throw new LaunchException($"下载 Java 运行时失败（HTTP {(int)response.StatusCode}）。");

                using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                long attemptBytes = 0;
                int read;
                while ((read = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    sha1.AppendData(buffer, 0, read);
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    attemptBytes += read;

                    if (counter != null)
                    {
                        progress?.Report(new LaunchProgress
                        {
                            Stage = "下载 Java",
                            ProgressedTasks = doneFiles,
                            TotalTasks = totalFiles,
                            ProgressedBytes = counter.Value + attemptBytes,
                            TotalBytes = totalBytes,
                            Message = $"已下载 {doneFiles}/{totalFiles} 个文件（{FormatUtils.FormatBytes(counter.Value + attemptBytes)} / {FormatUtils.FormatBytes(totalBytes)}）",
                        });
                    }
                }

                string actualSha1 = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
                bool sizeOk = expectedSize <= 0 || attemptBytes == expectedSize;
                bool sha1Ok = string.IsNullOrWhiteSpace(expectedSha1)
                    || string.Equals(actualSha1, expectedSha1.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);

                if (!sizeOk || !sha1Ok)
                    throw new LaunchException(failureMessage);

                if (counter != null)
                    counter.Value += attemptBytes;

                return true; // Success marker; the retry helper is generic over the operation's result.
            }, RuntimeDownloadError, ct, attempt =>
            {
                progress?.Report(new LaunchProgress
                {
                    Stage = "下载 Java",
                    ProgressedTasks = doneFiles,
                    TotalTasks = totalFiles,
                    ProgressedBytes = counter?.Value ?? 0,
                    TotalBytes = totalBytes,
                    Message = $"下载中断，正在重试（第 {attempt} 次）……",
                });
            }).ConfigureAwait(false);
        }

        private static void TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }

        // ------------------------------------------------------------------
        // Bounded retry for transient network failures
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

        private static LaunchException IndexDownloadError(Exception ex) => ex switch
        {
            RetryableHttpException h => new LaunchException($"获取 Java 运行时索引失败（HTTP {h.StatusCode}）。", ex),
            TaskCanceledException => new LaunchException("获取 Java 运行时索引超时，请稍后再试。", ex),
            _ => new LaunchException("网络错误，无法获取 Java 运行时索引，请检查网络连接。", ex),
        };

        private static LaunchException RuntimeDownloadError(Exception ex) => ex switch
        {
            RetryableHttpException h => new LaunchException($"下载 Java 运行时失败（HTTP {h.StatusCode}）。", ex),
            TaskCanceledException => new LaunchException("下载 Java 运行时超时，请稍后再试。", ex),
            _ => new LaunchException("网络错误，无法下载 Java 运行时，请检查网络连接。", ex),
        };

        private static async Task<T> WithRetryAsync<T>(
            Func<Task<T>> operation,
            Func<Exception, LaunchException> finalError,
            CancellationToken ct,
            Action<int>? onRetry = null)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await operation().ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex, ct))
                {
                    onRetry?.Invoke(attempt);
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

        private sealed class ByteCounter
        {
            public long Value;
        }

        private sealed class RuntimeEntry
        {
            public RuntimeManifestRef? Manifest { get; set; }
            public RuntimeVersionInfo? Version { get; set; }
        }

        private sealed class RuntimeManifestRef
        {
            public string? Sha1 { get; set; }
            public long? Size { get; set; }
            public string? Url { get; set; }
        }

        private sealed class RuntimeVersionInfo
        {
            public string? Name { get; set; }
        }

        private sealed class RuntimeManifest
        {
            public Dictionary<string, RuntimeFileEntry>? Files { get; set; }
        }

        private sealed class RuntimeFileEntry
        {
            public string? Type { get; set; }
            public RuntimeDownloads? Downloads { get; set; }
        }

        private sealed class RuntimeDownloads
        {
            public RuntimeRaw? Raw { get; set; }
        }

        private sealed class RuntimeRaw
        {
            public string? Sha1 { get; set; }
            public long? Size { get; set; }
            public string? Url { get; set; }
        }

        public void Dispose() => _http.Dispose();
    }
}
