using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using CMUmcl.Appearance;
using CMUmcl.Content;

internal static partial class Program
{
    private static async Task ActivityChecks(CMUmcl.MainWindow window, SettingsStore store)
    {
        await AppearanceManager.ApplyAsync(new AppSettings {
            Theme = LauncherTheme.Light, BackgroundMode = BackgroundKind.Local,
            BackgroundImages = [new(Path.Combine(_output,"first.png"),"first"),new(Path.Combine(_output,"second.png"),"second")],
            BackgroundAutoSwitch = true, BackgroundIntervalSeconds = 10
        });
        var controller = (BackgroundController)typeof(AppearanceManager).GetField("_background",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        var image = controller.Image;
        window.WindowState = System.Windows.WindowState.Minimized;
        await Task.Delay(150);
        await controller.TickAsync();
        Check(!controller.RotationEnabled && ReferenceEquals(image,controller.Image),"minimized main window pauses rotation without discarding wallpaper");
        var other = new System.Windows.Window { Width=100, Height=100, Left=-18000, Top=-18000, ShowInTaskbar=false, WindowStartupLocation=System.Windows.WindowStartupLocation.Manual };
        AppearanceManager.TrackWindow(other); other.Show();
        Check(controller.RotationEnabled,"another visible tracked window keeps rotation active");
        other.Close();
        Check(!controller.RotationEnabled,"closing last visible window pauses rotation again");
        window.WindowState = System.Windows.WindowState.Normal;
        Check(controller.RotationEnabled && ReferenceEquals(image,controller.Image),"restoring window resumes timer and preserves current wallpaper");
        await AppearanceManager.ApplyAsync(store.Load());
    }

    private static async Task BackgroundChecks()
    {
        string first=Path.Combine(_output,"first.png"),second=Path.Combine(_output,"second.png");
        CreateTestImage(first);CreateTestImage(second);
        var migrated=new AppSettings{BackgroundPath=first};SettingsStore.Sanitize(migrated);
        Check(migrated.BackgroundMode==BackgroundKind.Local && migrated.BackgroundImages.Single().Path==first && migrated.BackgroundPath==null,"legacy single background migrates without losing selection");
        Check(!BackgroundLoader.TryGetUri("https://user:secret@example.invalid/a",out _) && !BackgroundLoader.TryGetUri("file:///a.png",out _),"background URL forbids credentials and local schemes");
        string cache=Path.Combine(_output,"network-cache");byte[] bytes=File.ReadAllBytes(first);
        int requests=0;string mode="image";
        HttpResponseMessage ImageResponse() => new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};
        using var loader=new BackgroundLoader(new HttpClient(new ImageHandler(async (request,ct)=>
        {
            requests++;
            Check(request.Headers.Authorization==null && !request.Headers.Contains("Cookie"),"background request carries no launcher credentials");
            if(mode=="cancel") { await Task.Delay(Timeout.Infinite,ct); }
            if(mode=="redirect" && request.RequestUri!.AbsolutePath!="/final") return new(HttpStatusCode.Redirect){Headers={Location=new Uri("/final",UriKind.Relative)}};
            if(mode=="downgrade") return new(HttpStatusCode.Redirect){Headers={Location=new Uri("http://images.invalid/final")}};
            var response=ImageResponse();
            if(mode=="html") { response.Content=new StringContent("<html>not image</html>");response.Content.Headers.ContentType=new("text/html"); }
            if(mode=="broken") response.Content=new ByteArrayContent([1,2,3]);
            if(mode=="large") response.Content.Headers.ContentLength=BackgroundLoader.MaxBytes+1;
            if(mode=="stream-large") response.Content=new UnknownLengthContent(new byte[BackgroundLoader.MaxBytes+1]);
            return response;
        })));
        const string url="https://images.invalid/api";
        string path=await loader.DownloadAsync(url,cache,default);
        Check(File.ReadAllBytes(path).SequenceEqual(bytes),"network image validates and caches");
        mode="redirect";await loader.DownloadAsync(url,cache,default);
        Check(requests==3,"relative redirect resolves to image");
        foreach(string invalid in new[]{"downgrade","html","broken","large","stream-large"})
        {
            mode=invalid;bool rejected=false;try{await loader.DownloadAsync(url,cache,default);}catch{rejected=true;}
            Check(rejected && File.ReadAllBytes(path).SequenceEqual(bytes) && !Directory.EnumerateFiles(cache,"*.tmp").Any(),invalid+" rejects and preserves valid cache without temporary files");
        }
        mode="cancel";using(var cancellation=new CancellationTokenSource(50))
        {
            bool canceled=false;try{await loader.DownloadAsync(url,cache,cancellation.Token);}catch(OperationCanceledException){canceled=true;}
            Check(canceled && !Directory.EnumerateFiles(cache,"*.tmp").Any(),"cancellation cleans temporary files");
        }
        var settings=new AppSettings{BackgroundMode=BackgroundKind.Local,BackgroundImages=[new(first,"first"),new(second,"second")],BackgroundShuffle=false,BackgroundAutoSwitch=false,BackgroundIntervalSeconds=10};
        using(var controller=new BackgroundController(Path.Combine(_output,"local-cache")))
        {
            await controller.ConfigureAsync(settings);Check(controller.CurrentPath==first,"ordered local rotation starts at first image");
            await controller.TickAsync();Check(controller.CurrentPath==first && !controller.RotationEnabled,"automatic rotation off leaves current image unchanged");
            await controller.NextAsync();Check(controller.CurrentPath==second,"manual rotation works with automatic off");
            settings.Theme=LauncherTheme.Dark;await controller.ConfigureAsync(settings);
            Check(controller.CurrentPath==second,"theme-only reconfiguration does not advance background");
            settings.BackgroundAutoSwitch=true;await controller.ConfigureAsync(settings);
            controller.SetSuspended(true);
            await controller.TickAsync();
            Check(!controller.RotationEnabled && controller.CurrentPath==second,"suspension stops automatic background work and retains image");
            await controller.ConfigureAsync(settings);
            Check(!controller.RotationEnabled,"saving preferences cannot restart suspended rotation");
            controller.SetSuspended(false);
            await Until(()=>controller.CurrentPath==first,12000);
            Check(controller.RotationEnabled,"dispatcher timer rotates local background at configured interval");
            settings.BackgroundShuffle=true;await controller.ConfigureAsync(settings);await controller.NextAsync();
            Check(controller.CurrentPath==second,"shuffle avoids consecutive repeat");
            settings.BackgroundImages=[new(Path.Combine(_output,"missing.png"),"missing")];await controller.ConfigureAsync(settings);
            Check(controller.Image!=null && controller.Error!=null && controller.CurrentPath==second,"unreadable local source preserves previous image");
        }
        var appearanceSettings = new SettingsStore().Load();
        await AppearanceManager.ApplyAsync(appearanceSettings);
        var resources = System.Windows.Application.Current.Resources;
        var panel = resources["PanelBrush"];
        var backdrop = resources["WindowBackdropBrush"];
        await AppearanceManager.ApplyAsync(appearanceSettings);
        Check(ReferenceEquals(panel,resources["PanelBrush"]) && ReferenceEquals(backdrop,resources["WindowBackdropBrush"]),"unchanged settings reuse theme and wallpaper resources");
        Check(((System.Windows.Media.Brush)backdrop).IsFrozen,"shared wallpaper brush is frozen");
        using(var offline=new BackgroundController(cache,new BackgroundLoader(new HttpClient(new ImageHandler((_,_)=>throw new HttpRequestException("offline"))))))
        {
            await offline.ConfigureAsync(new AppSettings{BackgroundMode=BackgroundKind.Network,BackgroundUrl=url,BackgroundAutoSwitch=false});
            Check(offline.Image!=null && offline.Error!=null && offline.CurrentPath==path,"restart offline uses cached network image and reports failure");
        }
        var entered=new TaskCompletionSource();var release=new TaskCompletionSource();
        using(var racing=new BackgroundController(Path.Combine(_output,"race-cache"),new BackgroundLoader(new HttpClient(new ImageHandler(async(request,ct)=>
        {
            entered.SetResult();await release.Task;return ImageResponse();
        })))))
        {
            var old=racing.ConfigureAsync(new AppSettings{BackgroundMode=BackgroundKind.Network,BackgroundUrl=url,BackgroundAutoSwitch=false});
            await entered.Task;
            await racing.ConfigureAsync(new AppSettings{BackgroundMode=BackgroundKind.Local,BackgroundImages=[new(second,"second")],BackgroundAutoSwitch=false});
            release.SetResult();await old;
            Check(racing.CurrentPath==second && racing.Error==null,"late network response cannot overwrite a newer local background");
        }
    }
}
internal sealed class ImageHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>send(request,cancellationToken);
}
internal sealed class UnknownLengthContent(byte[] bytes):HttpContent
{
    protected override bool TryComputeLength(out long length) { length=0;return false; }
    protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context) => stream.WriteAsync(bytes).AsTask();
}
