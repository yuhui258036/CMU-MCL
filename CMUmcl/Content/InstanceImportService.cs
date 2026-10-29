using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CMUmcl.Content
{
    /// <summary>Raised when an existing instance folder cannot be imported (invalid input or an I/O failure).</summary>
    public sealed class InstanceImportException : Exception
    {
        public InstanceImportException(string message) : base(message) { }

        public InstanceImportException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>Simple progress reported while an instance folder is being copied in.</summary>
    public sealed class ImportProgress
    {
        public string Message { get; set; } = string.Empty;
        public long BytesCopied { get; set; }
        public long TotalBytes { get; set; }
    }

    /// <summary>The result of a successful instance import.</summary>
    public sealed class InstanceImportResult
    {
        public string Id { get; init; } = string.Empty;
        public string InstallPath { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string InstanceDirectory { get; init; } = string.Empty;
        public string Game { get; init; } = string.Empty;
        public string Loader { get; init; } = string.Empty;
        public string LoaderVersion { get; init; } = string.Empty;
    }

    /// <summary>
    /// Imports an existing instance/.minecraft folder by copying it (recursively, preserving all
    /// user data) into a new collision-safe directory under the active instances root, inferring
    /// game/loader metadata where possible, writing a fresh <c>instance.json</c>, and recording the
    /// install. The source folder is never modified; a partially copied target is deleted on failure.
    /// </summary>
    public sealed class InstanceImportService
    {
        private const int CopyBufferSize = 81920;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
        };

        private readonly InstalledStateStore _installedState;
        private readonly string _instancesRoot;

        public InstanceImportService(InstalledStateStore installedState, string? instancesRoot = null)
        {
            _installedState = installedState ?? throw new ArgumentNullException(nameof(installedState));
            _instancesRoot = NormalizeInstancesRoot(instancesRoot);
        }

        /// <summary>
        /// Imports <paramref name="sourceFolder"/> as a new local instance. <paramref name="displayName"/>
        /// overrides the inferred/folder name when non-empty. Throws <see cref="InstanceImportException"/>
        /// on invalid input, missing game version, or any copy/write failure (after rolling back the target).
        /// </summary>
        public async Task<InstanceImportResult> ImportAsync(
            string sourceFolder,
            string? displayName = null,
            IProgress<ImportProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sourceFolder))
                throw new InstanceImportException("请选择要导入的实例文件夹。");

            string source = NormalizePath(sourceFolder);
            if (!Directory.Exists(source))
                throw new InstanceImportException($"所选文件夹不存在：{sourceFolder}");

            // Never allow importing something that already lives under (or is) the instances root.
            if (LauncherPaths.IsSameOrUnder(source, _instancesRoot))
                throw new InstanceImportException("所选文件夹位于实例目录之内，不能导入。请选择实例目录之外的文件夹。");

            InferredMetadata inferred = InferMetadata(source);

            string baseDisplay = ResolveDisplayName(displayName, inferred.Name, source);
            string baseFolder = LocalInstanceNaming.SanitizeFolderName(baseDisplay);

            Directory.CreateDirectory(_instancesRoot);
            var taken = CollectTakenNames();
            (string folder, string resolvedDisplay) = LocalInstanceNaming.ResolveUniqueName(baseFolder, baseDisplay, taken);

            string id = "local:" + Guid.NewGuid().ToString("N");
            string target = Path.Combine(_instancesRoot, folder);

            try
            {
                await CopyDirectoryAsync(source, target, progress, ct).ConfigureAwait(false);

                var metadata = new InstanceMetadata
                {
                    Id = id,
                    Name = resolvedDisplay,
                    Revision = 1,
                    Game = inferred.Game,
                    Loader = inferred.Loader,
                    LoaderVersion = inferred.LoaderVersion,
                    ServerAddress = inferred.ServerAddress,
                    ServerPort = inferred.ServerPort,
                    PackSha512 = inferred.PackSha512,
                    PackSha256 = inferred.PackSha256,
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
                    Game = inferred.Game,
                    Loader = inferred.Loader,
                    LoaderVersion = inferred.LoaderVersion,
                };
            }
            catch (OperationCanceledException)
            {
                TryDeleteDirectory(target);
                throw;
            }
            catch (Exception ex)
            {
                TryDeleteDirectory(target);
                throw new InstanceImportException("导入实例失败：" + ex.Message, ex);
            }
        }

        // ------------------------------------------------------------------
        // Metadata inference
        // ------------------------------------------------------------------

        /// <summary>Metadata inferred from the source folder, before the copy runs.</summary>
        private sealed class InferredMetadata
        {
            public string? Name;
            public string Game = string.Empty;
            public string Loader = "vanilla";
            public string LoaderVersion = string.Empty;
            public string ServerAddress = string.Empty;
            public int ServerPort;
            public string PackSha512 = string.Empty;
            public string PackSha256 = string.Empty;
        }

        private static InferredMetadata InferMetadata(string source)
        {
            // a. Prefer a CMUmcl-compatible instance.json at the root.
            string instanceJson = Path.Combine(source, "instance.json");
            if (File.Exists(instanceJson))
            {
                InferredMetadata? fromInstance = TryInferFromInstanceJson(instanceJson);
                if (fromInstance != null)
                    return fromInstance;
            }

            // b. Else a modrinth.index.json or a version-folder JSON.
            string modrinth = Path.Combine(source, "modrinth.index.json");
            if (File.Exists(modrinth))
            {
                InferredMetadata? fromModrinth = TryInferFromModrinth(modrinth);
                if (fromModrinth != null)
                    return fromModrinth;
            }

            foreach (string candidate in EnumerateVersionJsonCandidates(source))
            {
                InferredMetadata? fromVersion = TryInferFromVersionJson(candidate);
                if (fromVersion != null)
                    return fromVersion;
            }

            throw new InstanceImportException(
                "无法从所选文件夹推断 Minecraft 游戏版本。请确认其中包含 instance.json、modrinth.index.json 或版本 JSON（含 id/inheritsFrom/type 字段）。");
        }

        private static InferredMetadata? TryInferFromInstanceJson(string path)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return null;

                JsonElement root = doc.RootElement;
                string? game = GetStringCi(root, "game");
                if (string.IsNullOrWhiteSpace(game))
                    return null; // Not a CMUmcl metadata file (or missing version) — fall through.

                return new InferredMetadata
                {
                    Name = GetStringCi(root, "name"),
                    Game = game.Trim(),
                    Loader = NormalizeLoader(GetStringCi(root, "loader")),
                    LoaderVersion = GetStringCi(root, "loaderVersion") ?? string.Empty,
                    ServerAddress = GetStringCi(root, "serverAddress") ?? string.Empty,
                    ServerPort = GetIntCi(root, "serverPort"),
                    PackSha512 = GetStringCi(root, "packSha512") ?? string.Empty,
                    PackSha256 = GetStringCi(root, "packSha256") ?? string.Empty,
                };
            }
        }

        private static InferredMetadata? TryInferFromModrinth(string path)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return null;

                JsonElement root = doc.RootElement;

                string? game = null;
                if (TryGetPropertyCi(root, "versionId", out var versionId) && versionId.ValueKind == JsonValueKind.String)
                {
                    string v = versionId.GetString() ?? string.Empty;
                    if (IsReleaseLike(v))
                        game = v;
                }

                if (game == null && TryGetPropertyCi(root, "dependencies", out var depsForGame))
                {
                    string? mc = GetDependencyVersion(depsForGame, "minecraft");
                    if (IsReleaseLike(mc))
                        game = mc;
                }

                if (string.IsNullOrWhiteSpace(game))
                    return null;

                string loader = "vanilla";
                string loaderVersion = string.Empty;
                if (TryGetPropertyCi(root, "dependencies", out var depsForLoader))
                {
                    (string, string)[] order =
                    {
                        ("fabric-loader", "fabric"),
                        ("quilt-loader", "quilt"),
                        ("forge", "forge"),
                        ("neoforge", "neoforge"),
                    };
                    foreach ((string key, string value) in order)
                    {
                        string? v = GetDependencyVersion(depsForLoader, key);
                        if (v != null)
                        {
                            loader = value;
                            loaderVersion = v;
                            break;
                        }
                    }
                }

                return new InferredMetadata
                {
                    Name = GetStringCi(root, "name"),
                    Game = game,
                    Loader = loader,
                    LoaderVersion = loaderVersion,
                };
            }
        }

        private static InferredMetadata? TryInferFromVersionJson(string path)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return null;

                JsonElement root = doc.RootElement;

                if (!TryGetPropertyCi(root, "id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                    return null;
                string id = idEl.GetString() ?? string.Empty;

                bool hasInherits = TryGetPropertyCi(root, "inheritsFrom", out var inheritsEl) && inheritsEl.ValueKind == JsonValueKind.String;
                bool hasType = TryGetPropertyCi(root, "type", out _);
                bool hasLibraries = TryGetPropertyCi(root, "libraries", out var librariesEl) && librariesEl.ValueKind == JsonValueKind.Array;

                if (!hasInherits && !hasType && !hasLibraries)
                    return null; // Not a version JSON.

                string? game = null;
                if (hasInherits)
                {
                    string inherits = inheritsEl.GetString() ?? string.Empty;
                    if (IsReleaseLike(inherits))
                        game = inherits;
                }

                if (game == null && IsReleaseLike(id))
                    game = id;

                if (string.IsNullOrWhiteSpace(game))
                    return null;

                string loader = "vanilla";
                string loaderVersion = string.Empty;
                if (hasLibraries)
                {
                    (string Loader, string Version)? fromLibraries = DetectLoaderFromLibraries(librariesEl, game);
                    if (fromLibraries != null)
                    {
                        loader = fromLibraries.Value.Loader;
                        loaderVersion = fromLibraries.Value.Version;
                    }
                }

                if (loader == "vanilla")
                {
                    (string Loader, string Version)? fromId = DetectLoaderFromId(id, game);
                    if (fromId != null)
                    {
                        loader = fromId.Value.Loader;
                        loaderVersion = fromId.Value.Version;
                    }
                }

                return new InferredMetadata { Game = game, Loader = loader, LoaderVersion = loaderVersion };
            }
        }

        private static IEnumerable<string> EnumerateVersionJsonCandidates(string source)
        {
            foreach (string file in EnumerateFilesSafely(source, "*.json", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (name.Equals("instance.json", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("modrinth.index.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return file;
            }

            string versionsDir = Path.Combine(source, "versions");
            if (!Directory.Exists(versionsDir))
                yield break;

            foreach (string sub in EnumerateDirectoriesSafely(versionsDir))
            {
                foreach (string file in EnumerateFilesSafely(sub, "*.json", SearchOption.TopDirectoryOnly))
                    yield return file;
            }
        }

        // ------------------------------------------------------------------
        // Inference helpers
        // ------------------------------------------------------------------

        private static (string Loader, string Version)? DetectLoaderFromLibraries(JsonElement libraries, string gameVersion)
        {
            foreach (JsonElement lib in libraries.EnumerateArray())
            {
                if (lib.ValueKind != JsonValueKind.Object)
                    continue;
                if (!TryGetPropertyCi(lib, "name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                    continue;

                string name = nameEl.GetString() ?? string.Empty;
                if (name.StartsWith("net.fabricmc:fabric-loader:", StringComparison.OrdinalIgnoreCase))
                    return ("fabric", VersionFromCoordinate(name));
                if (name.StartsWith("org.quiltmc:quilt-loader:", StringComparison.OrdinalIgnoreCase))
                    return ("quilt", VersionFromCoordinate(name));
                if (name.StartsWith("net.minecraftforge:forge:", StringComparison.OrdinalIgnoreCase))
                    return ("forge", StripGamePrefix(VersionFromCoordinate(name), gameVersion));
                if (name.StartsWith("net.neoforged:neoforge:", StringComparison.OrdinalIgnoreCase))
                    return ("neoforge", StripGamePrefix(VersionFromCoordinate(name), gameVersion));
            }

            return null;
        }

        private static (string Loader, string Version)? DetectLoaderFromId(string id, string gameVersion)
        {
            string lower = id.ToLowerInvariant();
            if (lower.StartsWith("fabric-loader-", StringComparison.Ordinal))
                return ("fabric", ExtractVersionAfterPrefix(lower, "fabric-loader-", gameVersion));
            if (lower.StartsWith("quilt-loader-", StringComparison.Ordinal))
                return ("quilt", ExtractVersionAfterPrefix(lower, "quilt-loader-", gameVersion));
            if (lower.Contains("-forge-", StringComparison.Ordinal))
                return ("forge", After(lower, "-forge-"));
            if (lower.Contains("-neoforge-", StringComparison.Ordinal))
                return ("neoforge", After(lower, "-neoforge-"));

            return null;
        }

        private static string VersionFromCoordinate(string name)
        {
            int idx = name.LastIndexOf(':');
            return idx >= 0 && idx + 1 < name.Length ? name.Substring(idx + 1) : string.Empty;
        }

        private static string StripGamePrefix(string version, string gameVersion)
        {
            if (!string.IsNullOrEmpty(gameVersion)
                && version.StartsWith(gameVersion + "-", StringComparison.OrdinalIgnoreCase))
            {
                return version.Substring(gameVersion.Length + 1);
            }

            return version;
        }

        private static string ExtractVersionAfterPrefix(string id, string prefix, string gameVersion)
        {
            string remainder = id.Substring(prefix.Length);
            if (!string.IsNullOrEmpty(gameVersion)
                && remainder.EndsWith("-" + gameVersion, StringComparison.OrdinalIgnoreCase))
            {
                remainder = remainder.Substring(0, remainder.Length - gameVersion.Length - 1);
            }

            return remainder;
        }

        private static string After(string value, string token)
        {
            int idx = value.IndexOf(token, StringComparison.Ordinal);
            return idx >= 0 ? value.Substring(idx + token.Length) : string.Empty;
        }

        private static string? GetDependencyVersion(JsonElement dependencies, string key)
        {
            if (dependencies.ValueKind == JsonValueKind.Object)
            {
                if (TryGetPropertyCi(dependencies, key, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
                return null;
            }

            if (dependencies.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in dependencies.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;
                    if (TryGetPropertyCi(item, "id", out var idEl)
                        && idEl.ValueKind == JsonValueKind.String
                        && string.Equals(idEl.GetString(), key, StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryGetPropertyCi(item, "version", out var v) && v.ValueKind == JsonValueKind.String)
                            return v.GetString();
                    }
                }
            }

            return null;
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

        private static string NormalizeLoader(string? loader)
        {
            string normalized = (loader ?? string.Empty).Trim().ToLowerInvariant();
            return normalized.Length == 0 ? "vanilla" : normalized;
        }

        private static bool TryGetPropertyCi(JsonElement obj, string name, out JsonElement value)
        {
            if (obj.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in obj.EnumerateObject())
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = prop.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        private static string? GetStringCi(JsonElement obj, string name)
        {
            if (TryGetPropertyCi(obj, name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
            return null;
        }

        private static int GetIntCi(JsonElement obj, string name)
        {
            if (TryGetPropertyCi(obj, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n))
                return n;
            return 0;
        }

        // ------------------------------------------------------------------
        // Naming / copy helpers
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
                throw new InstanceImportException($"文件夹路径无效：{path}", ex);
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

            string folder = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(folder) ? "导入实例" : folder;
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

        private static Task CopyDirectoryAsync(string source, string target, IProgress<ImportProgress>? progress, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                long total = GetDirectoryBytes(source);
                long copied = 0;

                Directory.CreateDirectory(target);

                foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();

                    string relative = Path.GetRelativePath(source, file);
                    string dest = Path.Combine(target, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

                    copied += CopyFile(file, dest, ct);

                    progress?.Report(new ImportProgress
                    {
                        Message = $"正在复制文件（{copied}/{total} 字节）…",
                        BytesCopied = copied,
                        TotalBytes = total,
                    });
                }
            }, ct);
        }

        private static long CopyFile(string source, string dest, CancellationToken ct)
        {
            using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize);
            using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize);

            var buffer = new byte[CopyBufferSize];
            long total = 0;
            int read;
            while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                dst.Write(buffer, 0, read);
                total += read;
            }

            return total;
        }

        private static long GetDirectoryBytes(string dir)
        {
            long total = 0;
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // Best effort — the copy itself surfaces any real problem.
                }
            }

            return total;
        }

        private static IEnumerable<string> EnumerateFilesSafely(string dir, string pattern, SearchOption option)
        {
            try
            {
                return Directory.EnumerateFiles(dir, pattern, option);
            }
            catch
            {
                return Array.Empty<string>();
            }
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

        private static void TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best effort — never affects the source.
            }
        }
    }
}
