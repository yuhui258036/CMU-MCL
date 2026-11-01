using System.Text.Json.Serialization;

namespace CMUmcl.Content
{
    public enum LauncherTheme { System, Light, Dark }
    public enum BackgroundKind { Default, Local, Network }
    public enum LauncherProxyMode { System, Direct, Http, Socks5 }
    public sealed record BackgroundImageEntry(string Path, string Name);
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
        [JsonPropertyName("proxyMode")]
        public LauncherProxyMode ProxyMode { get; set; }
        [JsonPropertyName("proxyHost")]
        public string ProxyHost { get; set; } = "";
        [JsonPropertyName("proxyPort")]
        public int ProxyPort { get; set; } = 8080;
        [JsonPropertyName("proxyAuthentication")]
        public bool ProxyAuthentication { get; set; }
        [JsonPropertyName("proxyUsername")]
        public string ProxyUsername { get; set; } = "";
        [JsonPropertyName("proxyPasswordProtected")]
        public string ProxyPasswordProtected { get; set; } = "";

        /// <summary>Manual javaw.exe path. Null/empty means auto-select (or auto-download).</summary>
        [JsonPropertyName("javaPath")]
        public string? JavaPath { get; set; }

        /// <summary>Maximum heap size in megabytes. A minimum of 512 is enforced at launch.</summary>
        [JsonPropertyName("maxMemoryMb")]
        public int MaxMemoryMb { get; set; } = 4096;

        // False when absent in an existing settings file: preserve previous manual allocation.
        [JsonPropertyName("autoMemory")]
        public bool AutoMemory { get; set; }

        [JsonPropertyName("theme")]
        public LauncherTheme Theme { get; set; } = LauncherTheme.System;

        [JsonPropertyName("backgroundPath")]
        public string? BackgroundPath { get; set; }

        [JsonPropertyName("backgroundOpacity")]
        public double BackgroundOpacity { get; set; } = 0.25;

        [JsonPropertyName("backgroundMode")]
        public BackgroundKind? BackgroundMode { get; set; }
        [JsonPropertyName("backgroundImages")]
        public List<BackgroundImageEntry> BackgroundImages { get; set; } = [];
        [JsonPropertyName("backgroundUrl")]
        public string BackgroundUrl { get; set; } = "";
        [JsonPropertyName("backgroundAutoSwitch")]
        public bool BackgroundAutoSwitch { get; set; }
        [JsonPropertyName("backgroundIntervalSeconds")]
        public int BackgroundIntervalSeconds { get; set; } = 60;
        [JsonPropertyName("backgroundShuffle")]
        public bool BackgroundShuffle { get; set; } = true;

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
