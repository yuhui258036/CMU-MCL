using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace CMUmcl.Update;

public sealed class LauncherUpdateClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _verificationKey;
    public LauncherUpdateClient() : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan }, UpdateProtocol.PublicKey) { }

    // Test seam used by the separately built acceptance assembly.
    // The shipped launcher always uses the parameterless constructor and pinned public key.
    internal LauncherUpdateClient(HttpClient http, string verificationKey)
    {
        _http = http;
        _verificationKey = verificationKey;
    }
    public static Version CurrentVersion => typeof(LauncherUpdateClient).Assembly.GetName().Version!;

    public async Task<(UpdateRelease Release, byte[] Envelope)?> CheckAsync(CancellationToken ct)
    {
        if (!Environment.Is64BitProcess) throw new InvalidOperationException("自动更新仅支持 Windows x64 客户端。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var bytes = await DownloadBytesAsync(UpdateProtocol.FeedUrl, 128 * 1024, timeout.Token).ConfigureAwait(false);
        var release = UpdateProtocol.ReadRelease(bytes, _verificationKey);
        return UpdateProtocol.ParseVersion(release.Version) > CurrentVersion ? (release, bytes) : null;
    }

    public async Task<string> StageAsync(UpdateRelease release, byte[] envelope, IProgress<double> progress, CancellationToken ct)
    {
        // Only use values obtained from the signed envelope again at the download boundary.
        var verified = UpdateProtocol.ReadRelease(envelope, _verificationKey);
        if (verified != release || UpdateProtocol.ParseVersion(release.Version) <= CurrentVersion)
            throw new InvalidDataException("更新信息已失效，请重新检查。");
        var stage = Path.Combine(Path.GetTempPath(), "CMUmcl-updates", Guid.NewGuid().ToString("N"));
        UpdateProtocol.EnsureFreeSpace(stage, release.Size);
        Directory.CreateDirectory(stage);
        var partial = Path.Combine(stage, "package.partial");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            using var response = await _http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if ((int)response.StatusCode is >= 300 and < 400) throw new IOException("更新下载不允许重定向。");
            if (response.Content.Headers.ContentLength is long length && length != release.Size)
                throw new InvalidDataException("更新响应大小不匹配。");
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var output = File.Create(partial))
            {
                var buffer = new byte[128 * 1024];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > release.Size) throw new InvalidDataException("更新下载超出声明大小。");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    progress.Report(100.0 * received / release.Size);
                }
            }
            await Task.Run(() => UpdateProtocol.VerifyArchive(partial, release, _verificationKey), ct).ConfigureAwait(false);
            File.Move(partial, Path.Combine(stage, "package.zip"));
            await File.WriteAllBytesAsync(Path.Combine(stage, "latest.json"), envelope, ct).ConfigureAwait(false);
            // Validate all archive entries before offering to close the launcher.
            await Task.Run(() => UpdateProtocol.ExtractArchive(Path.Combine(stage, "package.zip"), Path.Combine(stage, "preview")), ct).ConfigureAwait(false);
            var payloadVersion = AssemblyName.GetAssemblyName(Path.Combine(stage, "preview", "CMUmcl.dll")).Version;
            if (payloadVersion != UpdateProtocol.ParseVersion(release.Version)) throw new InvalidDataException("包内版本与签名版本不符。");
            Directory.Delete(Path.Combine(stage, "preview"), recursive: true);
            return stage;
        }
        catch
        {
            // stage is generated under a fixed temp parent; never delete install or game data.
            try { Directory.Delete(stage, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public static void StartUpdater(string stage)
    {
        var install = AppContext.BaseDirectory;
        if (!string.Equals(Path.GetFileName(Environment.ProcessPath), "CMUmcl.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("开发运行模式不能替换程序，请使用正式自包含客户端。");
        var updaterSource = Path.Combine(install, "updater");
        if (!File.Exists(Path.Combine(updaterSource, "CMUmcl.Updater.exe")))
            throw new FileNotFoundException("未找到独立更新程序，请先手动安装完整新版客户端。");
        var runner = Path.Combine(stage, "runner");
        UpdateProtocol.EnsureFreeSpace(runner, Directory.EnumerateFiles(updaterSource, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length));
        Directory.CreateDirectory(runner);
        foreach (var source in Directory.EnumerateFiles(updaterSource, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(updaterSource, source);
            UpdateTransaction.AssertSafeTarget(updaterSource, relative);
            var target = Path.Combine(runner, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        using var process = Process.GetCurrentProcess();
        var job = new UpdateJob(install, process.Id, process.StartTime.ToUniversalTime().Ticks, CurrentVersion.ToString(),
            CMUmcl.Content.LauncherPaths.RootOverride);
        File.WriteAllText(Path.Combine(stage, "job.json"), JsonSerializer.Serialize(job, UpdateProtocol.JsonOptions));
        var start = new ProcessStartInfo(Path.Combine(runner, "CMUmcl.Updater.exe")) { UseShellExecute = false,
            CreateNoWindow = true, WorkingDirectory = stage };
        start.ArgumentList.Add(stage);
        _ = Process.Start(start) ?? throw new IOException("无法启动更新程序。");
    }

    private async Task<byte[]> DownloadBytesAsync(string url, int max, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (result.Length + count > max) throw new InvalidDataException("更新清单过大。");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    public void Dispose() => _http.Dispose();
}
