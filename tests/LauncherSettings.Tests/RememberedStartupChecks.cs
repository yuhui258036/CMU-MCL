using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using CMUmcl;
using CMUmcl.Auth;
using CMUmcl.Content;
using CMUmcl.Launch;
using CMUmcl.Network;

internal static partial class Program
{
    private sealed class RememberedStartupHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) => send(request,ct); }

    private static async Task RememberedStartupChecks(MainWindow window)
    {
        var field = typeof(MainWindow).GetField("_session",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var previous = (AuthSession)field.GetValue(window)!;
        var previousLaunch = window._launchOverride; var previousLogin = window.ReauthenticateForLaunch;
        var store = new AuthStore();
        var expired = new AuthSession {AccessToken="startup-expired",ClientToken="fixture-client",SelectedProfileId=new string('a',32),SelectedProfileName="fixture",Username="fixture-user"};
        int passwords = 0, dialogs = 0;
        bool unavailable = false, holdAuthentication = true;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        HttpResponseMessage Response(HttpStatusCode status,object? body=null) => new(status){Content=new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json")};
        using var client = new YggdrasilClient(new RememberedStartupHandler(async (request,ct) =>
        {
            string path=request.RequestUri!.AbsolutePath;
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if(path.EndsWith("/authenticate"))
            {
                passwords++;
                if(holdAuthentication) await release.Task;
                if(unavailable) return Response(HttpStatusCode.ServiceUnavailable);
                return Response(HttpStatusCode.OK,new {accessToken="startup-restored",clientToken="fixture-client",selectedProfile=new{id=expired.SelectedProfileId,name="fixture"}});
            }
            return Response(path.EndsWith("/validate") && body.RootElement.GetProperty("accessToken").GetString()=="startup-restored" ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden);
        }));
        try
        {
            field.SetValue(window,expired); store.SaveLogin(expired,"fixture-user","fixture-only-password",true);
            window.ReauthenticateForLaunch=()=>{dialogs++;return Task.FromResult<AuthSession?>(null);};
            var gate = new AuthSessionGate(client,store);
            window._launchOverride=async (_,ct)=>new LaunchResult{Session=await gate.RunAsync((AuthSession)field.GetValue(window)!,Task.FromResult,ct),Process=null!};
            var background=window.VerifyRestoredSessionAsync((session,ct)=>gate.RunAsync(session,Task.FromResult,ct));
            await Until(()=>passwords==1);
            Check(!background.IsCompleted && Get<TextBlock>(window,"AccountStateText").Text=="正在确认…", "merged startup opens and stays responsive during remembered-password authentication");
            var launch=window.LaunchWithLoginRecoveryAsync(new InstanceCardViewModel("local:remembered-startup","fixture","本地实例",null,_output),null,default);
            release.SetResult(true); await background; var result=await launch;
            Check(passwords==1 && dialogs==0 && result.Session.AccessToken=="startup-restored", "background confirmation and concurrent launch share one automatic authentication with no login dialog");
            Check(store.HasRememberedPassword && store.LoadSession()?.AccessToken=="startup-restored" && Get<TextBlock>(window,"AccountStateText").Text=="已登录", "merged background startup silently adopts and preserves verified remembered login");

            field.SetValue(window,expired); store.SaveLogin(expired,"fixture-user","fixture-only-password",true);
            holdAuthentication=false; unavailable=true;
            ConnectionDiagnosis.Override=(_,_)=>Task.FromResult(ConnectionProblem.Unknown);
            gate=new AuthSessionGate(client,store);
            await window.VerifyRestoredSessionAsync((session,ct)=>gate.RunAsync(session,Task.FromResult,ct));
            Check(store.HasRememberedPassword && store.LoadSession()?.AccessToken=="startup-expired" && dialogs==0, "merged connection diagnosis preserves saved password during authentication outage");
            unavailable=false;
            await window.VerifyRestoredSessionAsync((session,ct)=>gate.RunAsync(session,Task.FromResult,ct));
            Check(((AuthSession)field.GetValue(window)!).AccessToken=="startup-restored" && !Get<System.Windows.FrameworkElement>(window,"AccountNotice").IsVisible,
                "a later successful automatic login clears the merged startup network warning");
        }
        finally { ConnectionDiagnosis.Override=null; store.ClearSession();field.SetValue(window,previous);window._launchOverride=previousLaunch;window.ReauthenticateForLaunch=previousLogin; }
    }

    private static async Task RememberedStartupCancellationCheck(MainWindow window)
    {
        bool requestCancelled=false;
        var check=window.VerifyRestoredSessionAsync(async (session,ct)=>
        {
            try { await Task.Delay(Timeout.Infinite,ct); return session; }
            catch(OperationCanceledException){requestCancelled=true;throw;}
        });
        var lifetime=(CancellationTokenSource)typeof(MainWindow).GetField("_updateLifetime",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
        lifetime.Cancel();await check;
        Check(requestCancelled && !Get<System.Windows.FrameworkElement>(window,"InlinePromptOverlay").IsVisible,
            "window lifetime cancels startup authentication without reporting a network error or opening login");
    }

    private static async Task StartupAndLaunchLoginPromptChecks(MainWindow window)
    {
        var field=typeof(MainWindow).GetField("_session",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var previous=(AuthSession)field.GetValue(window)!;
        var previousLogin=window.ReauthenticateForLaunch;var previousLaunch=window._launchOverride;
        var old=new AuthSession{AccessToken="old",ClientToken="client",SelectedProfileId=new string('a',32),SelectedProfileName="fixture"};
        var fresh=new AuthSession{AccessToken="fresh",ClientToken="client",SelectedProfileId=old.SelectedProfileId,SelectedProfileName="fixture"};
        var store=new AuthStore();
        var card=new InstanceCardViewModel("local:prompt-race","fixture","本地实例",null,_output);
        try
        {
            foreach(bool startupFirst in new[]{true,false})
            {
                field.SetValue(window,old);store.SaveSession(old);
                int dialogs=0,attempts=0;
                var login=new TaskCompletionSource<AuthSession?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var verification=new TaskCompletionSource<AuthSession>(TaskCreationOptions.RunContinuationsAsynchronously);
                window.ReauthenticateForLaunch=()=>{dialogs++;return login.Task;};
                window._launchOverride=(_,_)=>++attempts==1 ? Task.FromException<LaunchResult>(new LaunchException("expired",new YggdrasilException("fixture",true)))
                    : Task.FromResult(new LaunchResult{Session=fresh,Process=null!});
                var background=window.VerifyRestoredSessionAsync(_=>verification.Task);
                Task<LaunchResult> launch;
                if(startupFirst)
                {
                    verification.SetException(new YggdrasilException("expired",true));
                    await Until(()=>Get<System.Windows.FrameworkElement>(window,"InlinePromptOverlay").IsVisible);
                    launch=window.LaunchWithLoginRecoveryAsync(card,null,default);
                }
                else
                {
                    launch=window.LaunchWithLoginRecoveryAsync(card,null,default);
                    await Until(()=>dialogs==1);
                    verification.SetException(new YggdrasilException("expired",true));
                }
                await Until(()=>dialogs==1);await Task.Delay(15);
                Check(!Get<System.Windows.FrameworkElement>(window,"InlinePromptOverlay").IsVisible,
                    "startup verification and launch recovery share login without leaving a stale expired-session prompt");
                login.SetResult(fresh);await Task.WhenAll(background,launch);
                Check(dialogs==1 && attempts==2 && ReferenceEquals(field.GetValue(window),fresh),
                    "both startup/launch completion orders recover once and continue the original launch");
            }
        }
        finally {store.ClearSession();field.SetValue(window,previous);window.ReauthenticateForLaunch=previousLogin;window._launchOverride=previousLaunch;}
    }
}
