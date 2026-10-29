using System;
using System.IO;

namespace CMUmcl.Content
{
    /// <summary>
    /// Shared path/hash helpers for pack installation. Keeps the filesystem-oriented
    /// path safety (throwing, Chinese messages) in one place alongside the pure hash
    /// helpers used across the full and incremental install paths.
    /// </summary>
    internal static class PackFileSystem
    {
        /// <summary>Lowercases and trims a hex string; returns empty for null/blank.</summary>
        public static string NormalizeHex(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
                return string.Empty;
            return hex.Trim().ToLowerInvariant();
        }

        /// <summary>Lowercase hex of a hash byte array.</summary>
        public static string ComputeHex(byte[] hash)
        {
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Canonicalizes a validated relative path to forward slashes (the on-disk/index
        /// key form). Does not validate; the caller must validate first.
        /// </summary>
        public static string NormalizeRelativePath(string path)
        {
            return path.Replace('\\', '/');
        }

        /// <summary>
        /// Validates and normalizes a relative path into the instance directory, rejecting
        /// rooted paths, traversal (<c>..</c>/<c>.</c>) and invalid file-name characters.
        /// Returns an OS-separated relative path; throws <see cref="ResourceException"/>.
        /// </summary>
        public static string SanitizeRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ResourceException("路径为空，无法安装。");

            // Normalize separators.
            string normalized = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

            if (Path.IsPathRooted(normalized))
                throw new ResourceException($"检测到非法路径：{path}");

            // Reject any traversal segment.
            string[] segments = normalized.Split(Path.DirectorySeparatorChar);
            foreach (string segment in segments)
            {
                if (segment == ".." || segment == ".")
                    throw new ResourceException($"检测到路径越界：{path}");
                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new ResourceException($"检测到非法路径字符：{path}");
            }

            return normalized;
        }

        /// <summary>
        /// Resolves <paramref name="relativePath"/> under <paramref name="baseDir"/> and
        /// verifies the result stays within the base directory. Throws
        /// <see cref="ResourceException"/> otherwise.
        /// </summary>
        public static string ResolveUnder(string baseDir, string relativePath)
        {
            string fullBase = Path.GetFullPath(baseDir);
            string fullTarget = Path.GetFullPath(Path.Combine(fullBase, relativePath));

            string baseWithSep = fullBase.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? fullBase
                : fullBase + Path.DirectorySeparatorChar;

            if (!fullTarget.StartsWith(baseWithSep, StringComparison.OrdinalIgnoreCase))
                throw new ResourceException($"目标路径超出实例目录：{relativePath}");

            return fullTarget;
        }
    }
}
