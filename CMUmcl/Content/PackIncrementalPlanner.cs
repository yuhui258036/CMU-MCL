using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace CMUmcl.Content
{
    /// <summary>The outcome of planning an incremental update.</summary>
    internal sealed class IncrementalPlan
    {
        /// <summary>Files that must be downloaded (changed or new).</summary>
        public List<PackFileIndexEntry> Download { get; } = new();

        /// <summary>Managed files absent from the new index (should be deleted).</summary>
        public List<string> Delete { get; } = new();

        /// <summary>Files that are unchanged and valid on disk (skipped).</summary>
        public List<string> Skip { get; } = new();

        /// <summary>True when the incremental path cannot safely proceed; a full install is required.</summary>
        public bool NeedFullInstall { get; set; }

        /// <summary>Human-readable reason for <see cref="NeedFullInstall"/>.</summary>
        public string FallbackReason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Pure planner: computes which files to download/delete/skip for an incremental update
    /// given the previous local state, the new server index, and the on-disk files.
    /// </summary>
    internal static class PackIncrementalPlanner
    {
        public static IncrementalPlan Plan(PackInstallState state, PackFileIndex index, string instanceDir)
        {
            var plan = new IncrementalPlan();
            var newPaths = new HashSet<string>(StringComparer.Ordinal);

            foreach (PackFileIndexEntry entry in index.Files)
            {
                if (!PackFileIndexValidation.IsSafeRelativePath(entry.Path))
                {
                    plan.NeedFullInstall = true;
                    plan.FallbackReason = $"非法路径：{entry.Path}";
                    return plan;
                }

                string path = PackFileSystem.NormalizeRelativePath(entry.Path);
                newPaths.Add(path);

                string sha1 = PackFileSystem.NormalizeHex(entry.Hashes?.Sha1);
                string sha512 = PackFileSystem.NormalizeHex(entry.Hashes?.Sha512);

                if (FileMatches(instanceDir, path, entry.FileSize, sha1, sha512))
                {
                    plan.Skip.Add(path);
                    continue;
                }

                plan.Download.Add(entry);

                if (!HasHttpsUrl(entry.Downloads))
                {
                    plan.NeedFullInstall = true;
                    plan.FallbackReason = $"文件缺少 HTTPS 下载地址：{path}";
                    return plan;
                }

                if (sha1.Length == 0 && sha512.Length == 0)
                {
                    plan.NeedFullInstall = true;
                    plan.FallbackReason = $"文件缺少校验哈希：{path}";
                    return plan;
                }
            }

            // Delete managed files that are absent from the new index. User-added files
            // (not in state, not in index) are never touched.
            foreach (PackInstallStateFile oldFile in state.Files)
            {
                string path = PackFileSystem.NormalizeRelativePath(oldFile.Path);
                if (!newPaths.Contains(path))
                    plan.Delete.Add(path);
            }

            return plan;
        }

        private static bool HasHttpsUrl(List<string> downloads)
        {
            foreach (string url in downloads)
            {
                if (PackFileIndexValidation.IsAllowedDownloadUrl(url))
                    return true;
            }
            return false;
        }

        /// <summary>True when the on-disk file exists and matches the index entry's size + hashes.</summary>
        private static bool FileMatches(string instanceDir, string relPath, long fileSize, string sha1, string sha512)
        {
            try
            {
                string target = PackFileSystem.ResolveUnder(instanceDir, relPath);
                if (!File.Exists(target))
                    return false;

                var info = new FileInfo(target);
                if (fileSize > 0 && info.Length != fileSize)
                    return false;

                byte[] bytes = File.ReadAllBytes(target);

                if (sha512.Length > 0 &&
                    !string.Equals(PackFileSystem.ComputeHex(SHA512.HashData(bytes)), sha512, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (sha1.Length > 0 &&
                    !string.Equals(PackFileSystem.ComputeHex(SHA1.HashData(bytes)), sha1, StringComparison.OrdinalIgnoreCase))
                    return false;

                // A file with no usable hash can never be considered "valid"; it must be re-downloaded.
                return sha1.Length > 0 || sha512.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
