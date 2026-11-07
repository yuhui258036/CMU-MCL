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
        var editor=(InstanceSettingsView)Get<ContentControl>(window,"GameDefaultsCard").Content;
        T Field<T>(string name)=>(T)typeof(InstanceSettingsView).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(editor)!;
        void SaveDefaults()=>editor.FlushPendingSave();
        var status=Field<TextBlock>("_status");var memory=Field<TextBox>("_memory");var auto=Field<CheckBox>("_auto");
        Check(editor.IsGlobal && editor.IsVisible && window.FindName("MemoryTextBox")==null && window.FindName("JavaPathTextBox")==null,"game defaults use one inline editor without legacy controls");
        auto.IsChecked=false;auto.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Field<Slider>("_memorySlider").Value=3072;
        Check(memory.Text=="3072" && memory.IsEnabled,"memory slider fills manual allocation");
        Check(!Descendants(editor).OfType<Button>().Any(x=>Equals(x.Content,"保存")),"game defaults have no save button");
        await Until(()=>!store.Load().AutoMemory && store.Load().MaxMemoryMb==3072,3000);
        Check(!store.Load().AutoMemory && store.Load().MaxMemoryMb==3072 && !editor.HasUnsavedChanges,"manual memory persists on save");
        window.UpdateLayout(); var caret=memory.GetRectFromCharacterIndex(0);
        Check(caret.X>=1 && caret.X<=14,"textbox leading inset applied once");
        memory.Text="bad";SaveDefaults();
        Check(status.Text.Contains("内存") && store.Load().MaxMemoryMb==3072 && editor.HasUnsavedChanges,"invalid memory keeps last valid setting");
        long physical=MemorySnapshot.Read().TotalMb;
        if(physical>0)
        {
            memory.Text=(physical+1024).ToString();SaveDefaults();
            Check(status.Text.Contains("物理内存") && store.Load().MaxMemoryMb==3072,"manual memory above physical RAM is rejected on save");
        }
        memory.Text="2048";
        Directory.CreateDirectory(LauncherPaths.SettingsPath+".tmp");
        SaveDefaults();
        Check(status.Text.StartsWith("保存失败") && store.Load().MaxMemoryMb==3072 && editor.HasUnsavedChanges,"game defaults save failure keeps edits and previous file");
        Get<ComboBox>(window,"ThemeCombo").SelectedIndex=(int)LauncherTheme.Dark;
        Check(Get<Border>(window,"PreferenceNotice").IsVisible && !AppearanceManager.IsDark,"save failure preserves committed state and reports retry");
        Directory.Delete(LauncherPaths.SettingsPath+".tmp");
        Click(window,"RetryPreferenceButton");
        Check(AppearanceManager.IsDark && !Get<Border>(window,"PreferenceNotice").IsVisible,"retry commits pending launcher preferences");
        SaveDefaults();
        Check(store.Load().MaxMemoryMb==2048 && store.Load().Theme==LauncherTheme.Dark,"game defaults save after the directory recovers without reverting theme");
        auto.IsChecked=true;auto.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));SaveDefaults();
        Check(store.Load().AutoMemory && store.Load().MaxMemoryMb==2048 && !memory.IsEnabled,"auto memory persists and remembers manual allocation");
        Screenshot(window,"runtime-dark.png");
        Get<ComboBox>(window,"ThemeCombo").SelectedIndex=(int)LauncherTheme.Light;
        Screenshot(window,"runtime-light.png");
        var runtimeScroll=Get<ScrollViewer>(window,"SettingsContentScroll");runtimeScroll.ScrollToEnd();window.UpdateLayout();
        Screenshot(window,"runtime-bottom-light.png");runtimeScroll.ScrollToTop();window.UpdateLayout();
        string java=CreateJavaFixture("chosen-java","21.0.5","x64");
        var candidates=Field<ComboBox>("_javaCandidates");
        candidates.ItemsSource=new[]{JavaLocator.DetectExecutable(java)!};candidates.SelectedIndex=0;
        Check(Field<TextBox>("_java").Text==java,"selected Java fills the path");
        SaveDefaults();
        Check(store.Load().JavaPath==java,"selected Java validates and saves");
        window.UpdateLayout();
        Check(Descendants(candidates).OfType<TextBlock>().Any(t=>t.Text.Contains("21.0.5")),"Java selection shows version and architecture");
        Field<TextBox>("_java").Text=Path.Combine(_output,"nonexistent.exe");SaveDefaults();
        Check(status.Text.Contains("Java") && store.Load().JavaPath==java,"invalid Java is rejected without overwriting the saved path");
        Click(window,"ThemeToggleButton");
        Check(store.Load().JavaPath==java && store.Load().Theme==LauncherTheme.Dark,"invalid Java does not block independent theme save");
        Descendants(editor).OfType<Button>().Single(x=>Equals(x.Content,"自动选择")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));SaveDefaults();
        Check(store.Load().JavaPath==null && !editor.HasUnsavedChanges,"automatic Java clears manual override on save");
        foreach(var theme in new[]{LauncherTheme.Light,LauncherTheme.Dark})
        {
            Get<ComboBox>(window,"ThemeCombo").SelectedIndex=(int)theme;
            Window? shown=null;
            var pending=window.Dispatcher.InvokeAsync(()=>LauncherPrompt.Show(window,"启动失败，发生了未知错误。\nLaunch exception types: IOException (0x80131620).","启动失败",MessageBoxButton.OK,MessageBoxImage.Error));
            await Until(()=>(shown=Application.Current.Windows.OfType<Window>().FirstOrDefault(w=>w.Owner==window&&w.Title=="启动失败"))?.IsLoaded==true);
            shown!.UpdateLayout();
            var card=Descendants(shown).OfType<Border>().First(b=>b.CornerRadius.TopLeft==6);
            Check(((SolidColorBrush)card.Background).Color==((SolidColorBrush)Application.Current.FindResource("PopupBrush")).Color
                && shown.WindowStyle==WindowStyle.None && !Descendants(shown).OfType<Button>().Any(b=>Equals(b.Content,"—")||Equals(b.Content,"×")),"prompt: themed popup card without system caption buttons in "+theme);
            Check(Math.Abs(shown.Width-window.ActualWidth)<1 && Math.Abs(shown.Height-window.ActualHeight)<1,"prompt: backdrop covers the launcher in "+theme);
            Screenshot(shown,"prompt-"+theme.ToString().ToLowerInvariant()+".png");
            Descendants(shown).OfType<Button>().Single(b=>Equals(b.Content,"确定")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(await pending.Task==MessageBoxResult.OK && !shown.IsVisible,"prompt: confirm closes and returns OK in "+theme);
        }
        var afterLaunch=Field<ComboBox>("_afterLaunch");window.UpdateLayout();
        var sizeHeading=Descendants(editor).OfType<TextBlock>().First(x=>x.Text=="窗口尺寸");
        Check(afterLaunch.IsVisible && afterLaunch.SelectedIndex==0 && afterLaunch.TranslatePoint(new Point(),editor).Y>sizeHeading.TranslatePoint(new Point(),editor).Y,"after-launch option sits below window size and defaults to keep");
        afterLaunch.SelectedIndex=(int)AfterLaunchAction.Minimize;
        await Until(()=>store.Load().AfterLaunch==AfterLaunchAction.Minimize,3000);
        Check(true,"after-launch option autosaves");
        Call(window,"ApplyAfterLaunch");
        Check(window.WindowState==WindowState.Minimized,"minimize option minimizes the launcher after the game window appears");
        Call(window,"RestoreAfterGames");
        Check(window.WindowState==WindowState.Normal,"launcher is restored when no game is running");
        afterLaunch.SelectedIndex=(int)AfterLaunchAction.Keep;SaveDefaults();
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
        Compat.Move(image,image+".moved",true);
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
        var exited=new InstanceCardViewModel("local:exit-fixture","退出示例","本地实例",new InstanceMetadata{Id="local:exit-fixture",Game="1.21.1"},Path.Combine(_output,"exit-fixture"));
        exited.GameExited(0);Check(exited.StatusText=="游戏已退出","normal game exit is not reported as a crash");
        exited.GameExited(1,"内存不足，请提高内存分配或关闭其他程序。");Check(exited.StatusText.Contains("异常退出（代码 1）") && exited.StatusText.Contains("内存不足"),"abnormal game exit shows code and diagnostic hint");
        exited.GameExited(1,null,stoppedByUser:true);Check(exited.StatusText=="游戏已结束","user-terminated game is not reported as a crash");
    }

    internal static void CommunityUiChecks(Window window)
    {
        // Navigate to the community page via the nav button.
        Click(window, "CommunityNavButton");
        var view = Get<UserControl>(window, "CommunityResourceView");
        Check(view.Visibility == Visibility.Visible, "community: nav shows community view");

        // The view must be bound to the community view model (internal type, InternalsVisibleTo).
        Check(view.DataContext is CMUmcl.Community.CommunityResourceViewModel, "community: view bound to CommunityResourceViewModel");
        var vm = (CMUmcl.Community.CommunityResourceViewModel)view.DataContext;

        // Header chrome reflects the community page.
        Check(Get<TextBlock>(window, "HeaderTitleText").Text == "社区资源", "community: header title is 社区资源");
        Check(Get<Button>(window, "NewInstanceButton").Visibility == Visibility.Collapsed, "community: new-instance button collapsed");
        Check(Get<Button>(window, "RefreshButton").Visibility == Visibility.Visible, "community: refresh button visible");

        // First load shows the empty state without firing a search.
        Check(vm.Results.Count > 0, "community: first entry automatically displays resources");
        Check(!vm.IsLoading, "community: not loading on first load");
        Check(!vm.HasEmptyMessage, "community: loaded resources hide empty message");
        Check(string.IsNullOrEmpty(vm.EmptyMessage), "community: loaded resources clear empty text");
        Check(!vm.HasDetail, "community: no detail on first load");
        Check(vm.Detail == null, "community: detail null on first load");

        // Target / option collections are populated with sane defaults.
        Check(vm.TargetInstances != null, "community: target instances collection present");
        Check(vm.LoaderOptions.Count >= 6, "community: at least six loader options");
        Check(vm.SortOptions.Count >= 4, "community: at least four sort options");
        Check(vm.SourceOptions.Count == 3 && vm.SourceOptions.Any(o => o.Value == CMUmcl.Community.ResourceSourceId.CurseForge), "community: local candidate exposes both sources");
        Check(vm.OnlyCompatible, "community: only-compatible defaults on");
        Check(vm.CanFilterCompatibility == (vm.SelectedTarget != null), "community: compatibility filter gated on selected target");

        // Kind gates the loader filter (set the property directly; do not run SelectKindCommand).
        Check(vm.Kind == CMUmcl.Community.ResourceKind.Mod && vm.ShowLoaderFilter, "community: Mod kind shows loader filter");
        vm.Kind = CMUmcl.Community.ResourceKind.Modpack;
        Check(!vm.ShowLoaderFilter, "community: Modpack kind hides loader filter");
        vm.Kind = CMUmcl.Community.ResourceKind.Mod;
        Check(vm.ShowLoaderFilter, "community: Mod kind restores loader filter");

        // Switching pages retains the same view model instance (state retained).
        Click(window, "PacksNavButton");
        Check(view.Visibility == Visibility.Collapsed, "community: packs nav collapses community view");
        Click(window, "CommunityNavButton");
        Check(view.Visibility == Visibility.Visible, "community: nav back shows community view");
        Check(ReferenceEquals(vm, view.DataContext), "community: page switch retains same view model");

        // Theme / background toggling must not disturb the community page or its VM.
        Click(window, "ThemeToggleButton");
        Check(view.Visibility == Visibility.Visible && ReferenceEquals(vm, view.DataContext), "community: theme toggle keeps community view and VM");
        Click(window, "ThemeToggleButton"); // restore original theme
        Check(view.Visibility == Visibility.Visible && ReferenceEquals(vm, view.DataContext), "community: theme restore keeps community view and VM");

        // Commands are wired.
        Check(vm.SearchCommand != null && vm.SelectKindCommand != null && vm.OpenDetailCommand != null, "community: search/kind/detail commands bound");

        // Navigating and toggling must not auto-fire a network search.
        Check(vm.Results.Count > 0 && !vm.IsLoading, "community: navigation and theme changes preserve loaded resources");

        // The page lays out with a measurable size.
        window.UpdateLayout();
        Check(view.ActualWidth > 0 && view.ActualHeight > 0, "community: view laid out with measurable size");
        var results = Get<ListBox>(view, "ResourceResults"); results.SelectedIndex = 0;
        var sort = Descendants(Get<Border>(view, "SearchFilters")).OfType<ComboBox>().Last(); System.Windows.Input.Keyboard.Focus(sort); window.UpdateLayout();
        Check(sort.FocusVisualStyle == null && results.ItemContainerStyle.Setters.OfType<Setter>().Any(x => x.Property == Control.TemplateProperty), "community: dropdown focus and selected result use launcher styling");
        Screenshot(window, "community-focus.png");
        var searchBox = Get<TextBox>(view, "ResourceSearchText"); var caretX = searchBox.GetRectFromCharacterIndex(0).X;
        Check(caretX >= 1 && caretX <= 14, "community: search box caret starts at the normal inset (" + caretX.ToString("0") + ")");

        // Option value sanity.
        Check(new[]{"fabric","forge","neoforge","quilt"}.All(l => vm.LoaderOptions.Any(o => o.Value == l)), "community: loader options include fabric/forge/neoforge/quilt");
        Check(vm.SourceOptions.Any(o => o.Value == null)
            && vm.SourceOptions.Any(o => o.Value == CMUmcl.Community.ResourceSourceId.Modrinth)
            && vm.SourceOptions.Any(o => o.Value == CMUmcl.Community.ResourceSourceId.CurseForge), "community: source options include all/null/Modrinth/CurseForge");
        Check(vm.SortOptions.Any(o => o.Value == CMUmcl.Community.ResourceSort.Recommended)
            && vm.SortOptions.Any(o => o.Value == CMUmcl.Community.ResourceSort.Downloads)
            && vm.SortOptions.Any(o => o.Value == CMUmcl.Community.ResourceSort.LastUpdated)
            && vm.SortOptions.Any(o => o.Value == CMUmcl.Community.ResourceSort.Name), "community: sort options include Recommended/Downloads/LastUpdated/Name");
    }
}
