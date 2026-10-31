using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CMUmcl.Content;

internal sealed record DownloadExpectation(long? Size, string? Sha512 = null, string? Sha256 = null, string? Sha1 = null)
{
    public bool HasHash => !string.IsNullOrWhiteSpace(Sha512) || !string.IsNullOrWhiteSpace(Sha256) || !string.IsNullOrWhiteSpace(Sha1);
    public bool Matches(DownloadedContent content) => (Size is null || content.Size == Size) &&
        (!string.IsNullOrWhiteSpace(Sha512) ? content.Sha512.Equals(Sha512.Trim(), StringComparison.OrdinalIgnoreCase) :
         !string.IsNullOrWhiteSpace(Sha256) ? content.Sha256.Equals(Sha256.Trim(), StringComparison.OrdinalIgnoreCase) :
         !string.IsNullOrWhiteSpace(Sha1) ? content.Sha1.Equals(Sha1.Trim(), StringComparison.OrdinalIgnoreCase) : true);
}

internal sealed class DownloadedContent
{
    public string Sha1 { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string Sha512 { get; init; } = "";
    public long Size { get; init; }
}

internal static class ResumableDownload
{
    private sealed record Receipt(string Identity, string? ETag);
    private const long MaximumBytes = 2L * 1024 * 1024 * 1024;

    public static async Task<DownloadedContent> DownloadMirrorsAsync(HttpClient http, IEnumerable<string> urls,
        string target, DownloadExpectation expected, CancellationToken ct)
    {
        Exception? failure = null;
        foreach (var url in urls.Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme == "https")
                     .Distinct(StringComparer.Ordinal).Take(4))
        {
            try { return await DownloadAsync(http, url, target, expected, null, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException) { failure = ex; }
        }
        throw new IOException("文件没有可用的 HTTPS 下载源。", failure);
    }

    // All data is staged beside the destination. No invalid or partial bytes
    // replace the installed/cached file. Hash identity, rather than URL/length
    // alone, binds persisted partial bytes to a particular resource version.
    public static async Task<DownloadedContent> DownloadAsync(HttpClient http, string url, string target,
        DownloadExpectation expected, Action<long, long?>? progress, CancellationToken ct)
    {
        if (expected.Size is < 0 or > MaximumBytes) throw new InvalidDataException("下载文件大小超限。");
        var limit = expected.Size ?? MaximumBytes;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (expected.HasHash && File.Exists(target) && new FileInfo(target).Length <= limit &&
            (expected.Size is null || new FileInfo(target).Length == expected.Size))
        {
            await using var cached = File.OpenRead(target);
            var verified = await HashAsync(cached, ct).ConfigureAwait(false);
            if (expected.Matches(verified)) { progress?.Invoke(verified.Size, verified.Size); return verified; }
        }
        var partial = target + ".partial";
        var receiptPath = partial + ".json";
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { url, expected }))));
        Receipt? receipt = null;
        try { if (File.Exists(receiptPath)) receipt = JsonSerializer.Deserialize<Receipt>(await File.ReadAllBytesAsync(receiptPath, ct).ConfigureAwait(false)); }
        catch (JsonException) { /* Interrupted metadata: restart from zero. */ }
        DownloadedContent result;
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(TimeSpan.FromMinutes(30));
        await using (var output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (!expected.HasHash || receipt?.Identity != identity || output.Length > limit)
            { output.SetLength(0); receipt = new Receipt(identity, null); }
            receipt ??= new Receipt(identity, null);
            await SaveReceiptAsync(receiptPath, receipt, overall.Token).ConfigureAwait(false);
            Exception? lastFailure = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                overall.Token.ThrowIfCancellationRequested();
                // A fully received partial may remain after cancellation/response loss.
                if (expected.HasHash && expected.Size == output.Length)
                {
                    output.Position = 0;
                    result = await HashAsync(output, overall.Token).ConfigureAwait(false);
                    if (expected.Matches(result)) goto Verified;
                    output.SetLength(0);
                }
                try
                {
                    var offset = expected.HasHash ? output.Length : 0;
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.AcceptEncoding.ParseAdd("identity");
                    if (offset > 0)
                    {
                        request.Headers.Range = new RangeHeaderValue(offset, null);
                        if (EntityTagHeaderValue.TryParse(receipt.ETag, out var tag) && !tag.IsWeak)
                            request.Headers.IfRange = new RangeConditionHeaderValue(tag);
                    }
                    using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                    headerTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);
                    headerTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
                    { output.SetLength(0); throw new IOException("服务器不接受当前断点，重新下载。"); }
                    if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                        throw new HttpRequestException("下载服务暂时不可用。", null, response.StatusCode);
                    response.EnsureSuccessStatusCode();
                    long? total;
                    if (response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        var range = response.Content.Headers.ContentRange;
                        if (range?.Unit != "bytes" || range.From != offset || range.To is null || range.Length is null ||
                            range.To < range.From || range.To >= range.Length || range.Length > limit ||
                            (expected.Size is long size && range.Length != size) ||
                            (response.Content.Headers.ContentLength is long length && length != range.To - range.From + 1))
                            throw new InvalidDataException("服务器返回了无效的下载范围。");
                        total = range.Length;
                    }
                    else if (response.StatusCode == HttpStatusCode.OK)
                    {
                        // Range ignored / If-Range validator changed: overwrite the
                        // partial from zero, never append a full response to a prefix.
                        output.SetLength(0); offset = 0;
                        total = response.Content.Headers.ContentLength;
                        if (total > limit || (total is not null && expected.Size is long size && total != size))
                            throw new InvalidDataException("下载响应大小与清单不匹配。");
                    }
                    else throw new InvalidDataException("不支持的下载响应状态。");
                    var etag = response.Headers.ETag;
                    receipt = new Receipt(identity, etag is { IsWeak: false } ? etag.ToString() : null);
                    await SaveReceiptAsync(receiptPath, receipt, overall.Token).ConfigureAwait(false);
                    output.Position = offset;
                    await using var input = await response.Content.ReadAsStreamAsync(overall.Token).ConfigureAwait(false);
                    var buffer = new byte[131072];
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                    while (true)
                    {
                        idle.CancelAfter(TimeSpan.FromSeconds(60));
                        var read = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                        idle.CancelAfter(Timeout.InfiniteTimeSpan);
                        if (read == 0) break;
                        if (output.Position + read > limit) throw new InvalidDataException("下载超出声明大小。");
                        await output.WriteAsync(buffer.AsMemory(0, read), overall.Token).ConfigureAwait(false);
                        progress?.Invoke(output.Position, expected.Size ?? total);
                    }
                    await output.FlushAsync(overall.Token).ConfigureAwait(false);
                    if (total is long advertised && output.Length != advertised)
                        throw new IOException("下载连接提前结束，将继续剩余部分。");
                    output.Position = 0;
                    result = await HashAsync(output, overall.Token).ConfigureAwait(false);
                    if (!expected.Matches(result)) throw new InvalidDataException("下载文件哈希或大小不匹配。");
                    goto Verified;
                }
                catch (InvalidDataException)
                {
                    output.SetLength(0); // Invalid data must not be reused on retry.
                    throw;
                }
                catch (HttpRequestException ex) when (ex.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int?)ex.StatusCode >= 500)
                { lastFailure = ex; }
                catch (IOException ex) { lastFailure = ex; }
                catch (OperationCanceledException ex) when (!overall.IsCancellationRequested) { lastFailure = ex; }
                await output.FlushAsync(overall.Token).ConfigureAwait(false);
                if (attempt < 3) await Task.Delay(300 * (1 << attempt), overall.Token).ConfigureAwait(false);
            }
            throw new IOException("多次下载失败，已保留断点，可稍后重试。", lastFailure);
        Verified:
            await output.FlushAsync(overall.Token).ConfigureAwait(false);
        }
        File.Move(partial, target, overwrite: true);
        File.Delete(receiptPath);
        progress?.Invoke(result.Size, result.Size);
        return result;
    }

    private static async Task SaveReceiptAsync(string path, Receipt receipt, CancellationToken ct)
    {
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(receipt), ct).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private static async Task<DownloadedContent> HashAsync(Stream stream, CancellationToken ct)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        var buffer = new byte[131072];
        long size = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sha1.AppendData(buffer, 0, read); sha256.AppendData(buffer, 0, read); sha512.AppendData(buffer, 0, read);
            size += read;
        }
        return new DownloadedContent { Size = size,
            Sha1 = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(),
            Sha256 = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant(),
            Sha512 = Convert.ToHexString(sha512.GetHashAndReset()).ToLowerInvariant() };
    }
}
