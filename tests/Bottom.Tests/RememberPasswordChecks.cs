using System.Net;
using System.Text;
using CMUmcl.Auth;
using CMUmcl.Content;

internal static partial class Program
{
    private static object RememberedAuthentication(string token = "authenticated", string? profileId = null)
        => new { accessToken = token, clientToken = "client", selectedProfile = new { id = profileId ?? new string('a',32), name = "fixture" } };

    private static async Task RememberPasswordChecks()
    {
        const string username = "fixture-login", password = "fixture-password-not-a-real-secret";
        var store = new AuthStore(); var old = AuthFixture();
        void Remember() { store.ClearSession(); if (!store.SaveLogin(old, username, password, true)) throw new Exception("fixture encrypted login could not be saved"); }
        int authentications = 0;
        try
        {
            Remember();
            var bytes = File.ReadAllBytes(Path.Combine(LauncherPaths.RootDirectory,"auth.dat"));
            var text = Encoding.UTF8.GetString(bytes);
            Check(!text.Contains(password) && !text.Contains(username), "auth file contains neither plaintext password nor plaintext login identifier");
            var loaded = new AuthStore().LoadRememberedLogin(old);
            Check(loaded?.Username == username && loaded.Password == password, "same Windows user restores explicitly remembered credentials");
            string stableClient = store.GetOrCreateClientToken(); store.ClearSession();
            Check(!store.HasRememberedPassword && store.LoadSession() == null && store.GetOrCreateClientToken() == stableClient,
                "logout forgets password and session while preserving stable client token");
            Check(!store.TrySaveAutomaticLogin(loaded!, AuthFixture("new")), "late automatic login cannot restore a logged-out account");

            Remember(); var newer = AuthFixture("newer-login"); store.SaveLogin(newer,username,password,true);
            Check(!store.TryClearSession(old) && store.HasRememberedPassword, "late startup rejection cannot clear credentials of a newer login");
            Check(store.TryClearSession(newer) && !store.HasRememberedPassword, "startup conditional session removal clears the matching remembered password too");

            Remember(); store.SaveLogin(old, username, password, false);
            Check(!store.HasRememberedPassword, "unchecking remember-password removes previously saved password");
            var other = AuthFixture("other"); other.SelectedProfileId = new string('b',32);
            Remember(); store.SaveSession(other);
            Check(!store.HasRememberedPassword && store.LoadRememberedLogin(old) == null, "account switch cannot reuse another profile's password");

            foreach (bool optIn in new[] { false, true })
            {
                store.ClearSession(); store.SaveLogin(old, username, password, optIn); authentications = 0;
                using var client = new YggdrasilClient(new AuthHandler((path, body, ct) =>
                {
                    if (path == "authenticate")
                    {
                        authentications++;
                        Check(body.GetProperty("username").GetString() == username && body.GetProperty("password").GetString() == password,
                            "automatic recovery sends opted-in credentials only to authenticate");
                        return Task.FromResult(AuthResponse(HttpStatusCode.OK, RememberedAuthentication()));
                    }
                    return Task.FromResult(AuthResponse(path == "validate" && body.GetProperty("accessToken").GetString() == "authenticated"
                        ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden));
                }));
                var gate = new AuthSessionGate(client, store);
                if (optIn)
                {
                    var results = await Task.WhenAll(gate.RunAsync(old, Task.FromResult, default), gate.RunAsync(old, Task.FromResult, default));
                    Check(authentications == 1 && results.All(s => s.AccessToken == "authenticated"), "concurrent expired launches recover once without any manual login");
                    Check(store.LoadSession()?.AccessToken == "authenticated" && store.HasRememberedPassword, "automatic login persists verified session and retains opt-in");
                }
                else
                {
                    await AuthRejects(() => gate.RunAsync(old, Task.FromResult, default), "session-only mode still requests manual login when refresh is rejected");
                    Check(authentications == 0, "session-only mode never attempts password authentication");
                }
            }

            foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.ServiceUnavailable })
            {
                Remember(); authentications = 0;
                using var client = new YggdrasilClient(new AuthHandler((path, body, ct) =>
                {
                    if (path == "authenticate") authentications++;
                    return Task.FromResult(AuthResponse(path == "authenticate" ? status : HttpStatusCode.Forbidden));
                }));
                var gate = new AuthSessionGate(client, store);
                await AuthRejects(() => gate.RunAsync(old, Task.FromResult, default), "rejected or unavailable automatic authentication never starts game");
                Check(authentications == 1 && store.HasRememberedPassword == (status != HttpStatusCode.Forbidden),
                    "password rejection forgets unusable password while temporary outage preserves it");
                if (status == HttpStatusCode.Forbidden)
                {
                    await AuthRejects(() => gate.RunAsync(old, Task.FromResult, default), "rejected saved password falls back to manual login");
                    Check(authentications == 1, "bad saved password is not retried repeatedly");
                }
            }

            Remember(); authentications = 0;
            using (var client = new YggdrasilClient(new AuthHandler((path, body, ct) =>
            {
                if (path == "refresh") return Task.FromResult(AuthResponse(HttpStatusCode.OK, RefreshFixture("refresh-invalid")));
                if (path == "authenticate") { authentications++; return Task.FromResult(AuthResponse(HttpStatusCode.OK,RememberedAuthentication())); }
                return Task.FromResult(AuthResponse(body.GetProperty("accessToken").GetString() == "authenticated" ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden));
            })))
            {
                var recovered = await new AuthSessionGate(client,store).RunAsync(old,Task.FromResult,default);
                Check(recovered.AccessToken == "authenticated" && authentications == 1 && store.LoadSession()?.AccessToken == "authenticated",
                    "refresh 200 followed by invalid token automatically falls back to verified password login");
            }

            Remember(); bool boundOriginalRole = false;
            using (var client = new YggdrasilClient(new AuthHandler((path, body, ct) =>
            {
                if (path == "authenticate") return Task.FromResult(AuthResponse(HttpStatusCode.OK,
                    new { accessToken = "unbound", clientToken = "client", availableProfiles = new[] { new {id = old.SelectedProfileId,name = old.SelectedProfileName} } }));
                if (path == "refresh" && body.TryGetProperty("selectedProfile",out var profile))
                {
                    boundOriginalRole = profile.GetProperty("id").GetString() == old.SelectedProfileId;
                    return Task.FromResult(AuthResponse(HttpStatusCode.OK,RefreshFixture("bound")));
                }
                return Task.FromResult(AuthResponse(path == "validate" && body.GetProperty("accessToken").GetString() == "bound" ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden));
            })))
            {
                var recovered = await new AuthSessionGate(client,store).RunAsync(old,Task.FromResult,default);
                Check(boundOriginalRole && recovered.AccessToken == "bound", "automatic authentication restores and binds original role instead of prompting for another role");
            }

            Remember(); authentications = 0;
            using (var client = new YggdrasilClient(new AuthHandler((path, body, ct) =>
            {
                if (path == "authenticate") authentications++;
                return Task.FromResult(AuthResponse(HttpStatusCode.ServiceUnavailable));
            })))
            {
                await AuthRejects(() => new AuthSessionGate(client,store).RunAsync(old, Task.FromResult, default), "validation outage stops automatic recovery");
                Check(authentications == 0 && store.HasRememberedPassword, "validation outage neither sends password nor erases remembered login");
            }

            Remember();
            using (var cancel = new CancellationTokenSource())
            using (var client = new YggdrasilClient(new AuthHandler((path, body, ct) =>
            {
                if (path == "authenticate") { cancel.Cancel(); return Task.FromCanceled<System.Net.Http.HttpResponseMessage>(ct); }
                return Task.FromResult(AuthResponse(HttpStatusCode.Forbidden));
            })))
            {
                try { await new AuthSessionGate(client,store).RunAsync(old,Task.FromResult,cancel.Token); throw new Exception("automatic login was not cancelled"); }
                catch (OperationCanceledException) { Check(store.HasRememberedPassword && store.LoadSession()?.AccessToken == "old", "cancelled authentication preserves credentials and never saves unverified session"); }
            }

            foreach (bool logout in new[] { true, false })
            {
                Remember();
                using var client = new YggdrasilClient(new AuthHandler((path, body, ct) =>
                {
                    if (path == "authenticate")
                    {
                        if (logout) store.ClearSession(); else store.SaveLogin(AuthFixture("manual-new"),username,"fixture-new-password",true);
                        return Task.FromResult(AuthResponse(HttpStatusCode.OK,RememberedAuthentication()));
                    }
                    return Task.FromResult(AuthResponse(path == "validate" && body.GetProperty("accessToken").GetString() == "authenticated"
                        ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden));
                }));
                try { await new AuthSessionGate(client,store).RunAsync(old,Task.FromResult,default); throw new Exception("stale automatic login was not cancelled"); }
                catch (OperationCanceledException)
                {
                    Check(logout ? store.LoadSession() == null : store.LoadSession()?.AccessToken == "manual-new",
                        "in-flight automatic login cannot overwrite logout or a newer manual login");
                }
            }

            Remember();
            using (var client = new YggdrasilClient(new AuthHandler((path, body, ct) => Task.FromResult(path == "authenticate"
                ? AuthResponse(HttpStatusCode.OK,RememberedAuthentication(profileId:new string('b',32))) : AuthResponse(HttpStatusCode.Forbidden)))))
            {
                await AuthRejects(() => new AuthSessionGate(client,store).RunAsync(old,Task.FromResult,default), "automatic login never switches to a different server-selected role");
                Check(!store.HasRememberedPassword && store.LoadSession()?.AccessToken == "old", "role mismatch requires manual login and preserves original session identity");
            }

            Remember(); int oldChecks = 0, prepared = 0, started = 0; authentications = 0;
            using (var client = new YggdrasilClient(new AuthHandler((path, body, ct) =>
            {
                if (path == "authenticate") { authentications++; return Task.FromResult(AuthResponse(HttpStatusCode.OK,RememberedAuthentication())); }
                bool accepted = body.GetProperty("accessToken").GetString() == "authenticated" || ++oldChecks == 1;
                return Task.FromResult(AuthResponse(accepted ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden));
            })))
            {
                var gate = new AuthSessionGate(client,store);
                await gate.RunAsync(old, current => { prepared++; return gate.StartAsync(current, () => ++started, default); },default);
                Check(prepared == 2 && started == 1 && authentications == 1,
                    "token revoked during process construction is automatically reauthenticated and rebuilt before starting once");
            }

            Remember(); authentications = 0; int callbacks = 0;
            using (var client = new YggdrasilClient(new AuthHandler((path, body, ct) =>
            {
                if (path == "authenticate") { authentications++; return Task.FromResult(AuthResponse(HttpStatusCode.OK,RememberedAuthentication())); }
                return Task.FromResult(AuthResponse(HttpStatusCode.NoContent));
            })))
            {
                await AuthRejects(() => new AuthSessionGate(client,store).RunAsync<bool>(old, _ => { callbacks++; throw new YggdrasilException("fixture revoked",true); },default),
                    "persistent pre-start rejection remains bounded after automatic login");
                Check(authentications == 1 && callbacks == 2, "each launch performs at most one automatic password authentication");
            }

            Remember();
            string authPath = Path.Combine(LauncherPaths.RootDirectory,"auth.dat");
            var originalAttributes = File.GetAttributes(authPath);
            var protectedBefore = File.ReadAllBytes(authPath);
            try
            {
                File.SetAttributes(authPath,originalAttributes | FileAttributes.ReadOnly);
                Check(!store.SaveLogin(old,username,password,false) && File.ReadAllBytes(authPath).SequenceEqual(protectedBefore),
                    "failed credential opt-out reports persistence failure and preserves intact encrypted store");
                Check(!store.TryClearSession(), "logout reports inability to erase saved credentials instead of claiming success");
                Check(!store.TryClearSession(old), "conditional startup clearing also reports persistence failure");
                authentications = 0;
                using var client = new YggdrasilClient(new AuthHandler((path,body,ct) =>
                {
                    if (path == "authenticate") authentications++;
                    return Task.FromResult(AuthResponse(HttpStatusCode.Forbidden));
                }));
                var gate = new AuthSessionGate(client,store);
                await AuthRejects(() => gate.RunAsync(old,Task.FromResult,default), "rejected password requires manual login despite read-only store");
                await AuthRejects(() => gate.RunAsync(old,Task.FromResult,default), "read-only store does not cause repeated password attempts");
                Check(authentications == 1, "in-memory rejection prevents reusing bad password when disk removal fails");
            }
            finally { File.SetAttributes(authPath,originalAttributes); }
        }
        finally { store.ClearSession(); }
    }
}
