using System.Reflection;
using CMUmcl;
using CMUmcl.Auth;
using CMUmcl.Content;
using CMUmcl.Launch;

internal static partial class Program
{
    private static async Task AuthRecoveryChecks(MainWindow window)
    {
        var field = typeof(MainWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!;
        AuthSession Session() => (AuthSession)field.GetValue(window)!;
        AuthSession Fixture(string token) => new() { AccessToken = token, ClientToken = "fixture", SelectedProfileId = new string('a',32), SelectedProfileName = token };
        var old = Fixture("old"); var fresh = Fixture("fresh");
        var vm = new InstanceCardViewModel("local:auth-recovery", "fixture", "本地实例", null, _output);
        LaunchException Expired() => new("fixture auth failure", new YggdrasilException("fixture expired", true));
        LaunchResult Result(AuthSession session) => new() { Session = session, Process = null! };
        int attempts = 0, prompts = 0;
        try
        {
            field.SetValue(window, old);
            window.ReauthenticateForLaunch = () => { prompts++; return Task.FromResult<AuthSession?>(fresh); };
            window._launchOverride = (_, _) => ++attempts == 1 ? Task.FromException<LaunchResult>(Expired()) : Task.FromResult(Result(Session()));
            var result = await window.LaunchWithLoginRecoveryAsync(vm, null, default);
            Check(attempts == 2 && prompts == 1 && result.Session.AccessToken == "fresh", "auth recovery reauthenticates once and resumes original launch with fresh session");
            Check(Get<System.Windows.Controls.TextBlock>(window,"AccountNameText").Text == "fresh", "auth recovery updates displayed account");

            field.SetValue(window, old); attempts = prompts = 0;
            window.ReauthenticateForLaunch = () => { prompts++; return Task.FromResult<AuthSession?>(null); };
            window._launchOverride = (_, _) => { attempts++; return Task.FromException<LaunchResult>(Expired()); };
            try { await window.LaunchWithLoginRecoveryAsync(vm, null, default); throw new Exception("login cancel did not stop launch"); }
            catch (OperationCanceledException) { }
            Check(attempts == 1 && prompts == 1 && Session() == old, "cancelling login prevents process retry and keeps session unchanged");

            field.SetValue(window, old); attempts = prompts = 0;
            window.ReauthenticateForLaunch = () => { prompts++; return Task.FromResult<AuthSession?>(fresh); };
            var network = new LaunchException("fixture network failure", new YggdrasilException("fixture unavailable"));
            window._launchOverride = (_, _) => { attempts++; return Task.FromException<LaunchResult>(network); };
            try { await window.LaunchWithLoginRecoveryAsync(vm, null, default); throw new Exception("network failure was ignored"); }
            catch (LaunchException ex) { Check(ReferenceEquals(ex, network) && attempts == 1 && prompts == 0, "network failure never opens login or retries launch"); }

            field.SetValue(window, old); attempts = prompts = 0;
            window._launchOverride = (_, _) => { attempts++; return Task.FromException<LaunchResult>(Expired()); };
            try { await window.LaunchWithLoginRecoveryAsync(vm, null, default); throw new Exception("persistent auth failure was ignored"); }
            catch (LaunchException) { Check(attempts == 2 && prompts == 1, "persistent auth failure is bounded to one login prompt and one retry"); }

            field.SetValue(window, old); attempts = prompts = 0;
            var login = new TaskCompletionSource<AuthSession?>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.ReauthenticateForLaunch = () => { prompts++; return login.Task; };
            window._launchOverride = (_, _) => { attempts++; return Session() == old ? Task.FromException<LaunchResult>(Expired()) : Task.FromResult(Result(Session())); };
            var first = window.LaunchWithLoginRecoveryAsync(vm, null, default);
            var second = window.LaunchWithLoginRecoveryAsync(vm, null, default);
            await Until(() => prompts == 1);
            Check(attempts == 2 && prompts == 1, "concurrent expired launches share a single login dialog");
            login.SetResult(fresh); var both = await Task.WhenAll(first, second);
            Check(attempts == 4 && both.All(x => x.Session == fresh), "concurrent pending launches both resume with reauthenticated session");

            field.SetValue(window, old); attempts = prompts = 0;
            var sharedLogin = new TaskCompletionSource<AuthSession?>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.ReauthenticateForLaunch = () => { prompts++; return sharedLogin.Task; };
            using (var cancelledWaiter = new CancellationTokenSource())
            {
                var cancelledLaunch = window.LaunchWithLoginRecoveryAsync(vm, null, cancelledWaiter.Token);
                var survivingLaunch = window.LaunchWithLoginRecoveryAsync(vm, null, default);
                await Until(() => prompts == 1);
                cancelledWaiter.Cancel();
                await Until(() => cancelledLaunch.IsCompleted);
                try { await cancelledLaunch; throw new Exception("cancelled waiter was not cancelled"); }
                catch (OperationCanceledException) { }
                Check(!sharedLogin.Task.IsCompleted && !survivingLaunch.IsCompleted && attempts == 2,
                    "one cancelled launch exits promptly without cancelling shared login or other launch");
                sharedLogin.SetResult(fresh);
                Check((await survivingLaunch).Session == fresh && attempts == 3 && prompts == 1,
                    "remaining launch continues after shared login with no duplicate game attempt");
            }

            field.SetValue(window, old); attempts = prompts = 0;
            var abandonedLogin = new TaskCompletionSource<AuthSession?>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.ReauthenticateForLaunch = () => { prompts++; return abandonedLogin.Task; };
            using (var abandoned = new CancellationTokenSource())
            {
                var abandonedLaunch = window.LaunchWithLoginRecoveryAsync(vm, null, abandoned.Token);
                await Until(() => prompts == 1); abandoned.Cancel();
                try { await abandonedLaunch; throw new Exception("abandoned launch was not cancelled"); }
                catch (OperationCanceledException) { }
                abandonedLogin.SetResult(null);
                await Until(() => typeof(MainWindow).GetField("_reauthentication", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) == null);
            }
            window.ReauthenticateForLaunch = () => { prompts++; return Task.FromResult<AuthSession?>(fresh); };
            Check((await window.LaunchWithLoginRecoveryAsync(vm, null, default)).Session == fresh && prompts == 2,
                "completed abandoned login does not poison the next recovery attempt");

            field.SetValue(window, old); attempts = prompts = 0;
            var slow = new TaskCompletionSource<LaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.ReauthenticateForLaunch = () => { prompts++; return Task.FromResult<AuthSession?>(fresh); };
            window._launchOverride = (_, _) => ++attempts == 1 ? slow.Task : Session() == old ? Task.FromException<LaunchResult>(Expired()) : Task.FromResult(Result(Session()));
            var slowLaunch = window.LaunchWithLoginRecoveryAsync(vm, null, default);
            await window.LaunchWithLoginRecoveryAsync(vm, null, default);
            slow.SetResult(Result(old)); await slowLaunch;
            Check(Session() == fresh, "late older game-window result cannot overwrite reauthenticated session");

            field.SetValue(window, old); attempts = prompts = 0;
            using var cancel = new CancellationTokenSource();
            window.ReauthenticateForLaunch = () => { prompts++; cancel.Cancel(); return Task.FromResult<AuthSession?>(fresh); };
            window._launchOverride = (_, _) => { attempts++; return Task.FromException<LaunchResult>(Expired()); };
            try { await window.LaunchWithLoginRecoveryAsync(vm, null, cancel.Token); throw new Exception("cancelled launch was retried"); }
            catch (OperationCanceledException) { Check(attempts == 1 && prompts == 1, "cancel during login never starts another game attempt"); }
            await StartupVerificationChecks(window, field, old, fresh);
            Console.WriteLine("PASS authentication recovery UI checks");
        }
        finally { window._launchOverride = null; window.ReauthenticateForLaunch = null; }
    }

    // The main window opens with the saved session; the skin site is asked afterwards.
    private static async Task StartupVerificationChecks(MainWindow window, FieldInfo field, AuthSession old, AuthSession fresh)
    {
        AuthSession Session() => (AuthSession)field.GetValue(window)!;
        var store = (AuthStore)typeof(MainWindow).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        string State() => Get<System.Windows.Controls.TextBlock>(window, "AccountStateText").Text;
        var overlay = Get<System.Windows.Controls.Grid>(window, "InlinePromptOverlay");
        int prompts = 0;
        window.ReauthenticateForLaunch = () => { prompts++; return Task.FromResult<AuthSession?>(fresh); };

        // Slow skin site: the window is already usable while the check runs.
        field.SetValue(window, old); store.SaveSession(old);
        var slow = new TaskCompletionSource<AuthSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var check = window.VerifyRestoredSessionAsync(_ => slow.Task);
        Check(!check.IsCompleted && State() == "正在确认…" && window.IsVisible && !overlay.IsVisible,
            "startup: saved login opens the main window without waiting for the skin site");
        var refreshed = new AuthSession { AccessToken = "refreshed", ClientToken = old.ClientToken, SelectedProfileId = old.SelectedProfileId, SelectedProfileName = old.SelectedProfileName };
        slow.SetResult(refreshed); await check;
        Check(Session() == refreshed && State() == "已登录" && prompts == 0, "startup: background check adopts a refreshed session silently");

        // Skin site confirmed down (the internet works): keep the session, show the screenshot.
        var notice = Get<System.Windows.Controls.Border>(window, "AccountNotice");
        var noticeText = Get<System.Windows.Controls.TextBlock>(window, "AccountNoticeText");
        System.Windows.Controls.Panel? Hover() => notice.ToolTip as System.Windows.Controls.Panel;
        bool HasPicture() => Hover()?.Children.OfType<System.Windows.Controls.Image>().Any() == true;
        var diagnosed = new List<Exception>();
        Task<AuthSession> Unreachable(AuthSession _) => Task.FromException<AuthSession>(new YggdrasilException("网络错误，无法连接皮肤站，请检查网络连接。") { Failure = YggdrasilFailure.Unreachable });
        try
        {
            CMUmcl.Network.ConnectionDiagnosis.Override = (ex, _) => { diagnosed.Add(ex); return Task.FromResult(CMUmcl.Network.ConnectionProblem.SkinSiteDown); };
            field.SetValue(window, old); store.SaveSession(old);
            await window.VerifyRestoredSessionAsync(Unreachable);
            window.UpdateLayout();
            Check(notice.IsVisible && noticeText.Text == "皮肤站现在已经坚不可摧" && noticeText.ActualHeight > 0
                && noticeText.ActualWidth <= ((System.Windows.FrameworkElement)noticeText.Parent).ActualWidth + 0.5 && State() == "已登录",
                "startup: the skin-site notice gets its own full-width row instead of a truncated state line");
            Check(diagnosed.Count == 1 && Session() == old && !overlay.IsVisible && prompts == 0 && store.LoadSession()?.AccessToken == old.AccessToken,
                "startup: a connection failure is diagnosed, keeps the login and never asks for credentials");
            Check(Hover()?.Children.OfType<System.Windows.Controls.Image>().SingleOrDefault()?.Source is System.Windows.Media.Imaging.BitmapSource { PixelWidth: 780 }
                && Hover()!.Children.OfType<System.Windows.Controls.TextBlock>().Any(t => t.Text == "无法连接皮肤站，请检查皮肤站状态。") && !Hover()!.Children.OfType<System.Windows.Controls.TextBlock>().Any(t => t.Text.Contains("请检查网络连接")),
                "startup: a confirmed skin-site outage shows the screenshot and why it is the skin site");

            // The user's own network is down: plain explanation, no screenshot.
            CMUmcl.Network.ConnectionDiagnosis.Override = (_, _) => Task.FromResult(CMUmcl.Network.ConnectionProblem.LocalNetwork);
            await window.VerifyRestoredSessionAsync(Unreachable);
            Check(noticeText.Text == "网络连接不可用" && !HasPicture() && Hover()!.Children.OfType<System.Windows.Controls.TextBlock>().Any(t => t.Text.Contains("检查网络连接或代理设置")),
                "startup: the user's own network problem is not blamed on the skin site");

            // Could not pin it on either side (the skin site answered the second time).
            CMUmcl.Network.ConnectionDiagnosis.Override = (_, _) => Task.FromResult(CMUmcl.Network.ConnectionProblem.Unknown);
            await window.VerifyRestoredSessionAsync(Unreachable);
            Check(noticeText.Text == "暂时无法连接皮肤站" && !HasPicture(), "startup: an unconfirmed failure gets a neutral notice without the screenshot");
        }
        finally { CMUmcl.Network.ConnectionDiagnosis.Override = null; }
        await window.VerifyRestoredSessionAsync(s => Task.FromResult(s));
        Check(State() == "已登录" && notice.ToolTip == null && !notice.IsVisible, "startup: a later successful check removes the notice");

        // Login no longer valid, user postpones: saved session is cleared, UI says so.
        field.SetValue(window, old); store.SaveSession(old);
        var postponed = window.VerifyRestoredSessionAsync(_ => Task.FromException<AuthSession>(new YggdrasilException("登录已过期", true)));
        await Until(() => overlay.IsVisible);
        Check(Get<System.Windows.Controls.TextBlock>(window, "InlinePromptTitle").Text == "登录已失效" && store.LoadSession() == null && State() == "登录已失效",
            "startup: an expired login is reported in a themed prompt and not kept for the next start");
        Click(window, "InlinePromptCancel"); await postponed;
        Check(prompts == 0 && Session() == old, "startup: postponing re-login leaves the window usable");

        // Login no longer valid, user re-logs in from the prompt.
        field.SetValue(window, old); store.SaveSession(old);
        var relogin = window.VerifyRestoredSessionAsync(_ => Task.FromException<AuthSession>(new YggdrasilException("登录已过期", true)));
        await Until(() => overlay.IsVisible);
        Click(window, "InlinePromptContinue"); await relogin;
        Check(prompts == 1 && Session() == fresh && State() == "已登录" && Get<System.Windows.Controls.TextBlock>(window, "AccountNameText").Text == fresh.SelectedProfileName,
            "startup: re-login from the prompt replaces the session");

        // A late answer cannot overwrite a session that changed meanwhile (e.g. a launch re-login).
        field.SetValue(window, old);
        var late = new TaskCompletionSource<AuthSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateCheck = window.VerifyRestoredSessionAsync(_ => late.Task);
        field.SetValue(window, fresh); late.SetException(new YggdrasilException("登录已过期", true)); await lateCheck;
        Check(Session() == fresh && !overlay.IsVisible, "startup: a stale check result is ignored after the session changed");
        store.ClearSession();
    }
}
