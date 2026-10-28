// SPDX-License-Identifier: GPL-3.0-only
// 本文件移植/重实现自 Axolotl Launcher（YSUMCL fork）的 Forge 安装器逻辑
//（packages/daedalus/src/lib.rs 的 get_path_from_artifact 与
//   packages/daedalus/src/modded.rs 的 InstallerProfile/Processor/SidedDataEntry 结构，
//   GPL-3.0-only）。详见仓库根 LICENSE 与 NOTICE。

using System;
using System.Collections.Generic;
using System.IO;

namespace CMUmcl.Launch.Forge
{
    /// <summary>install_profile.json 的顶层结构（Forge 现代安装器）。</summary>
    public sealed class InstallerProfile
    {
        public string? Minecraft { get; set; }
        public Dictionary<string, SidedDataEntry> Data { get; set; } = new();
        public List<ForgeProcessor> Processors { get; set; } = new();
        public List<ForgeLibrary> Libraries { get; set; } = new();
    }

    /// <summary>data 映射中按安装端区分的值。</summary>
    public sealed class SidedDataEntry
    {
        public string Client { get; set; } = string.Empty;
        public string Server { get; set; } = string.Empty;
    }

    /// <summary>一个 Forge 处理器（按序执行、按 sides 过滤）。</summary>
    public sealed class ForgeProcessor
    {
        public string Jar { get; set; } = string.Empty;
        public List<string> Classpath { get; set; } = new();
        public List<string> Args { get; set; } = new();
        public Dictionary<string, string>? Outputs { get; set; }
        public List<string>? Sides { get; set; }
    }

    /// <summary>一个库（仅保留 name 与 downloads.artifact，用于下载与校验）。</summary>
    public sealed class ForgeLibrary
    {
        public string Name { get; set; } = string.Empty;
        public ForgeLibraryDownloads? Downloads { get; set; }
    }

    public sealed class ForgeLibraryDownloads
    {
        public ForgeLibraryDownload? Artifact { get; set; }
    }

    public sealed class ForgeLibraryDownload
    {
        public string? Path { get; set; }
        public string Url { get; set; } = string.Empty;
        public string? Sha1 { get; set; }
        public long Size { get; set; }
    }

    /// <summary>解析后的 Maven 坐标。</summary>
    public sealed record MavenCoordinate(string Group, string Artifact, string Version, string? Classifier, string Extension);

    /// <summary>
    /// 把 <c>group:artifact:version[:classifier][@ext]</c> 解析为库相对路径
    ///（移植自 daedalus 的 get_path_from_artifact）。
    /// </summary>
    public static class MavenArtifact
    {
        public static MavenCoordinate Parse(string coordinate)
        {
            if (string.IsNullOrWhiteSpace(coordinate))
                throw new LaunchException($"无法解析 Maven 坐标：{coordinate}");

            string[] parts = coordinate.Split(':');
            if (parts.Length < 3 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
                throw new LaunchException($"无法解析 Maven 坐标：{coordinate}");

            string group = parts[0];
            string artifact = parts[1];

            if (parts.Length == 3)
            {
                string[] ve = parts[2].Split('@');
                return new MavenCoordinate(group, artifact, ve[0], null, ve.Length > 1 ? ve[1] : "jar");
            }

            string[] de = parts[3].Split('@');
            return new MavenCoordinate(group, artifact, parts[2], de[0], de.Length > 1 ? de[1] : "jar");
        }

        public static string ToRelativePath(string coordinate)
        {
            MavenCoordinate c = Parse(coordinate);
            string dir = c.Group.Replace('.', '/');
            string file = string.IsNullOrEmpty(c.Classifier)
                ? $"{c.Artifact}-{c.Version}.{c.Extension}"
                : $"{c.Artifact}-{c.Version}-{c.Classifier}.{c.Extension}";
            return $"{dir}/{c.Artifact}/{c.Version}/{file}";
        }
    }

    /// <summary>Forge 安装器内路径的 zip-slip 防护（与 MojangRuntimeInstaller 同语义）。</summary>
    public static class ForgePathSafety
    {
        public static string SanitizeRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new LaunchException("检测到 Forge 安装器内非法路径：空路径。");

            string normalized = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

            if (Path.IsPathRooted(normalized))
                throw new LaunchException($"检测到 Forge 安装器内非法路径：{path}");

            foreach (string segment in normalized.Split(Path.DirectorySeparatorChar))
            {
                if (segment == ".." || segment == ".")
                    throw new LaunchException($"检测到 Forge 安装器内非法路径：{path}");
                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new LaunchException($"检测到 Forge 安装器内非法路径：{path}");
            }

            return normalized;
        }

        public static string ResolveUnder(string baseDir, string relativePath)
        {
            string fullBase = Path.GetFullPath(baseDir);
            string fullTarget = Path.GetFullPath(Path.Combine(fullBase, relativePath));

            string baseWithSep = fullBase.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? fullBase
                : fullBase + Path.DirectorySeparatorChar;

            if (!fullTarget.StartsWith(baseWithSep, StringComparison.OrdinalIgnoreCase))
                throw new LaunchException($"检测到 Forge 安装器内非法路径：{relativePath}");

            return fullTarget;
        }
    }
}
