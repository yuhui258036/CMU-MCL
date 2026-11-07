// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CMUmcl.Content;
using Microsoft.Win32;
using System.Threading;
using System.Threading.Tasks;
using CMUmcl.Launch;

namespace CMUmcl
{
    internal sealed partial class InstanceSettingsView : UserControl
    {
        private readonly string _directory;
        private readonly bool _global;
        private readonly bool _inlineGlobal;
        private readonly SettingsStore _store;
        private bool _loadFailed;
        private Button? _javaBrowse;
        private readonly TextBox _name=new(),_java=new(),_memory=new(),_width=new(),_height=new(),_arguments=new();
        private readonly ComboBox _icon=new(),_afterLaunch=new();
        private readonly CheckBox _javaOverride=new(),_memoryOverride=new(),_windowOverride=new(),_sizeOverride=new(),_argsOverride=new(),_auto=new(){Content="自动分配内存"},_fullscreen=new(){Content="全屏启动"};
        private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap};
        private readonly Grid _content=new();
        private readonly StackPanel[] _pages={new(),new(),new(),new(),new(),new()};
        internal bool Saved {get;private set;}
        internal InstanceSettingsView(string directory,string title,SettingsStore store,bool global=false,LaunchService? launch=null,bool inlineGlobal=false)
        {
            _directory=directory;_store=store;_global=global;_launch=launch;_inlineGlobal=inlineGlobal&&global;
            Title=global?"全局游戏设置":"实例管理 · "+title;
            BuildLayout();

            try{LoadValues();}catch{_loadFailed=true;_status.Text="设置文件无效或无法读取；已停止自动保存，请先恢复有效设置文件。";}
            foreach(var check in OverrideChecks())check.Click+=(_,_)=>RefreshInherited();
            foreach(var check in new[]{_auto,_fullscreen})check.Click+=(_,_)=>UpdateEnabled();UpdateEnabled();
            UpdateJavaDescription();_savedSnapshot=CaptureSettings();
            // Every edit is saved shortly after it is made; there is no save button.
            _autoSave=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(500)};
            _autoSave.Tick+=(_,_)=>{_autoSave.Stop();Save();};
            foreach(var box in new[]{_name,_java,_memory,_width,_height,_arguments})box.TextChanged+=(_,_)=>ScheduleSave();
            foreach(var combo in new[]{_icon,_afterLaunch})combo.SelectionChanged+=(_,_)=>ScheduleSave();
            foreach(var check in OverrideChecks().Concat(new[]{_auto,_fullscreen}).Concat(_syncChecks.Values))check.Click+=(_,_)=>ScheduleSave();
        }
        private readonly System.Windows.Threading.DispatcherTimer _autoSave;
        private void ScheduleSave(){if(IsClosed||_loadFailed)return;_autoSave.Stop();_autoSave.Start();}
        /// <summary>Saves pending edits now (leaving the page, closing, tests). Invalid edits stay unsaved with the reason shown.</summary>
        internal void FlushPendingSave(){_autoSave.Stop();if(HasUnsavedChanges)Save();}
        /// <summary>Drops edits the user chose to abandon so closing does not try to save them.</summary>
        internal void DiscardPendingChanges(){_autoSave.Stop();_savedSnapshot=CaptureSettings();}
        internal string Title {get;}
        internal string DirectoryPath=>_directory;
        internal bool IsGlobal=>_global;
        internal bool IsClosed {get;private set;}
        internal event EventHandler? Closed;
        internal event EventHandler? OperationFinished;
        internal event Action? SettingsApplied;
        internal event Action? BackRequested;
        internal event Action? EditGlobalDefaultsRequested;
        internal event Action? DeleteRequested;
        internal Func<string,string,Task<bool>>? ConfirmAsync {get;set;}
        private string _savedSnapshot="";
        internal bool HasUnsavedChanges=>!IsClosed && CaptureSettings()!=_savedSnapshot;
        private string CaptureSettings()=>System.Text.Json.JsonSerializer.Serialize(new object?[]{
            _global?null:_name.Text,_global?null:_icon.SelectedIndex,
            _javaOverride.IsChecked,Own(_javaOverride)?_java.Text:null,
            _memoryOverride.IsChecked,Own(_memoryOverride)?new object?[]{_auto.IsChecked,_memory.Text}:null,
            _windowOverride.IsChecked,Own(_windowOverride)?_fullscreen.IsChecked:null,
            _sizeOverride.IsChecked,Own(_sizeOverride)?new object?[]{_width.Text,_height.Text}:null,
            _argsOverride.IsChecked,(_global||_argsOverride.IsChecked==true)?_arguments.Text:null,
            _syncChecks.Where(x=>x.Value.IsChecked==true).Select(x=>x.Key).ToArray(),_global?_afterLaunch.SelectedIndex:null});
        internal void SaveAndClose(){FlushPendingSave();if(!HasUnsavedChanges && !_inlineGlobal)Close();}
        internal void RefreshGlobalIfUnchanged()
        {
            if(!_global || HasUnsavedChanges)return;
            LoadValues();UpdateEnabled();UpdateJavaDescription();_savedSnapshot=CaptureSettings();
        }
        internal void Close()
        {
            if(IsClosed)return;FlushPendingSave();IsClosed=true;_autoSave.Stop();_maintenanceCts?.Cancel();_javaScanCts.Cancel();_embeddedContent?.Close();_directoryView?.Close();Closed?.Invoke(this,EventArgs.Empty);
        }
        private Task<bool> Confirm(string message,string title)=>ConfirmAsync?.Invoke(message,title)??Task.FromResult(false);
        private static void AddText(Panel panel,string text)=>InstancePageUi.Hint(panel,text);
        private static void AddField(Panel panel,string label,Control field)=>InstancePageUi.Field(panel,label,field);
        private static Button Button(string text,Action action)=>InstancePageUi.Button(text,action);
        private void LoadValues()
        {
            var global=_store.Load();var local=_global?new InstanceOptions():InstanceOptionsStore.Load(_directory);var effective=InstanceOptionsStore.Resolve(global,local);
            _name.Text=local.Name??"";_icon.SelectedIndex=local.Icon==null?0:Array.IndexOf(InstanceOptionsStore.Icons,local.Icon)+1;
            var checks=OverrideChecks();for(int i=0;i<checks.Length;i++)checks[i].IsChecked=InstanceOptionsStore.UsesOverride(local,InstanceOptionsStore.OverrideKeys[i]);
            foreach(var box in checks)box.Visibility=_global?Visibility.Collapsed:Visibility.Visible;
            if(_global)_afterLaunch.SelectedIndex=(int)global.AfterLaunch;
            _java.Text=effective.JavaPath??"";_auto.IsChecked=effective.AutoMemory;_memory.Text=effective.MaxMemoryMb.ToString();_width.Text=effective.WindowWidth.ToString();_height.Text=effective.WindowHeight.ToString();_fullscreen.IsChecked=effective.FullScreen;_arguments.Text=string.Join(Environment.NewLine,effective.ExtraJvmArguments??Array.Empty<string>());
        }
        internal void RefreshInherited()
        {
            var g=_store.Load();
            if(_javaOverride.IsChecked!=true)_java.Text=g.JavaPath??"";
            if(_memoryOverride.IsChecked!=true){_auto.IsChecked=g.AutoMemory;_memory.Text=g.MaxMemoryMb.ToString();}
            if(_windowOverride.IsChecked!=true)_fullscreen.IsChecked=g.FullScreen;
            if(_sizeOverride.IsChecked!=true){_width.Text=g.WindowWidth.ToString();_height.Text=g.WindowHeight.ToString();}
            if(_argsOverride.IsChecked!=true)_arguments.Text=string.Join(Environment.NewLine,g.ExtraJvmArguments??Array.Empty<string>());
            UpdateEnabled();
        }
        private void UpdateEnabled()
        {
            bool java=Own(_javaOverride),memory=Own(_memoryOverride),size=Own(_sizeOverride);
            if(_javaBrowse!=null)_javaBrowse.IsEnabled=java;
            _java.IsEnabled=java;_javaCandidates.IsEnabled=java;_javaAutomatic.IsEnabled=java;
            _auto.IsEnabled=memory;_memory.IsEnabled=_memorySlider.IsEnabled=memory&&_auto.IsChecked!=true;
            _fullscreen.IsEnabled=Own(_windowOverride);_width.IsEnabled=_height.IsEnabled=_resolution.IsEnabled=size&&_fullscreen.IsChecked!=true;
            _arguments.IsEnabled=Own(_argsOverride);UpdateMemoryControls();
        }
        private CheckBox[] OverrideChecks()=>new[]{_javaOverride,_memoryOverride,_windowOverride,_sizeOverride,_argsOverride};
        private bool Own(CheckBox check)=>_global||check.IsChecked==true;
        private void Save()
        {
            if(IsClosed || _loadFailed || !HasUnsavedChanges)return;
            if(_maintenanceBusy){ScheduleSave();return;}
            if(!_global && IsOperationBlocked?.Invoke()==true){_status.Text="实例正在使用，修改会在任务结束后自动保存。";ScheduleSave();return;}
            try
            {
                var local=new InstanceOptions{SharedSettings=_global?Array.Empty<string>():_syncChecks.Where(x=>x.Value.IsChecked==true).Select(x=>x.Key).ToArray(),Name=string.IsNullOrWhiteSpace(_name.Text)?null:_name.Text.Trim(),Icon=_icon.SelectedIndex>0?InstanceOptionsStore.Icons[_icon.SelectedIndex-1]:null};
                local.OverrideProperties=InstanceOptionsStore.OverrideKeys.Where((key,i)=>Own(OverrideChecks()[i])).ToArray();
                // Unselected parts of each legacy container are snapshots only; resolution uses the explicit keys above.
                var defaults=_store.Load();
                if(Own(_javaOverride)||Own(_memoryOverride))
                {
                    int memory=defaults.MaxMemoryMb;if(Own(_memoryOverride)&&!int.TryParse(_memory.Text,out memory))throw new InvalidDataException("请输入有效内存数值。");
                    var path=Own(_javaOverride)?_java.Text.Trim():defaults.JavaPath;
                    JavaInfo? detected=null;
                    if(Own(_javaOverride)&&!string.IsNullOrEmpty(path))
                    {
                        detected=DetectJavaCached(path);
                        if(detected is not {Major:>0})throw new InvalidDataException("Java 路径无效或无法识别版本。");
                        if(!detected.IsSupportedArchitecture)throw new InvalidDataException("所选 Java 与本机架构不兼容，请更换运行时。");
                        path=detected.JavaPath;
                        InstanceMetadata? metadata=null;
                        if(!_global){try{metadata=InstanceMaintenance.Read(_directory);}catch{ }}
                        if(metadata!=null && JavaVersionResolver.ResolveMaximumMajor(metadata.Game??"",metadata.Loader) is int maximum && detected.Major>maximum)
                            throw new InvalidDataException($"Forge {metadata.Game} 只能使用 Java {maximum}，所选 Java {detected.Major} 会导致启动崩溃。");
                    }
                    // Match the launch-time check so an impossible heap is rejected here, not when starting the game.
                    if(Own(_memoryOverride)&&_auto.IsChecked!=true&&MemoryAllocation.ValidateManual(memory,MemorySnapshot.Read(),detected?.Bits??64) is { } memoryError)throw new InvalidDataException(memoryError);
                    local.Java=new(){Path=path,AutoMemory=Own(_memoryOverride)?_auto.IsChecked==true:defaults.AutoMemory,MemoryMb=memory};
                }
                if(Own(_windowOverride)||Own(_sizeOverride))
                {
                    int width=defaults.WindowWidth,height=defaults.WindowHeight;
                    if(Own(_sizeOverride)&&(!int.TryParse(_width.Text,out width)||!int.TryParse(_height.Text,out height)))throw new InvalidDataException("请输入有效窗口尺寸。");
                    local.Window=new(){Width=width,Height=height,FullScreen=Own(_windowOverride)?_fullscreen.IsChecked==true:defaults.FullScreen};
                }
                if(_global||_argsOverride.IsChecked==true)local.JvmArguments=_arguments.Text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(x=>x.Trim()).Where(x=>x.Length>0).ToArray();
                InstanceOptionsStore.Validate(local);
                if(_global){var settings=_store.Load();settings.JavaPath=local.Java!.Path;settings.AutoMemory=local.Java.AutoMemory;settings.MaxMemoryMb=local.Java.MemoryMb;settings.WindowWidth=local.Window!.Width;settings.WindowHeight=local.Window.Height;settings.FullScreen=local.Window.FullScreen;settings.ExtraJvmArguments=local.JvmArguments!;if(_afterLaunch.SelectedIndex>=0)settings.AfterLaunch=(AfterLaunchAction)_afterLaunch.SelectedIndex;if(!_store.Save(settings))throw new IOException();}
                else InstanceOptionsStore.Save(_directory,local);
                Saved=true;_savedSnapshot=CaptureSettings();_status.Text="已自动保存。";SettingsApplied?.Invoke();
            }
            catch(InvalidDataException ex){_status.Text=ex.Message;}
            catch{_status.Text="保存失败，原设置已保留，请检查目录权限。";}
        }
    }
}
