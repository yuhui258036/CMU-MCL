// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CMUmcl;
using CMUmcl.Content;
using CmlLib.Core.ProcessBuilder;

internal static partial class Program
{
    private static async Task InstanceSettingsChecks(MainWindow owner,SettingsStore store)
    {
        var dir=Path.Combine(_output,"fixture-instance");Directory.CreateDirectory(dir);
        var original="{\"id\":\"fixture\",\"name\":\"Original\",\"game\":\"1.21.1\",\"loader\":\"fabric\"}";File.WriteAllText(Path.Combine(dir,"instance.json"),original);
        var global=store.Load();global.JavaPath=null;global.AutoMemory=false;global.MaxMemoryMb=3072;global.WindowWidth=1024;global.WindowHeight=600;global.ExtraJvmArguments=new[]{"-Dfixture=global"};Check(store.Save(global),"instance: global fixture saved");
        var empty=InstanceOptionsStore.Load(dir);var inherited=InstanceOptionsStore.Resolve(global,empty);
        Check(inherited.MaxMemoryMb==3072 && inherited.WindowWidth==1024 && inherited.ExtraJvmArguments[0]=="-Dfixture=global","instance: absent overrides inherit global");
        var local=new InstanceOptions{Name="Renamed",Icon="Fabric.png",Java=new(){Path="",MemoryMb=2048,AutoMemory=false},Window=new(){Width=1280,Height=720,FullScreen=true},JvmArguments=new[]{"-Dfixture=local","-Dlabel=value with spaces"}};
        InstanceOptionsStore.Save(dir,local);var loaded=InstanceOptionsStore.Load(dir);var resolved=InstanceOptionsStore.Resolve(global,loaded);
        Check(resolved.MaxMemoryMb==2048 && resolved.JavaPath=="" && !resolved.AutoMemory && resolved.WindowWidth==1280 && resolved.FullScreen,"instance: Java/memory/window overrides resolve");
        Check(global.MaxMemoryMb==3072 && global.ExtraJvmArguments[0]=="-Dfixture=global","instance: merge leaves global object unchanged");
        var option=new MLaunchOption{ExtraJvmArguments=new[]{new MArgument("-javaagent:fixture-auth.jar")}};InstanceOptionsStore.ApplyLaunchOptions(option,resolved);
        Check(option.ScreenWidth==1280 && option.ScreenHeight==720 && option.FullScreen && option.ExtraJvmArguments.Count()==3,"instance: merged window and extra arguments enter actual launch option and retain auth argument");
        Check(option.ExtraJvmArguments.SelectMany(x=>x.Values).Contains("-Dlabel=value with spaces"),"instance: JVM argument with spaces remains one argument");
        Check(File.ReadAllText(Path.Combine(dir,"instance.json"))==original,"instance: save preserves original metadata");
        var card=new InstanceCardViewModel("fixture","Original","本地",new(){Game="1.21.1",Loader="fabric"},dir);Check(card.DisplayName=="Renamed" && card.IconSource!.EndsWith("Fabric.png"),"instance: saved name and icon reflected in list");
        local.Java=null;local.Window=null;local.JvmArguments=Array.Empty<string>();InstanceOptionsStore.Save(dir,local);resolved=InstanceOptionsStore.Resolve(global,InstanceOptionsStore.Load(dir));Check(resolved.MaxMemoryMb==3072 && resolved.ExtraJvmArguments.Length==0,"instance: explicit empty JVM override clears global extras");
        local.JvmArguments=null;InstanceOptionsStore.Save(dir,local);global.MaxMemoryMb=5120;Check(InstanceOptionsStore.Resolve(global,InstanceOptionsStore.Load(dir)).MaxMemoryMb==5120,"instance: inherited settings follow later global changes");
        foreach(var args in new[]{new[]{"-Xmx8G"},new[]{"-cp"},new[]{"bad"},new[]{"-Dfoo=bar\nsecret"}}){bool rejected=false;try{InstanceOptionsStore.ValidateArguments(args);}catch(InvalidDataException){rejected=true;}Check(rejected,"instance: conflicting/malformed JVM arguments rejected");}
        bool invalid=false;try{InstanceOptionsStore.Save(dir,new(){Window=new(){Width=0}});}catch(InvalidDataException){invalid=true;}Check(invalid && InstanceOptionsStore.Load(dir).Window==null,"instance: invalid write preserves previous settings");
        T Field<T>(object window,string name)=>(T)window.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
        foreach(bool dark in new[]{false,true})
        {
            if(CMUmcl.Appearance.AppearanceManager.IsDark!=dark)Click(owner,"ThemeToggleButton");
            foreach(var size in new[]{(850d,650d),(640d,440d)})
            {
                var dialog=new InstanceSettingsView(dir,"Fixture instance",store){Width=size.Item1,Height=size.Item2};dialog.Show();await Task.Delay(60);
                var navigation=Descendants(dialog).OfType<ListBox>().First();
                var selectedItem=(ListBoxItem)navigation.ItemContainerGenerator.ContainerFromIndex(0);
                Click(owner,"ThemeToggleButton");dialog.UpdateLayout();
                var surface=(Border)selectedItem.Template.FindName("Surface",selectedItem);
                Check(((SolidColorBrush)surface.Background).Color==((SolidColorBrush)Application.Current.FindResource("AccentSoftBrush")).Color,
                    "instance: selected navigation follows a live theme switch");
                Click(owner,"ThemeToggleButton");dialog.UpdateLayout();
                foreach(int tab in Enumerable.Range(0,7))
                {
                    Descendants(dialog).OfType<ListBox>().First().SelectedIndex=tab;dialog.UpdateLayout();await Task.Delay(20);
                    Check(dialog.ActualWidth>=640 && Descendants(dialog).OfType<ScrollViewer>().Any(x=>x.ActualWidth>200),"instance: responsive category content "+tab+" "+size+" dark="+dark);
                    var bitmap=new RenderTargetBitmap((int)dialog.ActualWidth,(int)dialog.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(dialog);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(_output,$"settings-{dark}-{size.Item1}-{tab}.png")))png.Save(file);
                }
                foreach(double scale in new[]{1d,1.25d,1.5d,2d})
                {
                    var root=(FrameworkElement)dialog.Content;root.LayoutTransform=new ScaleTransform(scale,scale);dialog.UpdateLayout();await Task.Delay(20);
                    var save=Descendants(dialog).OfType<Button>().First(b=>Equals(b.Content,"← 返回"));var bounds=save.TransformToAncestor(root).TransformBounds(new Rect(save.RenderSize));
                    Check(bounds.Right<=root.ActualWidth+1 && bounds.Bottom<=root.ActualHeight+1 && save.ActualHeight>20,"instance: back action stays within scaled layout "+scale);
                }
                ((FrameworkElement)dialog.Content).LayoutTransform=Transform.Identity;dialog.UpdateLayout();
                Field<CheckBox>(dialog,"_memoryOverride").IsChecked=true;Field<CheckBox>(dialog,"_sizeOverride").IsChecked=true;
                Field<TextBox>(dialog,"_name").Text="UI saved name";Field<CheckBox>(dialog,"_windowOverride").IsChecked=true;Field<TextBox>(dialog,"_width").Text="1152";Field<TextBox>(dialog,"_height").Text="648";
                Field<CheckBox>(dialog,"_javaOverride").IsChecked=true;Field<TextBox>(dialog,"_java").Text="";Field<CheckBox>(dialog,"_auto").IsChecked=false;Field<TextBox>(dialog,"_memory").Text="6144";Field<CheckBox>(dialog,"_argsOverride").IsChecked=true;Field<TextBox>(dialog,"_arguments").Text="-Dui.saved=value with spaces";
                dialog.FlushPendingSave();Check(!dialog.HasUnsavedChanges && InstanceOptionsStore.Load(dir).Name=="UI saved name" && InstanceOptionsStore.Load(dir).Window!.Width==1152,"instance: form edits persist name/window");
                var effectiveUi=InstanceOptionsStore.Resolve(store.Load(),InstanceOptionsStore.Load(dir));
                Check(effectiveUi.MaxMemoryMb==6144 && !effectiveUi.AutoMemory && string.IsNullOrWhiteSpace(effectiveUi.JavaPath) && effectiveUi.ExtraJvmArguments.Single()=="-Dui.saved=value with spaces","instance: Java/memory/JVM form edits reach effective launch configuration");
            }
        }
        var globalDialog=new InstanceSettingsView(dir,"fixture",store,true){};globalDialog.Show();Field<TextBox>(globalDialog,"_width").Text="1366";Field<TextBox>(globalDialog,"_height").Text="768";globalDialog.GetType().GetMethod("Save",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(globalDialog,null);Check(globalDialog.Saved && store.Load().WindowWidth==1366,"instance: global defaults editable and persist");
        await GranularSettingsChecks(owner,store,dir);
        File.WriteAllText(Path.Combine(dir,"launcher-settings.json"),"broken");var broken=new InstanceSettingsView(dir,"fixture",store){};broken.Show();broken.GetType().GetMethod("Save",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(broken,null);Check(!broken.Saved && File.ReadAllText(Path.Combine(dir,"launcher-settings.json"))=="broken","instance: corrupt configuration not silently overwritten");broken.Close();
        File.WriteAllText(Path.Combine(_output,"result.txt"),$"PASS {_checks} instance settings checks");
    }
}
