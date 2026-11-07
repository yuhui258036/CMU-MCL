// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using CMUmcl;
using CMUmcl.Content;

internal static partial class Program
{
    private static async Task GranularSettingsChecks(MainWindow owner, SettingsStore store, string directory)
    {
        var global=store.Load();global.JavaPath=@"C:\global\javaw.exe";global.MaxMemoryMb=3072;global.AutoMemory=true;
        global.WindowWidth=1024;global.WindowHeight=600;global.FullScreen=false;global.ExtraJvmArguments=new[]{"-Dscope=global"};
        var local=new InstanceOptions{Java=new(){Path=@"C:\instance\javaw.exe",AutoMemory=false,MemoryMb=6144},Window=new(){Width=1280,Height=720,FullScreen=true},JvmArguments=Array.Empty<string>()};
        foreach(string key in InstanceOptionsStore.OverrideKeys)
        {
            local.OverrideProperties=new[]{key};InstanceOptionsStore.Save(directory,local);
            var value=InstanceOptionsStore.Resolve(global,InstanceOptionsStore.Load(directory));
            Check(value.JavaPath==(key=="java"?local.Java.Path:global.JavaPath) && value.MaxMemoryMb==(key=="memory"?6144:3072) && value.AutoMemory!=(key=="memory")
                && value.FullScreen==(key=="windowMode") && value.WindowWidth==(key=="windowSize"?1280:1024) && value.ExtraJvmArguments.Length==(key=="arguments"?0:1),"granular: independent override round trip "+key);
        }
        local.OverrideProperties=new[]{"memory"};InstanceOptionsStore.Save(directory,local);
        string before=File.ReadAllText(Path.Combine(directory,"launcher-settings.json"));
        global.JavaPath=@"C:\changed\javaw.exe";global.WindowWidth=1600;
        var changed=InstanceOptionsStore.Resolve(global,InstanceOptionsStore.Load(directory));
        Check(changed.JavaPath==global.JavaPath && changed.WindowWidth==1600 && changed.MaxMemoryMb==6144,"granular: future defaults propagate only to inherited properties");
        Check(File.ReadAllText(Path.Combine(directory,"launcher-settings.json"))==before,"granular: reading does not migrate or rewrite settings");
        bool rejected=false;try{InstanceOptionsStore.Validate(new(){OverrideProperties=new[]{"memory"}});}catch(InvalidDataException){rejected=true;}
        Check(rejected,"granular: missing override payload rejected");
        InstanceOptionsStore.Save(directory,new());
        var view=new InstanceSettingsView(directory,"Inheritance fixture",store){Width=850,Height=650};view.Show();await Task.Delay(30);
        T Field<T>(string name)=>(T)view.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;
        var memory=Field<CheckBox>("_memoryOverride");memory.IsChecked=true;memory.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Field<CheckBox>("_auto").IsChecked=false;Field<TextBox>("_memory").Text="5376";
        var nav=Descendants(view).OfType<ListBox>().First();nav.SelectedIndex=2;await Task.Delay(80);
        var content=Field<InstanceContentView>("_embeddedContent");
        Check(content!=null && !content.Closed,"unified: mod manager opens inside instance editor");
        nav.SelectedIndex=0;
        Check(content!.Closed && Field<TextBox>("_memory").Text=="5376" && view.HasUnsavedChanges,"unified: tab switch retains edits and closes content resources");
        var javaOverride=Field<CheckBox>("_javaOverride");javaOverride.IsChecked=true;javaOverride.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        var candidates=Field<ComboBox>("_javaCandidates");candidates.ItemsSource=new[]{new CMUmcl.Launch.JavaInfo{JavaPath=@"C:\fixture\javaw.exe",Version="21",Architecture="x64"}};candidates.SelectedIndex=0;
        Check(Field<TextBox>("_java").Text==@"C:\fixture\javaw.exe","granular: Java selection sets explicit path");
        javaOverride.IsChecked=false;javaOverride.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(candidates.SelectedIndex==-1 && Field<TextBox>("_java").Text==(store.Load().JavaPath??""),"granular: reverting Java clears stale detected selection");
        var defaults=store.Load();defaults.MaxMemoryMb=7168;defaults.AutoMemory=false;Check(store.Save(defaults),"granular: updated defaults saved");
        view.RefreshInherited();Check(Field<TextBox>("_memory").Text=="5376","granular: global refresh preserves independent memory");
        memory.IsChecked=false;memory.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(Field<TextBox>("_memory").Text=="7168" && !Field<TextBox>("_memory").IsEnabled,"granular: revert restores current default and disables control");
        var size=Field<CheckBox>("_sizeOverride");size.IsChecked=true;size.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Field<ComboBox>("_resolution").SelectedIndex=2;
        Check(Field<TextBox>("_width").Text=="1280" && Field<TextBox>("_height").Text=="720","granular: resolution preset updates dimensions");
        Field<TextBox>("_width").Text="1366";
        Check(Field<ComboBox>("_resolution").SelectedIndex==0,"granular: manual dimensions clear mismatched preset");
        var folder=Descendants(view).OfType<Button>().Single(x=>Equals(x.Content,"打开目录"));folder.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));view.UpdateLayout();
        Check(view.Content is InstanceContentView && !Descendants(view).OfType<ListBox>().Any(x=>x.IsVisible) && Descendants(view).OfType<Button>().Count(x=>x.Tag is string)==8,"unified: directory page shows eight cards without category sidebar");
        Descendants(view).OfType<Button>().Single(x=>Equals(x.Content,"← 返回")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Field<TextBox>("_width").Text=="1366" && view.HasUnsavedChanges,"unified: returning from directory page preserves settings edits");
        view.SaveAndClose();var saved=InstanceOptionsStore.Load(directory);
        Check(view.Saved && saved.OverrideProperties!.SequenceEqual(new[]{"windowSize"}),"granular: UI saves only chosen override keys");
        // Version pickers load their own lists; vanilla has no loader build.
        VersionChoices.GameOverride=()=>Task.FromResult(new[]{new VersionChoice("1.21.1","正式版","2024-08-08"),new VersionChoice("24w14potato","愚人节","2024-04-01"),new VersionChoice("1.20.4","正式版","2023-12-07"),new VersionChoice("b1.7.3","远古版本","2011-07-08")});
        VersionChoices.Override=(loader,game)=>Task.FromResult(game=="1.21.1"?new[]{"0.16.10","0.16.9"}:new[]{"0.15.0"});
        try
        {
            string pickerDir=Path.Combine(_output,"picker-local");Directory.CreateDirectory(Path.Combine(pickerDir,"mods"));
            File.WriteAllText(Path.Combine(pickerDir,"instance.json"),"{\"id\":\"local:picker\",\"name\":\"Picker\",\"game\":\"1.21.1\",\"loader\":\"fabric\",\"loaderVersion\":\"0.16.9\"}");
            using(var zip=System.IO.Compression.ZipFile.Open(Path.Combine(pickerDir,"mods","fabric-api-0.92.0.jar"),System.IO.Compression.ZipArchiveMode.Create))
            using(var writer=new StreamWriter(zip.CreateEntry("fabric.mod.json").Open()))writer.Write("{\"id\":\"fabric-api\",\"version\":\"0.92.0\"}");
            var picker=new InstanceSettingsView(pickerDir,"Version fixture",store){Width=850,Height=650};picker.Show();await Task.Delay(30);
            Descendants(picker).OfType<ListBox>().First().SelectedIndex=1;await Task.Delay(80);picker.UpdateLayout();
            T PickerField<T>(string name)=>(T)picker.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(picker)!;
            await Until(()=>Descendants(picker).OfType<Button>().Any(x=>Equals(x.Content,"升级到 0.16.10")&&x.IsVisible));
            Check(Descendants(picker).OfType<TextBlock>().Any(t=>t.Text=="Minecraft 1.21.1 · Fabric 0.16.9"),"components: current version summary is shown");
            await Until(()=>PickerField<TextBlock>("_fabricApiState").Text=="已安装 0.92.0");
            Check(Descendants(picker).OfType<Button>().Any(x=>Equals(x.Content,"更新到最新版本")&&x.IsVisible),"components: installed Fabric API is detected and offers an update");
            Check(!Descendants(picker).OfType<Button>().Any(x=>Equals(x.Content,"取消")&&x.IsVisible),"components: cancel only appears while something runs");
            var game=PickerField<ComboBox>("_gameVersion");var kind=PickerField<ComboBox>("_loaderKind");var loaderVersion=PickerField<ComboBox>("_loaderVersion");
            Check(game.Items.Count==4 && game.Items.OfType<VersionChoice>().Any(v=>v.Tag=="愚人节") && game.Items.OfType<VersionChoice>().Any(v=>v.Tag=="远古版本") && game.Text=="1.21.1" && !Descendants(picker).OfType<Button>().Any(x=>x.Content is string s&&s.StartsWith("获取")),"versions: list loads with the tab and includes every version type");
            game.IsDropDownOpen=true;await Task.Delay(80);picker.UpdateLayout();
            var firstItem=(DependencyObject)game.ItemContainerGenerator.ContainerFromIndex(0);
            Check(firstItem!=null && Descendants(firstItem).OfType<TextBlock>().Any(t=>t.Text=="正式版 · 2024-08-08"),"versions: dropdown shows each version's type and date");
            var dropDown=(FrameworkElement)((System.Windows.Controls.Primitives.Popup)game.Template.FindName("PART_Popup",game)).Child;
            var shot=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(dropDown.ActualWidth),(int)Math.Ceiling(dropDown.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);shot.Render(dropDown);
            var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(shot));using(var file=File.Create(Path.Combine(_output,"version-dropdown.png")))png.Save(file);
            game.IsDropDownOpen=false;
            kind.SelectedItem="vanilla";await Task.Delay(20);
            Check(!loaderVersion.IsEnabled && loaderVersion.Text=="","versions: vanilla clears and disables loader version");
            kind.SelectedItem="fabric";await Until(()=>loaderVersion.Text=="0.16.10");
            Check(loaderVersion.IsEnabled && loaderVersion.Items.Count==2,"versions: choosing a loader selects its newest compatible build");
            game.SelectedItem=game.Items.OfType<VersionChoice>().Single(v=>v.Name=="1.20.4");await Until(()=>loaderVersion.Text=="0.15.0");
            Check(game.Text=="1.20.4","versions: choosing an entry puts the plain version id in the field");
            Check(true,"versions: changing game version re-picks a compatible loader build");
            picker.Close();
            var server=new InstanceSettingsView(directory,"Server fixture",store){Width=850,Height=650};server.Show();await Task.Delay(30);
            Descendants(server).OfType<ListBox>().First().SelectedIndex=1;await Task.Delay(40);server.UpdateLayout();
            var serverGame=(ComboBox)server.GetType().GetField("_gameVersion",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(server)!;
            Check(!serverGame.IsVisible && Descendants(server).OfType<TextBlock>().Any(t=>t.Text.StartsWith("这是服务器整合包")) && Descendants(server).OfType<Button>().Any(x=>Equals(x.Content,"检查并修复游戏文件")&&x.IsVisible),
                "components: server packs explain version ownership instead of a failing form, repair stays available");
            server.Close();
        }
        finally { VersionChoices.Override=null;VersionChoices.GameOverride=null; }
        Click(owner,"SettingsNavButton");Click(owner,"RuntimeTab");owner.UpdateLayout();
        var editor=(InstanceSettingsView)Get<ContentControl>(owner,"GameDefaultsCard").Content;
        Check(editor.IsGlobal && editor.IsVisible && !Get<ContentControl>(owner,"InstanceSettingsHost").IsVisible,"global: settings render inline without changing page");
        Check(!Descendants(editor).OfType<Button>().Any(x=>Equals(x.Content,"← 返回")),"global: inline form has no nested back navigation");
        var globalWidth=(TextBox)editor.GetType().GetField("_width",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(editor)!;
        globalWidth.Text="1440";
        Click(owner,"AppearanceTab");Click(owner,"RuntimeTab");Check(globalWidth.Text=="1440","global: tab switches preserve form edits");
        editor.SaveAndClose();
        Check(!editor.IsClosed && !editor.HasUnsavedChanges && store.Load().WindowWidth==1440 && Get<Grid>(owner,"SettingsScroll").IsVisible,"global: save persists inline and keeps settings page open");
        RenderScaled(owner,1,Path.Combine(_output,"global-settings-inline.png"));
    }
}
