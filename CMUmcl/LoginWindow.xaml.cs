using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CMUmcl.Auth;

namespace CMUmcl
{
    public partial class LoginWindow : Window
    {
        private readonly YggdrasilClient _client;
        private readonly AuthStore _store;

        /// <summary>The session produced by a successful login, or null.</summary>
        public AuthSession? ResultSession { get; private set; }

        /// <summary>True when the user should persist the session across runs.</summary>
        public bool RememberLogin => RememberBox.IsChecked == true;

        public LoginWindow(YggdrasilClient client, AuthStore store)
        {
            InitializeComponent();
            _client = client;
            _store = store;
        }

        private void UsernameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                PasswordBox.Focus();
            }
        }

        private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _ = LoginAsync();
            }
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            await LoginAsync();
        }

        private async Task LoginAsync()
        {
            if (_busy) return;

            string username = UsernameBox.Text.Trim();
            string password = PasswordBox.Password;

            if (string.IsNullOrEmpty(username))
            {
                SetError("请输入用户名 / 邮箱。");
                UsernameBox.Focus();
                return;
            }
            if (string.IsNullOrEmpty(password))
            {
                SetError("请输入密码。");
                PasswordBox.Focus();
                return;
            }

            SetBusy(true);
            SetError(string.Empty);

            try
            {
                string clientToken = _store.GetOrCreateClientToken();

                AuthenticateResult result = await _client.AuthenticateAsync(username, password, clientToken);

                // Resolve the profile to use.
                YggdrasilProfile? profile = result.SelectedProfile;

                if (profile == null && result.AvailableProfiles.Count == 1)
                {
                    profile = result.AvailableProfiles[0];
                }
                else if (profile == null && result.AvailableProfiles.Count > 1)
                {
                    var dialog = new ProfileSelectWindow(result.AvailableProfiles)
                    {
                        Owner = this,
                    };
                    bool? chosen = dialog.ShowDialog();
                    if (chosen == true && dialog.SelectedProfile != null)
                        profile = dialog.SelectedProfile;
                    else
                    {
                        SetError("未选择角色，请重新登录。");
                        return;
                    }
                }

                if (profile == null)
                {
                    SetError("该账号没有可用的角色，请先在皮肤站创建角色。");
                    return;
                }

                var session = new AuthSession
                {
                    ClientToken = string.IsNullOrWhiteSpace(result.ClientToken) ? clientToken : result.ClientToken,
                    AccessToken = result.AccessToken,
                    SelectedProfileId = profile.Id,
                    SelectedProfileName = profile.Name,
                    Username = result.User?.Username ?? username,
                    SavedAt = DateTimeOffset.UtcNow,
                };

                if (RememberLogin)
                {
                    _store.SaveSession(session);
                }
                else
                {
                    _store.ClearSession();
                }

                ResultSession = session;
                DialogResult = true;
            }
            catch (YggdrasilException ex)
            {
                SetError(ex.Message);
            }
            catch (Exception)
            {
                SetError("登录失败，发生了未知错误。");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private bool _busy;

        private void SetBusy(bool busy)
        {
            _busy = busy;
            LoginButton.IsEnabled = !busy;
            LoginButton.Content = busy ? "登录中…" : "登录";
            UsernameBox.IsEnabled = !busy;
            PasswordBox.IsEnabled = !busy;
        }

        private void SetError(string message)
        {
            ErrorText.Text = message;
        }

        private void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl(YggdrasilClient.RegisterUrl);
        }

        private void ForgotButton_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl(YggdrasilClient.LoginPageUrl);
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // Browser may be unavailable; ignore.
            }
        }
    }
}
