using System.Windows.Controls;
using CMUmcl;
using CMUmcl.Auth;

internal static partial class Program
{
    private static async Task RememberPasswordUiChecks()
    {
        var store = new AuthStore(); store.ClearSession();
        var session = new AuthSession {AccessToken="fixture-token",ClientToken="fixture-client",SelectedProfileId=new string('a',32),SelectedProfileName="fixture",Username="fixture-user"};
        using var client = new YggdrasilClient();
        var window = new LoginWindow(client,store);
        try
        {
            Check(!window.RememberPassword && Get<CheckBox>(window,"RememberPasswordBox").IsChecked == false,
                "remember-password is explicit opt-in for existing and new users");
            Get<CheckBox>(window,"RememberPasswordBox").IsChecked = true;
            Get<CheckBox>(window,"RememberBox").IsChecked = false;
            await Task.Yield();
            Check(!window.RememberPassword && !Get<CheckBox>(window,"RememberPasswordBox").IsEnabled,
                "do-not-remember-login disables credential persistence even if password option was checked");
        }
        finally { window.Close(); }
        store.SaveLogin(session,"fixture-user","fixture-only-password",true);
        window = new LoginWindow(client,store);
        try
        {
            Check(window.RememberPassword && Get<TextBox>(window,"UsernameBox").Text == "fixture-user",
                "login form reflects previous explicit opt-in and restores only login identifier");
            Check(Get<PasswordBox>(window,"PasswordBox").Password == string.Empty,
                "remembered password is never copied into the visible login form");
        }
        finally { window.Close(); store.ClearSession(); }
    }
}
