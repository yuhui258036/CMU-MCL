using System.Diagnostics;
using System.Reflection;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.VersionLoader;
using CMUmcl.Content;
using CMUmcl.Launch;

internal static partial class Program
{
    // A child process emulates mods which use relative config and log paths.
    private static int RelativeFilesProbe()
    {
        string value = File.ReadAllText(Path.Combine("config", "fancymenu", "customizablemenus.txt"));
        Directory.CreateDirectory("logs");
        File.WriteAllText(Path.Combine("logs", "relative-probe.log"), value);
        Console.WriteLine(Path.GetFullPath("."));
        return 0;
    }

    private static async Task LaunchDirectoryChecks()
    {
        string? previousRoot = LauncherPaths.RootOverride;
        string parentDirectory = Environment.CurrentDirectory;
        string root = Path.Combine(Path.GetTempPath(), "CMUmcl launch 中文 " + Guid.NewGuid().ToString("N"));
        LauncherPaths.RootOverride = root;
        try
        {
            var path = new MinecraftPath(Path.Combine(root, "shared minecraft"));
            string versionDirectory = Path.Combine(path.Versions, "directory-fixture");
            Directory.CreateDirectory(versionDirectory);
            File.WriteAllText(Path.Combine(versionDirectory, "directory-fixture.json"),
                "{\"id\":\"directory-fixture\",\"type\":\"release\",\"mainClass\":\"Fixture\",\"assets\":\"fixture\",\"libraries\":[],\"arguments\":{\"game\":[\"--gameDir\",\"${game_directory}\"],\"jvm\":[]}}");
            using var http = new System.Net.Http.HttpClient();
            var parameters = MinecraftLauncherParameters.CreateDefault(path, http);
            parameters.VersionLoader = new LocalJsonVersionLoader(path);
            var launcher = new MinecraftLauncher(parameters);
            var build = typeof(LaunchService).GetMethod("BuildGameProcessAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            var resolver = typeof(LaunchService).Assembly.GetType("CMUmcl.Content.GameDirectoryResolver")!
                .GetMethod("ResolveGameDirectory")!;
            string executable = Assembly.GetExecutingAssembly().Location;

            foreach (VersionIsolation isolation in new[] { VersionIsolation.All, VersionIsolation.ModdedOnly, VersionIsolation.Off })
            foreach (string loader in new[] { "vanilla", "fabric", "forge", "neoforge", "quilt" })
            {
                string label = isolation + "/" + loader;
                string instanceDirectory = Path.Combine(root, "instances", "服务端整合包 2 " + isolation + " " + loader);
                var settings = new AppSettings { VersionIsolation = isolation };
                string gameDirectory = (string)resolver.Invoke(null, new object[] { settings, instanceDirectory, loader })!;
                bool isolated = isolation == VersionIsolation.All || (isolation == VersionIsolation.ModdedOnly && loader != "vanilla");
                Check(gameDirectory == (isolated ? instanceDirectory : LauncherPaths.GameDirectory), label + " preserves isolation policy");
                string configDirectory = Path.Combine(gameDirectory, "config", "fancymenu");
                Directory.CreateDirectory(configDirectory);
                File.WriteAllText(Path.Combine(configDirectory, "customizablemenus.txt"), label);
                var options = new MLaunchOption {
                    Path = path, JavaPath = executable, Session = MSession.CreateOfflineSession("Fixture"),
                    ArgumentDictionary = new Dictionary<string, string> { ["game_directory"] = gameDirectory },
                };
                using var process = await (Task<Process>)build.Invoke(null, new object[] { launcher, "directory-fixture", options, gameDirectory, CancellationToken.None })!;
                Check(process.StartInfo.WorkingDirectory == gameDirectory && process.StartInfo.Arguments.Contains(gameDirectory),
                    label + " aligns process directory with gameDir");
                // Keep the production process settings; replace Java arguments with our test child mode.
                process.StartInfo.Arguments = "--relative-files-probe";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.Start();
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(15000)) { process.Kill(); throw new Exception(label + " child timed out"); }
                string actualDirectory = (await output).Trim();
                string errorText = await errors;
                Check(process.ExitCode == 0 && actualDirectory == gameDirectory,
                    label + " child reads relative config in selected directory: " + errorText);
                Check(File.ReadAllText(Path.Combine(gameDirectory, "logs", "relative-probe.log")) == label,
                    label + " child writes logs into selected directory");
            }
            Check(Environment.CurrentDirectory == parentDirectory, "launching instances leaves launcher current directory unchanged");
            Check(!Directory.Exists(Path.Combine(path.BasePath, "config")) && !Directory.Exists(Path.Combine(path.BasePath, "logs")),
                "relative mod files never leak into shared Minecraft dependencies");
        }
        finally { LauncherPaths.RootOverride = previousRoot; }
    }
}
