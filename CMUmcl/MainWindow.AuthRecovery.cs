using System;
using System.Threading;
using System.Threading.Tasks;
using CMUmcl.Auth;
using CMUmcl.Content;
using CMUmcl.Launch;
using CMUmcl.Network;

namespace CMUmcl
{
    public partial class MainWindow
    {
        internal Func<Task<AuthSession?>>? ReauthenticateForLaunch { get; set; }
        private Task<AuthSession?>? _reauthentication;

        // A failed refresh can require credentials, rather than another refresh.
        // Recover once and resume the original instance; network failures do not
        // open a login dialog. Concurrent launches share the same dialog.
        internal async Task<LaunchResult> LaunchWithLoginRecoveryAsync(InstanceCardViewModel vm,
            IProgress<LaunchProgress>? progress, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var attemptedSession = _session;
                try
                {
                    var result = await (_launchOverride != null ? _launchOverride(vm, ct)
                        : _launchService.LaunchLocalAsync(vm.InstanceId, attemptedSession, progress, ct));
                    // Another launch may have reauthenticated while this game was
                    // opening its window. Its newer session must not be replaced.
                    if (ReferenceEquals(_session, attemptedSession)) _session = result.Session;
                    // A launch validates the session, which settles any startup warning.
                    if (!_windowClosed && (AccountStateText.Text != "已登录" || AccountNotice.Visibility == System.Windows.Visibility.Visible)) SetAccountState("已登录");
                    return result;
                }
                catch (LaunchException ex) when (ex.RequiresLogin && attempt == 0 && ReauthenticateForLaunch != null)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new LaunchProgress { Stage = "重新登录", Message = "登录状态需要恢复，请重新登录；完成后将继续启动。" });
                    if (ReferenceEquals(_session, attemptedSession))
                    {
                        _reauthentication ??= PromptForLaunchLoginAsync();
                        var pending = _reauthentication;
                        var restored = await WaitForLaunchLoginAsync(pending, ct);
                        ct.ThrowIfCancellationRequested();
                        if (restored == null) throw new OperationCanceledException();
                    }
                }
            }
        }

        /// <summary>
        /// Startup check for a saved session, run after the window is shown. Only a definite
        /// "this login is no longer valid" asks for credentials; an unreachable skin site keeps
        /// the session, and the launch path validates it again before starting a game.
        /// </summary>
        internal async Task VerifyRestoredSessionAsync(Func<AuthSession, Task<AuthSession>> validate)
        {
            var original = _session;
            SetAccountState("正在确认…");
            try
            {
                var verified = await validate(original);
                if (_windowClosed || !ReferenceEquals(_session, original)) return;
                _session = verified;
                if (verified.SelectedProfileName != AccountNameText.Text) AccountNameText.Text = verified.SelectedProfileName;
                SetAccountState("已登录");
            }
            catch (YggdrasilException ex) when (ex.RequiresLogin)
            {
                if (_windowClosed || !ReferenceEquals(_session, original)) return;
                // The next start goes straight to the login window instead of repeating this.
                _store.TryClearSession(original);
                SetAccountState("登录已失效");
                var choice = await ShowInlinePromptAsync("登录已失效",
                    "保存的登录已过期或已在其他地方退出：" + ex.Message + "\n现在重新登录吗？也可以稍后再登录，启动游戏时会再次提示。", "重新登录");
                if (choice != InlineChoice.Continue || _windowClosed || !ReferenceEquals(_session, original) || ReauthenticateForLaunch == null) return;
                _reauthentication ??= PromptForLaunchLoginAsync();
                if (await _reauthentication != null) SetAccountState("已登录");
            }
            catch (Exception ex) when (ex is YggdrasilException || ex is System.Net.Http.HttpRequestException || ex is TaskCanceledException)
            {
                if (_windowClosed || !ReferenceEquals(_session, original)) return;
                // Only blame the skin site once the rest of the internet is known to work.
                var problem = await ConnectionDiagnosis.DiagnoseAsync(ex);
                if (_windowClosed || !ReferenceEquals(_session, original)) return;
                ShowConnectionProblem(problem, ex.Message);
            }
        }

        internal const string SkinSiteUnreachableText = "皮肤站现在已经坚不可摧";
        internal const string LocalNetworkText = "网络连接不可用";
        internal const string SkinSiteUncertainText = "暂时无法连接皮肤站";

        internal static string ConnectionProblemTitle(ConnectionProblem problem) => problem switch
        {
            ConnectionProblem.SkinSiteDown => SkinSiteUnreachableText,
            ConnectionProblem.LocalNetwork => LocalNetworkText,
            _ => SkinSiteUncertainText,
        };

        internal static string ConnectionProblemExplanation(ConnectionProblem problem) => problem switch
        {
            ConnectionProblem.SkinSiteDown => "无法连接皮肤站，请检查皮肤站状态。",
            ConnectionProblem.LocalNetwork => "目前无法访问互联网，请检查网络连接或代理设置。",
            _ => "皮肤站响应异常，请稍后重试。",
        };

        /// <summary>
        /// Notice under the account. The group's screenshot appears only when the skin site is
        /// confirmed down; the user's own network problems get a plain explanation.
        /// </summary>
        private void ShowConnectionProblem(ConnectionProblem problem, string reason)
        {
            if (_windowClosed) return;
            // Still signed in: the saved session is kept and checked again before launching.
            SetAccountState("已登录");
            var card = new System.Windows.Controls.StackPanel { Width = 300 };
            if (problem == ConnectionProblem.SkinSiteDown && SkinSitePicture is { } picture)
                card.Children.Add(new System.Windows.Controls.Image { Source = picture, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new System.Windows.Thickness(0, 2, 0, 10) });
            card.Children.Add(new System.Windows.Controls.TextBlock { Text = ConnectionProblemExplanation(problem), FontWeight = System.Windows.FontWeights.SemiBold, TextWrapping = System.Windows.TextWrapping.Wrap });
            // A confirmed outage needs no technical detail; the other cases keep the raw reason.
            string detail = problem == ConnectionProblem.SkinSiteDown ? "" : reason + "\n";
            var hint = new System.Windows.Controls.TextBlock { Text = detail + "登录仍然保留，启动游戏前会再次确认。", Margin = new System.Windows.Thickness(0, 4, 0, 0), TextWrapping = System.Windows.TextWrapping.Wrap };
            hint.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "MutedTextBrush");
            card.Children.Add(hint);
            AccountNoticeText.Text = ConnectionProblemTitle(problem);
            AccountNotice.ToolTip = card;
            AccountNotice.Visibility = System.Windows.Visibility.Visible;
        }

        private static System.Windows.Media.ImageSource? _skinSitePicture;
        internal static System.Windows.Media.ImageSource? SkinSitePicture
        {
            get
            {
                if (_skinSitePicture != null) return _skinSitePicture;
                try
                {
                    var image = new System.Windows.Media.Imaging.BitmapImage();
                    image.BeginInit();
                    image.UriSource = new Uri("pack://application:,,,/CMUmcl;component/Assets/Easter/skin-site-unbreakable.jpg");
                    image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    image.EndInit(); image.Freeze();
                    return _skinSitePicture = image;
                }
                catch { return null; }
            }
        }

        private void SetAccountState(string text)
        {
            if (_windowClosed) return;
            AccountStateText.Text = text;
            AccountStateText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                text is "登录已失效" ? "ErrorBrush" : "MutedTextBrush");
            AccountNotice.Visibility = System.Windows.Visibility.Collapsed;
            AccountNotice.ToolTip = null;
        }

        private static async Task<AuthSession?> WaitForLaunchLoginAsync(Task<AuthSession?> pending, CancellationToken ct)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancelled.TrySetResult(true)))
            {
                await Task.WhenAny(pending, cancelled.Task);
                ct.ThrowIfCancellationRequested();
                return await pending;
            }
        }

        private async Task<AuthSession?> PromptForLaunchLoginAsync()
        {
            // Yield before opening a modal so the shared task is assigned first.
            await Task.Yield();
            try
            {
                if (_windowClosed) return null;
                var restored = await ReauthenticateForLaunch!();
                if (restored != null && !_windowClosed)
                {
                    _session = restored;
                    AccountNameText.Text = restored.SelectedProfileName;
                    SetAccountState("已登录");
                    _ = LoadAccountAvatarAsync(true);
                }
                return restored;
            }
            finally { _reauthentication = null; }
        }
    }
}
