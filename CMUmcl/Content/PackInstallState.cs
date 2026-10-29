using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMUmcl.Content
{
    /// <summary>
    /// Per-instance local record of the last successfully applied pack state, stored as
    /// <c>.cmumcl-pack-manifest.json</c>. Enables incremental updates: the client compares
    /// the server's file index against this state plus the on-disk files.
    /// </summary>
    public sealed class PackInstallState
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
        public List<PackInstallStateFile> Files { get; set; } = new();
    }

    /// <summary>One managed file in the local state (path + hashes + size; no URLs).</summary>
    public sealed class PackInstallStateFile
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("sha1")]
        public string Sha1 { get; set; } = string.Empty;

        [JsonPropertyName("sha512")]
        public string Sha512 { get; set; } = string.Empty;

        [JsonPropertyName("fileSize")]
        public long FileSize { get; set; }
    }

    /// <summary>Loads/saves <see cref="PackInstallState"/> from an instance directory.</summary>
    public static class PackStateStore
    {
        /// <summary>State file name inside each instance directory.</summary>
        public const string FileName = ".cmumcl-pack-manifest.json";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

        /// <summary>
        /// Loads the state for an instance, or null when missing/corrupt. Best effort:
        /// any load/parse failure returns null and unsafe file entries are dropped.
        /// </summary>
        public static PackInstallState? Load(string instanceDir)
        {
            try
            {
                string path = Path.Combine(instanceDir, FileName);
                if (!File.Exists(path))
                    return null;

                var state = JsonSerializer.Deserialize<PackInstallState>(File.ReadAllText(path), JsonOptions);
                if (state is null)
                    return null;

                // Defensive: never trust persisted paths.
                state.Files = state.Files
                    .Where(f => PackFileIndexValidation.IsSafeRelativePath(f.Path))
                    .ToList();

                return state;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Atomically writes the state (temp file + move). Throws on IO failure.</summary>
        public static void Save(string instanceDir, PackInstallState state)
        {
            if (state is null) throw new ArgumentNullException(nameof(state));

            Directory.CreateDirectory(instanceDir);
            string path = Path.Combine(instanceDir, FileName);
            string json = JsonSerializer.Serialize(state, JsonOptions);

            string temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
    }
}
