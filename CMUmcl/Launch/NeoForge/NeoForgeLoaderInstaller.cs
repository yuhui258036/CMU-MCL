// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using CmlLib.Core;
using CmlLib.Core.Installer.NeoForge;
using CmlLib.Core.Installer.NeoForge.Installers;
using CmlLib.Core.Installer.NeoForge.Versions;
using CmlLib.Core.Installers;

namespace CMUmcl.Launch.NeoForge
{
    /// <summary>
    /// NeoForge 加载器安装薄封装：委托给 CmlLib.Core.Installer.NeoForge 4.0.1
    /// 的 <see cref="NeoForgeInstaller"/>（MIT 许可），并把其进度回调映射到
    /// CMUmcl 的 <see cref="LaunchProgress"/>。用已选 Java 的同目录 java.exe
    /// 运行安装器 processors；游戏启动仍使用 javaw。
    /// </summary>
    public static class NeoForgeLoaderInstaller
    {
        private const string Stage = "安装 NeoForge";

        /// <summary>安装 NeoForge 并返回最终版本 id。</summary>
        public static async Task<string> InstallAsync(
            string gameVersion,
            string loaderVersion,
            JavaInfo java,
            MinecraftLauncher launcher,
            IProgress<LaunchProgress>? progress,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(gameVersion) || string.IsNullOrWhiteSpace(loaderVersion))
                throw new LaunchException("缺少 NeoForge 版本信息，无法安装该加载器。");

            string consoleJava = ResolveConsoleJava(java.JavaPath);

            var options = new NeoForgeInstallOptions
            {
                JavaPath = consoleJava,
                SkipIfAlreadyInstalled = true,
                FileProgress = CreateFileProgress(progress),
                ByteProgress = CreateByteProgress(progress),
                InstallerOutput = CreateInstallerOutput(progress),
                CancellationToken = ct,
            };

            progress?.Report(new LaunchProgress { Stage = Stage, Message = "正在安装 NeoForge…" });

            var installer = new NeoForgeInstaller(launcher);
            var version = new NeoForgeVersion(gameVersion, loaderVersion);
            string versionId = await installer.Install(version, options).ConfigureAwait(false);

            progress?.Report(new LaunchProgress { Stage = Stage, Message = $"NeoForge {versionId} 安装完成。" });
            return versionId;
        }

        /// <summary>用已选 javaw 的同目录 java.exe 运行安装器 processors。</summary>
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

        private static Progress<InstallerProgressChangedEventArgs> CreateFileProgress(IProgress<LaunchProgress>? progress)
            => new(e => progress?.Report(new LaunchProgress
            {
                Stage = Stage,
                Message = e.Name ?? Stage,
                ProgressedTasks = e.ProgressedTasks,
                TotalTasks = e.TotalTasks,
            }));

        private static Progress<ByteProgress> CreateByteProgress(IProgress<LaunchProgress>? progress)
            => new(b => progress?.Report(new LaunchProgress
            {
                Stage = Stage,
                Message = Stage,
                ProgressedBytes = b.ProgressedBytes,
                TotalBytes = b.TotalBytes,
            }));

        private static Progress<string> CreateInstallerOutput(IProgress<LaunchProgress>? progress)
            => new(line => progress?.Report(new LaunchProgress { Stage = Stage, Message = line }));
    }
}
