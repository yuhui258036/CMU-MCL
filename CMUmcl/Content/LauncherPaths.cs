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

        public static string InstancesDirectory => Path.Combine(RootDirectory, "instances");

        public static string InstalledStatePath => Path.Combine(RootDirectory, "installed.json");

        /// <summary>Shared Minecraft directory (versions/libraries/assets/runtime).</summary>
        public static string MinecraftDirectory => Path.Combine(RootDirectory, "minecraft");

        /// <summary>Runtime copy of the authlib-injector jar (verified by SHA-256).</summary>
        public static string AuthlibInjectorPath => Path.Combine(RootDirectory, "authlib-injector.jar");

        /// <summary>Java runtime root directory (%LOCALAPPDATA%\CMUmcl\java).</summary>
        public static string JavaDirectory => Path.Combine(RootDirectory, "java");

        /// <summary>Auto-downloaded Mojang Java runtime directory (%LOCALAPPDATA%\CMUmcl\java\runtime).</summary>
        public static string JavaRuntimeDirectory => Path.Combine(JavaDirectory, "runtime");

        /// <summary>Creates and returns a fresh staging directory under the runtime dir.</summary>
        public static string CreateStagingDirectory()
        {
            string dir = Path.Combine(JavaRuntimeDirectory, ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
