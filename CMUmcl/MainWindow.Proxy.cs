using System.Windows;
using System.Windows.Controls;
using CMUmcl.Content;
using CMUmcl.Network;

namespace CMUmcl;

public partial class MainWindow
{
    private bool _proxyPasswordUnreadable;
    private void LoadProxyControls()
    {
        ProxyModeCombo.SelectedIndex = (int)_settings.ProxyMode;
        ProxyHostTextBox.Text = _settings.ProxyHost;
        ProxyPortTextBox.Text = _settings.ProxyPort.ToString();
        ProxyAuthenticationCheck.IsChecked = _settings.ProxyAuthentication;
        ProxyUsernameTextBox.Text = _settings.ProxyUsername;
        try { ProxyPasswordBox.Password = LauncherProxy.Unprotect(_settings.ProxyPasswordProtected); }
        catch { _proxyPasswordUnreadable = true; ProxyErrorText.Text = "无法读取代理密码，请重新输入。"; }
        RefreshProxyControls();
    }
    private void RefreshProxyControls()
    {
        if (ProxyDetailsPanel == null || ProxyCredentialsPanel == null) return;
        ProxyDetailsPanel.IsEnabled = ProxyModeCombo.SelectedIndex >= (int)LauncherProxyMode.Http;
        ProxyCredentialsPanel.IsEnabled = ProxyAuthenticationCheck.IsChecked == true;
    }
    private void ProxyMode_Changed(object sender, SelectionChangedEventArgs e) { RefreshProxyControls(); SaveProxyPreference(); }
    private void ProxyField_Changed(object sender, TextChangedEventArgs e) => SaveProxyPreference();
    private void ProxyAuthentication_Changed(object sender, RoutedEventArgs e) { RefreshProxyControls(); SaveProxyPreference(); }
    private void ProxyPassword_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingPreferences) return;
        _proxyPasswordUnreadable = false; SaveProxyPreference();
    }
    private void SaveProxyPreference()
    {
        if (!_preferencesReady || _loadingPreferences || ProxyErrorText == null || ProxyModeCombo.SelectedIndex < 0) return;
        var mode = (LauncherProxyMode)ProxyModeCombo.SelectedIndex;
        if (mode is LauncherProxyMode.System or LauncherProxyMode.Direct)
        {
            ProxyErrorText.Text = "";
            SavePreference(s => s.ProxyMode = mode);
            return;
        }
        if (!int.TryParse(ProxyPortTextBox.Text, out int port) || port is < 1 or > 65535)
        { ProxyErrorText.Text = "端口应为 1～65535。尚未应用此修改。"; return; }
        string host = ProxyHostTextBox.Text.Trim();
        if (!LauncherProxy.TryAddress(mode, host, port, out _))
        { ProxyErrorText.Text = "请输入有效的代理主机（域名或 IP，不含协议和端口）。尚未应用此修改。"; return; }
        bool authentication = ProxyAuthenticationCheck.IsChecked == true;
        if (authentication && _proxyPasswordUnreadable)
        { ProxyErrorText.Text = "无法读取代理密码，请重新输入。尚未应用此修改。"; return; }
        try
        {
            var saved = _unsavedSettings ?? _settings;
            string passwordProtected = saved.ProxyPasswordProtected;
            if (!_proxyPasswordUnreadable)
            {
                string? oldPassword;
                try { oldPassword = LauncherProxy.Unprotect(passwordProtected); }
                catch { oldPassword = null; }
                if (ProxyPasswordBox.Password != oldPassword) passwordProtected = LauncherProxy.Protect(ProxyPasswordBox.Password);
            }
            ProxyErrorText.Text = "";
            SavePreference(s =>
            {
                s.ProxyMode = mode; s.ProxyHost = host; s.ProxyPort = port;
                s.ProxyAuthentication = authentication; s.ProxyUsername = ProxyUsernameTextBox.Text;
                s.ProxyPasswordProtected = passwordProtected;
            });
        }
        catch { ProxyErrorText.Text = "无法保存代理密码，请重新输入。尚未应用此修改。"; }
    }
}
