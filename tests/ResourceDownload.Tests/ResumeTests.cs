using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using CMUmcl.Content;

internal static class ResumeTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "cmumcl-download-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var bytes = RandomNumberGenerator.GetBytes(270000);
        var expected = new DownloadExpectation(bytes.Length, Sha256: Convert.ToHexString(SHA256.HashData(bytes)));
        try
        {
            var calls = 0;
            long? resumeOffset = null;
            using (var http = Client(request =>
            {
                calls++;
                if (calls == 1) return Response(new CutStream(bytes, 70000), bytes.Length);
                resumeOffset = request.Headers.Range?.Ranges.Single().From;
                return Range(bytes, resumeOffset ?? 0);
            }))
            {
                var target = Path.Combine(root, "retry.bin");
                await ResumableDownload.DownloadAsync(http, "https://cdn.example.com/retry", target, expected, null, default);
                check(calls == 2 && resumeOffset == 70000, "connection loss retries from acknowledged local byte offset");
                check(File.ReadAllBytes(target).SequenceEqual(bytes), "retried download has exact expected bytes");
                check(!File.Exists(target + ".partial") && !File.Exists(target + ".partial.json"), "verified completion removes partial metadata");
                await ResumableDownload.DownloadAsync(http, "https://cdn.example.com/retry", target, expected, null, default);
                check(calls == 2, "verified cached pack avoids repeated network transfer");
            }

            foreach (var mode in new[] { "range", "ignored", "bad-range", "changed-identity" })
            {
                var target = Path.Combine(root, mode + ".bin");
                File.WriteAllText(target, "old installed file");
                using var cancel = new CancellationTokenSource();
                using (var http = Client(_ => Response(new CutStream(bytes, 60000, cancel), bytes.Length)))
                {
                    var cancelled = false;
                    try { await ResumableDownload.DownloadAsync(http, "https://cdn.example.com/item", target, expected, null, cancel.Token); }
                    catch (OperationCanceledException) { cancelled = true; }
                    check(cancelled && new FileInfo(target + ".partial").Length == 60000, mode + ": cancellation preserves partial bytes");
                    check(File.ReadAllText(target) == "old installed file", mode + ": incomplete download cannot overwrite installed file");
                }
                long? requestedRange = null;
                var resumedBytes = mode == "changed-identity" ? RandomNumberGenerator.GetBytes(bytes.Length) : bytes;
                var resumedExpected = expected with { Sha256 = Convert.ToHexString(SHA256.HashData(resumedBytes)) };
                using var secondHttp = Client(request =>
                {
                    requestedRange = request.Headers.Range?.Ranges.Single().From;
                    return mode switch
                    {
                        "ignored" or "changed-identity" => Response(new MemoryStream(resumedBytes), resumedBytes.Length),
                        "bad-range" => Range(resumedBytes, 60001),
                        _ => Range(resumedBytes, requestedRange ?? 0)
                    };
                });
                if (mode == "bad-range")
                {
                    var rejected = false;
                    try { await ResumableDownload.DownloadAsync(secondHttp, "https://cdn.example.com/item", target, resumedExpected, null, default); }
                    catch (InvalidDataException) { rejected = true; }
                    check(rejected && File.ReadAllText(target) == "old installed file", "invalid Content-Range fails closed without changing installed bytes");
                    check(new FileInfo(target + ".partial").Length == 0, "invalid range prefix is discarded");
                }
                else
                {
                    await ResumableDownload.DownloadAsync(secondHttp, "https://cdn.example.com/item", target, resumedExpected, null, default);
                    check(requestedRange == (mode == "changed-identity" ? null : 60000), mode + ": restart uses only matching file identity");
                    check(File.ReadAllBytes(target).SequenceEqual(resumedBytes), mode + ": final file passes SHA-256");
                }
            }
            using (var http = Client(_ => Response(new MemoryStream(new byte[bytes.Length]), bytes.Length)))
            {
                var target = Path.Combine(root, "corrupt.bin");
                File.WriteAllText(target, "old");
                var rejected = false;
                try { await ResumableDownload.DownloadAsync(http, "https://cdn.example.com/corrupt", target, expected, null, default); }
                catch (InvalidDataException) { rejected = true; }
                check(rejected && File.ReadAllText(target) == "old", "corrupt full response never replaces the previous file");
            }
            var unavailableCalls = 0;
            using (var http = Client(_ => { unavailableCalls++; return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); }))
            {
                var rejected = false;
                try { await ResumableDownload.DownloadAsync(http, "https://cdn.example.com/busy", Path.Combine(root, "busy.bin"), expected, null, default); }
                catch (IOException) { rejected = true; }
                check(rejected && unavailableCalls == 4, "transient HTTP retries are bounded");
            }
            var notFoundCalls = 0;
            using (var http = Client(_ => { notFoundCalls++; return new HttpResponseMessage(HttpStatusCode.NotFound); }))
            {
                var rejected = false;
                try { await ResumableDownload.DownloadAsync(http, "https://cdn.example.com/missing", Path.Combine(root, "missing.bin"), expected, null, default); }
                catch (HttpRequestException) { rejected = true; }
                check(rejected && notFoundCalls == 1, "permanent HTTP failure is not retried");
            }
            var mirrors = new List<string>();
            using (var http = Client(request =>
            {
                mirrors.Add(request.RequestUri!.Host);
                return request.RequestUri.Host == "working.example.com"
                    ? Response(new MemoryStream(bytes), bytes.Length) : new HttpResponseMessage(HttpStatusCode.NotFound);
            }))
            {
                var target = Path.Combine(root, "mirror.bin");
                await ResumableDownload.DownloadMirrorsAsync(http,
                    ["http://unsafe.example.com/file", "https://broken.example.com/file", "https://working.example.com/file"],
                    target, expected, default);
                check(mirrors.SequenceEqual(["broken.example.com", "working.example.com"]), "mirror fallback skips insecure URL and tries next HTTPS source");
                check(File.ReadAllBytes(target).SequenceEqual(bytes), "fallback mirror still must pass expected hash");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> response) => new(new Transport(response));
    private static HttpResponseMessage Response(Stream stream, long length)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentLength = length;
        response.Headers.ETag = new EntityTagHeaderValue("\"test-version\"");
        return response;
    }
    private static HttpResponseMessage Range(byte[] bytes, long offset)
    {
        var response = Response(new MemoryStream(bytes[(int)offset..]), bytes.Length - offset);
        response.StatusCode = HttpStatusCode.PartialContent;
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, bytes.Length - 1, bytes.Length);
        return response;
    }
    private sealed class Transport(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(response(request));
    }
    private sealed class CutStream(byte[] bytes, int cut, CancellationTokenSource? cancellation = null) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position >= cut)
            {
                if (cancellation is not null) { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
                throw new IOException("Simulated connection loss");
            }
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, cut - (int)Position)], ct);
        }
    }
}
