using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CMUmcl.Content
{
    /// <summary>
    /// Downloads, verifies and installs a Modrinth (.mrpack) server pack into
    /// <c>%LOCALAPPDATA%\CMUmcl\instances\{id}</c>. Does not require Minecraft/Java.
    /// </summary>
    public sealed class PackInstaller : IDisposable
    {
        private const string UserAgent = "CMUmcl/0.1.0";
        private const string IndexEntryName = "modrinth.index.json";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

        private readonly HttpClient _http;
        private readonly InstalledStateStore _installedState;

        public PackInstaller(InstalledStateStore installedState)
        {
            _installedState = installedState;

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            };

            // Downloads (packs + individual files) may be large; use a generous timeout.
            _http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(30),
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }

        /// <summary>
        /// Installs (or updates) the given instance from its pack URL. Throws
        /// <see cref="ResourceException"/> with a Chinese message on any failure.
        /// </summary>
        public async Task InstallAsync(
            ManifestInstance instance,
            IProgress<PackInstallProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));

            // 1. Download the .mrpack into the pack cache and verify it.
            string packCacheDir = Path.Combine(LauncherPaths.PackCacheDirectory, instance.Id);
            string packPath = Path.Combine(packCacheDir, $"{instance.Id}-{instance.Revision}.mrpack");

            await DownloadAndVerifyPackAsync(instance, packPath, progress, ct).ConfigureAwait(false);

            // 2. Read modrinth.index.json from the archive.
            ModrinthIndex index = ReadIndex(packPath);

            // 3. Resolve (and create) the instance directory under the configured instances root.
            //    Reuse the recorded install path when it is ours; otherwise pick a collision-free name.
            string instancesRoot = LauncherPaths.InstancesDirectory;
            Directory.CreateDirectory(instancesRoot);

            string recordedPath = _installedState.GetInstallPath(instance.Id);
            string? existingDisplayName = _installedState.GetDisplayName(instance.Id);
            (string installPath, int suffix) = PickInstallPath(instancesRoot, instance.Id, recordedPath);
            string instanceDir = Path.Combine(instancesRoot, installPath);
            Directory.CreateDirectory(instanceDir);

            // 4. Download the listed files.
            var files = index.Files ?? new List<ModrinthFile>();
            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                ModrinthFile file = files[i];
                progress?.Report(new PackInstallProgress
                {
                    Stage = "下载文件",
                    CurrentFile = i,
                    TotalFiles = files.Count,
                    Message = file.Path,
                });
                await DownloadFileAsync(file, instanceDir, ct).ConfigureAwait(false);
            }

            // 5. Extract overrides/ then client-overrides/ into the instance dir.
            progress?.Report(new PackInstallProgress { Stage = "解压覆盖", Message = "正在应用 overrides" });
            ExtractOverrides(packPath, instanceDir, "overrides", ct);
            progress?.Report(new PackInstallProgress { Stage = "解压覆盖", Message = "正在应用 client-overrides" });
            ExtractOverrides(packPath, instanceDir, "client-overrides", ct);

            // 6. Write instance.json metadata.
            var metadata = new InstanceMetadata
            {
                Id = instance.Id,
                Name = instance.Name,
                Revision = instance.Revision,
                Game = instance.Minecraft?.GameVersion ?? string.Empty,
                Loader = instance.Minecraft?.Loader ?? string.Empty,
                LoaderVersion = instance.Minecraft?.LoaderVersion ?? string.Empty,
                ServerAddress = instance.Server?.Address ?? string.Empty,
                ServerPort = instance.Server?.Port ?? 0,
                PackSha512 = instance.Pack?.Sha512 ?? string.Empty,
                InstalledAt = DateTimeOffset.UtcNow,
            };
            await File.WriteAllTextAsync(
                Path.Combine(instanceDir, "instance.json"),
                JsonSerializer.Serialize(metadata, JsonOptions),
                ct).ConfigureAwait(false);

            // 7. Record the installed revision, install path and display name. A path collision
            //    (suffix > 0) yields a disambiguated display name; otherwise the existing display
            //    name (if any) is preserved and null falls back to the manifest name in the UI.
            string? displayName = suffix > 0 ? $"{instance.Name} ({suffix})" : existingDisplayName;
            _installedState.RecordInstall(instance.Id, instance.Revision, installPath, displayName);

            progress?.Report(new PackInstallProgress
            {
                Stage = "完成",
                CurrentFile = files.Count,
                TotalFiles = files.Count,
                Message = $"{instance.Name} 安装完成",
            });
        }

        // ------------------------------------------------------------------
        // Pack download + verification
        // ------------------------------------------------------------------

        private async Task DownloadAndVerifyPackAsync(
            ManifestInstance instance,
            string packPath,
            IProgress<PackInstallProgress>? progress,
            CancellationToken ct)
        {
            string url = instance.Pack?.Url ?? string.Empty;
            if (string.IsNullOrWhiteSpace(url))
                throw new ResourceException("整合包下载地址为空。");

            long expectedSize = instance.Pack?.Size ?? 0;
            string expectedSha512 = instance.Pack?.Sha512 ?? string.Empty;
            string expectedSha256 = instance.Pack?.Sha256 ?? string.Empty;
            string expectedSha1 = instance.Pack?.Sha1 ?? string.Empty;

            Directory.CreateDirectory(Path.GetDirectoryName(packPath)!);

            progress?.Report(new PackInstallProgress
            {
                Stage = "下载整合包",
                Message = $"{instance.Name}（r{instance.Revision}）",
                TotalBytes = expectedSize > 0 ? expectedSize : null,
            });

            var downloaded = await DownloadToFileAsync(url, packPath, progress, "下载整合包", ct).ConfigureAwait(false);

            progress?.Report(new PackInstallProgress
            {
                Stage = "校验整合包",
                Message = "正在校验哈希",
                BytesDownloaded = downloaded.Size,
                TotalBytes = downloaded.Size,
            });

            bool ok = VerifyPack(downloaded, expectedSize, expectedSha512, expectedSha256, expectedSha1);
            if (!ok)
            {
                TryDelete(packPath);
                throw new ResourceException("整合包校验失败（哈希或大小不匹配），已中止安装。");
            }
        }

        private static bool VerifyPack(DownloadedFile f, long expectedSize, string sha512, string sha256, string sha1)
        {
            if (expectedSize > 0 && f.Size != expectedSize)
                return false;

            string lower512 = NormalizeHex(sha512);
            string lower256 = NormalizeHex(sha256);
            string lower1 = NormalizeHex(sha1);

            if (!string.IsNullOrEmpty(lower512))
                return string.Equals(f.Sha512, lower512, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(lower256))
                return string.Equals(f.Sha256, lower256, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(lower1))
                return string.Equals(f.Sha1, lower1, StringComparison.OrdinalIgnoreCase);

            // No hash provided — size-only check already passed above.
            return true;
        }

        // ------------------------------------------------------------------
        // modrinth.index.json
        // ------------------------------------------------------------------

        private static ModrinthIndex ReadIndex(string packPath)
        {
            using var zip = ZipFile.OpenRead(packPath);
            var entry = zip.GetEntry(IndexEntryName)
                ?? throw new ResourceException("整合包中缺少 modrinth.index.json。");

            using var stream = entry.Open();
            try
            {
                return JsonSerializer.Deserialize<ModrinthIndex>(stream, JsonOptions)
                    ?? throw new ResourceException("modrinth.index.json 内容为空或格式无效。");
            }
            catch (JsonException ex)
            {
                throw new ResourceException("modrinth.index.json 格式无效。", ex);
            }
        }

        // ------------------------------------------------------------------
        // Individual file download + verification
        // ------------------------------------------------------------------

        private async Task DownloadFileAsync(ModrinthFile file, string instanceDir, CancellationToken ct)
        {
            string relativePath = SanitizeRelativePath(file.Path);
            string targetPath = ResolveUnder(instanceDir, relativePath);

            // Skip if already present and valid.
            if (File.Exists(targetPath) && FileMatchesHashes(targetPath, file))
                return;

            string? url = null;
            if (file.Downloads is { Count: > 0 })
                url = file.Downloads[0];

            if (string.IsNullOrWhiteSpace(url))
                throw new ResourceException($"文件 {file.Path} 缺少下载地址。");

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            var downloaded = await DownloadToFileAsync(url, targetPath, progress: null, stage: string.Empty, ct).ConfigureAwait(false);

            if (!DownloadedMatches(file, downloaded))
            {
                TryDelete(targetPath);
                throw new ResourceException($"文件校验失败：{file.Path}（哈希不匹配）。");
            }
        }

        private static bool FileMatchesHashes(string path, ModrinthFile file)
        {
            try
            {
                var info = new FileInfo(path);
                if (file.FileSize is long size && info.Length != size)
                    return false;

                byte[] bytes = File.ReadAllBytes(path);
                return BytesMatchHashes(bytes, file);
            }
            catch
            {
                return false;
            }
        }

        private static bool DownloadedMatches(ModrinthFile file, DownloadedFile downloaded)
        {
            if (file.FileSize is long size && downloaded.Size != size)
                return false;

            string lower512 = NormalizeHex(file.Hashes?.Sha512 ?? string.Empty);
            string lower1 = NormalizeHex(file.Hashes?.Sha1 ?? string.Empty);

            if (!string.IsNullOrEmpty(lower512))
                return string.Equals(downloaded.Sha512, lower512, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(lower1))
                return string.Equals(downloaded.Sha1, lower1, StringComparison.OrdinalIgnoreCase);

            return true;
        }

        private static bool BytesMatchHashes(byte[] bytes, ModrinthFile file)
        {
            if (file.FileSize is long size && bytes.LongLength != size)
                return false;

            string lower512 = NormalizeHex(file.Hashes?.Sha512 ?? string.Empty);
            string lower1 = NormalizeHex(file.Hashes?.Sha1 ?? string.Empty);

            if (!string.IsNullOrEmpty(lower512))
                return string.Equals(ComputeHex(SHA512.HashData(bytes)), lower512, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(lower1))
                return string.Equals(ComputeHex(SHA1.HashData(bytes)), lower1, StringComparison.OrdinalIgnoreCase);

            return true;
        }

        // ------------------------------------------------------------------
        // Streaming download to file (with incremental hashing)
        // ------------------------------------------------------------------

        private sealed class DownloadedFile
        {
            public string Sha1 { get; init; } = string.Empty;
            public string Sha256 { get; init; } = string.Empty;
            public string Sha512 { get; init; } = string.Empty;
            public long Size { get; init; }
        }

        private async Task<DownloadedFile> DownloadToFileAsync(
            string url,
            string targetPath,
            IProgress<PackInstallProgress>? progress,
            string stage,
            CancellationToken ct)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ResourceException("网络错误，无法下载，请检查网络连接。", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new ResourceException("下载超时，请稍后再试。", ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new ResourceException($"下载失败（HTTP {(int)response.StatusCode}）。");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);

                await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

                // Stream to the final target path directly. A partially-written file on
                // failure is removed by the caller (and would fail hash checks anyway).
                await using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    sha1.AppendData(buffer, 0, read);
                    sha256.AppendData(buffer, 0, read);
                    sha512.AppendData(buffer, 0, read);
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    total += read;

                    progress?.Report(new PackInstallProgress
                    {
                        Stage = stage,
                        BytesDownloaded = total,
                        TotalBytes = response.Content.Headers.ContentLength,
                        Message = $"已下载 {FormatUtils.FormatBytes(total)}",
                    });
                }

                return new DownloadedFile
                {
                    Sha1 = ComputeHex(sha1.GetHashAndReset()),
                    Sha256 = ComputeHex(sha256.GetHashAndReset()),
                    Sha512 = ComputeHex(sha512.GetHashAndReset()),
                    Size = total,
                };
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }

        // ------------------------------------------------------------------
        // overrides / client-overrides extraction
        // ------------------------------------------------------------------

        private static void ExtractOverrides(string packPath, string instanceDir, string folder, CancellationToken ct)
        {
            using var zip = ZipFile.OpenRead(packPath);
            string prefix = folder + "/";

            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name))
                    continue; // directory entry

                if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string relative = entry.FullName.Substring(prefix.Length);
                if (string.IsNullOrEmpty(relative))
                    continue;

                string targetPath = ResolveUnder(instanceDir, SanitizeRelativePath(relative));

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                using var src = entry.Open();
                using var dst = File.Create(targetPath);
                src.CopyTo(dst);
            }
        }

        // ------------------------------------------------------------------
        // Path safety
        // ------------------------------------------------------------------

        /// <summary>
        /// Picks a collision-free install directory name under <paramref name="root"/>. Reuses the
        /// recorded path (or the id) when it is free or already belongs to this instance; otherwise
        /// appends <c>-2</c>, <c>-3</c>, … The returned suffix is 0 when no renaming was needed.
        /// </summary>
        private static (string Path, int Suffix) PickInstallPath(string root, string id, string recordedPath)
        {
            string candidate = SafeInstanceName(recordedPath) ?? id;

            if (!Directory.Exists(Path.Combine(root, candidate)) || IsOurInstance(Path.Combine(root, candidate), id))
                return (candidate, 0);

            string baseName = candidate;
            int n = 2;
            while (true)
            {
                string alt = baseName + "-" + n;
                string altDir = Path.Combine(root, alt);
                if (!Directory.Exists(altDir) || IsOurInstance(altDir, id))
                    return (alt, n);
                n++;
            }
        }

        /// <summary>True when <paramref name="dir"/> contains an instance.json whose id matches <paramref name="id"/>.</summary>
        private static bool IsOurInstance(string dir, string id)
        {
            try
            {
                string metaPath = Path.Combine(dir, "instance.json");
                if (!File.Exists(metaPath))
                    return false;

                var meta = JsonSerializer.Deserialize<InstanceMetadata>(File.ReadAllText(metaPath), JsonOptions);
                return string.Equals(meta?.Id, id, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Reduces a recorded install path to a safe single folder name, or null if unusable.</summary>
        private static string? SafeInstanceName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            string n = name.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

            if (Path.IsPathRooted(n)
                || n.IndexOf(Path.DirectorySeparatorChar) >= 0
                || n.IndexOf(Path.AltDirectorySeparatorChar) >= 0
                || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || n is "." or "..")
            {
                return null;
            }

            return n;
        }

        private static string SanitizeRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ResourceException("路径为空，无法安装。");

            // Normalize separators.
            string normalized = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

            if (Path.IsPathRooted(normalized))
                throw new ResourceException($"检测到非法路径：{path}");

            // Reject any traversal segment.
            string[] segments = normalized.Split(Path.DirectorySeparatorChar);
            foreach (string segment in segments)
            {
                if (segment == ".." || segment == ".")
                    throw new ResourceException($"检测到路径越界：{path}");
                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new ResourceException($"检测到非法路径字符：{path}");
            }

            return normalized;
        }

        private static string ResolveUnder(string baseDir, string relativePath)
        {
            string fullBase = Path.GetFullPath(baseDir);
            string fullTarget = Path.GetFullPath(Path.Combine(fullBase, relativePath));

            string baseWithSep = fullBase.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? fullBase
                : fullBase + Path.DirectorySeparatorChar;

            if (!fullTarget.StartsWith(baseWithSep, StringComparison.OrdinalIgnoreCase))
                throw new ResourceException($"目标路径超出实例目录：{relativePath}");

            return fullTarget;
        }

        // ------------------------------------------------------------------
        // Hash helpers
        // ------------------------------------------------------------------

        private static string NormalizeHex(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
                return string.Empty;
            return hex.Trim().ToLowerInvariant();
        }

        private static string ComputeHex(byte[] hash)
        {
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public void Dispose() => _http.Dispose();
    }
}
