using System.IO;
using System.Net;
using System.Net.Http;

namespace CMUmcl.Network;

// A new configuration uses a new connection pool. Existing streamed responses keep their
// original pool until disposed, so saving proxy settings does not cancel an active download.
internal sealed class ProxyRoutingHandler(bool redirects, bool cookies, DecompressionMethods decompression) : HttpMessageHandler
{
    private readonly object _gate = new();
    private Pool? _current;
    private bool _disposed;
    private sealed class Pool(LauncherProxy.Configuration config, HttpMessageInvoker invoker)
    {
        public readonly LauncherProxy.Configuration Config = config;
        public readonly HttpMessageInvoker Invoker = invoker;
        public int Leases;
        public bool Retired;
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Pool pool;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var config = LauncherProxy.Snapshot();
            if (_current == null || _current.Config != config)
            {
                var proxy = LauncherProxy.GetProxy(config);
                var handler = new HttpClientHandler { AllowAutoRedirect = redirects, UseCookies = cookies,
                    AutomaticDecompression = decompression, UseProxy = config.Mode != Content.LauncherProxyMode.Direct, Proxy = proxy };
                if (_current != null) Retire(_current);
                _current = new Pool(config, new HttpMessageInvoker(handler));
            }
            pool = _current; pool.Leases++;
        }
        try
        {
            var response = await pool.Invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.Content = new LeasedContent(response.Content, () => Release(pool));
            return response;
        }
        catch { Release(pool); throw; }
    }
    private static void Retire(Pool pool)
    {
        pool.Retired = true;
        if (pool.Leases == 0) pool.Invoker.Dispose();
    }
    private void Release(Pool pool)
    {
        lock (_gate) { if (--pool.Leases == 0 && pool.Retired) pool.Invoker.Dispose(); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) lock (_gate) { if (!_disposed) { _disposed = true; if (_current != null) Retire(_current); } }
        base.Dispose(disposing);
    }
    private sealed class LeasedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private Action? _release;
        public LeasedContent(HttpContent inner, Action release)
        {
            _inner = inner; _release = release;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override bool TryComputeLength(out long length) { length = _inner.Headers.ContentLength ?? 0; return _inner.Headers.ContentLength.HasValue; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _inner.CopyToAsync(stream);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) => _inner.CopyToAsync(stream, ct);
        protected override async Task<Stream> CreateContentReadStreamAsync()
            => new LeasedStream(await _inner.ReadAsStreamAsync().ConfigureAwait(false), Release);
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken ct)
            => new LeasedStream(await _inner.ReadAsStreamAsync(ct).ConfigureAwait(false), Release);
        private void Release() => Interlocked.Exchange(ref _release, null)?.Invoke();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { _inner.Dispose(); } finally { Release(); } }
            base.Dispose(disposing);
        }
    }
    // GetStreamAsync callers own only the returned stream, not its HttpResponseMessage.
    private sealed class LeasedStream(Stream inner, Action release) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken ct) => inner.CopyToAsync(destination, bufferSize, ct);
        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { inner.Dispose(); } finally { release(); } }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            try { await inner.DisposeAsync().ConfigureAwait(false); } finally { release(); GC.SuppressFinalize(this); }
        }
    }
}
