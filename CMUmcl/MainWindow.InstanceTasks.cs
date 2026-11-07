using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CMUmcl.Launch;
using CMUmcl.Content;

namespace CMUmcl;

public partial class MainWindow
{
    private readonly Dictionary<string, CancellationTokenSource> _launchRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Process> _gameProcesses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InstanceSettingsView> _settingsWindows = new(StringComparer.Ordinal);
    internal Func<InstanceCardViewModel, CancellationToken, Task<LaunchResult>>? _launchOverride;

    internal bool CanInstallPack(string id)
        => !_launchRequests.ContainsKey(id) && !_gameProcesses.ContainsKey(id)
        && !(_settingsWindows.TryGetValue(id,out var editor) && editor.MaintenanceBusy);

    private bool IsInstanceBusy(InstanceCardViewModel card)
        => _launchRequests.ContainsKey(card.InstanceId) || _gameProcesses.ContainsKey(card.InstanceId)
        || _cards.Any(x => x.InstanceId == card.InstanceId && x.IsProgressVisible);

    private InstanceSettingsView? _instanceEditor,_parentEditor;
    private InstanceContentView? _instanceContent;
    private void OpenInstanceContent(InstanceCardViewModel card)
        =>ShowInstanceContent(card,false);
    private void OpenInstanceDirectories(InstanceCardViewModel card)
        =>ShowInstanceContent(card,true);
    private void ShowInstanceContent(InstanceCardViewModel card,bool directoriesOnly)
    {
        CloseCreateInstanceView();CloseEmbeddedUiForNavigation();
        var metadata=InstanceMaintenance.Read(card.InstallPath);
        string gameDirectory=GameDirectoryResolver.ResolveGameDirectory(_settingsStore.Load(),card.InstallPath,metadata.Loader);
        _instanceContent=new InstanceContentView(card.InstallPath,gameDirectory,card.DisplayName,
            ()=>IsInstanceBusy(card) || !CanInstallPack(card.InstanceId)
                || (gameDirectory!=card.InstallPath && (_gameProcesses.Count!=0 || _launchRequests.Count!=0 || _activeOperations!=0 || CreateInstanceView.HasActiveOperation || _settingsWindows.Values.Any(x=>x.MaintenanceBusy))),
            async (title,message)=>await ShowInlinePromptAsync(title,message)==InlineChoice.Continue,directoriesOnly:directoriesOnly);
        _instanceContent.BackRequested+=ShowInstancesView;
        _currentPage=MainPage.InstanceContent;UpdateNavStyles();
        HeaderTitleText.Text=directoriesOnly?"实例目录":"实例内容";HeaderStatusText.Text=card.DisplayName;
        NewInstanceButton.Visibility=RefreshButton.Visibility=Visibility.Collapsed;
        CardsScroll.Visibility=EmptyStateText.Visibility=LoadingState.Visibility=ErrorState.Visibility=InstancesScroll.Visibility=InstancesEmptyStateText.Visibility=SettingsScroll.Visibility=CommunityResourceView.Visibility=Visibility.Collapsed;
        InstanceSettingsHost.Content=_instanceContent;InstanceSettingsHost.Visibility=Visibility.Visible;
    }
    private bool _leavingEditor;
    private void OpenInstanceSettings(InstanceCardViewModel card)
    {
        CloseCreateInstanceView();
        if(_settingsWindows.TryGetValue(card.InstanceId,out var existing))
        { if(!existing.IsClosed)ShowInstanceEditor(existing);return; }
        CloseEmbeddedUiForNavigation();
        var editor = new InstanceSettingsView(card.InstallPath,card.DisplayName,_settingsStore,launch:_launchService)
        {
            IsOperationBlocked=()=>IsInstanceBusy(card),
            IsMaintenanceBlocked=()=>_gameProcesses.Count!=0 || _launchRequests.Count!=0 || _activeOperations!=0 || CreateInstanceView.HasActiveOperation || _settingsWindows.Values.Any(x=>x.MaintenanceBusy),
        };
        _settingsWindows.Add(card.InstanceId,editor);
        editor.OperationFinished+=(_,_)=>{if(editor.IsClosed)_settingsWindows.Remove(card.InstanceId);};
        editor.Closed+=(_,_)=>{if(!editor.MaintenanceBusy)_settingsWindows.Remove(card.InstanceId);EmbeddedEditorClosed(editor);};
        editor.DeleteRequested+=async()=>await DeleteInstanceAsync(card,editor);
        editor.EditGlobalDefaultsRequested+=()=>
        {
            CancelInlinePrompt();_parentEditor=editor;
            var global=new InstanceSettingsView(editor.DirectoryPath,editor.Title,_settingsStore,true);
            global.Closed+=(_,_)=>EmbeddedEditorClosed(global);ShowInstanceEditor(global);
        };
        ShowInstanceEditor(editor);
    }

    private async Task DeleteInstanceAsync(InstanceCardViewModel card, InstanceSettingsView editor)
    {
        if(IsInstanceBusy(card) || editor.MaintenanceBusy || !CanInstallPack(card.InstanceId))
        { await ShowInlinePromptAsync("无法删除","实例正在启动、运行、安装或维护，请结束后再删除。","知道了");return; }
        bool server=!card.InstanceId.StartsWith(PackAutoUpdate.LocalIdPrefix,StringComparison.Ordinal);
        string shared="";
        try{var metadata=InstanceMaintenance.Read(card.InstallPath);if(!string.Equals(GameDirectoryResolver.ResolveGameDirectory(_settingsStore.Load(),card.InstallPath,metadata.Loader),card.InstallPath,StringComparison.OrdinalIgnoreCase))shared="此实例使用共享游戏目录，其中的存档和设置会保留。";}catch{ }
        string message=$"将把“{card.DisplayName}”的实例文件夹移到回收站，并从实例列表中移除。{shared}{(server?"之后可在“整合包”页重新安装。":"")}";
        if(await ShowInlinePromptAsync("删除实例",message,"删除")!=InlineChoice.Continue || editor.IsClosed)return;
        if(IsInstanceBusy(card) || !CanInstallPack(card.InstanceId)){await ShowInlinePromptAsync("无法删除","实例刚刚开始运行或安装，请结束后再删除。","知道了");return;}
        editor.DiscardPendingChanges();
        try{_activeOperations++;await Task.Run(()=>InstanceRemoval.Remove(_installedState,card.InstanceId,card.InstallPath));}
        catch(Exception ex){LauncherPrompt.Show(this,"删除失败："+ex.Message,"删除实例",MessageBoxButton.OK,MessageBoxImage.Warning);return;}
        finally{_activeOperations--;}
        _crashes.Remove(card.InstanceId);
        editor.Close();
        foreach(var pack in _cards)if(string.Equals(pack.InstanceId,card.InstanceId,StringComparison.Ordinal))pack.UpdateButtonState(null);
        if(!_windowClosed){RefreshInstances();ShowInstancesView();}
    }

    private void ShowInstanceEditor(InstanceSettingsView editor)
    {
        _instanceEditor=editor;
        editor.ConfirmAsync=async (message,title)=>await ShowInlinePromptAsync(title,message)==InlineChoice.Continue;
        editor.BackRequested-=EditorBackRequested;editor.BackRequested+=EditorBackRequested;
        _currentPage=MainPage.InstanceSettings;UpdateNavStyles();
        HeaderTitleText.Text=editor.IsGlobal?"游戏默认设置":"实例管理";HeaderStatusText.Text=editor.Title;
        NewInstanceButton.Visibility=RefreshButton.Visibility=Visibility.Collapsed;
        CardsScroll.Visibility=EmptyStateText.Visibility=LoadingState.Visibility=ErrorState.Visibility=InstancesScroll.Visibility=InstancesEmptyStateText.Visibility=SettingsScroll.Visibility=CommunityResourceView.Visibility=Visibility.Collapsed;
        InstanceSettingsHost.Content=editor;InstanceSettingsHost.Visibility=Visibility.Visible;
    }

    private async void EditorBackRequested()
    {
        var editor=_instanceEditor;if(editor==null)return;
        editor.FlushPendingSave();
        if(editor.HasUnsavedChanges)
        {
            // Settings save on change; what is left failed validation or waits for a running task.
            var choice=await ShowInlinePromptAsync("设置未保存","部分修改尚未保存（未通过校验或实例正在使用）。仍要返回吗？这些修改将丢失。","仍要返回");
            if(!ReferenceEquals(editor,_instanceEditor) || editor.IsClosed || choice!=InlineChoice.Continue)return;
            editor.DiscardPendingChanges();
        }
        editor.Close();
    }

    private void EmbeddedEditorClosed(InstanceSettingsView editor)
    {
        if(_leavingEditor || !ReferenceEquals(editor,_instanceEditor))return;
        if(editor.IsGlobal && _parentEditor!=null)
        {var parent=_parentEditor;_parentEditor=null;if(editor.Saved)parent.RefreshInherited();ShowInstanceEditor(parent);return;}
        _instanceEditor=null;InstanceSettingsHost.Content=null;InstanceSettingsHost.Visibility=Visibility.Collapsed;
        if(!_windowClosed){RefreshAfterInstanceSettings();ShowInstancesView();}
    }

    private void CloseEmbeddedUiForNavigation()
    {
        CancelInlinePrompt();
        if(_instanceContent!=null){_instanceContent.Close();_instanceContent=null;InstanceSettingsHost.Content=null;InstanceSettingsHost.Visibility=Visibility.Collapsed;}
        if(_instanceEditor==null && _parentEditor==null)return;
        _leavingEditor=true;
        try { _instanceEditor?.Close();_parentEditor?.Close();_instanceEditor=_parentEditor=null;InstanceSettingsHost.Content=null;InstanceSettingsHost.Visibility=Visibility.Collapsed; }
        finally { _leavingEditor=false; }
        if(!_windowClosed)RefreshAfterInstanceSettings();
    }

    private readonly HashSet<Process> _stoppedByUser = new();
    private readonly Dictionary<string, CrashContext> _crashes = new(StringComparer.Ordinal);
    private CrashContext? CreateCrashContext(InstanceCardViewModel card, LaunchResult result)
    {
        try
        {
            var metadata=InstanceMaintenance.Read(card.InstallPath);
            return new CrashContext
            {
                InstanceName=card.DisplayName,GameVersion=metadata.Game,Loader=metadata.Loader,LoaderVersion=metadata.LoaderVersion,
                GameDirectory=GameDirectoryResolver.ResolveGameDirectory(_settingsStore.Load(),card.InstallPath,metadata.Loader),
                VersionName=result.VersionName,JavaPath=result.JavaPath,JavaMajor=result.JavaMajor,
            };
        }
        catch { return null; }
    }
    private void TrackGame(InstanceCardViewModel card, Process process, GameProcessDiagnostics? diagnostics = null, CrashContext? crash = null)
    {
        _gameProcesses[card.InstanceId] = process;
        void Finished()
        {
            if (_gameProcesses.TryGetValue(card.InstanceId,out var current) && ReferenceEquals(current,process))
            {
                _gameProcesses.Remove(card.InstanceId);
                int? code=null;try{code=process.ExitCode;}catch{ }
                bool stopped=_stoppedByUser.Remove(process);
                string? hint=code is null or 0?null:diagnostics?.DescribeHint();
                card.GameExited(code,hint,stopped);
                if(!stopped && code is not (null or 0) && crash!=null)
                {
                    crash.ExitCode=code;crash.Hint=hint;crash.ExitedAt=DateTime.Now;
                    _crashes[card.InstanceId]=crash;card.OfferCrashReport();
                }
                RestoreAfterGames();
            }
        }
        process.Exited += (_,_) => { if(!Dispatcher.HasShutdownStarted)Dispatcher.BeginInvoke(new Action(Finished)); };
        process.EnableRaisingEvents = true;
        if(process.HasExited)Dispatcher.BeginInvoke(new Action(Finished));
    }

    private async void LaunchAction_Click(object sender, RoutedEventArgs e)
    {
        if(sender is not Button {DataContext:InstanceCardViewModel card})return;
        if(_launchRequests.TryGetValue(card.InstanceId,out var request)){request.Cancel();return;}
        if(!_gameProcesses.TryGetValue(card.InstanceId,out var process))
        {
            if(_crashes.TryGetValue(card.InstanceId,out var crash))await ExportCrashReportAsync(card,crash);
            return;
        }
        if(process.HasExited){_gameProcesses.Remove(card.InstanceId);card.GameExited();return;}
        if(await ShowInlinePromptAsync("结束游戏","将结束此实例的游戏进程，未保存的游戏进度可能丢失。继续？")!=InlineChoice.Continue)return;
        // The process may exit while the embedded confirmation is open.
        if(!_gameProcesses.TryGetValue(card.InstanceId,out var current) || !ReferenceEquals(process,current))return;
        try { _stoppedByUser.Add(process); TerminateTrackedGame(process); }
        catch { _stoppedByUser.Remove(process); card.LaunchFail("无法结束游戏进程，请检查游戏是否已退出。"); }
    }

    private bool _minimizedForGame;
    private void ApplyAfterLaunch()
    {
        var action=_settings.AfterLaunch;
        if(action==AfterLaunchAction.Keep || _windowClosed)return;
        // Never close over other work: another launch, a download, maintenance or unsaved edits stay visible.
        bool busy=_launchRequests.Count>1 || _activeOperations>1 || _migrating || _updatingLauncher
            || CreateInstanceView.HasActiveOperation || _settingsWindows.Values.Any(x=>x.MaintenanceBusy)
            || _globalSettingsInline?.HasUnsavedChanges==true || _instanceEditor?.HasUnsavedChanges==true;
        if(action==AfterLaunchAction.Close && !busy)
        {
            // Close after the launch call unwinds; the game keeps running without the launcher.
            _closingAfterFlush=true;
            Dispatcher.BeginInvoke(new Action(()=>{if(!_windowClosed)Close();}));
            return;
        }
        if(WindowState!=WindowState.Minimized){WindowState=WindowState.Minimized;_minimizedForGame=true;}
    }
    private void RestoreAfterGames()
    {
        if(!_minimizedForGame || _gameProcesses.Count!=0 || _windowClosed)return;
        _minimizedForGame=false;
        if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;
        Activate();
    }

    private async Task ExportCrashReportAsync(InstanceCardViewModel card, CrashContext crash)
    {
        var dialog=new Microsoft.Win32.SaveFileDialog
        {
            Title="导出崩溃报告",Filter="ZIP 压缩包 (*.zip)|*.zip",FileName=CrashReportExporter.DefaultFileName(crash),
            InitialDirectory=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if(dialog.ShowDialog(this)!=true)return;
        string target=dialog.FileName;
        try
        {
            // The access token never belongs in a shared report, even if a mod logged it.
            await Task.Run(()=>CrashReportExporter.Export(crash,target,new[]{_session.AccessToken,_session.ClientToken}));
            Process.Start(new ProcessStartInfo("explorer.exe","/select,\""+target+"\""){UseShellExecute=true});
        }
        catch(Exception ex){LauncherPrompt.Show(this,"导出失败："+ex.Message,"导出崩溃报告",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }

    // Operates only on the Process object returned by this launch. Never finds or
    // terminates unrelated Java processes by name or an unverified PID.
    internal static void TerminateTrackedGame(Process process)
    { if(!process.HasExited)process.Kill(); }
}
