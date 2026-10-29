using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CMUmcl.Content
{
    /// <summary>Top-level resource manifest served by the resource site.</summary>
    public sealed class ResourceManifest
    {
        [JsonPropertyName("formatVersion")]
        public int FormatVersion { get; set; }

        [JsonPropertyName("instances")]
        public List<ManifestInstance> Instances { get; set; } = new();
    }

    /// <summary>One managed server pack instance in the manifest.</summary>
    public sealed class ManifestInstance
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("icon_url")]
        public string? IconUrl { get; set; }

        [JsonPropertyName("sort")]
        public int? Sort { get; set; }

        [JsonPropertyName("revision")]
        public int Revision { get; set; }

        [JsonPropertyName("required")]
        public bool Required { get; set; }

        [JsonPropertyName("minecraft")]
        public ManifestMinecraft Minecraft { get; set; } = new();

        [JsonPropertyName("pack")]
        public ManifestPack Pack { get; set; } = new();

        [JsonPropertyName("server")]
        public ManifestServer Server { get; set; } = new();
    }

    public sealed class ManifestMinecraft
    {
        [JsonPropertyName("game_version")]
        public string GameVersion { get; set; } = string.Empty;

        [JsonPropertyName("loader")]
        public string Loader { get; set; } = string.Empty;

        [JsonPropertyName("loader_version")]
        public string LoaderVersion { get; set; } = string.Empty;
    }

    public sealed class ManifestPack
    {
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("sha1")]
        public string Sha1 { get; set; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [JsonPropertyName("sha512")]
        public string Sha512 { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        /// <summary>URL of the per-revision incremental file index (<c>&lt;rev&gt;.files.json</c>), or null for old revisions.</summary>
        [JsonPropertyName("files_url")]
        public string? FilesUrl { get; set; }

        /// <summary>Lowercase hex SHA-256 of the file index bytes; null when no index exists.</summary>
        [JsonPropertyName("files_sha256")]
        public string? FilesSha256 { get; set; }
    }

    public sealed class ManifestServer
    {
        [JsonPropertyName("address")]
        public string Address { get; set; } = string.Empty;

        [JsonPropertyName("port")]
        public int Port { get; set; }
    }

    /// <summary>Detached signature for the manifest JSON.</summary>
    public sealed class ManifestSignature
    {
        [JsonPropertyName("algorithm")]
        public string Algorithm { get; set; } = string.Empty;

        [JsonPropertyName("keyId")]
        public string? KeyId { get; set; }

        [JsonPropertyName("signature")]
        public string Signature { get; set; } = string.Empty;
    }
}
