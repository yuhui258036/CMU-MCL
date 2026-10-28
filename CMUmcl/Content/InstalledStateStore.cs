using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CMUmcl.Content
{
    /// <summary>
    /// Persists the locally-installed revision per instance id to
    /// <c>%LOCALAPPDATA%\CMUmcl\installed.json</c> (plain JSON — not secret).
    /// </summary>
    public sealed class InstalledStateStore
    {
        private static readonly object FileLock = new();

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

        private sealed class StateFile
        {
            public Dictionary<string, int> Instances { get; set; } = new();
        }

        public int? GetInstalledRevision(string id)
        {
            lock (FileLock)
            {
                var state = Load();
                if (state.Instances.TryGetValue(id, out var revision))
                    return revision;
                return null;
            }
        }

        public void SetInstalledRevision(string id, int revision)
        {
            lock (FileLock)
            {
                var state = Load();
                state.Instances[id] = revision;
                Save(state);
            }
        }

        private static StateFile Load()
        {
            try
            {
                if (!File.Exists(LauncherPaths.InstalledStatePath))
                    return new StateFile();

                string json = File.ReadAllText(LauncherPaths.InstalledStatePath);
                return JsonSerializer.Deserialize<StateFile>(json, JsonOptions) ?? new StateFile();
            }
            catch
            {
                return new StateFile();
            }
        }

        private static void Save(StateFile state)
        {
            try
            {
                Directory.CreateDirectory(LauncherPaths.RootDirectory);
                string json = JsonSerializer.Serialize(state, JsonOptions);
                File.WriteAllText(LauncherPaths.InstalledStatePath, json);
            }
            catch
            {
                // Non-fatal; installation metadata may be re-derived next run.
            }
        }
    }
}
