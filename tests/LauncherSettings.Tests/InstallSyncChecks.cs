// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using CMUmcl;
using CMUmcl.Content;
using CMUmcl.Launch;

internal static partial class Program
{
    private static async Task InstallSyncChecks(MainWindow owner,SettingsStore store,LaunchService launch)
    {
        string a=Path.Combine(LauncherPaths.InstancesDirectory,"sync-A"),b=Path.Combine(LauncherPaths.InstancesDirectory,"sync-B");Directory.CreateDirectory(a);Directory.CreateDirectory(b);
        foreach(string dir in new[]{a,b})File.WriteAllText(Path.Combine(dir,"instance.json"),JsonSerializer.Serialize(new InstanceMetadata{Id="local:"+Path.GetFileName(dir),Name="fixture",Game="1.20.1",Loader="fabric",LoaderVersion="fixture"}));
        launch.MaintenanceValidateOverride=(_,_,_,_)=>Task.CompletedTask;
        launch.MaintenanceProvisionOverride=async (directory,meta,progress,ct)=>{await Task.Delay(20,ct);Check(InstanceMaintenance.Read(directory).Game=="1.20.1","maintenance: old metadata visible during new provisioning");return "fixture-ready";};
        await launch.MaintainInstanceAsync(a,"1.21.1","fabric","new-fixture",false,null,CancellationToken.None);
        Check(InstanceMaintenance.Read(a).Game=="1.21.1" && InstanceMaintenance.Read(a).LoaderVersion=="new-fixture","maintenance: successful preparation commits target version");
        Check(Directory.GetFiles(Path.Combine(a,".cmumcl-maintenance-backups"),"instance.json",SearchOption.AllDirectories).Length==1,"maintenance: original metadata backed up");
        string before=File.ReadAllText(Path.Combine(a,"instance.json"));
        launch.MaintenanceProvisionOverride=(_,_,_,_)=>throw new IOException("fixture failure");bool failed=false;try{await launch.MaintainInstanceAsync(a,"1.22","vanilla","",false,null,CancellationToken.None);}catch(IOException){failed=true;}Check(failed && File.ReadAllText(Path.Combine(a,"instance.json"))==before,"maintenance: failed preparation leaves metadata intact");
        launch.MaintenanceProvisionOverride=async (_,_,_,ct)=>{await Task.Delay(50,ct);return "no-commit";};using(var cancel=new CancellationTokenSource()){cancel.CancelAfter(10);try{await launch.MaintainInstanceAsync(a,"1.22","vanilla","",false,null,cancel.Token);}catch(OperationCanceledException){ }Check(File.ReadAllText(Path.Combine(a,"instance.json"))==before,"maintenance: cancelled preparation does not switch version");}
        launch.MaintenanceProvisionOverride=(_,_,_,_)=>Task.FromResult("repaired");await launch.MaintainInstanceAsync(a,"1.21.1","fabric","new-fixture",true,null,CancellationToken.None);Check(File.ReadAllText(Path.Combine(a,"instance.json"))==before,"maintenance: repair preserves metadata");
        string root=LauncherPaths.MinecraftDirectory;string vd=Path.Combine(root,"versions","repair-fixture");Directory.CreateDirectory(vd);
        byte[] good=System.Text.Encoding.UTF8.GetBytes("verified fixture jar");string hash=BitConverter.ToString(SHA1.Create().ComputeHash(good)).Replace("-","").ToLowerInvariant();
        File.WriteAllText(Path.Combine(vd,"repair-fixture.json"),JsonSerializer.Serialize(new{downloads=new{client=new{size=good.Length,sha1=hash}}}));File.WriteAllBytes(Path.Combine(vd,"repair-fixture.jar"),Enumerable.Repeat((byte)42,good.Length).ToArray());
        Check(InstanceMaintenance.QuarantineCorruptDependencies("repair-fixture")==1 && !File.Exists(Path.Combine(vd,"repair-fixture.jar")),"repair: same-size corrupt dependency hash detected and quarantined");
        File.WriteAllBytes(Path.Combine(vd,"repair-fixture.jar"),good);Check(InstanceMaintenance.QuarantineCorruptDependencies("repair-fixture")==0 && File.Exists(Path.Combine(vd,"repair-fixture.jar")),"repair: valid dependency retained");
        File.WriteAllText(Path.Combine(vd,"repair-fixture.json"),"invalid json");Check(InstanceMaintenance.QuarantineCorruptDependencies("repair-fixture")==1 && !File.Exists(Path.Combine(vd,"repair-fixture.json")),"repair: corrupt version metadata quarantined for re-fetch");
        using(var process=System.Diagnostics.Process.GetCurrentProcess()){InstanceMaintenance.RecordRunning(a,process);bool running=false;try{InstanceMaintenance.EnsureStopped(a);}catch(IOException){running=true;}Check(running,"maintenance: running process blocks edits and repair");File.Delete(Path.Combine(a,".cmumcl-running.json"));}
        string[] names=InstanceSharing.Names;
        foreach(var name in names){if(name=="resourcepacks"){Directory.CreateDirectory(Path.Combine(a,name));File.WriteAllText(Path.Combine(a,name,"pack.zip"),"pack-A");}else File.WriteAllText(Path.Combine(a,name),"A-"+name);}
        File.WriteAllText(Path.Combine(b,"servers.dat"),"B-original");
        using(var session=InstanceSharing.Begin(a,names))
        {
            bool conflict=false;try{using var other=InstanceSharing.Begin(b,names);}catch(IOException){conflict=true;}Check(conflict,"sync: concurrent shared game rejected");
            File.WriteAllText(Path.Combine(a,"options.txt"),"changed-in-game-A");
        }
        using(var session=InstanceSharing.Begin(b,names))
        {
            Check(File.ReadAllText(Path.Combine(b,"options.txt"))=="changed-in-game-A","sync: A game changes visible in B before launch");
            Check(File.ReadAllText(Path.Combine(b,"resourcepacks","pack.zip"))=="pack-A","sync: resource pack directory shared");
            Check(names.Where(x=>x!="resourcepacks").All(x=>File.Exists(Path.Combine(b,x))),"sync: all four settings files shared");
            File.WriteAllText(Path.Combine(b,"servers.dat"),"changed-in-game-B");File.Delete(Path.Combine(b,"hotbar.nbt"));
        }
        using(var session=InstanceSharing.Begin(a,names)){Check(File.ReadAllText(Path.Combine(a,"servers.dat"))=="changed-in-game-B","sync: B edits flow back to A");Check(!File.Exists(Path.Combine(a,"hotbar.nbt")),"sync: deleted settings propagate with backups");}
        string shared=Path.Combine(LauncherPaths.RootDirectory,"shared-instance-settings");Check(Directory.GetFiles(Path.Combine(shared,"backups"),"servers.dat",SearchOption.AllDirectories).Any(p=>File.ReadAllText(p)=="B-original"),"sync: overwritten local original retained in backup");
        Check(InstanceSharing.Begin(a,Array.Empty<string>())==null,"sync: disabled instance does not touch shared data");
        File.WriteAllText(Path.Combine(a,"options.txt"),"recovered-after-launcher-exit");File.WriteAllText(Path.Combine(shared,"pending.json"),JsonSerializer.Serialize(new InstanceSharing.Journal{Directory=a,Names=new[]{"options.txt"},Pid=0}));
        using(var recovered=InstanceSharing.Begin(b,new[]{"options.txt"})){Check(File.ReadAllText(Path.Combine(b,"options.txt"))=="recovered-after-launcher-exit","sync: durable journal recovers closed launcher game edits");}
        bool invalid=false;try{using var ignored=InstanceSharing.Begin(a,new[]{"../auth.dat"});}catch(IOException){invalid=true;}Check(invalid,"sync: path traversal category rejected");
        var settings=new InstanceOptions{SharedSettings=new[]{"options.txt","servers.dat"}};InstanceOptionsStore.Save(a,settings);Check(InstanceOptionsStore.Load(a).SharedSettings.SequenceEqual(settings.SharedSettings),"sync: per-instance selection persists");
        foreach(bool dark in new[]{false,true})
        {
            if(CMUmcl.Appearance.AppearanceManager.IsDark!=dark)Click(owner,"ThemeToggleButton");
            var dialog=new InstanceSettingsView(a,"maintenance fixture",store,launch:launch){Width=640,Height=440};dialog.Show();await Task.Delay(40);
            var nav=Descendants(dialog).OfType<ListBox>().First();Check(nav.Items.Count==7,"maintenance UI: components, content and sync categories present");
            foreach(int tab in new[]{1,5}){nav.SelectedIndex=tab;dialog.UpdateLayout();await Task.Delay(30);var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)dialog.ActualWidth,(int)dialog.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(dialog);var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using(var output=File.Create(Path.Combine(_output,$"install-sync-{dark}-{tab}.png")))png.Save(output);foreach(double scale in new[]{1d,1.25d,1.5d,2d}){var content=(FrameworkElement)dialog.Content;content.LayoutTransform=new System.Windows.Media.ScaleTransform(scale,scale);dialog.UpdateLayout();var save=Descendants(dialog).OfType<Button>().First(x=>Equals(x.Content,"← 返回"));var bounds=save.TransformToAncestor(content).TransformBounds(new Rect(save.RenderSize));Check(bounds.Right<=content.ActualWidth+1 && bounds.Bottom<=content.ActualHeight+1,"maintenance UI: back action visible at scale "+scale);}((FrameworkElement)dialog.Content).LayoutTransform=System.Windows.Media.Transform.Identity;}
            dialog.Close();
        }
        launch.MaintenanceValidateOverride=null;launch.MaintenanceProvisionOverride=null;
        File.WriteAllText(Path.Combine(_output,"result.txt"),$"PASS {_checks} installation/sync checks");
    }
}
