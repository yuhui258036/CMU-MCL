using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CMUmcl.Content
{
    /// <summary>Raised when a local instance cannot be created (invalid input or an I/O failure).</summary>
    public sealed class LocalInstanceException : Exception
    {
        public LocalInstanceException(string message) : base(message) { }

        public LocalInstanceException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>User input required to create a local (non-manifest) instance.</summary>
    public sealed class CreateLocalInstanceRequest
    {
        /// <summary>User-facing instance name.</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Minecraft game version (e.g. <c>1.20.1</c>).</summary>
        public string GameVersion { get; set; } = string.Empty;

        /// <summary>Loader: <c>""</c>/<c>vanilla</c>, <c>fabric</c>, <c>forge</c>, <c>neoforge</c> or <c>quilt</c>.</summary>
        public string Loader { get; set; } = string.Empty;

        /// <summary>Loader version; required for Forge/NeoForge, optional for Fabric/Quilt.</summary>
        public string LoaderVersion { get; set; } = string.Empty;
    }

    /// <summary>The result of a successful local-instance creation.</summary>
    public sealed class LocalInstanceResult
    {
        /// <summary>The assigned local id (<c>local:{Guid}</c>).</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>The on-disk folder name under the instances root.</summary>
        public string InstallPath { get; init; } = string.Empty;

        /// <summary>The final (possibly auto-suffixed) display name.</summary>
        public string DisplayName { get; init; } = string.Empty;

        /// <summary>The absolute path to the created instance directory.</summary>
        public string InstanceDirectory { get; init; } = string.Empty;
    }

    /// <summary>
    /// Creates local instances that exist only on disk (and in the installed-state store) without a
    /// resource-manifest entry or a pack download. Mirrors PCL/YSUMCL "create custom instance".
    /// </summary>
    public sealed class LocalInstanceService
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
        };

        private readonly InstalledStateStore _installedState;

        public LocalInstanceService(InstalledStateStore installedState)
        {
            _installedState = installedState ?? throw new ArgumentNullException(nameof(installedState));
        }

        /// <summary>Creates a local instance and records it. Throws <see cref="LocalInstanceException"/> on invalid input.</summary>
        public LocalInstanceResult Create(CreateLocalInstanceRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            Validate(request);

            string id = "local:" + Guid.NewGuid().ToString("N");
            string loader = NormalizeLoader(request.Loader);

            string instancesRoot = LauncherPaths.InstancesDirectory;
            Directory.CreateDirectory(instancesRoot);

            string baseFolder = LocalInstanceNaming.SanitizeFolderName(request.DisplayName);
            string baseDisplay = request.DisplayName.Trim();

            var taken = CollectTakenNames(instancesRoot);
            (string folder, string displayName) = LocalInstanceNaming.ResolveUniqueName(baseFolder, baseDisplay, taken);

            string instanceDir = Path.Combine(instancesRoot, folder);
            Directory.CreateDirectory(instanceDir);

            var metadata = new InstanceMetadata
            {
                Id = id,
                Name = displayName,
                Revision = 1,
                Game = request.GameVersion.Trim(),
                Loader = loader,
                LoaderVersion = request.LoaderVersion.Trim(),
                ServerAddress = string.Empty,
                ServerPort = 0,
                PackSha512 = string.Empty,
                InstalledAt = DateTimeOffset.UtcNow,
            };

            string metaPath = Path.Combine(instanceDir, "instance.json");
            try
            {
                File.WriteAllText(metaPath, JsonSerializer.Serialize(metadata, JsonOptions));
            }
            catch (Exception ex)
            {
                TryDeleteDirectory(instanceDir);
                throw new LocalInstanceException("无法写入实例信息文件：" + ex.Message, ex);
            }

            _installedState.RecordInstall(id, 1, folder, displayName);

            return new LocalInstanceResult
            {
                Id = id,
                InstallPath = folder,
                DisplayName = displayName,
                InstanceDirectory = instanceDir,
            };
        }

        // ------------------------------------------------------------------
        // Validation
        // ------------------------------------------------------------------

        private static void Validate(CreateLocalInstanceRequest request)
        {
            string name = request.DisplayName?.Trim() ?? string.Empty;
            if (name.Length == 0)
                throw new LocalInstanceException("实例名称不能为空。");

            if (string.IsNullOrWhiteSpace(request.GameVersion))
                throw new LocalInstanceException("请填写 Minecraft 游戏版本。");

            string loader = NormalizeLoader(request.Loader);
            if (loader is not ("" or "vanilla" or "fabric" or "forge" or "neoforge" or "quilt"))
                throw new LocalInstanceException("不支持的加载器类型。当前版本支持原版/Fabric/Forge/NeoForge/Quilt。");

            if (loader is "forge" or "neoforge" && string.IsNullOrWhiteSpace(request.LoaderVersion))
                throw new LocalInstanceException("Forge / NeoForge 需要填写加载器版本。");
        }

        private static string NormalizeLoader(string loader)
        {
            string normalized = (loader ?? string.Empty).Trim().ToLowerInvariant();
            return normalized == "" ? "vanilla" : normalized;
        }

        // ------------------------------------------------------------------
        // Collision detection (existing folders + recorded install paths)
        // ------------------------------------------------------------------

        private List<string> CollectTakenNames(string instancesRoot)
        {
            var taken = new List<string>();

            try
            {
                foreach (string dir in Directory.EnumerateDirectories(instancesRoot))
                    taken.Add(Path.GetFileName(dir));
            }
            catch
            {
                // The directory was created above; a failure here is unexpected but non-fatal.
            }

            foreach (var kv in _installedState.ListRecords())
            {
                string path = string.IsNullOrWhiteSpace(kv.Value.InstallPath) ? kv.Key : kv.Value.InstallPath!;
                if (!string.IsNullOrWhiteSpace(path))
                    taken.Add(path);
            }

            return taken;
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
    }
}
