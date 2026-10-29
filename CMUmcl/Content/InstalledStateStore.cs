using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMUmcl.Content
{
    /// <summary>One locally-installed instance's record (revision + on-disk location + display name).</summary>
    public sealed class InstanceRecord
    {
        [JsonPropertyName("revision")]
        public int Revision { get; set; }

        /// <summary>Install directory name, relative to the active instances root. Null means the id.</summary>
        [JsonPropertyName("installPath")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? InstallPath { get; set; }

        /// <summary>User-facing name override (used by cards when set). Null means "use manifest name".</summary>
        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DisplayName { get; set; }

        public InstanceRecord Clone() => new() { Revision = Revision, InstallPath = InstallPath, DisplayName = DisplayName };
    }

    /// <summary>
    /// Persists installed-instance records to <c>%LOCALAPPDATA%\CMUmcl\installed.json</c>.
    /// Supports the legacy format <c>{ "Instances": { "id": revision } }</c> and the v2 format
    /// <c>{ "Instances": { "id": { "revision": n, "installPath": "...", "displayName": "..." } } }</c>.
    /// </summary>
    public sealed class InstalledStateStore
    {
        private static readonly object FileLock = new();

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

        /// <summary>On-disk shape for serialization; reads are done manually to support the legacy format.</summary>
        private sealed class StateFile
        {
            public Dictionary<string, InstanceRecord> Instances { get; set; } = new();
        }

        public int? GetInstalledRevision(string id)
        {
            lock (FileLock)
            {
                if (Load().Instances.TryGetValue(id, out var record))
                    return record.Revision;
                return null;
            }
        }

        /// <summary>Returns the recorded install directory name, or the id when no path was recorded.</summary>
        public string GetInstallPath(string id)
        {
            lock (FileLock)
            {
                if (Load().Instances.TryGetValue(id, out var record) && !string.IsNullOrWhiteSpace(record.InstallPath))
                    return record.InstallPath!;
                return id;
            }
        }

        /// <summary>Returns the recorded display name, or null when none was set.</summary>
        public string? GetDisplayName(string id)
        {
            lock (FileLock)
            {
                if (Load().Instances.TryGetValue(id, out var record) && !string.IsNullOrWhiteSpace(record.DisplayName))
                    return record.DisplayName;
                return null;
            }
        }

        /// <summary>Returns a snapshot of all records (id → record).</summary>
        public IReadOnlyDictionary<string, InstanceRecord> ListRecords()
        {
            lock (FileLock)
            {
                return Load().Instances.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
            }
        }

        /// <summary>Records (or updates) the install path, display name and revision for one instance.</summary>
        public void RecordInstall(string id, int revision, string installPath, string? displayName)
        {
            lock (FileLock)
            {
                var state = Load();
                state.Instances[id] = new InstanceRecord
                {
                    Revision = revision,
                    InstallPath = string.IsNullOrWhiteSpace(installPath) ? null : installPath,
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
                };
                Save(state);
            }
        }

        /// <summary>Atomically replaces the full record set (used after an instance-directory migration).</summary>
        public bool ApplyMigration(IReadOnlyDictionary<string, InstanceRecord> records)
        {
            lock (FileLock)
            {
                var state = Load();
                state.Instances = records.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
                return Save(state);
            }
        }

        /// <summary>Backward-compatible helper: sets only the revision, preserving any recorded path/name.</summary>
        public void SetInstalledRevision(string id, int revision)
        {
            lock (FileLock)
            {
                var state = Load();
                InstanceRecord record = state.Instances.TryGetValue(id, out var existing)
                    ? existing
                    : new InstanceRecord();
                record.Revision = revision;
                state.Instances[id] = record;
                Save(state);
            }
        }

        // ------------------------------------------------------------------
        // Load/save (legacy-compatible read; atomic write)
        // ------------------------------------------------------------------

        private static StateFile Load()
        {
            try
            {
                if (!File.Exists(LauncherPaths.InstalledStatePath))
                    return new StateFile();

                string json = File.ReadAllText(LauncherPaths.InstalledStatePath);
                return Parse(json);
            }
            catch
            {
                return new StateFile();
            }
        }

        /// <summary>Parses either the legacy (int values) or v2 (object values) format.</summary>
        private static StateFile Parse(string json)
        {
            var state = new StateFile();

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Instances", out var instances) || instances.ValueKind != JsonValueKind.Object)
                return state;

            foreach (var property in instances.EnumerateObject())
            {
                JsonElement value = property.Value;
                switch (value.ValueKind)
                {
                    case JsonValueKind.Number:
                        state.Instances[property.Name] = new InstanceRecord { Revision = value.GetInt32() };
                        break;

                    case JsonValueKind.Object:
                        int revision = 0;
                        if (value.TryGetProperty("revision", out var rev) && rev.ValueKind == JsonValueKind.Number)
                            revision = rev.GetInt32();

                        string? path = null;
                        if (value.TryGetProperty("installPath", out var p) && p.ValueKind == JsonValueKind.String)
                            path = p.GetString();

                        string? name = null;
                        if (value.TryGetProperty("displayName", out var n) && n.ValueKind == JsonValueKind.String)
                            name = n.GetString();

                        state.Instances[property.Name] = new InstanceRecord
                        {
                            Revision = revision,
                            InstallPath = string.IsNullOrWhiteSpace(path) ? null : path,
                            DisplayName = string.IsNullOrWhiteSpace(name) ? null : name,
                        };
                        break;

                    default:
                        // Unknown shape — skip rather than corrupt the map.
                        break;
                }
            }

            return state;
        }

        private static bool Save(StateFile state)
        {
            try
            {
                Directory.CreateDirectory(LauncherPaths.RootDirectory);
                string json = JsonSerializer.Serialize(state, JsonOptions);

                // Atomic write: temp file then move over the target.
                string temp = LauncherPaths.InstalledStatePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, LauncherPaths.InstalledStatePath, overwrite: true);
                return true;
            }
            catch
            {
                // Non-fatal; installation metadata may be re-derived next run.
                return false;
            }
        }
    }
}
