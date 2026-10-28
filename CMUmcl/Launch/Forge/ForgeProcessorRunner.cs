// SPDX-License-Identifier: GPL-3.0-only
// 本文件移植/重实现自 Axolotl Launcher（YSUMCL fork）的 Forge 安装器逻辑
//（packages/app-lib/src/launcher/mod.rs 的 processors 执行与 data map 构建，
//   packages/app-lib/src/launcher/args.rs 的 get_processor_arguments/get_processor_main_class，
//   GPL-3.0-only）。详见仓库根 LICENSE 与 NOTICE。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CMUmcl.Launch.Forge
{
    /// <summary>
    /// 用已选 Java 按序运行 Forge processors：data map 构建、{KEY}/[coordinate] 参数解析、
    /// 主类读取、退出码与 outputs 校验。
    /// </summary>
    public sealed class ForgeProcessorRunner
    {
        public async Task RunAsync(
            string javaExe,
            IReadOnlyList<ForgeProcessor> processors,
            IReadOnlyDictionary<string, SidedDataEntry> installerData,
            string minecraftJar,
            string minecraftVersion,
            string minecraftDir,
            string librariesDir,
            string installerPath,
            IProgress<LaunchProgress>? progress,
            CancellationToken ct)
        {
            if (!File.Exists(minecraftJar))
                throw new LaunchException($"Forge 安装需要 Minecraft {minecraftVersion} 已安装（找不到 {minecraftJar}）。");

            var dataMap = BuildDataMap(minecraftJar, minecraftVersion, minecraftDir, installerPath, librariesDir, installerData);

            int total = processors.Count;
            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                ForgeProcessor processor = processors[i];

                if (!ShouldRun(processor))
                {
                    progress?.Report(new LaunchProgress { Stage = "安装 Forge", Message = $"跳过 Forge 处理器 {i + 1}/{total}（server）" });
                    continue;
                }

                string classpath = BuildClassPath(processor.Classpath.Concat(new[] { processor.Jar }), librariesDir);
                string jarPath = ResolveLibraryPath(processor.Jar, librariesDir);
                string mainClass = GetProcessorMainClass(jarPath);
                var args = processor.Args.Select(a => ResolveProcessorArg(a, librariesDir, dataMap)).ToList();

                progress?.Report(new LaunchProgress { Stage = "安装 Forge", Message = $"运行 Forge 处理器 {i + 1}/{total}" });

                (int exitCode, string stderr) = await RunJavaAsync(javaExe, classpath, mainClass, args, minecraftDir, ct).ConfigureAwait(false);
                if (exitCode != 0)
                    throw new LaunchException($"Forge 处理器执行失败（{processor.Jar}）。{Tail(stderr)}");

                ValidateOutputs(processor, dataMap, librariesDir);
            }
        }

        // ------------------------------------------------------------------
        // 纯函数（可单测）
        // ------------------------------------------------------------------

        public static bool ShouldRun(ForgeProcessor processor)
        {
            if (processor.Sides == null || processor.Sides.Count == 0)
                return true;
            return processor.Sides.Contains("client", StringComparer.OrdinalIgnoreCase);
        }

        public static Dictionary<string, string> BuildClientDataMap(IReadOnlyDictionary<string, SidedDataEntry> installerData)
        {
            var map = new Dictionary<string, string>();
            foreach (var kv in installerData)
                map[kv.Key] = kv.Value.Client;
            return map;
        }

        public static Dictionary<string, string> BuildDataMap(
            string minecraftJar,
            string minecraftVersion,
            string minecraftDir,
            string installerPath,
            string librariesDir,
            IReadOnlyDictionary<string, SidedDataEntry> installerData)
        {
            var map = BuildClientDataMap(installerData);
            map["SIDE"] = "client";
            map["MINECRAFT_JAR"] = minecraftJar;
            map["MINECRAFT_VERSION"] = minecraftVersion;
            map["ROOT"] = minecraftDir;
            map["INSTALLER"] = installerPath;
            map["LIBRARY_DIR"] = librariesDir;
            return map;
        }

        public static string ResolveLibraryPath(string coordinate, string librariesDir)
            => Path.GetFullPath(Path.Combine(librariesDir, MavenArtifact.ToRelativePath(coordinate)));

        public static string BuildClassPath(IEnumerable<string> coordinates, string librariesDir)
            => string.Join(Path.PathSeparator, coordinates.Select(c => ResolveLibraryPath(c, librariesDir)));

        public static string StripQuotes(string s)
        {
            if (s.Length >= 2
                && ((s[0] == '\'' && s[s.Length - 1] == '\'') || (s[0] == '"' && s[s.Length - 1] == '"')))
                return s.Substring(1, s.Length - 2);
            return s;
        }

        public static string ResolveDataValue(string value, string librariesDir)
        {
            if (value.Length >= 2 && value[0] == '[' && value[value.Length - 1] == ']')
                return ResolveLibraryPath(value.Substring(1, value.Length - 2), librariesDir);
            return StripQuotes(value);
        }

        public static string ResolveProcessorArg(string arg, string librariesDir, IReadOnlyDictionary<string, string> data)
        {
            if (arg.Length >= 2 && arg[0] == '[' && arg[arg.Length - 1] == ']')
                return ResolveLibraryPath(arg.Substring(1, arg.Length - 2), librariesDir);

            string result = arg;
            foreach (var kv in data)
            {
                string value = ResolveDataValue(kv.Value, librariesDir);
                result = result.Replace("{" + kv.Key + "}", value);
            }
            return result;
        }

        public static string GetProcessorMainClass(string jarPath)
        {
            using ZipArchive zip = ZipFile.OpenRead(jarPath);
            ZipArchiveEntry entry = zip.GetEntry("META-INF/MANIFEST.MF")
                ?? throw new LaunchException($"找不到处理器主类：{jarPath}");

            using var reader = new StreamReader(entry.Open());
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string t = line.Replace(" ", string.Empty).Replace("\t", string.Empty);
                if (t.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase))
                {
                    string main = t.Substring("Main-Class:".Length).Trim();
                    if (!string.IsNullOrEmpty(main))
                        return main;
                }
            }

            throw new LaunchException($"找不到处理器主类：{jarPath}");
        }

        public static void ValidateOutputs(ForgeProcessor processor, IReadOnlyDictionary<string, string> data, string librariesDir)
        {
            if (processor.Outputs == null)
                return;

            foreach (var kv in processor.Outputs)
            {
                string outputPath = ResolveProcessorArg(kv.Key, librariesDir, data);
                string expectedSha1 = StripQuotes(ResolveProcessorArg(kv.Value, librariesDir, data));

                if (!File.Exists(outputPath))
                    throw new LaunchException($"Forge 处理器输出缺失：{outputPath}");

                if (!string.IsNullOrWhiteSpace(expectedSha1)
                    && !MojangRuntimeInstaller.MatchesSha1AndSize(outputPath, 0, expectedSha1))
                    throw new LaunchException($"Forge 处理器输出校验失败：{outputPath}");
            }
        }

        // ------------------------------------------------------------------
        // 进程执行
        // ------------------------------------------------------------------

        private static async Task<(int ExitCode, string Stderr)> RunJavaAsync(
            string javaExe,
            string classpath,
            string mainClass,
            IReadOnlyList<string> args,
            string workingDir,
            CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDir,
            };
            psi.ArgumentList.Add("-cp");
            psi.ArgumentList.Add(classpath);
            psi.ArgumentList.Add(mainClass);
            foreach (string a in args)
                psi.ArgumentList.Add(a);

            using var proc = new Process { StartInfo = psi };
            try
            {
                proc.Start();
                Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
                Task<string> stderrTask = proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                string stderr = await stderrTask.ConfigureAwait(false);
                await stdoutTask.ConfigureAwait(false);
                return (proc.ExitCode, stderr);
            }
            finally
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
            }
        }

        private static string Tail(string text, int maxLines = 30)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string[] lines = text.Split('\n');
            int start = Math.Max(0, lines.Length - maxLines);
            return string.Join("\n", lines, start, lines.Length - start).Trim();
        }
    }
}
