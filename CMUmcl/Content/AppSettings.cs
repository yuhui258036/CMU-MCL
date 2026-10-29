using System.Text.Json.Serialization;

namespace CMUmcl.Content
{
    /// <summary>How instances choose their game directory relative to shared storage.</summary>
    public enum VersionIsolation
    {
        /// <summary>All instances share a single game directory.</summary>
        Off,

        /// <summary>Modded (non-vanilla) instances use their own directory; vanilla share.</summary>
        ModdedOnly,

        /// <summary>Every instance uses its own directory.</summary>
        All,
    }

    /// <summary>User-configurable launcher settings persisted to settings.json.</summary>
    public sealed class AppSettings
    {
        /// <summary>Manual javaw.exe path. Null/empty means auto-select (or auto-download).</summary>
        [JsonPropertyName("javaPath")]
        public string? JavaPath { get; set; }

        /// <summary>Maximum heap size in megabytes. A minimum of 512 is enforced at launch.</summary>
        [JsonPropertyName("maxMemoryMb")]
        public int MaxMemoryMb { get; set; } = 4096;

        [JsonPropertyName("versionIsolation")]
        public VersionIsolation VersionIsolation { get; set; } = VersionIsolation.ModdedOnly;

        /// <summary>Stored now; auto-update behavior is implemented in a later batch.</summary>
        [JsonPropertyName("autoUpdatePacks")]
        public bool AutoUpdatePacks { get; set; } = true;

        /// <summary>
        /// Explicit directory where installed instances live. Null/empty means the default
        /// (<c>%LOCALAPPDATA%\CMUmcl\instances</c>). Sanitized to an absolute path on load/save.
        /// </summary>
        [JsonPropertyName("instancesDirectory")]
        public string? InstancesDirectory { get; set; }
    }
}
