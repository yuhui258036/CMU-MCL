using System;
using System.Text.Json.Serialization;

namespace CMUmcl.Content
{
    /// <summary>Metadata written into each installed instance directory as instance.json.</summary>
    public sealed class InstanceMetadata
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("revision")]
        public int Revision { get; set; }

        [JsonPropertyName("game")]
        public string Game { get; set; } = string.Empty;

        [JsonPropertyName("loader")]
        public string Loader { get; set; } = string.Empty;

        [JsonPropertyName("loaderVersion")]
        public string LoaderVersion { get; set; } = string.Empty;

        [JsonPropertyName("serverAddress")]
        public string ServerAddress { get; set; } = string.Empty;

        [JsonPropertyName("serverPort")]
        public int ServerPort { get; set; }

        [JsonPropertyName("packSha512")]
        public string PackSha512 { get; set; } = string.Empty;

        /// <summary>SHA-256 of the installed .mrpack; used to detect a stale incremental state.</summary>
        [JsonPropertyName("packSha256")]
        public string PackSha256 { get; set; } = string.Empty;

        [JsonPropertyName("installedAt")]
        public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
