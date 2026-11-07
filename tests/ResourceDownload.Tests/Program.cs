using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMUmcl.Content;

try {
var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("ok: " + name); }
using var key = new ECDsaCng(256);
var manifest = Encoding.UTF8.GetBytes("""{"formatVersion":1,"instances":[]}""");
var signature = JsonSerializer.SerializeToUtf8Bytes(new
{
    algorithm = "ECDSA-P256-SHA256",
    signature = Convert.ToBase64String(key.SignData(manifest, HashAlgorithmName.SHA256))
});
using (var handler = new Handler((request, _) => Reply(request.RequestUri!.AbsolutePath.EndsWith(".sig") ? signature : manifest)))
using (var client = new ResourceManifestClient(handler, new Uri("https://cdn.example.com/"), Compat.ExportPem((ECDsaCng)key)))
{
    Check((await client.GetManifestAsync()).FormatVersion == 1, "valid signed pair accepted");
    Check(handler.Calls == 2, "valid pair needs exactly two requests");
}
using (var handler = new Handler((request, _) =>
{
    if (request.RequestUri!.AbsolutePath.EndsWith(".sig")) return Reply(signature);
    return Reply(request.RequestUri.Query.Length == 0 ? Encoding.UTF8.GetBytes("{\"old\":true}") : manifest);
}))
using (var client = new ResourceManifestClient(handler, new Uri("https://cdn.example.com/"), Compat.ExportPem((ECDsaCng)key)))
{
    Check((await client.GetManifestAsync()).FormatVersion == 1, "publication/cache mismatch recovers with fresh verified pair");
    Check(handler.Calls == 4 && handler.RefreshCalls == 2, "retry revalidates both URLs and bypasses stale cache key");
}
using (var handler = new Handler((request, _) => Reply(request.RequestUri!.AbsolutePath.EndsWith(".sig") ? signature : [1, 2, 3])))
using (var client = new ResourceManifestClient(handler, new Uri("https://cdn.example.com/"), Compat.ExportPem((ECDsaCng)key)))
{
    var rejected = false;
    try { await client.GetManifestAsync(); } catch (ResourceException) { rejected = true; }
    Check(rejected && handler.Calls == 6, "persistent invalid signature fails closed after bounded retries");
}
using (var handler = new Handler((_, _) => Reply(new byte[4 * 1024 * 1024 + 1])))
using (var client = new ResourceManifestClient(handler, new Uri("https://cdn.example.com/"), Compat.ExportPem((ECDsaCng)key)))
{
    var rejected = false;
    try { await client.GetManifestAsync(); } catch (ResourceException) { rejected = true; }
    Check(rejected, "oversized manifest cannot exhaust memory");
}
using (var handler = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))
using (var client = new ResourceManifestClient(handler, new Uri("https://cdn.example.com/"), Compat.ExportPem((ECDsaCng)key)))
{
    var rejected = false;
    try { await client.GetManifestAsync(); } catch (ResourceException) { rejected = true; }
    Check(rejected, "HTTP errors remain user-facing ResourceException");
}
Check(CMUmcl.Community.CommunityHosts.ResourceCdn.Contains("csumcl.mcapcs.cn") &&
      CMUmcl.Community.CommunityHosts.TryGetHttpsHost("https://csumcl.mcapcs.cn/icons/" + new string('a', 64) + ".png", out var iconHost) &&
      iconHost == "csumcl.mcapcs.cn", "server pack icon host is allowlisted for HTTPS loading");
await ResumeTests.Run(Check);
await FullInstallCleanupTests.Run(Check);
await LauncherFeatureTests.Run(Check);
await FeedbackFixTests.Run(Check);
await UpdateFeedTests.Run(Check);
await UpdateStageTests.Run(Check);
if(args.Length!=0) throw new ArgumentException("No backend operations in this isolated suite");
Console.WriteLine($"{passed} resource download checks passed.");


} catch(Exception ex) {for(Exception e=ex;e!=null;e=e.InnerException){Console.WriteLine(e.GetType().FullName);Console.WriteLine(e.Message);}Console.WriteLine(ex.StackTrace);return 1;}
return 0;
static HttpResponseMessage Reply(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handle) : HttpMessageHandler
{
    public int Calls;
    public int RefreshCalls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (request.Headers.CacheControl?.NoCache == true && request.RequestUri!.Query.Length > 0) Interlocked.Increment(ref RefreshCalls);
        return Task.FromResult(handle(request, ct));
    }
}
