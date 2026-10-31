using System;
using System.Windows;
using System.Threading;
using CMUmcl.Update;
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
        private SettingsStore? _settingsStore;
        private LaunchService? _launchService;
        private LocalInstanceService? _localInstanceService;
        private Mutex? _installGate;
        private bool _ownsInstallGate;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Use a separate, explicit data root for portable runs and isolated acceptance tests.
            if (e.Args.Length != 0)
            {
                if (e.Args.Length != 2 || e.Args[0] != "--data-directory" || !System.IO.Path.IsPathFullyQualified(e.Args[1]))
                {
                    MessageBox.Show("数据目录参数无效。用法：--data-directory <绝对路径>", "CMUmcl");
                    Shutdown();
                    return;
                }
                LauncherPaths.RootOverride = e.Args[1];
            }

            _installGate = new Mutex(false, UpdateProtocol.InstallLockName(AppContext.BaseDirectory));
            try { _ownsInstallGate = _installGate.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsInstallGate = true; }
            if (!_ownsInstallGate)
            {
                MessageBox.Show("此目录的客户端已在运行或正在更新，请稍后再打开。", "CMUmcl");
                Shutdown();
                return;
            }

            // Load settings and ensure the on-disk layout exists before any service runs.
            _settingsStore = new SettingsStore();
            _settingsStore.Load();
            LauncherPaths.EnsureDirectories();

            _client = new YggdrasilClient();
            _store = new AuthStore();
            _manifestClient = new ResourceManifestClient();
            _installedState = new InstalledStateStore();
            _installer = new PackInstaller(_installedState);
            _launchService = new LaunchService(_client, _store, _installedState, _settingsStore);
            _localInstanceService = new LocalInstanceService(_installedState);

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
            var main = new MainWindow(session, _store!, _manifestClient!, _installer!, _installedState!, _launchService!, _settingsStore!, _localInstanceService!);
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
            if (_ownsInstallGate) _installGate?.ReleaseMutex();
            _installGate?.Dispose();
            base.OnExit(e);
        }
    }
}
