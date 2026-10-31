using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using CMUmcl;
using CMUmcl.Appearance;
using CMUmcl.Auth;
using CMUmcl.Content;
using CMUmcl.Launch;
using CMUmcl.Update;

internal static partial class Program
{
    private static int _checks;
    private static bool _profile;
    private static string _output = "";
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS " + name); _checks++;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 2 || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1])) return 2;
        string source = args[0]; _output = args[1];
        Directory.CreateDirectory(_output);
        LauncherPaths.RootOverride = Path.Combine(_output, "isolated-data");
        bool preview = args.Contains("--preview");
        _profile = args.Contains("--profile");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var document = XDocument.Load(Path.Combine(source, "CMUmcl", "App.xaml"));
        var dictionary = new XElement(document.Root!.Element(ns + "Application.Resources")!.Element(ns + "ResourceDictionary")!);
        dictionary.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        app.Startup += async (_, _) =>
        {
            try { await Run(app, preview, args.Contains("--live-background")); if (!preview) app.Shutdown(0); }
            catch (Exception ex) { Console.Error.WriteLine(ex); File.WriteAllText(Path.Combine(_output, "failure.txt"), ex.ToString()); app.Shutdown(1); }
        };
        return app.Run();
    }

    private static async Task Run(Application app, bool preview, bool liveBackground)
    {
        var store = new SettingsStore();
        if (!preview && !_profile) CoreChecks(store);
        if (_profile) PrepareProfile(store);
        var settings = store.Load();
        CMUmcl.Network.LauncherProxy.Initialize();
        if (preview && !File.Exists(LauncherPaths.SettingsPath)) { settings.Theme = LauncherTheme.Light; store.Save(settings); }
        AppearanceManager.Initialize(settings);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var model = new ResourceManifest { FormatVersion = 1, Instances = [
            new() { Id="campus-survival", Name="示例整合包 1", Description="用于本地界面测试。", Revision=3,
                Minecraft=new(){ GameVersion="1.21.1", Loader="fabric", LoaderVersion="0.16.10"},
                Pack=new(){ Kind="mrpack", Url=ResourceManifestClient.BaseUrl+"/packs/campus.mrpack", Size=264241152, Sha256=new string('0',64)}, Server=new(){Address="preview.invalid",Port=25565}},
            new() { Id="creative", Name="示例整合包 2", Description="用于本地界面测试。", Revision=2,
                Minecraft=new(){GameVersion="1.20.1",Loader="forge",LoaderVersion="47.3.0"},
                Pack=new(){Kind="mrpack",Url=ResourceManifestClient.BaseUrl+"/packs/creative.mrpack",Size=137363456,Sha256=new string('1',64)},Server=new(){Address="preview.invalid",Port=25565}}
        ]};
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(model);
        byte[] signature = JsonSerializer.SerializeToUtf8Bytes(new { algorithm="ECDSA-P256-SHA256", signature=Convert.ToBase64String(key.SignData(manifest,HashAlgorithmName.SHA256)) });
        var handler = new FixtureHandler(manifest, signature);
        var client = new ResourceManifestClient(handler, new Uri("https://preview.invalid/"), key.ExportSubjectPublicKeyInfoPem());
        var auth = new YggdrasilClient();
        var authStore = new AuthStore();
        var installed = new InstalledStateStore();
        var installer = new PackInstaller(installed);
        var launch = new LaunchService(auth, authStore, installed, store);
        var updates = new LauncherUpdateClient(new HttpClient(new FixtureHandler([],[])), key.ExportSubjectPublicKeyInfoPem());
        var window = new MainWindow(new AuthSession { SelectedProfileName="开发预览", SelectedProfileId=new string('0',32) },
            authStore,client,installer,installed,launch,store,new LocalInstanceService(installed),updates);
        window.Title = "CMUmcl — 设置与背景开发预览";
        if (!preview) { window.ShowInTaskbar=false; window.WindowStartupLocation=WindowStartupLocation.Manual; window.Left=-18000; window.Top=-18000; }
        window.Closed += (_,_) => { client.Dispose();auth.Dispose();installer.Dispose();launch.Dispose(); if(preview) app.Shutdown(); };
        window.Show();
        await Task.Delay(200);
        if (!preview) Get<ComboBox>(window,"ThemeCombo").SelectedIndex=(int)LauncherTheme.Light;
        // This standalone fixture never provides a real session or production transport.
        Get<Button>(window,"LogoutButton").IsEnabled=false;
        Get<Button>(window,"NewInstanceButton").IsEnabled=false;
        Get<ItemsControl>(window,"CardsList").IsEnabled=false;
        Get<Button>(window,"CheckLauncherUpdateButton").IsEnabled=false;
        Get<TextBlock>(window,"LauncherUpdateStatus").Text="本地开发预览，不连接线上更新服务。";
        Check(Get<ItemsControl>(window,"CardsList").Items.Count==2,"signed fixture manifest displays two pack cards");
        if (preview) return;
        if (_profile) { await Profile(window, store); window.Close(); return; }

        await ProxyChecks(store);
        await UiChecks(app, window, store, installed, launch, auth, authStore, liveBackground);
        string savedJson=File.ReadAllText(LauncherPaths.SettingsPath);
        var reloaded = new MainWindow(new AuthSession { SelectedProfileName="开发预览", SelectedProfileId=new string('0',32) },
            authStore,client,installer,installed,launch,new SettingsStore(),new LocalInstanceService(installed),
            new LauncherUpdateClient(new HttpClient(new FixtureHandler([],[])),key.ExportSubjectPublicKeyInfoPem()));
        Check(File.ReadAllText(LauncherPaths.SettingsPath)==savedJson && Get<ComboBox>(reloaded,"ThemeCombo").SelectedIndex==(int)LauncherTheme.Dark
            && Get<TextBox>(reloaded,"MemoryTextBox").Text=="3072" && Get<CheckBox>(reloaded,"AutoUpdatePacksCheck").IsChecked==false,
            "new window restores saved settings without writing during initialization");
        reloaded.Close();
        await BackgroundChecks();
        await ActivityChecks(window, store);
        string closingJava = CreateJavaFixture("closing-java", "21.0.6", "x64");
        Get<RadioButton>(window,"JavaManualRadio").IsChecked=true;
        Get<TextBox>(window,"JavaPathTextBox").Text=closingJava;
        window.Close();
        await Until(() => !window.IsVisible);
        Check(store.Load().JavaPath==closingJava,"closing flushes pending valid Java detection");
        File.WriteAllText(Path.Combine(_output,"result.txt"), $"PASS {_checks} checks");
        Console.WriteLine($"PASS {_checks} launcher settings checks; artifacts: {_output}");
    }

    private static void CoreChecks(SettingsStore store)
    {
        Directory.CreateDirectory(LauncherPaths.RootDirectory);
        File.WriteAllText(LauncherPaths.SettingsPath,"{\"maxMemoryMb\":6144,\"javaPath\":null}");
        Check(!store.Load().AutoMemory && store.Load().MaxMemoryMb==6144,"legacy settings preserve manual heap");
        File.Move(LauncherPaths.SettingsPath,LauncherPaths.SettingsPath+".legacy",true);
        Check(store.Load().AutoMemory && SettingsStore.Current.AutoMemory,"fresh settings enable automatic memory and update Current");
        Check(MemoryAllocation.Recommend(new(8192,6144))==4096,"8 GB machine reserves system memory");
        Check(MemoryAllocation.Recommend(new(32768,24576))==8192,"automatic heap capped at 8 GB");
        Check(MemoryAllocation.Recommend(new(8192,600))==512,"low available RAM has bounded minimum");
        Check(MemoryAllocation.Recommend(new(16384,12000),32)==1024,"32-bit automatic heap capped");
        Check(MemoryAllocation.ValidateManual(2048,new(8192,6000),32)!=null,"32-bit excessive manual heap rejected");
        Check(MemoryAllocation.ValidateManual(16384,new(8192,6000))!=null,"manual heap above physical RAM rejected");
        var settings=new AppSettings{AutoMemory=true,MaxMemoryMb=65536};
        Check(MemoryAllocation.Resolve(settings,new JavaInfo{Architecture="x64"},new(8192,6144))==4096,"launch allocation uses auto policy instead of stale manual heap");
        settings.AutoMemory=false;settings.MaxMemoryMb=3072;
        Check(MemoryAllocation.Resolve(settings,new JavaInfo{Architecture="x64"},new(8192,6144))==3072,"launch allocation honors manual setting");
        var java64=new JavaInfo{Major=21,Version="21.0.3",Architecture="x64",JavaPath="javaw.exe"};
        var java32=new JavaInfo{Major=21,Version="21.0.9",Architecture="x86",JavaPath="javaw.exe"};
        Check(JavaLocator.Select(21,[java32,java64])==java64,"64-bit matching runtime selected over 32-bit");
        Check(JavaLocator.Select(17,[java64])==null,"automatic selection does not silently substitute a newer major");
        string fixture=CreateJavaFixture("metadata-java","1.8.0_402","x86");
        Check(JavaLocator.DetectExecutable(fixture) is {Major:8,Bits:32},"release metadata detects Java 8 and architecture");
        Check(JavaLocator.DetectExecutable(Path.GetDirectoryName(fixture))?.Major==8,"bin directory input resolves correctly");
        string sibling=Path.Combine(Path.GetDirectoryName(fixture)!,"other.exe");File.WriteAllText(sibling,"");
        Check(JavaLocator.DetectExecutable(sibling)==null,"non-Java executable path rejected");
        using var cancelled = new CancellationTokenSource();cancelled.Cancel();
        bool scanCancelled=false;try { JavaLocator.Scan(cancelled.Token); } catch(OperationCanceledException) { scanCancelled=true; }
        Check(scanCancelled,"Java scan supports cancellation");
        foreach(var (version,major) in new[]{("1.16.5",8),("1.17.1",16),("1.18.2",17),("1.20.4",17),("1.20.5",21),("1.21.9",21),("1.21.11",21),("26.1",25)})
            Check(JavaVersionResolver.ResolveFallbackMajor(version)==major,$"Java fallback {version} -> {major}");
        var invalid=new AppSettings{Theme=(LauncherTheme)99,BackgroundOpacity=double.NaN,BackgroundPath="relative.jpg"};SettingsStore.Sanitize(invalid);
        Check(invalid.Theme==LauncherTheme.System && invalid.BackgroundOpacity==0.25 && invalid.BackgroundPath==null,"invalid appearance preferences sanitized");
        Check(store.Save(SettingsStore.CreateDefaults()),"save fresh isolated defaults");
    }
    private static string CreateJavaFixture(string name,string version,string arch)
    {
        string root=Path.Combine(_output,name); Directory.CreateDirectory(Path.Combine(root,"bin"));
        File.WriteAllText(Path.Combine(root,"release"),$"JAVA_VERSION=\"{version}\"\nOS_ARCH=\"{arch}\"\nIMPLEMENTOR=\"Test fixture\"\n");
        string file=Path.Combine(root,"bin","javaw.exe");File.WriteAllBytes(file,[]);return file;
    }
    private static T Get<T>(Window window,string name) where T:class => (T)window.FindName(name);
    private static void Click(Window window,string name) => Get<Button>(window,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Call(object target,string method,params object[] args) => target.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(target,args);
    private static void SetPrivate(object target,string name,object value) => target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(target,value);
    private static void Screenshot(Window window,string name)
    {
        window.UpdateLayout();
        var content=(FrameworkElement)window.Content;
        var bitmap=new RenderTargetBitmap((int)content.ActualWidth,(int)content.ActualHeight,96,96,PixelFormats.Pbgra32);
        bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file=File.Create(Path.Combine(_output,name));encoder.Save(file);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);yield return child;
            foreach(var nested in Descendants(child)) yield return nested;
        }
    }
    private static void CreateTestImage(string path)
    {
        // A deterministic geometric fixture, not a bundled user wallpaper.
        var visual=new DrawingVisual();using(var dc=visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(57,100,175),Color.FromRgb(171,208,204),45),null,new Rect(0,0,1200,800));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(255,217,161)),null,new Point(940,210),100,100);
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(36,82,105)),null,Geometry.Parse("M0,650 L300,280 650,650 900,390 1200,700 1200,800 0,800Z"));
        }
        var image=new RenderTargetBitmap(1200,800,96,96,PixelFormats.Pbgra32);image.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(path);encoder.Save(file);
    }
}

internal sealed class FixtureHandler(byte[] manifest,byte[] signature):HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        if(request.RequestUri!.Host!="preview.invalid") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(request.RequestUri.AbsolutePath.EndsWith(".sig")?signature:manifest)});
    }
}
