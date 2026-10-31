using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CMUmcl.Content;

namespace CMUmcl.Appearance;

/// <summary>One wallpaper for the complete application; theme changes never request a new image.</summary>
public sealed class BackgroundController : IDisposable
{
    private readonly BackgroundLoader _loader;
    private readonly string _cacheDirectory;
    private readonly DispatcherTimer _timer = new();
    private CancellationTokenSource? _request;
    private BackgroundImageEntry[] _images = [];
    private BackgroundKind _kind;
    private string _url = "", _sourceKey = "";
    private bool _shuffle, _automatic, _disposed, _suspended;
    private int _index = -1, _generation;
    public BitmapSource? Image { get; private set; }
    public string? CurrentPath { get; private set; }
    public string? Error { get; private set; }
    public bool IsLoading { get; private set; }
    public bool RotationEnabled => _timer.IsEnabled;
    public event Action? Changed;

    public BackgroundController(string cacheDirectory, BackgroundLoader? loader = null)
    {
        _cacheDirectory = cacheDirectory; _loader = loader ?? new BackgroundLoader();
        _timer.Tick += async (_, _) => await TickAsync();
    }
    internal Task TickAsync() => !_suspended && _automatic && !IsLoading ? NextAsync() : Task.CompletedTask;

    public void SetSuspended(bool suspended)
    {
        if (_suspended == suspended || _disposed) return;
        _suspended = suspended;
        UpdateTimer();
    }
    private void UpdateTimer()
    {
        _timer.Stop();
        if (!_disposed && !_suspended && _automatic &&
            (_kind == BackgroundKind.Local && _images.Length > 1 ||
             _kind == BackgroundKind.Network && BackgroundLoader.TryGetUri(_url, out _))) _timer.Start();
    }

    public Task ConfigureAsync(AppSettings settings)
    {
        if (_disposed) return Task.CompletedTask;
        _automatic = settings.BackgroundAutoSwitch;
        _shuffle = settings.BackgroundShuffle;
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(settings.BackgroundIntervalSeconds, 10, 3600));
        var kind = settings.BackgroundMode ?? BackgroundKind.Default;
        var images = settings.BackgroundImages.ToArray();
        string url = settings.BackgroundUrl;
        string key = kind + "|" + (kind == BackgroundKind.Local ? string.Join('\n', images.Select(i => i.Path)) : kind == BackgroundKind.Network ? url : "");
        if (key == _sourceKey) { UpdateTimer(); return Task.CompletedTask; }
        _kind = kind; _url = url; _images = images; _sourceKey = key; _index = -1;
        UpdateTimer();
        return NextAsync();
    }

    public async Task NextAsync()
    {
        if (_disposed) return;
        _request?.Cancel();
        _request?.Dispose();
        _request = new CancellationTokenSource();
        CancellationToken ct = _request.Token;
        int generation = ++_generation;
        Error = null;
        if (_kind == BackgroundKind.Default || _kind == BackgroundKind.Local && _images.Length == 0 || _kind == BackgroundKind.Network && string.IsNullOrWhiteSpace(_url))
        {
            Image = null; CurrentPath = null; IsLoading = false; Changed?.Invoke(); return;
        }
        IsLoading = true; Changed?.Invoke();
        try
        {
            if (_kind == BackgroundKind.Network)
            {
                if (!BackgroundLoader.TryGetUri(_url, out _)) throw new IOException("请输入有效的 HTTP 或 HTTPS 图片地址。");
                string cached = BackgroundLoader.CachePath(_cacheDirectory, _url);
                if (Image == null && File.Exists(cached))
                {
                    try
                    {
                        var cachedImage = await Task.Run(() => AppearanceManager.LoadImage(cached), ct);
                        if (generation != _generation || ct.IsCancellationRequested) return;
                        Image = cachedImage; CurrentPath = cached; Changed?.Invoke();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { /* Try a fresh response. */ }
                }
                await Task.Delay(300, ct); // Coalesce URL edits; never submit credentials/cookies.
                var downloaded = await _loader.DownloadImageAsync(_url, _cacheDirectory, ct);
                if (generation != _generation || ct.IsCancellationRequested) return;
                Image = downloaded.Image; CurrentPath = downloaded.Path;
                return;
            }
            else
            {
                var candidates = Enumerable.Range(0, _images.Length).Where(i => _images.Length == 1 || i != _index).ToList();
                if (_shuffle) Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(candidates));
                else candidates = candidates.OrderBy(i => (i - _index - 1 + _images.Length) % _images.Length).ToList();
                foreach (int i in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var candidate = await Task.Run(() => AppearanceManager.LoadImage(_images[i].Path), ct);
                        if (generation != _generation) return;
                        _index = i; Image = candidate; CurrentPath = _images[i].Path;
                        return;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { }
                }
                throw new IOException("无法读取背景图片，请重新选择。" );
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation == _generation) Error = "背景图片加载失败：" + ex.Message + (Image != null ? " 已保留上一张图片。" : "");
        }
        finally
        {
            if (generation == _generation) { IsLoading = false; Changed?.Invoke(); }
        }
    }
    public void Dispose()
    {
        _disposed = true; _timer.Stop(); _request?.Cancel(); _request?.Dispose(); _loader.Dispose();
    }
}
