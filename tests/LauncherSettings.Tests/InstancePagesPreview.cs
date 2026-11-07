using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using CMUmcl;
using CMUmcl.Content;
using CMUmcl.Launch;

internal static partial class Program
{
    // Uses the existing fixture transport and isolated data root; never a saved account.
    private static void ShowInstancePagesPreview(MainWindow window, InstalledStateStore installed, SettingsStore store)
    {
        const string id = "local:style-preview";
        string directory = Path.Combine(LauncherPaths.InstancesDirectory, "style-preview");
        Directory.CreateDirectory(directory);
        var metadata = new InstanceMetadata { Id = id, Name = "本地示例实例", Game = "1.21.1", Loader = "fabric", LoaderVersion = "0.16.10" };
        File.WriteAllText(Path.Combine(directory, "instance.json"), JsonSerializer.Serialize(metadata));
        installed.RecordInstall(id, 1, "style-preview", metadata.Name);
        var settings = store.Load(); settings.VersionIsolation = VersionIsolation.All; store.Save(settings);
        string mods = Path.Combine(directory, "mods"); Directory.CreateDirectory(mods);
        string example = Path.Combine(mods, "style-preview.jar");
        if (!File.Exists(example))
        {
            using var zip = ZipFile.Open(example, ZipArchiveMode.Create);
            using var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open());
            writer.Write("{\"id\":\"cmumcl-style-preview\",\"name\":\"界面预览示例\",\"description\":\"用于查看资源列表样式的本地示例文件。\"}");
        }
        window._launchOverride = (_, _) => Task.FromException<LaunchResult>(new LaunchException("这是界面预览，不会真正启动游戏。"));
        window.Title = "CMUmcl — NET48 实例管理预览";
        typeof(MainWindow).GetMethod("RefreshInstances", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
        var card = new InstanceCardViewModel(id, metadata.Name, "本地预览", metadata, directory);
        if (Environment.GetCommandLineArgs().Contains("--features")) { ShowFeaturePreview(window, id, metadata, directory, example); return; }
        if(Environment.GetCommandLineArgs().Contains("--copy-review"))
        {
            window.Title="CMUmcl — NET48 文案预览";
            typeof(MainWindow).GetMethod("ShowSettingsView",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,null);
            Click(window,"ProxyTab");return;
        }
        bool directories=Environment.GetCommandLineArgs().Contains("--directories");
        if(directories)window.Title="CMUmcl — NET48 目录卡片预览";
        typeof(MainWindow).GetMethod(directories?"OpenInstanceDirectories":"OpenInstanceSettings", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, new object[] { card });
    }

    // New launcher features with local sample data: a crashed instance with an exportable report,
    // a mod with a sample update, and the after-launch option in settings. Nothing reaches the network.
    private static void ShowFeaturePreview(MainWindow window, string id, InstanceMetadata metadata, string directory, string mod)
    {
        window.Title = "CMUmcl — 新功能预览";
        Directory.CreateDirectory(Path.Combine(directory, "logs"));
        Directory.CreateDirectory(Path.Combine(directory, "crash-reports"));
        File.WriteAllText(Path.Combine(directory, "logs", "latest.log"), "[12:00:00] [Render thread/INFO]: 预览日志\n[12:00:05] [Render thread/ERROR]: java.lang.OutOfMemoryError: Java heap space\n");
        File.WriteAllText(Path.Combine(directory, "crash-reports", "crash-preview-client.txt"), "---- Minecraft Crash Report ----\n// 预览示例\nDescription: Unexpected error\n");
        InstanceContentView.DefaultModUpdatesFactory = () => CreateModUpdateFixture(mod);
        typeof(MainWindow).GetMethod("ShowInstancesView", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
        var card = Get<ItemsControl>(window, "InstancesList").Items.OfType<InstanceCardViewModel>().Single(x => x.InstanceId == id);
        const string hint = "内存不足，请提高内存分配或关闭其他程序。";
        card.GameExited(1, hint);
        var crashes = (Dictionary<string, CrashContext>)typeof(MainWindow).GetField("_crashes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        crashes[id] = new CrashContext { InstanceName = metadata.Name, GameDirectory = directory, GameVersion = metadata.Game, Loader = metadata.Loader, LoaderVersion = metadata.LoaderVersion,
            JavaMajor = 21, JavaPath = "（预览）", ExitCode = 1, Hint = hint };
        card.OfferCrashReport();
    }
}
