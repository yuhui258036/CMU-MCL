using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CMUmcl;
using CMUmcl.Appearance;
using CMUmcl.Content;
using CMUmcl.Launch;
using CMUmcl.Auth;
using CMUmcl.Update;

internal static partial class Program
{
    private static async Task Until(Func<bool> condition, int timeout=5000)
    {
        var start=DateTime.UtcNow;
        while(!condition()) { if((DateTime.UtcNow-start).TotalMilliseconds>timeout) throw new Exception("Timed out waiting for UI state"); await Task.Delay(50); }
    }
    private static Task CallAsync(object target,string method,params object[] args) => (Task)target.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(target,args)!;
    private static async Task UiChecks(Application app,MainWindow window,SettingsStore store,InstalledStateStore installed,LaunchService launch,YggdrasilClient auth,AuthStore authStore,bool live)
    {
        Check(window.FindName("SaveSettingsButton")==null,"no explicit save action remains");
        Click(window,"ThemeToggleButton");
        Check(Get<ScrollViewer>(window,"CardsScroll").IsVisible && !Get<FrameworkElement>(window,"SettingsScroll").IsVisible,"theme toggle preserves home page");
        Check(store.Load().Theme==LauncherTheme.Dark,"title-bar theme toggle persists immediately");
        Click(window,"ThemeToggleButton"); Screenshot(window,"home-light.png");
        Click(window,"SettingsNavButton"); await Task.Delay(100);
        var memory=Get<TextBox>(window,"MemoryTextBox");
        Get<RadioButton>(window,"MemoryManualRadio").IsChecked=true;
        Get<Slider>(window,"MemorySlider").Value=3072;
        Check(memory.Text=="3072" && store.Load().MaxMemoryMb==3072,"memory slider saves immediately");
        memory.Text="2048";
        Check(!store.Load().AutoMemory && store.Load().MaxMemoryMb==2048,"typed valid memory persists without save");
        window.UpdateLayout(); var caret=memory.GetRectFromCharacterIndex(0);
        Check(caret.X>=1 && caret.X<=14,"textbox leading inset applied once");
        memory.Text="bad";
        Check(Get<TextBlock>(window,"MemoryErrorText").Visibility==Visibility.Visible && store.Load().MaxMemoryMb==2048,"invalid memory keeps last valid setting");
        memory.Text="2048";
        Get<RadioButton>(window,"MemoryAutoRadio").IsChecked=true;
        Check(store.Load().AutoMemory && store.Load().MaxMemoryMb==2048 && !memory.IsEnabled,"auto memory persists and remembers manual allocation");
        Directory.CreateDirectory(LauncherPaths.SettingsPath+".tmp");
        Get<RadioButton>(window,"MemoryManualRadio").IsChecked=true; memory.Text="3072";
        Get<ComboBox>(window,"ThemeCombo").SelectedIndex=(int)LauncherTheme.Dark;
        Check(Get<Border>(window,"PreferenceNotice").IsVisible && store.Load().AutoMemory && !AppearanceManager.IsDark,"save failure preserves committed state and reports retry");
        Directory.Delete(LauncherPaths.SettingsPath+".tmp");
        Click(window,"RetryPreferenceButton");
        Check(!store.Load().AutoMemory && store.Load().MaxMemoryMb==3072 && AppearanceManager.IsDark && !Get<Border>(window,"PreferenceNotice").IsVisible,"retry commits all pending valid settings");
        Screenshot(window,"runtime-dark.png");
        Get<ComboBox>(window,"ThemeCombo").SelectedIndex=(int)LauncherTheme.Light;
        Screenshot(window,"runtime-light.png");
        string java=CreateJavaFixture("chosen-java","21.0.5","x64");
        Get<ComboBox>(window,"JavaCandidatesCombo").ItemsSource=new[]{JavaLocator.DetectExecutable(java)!};
        Get<ComboBox>(window,"JavaCandidatesCombo").SelectedIndex=0;
        await Until(()=>store.Load().JavaPath==java);
        Check(Get<RadioButton>(window,"JavaManualRadio").IsChecked==true,"selected Java validates and saves automatically");
        Check(Descendants(Get<ComboBox>(window,"JavaCandidatesCombo")).OfType<TextBlock>().Any(t=>t.Text.Contains("21.0.5")),"Java selection shows version and architecture");
        Get<TextBox>(window,"JavaPathTextBox").Text=Path.Combine(_output,"nonexistent.exe");
        await Until(()=>Get<TextBlock>(window,"JavaErrorText").Visibility==Visibility.Visible);
        Click(window,"ThemeToggleButton");
        Check(store.Load().JavaPath==java && store.Load().Theme==LauncherTheme.Dark,"invalid Java does not block independent theme save");
        Get<RadioButton>(window,"JavaAutoRadio").IsChecked=true;
        Check(store.Load().JavaPath==null,"automatic Java clears manual override immediately");
        Click(window,"AppearanceTab");
        await ProxyUiChecks(window,store);
        Click(window,"AppearanceTab");
        string image=Path.Combine(_output,"test-background.png"), second=Path.Combine(_output,"second-background.png");
        CreateTestImage(image); CreateTestImage(second);
        // Optional local fixture for inspecting readability against a real wallpaper.
        string? contrastImage=Environment.GetEnvironmentVariable("CMUMCL_TEST_BACKGROUND");
        if(!string.IsNullOrWhiteSpace(contrastImage)) { File.Copy(contrastImage,image,true); File.Copy(contrastImage,second,true); }
        await CallAsync(window,"AddBackgroundImagesAsync",new object[]{new[]{image,second}});
        await Until(()=>!AppearanceManager.BackgroundLoading);
        Check(store.Load().BackgroundImages.Count==2 && store.Load().BackgroundMode==BackgroundKind.Local,"local multiselection imports and saves both backgrounds");
        File.Move(image,image+".moved",true);
        await AppearanceManager.NextBackgroundAsync();
        Check(AppearanceManager.HasBackground && AppearanceManager.BackgroundError==null,"owned images survive original file move");
        string? previous=AppearanceManager.CurrentBackgroundPath;
        await AppearanceManager.NextBackgroundAsync();
        Check(previous!=AppearanceManager.CurrentBackgroundPath,"next background does not repeat current local image");
        previous=AppearanceManager.CurrentBackgroundPath;
        Get<Slider>(window,"BackgroundOpacitySlider").Value=100;
        Click(window,"ThemeToggleButton");
        Check(AppearanceManager.CurrentBackgroundPath==previous && Math.Abs(store.Load().BackgroundOpacity-1)<0.001,"theme and opacity changes do not advance wallpaper");
        Check(Get<Grid>(window,"WindowSurface").Background is DrawingBrush && Get<Border>(window,"SidebarSurface").Background.Opacity<1 && Get<Border>(window,"AppearanceCard").Background.Opacity<1,"single wallpaper covers window with translucent sidebar and cards");
        var scroll=Get<ScrollViewer>(window,"SettingsContentScroll");
        window.Width=980; window.Height=620; await Task.Delay(150); scroll.ScrollToEnd(); await Task.Delay(80);
        double offset=scroll.VerticalOffset;
        Click(window,"ThemeToggleButton"); await Task.Delay(80);
        Check(Get<Border>(window,"AppearanceCard").IsVisible && Math.Abs(scroll.VerticalOffset-offset)<1,"theme toggle preserves current settings tab and scroll");
        Screenshot(window,"appearance-minimum.png");
        window.Width=1200; window.Height=800; scroll.ScrollToTop(); await Task.Delay(120);
        Screenshot(window,"appearance-dark.png");
        Click(window,"ThemeToggleButton"); Screenshot(window,"appearance-light.png");
        Get<CheckBox>(window,"BackgroundAutoSwitchCheck").IsChecked=false;
        Get<TextBox>(window,"BackgroundIntervalTextBox").Text="15";
        Check(!store.Load().BackgroundAutoSwitch && store.Load().BackgroundIntervalSeconds==15,"rotation settings autosave");
        Get<TextBox>(window,"BackgroundIntervalTextBox").Text="0";
        Check(store.Load().BackgroundIntervalSeconds==15 && Get<TextBlock>(window,"BackgroundIntervalError").Text.Length>0,"invalid rotation interval preserves last valid value");
        Get<TextBox>(window,"BackgroundIntervalTextBox").Text="15";
        Get<TextBox>(window,"BackgroundUrlTextBox").Text="file:///invalid";
        Check(store.Load().BackgroundUrl=="" && Get<TextBlock>(window,"BackgroundUrlError").Text.Length>0,"unsupported URL scheme rejected without saving");
        if(live)
        {
            Get<TextBox>(window,"BackgroundUrlTextBox").Text="https://acg.neko-arc.top/api.php";
            Get<ComboBox>(window,"BackgroundModeCombo").SelectedIndex=(int)BackgroundKind.Network;
            await Until(()=>!AppearanceManager.BackgroundLoading,35000);
            Check(AppearanceManager.BackgroundError==null && AppearanceManager.CurrentBackgroundPath?.EndsWith(".image")==true,"user supplied image API downloads decodes and displays in real UI");
            Screenshot(window,"network-background.png");
        }
        Get<ComboBox>(window,"BackgroundModeCombo").SelectedIndex=(int)BackgroundKind.Local;
        await Until(()=>!AppearanceManager.BackgroundLoading);
        Click(window,"StorageTab");
        Get<ComboBox>(window,"IsolationComboBox").SelectedIndex=0;
        Check((int)store.Load().VersionIsolation==0,"version isolation autosaves");
        string directory=Path.Combine(_output,"new-instances");
        await CallAsync(window,"ChangeInstancesDirectoryAsync",directory);
        Check(store.Load().InstancesDirectory==directory && Get<TextBox>(window,"InstancesDirectoryTextBox").IsReadOnly,"empty instance directory change commits on selection");
        Screenshot(window,"storage-light.png");
        Click(window,"UpdateTab");
        Get<CheckBox>(window,"AutoUpdatePacksCheck").IsChecked=false;
        Check(!store.Load().AutoUpdatePacks,"pack update checkbox autosaves");
        Screenshot(window,"update-light.png");
        var create=Get<CreateInstanceView>(window,"CreateInstanceView");create.Open(new LocalInstanceService(installed),launch);
        Click(window,"ThemeToggleButton");
        Check(create.IsVisible && ((SolidColorBrush)create.FindResource("CivBg")).Color.A==0,"theme preserves create page with whole-window background");
        window.UpdateLayout();
        Check(!Get<Grid>(window,"MainContent").IsVisible,"create page hides previous content while retaining shared wallpaper");
        Screenshot(window,"create-dark.png");
        Click(window,"ThemeToggleButton"); Screenshot(window,"create-light.png");create.Close();
        window.UpdateLayout();
        Check(Get<Grid>(window,"MainContent").IsVisible,"closing create page restores previous content");
        var login=new LoginWindow(auth,authStore){ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-16000,Top=-16000};login.Show();
        foreach(var theme in new[]{LauncherTheme.Light,LauncherTheme.Dark})
        {
            Get<ComboBox>(window,"ThemeCombo").SelectedIndex=(int)theme;
            Check(login.Background is DrawingBrush,"login uses global background in "+theme);
            Check(((SolidColorBrush)login.Foreground).Color.R==(theme==LauncherTheme.Light?24:228),"login follows "+theme+" palette");
            Screenshot(login,"login-"+theme.ToString().ToLowerInvariant()+".png");
        }
        Get<TextBox>(login,"UsernameBox").Text="preview-user";
        Get<PasswordBox>(login,"PasswordBox").Password="preview-only";
        var beforeTheme=AppearanceManager.IsDark;
        Click(login,"ThemeToggleButton");
        Check(AppearanceManager.IsDark!=beforeTheme && store.Load().Theme==(AppearanceManager.IsDark?LauncherTheme.Dark:LauncherTheme.Light),"login theme toggle persists selection");
        Check(Get<TextBox>(login,"UsernameBox").Text=="preview-user" && Get<PasswordBox>(login,"PasswordBox").Password=="preview-only","login theme toggle preserves entered fields");
        Check(Get<TextBlock>(login,"VersionText").Text=="版本 "+LauncherUpdateClient.CurrentVersion.ToString(3),"login displays current release version");
        Get<TextBox>(login,"UsernameBox").Clear();
        Get<PasswordBox>(login,"PasswordBox").Clear();
        Click(login,"LoginButton");
        Check(Get<TextBlock>(login,"ErrorText").Text=="请输入用户名 / 邮箱。" && Get<Button>(login,"LoginButton").IsEnabled,"empty login still validates locally");
        Screenshot(login,"login-validation.png");
        login.Close();
        foreach(var entry in store.Load().BackgroundImages.ToArray()) Call(window,"RemoveBackground",entry);
        await Until(()=>!AppearanceManager.BackgroundLoading);
        Check(store.Load().BackgroundImages.Count==0 && !AppearanceManager.HasBackground,"removing all local backgrounds restores plain theme");
        Get<ComboBox>(window,"BackgroundModeCombo").SelectedIndex=(int)BackgroundKind.Default;
        Check(store.Load().BackgroundMode==BackgroundKind.Default && app.Resources["WindowBackdropBrush"] is SolidColorBrush,"default background persists and clears custom rendering");
        var plainLogin=new LoginWindow(auth,authStore){ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-16000,Top=-16000};
        plainLogin.Show();
        foreach(var theme in new[]{LauncherTheme.Light,LauncherTheme.Dark})
        {
            var loginSettings=store.Load(); loginSettings.Theme=theme; store.Save(loginSettings);
            await AppearanceManager.ApplyAsync(loginSettings);
            Screenshot(plainLogin,"login-default-"+theme.ToString().ToLowerInvariant()+".png");
        }
        plainLogin.Close();
        Check(!File.Exists(Path.Combine(LauncherPaths.RootDirectory,"auth.dat")),"fixture never creates real account data");
    }
}
