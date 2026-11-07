using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CMUmcl;
using CMUmcl.Content;

internal static partial class Program
{
    private static async Task InstanceContentChecks(MainWindow window,InstalledStateStore installed,SettingsStore store)
    {
        SchematicPreviewSession.RootOverride=Path.Combine(LauncherPaths.RootDirectory,"components-test");
        string directory=Path.Combine(LauncherPaths.InstancesDirectory,"content-test");Directory.CreateDirectory(directory);
        var metadata=new InstanceMetadata{Id="local:content-test",Name="内容验收",Game="1.21.1",Loader="fabric"};File.WriteAllText(Path.Combine(directory,"instance.json"),JsonSerializer.Serialize(metadata));installed.RecordInstall(metadata.Id,1,"content-test",metadata.Name);
        var settings=store.Load();settings.VersionIsolation=VersionIsolation.All;store.Save(settings);
        var files=new InstanceContentFiles(directory);Directory.CreateDirectory(files.Folder("mods"));Directory.CreateDirectory(files.Folder("resourcepacks"));
        void Archive(string path,string entry,string content){using var zip=ZipFile.Open(path,ZipArchiveMode.Create);using var writer=new StreamWriter(zip.CreateEntry(entry).Open());writer.Write(content);}
        string mod=files.Folder("mods/alpha.jar");Archive(mod,"fabric.mod.json","{\"name\":\"Alpha 模组\",\"description\":\"中文描述\"}");
        File.WriteAllText(files.Folder("mods/broken.jar"),"broken archive");File.WriteAllText(files.Folder("mods/readme.txt"),"ignored");
        Check(files.List("mods",true).Count==2&&files.List("mods",true).Any(x=>x.Name=="Alpha 模组"&&x.Description=="中文描述"),"content: read mod metadata, retain malformed jar and ignore unrelated files");
        files.Toggle(mod);Check(File.Exists(mod+".disabled")&&files.List("mods",true).Single(x=>x.Name=="Alpha 模组").Disabled,"content: disable renames actual mod file");files.Toggle(mod+".disabled");Check(File.Exists(mod),"content: enable restores original file");
        File.WriteAllText(mod+".disabled","conflict");bool collision=false;try{files.Toggle(mod);}catch(IOException){collision=true;}Check(collision&&File.Exists(mod)&&File.ReadAllText(mod+".disabled")=="conflict","content: toggle collision preserves both files");File.Delete(mod+".disabled");
        bool escaped=false;try{files.Folder("../outside");}catch(IOException){escaped=true;}Check(escaped,"content: traversal rejected");
        bool wrong=false;try{files.Remove(Path.Combine(directory,"instance.json"));}catch(IOException){wrong=true;}Check(wrong&&File.Exists(Path.Combine(directory,"instance.json")),"content: unrelated instance files cannot be removed");
        string pack=files.Folder("resourcepacks/pack.zip");Archive(pack,"pack.mcmeta","{\"pack\":{\"description\":\"资源包描述\",\"pack_format\":34}}");
        string unpacked=files.Folder("resourcepacks/文件夹资源包");Directory.CreateDirectory(unpacked);File.WriteAllText(Path.Combine(unpacked,"pack.mcmeta"),"{\"pack\":{\"description\":\"文件夹资源包\"}}");
        byte[] cover;var coverBitmap=System.Windows.Media.Imaging.BitmapSource.Create(2,2,96,96,PixelFormats.Bgra32,null,new byte[]{40,170,70,255,20,150,60,255,20,150,60,255,40,170,70,255},8);var coverEncoder=new System.Windows.Media.Imaging.PngBitmapEncoder();coverEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(coverBitmap));using(var memory=new MemoryStream()){coverEncoder.Save(memory);cover=memory.ToArray();}
        using(var zip=ZipFile.Open(pack,ZipArchiveMode.Update)){using var output=zip.CreateEntry("pack.png").Open();output.Write(cover,0,cover.Length);}File.WriteAllBytes(Path.Combine(unpacked,"pack.png"),cover);
        Check(files.List("resourcepacks",false).All(x=>x.Icon!=null&&x.Icon.IsFrozen&&x.Icon.PixelWidth==2),"content: ZIP and folder pack.png loaded into frozen covers");
        string bad=Path.Combine(_output,"bad-cover.zip");using(var zip=ZipFile.Open(bad,ZipArchiveMode.Create)){using var output=new StreamWriter(zip.CreateEntry("pack.png").Open());output.Write("broken image");}Check(InstanceContentFiles.ReadPackIcon(bad)==null,"content: corrupt pack cover keeps default icon");
        string noCover=Path.Combine(_output,"no-cover.zip");using(var zip=ZipFile.Open(noCover,ZipArchiveMode.Create)){zip.CreateEntry("nested/pack.png");}Check(InstanceContentFiles.ReadPackIcon(noCover)==null,"content: missing root pack.png keeps default icon");
        string huge=Path.Combine(_output,"oversize-cover.zip");using(var zip=ZipFile.Open(huge,ZipArchiveMode.Create)){using var output=zip.CreateEntry("pack.png").Open();var large=new byte[2*1024*1024+1];output.Write(large,0,large.Length);}Check(InstanceContentFiles.ReadPackIcon(huge)==null,"content: oversized pack cover rejected before decode");
        files.Toggle(pack);Check(files.List("resourcepacks",false).Single(x=>x.Path==pack+".disabled").Icon!=null,"content: disabled ZIP keeps embedded cover");files.Toggle(pack+".disabled");
        Check(files.List("resourcepacks",false).Count==2,"content: zipped and unpacked resource packs listed");files.Toggle(unpacked);Check(!Directory.Exists(unpacked)&&files.List("resourcepacks",false).Single(x=>x.Name=="文件夹资源包").Disabled,"content: unpacked pack disabled outside game-scanned resourcepacks");files.Toggle(files.Folder(".cmumcl-disabled-resourcepacks/文件夹资源包"));Check(Directory.Exists(unpacked),"content: unpacked pack re-enabled");
        string trash=files.Remove(pack);Check(!File.Exists(pack)&&File.Exists(trash),"content: delete keeps exact recoverable file");files.Install("resourcepacks",trash,false);Check(File.Exists(pack),"content: local zip install works");
        bool duplicate=false;try{files.Install("resourcepacks",trash,false);}catch(IOException){duplicate=true;}Check(duplicate,"content: install refuses overwrite");
        string sync=files.Folder("schematics/sync/sub");Directory.CreateDirectory(sync);File.WriteAllText(Path.Combine(sync,"nested.litematic"),"fixture");File.WriteAllText(files.Folder("schematics/root.litematic"),"fixture");File.WriteAllText(files.Folder("schematics/hidden.txt"),"ignore");
        Click(window,"InstancesNavButton");window.UpdateLayout();var card=Get<ItemsControl>(window,"InstancesList").Items.OfType<InstanceCardViewModel>().Single(x=>x.InstanceId==metadata.Id);
        int count=Application.Current.Windows.Count;var host=Get<ContentControl>(window,"InstanceSettingsHost");
        InstanceContentView Open(){Call(window,"OpenInstanceContent",card);window.UpdateLayout();var opened=(InstanceContentView)host.Content;opened.LoadModIconOverride=_=>Task.FromResult<System.Windows.Media.Imaging.BitmapSource?>(null);opened.PreviewUpdatesFactory=()=>new SchematicPreviewUpdateClient(new System.Net.Http.HttpClient(new FixtureHandler(Array.Empty<byte>(),Array.Empty<byte>())),SchematicPreviewProtocol.PublicKey);return opened;}
        Button B(DependencyObject root,string text)=>Descendants(root).OfType<Button>().Single(x=>Equals(x.Content,text));
        void Capture(string name){window.UpdateLayout();var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using var output=File.Create(Path.Combine(_output,name+".png"));encoder.Save(output);}
        Call(window,"OpenInstanceDirButton_Click",new Button{DataContext=card},new RoutedEventArgs());window.UpdateLayout();
        var directories=(InstanceContentView)host.Content;
        Check(!Descendants(directories).OfType<ListBox>().Any(x=>x.IsVisible) && Descendants(directories).OfType<Button>().Count(x=>x.Tag is string)==8,"content: card directory action shows full-width folder cards without category navigation");
        Capture("directory-cards");
        B(directories,"← 返回").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(directories.Closed&&!host.IsVisible,"content: directory back returns to instances");
        var view=Open();Check(Window.GetWindow(view)==window&&Application.Current.Windows.Count==count,"content: resource manager embeds without another window");
        Check(Descendants(view).OfType<Button>().Count(x=>x.Content is string s&&s.EndsWith("文件夹"))==8,"content: eight folder shortcuts including logs and crash reports");
        var nav=Descendants(view).OfType<ListBox>().Single();nav.SelectedIndex=1;await Task.Delay(200);window.UpdateLayout();
        Check(Descendants(view).OfType<TextBlock>().Any(x=>x.Text=="Alpha 模组"),"content: mods load automatically on entry");
        Capture("mods");
        view.ModUpdatesFactory=()=>CreateModUpdateFixture(mod);
        B(view,"检查更新").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(()=>Descendants(view).OfType<TextBlock>().Any(x=>x.Text=="可更新：1.0.0 → 1.1.0"));window.UpdateLayout();
        Check(Descendants(view).OfType<Button>().Any(x=>Equals(x.Content,"全部更新（1）")&&x.IsVisible) && Descendants(view).OfType<Button>().Count(x=>Equals(x.Content,"更新"))==1,"content: mod update check marks the matching mod and offers updates");
        Capture("mod-updates");
        ((System.Collections.IDictionary)typeof(InstanceContentView).GetField("_modUpdates",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(view)!).Clear();
        Check(Descendants(view).OfType<Image>().All(x=>x.Visibility==Visibility.Collapsed),"content: unmatched mods retain default icon");
        var sample=System.Windows.Media.Imaging.BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[]{40,160,60,255},4);sample.Freeze();view.LoadModIconOverride=_=>Task.FromResult<System.Windows.Media.Imaging.BitmapSource?>(sample);B(view,"刷新").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(150);window.UpdateLayout();Check(Descendants(view).OfType<Image>().Count(x=>x.Visibility==Visibility.Visible&&ReferenceEquals(x.Source,sample))==2,"content: cached async icons populate aligned mod rows");Capture("mods-with-icons");
        foreach(double scale in new[]{1.0,1.25,1.5,2.0}){view.LayoutTransform=new ScaleTransform(scale,scale);window.UpdateLayout();foreach(var text in new[]{"刷新","启用/禁用所选","删除所选"}){var b=B(view,text);var rect=b.TransformToAncestor(view).TransformBounds(new Rect(b.RenderSize));Check(rect.Right<=view.ActualWidth+1&&rect.Bottom<=view.ActualHeight+1,"content: toolbar "+text+" accessible at "+scale);}Capture("mods-scale-"+scale);}view.LayoutTransform=Transform.Identity;window.UpdateLayout();
        var search=Descendants(view).OfType<TextBox>().Single();search.Text="中文描述";window.UpdateLayout();Check(Descendants(view).OfType<Button>().Count(x=>Equals(x.Content,"禁用"))==1,"content: filter by metadata description");
        B(view,"禁用").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(100);window.UpdateLayout();Check(File.Exists(mod+".disabled"),"content: row disable reaches filesystem");
        B(view,"启用").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(100);window.UpdateLayout();
        var select=Descendants(view).OfType<CheckBox>().Single(x=>Equals(x.Content,"全选"));select.IsChecked=true;select.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));window.UpdateLayout();B(view,"启用/禁用所选").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(100);window.UpdateLayout();Check(File.Exists(mod+".disabled")&&File.Exists(files.Folder("mods/broken.jar")),"content: select all changes only filtered mods");B(view,"启用").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(100);window.UpdateLayout();
        B(view,"删除").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>Get<Grid>(window,"InlinePromptOverlay").IsVisible);Click(window,"CommunityNavButton");await Task.Delay(50);
        Check(view.Closed&&!host.IsVisible&&File.Exists(mod),"content: navigating away cancels pending deletion");
        view=Open();nav=Descendants(view).OfType<ListBox>().Single();nav.SelectedIndex=1;await Task.Delay(100);window.UpdateLayout();search=Descendants(view).OfType<TextBox>().Single();search.Text="alpha";window.UpdateLayout();B(view,"删除").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>Get<Grid>(window,"InlinePromptOverlay").IsVisible);Click(window,"InlinePromptContinue");await Task.Delay(100);Check(!File.Exists(mod)&&Directory.GetFiles(files.Folder(".cmumcl-trash"),"alpha.jar",SearchOption.AllDirectories).Length==1,"content: inline confirmed deletion creates recoverable copy");
        nav.SelectedIndex=3;window.UpdateLayout();var expander=Descendants(view).OfType<Expander>().Single();Check(Equals(expander.Header,"sync")&&!expander.IsExpanded,"content: sync folder starts collapsed");expander.IsExpanded=true;window.UpdateLayout();var sub=Descendants(view).OfType<Expander>().Single(x=>Equals(x.Header,"sub"));sub.IsExpanded=true;window.UpdateLayout();
        Check(Descendants(view).OfType<TextBlock>().Any(x=>x.Text=="nested.litematic")&&!Descendants(view).OfType<TextBlock>().Any(x=>x.Text=="hidden.txt"),"content: nested schematic files visible, unrelated files ignored");
        Capture("schematics");
        var preview=Descendants(view).OfType<Button>().First(x=>Equals(x.Content,"快速预览"));preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(50);
        Check(Descendants(view).OfType<TextBlock>().Any(x=>x.Text=="可选原理图预览扩展")&&!Directory.Exists(SchematicPreviewSession.ComponentRoot),"content: uninstalled preview opens optional extension page without installation");Capture("preview-extension");
        B(view,"← 返回原理图").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();
        var schematicSearch=Descendants(view).OfType<TextBox>().Single();schematicSearch.Text="nested";await Task.Delay(100);window.UpdateLayout();Check(Descendants(view).OfType<TextBlock>().Any(x=>x.Text=="nested.litematic")&&!Descendants(view).OfType<TextBlock>().Any(x=>x.Text=="root.litematic"),"content: schematic filename search finds nested files without unfolding");Capture("schematics-search");schematicSearch.Text="nothing-matches";await Task.Delay(100);window.UpdateLayout();Check(Descendants(view).OfType<TextBlock>().Any(x=>x.Text=="没有匹配的原理图。"),"content: schematic search empty state");schematicSearch.Text="";window.UpdateLayout();
        foreach(var theme in new[]{LauncherTheme.Light,LauncherTheme.Dark}){settings.Theme=theme;await CMUmcl.Appearance.AppearanceManager.ApplyAsync(settings);window.UpdateLayout();Capture("schematics-"+theme);}settings.Theme=LauncherTheme.Light;await CMUmcl.Appearance.AppearanceManager.ApplyAsync(settings);
        foreach(double scale in new[]{1.0,1.25,1.5,2.0}){view.LayoutTransform=new ScaleTransform(scale,scale);window.UpdateLayout();var back=B(view,"← 返回");var rect=back.TransformToAncestor(view).TransformBounds(new Rect(back.RenderSize));Check(rect.Right<=view.ActualWidth+1&&rect.Bottom<=view.ActualHeight+1,"content: back remains inside embedded layout at scale "+scale);Capture("schematics-scale-"+scale);}view.LayoutTransform=Transform.Identity;
        nav.SelectedIndex=1;await Task.Delay(100);window.UpdateLayout();
        foreach(var theme in new[]{LauncherTheme.Light,LauncherTheme.Dark}){settings.Theme=theme;await CMUmcl.Appearance.AppearanceManager.ApplyAsync(settings);CMUmcl.Appearance.DpiWindowBehavior.Fit(window,960,500);window.Width=960;window.Height=500;window.UpdateLayout();await Task.Delay(30);Check(Descendants(view).OfType<ScrollViewer>().Any(x=>x.ViewportHeight>=48),"content: resource list retains visible viewport in compact "+theme);Capture("compact-mods-"+theme);}
        settings.Theme=LauncherTheme.Light;await CMUmcl.Appearance.AppearanceManager.ApplyAsync(settings);CMUmcl.Appearance.DpiWindowBehavior.Fit(window,1920,1000);window.Width=1200;window.Height=800;window.UpdateLayout();
        nav.SelectedIndex=2;await Task.Delay(150);window.UpdateLayout();Check(Descendants(view).OfType<Image>().Count(x=>x.Visibility==Visibility.Visible&&x.Source!=null)==2,"content: resource pack rows display embedded covers");Capture("resource-pack-covers");
        nav.SelectedIndex=3;await Task.Delay(100);window.UpdateLayout();
        Directory.CreateDirectory(SchematicPreviewSession.ComponentRoot);File.WriteAllText(Path.Combine(SchematicPreviewSession.ComponentRoot,"current.json"),"{\"version\":\"0.2.3\"}");
        string materialA=Path.Combine(_output,"原版增强示例.zip"),materialB=Path.Combine(_output,"玻璃透明示例.zip");
        Archive(materialA,"pack.mcmeta","{\"pack\":{\"pack_format\":71,\"description\":\"A\"}}");Archive(materialB,"pack.mcmeta","{\"pack\":{\"pack_format\":71,\"description\":\"B\"}}");
        SchematicPreviewMaterials.Import(new[]{materialA,materialB});SchematicPreviewMaterials.Change(SchematicPreviewSession.Hash(materialB),enabled:false);
        B(view,"预览材质").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();
        Check(B(view,"导入资源包").IsEnabled,"content: material manager enables imports with supported component");
        Check(Descendants(view).OfType<TextBlock>().Any(t=>t.Text.Contains("内置基础材质")),"content: material manager displays bundled fallback");
        Check(Descendants(view).OfType<CheckBox>().Count()==2,"content: material manager displays enabled and disabled imported packs");
        foreach(var theme in new[]{LauncherTheme.Light,LauncherTheme.Dark}){settings.Theme=theme;await CMUmcl.Appearance.AppearanceManager.ApplyAsync(settings);window.UpdateLayout();Capture("preview-materials-"+theme);}
        B(view,"← 返回原理图").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();
        B(view,"← 返回").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(view.Closed&&!host.IsVisible,"content: back returns to instance list");
        // Guard tested with the actual button path, without starting a game.
        bool busy=true;var guarded=new InstanceContentView(directory,directory,"占用测试",()=>busy,(_,_)=>Task.FromResult(true));guarded.LoadModIconOverride=_=>Task.FromResult<System.Windows.Media.Imaging.BitmapSource?>(null);host.Content=guarded;host.Visibility=Visibility.Visible;window.UpdateLayout();nav=Descendants(guarded).OfType<ListBox>().Single();nav.SelectedIndex=2;await Task.Delay(100);window.UpdateLayout();
        var disable=Descendants(guarded).OfType<Button>().First(x=>Equals(x.Content,"禁用"));disable.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(File.Exists(pack)&&Directory.Exists(unpacked),"content: occupied instance blocks file mutation");busy=false;disable.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(100);window.UpdateLayout();Check(File.Exists(pack+".disabled"),"content: resource pack UI disable reaches filesystem");B(guarded,"启用").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(100);Check(File.Exists(pack),"content: resource pack UI enable restores file");guarded.Close();host.Content=null;host.Visibility=Visibility.Collapsed;
        File.WriteAllText(Path.Combine(_output,"result.txt"),$"PASS {_checks} instance content checks");
    }
}
