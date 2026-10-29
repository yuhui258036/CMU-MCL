using System;
using System.IO;

namespace CMUmcl.Content
{
    /// <summary>Central location for the launcher's on-disk state under %LOCALAPPDATA%\CMUmcl.</summary>
    public static class LauncherPaths
    {
        public static string RootDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CMUmcl");

        public static string CacheDirectory => Path.Combine(RootDirectory, "cache");

        public static string PackCacheDirectory => Path.Combine(CacheDirectory, "packs");

        /// <summary>Default instance storage directory (<c>%LOCALAPPDATA%\CMUmcl\instances</c>).</summary>
        public static string DefaultInstancesDirectory => Path.Combine(RootDirectory, "instances");

        /// <summary>
        /// The active instance storage directory: the user-configured path when set and valid,
        /// otherwise the default. Backed by <see cref="SettingsStore.Current"/>.
        /// </summary>
        public static string InstancesDirectory
        {
            get
            {
                string? configured = SettingsStore.Current.InstancesDirectory;
                return string.IsNullOrWhiteSpace(configured) ? DefaultInstancesDirectory : configured!;
            }
        }

        public static string InstalledStatePath => Path.Combine(RootDirectory, "installed.json");

        /// <summary>User settings file (%LOCALAPPDATA%\CMUmcl\settings.json).</summary>
        public static string SettingsPath => Path.Combine(RootDirectory, "settings.json");

        /// <summary>Shared game directory used when version isolation is Off (or for vanilla instances).</summary>
        public static string GameDirectory => Path.Combine(RootDirectory, "game");

        /// <summary>Log output directory (%LOCALAPPDATA%\CMUmcl\logs).</summary>
        public static string LogsDirectory => Path.Combine(RootDirectory, "logs");

        /// <summary>Shared Minecraft directory (versions/libraries/assets/runtime).</summary>
        public static string MinecraftDirectory => Path.Combine(RootDirectory, "minecraft");

        /// <summary>Runtime copy of the authlib-injector jar (verified by SHA-256).</summary>
        public static string AuthlibInjectorPath => Path.Combine(RootDirectory, "authlib-injector.jar");

        /// <summary>Java runtime root directory (%LOCALAPPDATA%\CMUmcl\java).</summary>
        public static string JavaDirectory => Path.Combine(RootDirectory, "java");

        /// <summary>Auto-downloaded Mojang Java runtime directory (%LOCALAPPDATA%\CMUmcl\java\runtime).</summary>
        public static string JavaRuntimeDirectory => Path.Combine(JavaDirectory, "runtime");

        /// <summary>
        /// Creates the launcher's root directory and all standard subdirectories.
        /// Safe to call repeatedly; does not touch or recreate the existing root junction.
        /// </summary>
        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(RootDirectory);
            Directory.CreateDirectory(CacheDirectory);
            Directory.CreateDirectory(PackCacheDirectory);
            Directory.CreateDirectory(InstancesDirectory);
            Directory.CreateDirectory(MinecraftDirectory);
            Directory.CreateDirectory(JavaDirectory);
            Directory.CreateDirectory(LogsDirectory);
            Directory.CreateDirectory(GameDirectory);
        }

        /// <summary>Creates and returns a fresh staging directory under the runtime dir.</summary>
        public static string CreateStagingDirectory()
        {
            string dir = Path.Combine(JavaRuntimeDirectory, ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// Normalizes a user-supplied instance storage directory to a rooted, absolute path, or
        /// returns null when it is blank, relative, invalid, or overlaps a launcher-internal
        /// directory (minecraft/cache/java/game/logs), the root directory itself, or an ancestor
        /// of the root directory.
        /// </summary>
        public static string? NormalizeInstancesDirectory(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            string full;
            try
            {
                full = Path.GetFullPath(input.Trim());
            }
            catch (Exception)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(full) || !Path.IsPathRooted(full))
                return null;

            // The chosen directory must not be (or sit inside) a reserved state directory.
            if (string.Equals(full, RootDirectory, StringComparison.OrdinalIgnoreCase))
                return null;

            if (IsSameOrUnder(full, MinecraftDirectory)
                || IsSameOrUnder(full, CacheDirectory)
                || IsSameOrUnder(full, JavaDirectory)
                || IsSameOrUnder(full, GameDirectory)
                || IsSameOrUnder(full, LogsDirectory))
            {
                return null;
            }

            // Nor may it be an ancestor of the root directory (it would swallow all state).
            if (IsSameOrUnder(RootDirectory, full))
                return null;

            return full;
        }

        /// <summary>Case-insensitive check that <paramref name="path"/> equals or lives under <paramref name="parent"/>.</summary>
        public static bool IsSameOrUnder(string path, string parent)
        {
            string child = EnsureTrailingSeparator(Path.GetFullPath(path));
            string root = EnsureTrailingSeparator(Path.GetFullPath(parent));
            return child.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static string EnsureTrailingSeparator(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            if (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
                return path;

            return path + Path.DirectorySeparatorChar;
        }
    }
}
