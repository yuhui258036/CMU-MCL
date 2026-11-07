using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CMUmcl.Auth
{
    // One gate per launcher: rotating a token and handing it to a new process must
    // not race another launch using the same (possibly stale) UI session.
    internal sealed class AuthSessionGate
    {
        private readonly YggdrasilClient _client;
        private readonly AuthStore _store;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, AuthSession> _replacements = new Dictionary<string, AuthSession>();
        private readonly HashSet<string> _rejectedPasswords = new HashSet<string>();

        internal AuthSessionGate(YggdrasilClient client, AuthStore store)
        { _client = client; _store = store; }

        internal async Task<T> RunAsync<T>(AuthSession session, Func<AuthSession, Task<T>> start, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var requested = session;
                while (_replacements.TryGetValue(session.AccessToken, out var replacement))
                    session = replacement;
                bool automaticallyRecovered = false;
                for (;;)
                {
                    try
                    {
                        RequireComplete(session);
                        if (!await _client.ValidateAsync(session.AccessToken, session.ClientToken, ct).ConfigureAwait(false))
                        {
                            var refreshed = await _client.RefreshAsync(session.AccessToken, session.ClientToken, ct).ConfigureAwait(false);
                            var updated = new AuthSession
                            {
                                AccessToken = refreshed.AccessToken,
                                ClientToken = string.IsNullOrWhiteSpace(refreshed.ClientToken) ? session.ClientToken : refreshed.ClientToken,
                                SelectedProfileId = refreshed.SelectedProfile?.Id ?? string.Empty,
                                SelectedProfileName = refreshed.SelectedProfile?.Name ?? string.Empty,
                                Username = refreshed.User?.Username ?? session.Username,
                                SavedAt = DateTimeOffset.UtcNow,
                            };
                            RequireComplete(updated);
                            if (!string.Equals(updated.SelectedProfileId, session.SelectedProfileId, StringComparison.OrdinalIgnoreCase))
                                throw new YggdrasilException("刷新后的角色与当前角色不一致，请重新登录。", requiresLogin: true);
                            // Retain the response in memory if verification is cancelled or times
                            // out. Every later use still validates it before persistence/handoff.
                            if (!string.Equals(session.AccessToken, updated.AccessToken, StringComparison.Ordinal))
                                _replacements[session.AccessToken] = updated;
                            // HTTP 200 from refresh is not proof that the replacement is usable.
                            if (!await _client.ValidateAsync(updated.AccessToken, updated.ClientToken, ct).ConfigureAwait(false))
                                throw new YggdrasilException("刷新后的登录状态仍然无效，请重新登录。", requiresLogin: true);
                            _store.TryReplaceSession(session, updated);
                            session = updated;
                        }
                        // A prior attempt may have returned a new token before its validation
                        // was cancelled; persist it only after this attempt verified it.
                        if (!ReferenceEquals(requested, session))
                            _store.TryReplaceSession(requested, session);
                        ct.ThrowIfCancellationRequested();
                        return await start(session).ConfigureAwait(false);
                    }
                    catch (YggdrasilException ex) when (ex.RequiresLogin && !automaticallyRecovered)
                    {
                        var restored = await RestoreRememberedLoginAsync(requested, ct).ConfigureAwait(false);
                        if (restored == null) throw;
                        automaticallyRecovered = true;
                        if (!string.IsNullOrEmpty(session.AccessToken) && session.AccessToken != restored.AccessToken)
                            _replacements[session.AccessToken] = restored;
                        if (!string.IsNullOrEmpty(requested.AccessToken) && requested.AccessToken != restored.AccessToken)
                            _replacements[requested.AccessToken] = restored;
                        session = restored;
                    }
                }
            }
            finally { _gate.Release(); }
        }

        private async Task<AuthSession?> RestoreRememberedLoginAsync(AuthSession expected, CancellationToken ct)
        {
            var remembered = _store.LoadRememberedLogin(expected);
            if (remembered == null || _rejectedPasswords.Contains(remembered.Generation)) return null;
            try
            {
                ct.ThrowIfCancellationRequested();
                var result = await _client.AuthenticateAsync(remembered.Username, remembered.Password,
                    remembered.ClientToken, ct).ConfigureAwait(false);
                var profile = result.SelectedProfile ?? result.AvailableProfiles.FirstOrDefault(p =>
                    string.Equals(p.Id, remembered.ProfileId, StringComparison.OrdinalIgnoreCase));
                if (profile == null || !string.Equals(profile.Id, remembered.ProfileId, StringComparison.OrdinalIgnoreCase))
                    throw new YggdrasilException("自动登录无法恢复原角色，请重新登录。", requiresLogin: true);
                var restored = await CompleteLoginAsync(_client, result, profile, remembered.ClientToken,
                    remembered.Username, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!_store.TrySaveAutomaticLogin(remembered, restored))
                    throw new OperationCanceledException("登录信息已更改，本次自动恢复已取消。");
                return restored;
            }
            catch (YggdrasilException ex) when (ex.RequiresLogin)
            {
                _rejectedPasswords.Add(remembered.Generation);
                _store.ForgetRejectedPassword(remembered);
                throw;
            }
        }

        internal static async Task<AuthSession> CompleteLoginAsync(YggdrasilClient client, AuthenticateResult result,
            YggdrasilProfile profile, string clientToken, string username, CancellationToken ct = default)
        {
            string accessToken = result.AccessToken;
            string effectiveClientToken = string.IsNullOrWhiteSpace(result.ClientToken) ? clientToken : result.ClientToken;
            // A local role selection must also bind the server-side token.
            if (result.SelectedProfile == null)
            {
                var bound = await client.RefreshAsync(accessToken, effectiveClientToken, ct, selectedProfile: profile).ConfigureAwait(false);
                if (bound.SelectedProfile == null || !string.Equals(bound.SelectedProfile.Id, profile.Id, StringComparison.OrdinalIgnoreCase))
                    throw new YggdrasilException("皮肤站未确认所选角色，请重新登录。", requiresLogin: true);
                accessToken = bound.AccessToken;
                effectiveClientToken = string.IsNullOrWhiteSpace(bound.ClientToken) ? effectiveClientToken : bound.ClientToken;
                profile = bound.SelectedProfile;
            }
            var session = new AuthSession
            {
                AccessToken = accessToken,
                ClientToken = effectiveClientToken,
                SelectedProfileId = profile.Id,
                SelectedProfileName = profile.Name,
                Username = result.User?.Username ?? username,
                SavedAt = DateTimeOffset.UtcNow,
            };
            RequireComplete(session);
            if (!await client.ValidateAsync(session.AccessToken, session.ClientToken, ct).ConfigureAwait(false))
                throw new YggdrasilException("登录后的会话校验失败，请重新登录；若反复出现，请联系皮肤站管理员。", requiresLogin: true);
            return session;
        }

        internal static void RequireComplete(AuthSession session)
        {
            if (string.IsNullOrWhiteSpace(session.AccessToken) || string.IsNullOrWhiteSpace(session.ClientToken)
                || string.IsNullOrWhiteSpace(session.SelectedProfileId) || string.IsNullOrWhiteSpace(session.SelectedProfileName))
                throw new YggdrasilException("登录信息或角色信息不完整，请重新登录。", requiresLogin: true);
        }

        internal async Task<T> StartAsync<T>(AuthSession session, Func<T> start, CancellationToken ct)
        {
            if (!await _client.ValidateAsync(session.AccessToken, session.ClientToken, ct).ConfigureAwait(false))
                throw new YggdrasilException("启动前登录状态已失效，请重试；若仍失败，请重新登录。", requiresLogin: true);
            ct.ThrowIfCancellationRequested();
            return start();
        }
    }
}
