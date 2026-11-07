using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMUmcl.Community;

internal static partial class Program
{
    // Offline Modrinth stand-in: the given file is installed at 1.0.0 and 1.1.0 is available.
    private static ModUpdateService CreateModUpdateFixture(string installedJar)
    {
        string hash;
        using (var sha=SHA1.Create()) hash=BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(installedJar))).Replace("-","").ToLowerInvariant();
        object Version(string id,string number,string date,string file)=>new{id,version_number=number,date_published=date,
            files=new[]{new{url="https://cdn.modrinth.com/data/fixture/"+file,filename=file,primary=true,size=3,hashes=new{sha1="0000000000000000000000000000000000000000",sha512=""}}}};
        var api=new ReplyHandler(request=>JsonReply(new Dictionary<string,object>{[hash]=request.RequestUri!.AbsolutePath.EndsWith("/update")
            ?Version("v2","1.1.0","2024-02-01T00:00:00Z","alpha-1.1.0.jar"):Version("v1","1.0.0","2024-01-01T00:00:00Z","alpha.jar")}));
        return new ModUpdateService(api,new ReplyHandler(_=>new HttpResponseMessage(HttpStatusCode.NotFound)));
    }
    private static HttpResponseMessage JsonReply(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
    private sealed class ReplyHandler(Func<HttpRequestMessage,HttpResponseMessage> reply):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){var response=reply(request);response.RequestMessage=request;return Task.FromResult(response);}
    }
}
