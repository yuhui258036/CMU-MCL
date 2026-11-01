using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using CMUmcl;
using CMUmcl.Appearance;
using CMUmcl.Content;

internal static partial class Program
{
    private static void PrepareProfile(SettingsStore store)
    {
        string image = Environment.GetEnvironmentVariable("CMUMCL_PROFILE_IMAGE") ?? "";
        store.Save(new AppSettings
        {
            Theme = LauncherTheme.Light,
            BackgroundMode = File.Exists(image) ? BackgroundKind.Local : BackgroundKind.Default,
            BackgroundImages = File.Exists(image) ? [new(image, "fixed fixture")] : [],
            BackgroundAutoSwitch = false,
            BackgroundOpacity = 1,
        });
    }

    private static async Task Profile(MainWindow window, SettingsStore store)
    {
        var samples = new List<object>();
        async Task Sample(string stage, int delay = 4000)
        {
            using var process = Process.GetCurrentProcess();
            var startCpu = process.TotalProcessorTime;
            var clock = Stopwatch.StartNew();
            await Task.Delay(delay);
            process.Refresh();
            var gc = GC.GetGCMemoryInfo();
            samples.Add(new {
                stage, workingSetMiB = process.WorkingSet64 / 1048576d,
                privateMiB = process.PrivateMemorySize64 / 1048576d,
                managedAllocatedMiB = GC.GetTotalMemory(false) / 1048576d,
                gcCommittedMiB = gc.TotalCommittedBytes / 1048576d,
                cpuSeconds = (process.TotalProcessorTime - startCpu).TotalSeconds,
                elapsedSeconds = clock.Elapsed.TotalSeconds,
                totalAllocatedBytes = GC.GetTotalAllocatedBytes(false),
            });
            File.WriteAllText(Path.Combine(_output, "performance.json"), JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        }
        await Sample("visible-idle");
        var settings = store.Load();
        long start = GC.GetTotalAllocatedBytes(true);
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++) await AppearanceManager.ApplyAsync(settings);
        File.WriteAllText(Path.Combine(_output, "appearance-reapply.json"), JsonSerializer.Serialize(new {
            allocatedBytes = GC.GetTotalAllocatedBytes(true) - start, elapsedMs = clock.Elapsed.TotalMilliseconds
        }));
        await Sample("after-100-unchanged-settings");
        window.WindowState = WindowState.Minimized;
        await Sample("minimized-idle", 10000);
        window.WindowState = WindowState.Normal;
        await Sample("restored-idle");
        Screenshot(window, "profile-restored.png");
    }
}
