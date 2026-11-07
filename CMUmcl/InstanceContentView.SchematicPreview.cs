// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CMUmcl.Content;
using Microsoft.Win32;

namespace CMUmcl;

internal sealed partial class InstanceContentView
{
    private SchematicPreviewSession? _schematicPreview;
    private CancellationTokenSource? _previewCancellation;
    private string? _previewPack;
    private SchematicPreviewHost? _previewViewport;
    private CancellationTokenSource? _componentCancellation;
    internal Func<SchematicPreviewUpdateClient>? PreviewUpdatesFactory { get; set; }
    private void StopSchematicPreview()
    {
        _componentCancellation?.Cancel(); _componentCancellation = null;
        _previewCancellation?.Cancel(); _previewCancellation = null;
        _schematicPreview?.Dispose(); _schematicPreview = null;
        var viewport = _previewViewport; _previewViewport = null; viewport?.Dispose();
    }

    private void ShowSchematicExtension()
    {
        if(Closed)return; StopSchematicPreview(); int generation=++_generation;
        var lifetime=new CancellationTokenSource(); _componentCancellation=lifetime;
        var panel=new StackPanel();_body.Content=new ScrollViewer {Content=InstancePageUi.Card(panel),VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        panel.Children.Add(Label("可选原理图预览扩展",20));
        panel.Children.Add(Label("安装扩展后可预览原理图。",14));
        var details=Label("",14);panel.Children.Add(details);

        var buttons=new WrapPanel();panel.Children.Add(buttons);
        SchematicPreviewOffer? offer=null;bool busy=false;
        Button check=null!,install=null!;
        check=Button("检查在线版本",async()=>await Check());buttons.Children.Add(check);
        install=Button("安装扩展",async()=>await Install());install.IsEnabled=false;buttons.Children.Add(install);
        var cancel=Button("取消",()=>lifetime.Cancel());buttons.Children.Add(cancel);
        buttons.Children.Add(Button("从本地安装",InstallSchematicPreview));
        buttons.Children.Add(Button("← 返回原理图",()=>{StopSchematicPreview();_generation++;ShowSchematics();_status.Text="";}));
        bool Current()=>!Closed && _generation==generation;
        void State() {
            string? version=null;try{version=SchematicPreviewSession.InstalledVersion();}catch(Exception ex){install.IsEnabled=false;details.Text="扩展状态需要修复："+ex.Message;_status.Text="扩展目录不可用，暂时无法安装。";return;}
            details.Text=version==null?"尚未安装预览扩展":"当前扩展版本："+version;
            if(offer!=null) {
                details.Text+=$"\n在线版本：{offer.Release.Version} · {offer.Release.Size/1048576.0:F2} MiB\n{offer.Release.Notes}";
                bool newer=version==null || CMUmcl.Update.UpdateProtocol.ParseVersion(offer.Release.Version)>CMUmcl.Update.UpdateProtocol.ParseVersion(version);
                install.Content=version==null?"安装扩展":"更新扩展"; install.IsEnabled=newer && !busy && !lifetime.IsCancellationRequested;
                if(!newer)details.Text+="\n已是最新版本。";
            }
        }
        async Task Check() {
            if(busy||lifetime.IsCancellationRequested)return;busy=true;check.IsEnabled=false;install.IsEnabled=false;_status.Text="正在检查更新…";
            try{using var client=PreviewUpdatesFactory?.Invoke()??new SchematicPreviewUpdateClient();var result=await client.CheckAsync(lifetime.Token);if(Current()){offer=result;_status.Text="版本信息已更新。";}}
            catch(OperationCanceledException){if(Current())_status.Text="已取消扩展操作。";}
            catch(Exception ex){if(Current())_status.Text="检查扩展失败："+ex.Message;}
            finally{busy=false;if(Current()){check.IsEnabled=!lifetime.IsCancellationRequested;State();}}
        }
        async Task Install() {
            if(busy||offer==null||lifetime.IsCancellationRequested)return;
            var selected=offer;
            busy=true;check.IsEnabled=false;install.IsEnabled=false;
            try{
                if(!await _confirm("安装可选预览扩展",$"即将从 csumcl.mcapcs.cn 下载扩展 {selected.Release.Version}，大小 {selected.Release.Size/1048576.0:F2} MiB。确认继续？")||!Current()||lifetime.IsCancellationRequested)return;
                using var client=PreviewUpdatesFactory?.Invoke()??new SchematicPreviewUpdateClient();await client.InstallAsync(selected,lifetime.Token,new Progress<string>(message=>{if(Current())_status.Text=message;}));if(Current())_status.Text="扩展已安装。";}
            catch(OperationCanceledException){if(Current())_status.Text="已取消，原有扩展保持可用。";}
            catch(Exception ex){if(Current())_status.Text="扩展安装失败，原有版本已保留："+ex.Message;}
            finally{busy=false;if(Current()){check.IsEnabled=!lifetime.IsCancellationRequested;State();}}
        }
        State();_ = Check();
    }

    private async void InstallSchematicPreview()
    {
        int generation = _generation;
        var dialog = new OpenFileDialog { Title = "选择本地构建扩展的 component.json", Filter = "预览扩展清单 (component.json)|component.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (!await _confirm("安装本地预览扩展", "本地扩展未经发布者签名验证，请确认来源可信后安装。")) return;
        try
        {
            StopSchematicPreview();
            await Task.Run(() => SchematicPreviewSession.InstallLocal(dialog.FileName));
            if (!Closed && generation == _generation) _status.Text = "本地扩展已安装。";
        }
        catch (Exception ex) { if (!Closed && generation == _generation) _status.Text = "扩展安装失败：" + ex.Message; }
    }

    private async void OpenSchematicPreview(string path)
    {
        if (Closed) return;
        try { SchematicPreviewSession.ResolveInstalled(); path = _files.Folder(path); }
        catch (Exception ex) { ShowSchematicExtension(); _status.Text = ex.Message; return; }
        bool stack=SchematicPreviewMaterials.SupportsStack;
        if (!stack && (_previewPack == null || !File.Exists(_previewPack)))
        {
            var dialog = new OpenFileDialog { Title = "选择本地 Minecraft Java 完整资源包（扩展不附带材质）", Filter = "Minecraft Java 资源包 (*.zip)|*.zip" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            _previewPack = dialog.FileName;
        }
        StopSchematicPreview();
        int generation = ++_generation;
        var cancellation = new CancellationTokenSource(); _previewCancellation = cancellation;
        var panel = new Grid(); panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _body.Content = panel;
        panel.Children.Add(Label(Path.GetFileName(path), 18));
        var actions = new WrapPanel(); Grid.SetRow(actions, 1); panel.Children.Add(actions);
        actions.Children.Add(Button("← 返回原理图", () => { StopSchematicPreview(); _generation++; ShowSchematics(); _status.Text = ""; }));
        actions.Children.Add(stack?Button("材质包",()=>ShowSchematicMaterials(path)):Button("更换资源包", () => { _previewPack = null; OpenSchematicPreview(path); }));
        var camera = new WrapPanel { IsEnabled = false }; actions.Children.Add(camera);
        var viewport = new SchematicPreviewHost { Margin = new Thickness(4) }; _previewViewport = viewport;
        Grid.SetRow(viewport, 2); panel.Children.Add(viewport);
        viewport.SurfaceDestroyed += () => {
            cancellation.Cancel();
            if (ReferenceEquals(_previewViewport, viewport)) { _schematicPreview?.Dispose(); _schematicPreview = null; }
        };
        async Task Send(object command)
        {
            var session = _schematicPreview;
            if (session == null || cancellation.IsCancellationRequested) return;
            try { await session.CommandAsync(command, cancellation.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!Closed && generation == _generation) { camera.IsEnabled = false; _status.Text = "预览已停止：" + ex.Message; StopSchematicPreview(); } }
        }
        bool resizing = false, resizeAgain = false;
        async void Resize()
        {
            resizeAgain = true;
            if (resizing || _schematicPreview == null || cancellation.IsCancellationRequested) return;
            resizing = true;
            try { while (resizeAgain && !cancellation.IsCancellationRequested) { resizeAgain = false; await Send(new { command = "resize" }); } }
            finally { resizing = false; }
        }
        viewport.BoundsChanged += Resize;
        camera.Children.Add(Button("左转", async () => await Send(new { command = "orbit", x = -90, y = 0 })));
        camera.Children.Add(Button("右转", async () => await Send(new { command = "orbit", x = 90, y = 0 })));
        camera.Children.Add(Button("放大", async () => await Send(new { command = "zoom", factor = 0.8 })));
        camera.Children.Add(Button("缩小", async () => await Send(new { command = "zoom", factor = 1.25 })));
        camera.Children.Add(Button("重置视角", async () => await Send(new { command = "fit" })));
        _status.Text = "正在加载预览…";
        try
        {
            IntPtr parent = await viewport.WaitForHandleAsync(cancellation.Token);
            var packs=stack?await Task.Run(()=>SchematicPreviewMaterials.Enabled(),cancellation.Token):null;
            var session = await SchematicPreviewSession.OpenAsync(path, stack?"":_previewPack!, parent, cancellation.Token,packs:packs);
            if (Closed || generation != _generation || cancellation.IsCancellationRequested) { session.Dispose(); return; }
            _schematicPreview = session;
            session.Failed += message => Dispatcher.BeginInvoke(new Action(() => {
                if (!Closed && generation == _generation && !cancellation.IsCancellationRequested) {
                    camera.IsEnabled = false; _status.Text = "预览已停止：" + message; StopSchematicPreview();
                }
            }));
            camera.IsEnabled = true; Resize();
            _status.Text = session.Summary + (stack?$" · 基础材质 + {packs!.Count} 个自定义包":"") + " · 拖动旋转 · Shift 拖动平移 · 滚轮缩放";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!Closed && generation == _generation) { _status.Text = "预览失败：" + ex.Message; StopSchematicPreview(); } }
    }
}
