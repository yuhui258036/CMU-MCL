using System;
using System.IO;
using System.Text.Json;

namespace CMUmcl.Content
{
    /// <summary>
    /// Loads and saves <see cref="AppSettings"/> to <c>%LOCALAPPDATA%\CMUmcl\settings.json</c>.
    /// Returns defaults on a missing/corrupt file, sanitizes loaded values, and writes atomically.
    /// </summary>
    public sealed class SettingsStore
    {
        private static readonly object FileLock = new();

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

        /// <summary>Safety ceiling (1 TB) so an absurd persisted value resets to the default.</summary>
        private const int MaxMemoryCap = 1024 * 1024;

        /// <summary>The most recently loaded/saved settings, seeded with defaults before first load.</summary>
        public static AppSettings Current { get; private set; } = CreateDefaults();

        public AppSettings Load()
        {
            lock (FileLock)
            {
                AppSettings settings;
                try
                {
                    if (!File.Exists(LauncherPaths.SettingsPath))
                        return Current = CreateDefaults();

                    string json = File.ReadAllText(LauncherPaths.SettingsPath);
                    settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                }
                catch
                {
                    settings = new AppSettings();
                }

                Sanitize(settings);
                Current = settings;
                return settings;
            }
        }

        /// <summary>Sanitizes and atomically persists <paramref name="settings"/>; returns true on success.</summary>
        public bool Save(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            Sanitize(settings);

            lock (FileLock)
            {
                try
                {
                    Directory.CreateDirectory(LauncherPaths.RootDirectory);
                    string json = JsonSerializer.Serialize(settings, JsonOptions);

                    // Atomic write: write to a temp file then move over the target.
                    string temp = LauncherPaths.SettingsPath + ".tmp";
                    File.WriteAllText(temp, json);
                    File.Move(temp, LauncherPaths.SettingsPath, overwrite: true);

                    Current = settings;
                    return true;
                }
                catch
                {
                    // Non-fatal; settings are reloaded from the previous file next run.
                    return false;
                }
            }
        }

        public static AppSettings CreateDefaults()
        {
            var settings = new AppSettings { AutoMemory = true };
            Sanitize(settings);
            return settings;
        }

        /// <summary>Restores safe values after a load or before a save.</summary>
        public static void Sanitize(AppSettings settings)
        {
            if (!Enum.IsDefined(settings.ProxyMode)) settings.ProxyMode = LauncherProxyMode.System;
            settings.ProxyHost = settings.ProxyHost?.Trim() ?? "";
            settings.ProxyUsername ??= "";
            settings.ProxyPasswordProtected ??= "";
            if (settings.JavaPath is string p && string.IsNullOrWhiteSpace(p))
                settings.JavaPath = null;

            if (settings.MaxMemoryMb < 512 || settings.MaxMemoryMb > MaxMemoryCap)
                settings.MaxMemoryMb = 4096;

            if (!Enum.IsDefined(typeof(VersionIsolation), settings.VersionIsolation))
                settings.VersionIsolation = VersionIsolation.ModdedOnly;

            settings.InstancesDirectory = LauncherPaths.NormalizeInstancesDirectory(settings.InstancesDirectory);
            if (!Enum.IsDefined(settings.Theme)) settings.Theme = LauncherTheme.System;
            if (!double.IsFinite(settings.BackgroundOpacity)) settings.BackgroundOpacity = 0.25;
            settings.BackgroundOpacity = Math.Clamp(settings.BackgroundOpacity, 0.05, 1);
            if (string.IsNullOrWhiteSpace(settings.BackgroundPath)) settings.BackgroundPath = null;
            else
            {
                try { settings.BackgroundPath = Path.IsPathFullyQualified(settings.BackgroundPath)
                    ? Path.GetFullPath(settings.BackgroundPath) : null; }
                catch { settings.BackgroundPath = null; }
            }
            settings.BackgroundImages ??= [];
            if (settings.BackgroundMode == null)
            {
                settings.BackgroundMode = settings.BackgroundPath == null ? BackgroundKind.Default : BackgroundKind.Local;
                if (settings.BackgroundPath != null && settings.BackgroundImages.Count == 0)
                    settings.BackgroundImages.Add(new(settings.BackgroundPath, "背景图片"));
            }
            settings.BackgroundPath = null; // One-time migration of the previous single-image setting.
            if (!Enum.IsDefined(settings.BackgroundMode.Value)) settings.BackgroundMode = BackgroundKind.Default;
            settings.BackgroundImages = settings.BackgroundImages.Where(i => i != null && !string.IsNullOrWhiteSpace(i.Path) && Path.IsPathFullyQualified(i.Path))
                .DistinctBy(i => i.Path, StringComparer.OrdinalIgnoreCase).Take(50)
                .Select(i => i with { Name = string.IsNullOrWhiteSpace(i.Name) ? "背景图片" : i.Name }).ToList();
            settings.BackgroundUrl = settings.BackgroundUrl?.Trim() ?? "";
            settings.BackgroundIntervalSeconds = Math.Clamp(settings.BackgroundIntervalSeconds, 10, 3600);
        }
    }
}
