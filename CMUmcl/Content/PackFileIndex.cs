using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMUmcl.Content
{
    /// <summary>
    /// Client view of the per-revision incremental file index
    /// (<c>&lt;rev&gt;.files.json</c>) served next to the .mrpack. Field names match the
    /// server's camelCase shape. Its SHA-256 is covered by the signed manifest's
    /// <c>files_sha256</c>.
    /// </summary>
    public sealed class PackFileIndex
    {
        [JsonPropertyName("formatVersion")]
        public int FormatVersion { get; set; } = 1;

        [JsonPropertyName("revision")]
        public int Revision { get; set; }

        [JsonPropertyName("packSha256")]
        public string PackSha256 { get; set; } = string.Empty;

        [JsonPropertyName("overridesSha256")]
        public string OverridesSha256 { get; set; } = string.Empty;

        [JsonPropertyName("files")]
        public List<PackFileIndexEntry> Files { get; set; } = new();
    }

    /// <summary>One downloadable file in the incremental index.</summary>
    public sealed class PackFileIndexEntry
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("downloads")]
        public List<string> Downloads { get; set; } = new();

        [JsonPropertyName("hashes")]
        public PackFileIndexHashes Hashes { get; set; } = new();

        [JsonPropertyName("fileSize")]
        public long FileSize { get; set; }
    }

    /// <summary>Hashes for a single index entry (sha1/sha512, nullable).</summary>
    public sealed class PackFileIndexHashes
    {
        [JsonPropertyName("sha1")]
        public string? Sha1 { get; set; }

        [JsonPropertyName("sha512")]
        public string? Sha512 { get; set; }
    }

    /// <summary>
    /// Pure validation/parsing helpers for the incremental file index. Mirrors the server's
    /// <c>FileIndexBuilder.IsSafeRelativePath</c> and HTTPS-only download rules.
    /// </summary>
    public static class PackFileIndexValidation
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        /// <summary>
        /// Rejects empty, rooted (leading <c>/</c> or <c>\</c>), Windows drive-letter, or
        /// traversal (<c>..</c>, <c>.</c>, empty segment) relative paths.
        /// </summary>
        public static bool IsSafeRelativePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            // Rooted (POSIX or Windows).
            if (path[0] == '/' || path[0] == '\\')
                return false;

            // Windows drive-letter absolute path, e.g. "C:" or "c:foo".
            if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
                return false;

            foreach (var segment in path.Split('/', '\\'))
            {
                if (segment.Length == 0 || segment == "." || segment == "..")
                    return false;
            }

            return true;
        }

        /// <summary>Absolute HTTPS URLs only (per-file download URLs).</summary>
        public static bool IsAllowedDownloadUrl(string? url)
        {
            return !string.IsNullOrWhiteSpace(url)
                && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Absolute HTTPS URLs only (the files_url in the manifest).</summary>
        public static bool IsAllowedFilesUrl(string? url) => IsAllowedDownloadUrl(url);

        /// <summary>Parses index bytes into a <see cref="PackFileIndex"/>; false on malformed input.</summary>
        public static bool TryParse(byte[] json, out PackFileIndex? index, out string? error)
        {
            index = null;
            error = null;

            try
            {
                index = JsonSerializer.Deserialize<PackFileIndex>(json, JsonOptions);
                if (index is null)
                {
                    error = "内容为空";
                    return false;
                }

                if (index.FormatVersion != 1)
                {
                    error = $"不支持的格式版本：{index.FormatVersion}";
                    return false;
                }

                return true;
            }
            catch (JsonException ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
