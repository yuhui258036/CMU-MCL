using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace CMUmcl.Appearance;

/// <summary>Bounded image download with validated redirects, independent from launcher authentication.</summary>
public sealed class BackgroundLoader : IDisposable
{
    public const long MaxBytes = 20 * 1024 * 1024;
    private readonly HttpClient _http;
    public BackgroundLoader() : this(new HttpClient(CMUmcl.Network.LauncherProxy.CreateHandler(allowAutoRedirect: false, useCookies: false))) { }
    internal BackgroundLoader(HttpClient http) { _http = http; _http.Timeout = Timeout.InfiniteTimeSpan; }

    public static bool TryGetUri(string? value, out Uri? uri)
    {
        uri = null;
        return Uri.TryCreate(value?.Trim(), UriKind.Absolute, out uri) &&
            uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0 && uri.Host.Length > 0;
    }

    public static string CachePath(string directory, string url)
        => Path.Combine(directory, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url))) + ".image");

    public async Task<string> DownloadAsync(string url, string directory, CancellationToken ct)
        => (await DownloadImageAsync(url, directory, ct).ConfigureAwait(false)).Path;

    internal async Task<(string Path, System.Windows.Media.Imaging.BitmapSource Image)> DownloadImageAsync(string url, string directory, CancellationToken ct)
    {
        if (!TryGetUri(url, out var uri)) throw new IOException("请输入有效的 HTTP 或 HTTPS 图片地址。");
        Directory.CreateDirectory(directory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        string target = CachePath(directory, url), temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            for (int redirect = 0; redirect <= 5; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("CMUmcl/0.1.0");
                request.Headers.CacheControl = new() { NoCache = true };
                request.Headers.Accept.ParseAdd("image/*");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location;
                    if (location == null || !Uri.TryCreate(uri, location, out var next) || !TryGetUri(next.AbsoluteUri, out next))
                        throw new IOException("图片地址重定向无效。");
                    if (uri!.Scheme == "https" && next!.Scheme != "https") throw new IOException("图片地址重定向到不安全的连接。");
                    uri = next;
                    continue;
                }
                response.EnsureSuccessStatusCode();
                var media = response.Content.Headers.ContentType?.MediaType;
                if (media != null && !media.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && media != "application/octet-stream")
                    throw new IOException("此地址未返回图片。请使用直接返回图片的 API。");
                if (response.Content.Headers.ContentLength > MaxBytes) throw new IOException("背景图片不能超过 20 MB。");
                await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    byte[] buffer = new byte[65536]; long total = 0; int read;
                    while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > MaxBytes) throw new IOException("背景图片不能超过 20 MB。");
                        await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    }
                }
                // Validate once and return the frozen bitmap for display, avoiding a second decode.
                var image = await Task.Run(() => AppearanceManager.LoadImage(temp), timeout.Token).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                File.Move(temp, target, true);
                return (target, image);
            }
            throw new IOException("图片地址重定向次数过多。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("背景图片加载超时。"); }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }
    public void Dispose() => _http.Dispose();
}
