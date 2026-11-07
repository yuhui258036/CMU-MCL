// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CMUmcl.Content;
using CMUmcl.Launch;

namespace CMUmcl
{
    internal sealed partial class InstanceSettingsView
    {
        internal Func<bool>? IsOperationBlocked {get;set;}
        internal Func<bool>? IsMaintenanceBlocked {get;set;}
        internal bool MaintenanceBusy=>_maintenanceBusy;
        private readonly LaunchService? _launch;
        private readonly ComboBox _gameVersion=new(){IsEditable=true},_loaderKind=new(){ItemsSource=new[]{"vanilla","fabric","forge","neoforge","quilt"}},_loaderVersion=new(){IsEditable=true};
        private readonly Dictionary<string,CheckBox> _syncChecks=new();
        private bool _maintenanceBusy;
        private CancellationTokenSource? _maintenanceCts;
        private void BuildMaintenancePages()
        {
            InstanceMetadata? metadata=null;try{metadata=InstanceMaintenance.Read(_directory);}catch{ }
            BuildComponentPage(metadata);
            var sync=InstancePageUi.Section(_pages[5],"共享设置");
            AddText(sync,"勾选的项目在游戏启动、退出时同步。共享设置的实例不能同时运行。");
            AddText(sync,"已有共享设置会覆盖本地文件，并保留备份。跨版本共享可能不兼容。");
            string[] selected;try{selected=InstanceOptionsStore.Load(_directory).SharedSettings;}catch{selected=Array.Empty<string>();}
            for(int i=0;i<InstanceSharing.Names.Length;i++){var box=new CheckBox{Content=InstanceSharing.Labels[i],IsChecked=selected.Contains(InstanceSharing.Names[i]),Margin=new Thickness(0,5,0,9)};_syncChecks.Add(InstanceSharing.Names[i],box);sync.Children.Add(box);}
            sync.Children.Add(Button("立即同步已勾选项目",async ()=>{
                if(!await Confirm("将使用共享数据替换对应本地文件，原文件保留备份。首次没有共享数据时以此实例初始化。继续？","同步设置") || IsClosed)return;
                _=RunMaintenance(async ct=>{InstanceMaintenance.EnsureStopped(_directory);var meta=InstanceMaintenance.Read(_directory);var gameDir=GameDirectoryResolver.ResolveGameDirectory(_store.Load(),_directory,meta.Loader);InstanceMaintenance.EnsureStopped(gameDir);var names=_syncChecks.Where(x=>x.Value.IsChecked==true).Select(x=>x.Key).ToArray();await Task.Run(()=>{ct.ThrowIfCancellationRequested();using var sharing=InstanceSharing.Begin(gameDir,names);},ct);},"同步完成。");
            }));
            AddText(sync,"备份位置：shared-instance-settings/backups");
        }
        private async void StartInstallation(bool repair)
        {
            if(_launch==null){_status.Text="当前预览未连接安装服务。";return;}
            if(!repair && !IsLocalInstance){_status.Text="服务器整合包的版本由服务器维护，不能在此修改。";return;}
            if(!await Confirm(repair?"将校验依赖并补齐缺失或损坏文件，不修改存档和模组。继续？":"请先备份存档。将准备目标版本及加载器，成功后切换，现有模组不会自动适配。继续？","实例安装") || IsClosed)return;
            string game=_gameVersion.Text.Trim(),loader=_loaderKind.SelectedItem as string??"vanilla",version=_loaderVersion.Text.Trim();
            if(repair){try{var meta=InstanceMaintenance.Read(_directory);game=meta.Game;loader=meta.Loader;version=meta.LoaderVersion;}catch{_status.Text="实例信息无效或无法读取，请先检查实例目录。";return;}}
            _=RunMaintenance(async ct=>{var progress=new Progress<LaunchProgress>(p=>{if(IsVisible)_status.Text=p.Stage+" · "+p.Message;});await _launch.MaintainInstanceAsync(_directory,game,loader,version,repair,progress,ct);Saved=true;},repair?"依赖修复完成":"版本已更新，重新进入此页查看。");
        }
        private Task RunMaintenance(Func<CancellationToken,Task> action,string success)
            =>RunMaintenanceWithMessage(async ct=>{await action(ct);return (string?)null;},success);
        /// <param name="action">May return its own completion message instead of <paramref name="success"/>.</param>
        private async Task RunMaintenanceWithMessage(Func<CancellationToken,Task<string?>> action,string success)
        {
            if(IsClosed || _maintenanceBusy)return;
            if(IsOperationBlocked?.Invoke()==true || IsMaintenanceBlocked?.Invoke()==true){_status.Text="实例正在使用，请结束任务后重试。";return;}
            _maintenanceBusy=true;_maintenanceCts=new();_status.Text="正在处理…";
            if(_cancelMaintenance!=null)_cancelMaintenance.Visibility=Visibility.Visible;
            try{_status.Text=await action(_maintenanceCts.Token)??success;}
            catch(OperationCanceledException){_status.Text="已取消。";}
            catch(Exception ex){_status.Text=ex is IOException || ex is LaunchException?ex.Message:"操作失败，原版本记录保留，请检查网络或重试。";}
            finally{_maintenanceBusy=false;_maintenanceCts.Dispose();_maintenanceCts=null;if(_cancelMaintenance!=null)_cancelMaintenance.Visibility=Visibility.Collapsed;if(HasUnsavedChanges)ScheduleSave();OperationFinished?.Invoke(this,EventArgs.Empty);}
        }
    }
}
