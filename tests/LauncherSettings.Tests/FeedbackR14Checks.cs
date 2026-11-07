using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CMUmcl;
using CMUmcl.Auth;
using CMUmcl.Community;
using CMUmcl.Content;
using CMUmcl.Launch;

internal static partial class Program
{
    private sealed class GeneratedIconHandler(byte[] bytes):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});}
    private static Process StartProcessFixture()=>Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"ping.exe"),"-n 60 127.0.0.1"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true})!;
    private static T EditorField<T>(InstanceSettingsView view,string name)=>(T)typeof(InstanceSettingsView).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
    private static void EditorBack(InstanceSettingsView view)=>Descendants(view).OfType<Button>().Single(x=>Equals(x.Content,"← 返回")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task FeedbackR14Checks(MainWindow window,InstalledStateStore installed,SettingsStore store,LaunchService launch,string source)
    {
        int windows=Application.Current.Windows.Count;
        string directory=Path.Combine(LauncherPaths.InstancesDirectory,"embedded-B");Directory.CreateDirectory(directory);
        var metadata=new InstanceMetadata{Id="local:embedded-B",Name="实例 B",Game="26.3",Loader="fabric",LoaderVersion="0.20.1"};
        File.WriteAllText(Path.Combine(directory,"instance.json"),JsonSerializer.Serialize(metadata));installed.RecordInstall(metadata.Id,1,"embedded-B",metadata.Name);
        Click(window,"InstancesNavButton");window.UpdateLayout();
        var cards=Get<ItemsControl>(window,"InstancesList");var card=cards.Items.OfType<InstanceCardViewModel>().Single(x=>x.InstanceId==metadata.Id);
        var host=Get<ContentControl>(window,"InstanceSettingsHost");
        InstanceSettingsView Open(){Call(window,"InstanceSettings_Click",new Button{DataContext=card},new RoutedEventArgs());window.UpdateLayout();return (InstanceSettingsView)host.Content;}
        var create=Get<CreateInstanceView>(window,"CreateInstanceView");create.Open(new LocalInstanceService(installed),launch);
        SetPrivate(create,"_selectedGameVersion",new GameVersionRow(new MinecraftVersionInfo("1.21.1","release",DateTimeOffset.UtcNow,MinecraftVersionCategory.Release)));Get<TextBox>(create,"NameBox").Text="下载 A";
        var download=new TaskCompletionSource<bool>();create.ProvisionOverride=async (_,_,ct)=>{await download.Task;ct.ThrowIfCancellationRequested();};var installing=CallAsync(create,"InstallAsync");await Until(()=>create.HasActiveOperation);
        Click(window,"InstancesNavButton");var editor=Open();
        Check(Application.Current.Windows.Count==windows && Window.GetWindow(editor)==window && editor.IsVisible,"embedded: settings is a UserControl in main window with no extra window");
        EditorField<TextBox>(editor,"_name").Text="下载时保存 B";await Until(()=>InstanceOptionsStore.Load(directory).Name=="下载时保存 B",3000);
        Check(!editor.HasUnsavedChanges && EditorField<TextBlock>(editor,"_status").Text=="已自动保存。" && !installing.IsCompleted && Descendants(editor).OfType<Button>().All(x=>!Equals(x.Content,"保存")),"embedded: edits save automatically without a save button while A keeps downloading");
        EditorField<TextBox>(editor,"_name").Text="返回时保存";EditorBack(editor);await Until(()=>editor.IsClosed);
        Check(!Get<Grid>(window,"InlinePromptOverlay").IsVisible && InstanceOptionsStore.Load(directory).Name=="返回时保存","embedded: back saves a pending edit without asking");
        editor=Open();EditorField<TextBox>(editor,"_name").Text="切页保存";Click(window,"CommunityNavButton");
        Check(editor.IsClosed && host.Visibility==Visibility.Collapsed && Get<Grid>(window,"InlinePromptOverlay").Visibility==Visibility.Collapsed && InstanceOptionsStore.Load(directory).Name=="切页保存","embedded: navigating away saves pending edits and closes without a prompt");
        Check(Get<UserControl>(window,"CommunityResourceView").IsVisible && Get<ScrollViewer>(window,"InstancesScroll").Visibility==Visibility.Collapsed && Get<TextBlock>(window,"HeaderStatusText").Text.StartsWith("搜索"),"embedded: editor closure does not overlay instances or replace community header");
        Click(window,"InstancesNavButton");editor=Open();EditorField<CheckBox>(editor,"_sizeOverride").IsChecked=true;EditorField<TextBox>(editor,"_width").Text="invalid";EditorBack(editor);await Until(()=>Get<Grid>(window,"InlinePromptOverlay").IsVisible);Click(window,"PacksNavButton");await Task.Delay(20);
        Check(editor.IsClosed && !Get<Grid>(window,"InlinePromptOverlay").IsVisible && !InstanceOptionsStore.UsesOverride(InstanceOptionsStore.Load(directory),"windowSize"),"embedded: navigation cancels a pending discard confirmation without writing the invalid edit");
        download.SetResult(true);await installing;create.ProvisionOverride=null;create.Close();
        Click(window,"InstancesNavButton");editor=Open();EditorField<TextBox>(editor,"_name").Text="保留父页草稿";
        Descendants(editor).OfType<Button>().Single(x=>Equals(x.Content,"编辑全局默认")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var global=(InstanceSettingsView)host.Content;Check(global.IsGlobal && Window.GetWindow(global)==window && Application.Current.Windows.Count==windows,"embedded: global defaults also stay inside main window");
        EditorField<TextBox>(global,"_width").Text="1024";global.SaveAndClose();await Task.Delay(20);
        Check(ReferenceEquals(host.Content,editor) && EditorField<TextBox>(editor,"_name").Text=="保留父页草稿" && store.Load().WindowWidth==1024,"embedded: global save returns to parent without losing draft");
        Click(window,"InstancesNavButton");editor=Open();
        var pending=CallAsync(editor,"RunMaintenance",(Func<CancellationToken,Task>)(ct=>Task.Delay(Timeout.Infinite,ct)),"not finished");await Until(()=>editor.MaintenanceBusy);
        Check(!window.CanInstallPack(card.InstanceId),"embedded: maintenance blocks same-instance pack replacement");
        Click(window,"CommunityNavButton");Check(editor.IsClosed && !host.IsVisible,"embedded: navigating during maintenance immediately removes editor");await pending;
        Check(!editor.MaintenanceBusy && window.CanInstallPack(card.InstanceId),"embedded: closed editor cancels operation and releases maintenance guard");
        Click(window,"InstancesNavButton");editor=Open();EditorField<CheckBox>(editor,"_sizeOverride").IsChecked=true;EditorField<TextBox>(editor,"_width").Text="invalid";EditorBack(editor);await Until(()=>Get<Grid>(window,"InlinePromptOverlay").IsVisible);
        Check(!editor.IsClosed && EditorField<TextBlock>(editor,"_status").Text.Contains("窗口"),"embedded: invalid edit is reported and back asks before discarding it");
        Click(window,"InlinePromptCancel");await Task.Delay(20);Check(!editor.IsClosed && editor.HasUnsavedChanges,"embedded: cancelling keeps the invalid edit for correction");
        EditorBack(editor);await Until(()=>Get<Grid>(window,"InlinePromptOverlay").IsVisible);Click(window,"InlinePromptContinue");await Until(()=>editor.IsClosed);
        Check(!InstanceOptionsStore.UsesOverride(InstanceOptionsStore.Load(directory),"windowSize"),"embedded: discarded invalid edit is never written");Click(window,"InstancesNavButton");
        var calls=0;window._launchOverride=async (_,ct)=>{calls++;await Task.Delay(Timeout.Infinite,ct);throw new Exception("unreachable");};var starting=CallAsync(window,"LaunchInstanceAsync",card);await Until(()=>card.HasLaunchAction);
        await CallAsync(window,"LaunchInstanceAsync",card);Check(calls==1,"embedded: duplicate launch remains blocked");Call(window,"LaunchAction_Click",new Button{DataContext=card},new RoutedEventArgs());await starting;Check(card.ButtonEnabled && !card.HasLaunchAction,"embedded: cancel-start still restores launch button");
        using(var owned=StartProcessFixture())using(var unrelated=StartProcessFixture())
        {
            try
            {
                window._launchOverride=(_,_)=>Task.FromResult(new LaunchResult{Process=owned,Session=new AuthSession{SelectedProfileName="fixture"},WindowDetected=true});await CallAsync(window,"LaunchInstanceAsync",card);
                Call(window,"LaunchAction_Click",new Button{DataContext=card},new RoutedEventArgs());await Until(()=>Get<Grid>(window,"InlinePromptOverlay").IsVisible);
                var overlay=Get<Grid>(window,"InlinePromptOverlay");Check(Descendants(overlay).OfType<Button>().Count(x=>x.IsVisible)==2 && !Get<Button>(window,"InlinePromptDiscard").IsVisible && Application.Current.Windows.Count==windows,"embedded: stop confirmation has only continue/cancel and no extra window or back button");
                Click(window,"CommunityNavButton");await Task.Delay(20);Check(!overlay.IsVisible && !owned.HasExited,"embedded: changing page defaults stop confirmation to cancel");
                Click(window,"InstancesNavButton");Call(window,"LaunchAction_Click",new Button{DataContext=card},new RoutedEventArgs());await Until(()=>overlay.IsVisible);Click(window,"InlinePromptCancel");await Task.Delay(20);Check(!owned.HasExited,"embedded: explicit cancel preserves running game");
                Call(window,"LaunchAction_Click",new Button{DataContext=card},new RoutedEventArgs());await Until(()=>overlay.IsVisible);Click(window,"InlinePromptContinue");await Until(()=>owned.HasExited && card.ButtonEnabled);Check(!unrelated.HasExited && !card.HasLaunchAction,"embedded: confirmed stop affects only owned game and restores card");
            }
            finally{window._launchOverride=null;if(!owned.HasExited)owned.Kill();if(!unrelated.HasExited)unrelated.Kill();}
        }
        using(var process=StartProcessFixture())using(var cancellation=new CancellationTokenSource())
        {var wait=LaunchService.WaitForOwnedGameAsync(process,cancellation.Token);cancellation.Cancel();bool cancelled=false;try{await wait;}catch(OperationCanceledException){cancelled=true;}Check(cancelled && process.HasExited,"embedded: post-process cancellation still leaves no orphan");}
        foreach(var slug in new[]{"sodium","iris"}){using var input=File.OpenRead(Path.Combine(source,"tests/LauncherSettings.Tests/Fixtures",slug+".webp"));var icon=ResourceIconLoader.Decode(input);Check(icon!=null && icon.IsFrozen && icon.PixelWidth==96,"embedded: "+slug+" WebP regression");}
        using(var encoder=new Imazen.WebP.AnimEncoder(2,2))
        {
            encoder.AddFrame(Enumerable.Repeat(new byte[]{20,180,60,255},4).SelectMany(x=>x).ToArray(),0,-1);
            encoder.AddFrame(Enumerable.Repeat(new byte[]{180,20,60,255},4).SelectMany(x=>x).ToArray(),100,-1);
            using var input=new MemoryStream(encoder.Assemble());var decoded=ResourceIconLoader.Decode(input);Check(decoded!=null&&decoded.IsFrozen,"icon: generated animated WebP decodes first frame");
        }
        byte[] gif;var bitmap=System.Windows.Media.Imaging.BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[]{20,180,60,255},4);var gifEncoder=new System.Windows.Media.Imaging.GifBitmapEncoder();gifEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using(var output=new MemoryStream()){gifEncoder.Save(output);gif=output.ToArray();}Array.Resize(ref gif,3*1024*1024);
        using(var icons=new ResourceIconLoader(new GeneratedIconHandler(gif)))Check(await icons.LoadAsync("https://media.forgecdn.net/generated.gif",default)!=null,"icon: GIF response above legacy 2 MiB limit loads safely");
        editor=Open();var root=(FrameworkElement)editor.Content;
        foreach(double scale in new[]{1d,1.25d,1.5d,2d})
        {
            host.LayoutTransform=new ScaleTransform(scale,scale);window.UpdateLayout();await Task.Delay(20);
            var back=Descendants(editor).OfType<Button>().Single(x=>Equals(x.Content,"← 返回"));var bounds=back.TransformToAncestor(root).TransformBounds(new Rect(back.RenderSize));Check(bounds.Right<=root.ActualWidth+1 && bounds.Bottom<=root.ActualHeight+1 && back.ActualHeight>0,"embedded: back action remains in main content at scale "+scale);
        }
        host.LayoutTransform=Transform.Identity;window.UpdateLayout();Screenshot(window,"embedded-settings.png");Click(window,"InstancesNavButton");
        // Deleting an instance from its management page.
        string doomed=Path.Combine(LauncherPaths.InstancesDirectory,"embedded-delete");Directory.CreateDirectory(Path.Combine(doomed,"saves"));
        File.WriteAllText(Path.Combine(doomed,"instance.json"),JsonSerializer.Serialize(new InstanceMetadata{Id="local:embedded-delete",Name="待删除实例",Game="1.21.1",Loader="fabric",LoaderVersion="0.16.10"}));
        installed.RecordInstall("local:embedded-delete",1,"embedded-delete","待删除实例");
        string recycled=Path.Combine(_output,"recycled-instance");InstanceRemoval.RecycleOverride=path=>Directory.Move(path,recycled);
        try
        {
            Click(window,"InstancesNavButton");Call(window,"RefreshInstances");window.UpdateLayout();
            var doomedCard=Get<ItemsControl>(window,"InstancesList").Items.OfType<InstanceCardViewModel>().Single(x=>x.InstanceId=="local:embedded-delete");
            Call(window,"InstanceSettings_Click",new Button{DataContext=doomedCard},new RoutedEventArgs());window.UpdateLayout();
            var doomedEditor=(InstanceSettingsView)host.Content;
            Descendants(doomedEditor).OfType<ListBox>().First().SelectedIndex=6;window.UpdateLayout();
            var deleteButton=Descendants(doomedEditor).OfType<Button>().Single(x=>Equals(x.Content,"删除实例"));
            Check(deleteButton.IsVisible,"delete: instance management offers deletion");
            deleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>Get<Grid>(window,"InlinePromptOverlay").IsVisible);
            Check(Get<TextBlock>(window,"InlinePromptMessage").Text.Contains("回收站"),"delete: confirmation says the folder goes to the recycle bin");
            Click(window,"InlinePromptContinue");await Until(()=>doomedEditor.IsClosed);await Task.Delay(50);window.UpdateLayout();
            Check(!installed.ListRecords().ContainsKey("local:embedded-delete") && !Directory.Exists(doomed) && Directory.Exists(recycled)
                && !Get<ItemsControl>(window,"InstancesList").Items.OfType<InstanceCardViewModel>().Any(x=>x.InstanceId=="local:embedded-delete") && Get<ScrollViewer>(window,"InstancesScroll").IsVisible,
                "delete: instance leaves the list and its folder is recycled");
        }
        finally { InstanceRemoval.RecycleOverride=null; }
        File.WriteAllText(Path.Combine(_output,"result.txt"),$"PASS {_checks} embedded interaction checks");
    }
}
