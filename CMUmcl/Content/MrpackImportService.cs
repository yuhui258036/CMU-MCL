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
    /// Imports a local Modrinth <c>.mrpack</c> (a ZIP archive containing a root
    /// <c>modrinth.index.json</c>) into a new local instance. Parses the index to infer the
    /// Minecraft game version and loader, downloads and verifies each listed file, applies the
    /// archive's <c>overrides/</c> and <c>client-overrides/</c> trees, writes a fresh
    /// <c>instance.json</c>, and records the install. The source archive is never modified; a
    /// partially written target is deleted on failure.
    /// </summary>
    public sealed class MrpackImportService : IDisposable
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
        private readonly string _instancesRoot;

        public MrpackImportService(InstalledStateStore installedState, string? instancesRoot = null)
        {
            _installedState = installedState ?? throw new ArgumentNullException(nameof(installedState));
            _instancesRoot = NormalizeInstancesRoot(instancesRoot);

            var handler = CMUmcl.Network.LauncherProxy.CreateHandler();

            _http = CreateClient(handler);
        }

        /// <summary>Test seam: uses a caller-supplied handler (e.g. a fake local server).</summary>
        internal MrpackImportService(InstalledStateStore installedState, string? instancesRoot, HttpMessageHandler handler)
        {
            _installedState = installedState ?? throw new ArgumentNullException(nameof(installedState));
            _instancesRoot = NormalizeInstancesRoot(instancesRoot);
            _http = CreateClient(handler);
        }

        private static HttpClient CreateClient(HttpMessageHandler handler)
        {
            var client = new HttpClient(handler)
            {
                // Individual mods can be large; use a generous timeout.
                Timeout = TimeSpan.FromMinutes(30),
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return client;
        }

        public void Dispose() => _http.Dispose();

        /// <summary>
        /// Imports <paramref name="sourceFile"/> (a <c>.mrpack</c>/ZIP) as a new local instance.
        /// <paramref name="displayName"/> overrides the archive name when non-empty. Throws
        /// <see cref="InstanceImportException"/> on invalid input, missing game version, or any
        /// download/copy/write failure (after deleting the partial target). The source is untouched.
        /// </summary>
        public async Task<InstanceImportResult> ImportAsync(
            string sourceFile,
            string? displayName = null,
            IProgress<ImportProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sourceFile))
                throw new InstanceImportException("请选择要导入的 .mrpack 整合包文件。");

            string source = NormalizePath(sourceFile);
            if (!File.Exists(source))
                throw new InstanceImportException($"所选文件不存在：{sourceFile}");

            // Read + validate the index up front, before any target is created.
            ParsedIndex parsed = ReadIndex(source);

            (string game, string loader, string loaderVersion) = InferLoader(parsed);

            string baseDisplay = ResolveDisplayName(displayName, parsed.Index.Name, source);
            string baseFolder = LocalInstanceNaming.SanitizeFolderName(baseDisplay);

            Directory.CreateDirectory(_instancesRoot);
            var taken = CollectTakenNames();
            (string folder, string resolvedDisplay) = LocalInstanceNaming.ResolveUniqueName(baseFolder, baseDisplay, taken);

            string id = "local:" + Guid.NewGuid().ToString("N");
            string target = Path.Combine(_instancesRoot, folder);

            try
            {
                Directory.CreateDirectory(target);

                // Apply overrides/ then client-overrides/ (path-traversal protected).
                progress?.Report(new ImportProgress { Message = "正在解压 overrides…" });
                ExtractOverrides(source, target, "overrides", ct);
                progress?.Report(new ImportProgress { Message = "正在解压 client-overrides…" });
                ExtractOverrides(source, target, "client-overrides", ct);

                // Download and verify each listed file.
                var files = parsed.Index.Files ?? new List<ModrinthFile>();
                for (int i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    await DownloadFileAsync(files[i], i, files.Count, target, progress, ct).ConfigureAwait(false);
                }

                var metadata = new InstanceMetadata
                {
                    Id = id,
                    Name = resolvedDisplay,
                    Revision = 1,
                    Game = game,
                    Loader = loader,
                    LoaderVersion = loaderVersion,
                    InstalledAt = DateTimeOffset.UtcNow,
                };

                progress?.Report(new ImportProgress { Message = "正在写入实例信息…" });
                string metaPath = Path.Combine(target, "instance.json");
                await Task.Run(() => File.WriteAllText(metaPath, JsonSerializer.Serialize(metadata, JsonOptions)), ct).ConfigureAwait(false);

                _installedState.RecordInstall(id, 1, folder, resolvedDisplay);

                return new InstanceImportResult
                {
                    Id = id,
                    InstallPath = folder,
                    DisplayName = resolvedDisplay,
                    InstanceDirectory = target,
                    Game = game,
                    Loader = loader,
                    LoaderVersion = loaderVersion,
                };
            }
            catch (OperationCanceledException)
            {
                TryDeleteDirectory(target);
                throw;
            }
            catch (InstanceImportException)
            {
                TryDeleteDirectory(target);
                throw;
            }
            catch (Exception ex)
            {
                TryDeleteDirectory(target);
                throw new InstanceImportException("导入整合包失败：" + ex.Message, ex);
            }
        }

        // ------------------------------------------------------------------
        // Index reading / inference
        // ------------------------------------------------------------------

        /// <summary>A parsed index plus its raw dependencies map (object or array form).</summary>
        private sealed class ParsedIndex
        {
            public ModrinthIndex Index { get; init; } = new();
            public Dictionary<string, string> Dependencies { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private static ParsedIndex ReadIndex(string sourceFile)
        {
            byte[] bytes;
            try
            {
                using var zip = ZipFile.OpenRead(sourceFile);
                var entry = zip.GetEntry(IndexEntryName);
                if (entry == null)
                    throw new InstanceImportException("整合包中缺少 modrinth.index.json。");

                using var stream = entry.Open();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                bytes = ms.ToArray();
            }
            catch (InstanceImportException)
            {
                throw;
            }
            catch (InvalidDataException)
            {
                throw new InstanceImportException("所选文件不是有效的 .mrpack 整合包（无法作为 ZIP 打开）。");
            }
            catch (Exception ex)
            {
                throw new InstanceImportException("无法读取整合包内容：" + ex.Message, ex);
            }

            ModrinthIndex index;
            try
            {
                index = JsonSerializer.Deserialize<ModrinthIndex>(bytes, JsonOptions) ?? new ModrinthIndex();
            }
            catch (JsonException ex)
            {
                throw new InstanceImportException("modrinth.index.json 格式无效。", ex);
            }

            return new ParsedIndex { Index = index, Dependencies = ReadDependencies(bytes) };
        }

        private static Dictionary<string, string> ReadDependencies(byte[] json)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("dependencies", out JsonElement deps))
                    return result;

                if (deps.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty prop in deps.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                            result[prop.Name] = prop.Value.GetString() ?? string.Empty;
                    }
                }
                else if (deps.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in deps.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                            continue;

                        string? id = null;
                        string version = string.Empty;
                        if (item.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String)
                            id = idEl.GetString();
                        if (item.TryGetProperty("version", out JsonElement vEl) && vEl.ValueKind == JsonValueKind.String)
                            version = vEl.GetString() ?? string.Empty;

                        if (!string.IsNullOrWhiteSpace(id))
                            result[id!] = version;
                    }
                }
            }
            catch
            {
                // Dependencies are best-effort; the game/loader inference reports its own error.
            }

            return result;
        }

        private static (string Game, string Loader, string LoaderVersion) InferLoader(ParsedIndex parsed)
        {
            ModrinthIndex index = parsed.Index;
            IReadOnlyDictionary<string, string> deps = parsed.Dependencies;

            string? game = null;
            if (IsReleaseLike(index.VersionId))
                game = index.VersionId;
            if (game == null && deps.TryGetValue("minecraft", out string? mc) && IsReleaseLike(mc))
                game = mc;

            if (string.IsNullOrWhiteSpace(game))
                throw new InstanceImportException("无法从整合包推断 Minecraft 游戏版本（缺少 versionId 或 dependencies.minecraft）。");

            string loader = "vanilla";
            string loaderVersion = string.Empty;

            (string Key, string Loader)[] order =
            {
                ("fabric-loader", "fabric"),
                ("quilt-loader", "quilt"),
                ("forge", "forge"),
                ("neoforge", "neoforge"),
            };

            foreach ((string key, string loaderValue) in order)
            {
                if (deps.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v))
                {
                    loader = loaderValue;
                    loaderVersion = v.Trim();
                    break;
                }
            }

            return (game, loader, loaderVersion);
        }

        private static bool IsReleaseLike(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return false;

            bool sawDigit = false;
            bool sawDot = false;
            foreach (char c in s)
            {
                if (char.IsDigit(c))
                {
                    sawDigit = true;
                }
                else if (c == '.')
                {
                    sawDot = true;
                }
                else
                {
                    return false;
                }
            }

            return sawDigit && sawDot;
        }

        // ------------------------------------------------------------------
        // overrides extraction (path-traversal protected)
        // ------------------------------------------------------------------

        private static void ExtractOverrides(string sourceFile, string target, string folder, CancellationToken ct)
        {
            using var zip = ZipFile.OpenRead(sourceFile);
            string prefix = folder + "/";

            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name))
                    continue; // directory entry

                if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string relative = entry.FullName.Substring(prefix.Length);
                if (string.IsNullOrEmpty(relative))
                    continue;

                string targetPath = PackFileSystem.ResolveUnder(target, PackFileSystem.SanitizeRelativePath(relative));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                using var src = entry.Open();
                using var dst = File.Create(targetPath);
                src.CopyTo(dst);
            }
        }

        // ------------------------------------------------------------------
        // Individual file download + verification
        // ------------------------------------------------------------------

        private async Task DownloadFileAsync(
            ModrinthFile file,
            int index,
            int total,
            string targetDir,
            IProgress<ImportProgress>? progress,
            CancellationToken ct)
        {
            string targetPath = PackFileSystem.ResolveUnder(targetDir, PackFileSystem.SanitizeRelativePath(file.Path));

            // Skip when a valid file is already present (e.g. placed by overrides).
            if (File.Exists(targetPath) && FileMatches(file, targetPath))
                return;

            string? url = SelectDownloadUrl(file.Downloads);
            if (url == null)
                throw new InstanceImportException($"文件 {file.Path} 缺少下载地址，无法导入。");

            progress?.Report(new ImportProgress { Message = $"正在下载文件（{index + 1}/{total}）：{file.Path}" });
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            DownloadedFile downloaded = await DownloadToFileAsync(url, targetPath, file.Path, progress, ct).ConfigureAwait(false);

            if (!DownloadedMatches(file, downloaded))
            {
                TryDeleteFile(targetPath);
                throw new InstanceImportException($"文件校验失败：{file.Path}（哈希或大小不匹配）。");
            }
        }

        private static string? SelectDownloadUrl(List<string>? downloads)
        {
            if (downloads == null || downloads.Count == 0)
                return null;

            foreach (string candidate in downloads)
            {
                if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)
                    && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            // No HTTPS URL advertised; fall back to the first URL rather than fail a valid file.
            return downloads[0];
        }

        private static bool FileMatches(ModrinthFile file, string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (file.FileSize is long size && info.Length != size)
                    return false;

                using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
                var buffer = new byte[81920];
                int read;
                while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha1.AppendData(buffer, 0, read);
                    sha512.AppendData(buffer, 0, read);
                }

                return BytesMatchHashes(file, sha1.GetHashAndReset(), sha512.GetHashAndReset());
            }
            catch
            {
                return false;
            }
        }

        private static bool BytesMatchHashes(ModrinthFile file, byte[] sha1, byte[] sha512)
        {
            string lower512 = PackFileSystem.NormalizeHex(file.Hashes?.Sha512);
            string lower1 = PackFileSystem.NormalizeHex(file.Hashes?.Sha1);

            if (lower512.Length > 0)
                return string.Equals(PackFileSystem.ComputeHex(sha512), lower512, StringComparison.OrdinalIgnoreCase);
            if (lower1.Length > 0)
                return string.Equals(PackFileSystem.ComputeHex(sha1), lower1, StringComparison.OrdinalIgnoreCase);

            return true;
        }

        private static bool DownloadedMatches(ModrinthFile file, DownloadedFile downloaded)
        {
            if (file.FileSize is long size && downloaded.Size != size)
                return false;

            string lower512 = PackFileSystem.NormalizeHex(file.Hashes?.Sha512);
            string lower1 = PackFileSystem.NormalizeHex(file.Hashes?.Sha1);

            if (lower512.Length > 0)
                return string.Equals(downloaded.Sha512, lower512, StringComparison.OrdinalIgnoreCase);
            if (lower1.Length > 0)
                return string.Equals(downloaded.Sha1, lower1, StringComparison.OrdinalIgnoreCase);

            return true;
        }

        private sealed class DownloadedFile
        {
            public string Sha1 { get; init; } = string.Empty;
            public string Sha512 { get; init; } = string.Empty;
            public long Size { get; init; }
        }

        private async Task<DownloadedFile> DownloadToFileAsync(
            string url,
            string targetPath,
            string label,
            IProgress<ImportProgress>? progress,
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
                throw new InstanceImportException("网络错误，无法下载文件，请检查网络连接。", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new InstanceImportException("下载超时，请稍后再试。", ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw new InstanceImportException($"下载失败（HTTP {(int)response.StatusCode}）。");

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);

                await using var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                long contentLength = response.Content.Headers.ContentLength ?? 0;
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    sha1.AppendData(buffer, 0, read);
                    sha512.AppendData(buffer, 0, read);
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    total += read;

                    progress?.Report(new ImportProgress
                    {
                        Message = $"正在下载 {label}：{FormatUtils.FormatBytes(total)}"
                            + (contentLength > 0 ? $" / {FormatUtils.FormatBytes(contentLength)}" : string.Empty),
                        BytesCopied = total,
                        TotalBytes = contentLength,
                    });
                }

                return new DownloadedFile
                {
                    Sha1 = PackFileSystem.ComputeHex(sha1.GetHashAndReset()),
                    Sha512 = PackFileSystem.ComputeHex(sha512.GetHashAndReset()),
                    Size = total,
                };
            }
        }

        // ------------------------------------------------------------------
        // Naming helpers
        // ------------------------------------------------------------------

        private static string NormalizeInstancesRoot(string? instancesRoot)
        {
            if (string.IsNullOrWhiteSpace(instancesRoot))
                return LauncherPaths.InstancesDirectory;

            return Path.GetFullPath(instancesRoot.Trim());
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path.Trim());
            }
            catch (Exception ex)
            {
                throw new InstanceImportException($"文件路径无效：{path}", ex);
            }
        }

        private static string ResolveDisplayName(string? displayName, string? inferredName, string source)
        {
            string explicitName = (displayName ?? string.Empty).Trim();
            if (explicitName.Length > 0)
                return explicitName;

            string name = (inferredName ?? string.Empty).Trim();
            if (name.Length > 0)
                return name;

            string stem = Path.GetFileNameWithoutExtension(source);
            return string.IsNullOrWhiteSpace(stem) ? "导入整合包" : stem;
        }

        private List<string> CollectTakenNames()
        {
            var taken = new List<string>();
            foreach (string dir in EnumerateDirectoriesSafely(_instancesRoot))
                taken.Add(Path.GetFileName(dir));

            foreach (var kv in _installedState.ListRecords())
            {
                string path = string.IsNullOrWhiteSpace(kv.Value.InstallPath) ? kv.Key : kv.Value.InstallPath!;
                if (!string.IsNullOrWhiteSpace(path))
                    taken.Add(path);
            }

            return taken;
        }

        private static IEnumerable<string> EnumerateDirectoriesSafely(string dir)
        {
            try
            {
                return Directory.EnumerateDirectories(dir);
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static void TryDeleteFile(string path)
        {
            try { File.Delete(path); } catch { /* best effort */ }
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
                // Best effort — never affects the source archive.
            }
        }
    }
}
