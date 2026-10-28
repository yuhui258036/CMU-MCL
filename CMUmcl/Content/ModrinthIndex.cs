using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CMUmcl.Content
{
    /// <summary>Parsed <c>modrinth.index.json</c> inside a .mrpack archive.</summary>
    public sealed class ModrinthIndex
    {
        [JsonPropertyName("formatVersion")]
        public int FormatVersion { get; set; }

        [JsonPropertyName("game")]
        public string Game { get; set; } = string.Empty;

        [JsonPropertyName("versionId")]
        public string VersionId { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("files")]
        public List<ModrinthFile> Files { get; set; } = new();
    }

    /// <summary>A single downloadable file listed in modrinth.index.json.</summary>
    public sealed class ModrinthFile
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("hashes")]
        public ModrinthHashes Hashes { get; set; } = new();

        [JsonPropertyName("downloads")]
        public List<string> Downloads { get; set; } = new();

        [JsonPropertyName("fileSize")]
        public long? FileSize { get; set; }
    }

    public sealed class ModrinthHashes
    {
        [JsonPropertyName("sha512")]
        public string Sha512 { get; set; } = string.Empty;

        [JsonPropertyName("sha1")]
        public string Sha1 { get; set; } = string.Empty;
    }
}
