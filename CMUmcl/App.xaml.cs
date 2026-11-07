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
            AppDomain.CurrentDomain.UnhandledException += (_, ev) => { if(ev.ExceptionObject is Exception ex) System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "sandbox-error.log"), ex.GetType().FullName + "\n" + ex.StackTrace); };
            base.OnStartup(e);
            CMUmcl.Appearance.DpiWindowBehavior.Initialize();
            LauncherPaths.RootOverride = System.IO.Path.Combine(AppContext.BaseDirectory, "sandbox-data");

            // Use a separate, explicit data root for portable runs and isolated acceptance tests.
            if (e.Args.Length != 0)
            {
                if (e.Args.Length != 2 || e.Args[0] != "--data-directory" || !Compat.IsPathFullyQualified(e.Args[1]))
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
            CMUmcl.Network.LauncherProxy.Initialize();
            LauncherPaths.EnsureDirectories();
            CMUmcl.Appearance.AppearanceManager.Initialize(SettingsStore.Current);

            _client = new YggdrasilClient();
            _store = new AuthStore();
            _manifestClient = new ResourceManifestClient();
            _installedState = new InstalledStateStore();
            _installer = new PackInstaller(_installedState);
            _launchService = new LaunchService(_client, _store, _installedState, _settingsStore);
            _localInstanceService = new LocalInstanceService(_installedState);

            // A saved session opens the main window at once; the skin site is asked in the
            // background, so a slow or unreachable server never holds up startup. Launching a
            // game validates the session again regardless.
            AuthSession? session = LoadSavedSession();
            bool verified = session == null;
            if (session == null)
            {
                session = ShowLogin();
                if (session == null)
                {
                    Shutdown();
                    return;
                }
            }

            ShowMainWindow(session, verified);
        }

        /// <summary>Reads the saved session without contacting the skin site.</summary>
        private AuthSession? LoadSavedSession()
        {
            var saved = _store!.LoadSession();
            if (saved == null)
                return null;

            // Ensure the clientToken is stable even if the file predates it.
            if (string.IsNullOrWhiteSpace(saved.ClientToken))
            {
                saved.ClientToken = _store.GetOrCreateClientToken();
            }

            try { AuthSessionGate.RequireComplete(saved); }
            catch (YggdrasilException)
            {
                _store.ClearSession();
                return null;
            }
            return saved;
        }

        /// <summary>Shows the login window and returns the resulting session, or null if cancelled.</summary>
        private AuthSession? ShowLogin(Window? owner = null)
        {
            var login = new LoginWindow(_client!, _store!) { Title = "CMUmcl \u2014 NET48 " + CMUmcl.Update.LauncherUpdateClient.CurrentVersion.ToString() + " \u767B\u5F55" };
            if (owner != null) login.Owner = owner;
            bool? ok = login.ShowDialog();
            if (ok == true && login.ResultSession != null)
            {
                return login.ResultSession;
            }
            return null;
        }

        /// <summary>Shows the main window; on logout, returns to the login gate.</summary>
        private void ShowMainWindow(AuthSession session, bool verified = true)
        {
            var main = new MainWindow(session, _store!, _manifestClient!, _installer!, _installedState!, _launchService!, _settingsStore!, _localInstanceService!);
            main.ReauthenticateForLaunch = () => System.Threading.Tasks.Task.FromResult(ShowLogin(main));
            bool loggingOut = false;

            main.Title = "CMUmcl \u2014 NET48 " + CMUmcl.Update.LauncherUpdateClient.CurrentVersion.ToString();
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
            if (!verified) _ = main.VerifyRestoredSessionAsync(saved => _launchService!.EnsureValidSessionAsync(saved));
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _client?.Dispose();
            _manifestClient?.Dispose();
            _installer?.Dispose();
            _launchService?.Dispose();
            if (_ownsInstallGate) _installGate?.ReleaseMutex();
            _installGate?.Dispose();
            CMUmcl.Network.LauncherProxy.Shutdown();
            base.OnExit(e);
        }
    }
}
