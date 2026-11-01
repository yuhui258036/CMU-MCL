using DownloadedFile = CMUmcl.Content.DownloadedContent;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
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
    /// Prefers an incremental update via the per-revision file index (files_url/files_sha256)
    /// and falls back to a full pack install whenever that is unsafe or unavailable.
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

            var handler = CMUmcl.Network.LauncherProxy.CreateHandler();

            _http = CreateClient(handler);
        }

        /// <summary>Test seam: uses a caller-supplied handler (e.g. a fake local server).</summary>
        internal PackInstaller(InstalledStateStore installedState, HttpMessageHandler handler)
        {
            _installedState = installedState;
            _http = CreateClient(handler);
        }

        private static HttpClient CreateClient(HttpMessageHandler handler)
        {
            // Downloads (packs + individual files) may be large; use a generous timeout.
            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(30),
                // Only the JSON file index uses buffered HTTP reads. Pack/mod
                // bodies stream separately and keep their own size limits.
                MaxResponseContentBufferSize = 32 * 1024 * 1024,
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return client;
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

            // Resolve (and create) the instance directory under the configured instances root.
            // Reuse the recorded install path when it is ours; otherwise pick a collision-free name.
            string instancesRoot = LauncherPaths.InstancesDirectory;
            Directory.CreateDirectory(instancesRoot);

            string recordedPath = _installedState.GetInstallPath(instance.Id);
            string? existingDisplayName = _installedState.GetDisplayName(instance.Id);
            (string installPath, int suffix) = PickInstallPath(instancesRoot, instance.Id, recordedPath);
            string instanceDir = Path.Combine(instancesRoot, installPath);
            Directory.CreateDirectory(instanceDir);

            // Try the incremental path when the manifest advertises a file index.
            if (HasFileIndex(instance))
            {
                bool incrementalDone = await TryInstallIncrementallyAsync(
                    instance, instanceDir, installPath, suffix, existingDisplayName, progress, ct).ConfigureAwait(false);

                if (incrementalDone)
                    return;

                progress?.Report(new PackInstallProgress
                {
                    Stage = "检查增量更新",
                    Message = "无法增量更新，回退到完整安装",
                });
            }

            await InstallFullAsync(instance, instanceDir, installPath, suffix, existingDisplayName, progress, ct).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------
        // Full install path (existing behavior + local state persistence)
        // ------------------------------------------------------------------

        private async Task InstallFullAsync(
            ManifestInstance instance,
            string instanceDir,
            string installPath,
            int suffix,
            string? existingDisplayName,
            IProgress<PackInstallProgress>? progress,
            CancellationToken ct)
        {
            // 1. Download the .mrpack into the pack cache and verify it.
            string packCacheDir = Path.Combine(LauncherPaths.PackCacheDirectory, instance.Id);
            string packPath = Path.Combine(packCacheDir, $"{instance.Id}-{instance.Revision}.mrpack");

            DownloadedFile pack = await DownloadAndVerifyPackAsync(instance, packPath, progress, ct).ConfigureAwait(false);

            // 2. Read modrinth.index.json from the archive.
            ModrinthIndex index = ReadIndex(packPath);

            // 3. Download the listed files.
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

            // 4. Extract overrides/ then client-overrides/ into the instance dir.
            progress?.Report(new PackInstallProgress { Stage = "解压覆盖", Message = "正在应用 overrides" });
            ExtractOverrides(packPath, instanceDir, "overrides", ct);
            progress?.Report(new PackInstallProgress { Stage = "解压覆盖", Message = "正在应用 client-overrides" });
            ExtractOverrides(packPath, instanceDir, "client-overrides", ct);

            // 5. Compute the override hash (matching the server) and persist local state.
            string overridesSha256 = OverridesHash.Compute(packPath);
            string packSha256 = PackFileSystem.NormalizeHex(pack.Sha256);
            PackStateStore.Save(instanceDir, BuildStateFromModrinthIndex(index, instance.Revision, packSha256, overridesSha256));

            // 6. Write instance.json metadata.
            await WriteInstanceMetadataAsync(instance, instanceDir, packSha256, ct).ConfigureAwait(false);

            // 7. Record the installed revision, install path and display name.
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
        // Incremental update path
        // ------------------------------------------------------------------

        private static bool HasFileIndex(ManifestInstance instance)
            => !string.IsNullOrWhiteSpace(instance.Pack?.FilesUrl)
               && !string.IsNullOrWhiteSpace(instance.Pack?.FilesSha256);

        /// <summary>
        /// Attempts an incremental update. Returns true when the update was fully applied;
        /// false means the caller should fall back to a full install.
        /// </summary>
        private async Task<bool> TryInstallIncrementallyAsync(
            ManifestInstance instance,
            string instanceDir,
            string installPath,
            int suffix,
            string? existingDisplayName,
            IProgress<PackInstallProgress>? progress,
            CancellationToken ct)
        {
            // 1. Local state must exist.
            PackInstallState? state = PackStateStore.Load(instanceDir);
            if (state is null)
                return false;

            // 2. The local state must correspond to the currently installed pack.
            string installedPackSha256 = ReadInstalledPackSha256(instanceDir);
            if (!string.IsNullOrEmpty(installedPackSha256)
                && !string.Equals(
                    PackFileSystem.NormalizeHex(state.PackSha256),
                    PackFileSystem.NormalizeHex(installedPackSha256),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 3. Download and verify the file index.
            progress?.Report(new PackInstallProgress { Stage = "检查增量更新", Message = "正在获取文件索引" });

            PackFileIndex index;
            try
            {
                index = await DownloadAndVerifyIndexAsync(instance, ct).ConfigureAwait(false);
            }
            catch (ResourceException)
            {
                return false; // index unavailable/bad hash -> full install
            }

            // 4. Index must describe this manifest revision and pack.
            if (index.Revision != instance.Revision)
                return false;

            string manifestPackSha256 = PackFileSystem.NormalizeHex(instance.Pack?.Sha256);
            if (!string.IsNullOrEmpty(manifestPackSha256)
                && !string.IsNullOrEmpty(index.PackSha256)
                && !string.Equals(
                    PackFileSystem.NormalizeHex(index.PackSha256),
                    manifestPackSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 5. Overrides must be unchanged from the installed state; otherwise fall back
            //    to a full install so config/override edits are re-applied.
            if (!string.Equals(
                PackFileSystem.NormalizeHex(index.OverridesSha256),
                PackFileSystem.NormalizeHex(state.OverridesSha256),
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 6. Plan the update.
            IncrementalPlan plan = PackIncrementalPlanner.Plan(state, index, instanceDir);
            if (plan.NeedFullInstall)
                return false;

            // 7. Apply it.
            return await ApplyIncrementalAsync(
                instance, instanceDir, installPath, suffix, existingDisplayName, index, plan, progress, ct).ConfigureAwait(false);
        }

        private async Task<PackFileIndex> DownloadAndVerifyIndexAsync(ManifestInstance instance, CancellationToken ct)
        {
            string url = instance.Pack?.FilesUrl ?? string.Empty;
            if (!PackFileIndexValidation.IsAllowedFilesUrl(url))
                throw new ResourceException("文件索引地址无效。");

            string expectedSha256 = PackFileSystem.NormalizeHex(instance.Pack?.FilesSha256);
            if (expectedSha256.Length == 0)
                throw new ResourceException("文件索引缺少校验值。");

            byte[] bytes;
            try
            {
                using var indexTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                indexTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                bytes = await _http.GetByteArrayAsync(url, indexTimeout.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ResourceException("网络错误，无法下载文件索引。", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new ResourceException("下载文件索引超时。", ex);
            }

            string actualSha256 = PackFileSystem.ComputeHex(SHA256.HashData(bytes));
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new ResourceException("文件索引校验失败（哈希不匹配）。");

            if (!PackFileIndexValidation.TryParse(bytes, out PackFileIndex? index, out string? error))
                throw new ResourceException("文件索引格式无效：" + (error ?? string.Empty));

            // Reject unsafe paths up front (defense in depth; the planner re-checks too).
            foreach (PackFileIndexEntry entry in index!.Files)
            {
                if (!PackFileIndexValidation.IsSafeRelativePath(entry.Path))
                    throw new ResourceException($"文件索引包含非法路径：{entry.Path}");
            }

            return index;
        }

        /// <summary>
        /// Downloads changed files into a staging directory, verifies them, then moves them
        /// into place and deletes removed files. Returns false (and cleans up) on any failure
        /// so the caller can fall back to a full install as repair.
        /// </summary>
        private async Task<bool> ApplyIncrementalAsync(
            ManifestInstance instance,
            string instanceDir,
            string installPath,
            int suffix,
            string? existingDisplayName,
            PackFileIndex index,
            IncrementalPlan plan,
            IProgress<PackInstallProgress>? progress,
            CancellationToken ct)
        {
            // Staging lives inside the instance directory (same volume => atomic-ish moves).
            string stagingDir = Path.Combine(instanceDir, ".cmumcl-staging-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(stagingDir);

                // 1. Download changed files into staging, verifying each.
                for (int i = 0; i < plan.Download.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    PackFileIndexEntry entry = plan.Download[i];
                    progress?.Report(new PackInstallProgress
                    {
                        Stage = "下载变化文件",
                        CurrentFile = i,
                        TotalFiles = plan.Download.Count,
                        Message = entry.Path,
                    });

                    string relPath = PackFileSystem.NormalizeRelativePath(entry.Path);
                    string stagingTarget = PackFileSystem.ResolveUnder(stagingDir, PackFileSystem.SanitizeRelativePath(relPath));

                    Directory.CreateDirectory(Path.GetDirectoryName(stagingTarget)!);
                    DownloadedFile downloaded = await ResumableDownload.DownloadMirrorsAsync(_http, entry.Downloads,
                        stagingTarget, new DownloadExpectation(entry.FileSize, entry.Hashes?.Sha512, Sha1: entry.Hashes?.Sha1), ct).ConfigureAwait(false);

                    if (!IndexEntryMatches(entry, downloaded))
                        throw new ResourceException($"文件校验失败：{entry.Path}（哈希不匹配）。");
                }

                // 2. Apply: move staged files into place, then delete removed managed files.
                progress?.Report(new PackInstallProgress { Stage = "应用增量更新", Message = "正在应用变化文件" });

                foreach (PackFileIndexEntry entry in plan.Download)
                {
                    string relPath = PackFileSystem.NormalizeRelativePath(entry.Path);
                    string stagingTarget = PackFileSystem.ResolveUnder(stagingDir, PackFileSystem.SanitizeRelativePath(relPath));
                    string finalTarget = PackFileSystem.ResolveUnder(instanceDir, PackFileSystem.SanitizeRelativePath(relPath));

                    Directory.CreateDirectory(Path.GetDirectoryName(finalTarget)!);
                    File.Move(stagingTarget, finalTarget, overwrite: true);
                }

                foreach (string relPath in plan.Delete)
                {
                    string target = PackFileSystem.ResolveUnder(instanceDir, PackFileSystem.SanitizeRelativePath(relPath));
                    TryDelete(target);
                }

                // 3. Persist the new state, instance metadata and installed revision.
                string newPackSha256 = !string.IsNullOrWhiteSpace(index.PackSha256)
                    ? PackFileSystem.NormalizeHex(index.PackSha256)
                    : PackFileSystem.NormalizeHex(instance.Pack?.Sha256);

                PackStateStore.Save(instanceDir, BuildStateFromIndexEntries(index.Files, instance.Revision, newPackSha256, index.OverridesSha256));
                await WriteInstanceMetadataAsync(instance, instanceDir, newPackSha256, ct).ConfigureAwait(false);

                string? displayName = suffix > 0 ? $"{instance.Name} ({suffix})" : existingDisplayName;
                _installedState.RecordInstall(instance.Id, instance.Revision, installPath, displayName);

                progress?.Report(new PackInstallProgress
                {
                    Stage = "完成",
                    CurrentFile = plan.Download.Count,
                    TotalFiles = plan.Download.Count,
                    Message = $"{instance.Name} 更新完成（增量 {plan.Download.Count} 个文件）",
                });

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Apply failed after downloads may have started; fall back to a full install.
                return false;
            }
            finally
            {
                TryDeleteDirectory(stagingDir);
            }
        }

        private static bool IndexEntryMatches(PackFileIndexEntry entry, DownloadedFile downloaded)
        {
            if (entry.FileSize > 0 && downloaded.Size != entry.FileSize)
                return false;

            string sha512 = PackFileSystem.NormalizeHex(entry.Hashes?.Sha512);
            string sha1 = PackFileSystem.NormalizeHex(entry.Hashes?.Sha1);

            if (sha512.Length > 0)
                return string.Equals(downloaded.Sha512, sha512, StringComparison.OrdinalIgnoreCase);
            if (sha1.Length > 0)
                return string.Equals(downloaded.Sha1, sha1, StringComparison.OrdinalIgnoreCase);

            return false; // No hash to verify against (planner prevents this for downloads).
        }

        // ------------------------------------------------------------------
        // Local state / metadata helpers
        // ------------------------------------------------------------------

        private static PackInstallState BuildStateFromModrinthIndex(
            ModrinthIndex index, int revision, string packSha256, string overridesSha256)
        {
            var state = new PackInstallState
            {
                FormatVersion = 1,
                Revision = revision,
                PackSha256 = PackFileSystem.NormalizeHex(packSha256),
                OverridesSha256 = PackFileSystem.NormalizeHex(overridesSha256),
            };

            foreach (ModrinthFile f in index.Files ?? new List<ModrinthFile>())
            {
                if (!PackFileIndexValidation.IsSafeRelativePath(f.Path))
                    continue; // Defensive; unsafe paths never enter state.

                state.Files.Add(new PackInstallStateFile
                {
                    Path = PackFileSystem.NormalizeRelativePath(f.Path),
                    Sha1 = PackFileSystem.NormalizeHex(f.Hashes?.Sha1),
                    Sha512 = PackFileSystem.NormalizeHex(f.Hashes?.Sha512),
                    FileSize = f.FileSize ?? 0,
                });
            }

            return state;
        }

        private static PackInstallState BuildStateFromIndexEntries(
            List<PackFileIndexEntry> entries, int revision, string packSha256, string overridesSha256)
        {
            var state = new PackInstallState
            {
                FormatVersion = 1,
                Revision = revision,
                PackSha256 = PackFileSystem.NormalizeHex(packSha256),
                OverridesSha256 = PackFileSystem.NormalizeHex(overridesSha256),
            };

            foreach (PackFileIndexEntry f in entries)
            {
                if (!PackFileIndexValidation.IsSafeRelativePath(f.Path))
                    continue;

                state.Files.Add(new PackInstallStateFile
                {
                    Path = PackFileSystem.NormalizeRelativePath(f.Path),
                    Sha1 = PackFileSystem.NormalizeHex(f.Hashes?.Sha1),
                    Sha512 = PackFileSystem.NormalizeHex(f.Hashes?.Sha512),
                    FileSize = f.FileSize,
                });
            }

            return state;
        }

        private static string ReadInstalledPackSha256(string instanceDir)
        {
            try
            {
                string metaPath = Path.Combine(instanceDir, "instance.json");
                if (!File.Exists(metaPath))
                    return string.Empty;

                var meta = JsonSerializer.Deserialize<InstanceMetadata>(File.ReadAllText(metaPath), JsonOptions);
                return meta?.PackSha256 ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static async Task WriteInstanceMetadataAsync(
            ManifestInstance instance, string instanceDir, string packSha256, CancellationToken ct)
        {
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
                PackSha256 = PackFileSystem.NormalizeHex(packSha256),
                InstalledAt = DateTimeOffset.UtcNow,
            };

            await File.WriteAllTextAsync(
                Path.Combine(instanceDir, "instance.json"),
                JsonSerializer.Serialize(metadata, JsonOptions),
                ct).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------
        // Pack download + verification
        // ------------------------------------------------------------------

        private async Task<DownloadedFile> DownloadAndVerifyPackAsync(
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

            var downloaded = await DownloadToFileAsync(url, packPath, progress, "下载整合包", ct, new DownloadExpectation(expectedSize > 0 ? expectedSize : null, expectedSha512, expectedSha256, expectedSha1)).ConfigureAwait(false);

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

            return downloaded;
        }

        private static bool VerifyPack(DownloadedFile f, long expectedSize, string sha512, string sha256, string sha1)
        {
            if (expectedSize > 0 && f.Size != expectedSize)
                return false;

            string lower512 = PackFileSystem.NormalizeHex(sha512);
            string lower256 = PackFileSystem.NormalizeHex(sha256);
            string lower1 = PackFileSystem.NormalizeHex(sha1);

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
            string relativePath = PackFileSystem.SanitizeRelativePath(file.Path);
            string targetPath = PackFileSystem.ResolveUnder(instanceDir, relativePath);

            // Skip if already present and valid.
            if (File.Exists(targetPath) && FileMatchesHashes(targetPath, file))
                return;

            if (file.Downloads is not { Count: > 0 })
                throw new ResourceException($"文件 {file.Path} 缺少下载地址。");

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            DownloadedFile downloaded;
            try
            {
                downloaded = await ResumableDownload.DownloadMirrorsAsync(_http, file.Downloads, targetPath,
                    new DownloadExpectation(file.FileSize, file.Hashes?.Sha512, Sha1: file.Hashes?.Sha1), ct).ConfigureAwait(false);
            }
            catch (IOException ex) { throw new ResourceException($"文件下载失败：{file.Path}，已保留可用断点。", ex); }

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

                // Hash large mods/resource packs as a stream instead of allocating
                // one array the size of every existing file during update checks.
                using var stream = File.OpenRead(path);
                var sha512 = PackFileSystem.NormalizeHex(file.Hashes?.Sha512);
                var sha1 = PackFileSystem.NormalizeHex(file.Hashes?.Sha1);
                if (!string.IsNullOrEmpty(sha512))
                    return string.Equals(PackFileSystem.ComputeHex(SHA512.HashData(stream)), sha512, StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(sha1))
                    return string.Equals(PackFileSystem.ComputeHex(SHA1.HashData(stream)), sha1, StringComparison.OrdinalIgnoreCase);
                return true;
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

            string lower512 = PackFileSystem.NormalizeHex(file.Hashes?.Sha512);
            string lower1 = PackFileSystem.NormalizeHex(file.Hashes?.Sha1);

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

            string lower512 = PackFileSystem.NormalizeHex(file.Hashes?.Sha512);
            string lower1 = PackFileSystem.NormalizeHex(file.Hashes?.Sha1);

            if (!string.IsNullOrEmpty(lower512))
                return string.Equals(PackFileSystem.ComputeHex(SHA512.HashData(bytes)), lower512, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(lower1))
                return string.Equals(PackFileSystem.ComputeHex(SHA1.HashData(bytes)), lower1, StringComparison.OrdinalIgnoreCase);

            return true;
        }

        // ------------------------------------------------------------------
        // Streaming download to file (with incremental hashing)
        // ------------------------------------------------------------------

        private async Task<DownloadedFile> DownloadToFileAsync(
            string url, string targetPath, IProgress<PackInstallProgress>? progress,
            string stage, CancellationToken ct, DownloadExpectation expected)
        {
            try
            {
                return await ResumableDownload.DownloadAsync(_http, url, targetPath, expected,
                    (received, total) => progress?.Report(new PackInstallProgress
                    {
                        Stage = stage, BytesDownloaded = received, TotalBytes = total,
                        Message = $"已下载 {FormatUtils.FormatBytes(received)}",
                    }), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
            {
                throw new ResourceException("下载或校验失败，已保留有效断点，可稍后重试。", ex);
            }
        }
        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
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

                string targetPath = PackFileSystem.ResolveUnder(instanceDir, PackFileSystem.SanitizeRelativePath(relative));

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                using var src = entry.Open();
                using var dst = File.Create(targetPath);
                src.CopyTo(dst);
            }
        }

        // ------------------------------------------------------------------
        // Path safety (install directory naming)
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

        public void Dispose() => _http.Dispose();
    }
}
