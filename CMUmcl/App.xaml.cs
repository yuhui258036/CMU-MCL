using System;
using System.Windows;
using CMUmcl.Auth;
using CMUmcl.Content;
using CMUmcl.Launch;

namespace CMUmcl
{
    public partial class App : Application
    {
        private YggdrasilClient? _client;
        private AuthStore? _store;
        private ResourceManifestClient? _manifestClient;
        private PackInstaller? _installer;
        private InstalledStateStore? _installedState;
        private LaunchService? _launchService;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _client = new YggdrasilClient();
            _store = new AuthStore();
            _manifestClient = new ResourceManifestClient();
            _installedState = new InstalledStateStore();
            _installer = new PackInstaller(_installedState);
            _launchService = new LaunchService(_client, _store, _installedState);

            AuthSession? session = TryRestoreSession();
            if (session == null)
            {
                session = ShowLogin();
                if (session == null)
                {
                    Shutdown();
                    return;
                }
            }

            ShowMainWindow(session);
        }

        /// <summary>
        /// Attempts to restore a saved session: validate first, then refresh on 403.
        /// Returns null if no valid session could be restored.
        /// </summary>
        private AuthSession? TryRestoreSession()
        {
            var saved = _store!.LoadSession();
            if (saved == null)
                return null;

            // Ensure the clientToken is stable even if the file predates it.
            if (string.IsNullOrWhiteSpace(saved.ClientToken))
            {
                saved.ClientToken = _store.GetOrCreateClientToken();
            }

            try
            {
                bool valid = _client!.ValidateAsync(saved.AccessToken, saved.ClientToken)
                    .GetAwaiter().GetResult();
                if (valid)
                {
                    saved.SavedAt = DateTimeOffset.UtcNow;
                    return saved;
                }
            }
            catch (YggdrasilException)
            {
                // Fall through to refresh attempt.
            }

            // Token invalid (403) or unavailable — try refreshing.
            try
            {
                RefreshResult refreshed = _client!.RefreshAsync(saved.AccessToken, saved.ClientToken)
                    .GetAwaiter().GetResult();

                var updated = new AuthSession
                {
                    ClientToken = string.IsNullOrWhiteSpace(refreshed.ClientToken)
                        ? saved.ClientToken
                        : refreshed.ClientToken,
                    AccessToken = refreshed.AccessToken,
                    SelectedProfileId = refreshed.SelectedProfile?.Id ?? saved.SelectedProfileId,
                    SelectedProfileName = refreshed.SelectedProfile?.Name ?? saved.SelectedProfileName,
                    Username = refreshed.User?.Username ?? saved.Username,
                    SavedAt = DateTimeOffset.UtcNow,
                };

                _store.SaveSession(updated);
                return updated;
            }
            catch (YggdrasilException)
            {
                // Refresh failed — the saved session is unusable.
            }

            _store.ClearSession();
            return null;
        }

        /// <summary>Shows the login window and returns the resulting session, or null if cancelled.</summary>
        private AuthSession? ShowLogin()
        {
            var login = new LoginWindow(_client!, _store!);
            bool? ok = login.ShowDialog();
            if (ok == true && login.ResultSession != null)
            {
                return login.ResultSession;
            }
            return null;
        }

        /// <summary>Shows the main window; on logout, returns to the login gate.</summary>
        private void ShowMainWindow(AuthSession session)
        {
            var main = new MainWindow(session, _store!, _manifestClient!, _installer!, _installedState!, _launchService!);
            bool loggingOut = false;

            main.LogoutRequested += () => loggingOut = true;

            main.Closed += (_, _) =>
            {
                if (loggingOut)
                {
                    var next = ShowLogin();
                    if (next == null)
                    {
                        Shutdown();
                        return;
                    }
                    ShowMainWindow(next);
                }
                else
                {
                    Shutdown();
                }
            };

            MainWindow = main;
            main.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _client?.Dispose();
            _manifestClient?.Dispose();
            _installer?.Dispose();
            _launchService?.Dispose();
            base.OnExit(e);
        }
    }
}
