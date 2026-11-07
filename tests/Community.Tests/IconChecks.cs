using System.Net;
using System.Net.Http;
using CMUmcl.Community;

internal static partial class Program
{
    private static void IconChecks()
    {
        Check(new ResourceCardViewModel(new ResourceProject(),CompatibilityHint.Unknown,false).HintText=="","modpack: hidden compatibility has no unknown text");
        Check(new ResourceCardViewModel(new ResourceProject(),CompatibilityHint.Unknown).HintText=="兼容性未知","mod: unknown compatibility remains honest");
        using(var bounded=new ResourceIconLoader(new IconConcurrencyHandler()))
        {
            IconConcurrencyHandler.Peak=0;
            Task.WhenAll(Enumerable.Range(0,20).Select(i=>bounded.LoadAsync("https://cdn.modrinth.com/"+i+".png",default))).GetAwaiter().GetResult();
            Check(IconConcurrencyHandler.Peak<=6&&IconConcurrencyHandler.Peak>1,"icon: concurrent requests bounded at six");
            using var cancel=new CancellationTokenSource();cancel.Cancel();Check(bounded.LoadAsync("https://cdn.modrinth.com/cancel.png",cancel.Token).GetAwaiter().GetResult()==null,"icon: canceled queued request falls back safely");
        }
        // 1. Non-allowlisted icon host returns null (placeholder).
        {
            var handler = new PassthroughHandler();
            using var loader = new ResourceIconLoader(handler);
            var result = loader.LoadAsync("https://evil.example.com/icon.png", CancellationToken.None).GetAwaiter().GetResult();
            Check(result == null && handler.Calls == 0, "icon: non-allowlisted host rejected before request");
        }

        // 2. Non-HTTPS icon URL returns null.
        {
            var handler = new PassthroughHandler();
            using var loader = new ResourceIconLoader(handler);
            var result = loader.LoadAsync("http://cdn.modrinth.com/icon.png", CancellationToken.None).GetAwaiter().GetResult();
            Check(result == null, "icon: non-HTTPS rejected");
        }

        // 3. Oversized (>8 MiB) response returns null.
        {
            var handler = new PassthroughHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(new byte[9 * 1024 * 1024]) });
            using var loader = new ResourceIconLoader(handler);
            var result = loader.LoadAsync("https://cdn.modrinth.com/icon.png", CancellationToken.None).GetAwaiter().GetResult();
            Check(result == null, "icon: oversized response rejected");
        }

        // 4. Invalid image bytes return null (placeholder fallback).
        {
            var handler = new PassthroughHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 }) });
            using var loader = new ResourceIconLoader(handler);
            var result = loader.LoadAsync("https://cdn.modrinth.com/icon.png", CancellationToken.None).GetAwaiter().GetResult();
            Check(result == null, "icon: invalid image bytes fall back to placeholder");
        }
    }

    private sealed class IconConcurrencyHandler:HttpMessageHandler
    {
        private int active;internal static int Peak;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            int count=Interlocked.Increment(ref active);int previous;
            do{previous=Peak;if(previous>=count)break;}while(Interlocked.CompareExchange(ref Peak,count,previous)!=previous);
            try{await Task.Delay(20,ct);return new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[]{1,2,3})};}finally{Interlocked.Decrement(ref active);}
        }
    }

    private sealed class PassthroughHandler(Func<HttpResponseMessage>? reply = null) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(reply?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[0]) });
        }
    }
}
