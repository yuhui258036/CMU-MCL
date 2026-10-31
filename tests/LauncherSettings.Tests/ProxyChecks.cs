using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using CMUmcl;
using CMUmcl.Content;
using CMUmcl.Network;

internal static partial class Program
{
    private static async Task ProxyUiChecks(MainWindow window,SettingsStore store)
    {
        Click(window,"ProxyTab");
        Check(Get<Border>(window,"ProxyCard").IsVisible,"proxy tab displays HMCL-style settings");
        Get<ComboBox>(window,"ProxyModeCombo").SelectedIndex=(int)LauncherProxyMode.Http;
        Check(store.Load().ProxyMode==LauncherProxyMode.System && Get<TextBlock>(window,"ProxyErrorText").Text.Length>0,"incomplete custom proxy does not replace active configuration");
        Get<TextBox>(window,"ProxyHostTextBox").Text="127.0.0.1";
        Get<TextBox>(window,"ProxyPortTextBox").Text="7890";
        Check(store.Load().ProxyMode==LauncherProxyMode.Http && store.Load().ProxyPort==7890,"valid custom proxy autosaves");
        Get<CheckBox>(window,"ProxyAuthenticationCheck").IsChecked=true;
        Get<TextBox>(window,"ProxyUsernameTextBox").Text="fixture";
        Get<PasswordBox>(window,"ProxyPasswordBox").Password="fixture-password";
        Check(!File.ReadAllText(LauncherPaths.SettingsPath).Contains("fixture-password") && LauncherProxy.Unprotect(store.Load().ProxyPasswordProtected)=="fixture-password","UI proxy password stored encrypted and recoverable");
        Get<TextBox>(window,"ProxyPortTextBox").Text="70000";
        Check(store.Load().ProxyPort==7890,"invalid proxy port preserves last valid configuration");
        Get<TextBox>(window,"ProxyPortTextBox").Text="7890";
        Screenshot(window,"proxy-dark.png");
        Click(window,"ThemeToggleButton");Screenshot(window,"proxy-light.png");
        Get<ComboBox>(window,"ProxyModeCombo").SelectedIndex=(int)LauncherProxyMode.Direct;
        Check(store.Load().ProxyMode==LauncherProxyMode.Direct && !Get<System.Windows.FrameworkElement>(window,"ProxyDetailsPanel").IsEnabled,"direct mode saves and disables custom fields");
        Get<ComboBox>(window,"ProxyModeCombo").SelectedIndex=(int)LauncherProxyMode.System;
        Check(store.Load().ProxyMode==LauncherProxyMode.System,"system proxy restored through UI");
        // Remove fixture credentials without connecting to an actual local proxy.
        Get<ComboBox>(window,"ProxyModeCombo").SelectedIndex=(int)LauncherProxyMode.Http;
        Get<CheckBox>(window,"ProxyAuthenticationCheck").IsChecked=false;
        Get<PasswordBox>(window,"ProxyPasswordBox").Password="";
        Get<TextBox>(window,"ProxyUsernameTextBox").Text="";
        Get<ComboBox>(window,"ProxyModeCombo").SelectedIndex=(int)LauncherProxyMode.System;
        await Task.CompletedTask;
    }
    private static async Task ProxyChecks(SettingsStore store)
    {
        var original=store.Load();
        var config=SettingsStore.CreateDefaults();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct=timeout.Token;
        var releaseStream=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int httpRequests=0, originRequests=0, socksRequests=0, connects=0;
        string expectedAuth="Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture:secret"));
        bool leakedOriginAuth=false,socksAuthenticated=false;
        await using var origin=new LocalProxyFixture(async stream=>
        {
            var header=await ReadHeader(stream,ct);Interlocked.Increment(ref originRequests);
            if(header.Contains("Proxy-Authorization",StringComparison.OrdinalIgnoreCase)) leakedOriginAuth=true;
            await Reply(stream,"origin",ct);
        });
        await using var proxy=new LocalProxyFixture(async stream=>
        {
            var header=await ReadHeader(stream,ct);Interlocked.Increment(ref httpRequests);
            if(!header.Contains("Proxy-Authorization: "+expectedAuth,StringComparison.OrdinalIgnoreCase))
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"fixture\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),ct);return;
            }
            if(header.StartsWith("CONNECT "))
            {
                Interlocked.Increment(ref connects);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),ct);return;
            }
            if(header.Contains("/stream "))
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 6\r\nConnection: close\r\n\r\nabc"),ct);
                await releaseStream.Task.WaitAsync(ct);await stream.WriteAsync("def"u8.ToArray(),ct);return;
            }
            await Reply(stream,"proxy",ct);
        });
        await using var socks=new LocalProxyFixture(async stream=>
        {
            byte[] greeting=new byte[2];await stream.ReadExactlyAsync(greeting,ct);
            byte[] methods=new byte[greeting[1]];await stream.ReadExactlyAsync(methods,ct);
            await stream.WriteAsync(new byte[]{5,2},ct);
            byte[] auth=new byte[2];await stream.ReadExactlyAsync(auth,ct);
            byte[] user=new byte[auth[1]];await stream.ReadExactlyAsync(user,ct);
            byte[] length=new byte[1];await stream.ReadExactlyAsync(length,ct);
            byte[] pass=new byte[length[0]];await stream.ReadExactlyAsync(pass,ct);
            socksAuthenticated=Encoding.UTF8.GetString(user)=="fixture" && Encoding.UTF8.GetString(pass)=="secret";
            await stream.WriteAsync(new byte[]{1,(byte)(socksAuthenticated?0:1)},ct);
            byte[] connect=new byte[4];await stream.ReadExactlyAsync(connect,ct);
            int count=connect[3] switch {1=>4,4=>16,3=>0,_=>throw new IOException("SOCKS address type")};
            if(count==0){await stream.ReadExactlyAsync(length,ct);count=length[0];}
            byte[] target=new byte[count+2];await stream.ReadExactlyAsync(target,ct);
            await stream.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,0,80},ct);
            await ReadHeader(stream,ct);Interlocked.Increment(ref socksRequests);await Reply(stream,"socks",ct);
        });
        try
        {
            Check(config.ProxyMode==LauncherProxyMode.System,"new settings default to system proxy without port probing");
            Check(LauncherProxy.TryAddress(LauncherProxyMode.Http,"[::1]",8080,out var ipv6) && ipv6!.Host.Contains("::1"),"proxy address accepts IPv6");
            Check(!LauncherProxy.TryAddress(LauncherProxyMode.Http,"http://user:pass@host",8080,out _) && !LauncherProxy.TryAddress(LauncherProxyMode.Http,"localhost",0,out _),"proxy address rejects embedded credentials and invalid port");
            config.ProxyMode=LauncherProxyMode.Direct;store.Save(config);
            using var client=new HttpClient(LauncherProxy.CreateHandler()){Timeout=TimeSpan.FromSeconds(5)};
            Check(await client.GetStringAsync(origin.Url,ct)=="origin","direct request reaches owned origin");
            config.ProxyMode=LauncherProxyMode.Http;config.ProxyHost="127.0.0.1";config.ProxyPort=proxy.Port;
            config.ProxyAuthentication=true;config.ProxyUsername="fixture";config.ProxyPasswordProtected=LauncherProxy.Protect("secret");store.Save(config);
            Check(await client.GetStringAsync("http://proxy-target.invalid/test",ct)=="proxy" && httpRequests>=2,"existing client switches to HTTP proxy and completes 407 authentication");
            Check(!File.ReadAllText(LauncherPaths.SettingsPath).Contains("secret") && LauncherProxy.Unprotect(config.ProxyPasswordProtected)=="secret","DPAPI proxy credential persistence contains no plaintext secret");
            using var held=await client.GetAsync("http://proxy-target.invalid/stream",HttpCompletionOption.ResponseHeadersRead,ct);
            config.ProxyMode=LauncherProxyMode.Direct;store.Save(config);
            Check(await client.GetStringAsync(origin.Url,ct)=="origin","same client switches back to direct for new requests");
            releaseStream.TrySetResult();
            Check(await held.Content.ReadAsStringAsync(ct)=="abcdef","proxy switch preserves an in-flight streamed download");
            Check(!leakedOriginAuth && originRequests==2,"proxy credentials never sent to direct origin");
            config.ProxyMode=LauncherProxyMode.Http;store.Save(config);
            using(var streamed=await client.GetStreamAsync("http://proxy-target.invalid/test",ct))
            using(var reader=new StreamReader(streamed))
                Check(await reader.ReadToEndAsync(ct)=="proxy","GetStreamAsync preserves streamed proxy content and disposes its lease");
            bool connectFailed=false;try{await client.GetAsync("https://proxy-target.invalid/test",ct);}catch(HttpRequestException){connectFailed=true;}
            Check(connectFailed && connects==1,"HTTPS uses authenticated CONNECT and fails closed on tunnel rejection");
            config.ProxyMode=LauncherProxyMode.Socks5;config.ProxyPort=socks.Port;store.Save(config);
            Check(await client.GetStringAsync("http://socks-target.invalid/test",ct)=="socks" && socksAuthenticated && socksRequests==1,"SOCKS5 handshake and username/password authentication carry HTTP request");
            using var thirdParty=new HttpClient(){Timeout=TimeSpan.FromSeconds(5)};
            Check(await thirdParty.GetStringAsync("http://socks-target.invalid/default",ct)=="socks","third-party default HttpClient follows process-local proxy");
            config.ProxyHost="invalid/path";store.Save(config);
            bool invalidRejected=false;try{await client.GetStringAsync(origin.Url,ct);}catch(HttpRequestException){invalidRejected=true;}
            Check(invalidRejected && originRequests==2,"corrupt saved custom proxy never silently falls back to direct");
        }
        finally {releaseStream.TrySetResult();store.Save(original);}
    }
    private static async Task<string> ReadHeader(Stream stream,CancellationToken ct)
    {
        var bytes=new List<byte>();byte[] b=new byte[1];
        while(bytes.Count<32768)
        {
            await stream.ReadExactlyAsync(b,ct);bytes.Add(b[0]);
            if(bytes.Count>=4 && bytes[^4]==13 && bytes[^3]==10 && bytes[^2]==13 && bytes[^1]==10) return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new IOException("Header limit");
    }
    private static Task Reply(Stream stream,string body,CancellationToken ct) => stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"),ct).AsTask();
}
internal sealed class LocalProxyFixture : IAsyncDisposable
{
    private readonly TcpListener _listener=new(IPAddress.Loopback,0);
    private readonly CancellationTokenSource _stop=new();
    private readonly Task _loop;
    private readonly List<Task> _connections=[];
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string Url => $"http://127.0.0.1:{Port}/";
    public LocalProxyFixture(Func<NetworkStream,Task> serve)
    {
        _listener.Start();
        _loop=Task.Run(async()=>
        {
            try {while(!_stop.IsCancellationRequested){var tcp=await _listener.AcceptTcpClientAsync(_stop.Token);_connections.Add(Task.Run(async()=>{using(tcp){await serve(tcp.GetStream());}}));}}
            catch(OperationCanceledException){}
        });
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();_listener.Stop();await _loop;
        await Task.WhenAll(_connections);_stop.Dispose();
    }
}
