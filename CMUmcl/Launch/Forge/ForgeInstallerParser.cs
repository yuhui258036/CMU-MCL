// SPDX-License-Identifier: GPL-3.0-only
// 本文件移植/重实现自 Axolotl Launcher（YSUMCL fork）的 Forge 安装器逻辑
//（packages/app-lib/src/api/loader_metadata.rs 的 parse_installer，
//   GPL-3.0-only）。详见仓库根 LICENSE 与 NOTICE。

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CMUmcl.Launch.Forge
{
    /// <summary>解析结果。</summary>
    public sealed class ParsedForgeInstaller
    {
        public string VersionId { get; init; } = string.Empty;
        public string InheritsFrom { get; init; } = string.Empty;
        public List<ForgeLibrary> MergedLibraries { get; init; } = new();
        public List<ForgeLibrary> InstallerLibraries { get; init; } = new();
        public List<ForgeProcessor> Processors { get; init; } = new();
        public Dictionary<string, SidedDataEntry> Data { get; init; } = new();
        public List<EmbeddedArtifact> Embedded { get; init; } = new();
        public JsonObject FinalVersionDocument { get; init; } = new();
        public string InstallProfileJson { get; init; } = string.Empty;
    }

    /// <summary>从安装器 jar 提取的内嵌工件。</summary>
    public sealed class EmbeddedArtifact
    {
        public string ArchivePath { get; init; } = string.Empty;
        public string TargetPath { get; init; } = string.Empty;
    }

    /// <summary>
    /// 从安装器 jar 读取 install_profile.json 与 version.json，合并库、提取内嵌工件、
    /// 产出最终版本档案文档（纯逻辑，输入字节数组 + 暂存目录）。
    /// </summary>
    public static class ForgeInstallerParser
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        public static ParsedForgeInstaller Parse(byte[] installerJarBytes, string stagingDir)
        {
            using var ms = new MemoryStream(installerJarBytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);

            string profileJson = ReadZipEntryText(zip, "install_profile.json");
            JsonNode? profileNode = JsonNode.Parse(profileJson);
            if (profileNode is not JsonObject profileObj)
                throw new LaunchException("解析 Forge 安装器失败（install_profile.json 格式无效）。当前版本仅支持 Forge 1.20.1 现代安装器。");

            InstallerProfile profile = JsonSerializer.Deserialize<InstallerProfile>(profileJson, JsonOptions)
                ?? new InstallerProfile();

            // 读取 version.json（缺失时读 json 字段；内联 versionInfo/install → 旧版错误）
            string versionJson = ReadVersionJson(zip, profileObj);
            JsonNode? versionNode = JsonNode.Parse(versionJson);
            if (versionNode is not JsonObject versionObj)
                throw new LaunchException("解析 Forge 安装器失败（version.json 格式无效）。当前版本仅支持 Forge 1.20.1 现代安装器。");

            // inheritsFrom 回填
            if (versionObj["inheritsFrom"] is null)
            {
                if (string.IsNullOrWhiteSpace(profile.Minecraft))
                    throw new LaunchException("解析 Forge 安装器失败（inheritsFrom 缺失且无 minecraft 版本）。当前版本仅支持 Forge 1.20.1 现代安装器。");
                versionObj["inheritsFrom"] = profile.Minecraft;
            }
            string inheritsFrom = versionObj["inheritsFrom"]?.GetValue<string>() ?? string.Empty;

            // version.json 库（typed，仅取 name + downloads.artifact，用于校验）
            var versionLibraries = new List<ForgeLibrary>();
            if (versionObj["libraries"] is JsonArray varr)
                versionLibraries = JsonSerializer.Deserialize<List<ForgeLibrary>>(varr.ToJsonString(), JsonOptions) ?? new List<ForgeLibrary>();

            var versionNames = new HashSet<string>(versionLibraries.Select(l => l.Name), StringComparer.Ordinal);

            // 合并（version 优先，按 name 去重）
            var merged = new List<ForgeLibrary>(versionLibraries);
            var mergedNames = new HashSet<string>(versionNames, StringComparer.Ordinal);
            foreach (ForgeLibrary lib in profile.Libraries)
                if (mergedNames.Add(lib.Name))
                    merged.Add(lib);

            // 提取内嵌工件（data 值以 '/' 开头），并把 client 值改写为绝对路径
            var data = new Dictionary<string, SidedDataEntry>(profile.Data);
            var embedded = new List<EmbeddedArtifact>();
            foreach (var kv in data)
            {
                if (!kv.Value.Client.StartsWith("/", StringComparison.Ordinal))
                    continue;

                string archivePath = kv.Value.Client.TrimStart('/');
                byte[] bytes = ReadZipEntryBytes(zip, archivePath);
                string rel = ForgePathSafety.SanitizeRelativePath(archivePath);
                string target = ForgePathSafety.ResolveUnder(stagingDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
                kv.Value.Client = target;
                embedded.Add(new EmbeddedArtifact { ArchivePath = "/" + archivePath, TargetPath = target });
            }

            // 识别 Forge 构件版本：语言提供器（fmlcore/javafmllanguage/lowcodelanguage/mclanguage）
            // 与 universal jar 共享 {mc}-{forge} 版本，而安装期工具（ForgeAutoRenamingTool/
            // installertools/binarypatcher/旧版 asm 等）版本独立。最终版本档案只合并前者，
            // 避免把安装期工具混入运行时 classpath（会破坏 BootstrapLauncher 的模块层）。
            string forgeVersion = string.Empty;
            foreach (ForgeLibrary lib in profile.Libraries)
            {
                if (!lib.Name.StartsWith("net.minecraftforge:forge:", StringComparison.Ordinal))
                    continue;
                forgeVersion = MavenArtifact.Parse(lib.Name).Version;
                break;
            }

            // 最终版本档案文档：verbatim version.json 的库 + 运行时 install_profile 库
            var mergedArray = new JsonArray();
            if (versionObj["libraries"] is JsonArray originalArray)
                foreach (JsonNode? item in originalArray)
                    if (item != null)
                        mergedArray.Add(item.DeepClone());
            foreach (ForgeLibrary lib in profile.Libraries)
            {
                if (versionNames.Contains(lib.Name))
                    continue;
                if (string.IsNullOrEmpty(forgeVersion)
                    || MavenArtifact.Parse(lib.Name).Version != forgeVersion)
                    continue;
                mergedArray.Add(LibraryToJson(lib));
            }

            versionObj["libraries"] = mergedArray;
            versionObj.Remove("data");
            versionObj.Remove("processors");

            return new ParsedForgeInstaller
            {
                VersionId = versionObj["id"]?.GetValue<string>() ?? string.Empty,
                InheritsFrom = inheritsFrom,
                MergedLibraries = merged,
                InstallerLibraries = profile.Libraries,
                Processors = profile.Processors,
                Data = data,
                Embedded = embedded,
                FinalVersionDocument = versionObj,
                InstallProfileJson = profileJson,
            };
        }

        private static JsonObject LibraryToJson(ForgeLibrary lib)
        {
            var artifact = new JsonObject();
            ForgeLibraryDownload? a = lib.Downloads?.Artifact;
            if (!string.IsNullOrWhiteSpace(a?.Path)) artifact["path"] = a.Path;
            if (!string.IsNullOrWhiteSpace(a?.Url)) artifact["url"] = a.Url;
            if (!string.IsNullOrWhiteSpace(a?.Sha1)) artifact["sha1"] = a.Sha1;
            if (a != null && a.Size > 0) artifact["size"] = a.Size;

            var obj = new JsonObject { ["name"] = lib.Name };
            if (artifact.Count > 0)
                obj["downloads"] = new JsonObject { ["artifact"] = artifact };
            return obj;
        }

        private static string ReadVersionJson(ZipArchive zip, JsonObject profileObj)
        {
            if (zip.GetEntry("version.json") != null)
                return ReadZipEntryText(zip, "version.json");

            string? jsonField = profileObj["json"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(jsonField))
                return ReadZipEntryText(zip, jsonField.TrimStart('/'));

            if (profileObj.ContainsKey("versionInfo") || profileObj.ContainsKey("install"))
                throw new LaunchException("暂不支持该 Forge 版本的旧版安装流程，请使用 1.20.1 或更新版本。");

            throw new LaunchException("解析 Forge 安装器失败（缺少 version.json）。当前版本仅支持 Forge 1.20.1 现代安装器。");
        }

        private static string ReadZipEntryText(ZipArchive zip, string path)
            => Encoding.UTF8.GetString(ReadZipEntryBytes(zip, path));

        private static byte[] ReadZipEntryBytes(ZipArchive zip, string path)
        {
            ZipArchiveEntry entry = zip.GetEntry(path)
                ?? throw new LaunchException($"解析 Forge 安装器失败（缺少 {path}）。当前版本仅支持 Forge 1.20.1 现代安装器。");
            using Stream s = entry.Open();
            using var buffer = new MemoryStream();
            s.CopyTo(buffer);
            return buffer.ToArray();
        }
    }
}
