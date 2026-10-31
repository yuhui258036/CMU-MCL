using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;
using System.Xml.Linq;
using CMUmcl.Auth;
using CMUmcl.Content;
using CMUmcl.Launch;
using CMUmcl.Update;

namespace CMUmcl.UpdateUiAcceptance;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var root = Environment.GetEnvironmentVariable("CMUMCL_UI_FIXTURE_ROOT");
        if (root is null || !Path.IsPathFullyQualified(root) ||
            !File.Exists(Path.Combine(root, "fixture.marker"))) return 2;
        try
        {
            LauncherPaths.RootOverride = Path.Combine(root, "isolated-data");
            LauncherPaths.EnsureDirectories();
            var app = new AcceptanceApp(root);
            using var info = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "fixture-info.json")));
            string source = info.RootElement.GetProperty("source").GetString()!;
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var markup = XDocument.Load(Path.Combine(source, "CMUmcl", "App.xaml"));
            var resources = new XElement(markup.Root!.Element(presentation + "Application.Resources")!
                .Element(presentation + "ResourceDictionary")!);
            resources.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
            app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
            return app.Run();
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(root, "startup-error.txt"), error.ToString());
            return 1;
        }
    }
}

// This class exists only in this separately built test executable. It does not call
// App.OnStartup, restore sessions, authenticate, or expose a login bypass in the launcher.
internal sealed class AcceptanceApp(string root) : Application
{
    private readonly List<IDisposable> _services = [];
    private Mutex? _gate;
    private bool _ownsGate;

    protected override void OnStartup(StartupEventArgs e)
    {
        _gate = new Mutex(false, UpdateProtocol.InstallLockName(AppContext.BaseDirectory));
        try { _ownsGate = _gate.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsGate = true; }
        if (!_ownsGate) { Shutdown(3); return; }
        var settings = new SettingsStore();
        settings.Load();
        var auth = new YggdrasilClient();
        var store = new AuthStore(); // Paths point at the new, empty fixture data directory.
        var manifest = new ResourceManifestClient();
        var installed = new InstalledStateStore();
        var installer = new PackInstaller(installed);
        var launch = new LaunchService(auth, store, installed, settings);
        var local = new LocalInstanceService(installed);
        _services.AddRange([auth, manifest, installer, launch]);
        LauncherUpdateClient updates;
        if (File.Exists(Path.Combine(root, "live-cdn.marker"))) updates = new LauncherUpdateClient();
        else
        {
            var http = new HttpClient(new FixtureTransport(root)) { Timeout = Timeout.InfiniteTimeSpan };
            updates = new LauncherUpdateClient(http, File.ReadAllText(Path.Combine(root, "public-key.pem")));
        }
        // No real token is created, loaded, refreshed, or sent. The shell is exercised
        // directly with an explicitly synthetic display identity.
        var session = new AuthSession { SelectedProfileName = "隔离验收（无真实账号）", Username = "isolated-test" };
        var window = new CMUmcl.MainWindow(session, store, manifest, installer, installed, launch, settings, local, updates)
            { Title = "CMUmcl — 设置页隔离验收" };
        window.Closed += (_, _) => Shutdown();
        MainWindow = window;
        File.AppendAllText(Path.Combine(root, "lifecycle.jsonl"), JsonSerializer.Serialize(new
        {
            Event = "started", Version = LauncherUpdateClient.CurrentVersion.ToString(),
            Pid = Environment.ProcessId, Data = LauncherPaths.RootDirectory,
            Install = AppContext.BaseDirectory, Utc = DateTimeOffset.UtcNow,
        }) + "\n");
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        foreach (var service in _services) service.Dispose();
        if (_ownsGate) _gate!.ReleaseMutex();
        _gate?.Dispose();
        File.AppendAllText(Path.Combine(root, "lifecycle.jsonl"), JsonSerializer.Serialize(new
            { Event = "exited", Version = LauncherUpdateClient.CurrentVersion.ToString(), Pid = Environment.ProcessId,
              Code = e.ApplicationExitCode, Utc = DateTimeOffset.UtcNow }) + "\n");
        base.OnExit(e);
    }
}

internal sealed class FixtureTransport(string root) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var scenario = File.ReadAllText(Path.Combine(root, "scenario.txt")).Trim();
        File.AppendAllText(Path.Combine(root, "requests.jsonl"), JsonSerializer.Serialize(new
            { Utc = DateTimeOffset.UtcNow, Scenario = scenario, Url = request.RequestUri!.ToString() }) + "\n");
        if (request.RequestUri!.ToString() == UpdateProtocol.FeedUrl)
        {
            if (scenario == "offline") return new(HttpStatusCode.ServiceUnavailable);
            string manifest = scenario switch
            {
                "no-update" => "no-update.json", "tampered" => "tampered.json", "bad-hash" => "bad-hash.json",
                "legacy" => "legacy.json", _ => "available.json",
            };
            var body = await File.ReadAllBytesAsync(Path.Combine(root, "manifests", manifest), ct);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }
        if (request.RequestUri.ToString() != "https://csumcl.mcapcs.cn/update/windows-x86_64/ui-acceptance-only-0.2.0.zip")
            throw new InvalidOperationException("Unexpected fixture request");
        var package = Path.Combine(root, "package.zip");
        Stream input = File.OpenRead(package);
        if (scenario == "slow") input = new ThrottledStream(input);
        var content = new StreamContent(input);
        content.Headers.ContentLength = new FileInfo(package).Length;
        return new(HttpStatusCode.OK) { Content = content };
    }
}

internal sealed class ThrottledStream(Stream inner) : Stream
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    { await Task.Delay(60, ct); return await inner.ReadAsync(buffer, ct); }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
