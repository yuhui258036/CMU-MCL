using System.Net;
using CMUmcl.Community;
using CMUmcl.Auth;
using CMUmcl.Content;
using CMUmcl.Network;

// User feedback: slow community page, instances that cannot be deleted.
internal static class FeedbackFixTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "cmumcl-feedback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await ProgressiveSearchChecks(check);
            await IconCacheChecks(root, check);
            RemovalChecks(root, check);
            await DiagnosisChecks(check);
        }
        finally { try { Directory.Delete(root, true); } catch { /* best effort */ } }
    }

    private static async Task ProgressiveSearchChecks(Action<bool, string> check)
    {
        var slowGate = new TaskCompletionSource<bool>();
        var fast = new FakeSource(ResourceSourceId.Modrinth, Task.FromResult(true), "fast");
        var slow = new FakeSource(ResourceSourceId.CurseForge, slowGate.Task, "slow");
        var catalog = new ResourceCatalog(fast, slow);
        var partial = new TaskCompletionSource<IReadOnlyList<ResourceProject>>();
        var search = catalog.SearchAsync(new ResourceQuery { Kind = ResourceKind.Mod, PageSize = 50 }, null, null, CancellationToken.None,
            projects => partial.TrySetResult(projects));
        var first = await Task.WhenAny(partial.Task, Task.Delay(5000));
        check(first == partial.Task && partial.Task.Result.All(p => p.Id.StartsWith("fast")) && !search.IsCompleted,
            "community: faster source is shown before the slower source answers");
        slowGate.SetResult(true);
        var page = await search;
        check(page.Projects.Count == 4 && page.Projects[0].Source == ResourceSourceId.Modrinth && page.Projects[3].Source == ResourceSourceId.CurseForge,
            "community: final page merges both sources in source order");
    }

    private static async Task IconCacheChecks(string root, Action<bool, string> check)
    {
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        string cache = Path.Combine(root, "icons");
        int requests = 0;
        const string url = "https://cdn.modrinth.com/data/fixture/icon.png";
        using (var online = new ResourceIconLoader(new Reply(_ => { requests++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) }; }), cache))
            check(await online.LoadAsync(url, CancellationToken.None) != null && requests == 1, "icons: first load downloads the icon");
        using (var offline = new ResourceIconLoader(new Reply(_ => { requests++; return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); }), cache))
            check(await offline.LoadAsync(url, CancellationToken.None) != null && requests == 1, "icons: later launches load the icon from disk without a request");
        check(Directory.GetFiles(cache, "*.img").Length == 1 && Directory.GetFiles(cache, "*.tmp").Length == 0, "icons: one cache file, no leftovers");
    }

    private static void RemovalChecks(string root, Action<bool, string> check)
    {
        string? oldRoot = LauncherPaths.RootOverride;
        LauncherPaths.RootOverride = Path.Combine(root, "data");
        string? recycled = null;
        InstanceRemoval.RecycleOverride = path => { recycled = path; Directory.Delete(path, true); };
        try
        {
            var installed = new InstalledStateStore();
            string dir = Path.Combine(LauncherPaths.InstancesDirectory, "to-delete");
            Directory.CreateDirectory(Path.Combine(dir, "saves", "world"));
            File.WriteAllText(Path.Combine(dir, "instance.json"), "{\"id\":\"local:to-delete\",\"game\":\"1.21.1\"}");
            installed.RecordInstall("local:to-delete", 1, "to-delete", "待删除");
            installed.RecordInstall("local:keep", 1, "keep", "保留");
            InstanceRemoval.Remove(installed, "local:to-delete", dir);
            check(recycled == Path.GetFullPath(dir) && !Directory.Exists(dir), "delete: instance folder goes to the recycle bin");
            check(!installed.ListRecords().ContainsKey("local:to-delete") && installed.ListRecords().ContainsKey("local:keep"), "delete: only the deleted record is removed");
            string outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            installed.RecordInstall("local:outside", 1, "outside", null);
            bool refused = false;
            try { InstanceRemoval.Remove(installed, "local:outside", outside); } catch (IOException) { refused = true; }
            check(refused && Directory.Exists(outside) && installed.ListRecords().ContainsKey("local:outside"), "delete: folders outside the instances directory are never touched");
        }
        finally { InstanceRemoval.RecycleOverride = null; LauncherPaths.RootOverride = oldRoot; }
    }

    // "Skin site down" must only be said when the rest of the internet is known to work.
    private static async Task DiagnosisChecks(Action<bool, string> check)
    {
        var requested = new List<string>();
        bool internet = true; Func<HttpResponseMessage?> skinSite = () => null;
        HttpResponseMessage Route(HttpRequestMessage request)
        {
            requested.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host == new Uri(YggdrasilClient.ApiRoot).Host)
                return skinSite() ?? throw new HttpRequestException("skin site refused");
            return internet ? new HttpResponseMessage(HttpStatusCode.OK) : throw new HttpRequestException("no route");
        }
        var unreachable = new YggdrasilException("网络错误") { Failure = YggdrasilFailure.Unreachable };
        var serverError = new YggdrasilException("皮肤站暂时不可用") { Failure = YggdrasilFailure.ServerError };
        bool adapter = true;
        ConnectionDiagnosis.HandlerOverride = new Reply(Route);
        ConnectionDiagnosis.NetworkAvailableOverride = () => adapter;
        try
        {
            check(await ConnectionDiagnosis.DiagnoseAsync(new YggdrasilException("密码错误")) == ConnectionProblem.Unknown && requested.Count == 0,
                "diagnosis: a regular skin-site error is not treated as a connection problem");

            adapter = false;
            check(await ConnectionDiagnosis.DiagnoseAsync(unreachable) == ConnectionProblem.LocalNetwork && requested.Count == 0,
                "diagnosis: no connected network adapter is the user's network, without probing");
            adapter = true;

            internet = false; requested.Clear();
            check(await ConnectionDiagnosis.DiagnoseAsync(unreachable) == ConnectionProblem.LocalNetwork
                && !requested.Contains(new Uri(YggdrasilClient.ApiRoot).Host) && requested.Count == ConnectionDiagnosis.ReferenceUrls.Length,
                "diagnosis: other sites unreachable too means the user's network, not the skin site");
            check(await ConnectionDiagnosis.DiagnoseAsync(serverError) == ConnectionProblem.LocalNetwork,
                "diagnosis: a 5xx seen while nothing else answers (e.g. a broken proxy) is not blamed on the skin site");

            internet = true; skinSite = () => null;
            check(await ConnectionDiagnosis.DiagnoseAsync(unreachable) == ConnectionProblem.SkinSiteDown,
                "diagnosis: internet works and the skin site still refuses connections, so the skin site is down");
            skinSite = () => new HttpResponseMessage(HttpStatusCode.BadGateway);
            check(await ConnectionDiagnosis.DiagnoseAsync(serverError) == ConnectionProblem.SkinSiteDown,
                "diagnosis: internet works and the skin site answers 502, so the skin site is down");
            skinSite = () => new HttpResponseMessage(HttpStatusCode.OK);
            check(await ConnectionDiagnosis.DiagnoseAsync(unreachable) == ConnectionProblem.Unknown,
                "diagnosis: the skin site answering on the re-check is reported as a blip, not an outage");
        }
        finally { ConnectionDiagnosis.HandlerOverride = null; ConnectionDiagnosis.NetworkAvailableOverride = null; }

        async Task<YggdrasilFailure?> FailureFor(Func<HttpRequestMessage, HttpResponseMessage> reply)
        {
            using var client = new YggdrasilClient(new Reply(reply));
            try { await client.ValidateAsync("token", "client"); return null; }
            catch (YggdrasilException ex) { return ex.Failure; }
        }
        check(await FailureFor(_ => throw new HttpRequestException("refused")) == YggdrasilFailure.Unreachable
            && await FailureFor(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)) == YggdrasilFailure.ServerError
            && await FailureFor(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)) == null,
            "diagnosis: the skin-site client tags refused connections and 5xx answers differently from a rejected token");
    }

    private sealed class FakeSource(ResourceSourceId id, Task gate, string prefix) : IResourceSource
    {
        public ResourceSourceId SourceId => id;
        public bool IsAvailable => true;
        public async Task<IReadOnlyList<ResourceProject>> SearchAsync(ResourceQuery query, CancellationToken ct)
        {
            await gate.ConfigureAwait(false);
            return new[] { new ResourceProject { Source = id, Id = prefix + "-1", Title = prefix + " 1" }, new ResourceProject { Source = id, Id = prefix + "-2", Title = prefix + " 2" } };
        }
        public Task<ResourceProject> GetProjectAsync(string projectId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ResourceVersion>> GetVersionsAsync(string projectId, ResourceQuery query, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Reply(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = reply(request); response.RequestMessage = request; return Task.FromResult(response);
        }
    }
}
