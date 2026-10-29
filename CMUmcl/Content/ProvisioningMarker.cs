using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMUmcl.Content
{
    /// <summary>
    /// Per-instance provisioning marker written to <c>{instanceDir}\.cmumcl-provisioned.json</c>
    /// after a successful base/loader install. <see cref="Launch.LaunchService"/> uses it to skip
    /// re-downloading an already-provisioned game/loader on subsequent launches.
    /// </summary>
    public static class ProvisioningMarker
    {
        /// <summary>Marker file name inside an instance directory.</summary>
        public const string FileName = ".cmumcl-provisioned.json";

        /// <summary>Launcher version stamped into markers (informational only).</summary>
        public const string AppVersion = "0.1.0";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

        private sealed class MarkerData
        {
            [JsonPropertyName("signature")]
            public string Signature { get; set; } = string.Empty;

            [JsonPropertyName("versionName")]
            public string VersionName { get; set; } = string.Empty;

            [JsonPropertyName("provisionedAt")]
            public DateTimeOffset ProvisionedAt { get; set; }

            [JsonPropertyName("appVersion")]
            public string AppVersion { get; set; } = string.Empty;
        }

        /// <summary>Full path to the marker file for an instance directory.</summary>
        public static string GetPath(string instanceDir) => Path.Combine(instanceDir, FileName);

        /// <summary>
        /// Computes the provisioning signature for a game/loader/loaderVersion triple.
        /// The loader is normalized so <c>""</c> and <c>vanilla</c> are equivalent.
        /// </summary>
        public static string ComputeSignature(string? game, string? loader, string? loaderVersion)
        {
            string normalizedGame = (game ?? string.Empty).Trim();
            string normalizedLoader = NormalizeLoaderKey(loader);
            string normalizedLoaderVersion = (loaderVersion ?? string.Empty).Trim();
            return $"{normalizedGame}|{normalizedLoader}|{normalizedLoaderVersion}";
        }

        /// <summary>Normalizes a loader name to its key form: empty for vanilla/blank, otherwise lowercase trimmed.</summary>
        public static string NormalizeLoaderKey(string? loader)
        {
            string normalized = (loader ?? string.Empty).Trim().ToLowerInvariant();
            return normalized is "" or "vanilla" ? string.Empty : normalized;
        }

        /// <summary>
        /// Returns true when the marker exists, its signature matches, and the resolved version JSON
        /// (and its version directory) exist under <paramref name="minecraftDirectory"/>. On success,
        /// <paramref name="versionName"/> receives the marker's resolved version name.
        /// </summary>
        public static bool TryReadReady(
            string instanceDir,
            string minecraftDirectory,
            string signature,
            out string versionName)
        {
            versionName = string.Empty;

            string markerPath = GetPath(instanceDir);
            if (!File.Exists(markerPath))
                return false;

            MarkerData? marker;
            try
            {
                marker = JsonSerializer.Deserialize<MarkerData>(File.ReadAllText(markerPath), JsonOptions);
            }
            catch
            {
                return false;
            }

            if (marker == null
                || !string.Equals(marker.Signature, signature, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(marker.VersionName))
            {
                return false;
            }

            if (!VersionJsonExists(minecraftDirectory, marker.VersionName))
                return false;

            versionName = marker.VersionName;
            return true;
        }

        /// <summary>
        /// True when the version directory and its <c>&lt;versionName&gt;.json</c> exist under
        /// <c>&lt;minecraftDirectory&gt;\versions\&lt;versionName&gt;</c>.
        /// </summary>
        public static bool VersionJsonExists(string minecraftDirectory, string versionName)
        {
            if (string.IsNullOrWhiteSpace(versionName))
                return false;

            // Version names are plain identifiers; refuse anything that could escape the versions dir.
            if (versionName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || string.Equals(versionName, ".", StringComparison.Ordinal)
                || string.Equals(versionName, "..", StringComparison.Ordinal))
            {
                return false;
            }

            string versionDir = Path.Combine(minecraftDirectory, "versions", versionName);
            string jsonPath = Path.Combine(versionDir, versionName + ".json");
            return Directory.Exists(versionDir) && File.Exists(jsonPath);
        }

        /// <summary>
        /// Atomically writes the marker. Best-effort: returns false (rather than throwing) when the
        /// instance directory is missing or unwritable, so a launch is never blocked by the marker.
        /// </summary>
        public static bool TryWrite(string instanceDir, string signature, string versionName)
        {
            try
            {
                if (!Directory.Exists(instanceDir))
                    return false;

                var marker = new MarkerData
                {
                    Signature = signature,
                    VersionName = versionName,
                    ProvisionedAt = DateTimeOffset.UtcNow,
                    AppVersion = AppVersion,
                };

                string json = JsonSerializer.Serialize(marker, JsonOptions);
                string markerPath = GetPath(instanceDir);
                string tempPath = markerPath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, markerPath, overwrite: true);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
