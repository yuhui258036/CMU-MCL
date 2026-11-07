// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CMUmcl.Content;
using Microsoft.Win32;

namespace CMUmcl;

internal sealed partial class InstanceSettingsView
{
    private InstanceContentView? _embeddedContent;
    private InstanceContentView? _directoryView;
    private void BuildLayout()
    {
        var nav = new ListBox { ItemsSource = _global ? new[] { "全局游戏设置" }
            : new[] { "游戏设置", "组件管理", "模组", "资源包", "原理图", "共享设置", "显示信息" } };
        var scroll = new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        var host = new Grid(); host.Children.Add(scroll);
        if(_inlineGlobal)
        {
            InstancePageUi.Initialize(this);scroll.Content=null;
            var inline=new StackPanel();inline.Children.Add(_content);
            _status.Margin=new Thickness(0);_status.FontSize=12;_status.SetResourceReference(TextBlock.ForegroundProperty,"MutedTextBrush");
            inline.Children.Add(_status);Content=inline;
        }
        else InstancePageUi.Shell(this, Title, () => BackRequested?.Invoke(), nav, host, _status);
        // Embedded pages leave room for their own scrollbar; the inline settings tab scrolls with the page.
        var pageMargin = new Thickness(0, 0, _inlineGlobal ? 0 : 10, 0);
        var game = new StackPanel { Margin = pageMargin };
        var context = new DockPanel { Margin = new Thickness(0, 0, 0, 14), LastChildFill=false, HorizontalAlignment=HorizontalAlignment.Right };
        if (!_global)
        {
            var edit = Button("编辑全局默认", () => EditGlobalDefaultsRequested?.Invoke());
            edit.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(edit, Dock.Right); context.Children.Add(edit);
            var folder = Button("打开目录", OpenDirectoryPage);
            folder.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(folder, Dock.Right); context.Children.Add(folder);
        }
        if(!_global)game.Children.Add(context);
        foreach (int i in new[] { 1, 2, 3 }) game.Children.Add(_pages[i]);
        _content.Children.Add(game);
        foreach (int i in new[] { 0, 4, 5 }) { _pages[i].Margin = pageMargin; _pages[i].Visibility = Visibility.Collapsed; _content.Children.Add(_pages[i]); }
        nav.SelectionChanged += (_, _) =>
        {
            int tab = nav.SelectedIndex;
            if (!_global && tab == 1) PrefetchVersionLists();
            _embeddedContent?.Close(); if (_embeddedContent != null) host.Children.Remove(_embeddedContent); _embeddedContent = null;
            bool contentTab = !_global && tab >= 2 && tab <= 4;
            scroll.Visibility = contentTab ? Visibility.Collapsed : Visibility.Visible;
            game.Visibility = tab == 0 ? Visibility.Visible : Visibility.Collapsed;
            _pages[0].Visibility = tab == 6 ? Visibility.Visible : Visibility.Collapsed;
            _pages[4].Visibility = tab == 1 ? Visibility.Visible : Visibility.Collapsed;
            _pages[5].Visibility = tab == 5 ? Visibility.Visible : Visibility.Collapsed;
            if (contentTab)
            {
                try
                {
                    var metadata = InstanceMaintenance.Read(_directory);
                    string gameDirectory = GameDirectoryResolver.ResolveGameDirectory(_store.Load(), _directory, metadata.Loader);
                    _embeddedContent = new InstanceContentView(_directory, gameDirectory, Title,
                        () => _maintenanceBusy || IsOperationBlocked?.Invoke() == true || (gameDirectory != _directory && IsMaintenanceBlocked?.Invoke() == true),
                        (title, message) => Confirm(message, title), true);
                    host.Children.Add(_embeddedContent); _embeddedContent.SelectCategory(tab - 1);
                }
                catch (Exception ex) { _status.Text = "无法读取实例内容：" + ex.Message; }
            }
            scroll.ScrollToTop();
        };
        var general = InstancePageUi.Section(_pages[0], "显示信息");
        AddField(general, "显示名称（留空恢复原名）", _name);
        _icon.ItemsSource = new[] { "自动图标（按加载器）" }.Concat(InstanceOptionsStore.Icons);
        AddField(general, "实例图标", _icon); AddText(general, _directory);
        if (!_global)
        {
            var removal = InstancePageUi.Section(_pages[0], "删除实例");
            AddText(removal, "实例文件夹会移到回收站，可以从回收站恢复。共享游戏目录中的内容不受影响。");
            var delete = Button("删除实例", () => DeleteRequested?.Invoke());
            delete.SetResourceReference(ForegroundProperty, "DangerBrush");
            removal.Children.Add(delete);
        }

        var java = SettingSection(_pages[1], "Java", _javaOverride);
        var picker = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        picker.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); picker.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _javaCandidates.MinHeight = 38; _javaCandidates.Margin = new Thickness(0, 0, 10, 0); _javaCandidates.DisplayMemberPath = "DisplayName";
        _javaCandidates.ToolTip = "选择检测到的 Java"; picker.Children.Add(_javaCandidates);
        _javaChoiceHint.Margin=new Thickness(12,0,42,0);_javaChoiceHint.VerticalAlignment=VerticalAlignment.Center;_javaChoiceHint.IsHitTestVisible=false;_javaChoiceHint.FontSize=13;_javaChoiceHint.SetResourceReference(TextBlock.ForegroundProperty,"MutedTextBrush");picker.Children.Add(_javaChoiceHint);
        _scanJava = Button("检测 Java", async () => await ScanJavaChoices()); _scanJava.Margin = new Thickness(0);
        Grid.SetColumn(_scanJava, 1); picker.Children.Add(_scanJava); java.Children.Add(picker);
        var javaPath = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        javaPath.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); javaPath.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _java.MinHeight = 38; _java.Margin = new Thickness(0, 0, 10, 0); _java.ToolTip = "Java 路径；留空时自动选择"; javaPath.Children.Add(_java);
        _javaPathHint.Text="自动选择（无需指定路径）";_javaPathHint.Margin=new Thickness(12,0,20,0);_javaPathHint.VerticalAlignment=VerticalAlignment.Center;_javaPathHint.IsHitTestVisible=false;_javaPathHint.FontSize=13;_javaPathHint.SetResourceReference(TextBlock.ForegroundProperty,"MutedTextBrush");javaPath.Children.Add(_javaPathHint);
        _javaBrowse = Button("浏览…", () =>
        {
            var dialog = new OpenFileDialog { Filter = "Java (javaw.exe;java.exe)|javaw.exe;java.exe" };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) _java.Text = dialog.FileName;
        });
        _javaBrowse.Margin = new Thickness(0); Grid.SetColumn(_javaBrowse, 1); javaPath.Children.Add(_javaBrowse); java.Children.Add(javaPath);
        var javaFooter = new DockPanel();
        _javaAutomatic = Button("自动选择", () => { _java.Text = ""; _javaCandidates.SelectedItem = null; });
        _javaAutomatic.Margin = new Thickness(0, 0, 12, 0); DockPanel.SetDock(_javaAutomatic, Dock.Left); javaFooter.Children.Add(_javaAutomatic);
        _javaInfo.FontSize = 12; _javaInfo.TextWrapping = TextWrapping.Wrap; _javaInfo.VerticalAlignment = VerticalAlignment.Center;
        _javaInfo.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush"); javaFooter.Children.Add(_javaInfo); java.Children.Add(javaFooter);
        _java.TextChanged += (_, _) => UpdateJavaDescription();
        _javaCandidates.SelectionChanged += (_, _) => { if (!_syncJavaChoice && _javaCandidates.SelectedItem is Launch.JavaInfo selected && Own(_javaOverride)) _java.Text = selected.JavaPath; };

        var memory = SettingSection(_pages[1], "内存分配", _memoryOverride); memory.Children.Add(_auto);
        var memoryRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        memoryRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); memoryRow.ColumnDefinitions.Add(new() { Width = new GridLength(96) }); memoryRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _memorySlider.Minimum = 512; _memorySlider.Maximum = Math.Max(8192, _physicalMemory.TotalMb);
        _memorySlider.TickFrequency = 256; _memorySlider.IsSnapToTickEnabled = true; _memorySlider.VerticalAlignment = VerticalAlignment.Center; _memorySlider.Margin = new Thickness(0, 0, 16, 0);
        memoryRow.Children.Add(_memorySlider); _memory.MinHeight = 38;_memory.ToolTip="手动分配上限（MiB）；自动模式下保留此值"; Grid.SetColumn(_memory, 1); memoryRow.Children.Add(_memory);
        var unit = InstancePageUi.Text("MiB", 12, true); unit.VerticalAlignment = VerticalAlignment.Center; unit.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(unit, 2); memoryRow.Children.Add(unit); memory.Children.Add(memoryRow);
        _memoryInfo.FontSize = 12; _memoryInfo.TextWrapping = TextWrapping.Wrap; _memoryInfo.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush"); memory.Children.Add(_memoryInfo);
        _memory.TextChanged += (_, _) => UpdateMemoryControls();
        _memorySlider.ValueChanged += (_, _) => { if (!_syncMemory && _memorySlider.IsEnabled) _memory.Text = ((int)_memorySlider.Value).ToString(); };

        var window = SettingSection(_pages[2], "窗口模式", _windowOverride); window.Children.Add(_fullscreen);
        var dimensions = SettingSection(_pages[2], "窗口尺寸", _sizeOverride);
        _resolution.ItemsSource = new[] { "自定义", "854 × 480", "1280 × 720", "1600 × 900", "1920 × 1080", "2560 × 1440" };
        _resolution.SelectedIndex = 0; _resolution.MinHeight = 38; _resolution.Margin = new Thickness(0, 0, 0, 12); dimensions.Children.Add(_resolution);
        _resolution.SelectionChanged += (_, _) =>
        {
            if (_syncResolution || _resolution.SelectedIndex <= 0) return;
            _syncResolution=true;
            try { var parts = ((string)_resolution.SelectedItem).Split('×'); _width.Text = parts[0].Trim(); _height.Text = parts[1].Trim(); }
            finally { _syncResolution=false; }
        };
        _width.TextChanged+=(_,_)=>UpdateResolutionChoice();_height.TextChanged+=(_,_)=>UpdateResolutionChoice();
        var sizes = new Grid(); sizes.ColumnDefinitions.Add(new()); sizes.ColumnDefinitions.Add(new());
        var width = new StackPanel { Margin = new Thickness(0, 0, 8, 0) }; var height = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        AddField(width, "宽度", _width); AddField(height, "高度", _height); Grid.SetColumn(height, 1); sizes.Children.Add(width); sizes.Children.Add(height); dimensions.Children.Add(sizes);
        if (_global)
        {
            // A launcher preference, but it belongs with the game window options it follows.
            var afterLaunch = InstancePageUi.Section(_pages[2], "游戏启动后");
            InstancePageUi.Hint(afterLaunch, "游戏窗口出现后启动器的行为。");
            _afterLaunch.ItemsSource = new[] { "保持启动器", "最小化，游戏退出后恢复", "关闭启动器" };
            _afterLaunch.MinHeight = 38; _afterLaunch.ToolTip = "有下载、维护或其他游戏正在启动时不会关闭，改为最小化。";
            afterLaunch.Children.Add(_afterLaunch);
        }

        var advanced = new Expander { Header = "高级设置", Margin = new Thickness(0, 4, 0, 14), Padding = new Thickness(0, 10, 0, 0) };
        advanced.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        var advancedBody = new StackPanel(); advanced.Content = advancedBody; _pages[3].Children.Add(advanced);
        var arguments = SettingSection(advancedBody, "JVM 参数", _argsOverride);
        AddText(arguments, "每行一个参数。内存由上方的内存分配设置控制。");
        _arguments.AcceptsReturn = true; _arguments.MinHeight = 140; _arguments.VerticalContentAlignment = VerticalAlignment.Top;
        _arguments.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"); _arguments.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; arguments.Children.Add(_arguments);
        if (!_global) BuildMaintenancePages(); nav.SelectedIndex = 0;
    }

    private void OpenDirectoryPage()
    {
        try
        {
            var previous=Content;
            var metadata=InstanceMaintenance.Read(_directory);
            string gameDirectory=GameDirectoryResolver.ResolveGameDirectory(_store.Load(),_directory,metadata.Loader);
            var directories=new InstanceContentView(_directory,gameDirectory,_name.Text.Length>0?_name.Text:metadata.Name,
                ()=>true,(_,_)=>System.Threading.Tasks.Task.FromResult(false),directoriesOnly:true);
            _directoryView=directories;
            directories.BackRequested+=()=>{directories.Close();_directoryView=null;if(!IsClosed)Content=previous;};
            Content=directories;
        }
        catch(Exception ex){_status.Text="无法打开目录："+ex.Message;}
    }

    private StackPanel SettingSection(Panel page, string title, CheckBox inheritance)
    {
        var section = new StackPanel(); var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        inheritance.SetResourceReference(StyleProperty, "InstanceInheritance"); inheritance.ToolTip = "沿用全局时随全局设置变化；独立设置仅影响当前实例。";
        inheritance.Visibility = _global ? Visibility.Collapsed : Visibility.Visible; DockPanel.SetDock(inheritance, Dock.Right); header.Children.Add(inheritance);
        var label = InstancePageUi.Text(title, 15); label.FontWeight = FontWeights.SemiBold; label.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(label);
        section.Children.Add(header); page.Children.Add(InstancePageUi.Card(section)); return section;
    }
}
