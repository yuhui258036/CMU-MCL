using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installers;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.ModLoaders.QuiltMC;
using CmlLib.Core.ProcessBuilder;

using CMUmcl.Auth;
using CMUmcl.Content;
using CMUmcl.Launch.Forge;
using CMUmcl.Launch.NeoForge;

namespace CMUmcl.Launch
{
    /// <summary>
    /// Installs and launches vanilla, Fabric, Forge, NeoForge or Quilt server packs.
    /// Unknown loader types throw a clear message.
    /// </summary>
    public sealed class LaunchService : IDisposable
    {
        private const string UnsupportedLoaderMessage = "不支持的加载器类型。当前版本支持原版/Fabric/Forge/NeoForge/Quilt。";
        private const string AuthlibInjectorSha256 = "9C7F4343E6C82034958FFB48C14A2CB0C85928BE7283103CE17DA00C6D5A7B10";

        private readonly YggdrasilClient _yggdrasil;
        private readonly AuthStore _authStore;
        private readonly InstalledStateStore _installedState;
        private readonly SettingsStore _settingsStore;
        private readonly HttpClient _http;

        public LaunchService(
            YggdrasilClient yggdrasil,
            AuthStore authStore,
            InstalledStateStore installedState,
            SettingsStore settingsStore)
        {
            _yggdrasil = yggdrasil;
            _authStore = authStore;
            _installedState = installedState;
            _settingsStore = settingsStore;
            _http = new HttpClient();
        }

        public async Task<LaunchResult> LaunchAsync(
            ManifestInstance instance,
            AuthSession session,
            IProgress<LaunchProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            if (session == null) throw new ArgumentNullException(nameof(session));

            // 1. Ensure installed revision matches the manifest revision.
            int? installedRevision = _installedState.GetInstalledRevision(instance.Id);
            if (installedRevision == null || installedRevision.Value < instance.Revision)
                throw new LaunchException("请先安装 / 更新该整合包再启动。");

            (string instanceDir, InstanceMetadata metadata) = await ReadInstanceMetadataAsync(instance.Id, ct).ConfigureAwait(false);
            return await LaunchCoreAsync(instanceDir, metadata, session, progress, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Launches a locally-created instance (no manifest entry) by reading its instance.json and
        /// installing the base game/loader exactly like a server pack. Skips the manifest revision check.
        /// </summary>
        public async Task<LaunchResult> LaunchLocalAsync(
            string instanceId,
            AuthSession session,
            IProgress<LaunchProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentNullException(nameof(instanceId));
            if (session == null) throw new ArgumentNullException(nameof(session));

            (string instanceDir, InstanceMetadata metadata) = await ReadInstanceMetadataAsync(instanceId, ct).ConfigureAwait(false);
            return await LaunchCoreAsync(instanceDir, metadata, session, progress, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Install-only path: installs the base Minecraft version plus the loader for an instance
        /// without validating a session or starting the game process. Used by create-time
        /// provisioning. Reads instance.json (via <see cref="ReadInstanceMetadataAsync"/>) and
        /// returns the resolved version name; throws a clear <see cref="LaunchException"/> on failure.
        /// For a local custom instance the caller passes a synthetic <see cref="ManifestInstance"/>
        /// with <c>Id</c> = local id and <c>Revision</c> = 1.
        /// </summary>
        public async Task<string> PrepareInstanceAsync(
            ManifestInstance instance,
            IProgress<LaunchProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));

            (string instanceDir, InstanceMetadata metadata) = await ReadInstanceMetadataAsync(instance.Id, ct).ConfigureAwait(false);
            PreparedInstall prepared = await PrepareCoreAsync(instanceDir, metadata, progress, ct).ConfigureAwait(false);
            return prepared.VersionName;
        }

        /// <summary>Reads instance.json from the recorded install path (fallback to the id) for an instance.</summary>
        private async Task<(string InstanceDir, InstanceMetadata Metadata)> ReadInstanceMetadataAsync(
            string instanceId,
            CancellationToken ct)
        {
            string installPath = _installedState.GetInstallPath(instanceId);
            string instanceDir = Path.Combine(LauncherPaths.InstancesDirectory, installPath);
            string instanceJsonPath = Path.Combine(instanceDir, "instance.json");
            if (!File.Exists(instanceJsonPath))
                throw new LaunchException("找不到实例信息，请重新安装该整合包。");

            InstanceMetadata metadata;
            try
            {
                metadata = System.Text.Json.JsonSerializer.Deserialize<InstanceMetadata>(
                    await File.ReadAllTextAsync(instanceJsonPath, ct).ConfigureAwait(false))
                    ?? throw new LaunchException("实例信息无效，请重新安装该整合包。");
            }
            catch (System.Text.Json.JsonException)
            {
                throw new LaunchException("实例信息格式无效，请重新安装该整合包。");
            }

            return (instanceDir, metadata);
        }

        /// <summary>Shared launch path: validate session + prepare authlib, install base + loader, then start the process.</summary>
        private async Task<LaunchResult> LaunchCoreAsync(
            string instanceDir,
            InstanceMetadata metadata,
            AuthSession session,
            IProgress<LaunchProgress>? progress,
            CancellationToken ct)
        {
            // Validate/refresh the session before launching.
            session = await EnsureValidSessionAsync(session, progress, ct).ConfigureAwait(false);

            // Prepare authlib-injector.
            progress?.Report(new LaunchProgress { Stage = "准备", Message = "正在准备 authlib-injector…" });
            string authlibJar = EnsureAuthlibInjector();

            PreparedInstall prepared = await PrepareCoreAsync(instanceDir, metadata, progress, ct).ConfigureAwait(false);

            // Build MSession.
            var mSession = new MSession(session.SelectedProfileName, session.AccessToken, session.SelectedProfileId);

            // Build MLaunchOption.
            string gameDir = ResolveGameDirectory(prepared.Settings, instanceDir, prepared.Loader);
            Directory.CreateDirectory(gameDir);

            var option = new MLaunchOption
            {
                Session = mSession,
                Path = prepared.Launcher.MinecraftPath,
                JavaPath = prepared.Java.JavaPath,
                MaximumRamMb = Math.Max(512, prepared.Settings.MaxMemoryMb),
                MinimumRamMb = 512,
                GameLauncherName = "CMUmcl",
                GameLauncherVersion = "0.1.0",
                ArgumentDictionary = new Dictionary<string, string>
                {
                    ["game_directory"] = gameDir,
                },
            };

            option.ExtraJvmArguments = MLaunchOption.DefaultExtraJvmArguments
                .Append(new MArgument($"-javaagent:{authlibJar}={YggdrasilClient.ApiRoot}"));

            // Quick play only when the server is not a local/loopback placeholder.
            string serverAddress = metadata.ServerAddress ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(serverAddress)
                && !string.Equals(serverAddress, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(serverAddress, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                option.ServerIp = serverAddress;
                option.ServerPort = metadata.ServerPort;
            }

            // Build and start the process.
            progress?.Report(new LaunchProgress { Stage = "启动", Message = "正在构建启动进程…" });
            Process process = await prepared.Launcher.BuildProcessAsync(prepared.VersionName, option, ct).ConfigureAwait(false);

            process.Start();

            progress?.Report(new LaunchProgress { Stage = "已启动", Message = "游戏已启动。" });

            return new LaunchResult
            {
                Process = process,
                Session = session,
                VersionName = prepared.VersionName,
            };
        }

        /// <summary>Shared install path: load settings, install vanilla, select Java, install the loader. No session/process.</summary>
        private async Task<PreparedInstall> PrepareCoreAsync(
            string instanceDir,
            InstanceMetadata metadata,
            IProgress<LaunchProgress>? progress,
            CancellationToken ct)
        {
            string gameVersion = metadata.Game ?? string.Empty;
            string loader = metadata.Loader ?? string.Empty;
            string loaderVersion = metadata.LoaderVersion ?? string.Empty;

            if (string.IsNullOrWhiteSpace(gameVersion))
                throw new LaunchException("实例缺少 Minecraft 版本信息。");

            AppSettings settings = _settingsStore.Load();

            // Build shared Minecraft path + launcher.
            var minecraftPath = new MinecraftPath(LauncherPaths.MinecraftDirectory);
            var launcher = new MinecraftLauncher(minecraftPath);

            // Install vanilla.
            progress?.Report(new LaunchProgress { Stage = "安装原版", Message = $"正在安装 Minecraft {gameVersion}…" });
            await launcher.InstallAsync(
                gameVersion,
                CreateFileProgress("安装原版", progress),
                CreateByteProgress("安装原版", progress),
                ct).ConfigureAwait(false);

            // Select a compatible Java runtime before loader resolution so Forge/NeoForge
            // processors can run.
            JavaInfo java = await SelectJavaAsync(gameVersion, settings, progress, ct).ConfigureAwait(false);

            string versionName = await InstallLoaderAsync(gameVersion, loader, loaderVersion, java, launcher, progress, ct).ConfigureAwait(false);

            return new PreparedInstall
            {
                VersionName = versionName,
                Java = java,
                Launcher = launcher,
                Settings = settings,
                Loader = loader,
            };
        }

        /// <summary>
        /// Selects a compatible Java runtime: validates a manually configured path first,
        /// otherwise auto-selects from local JDKs or auto-downloads the Mojang runtime.
        /// </summary>
        private static async Task<JavaInfo> SelectJavaAsync(
            string gameVersion,
            AppSettings settings,
            IProgress<LaunchProgress>? progress,
            CancellationToken ct)
        {
            JavaRequirement requirement = JavaVersionResolver.ResolveRequirement(gameVersion, LauncherPaths.MinecraftDirectory);

            JavaInfo? java;
            if (!string.IsNullOrWhiteSpace(settings.JavaPath))
            {
                java = JavaLocator.DetectExecutable(settings.JavaPath);
                if (java == null || java.Major <= 0)
                    throw new LaunchException("手动指定的 Java 路径无效或无法读取其版本信息。请在设置中检查 Java 路径。");

                if (java.Major < requirement.Major)
                    throw new LaunchException(
                        $"手动指定的 Java 版本为 {java.Major}，但 Minecraft {gameVersion} 需要 Java {requirement.Major} 或更高版本。请在设置中更换 Java 版本。");
            }
            else
            {
                IReadOnlyList<JavaInfo> javaCandidates = JavaLocator.Scan();
                java = JavaLocator.Select(requirement.Major, javaCandidates);

                if (java == null && requirement.Component != null)
                {
                    progress?.Report(new LaunchProgress { Stage = "下载 Java", Message = "未找到兼容的本地 Java，正在自动下载 Java 运行时…" });
                    using var installer = new MojangRuntimeInstaller();
                    java = await installer.EnsureRuntimeAsync(requirement.Component, requirement.Major, progress, ct).ConfigureAwait(false);
                }

                if (java == null)
                {
                    if (javaCandidates.Count == 0)
                        throw new LaunchException($"未找到 Java 运行环境。请安装 Java {requirement.Major} 并重试。");

                    string detected = string.Join(", ", javaCandidates.Select(c => c.Major).Distinct().OrderBy(m => m));
                    throw new LaunchException(
                        $"未找到满足 Minecraft {gameVersion} 的 Java（需要 Java {requirement.Major}）。已检测到的 Java 主版本：{detected}。请手动安装 Java {requirement.Major} 后重试。");
                }
            }

            progress?.Report(new LaunchProgress
            {
                Stage = "准备",
                Message = $"已选择 Java {java.Major}（{java.JavaPath}）",
                JavaVersion = java.Major.ToString(),
                JavaPath = java.JavaPath,
            });

            return java;
        }

        /// <summary>Resolves and installs the loader, returning the resolved version id.</summary>
        private async Task<string> InstallLoaderAsync(
            string gameVersion,
            string loader,
            string loaderVersion,
            JavaInfo java,
            MinecraftLauncher launcher,
            IProgress<LaunchProgress>? progress,
            CancellationToken ct)
        {
            string versionName;
            string normalizedLoader = loader.Trim().ToLowerInvariant();
            switch (normalizedLoader)
            {
                case "":
                case "vanilla":
                    versionName = gameVersion;
                    break;

                case "fabric":
                {
                    progress?.Report(new LaunchProgress { Stage = "安装 Fabric", Message = "正在安装 Fabric…" });
                    var fabric = new FabricInstaller(_http);
                    if (string.IsNullOrWhiteSpace(loaderVersion))
                        versionName = await fabric.Install(gameVersion, launcher.MinecraftPath).ConfigureAwait(false);
                    else
                        versionName = await fabric.Install(gameVersion, loaderVersion, launcher.MinecraftPath).ConfigureAwait(false);

                    progress?.Report(new LaunchProgress { Stage = "安装 Fabric", Message = $"正在下载 Fabric 依赖（{versionName}）…" });
                    await launcher.InstallAsync(
                        versionName,
                        CreateFileProgress("安装 Fabric", progress),
                        CreateByteProgress("安装 Fabric", progress),
                        ct).ConfigureAwait(false);
                    break;
                }

                case "forge":
                {
                    if (string.IsNullOrWhiteSpace(loaderVersion))
                        throw new LaunchException("缺少 Forge 版本信息，无法安装该加载器。");

                    progress?.Report(new LaunchProgress { Stage = "安装 Forge", Message = "正在安装 Forge…" });
                    using var forgeInstaller = new ForgeLoaderInstaller();
                    versionName = await forgeInstaller.InstallAsync(gameVersion, loaderVersion, java, launcher.MinecraftPath, progress, ct).ConfigureAwait(false);

                    progress?.Report(new LaunchProgress { Stage = "安装 Forge", Message = $"正在下载 Forge 依赖（{versionName}）…" });
                    await launcher.InstallAsync(
                        versionName,
                        CreateFileProgress("安装 Forge", progress),
                        CreateByteProgress("安装 Forge", progress),
                        ct).ConfigureAwait(false);
                    break;
                }

                case "neoforge":
                {
                    if (string.IsNullOrWhiteSpace(loaderVersion))
                        throw new LaunchException("缺少 NeoForge 版本信息，无法安装该加载器。");

                    progress?.Report(new LaunchProgress { Stage = "安装 NeoForge", Message = "正在安装 NeoForge…" });
                    versionName = await NeoForgeLoaderInstaller.InstallAsync(gameVersion, loaderVersion, java, launcher, progress, ct).ConfigureAwait(false);

                    progress?.Report(new LaunchProgress { Stage = "安装 NeoForge", Message = $"正在下载 NeoForge 依赖（{versionName}）…" });
                    await launcher.InstallAsync(
                        versionName,
                        CreateFileProgress("安装 NeoForge", progress),
                        CreateByteProgress("安装 NeoForge", progress),
                        ct).ConfigureAwait(false);
                    break;
                }

                case "quilt":
                {
                    progress?.Report(new LaunchProgress { Stage = "安装 Quilt", Message = "正在安装 Quilt…" });
                    var quilt = new QuiltInstaller(_http);
                    if (string.IsNullOrWhiteSpace(loaderVersion))
                        versionName = await quilt.Install(gameVersion, launcher.MinecraftPath).ConfigureAwait(false);
                    else
                        versionName = await quilt.Install(gameVersion, loaderVersion, launcher.MinecraftPath).ConfigureAwait(false);

                    progress?.Report(new LaunchProgress { Stage = "安装 Quilt", Message = $"正在下载 Quilt 依赖（{versionName}）…" });
                    await launcher.InstallAsync(
                        versionName,
                        CreateFileProgress("安装 Quilt", progress),
                        CreateByteProgress("安装 Quilt", progress),
                        ct).ConfigureAwait(false);
                    break;
                }

                default:
                    throw new LaunchException(UnsupportedLoaderMessage);
            }

            return versionName;
        }

        /// <summary>Carrier for the shared install path's outputs, consumed by both launch and provisioning.</summary>
        private sealed class PreparedInstall
        {
            public string VersionName = string.Empty;
            public JavaInfo Java = null!;
            public MinecraftLauncher Launcher = null!;
            public AppSettings Settings = null!;
            public string Loader = string.Empty;
        }

        /// <summary>
        /// Determines the game directory for an instance according to the version-isolation
        /// setting. "Modded" means the loader is neither empty nor vanilla.
        /// </summary>
        private static string ResolveGameDirectory(AppSettings settings, string instanceDir, string loader)
        {
            switch (settings.VersionIsolation)
            {
                case VersionIsolation.All:
                    return instanceDir;

                case VersionIsolation.ModdedOnly:
                    return IsModdedLoader(loader) ? instanceDir : LauncherPaths.GameDirectory;

                case VersionIsolation.Off:
                default:
                    return LauncherPaths.GameDirectory;
            }
        }

        private static bool IsModdedLoader(string? loader)
        {
            string normalized = (loader ?? string.Empty).Trim().ToLowerInvariant();
            return normalized is not ("" or "vanilla");
        }

        private async Task<AuthSession> EnsureValidSessionAsync(
            AuthSession session,
            IProgress<LaunchProgress>? progress,
            CancellationToken ct)
        {
            progress?.Report(new LaunchProgress { Stage = "校验登录", Message = "正在校验登录状态…" });

            bool valid;
            try
            {
                valid = await _yggdrasil.ValidateAsync(session.AccessToken, session.ClientToken, ct).ConfigureAwait(false);
            }
            catch (YggdrasilException ex)
            {
                throw new LaunchException("无法校验登录状态：" + ex.Message);
            }

            if (valid)
            {
                session.SavedAt = DateTimeOffset.UtcNow;
                return session;
            }

            // 403 -> refresh.
            try
            {
                RefreshResult refreshed = await _yggdrasil.RefreshAsync(session.AccessToken, session.ClientToken, ct).ConfigureAwait(false);

                var updated = new AuthSession
                {
                    ClientToken = string.IsNullOrWhiteSpace(refreshed.ClientToken) ? session.ClientToken : refreshed.ClientToken,
                    AccessToken = refreshed.AccessToken,
                    SelectedProfileId = refreshed.SelectedProfile?.Id ?? session.SelectedProfileId,
                    SelectedProfileName = refreshed.SelectedProfile?.Name ?? session.SelectedProfileName,
                    Username = refreshed.User?.Username ?? session.Username,
                    SavedAt = DateTimeOffset.UtcNow,
                };

                _authStore.SaveSession(updated);
                return updated;
            }
            catch (YggdrasilException)
            {
                throw new LaunchException("登录已过期，请重新登录。");
            }
        }

        private string EnsureAuthlibInjector()
        {
            string target = LauncherPaths.AuthlibInjectorPath;
            if (File.Exists(target) && Sha256Equals(target, AuthlibInjectorSha256))
                return target;

            // Copy the bundled jar into the shared launcher directory.
            string bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "authlib-injector.jar");
            if (!File.Exists(bundled))
                throw new LaunchException("缺少 authlib-injector.jar，无法启动。");

            Directory.CreateDirectory(LauncherPaths.RootDirectory);
            File.Copy(bundled, target, overwrite: true);

            if (!Sha256Equals(target, AuthlibInjectorSha256))
                throw new LaunchException("authlib-injector.jar 校验失败，无法启动。");

            return target;
        }

        private static bool Sha256Equals(string path, string expectedHex)
        {
            try
            {
                byte[] hash = SHA256.HashData(File.ReadAllBytes(path));
                string actual = Convert.ToHexString(hash);
                return string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static Progress<InstallerProgressChangedEventArgs> CreateFileProgress(
            string stage,
            IProgress<LaunchProgress>? progress)
        {
            return new Progress<InstallerProgressChangedEventArgs>(e =>
                progress?.Report(new LaunchProgress
                {
                    Stage = stage,
                    Message = e.Name ?? stage,
                    ProgressedTasks = e.ProgressedTasks,
                    TotalTasks = e.TotalTasks,
                }));
        }

        private static Progress<ByteProgress> CreateByteProgress(string stage, IProgress<LaunchProgress>? progress)
        {
            return new Progress<ByteProgress>(b =>
                progress?.Report(new LaunchProgress
                {
                    Stage = stage,
                    Message = stage,
                    ProgressedBytes = b.ProgressedBytes,
                    TotalBytes = b.TotalBytes,
                }));
        }

        public void Dispose() => _http.Dispose();
    }
}
