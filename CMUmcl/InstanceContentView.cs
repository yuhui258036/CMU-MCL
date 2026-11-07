// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CMUmcl.Content;
using Microsoft.Win32;
using CMUmcl.Community;
using System.Windows.Media.Imaging;

namespace CMUmcl;

internal sealed partial class InstanceContentView : UserControl
{
    private readonly InstanceContentFiles _files;
    private readonly string _instanceDirectory;
    private readonly Func<bool> _blocked;
    private readonly Func<string,string,Task<bool>> _confirm;
    private readonly ContentControl _body=new();
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap};
    private readonly ListBox _navigation=new();
    private readonly HashSet<string> _selected=new(StringComparer.OrdinalIgnoreCase);
    private int _generation;
    private LocalModIconResolver? _modIcons;
    internal Func<string,Task<BitmapSource?>>? LoadModIconOverride {get;set;}
    internal bool Closed {get;private set;}
    internal event Action? BackRequested;
    internal InstanceContentView(string instanceDirectory,string gameDirectory,string name,Func<bool> blocked,Func<string,string,Task<bool>> confirm,bool embedded=false,bool directoriesOnly=false)
    {
        _instanceDirectory=instanceDirectory;_files=new(gameDirectory);_blocked=blocked;_confirm=confirm;
        _navigation.ItemsSource=directoriesOnly?new[]{"快捷方式"}:new[]{"快捷方式","模组管理","资源包管理","原理图"};
        _body.Margin=new Thickness(0,0,10,0);
        _body.HorizontalContentAlignment=HorizontalAlignment.Stretch;
        _body.VerticalContentAlignment=VerticalAlignment.Stretch;
        if(embedded)
        {
            InstancePageUi.Initialize(this);
            var root=new Grid();root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=GridLength.Auto});
            root.Children.Add(_body);Grid.SetRow(_status,1);_status.FontSize=12;_status.Margin=new Thickness(0,8,10,0);_status.SetResourceReference(TextBlock.ForegroundProperty,"MutedTextBrush");root.Children.Add(_status);Content=root;
        }
        else InstancePageUi.Shell(this,(directoriesOnly?"实例目录 · ":"实例内容 · ")+name,()=>BackRequested?.Invoke(),_navigation,_body,_status);
        _navigation.SelectionChanged+=(_,_)=>ShowPage();_navigation.SelectedIndex=0;
    }
    internal void Close(){if(Closed)return;Closed=true;StopSchematicPreview();_generation++;_modIcons?.Dispose();}
    internal void SelectCategory(int index)=>_navigation.SelectedIndex=index;
    private void ShowPage()
    {
        StopSchematicPreview();_generation++;_selected.Clear();_modUpdates.Clear();_status.Text="";
        if(_navigation.SelectedIndex==0)
        {
            var panel=new FolderCardsPanel();
            foreach(var link in new[]{("版本文件夹",(string?)null),("存档文件夹","saves"),("Mod 文件夹","mods"),("截图文件夹","screenshots"),("资源包文件夹","resourcepacks"),("原理图文件夹","schematics"),("日志文件夹","logs"),("崩溃报告文件夹","crash-reports")})
            {var item=link;var b=Button(item.Item1,()=>OpenFolder(item.Item2));b.HorizontalAlignment=HorizontalAlignment.Stretch;b.Tag=item.Item2??"instance";b.Margin=new Thickness(0);b.SetResourceReference(StyleProperty,"InstanceFolderButton");panel.Children.Add(b);}
            _body.Content=new ScrollViewer{Content=panel,Focusable=false,FocusVisualStyle=null,BorderThickness=new Thickness(0),VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};return;
        }
        if(_navigation.SelectedIndex==3){ShowSchematics();return;}
        ShowFiles(_navigation.SelectedIndex==1);
    }
    private void OpenFolder(string? relative)
    {
        Try(()=>{string path=relative==null?new InstanceContentFiles(_instanceDirectory).Folder(""):_files.Folder(relative);Directory.CreateDirectory(path);Process.Start(new ProcessStartInfo(path){UseShellExecute=true});});
    }
    private void Try(Action action){try{action();}catch(Exception ex){_status.Text="操作未完成："+ex.Message;}}
    private static string Mutate(IEnumerable<string> paths,Action<string> action)
    {
        var errors=new List<string>();int completed=0;
        foreach(string path in paths){try{action(path);completed++;}catch(Exception ex){errors.Add(Path.GetFileName(path)+"："+ex.Message);}}
        return errors.Count==0?"":"已处理 "+completed+" 项；未完成 "+errors.Count+" 项。"+string.Join("；",errors.Take(3));
    }
    private bool CanChange(){if(Closed)return false;if(_blocked()){_status.Text="实例正在使用，请结束任务后修改。";return false;}return true;}
    private void ShowFiles(bool mods)
    {
        string folder=mods?"mods":"resourcepacks";int pageGeneration=_generation,generation=0;
        Button? updateAll=null;
        var all=new CheckBox{Content="全选",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,0,12,8)};
        var list=new StackPanel();IReadOnlyList<InstanceContentFile> items=Array.Empty<InstanceContentFile>();
        var grid=new Grid();grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});_body.Content=grid;
        var searchPanel=new StackPanel();searchPanel.Children.Add(Label("搜索名称、描述或文件名",12));var search=new TextBox{MinHeight=36,Margin=new Thickness(0,4,0,0),ToolTip="搜索名称、描述或文件名"};searchPanel.Children.Add(search);grid.Children.Add(InstancePageUi.Card(searchPanel));
        var toolbar=new WrapPanel{Margin=new Thickness(0,12,0,0)};searchPanel.Children.Add(toolbar);
        toolbar.Children.Add(Button("打开文件夹",()=>OpenFolder(folder)));
        toolbar.Children.Add(Button("从文件安装",()=>{if(!CanChange())return;var dialog=new OpenFileDialog{Filter=mods?"Mod 文件 (*.jar)|*.jar":"资源包 (*.zip)|*.zip",Multiselect=true};if(dialog.ShowDialog(Window.GetWindow(this))!=true||!CanChange())return;Refresh(Mutate(dialog.FileNames,file=>_files.Install(folder,file,mods)));}));
        toolbar.Children.Add(Button("刷新",()=>Refresh()));
        toolbar.Children.Add(all);
        toolbar.Children.Add(Button("启用/禁用所选",()=>ChangeSelected(false)));
        toolbar.Children.Add(Button("删除所选",()=>ChangeSelected(true)));
        if(mods && ModUpdateTarget()!=null)
        {
            toolbar.Children.Add(Button("检查更新",async()=>await CheckModUpdatesAsync(items,Render)));
            updateAll=Button("全部更新",async()=>await ApplyModUpdatesAsync(_modUpdates.Values.ToArray(),notice=>Refresh(notice)));
            updateAll.Visibility=Visibility.Collapsed;toolbar.Children.Add(updateAll);
        }
        var scroll=new ScrollViewer{Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetRow(scroll,2);grid.Children.Add(scroll);
        void Render()
        {
            list.Children.Clear();string query=search.Text.Trim();var visible=items.Where(i=>(i.Name+" "+i.Description+" "+Path.GetFileName(i.Path)).IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0).ToArray();
            list.Children.Add(Label($"全部 {items.Count} · 当前 {visible.Length}",13));
            if(updateAll!=null){updateAll.Visibility=_modUpdates.Count>0?Visibility.Visible:Visibility.Collapsed;updateAll.Content=$"全部更新（{_modUpdates.Count}）";}
            foreach(var item in visible)
            {
                var row=new Grid{Margin=new Thickness(12,6,12,6),MinHeight=76};row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});row.ColumnDefinitions.Add(new(){Width=new GridLength(56)});row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
                var check=new CheckBox{IsChecked=_selected.Contains(item.Path),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8)};check.Click+=(_,_)=>{if(check.IsChecked==true)_selected.Add(item.Path);else _selected.Remove(item.Path);};row.Children.Add(check);
                var iconSurface=new Grid{Width=40,Height=40,Opacity=item.Disabled?0.55:1};
                var fallback=new Border{CornerRadius=new CornerRadius(5),Child=new TextBlock{Text=mods?"\uE7FC":"\uE8B7",FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=22,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}};fallback.SetResourceReference(Border.BackgroundProperty,"AccentSoftBrush");fallback.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,"AccentBrush");iconSurface.Children.Add(fallback);
                var image=new Image{Width=40,Height=40,Stretch=Stretch.Uniform,Visibility=Visibility.Collapsed};iconSurface.Children.Add(image);Grid.SetColumn(iconSurface,1);row.Children.Add(iconSurface);
                if(mods)_=LoadIcon(item,image,fallback);
                else if(item.Icon!=null){image.Source=item.Icon;image.Visibility=Visibility.Visible;fallback.Visibility=Visibility.Collapsed;}
                var text=new StackPanel{Margin=new Thickness(0,8,12,8),VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(text,2);row.Children.Add(text);
                var title=RowText(item.Name+(item.Disabled?" · 已禁用":""),14,false);title.FontWeight=FontWeights.SemiBold;text.Children.Add(title);text.Children.Add(RowText(Path.GetFileName(item.Path),12,true));if(item.Description.Length>0)text.Children.Add(RowText(item.Description,12,true));
                var actions=new WrapPanel{MaxWidth=148,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(actions,3);row.Children.Add(actions);
                if(mods && _modUpdates.TryGetValue(item.Path,out var update))
                {
                    var available=RowText($"可更新：{update.CurrentVersion} → {update.LatestVersion}",12,false);available.SetResourceReference(TextBlock.ForegroundProperty,"AccentBrush");text.Children.Add(available);
                    actions.Children.Add(Button("更新",async()=>await ApplyModUpdatesAsync(new[]{update},notice=>Refresh(notice))));
                }
                actions.Children.Add(Button(item.Disabled?"启用":"禁用",()=>{if(CanChange())Refresh(Mutate(new[]{item.Path},_files.Toggle));}));
                actions.Children.Add(Button("删除",async ()=>await Delete(new[]{item.Path})));
                var border=new Border{Child=row,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(6),Margin=new Thickness(0,0,0,10)};border.SetResourceReference(BorderBrushProperty,"PanelBorderBrush");border.SetResourceReference(BackgroundProperty,"PanelBrush");list.Children.Add(border);
            }
            if(visible.Length==0)list.Children.Add(Label(items.Count==0?"此目录暂无资源，可从文件安装。":"没有匹配的资源。",14));
        }
        async Task LoadIcon(InstanceContentFile item,Image image,Border fallback)
        {
            try
            {
                var icon=await (LoadModIconOverride?.Invoke(item.Path) ?? (_modIcons??=new LocalModIconResolver()).LoadAsync(item.Path));
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
                if(Closed||_generation!=pageGeneration||!image.IsLoaded||icon==null)return;
                image.Source=icon;image.Visibility=Visibility.Visible;fallback.Visibility=Visibility.Collapsed;
            }
            catch { /* Keep the default icon for offline, unknown and changed files. */ }
        }
        async void Refresh(string? notice=null)
        {
            int request=++generation;_status.Text="读取资源列表…";
            try{var result=await Task.Run(()=>_files.List(folder,mods));if(Closed||_generation!=pageGeneration||request!=generation)return;items=result;_selected.Clear();all.IsChecked=false;Render();_status.Text=!string.IsNullOrEmpty(notice)?notice:"";}
            catch(Exception ex){if(!Closed&&_generation==pageGeneration&&request==generation)_status.Text="读取失败："+ex.Message;}
        }
        async Task Delete(string[] paths)
        {
            if(paths.Length==0||!CanChange())return;
            if(!await _confirm("删除所选资源","删除这 "+paths.Length+" 项资源？备份保存在游戏目录的 .cmumcl-trash 中。")||!CanChange())return;
            Refresh(Mutate(paths,path=>_files.Remove(path)));
        }
        async void ChangeSelected(bool delete)
        {
            var paths=_selected.ToArray();if(delete){await Delete(paths);return;}if(!CanChange())return;
            Refresh(Mutate(paths,_files.Toggle));
        }
        all.Click+=(_,_)=>{foreach(var item in items.Where(i=>(i.Name+" "+i.Description+" "+Path.GetFileName(i.Path)).IndexOf(search.Text.Trim(),StringComparison.OrdinalIgnoreCase)>=0)){if(all.IsChecked==true)_selected.Add(item.Path);else _selected.Remove(item.Path);}Render();};
        search.TextChanged+=(_,_)=>Render();Refresh();
    }
    private void ShowSchematics()
    {
        int pageGeneration=_generation,searchGeneration=0;
        var grid=new Grid();foreach(var height in new[]{GridLength.Auto,GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star)})grid.RowDefinitions.Add(new(){Height=height});_body.Content=grid;
        var searchPanel=new StackPanel();searchPanel.Children.Add(Label("搜索原理图文件名",12));var search=new TextBox{MinHeight=40,Margin=new Thickness(0,4,0,0)};searchPanel.Children.Add(search);grid.Children.Add(InstancePageUi.Card(searchPanel));
        var summary=Label("",13);var panel=new StackPanel();
        var toolbar=new WrapPanel();toolbar.Children.Add(Button("打开文件夹",()=>OpenFolder("schematics")));toolbar.Children.Add(Button("刷新",()=>Render()));toolbar.Children.Add(Button("预览扩展",ShowSchematicExtension));toolbar.Children.Add(Button("预览材质",()=>ShowSchematicMaterials()));toolbar.Margin=new Thickness(0,12,0,0);searchPanel.Children.Add(toolbar);
        Grid.SetRow(summary,2);grid.Children.Add(summary);
        var scroll=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetRow(scroll,3);grid.Children.Add(scroll);
        async void Render()
        {
            int request=++searchGeneration;panel.Children.Clear();string query=search.Text.Trim();summary.Text="按文件夹浏览 · .litematic";
            try
            {
                string root=_files.Folder("schematics");
                if(!Directory.Exists(root)){panel.Children.Add(Label("暂无原理图，可将文件放入 schematics 文件夹。",14));return;}
                if(query.Length==0){AddDirectory(root,panel,0);if(panel.Children.Count==0)panel.Children.Add(Label("此文件夹暂无原理图。",14));return;}
                summary.Text="正在搜索…";
                var matches=await Task.Run(()=>FindSchematics(root,query,0).ToArray());
                if(Closed||pageGeneration!=_generation||request!=searchGeneration)return;
                panel.Children.Clear();summary.Text=$"搜索结果 {matches.Length}";
                foreach(string path in matches)AddSchematicRow(path,panel);
                if(matches.Length==0)panel.Children.Add(Label("没有匹配的原理图。",14));
            }
            catch(Exception ex){if(!Closed&&pageGeneration==_generation&&request==searchGeneration)_status.Text="读取失败："+ex.Message;}
        }
        search.TextChanged+=(_,_)=>Render();Render();
    }
    private IEnumerable<string> FindSchematics(string directory,string query,int depth)
    {
        if(depth>24||Closed)return Array.Empty<string>();directory=_files.Folder(directory);
        var result=Directory.GetFiles(directory).Where(p=>p.EndsWith(".litematic",StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(p).IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)==0).ToList();
        foreach(string child in Directory.GetDirectories(directory))if((File.GetAttributes(child)&FileAttributes.ReparsePoint)==0)result.AddRange(FindSchematics(child,query,depth+1));
        return result.OrderBy(Path.GetFileName,StringComparer.OrdinalIgnoreCase);
    }
    private void AddDirectory(string directory,StackPanel target,int depth)
    {
        directory=_files.Folder(directory);
        if(depth>24){target.Children.Add(Label("目录层级过深，请在文件夹中查看。",12));return;}
        foreach(string child in Directory.GetDirectories(directory).OrderBy(Path.GetFileName,StringComparer.OrdinalIgnoreCase))
        {
            if((File.GetAttributes(child)&FileAttributes.ReparsePoint)!=0)continue;
            var inner=new StackPanel{Margin=new Thickness(14,0,0,0)};var expander=new Expander{Header=Path.GetFileName(child),Content=inner,Margin=new Thickness(4),IsExpanded=false,Style=SchematicFolderStyle()};target.Children.Add(expander);bool loaded=false;
            expander.Expanded+=(_,_)=>{if(loaded)return;loaded=true;Try(()=>AddDirectory(child,inner,depth+1));};
        }
        foreach(string path in Directory.GetFiles(directory).Where(p=>p.EndsWith(".litematic",StringComparison.OrdinalIgnoreCase)).OrderBy(Path.GetFileName,StringComparer.OrdinalIgnoreCase))
        {
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)continue;
            AddSchematicRow(path,target);
        }
    }
    private void AddSchematicRow(string path,StackPanel target)
    {
        var row=new Grid{MinHeight=76,Margin=new Thickness(12,4,8,4),Focusable=true,Background=Brushes.Transparent};row.ColumnDefinitions.Add(new(){Width=new GridLength(56)});row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var icon=new Border{Width=40,Height=40,CornerRadius=new CornerRadius(5),Child=new TextBlock{Text="\uE8A5",FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=22,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center}};icon.SetResourceReference(Border.BackgroundProperty,"AccentSoftBrush");icon.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,"AccentBrush");row.Children.Add(icon);
        var title=RowText(Path.GetFileName(path),14,false);title.FontWeight=FontWeights.SemiBold;title.VerticalAlignment=VerticalAlignment.Center;title.Margin=new Thickness(0,0,12,0);Grid.SetColumn(title,1);row.Children.Add(title);
        var preview=Button("快速预览",()=>OpenSchematicPreview(path));preview.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(preview,2);row.Children.Add(preview);
        void Reveal(bool show){preview.Opacity=show?1:0;preview.IsHitTestVisible=show;preview.IsTabStop=show;}
        Reveal(false);row.MouseEnter+=(_,_)=>Reveal(true);row.MouseLeave+=(_,_)=>Reveal(row.IsKeyboardFocusWithin);row.GotKeyboardFocus+=(_,_)=>Reveal(true);row.LostKeyboardFocus+=(_,_)=>Reveal(row.IsMouseOver);
        var card=new Border{Child=row,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(6),Margin=new Thickness(0,0,0,10)};card.SetResourceReference(Border.BackgroundProperty,"PanelBrush");card.SetResourceReference(Border.BorderBrushProperty,"PanelBorderBrush");target.Children.Add(card);
    }
    private static Style SchematicFolderStyle()=>(Style)System.Windows.Markup.XamlReader.Parse(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Expander'>
 <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Expander'><StackPanel>
  <ToggleButton IsChecked='{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}' Content='{TemplateBinding Header}' FontSize='14' FontWeight='SemiBold' Foreground='{DynamicResource TextBrush}' HorizontalContentAlignment='Stretch'>
   <ToggleButton.Template><ControlTemplate TargetType='ToggleButton'><Border x:Name='Surface' Background='{DynamicResource PanelBrush}' BorderBrush='{DynamicResource PanelBorderBrush}' BorderThickness='1' CornerRadius='5' Padding='12,14'><Grid><Grid.ColumnDefinitions><ColumnDefinition Width='28'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions><TextBlock x:Name='Arrow' Text='›' VerticalAlignment='Center'/><ContentPresenter Grid.Column='1' VerticalAlignment='Center'/></Grid></Border><ControlTemplate.Triggers><Trigger Property='IsChecked' Value='True'><Setter TargetName='Arrow' Property='Text' Value='⌄'/></Trigger><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Background' Value='{DynamicResource AccentSoftBrush}'/></Trigger></ControlTemplate.Triggers></ControlTemplate></ToggleButton.Template>
  </ToggleButton>
  <ContentPresenter x:Name='Items' Content='{TemplateBinding Content}' Visibility='Collapsed' Margin='0,4,0,0'/>
 </StackPanel><ControlTemplate.Triggers><Trigger Property='IsExpanded' Value='True'><Setter TargetName='Items' Property='Visibility' Value='Visible'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
</Style>");
    private static TextBlock Label(string text,double size)
    {var label=InstancePageUi.Text(text,size,size<=13);label.Margin=new Thickness(0,0,0,10);label.VerticalAlignment=VerticalAlignment.Center;if(size>=18)label.FontWeight=FontWeights.SemiBold;return label;}
    private static TextBlock RowText(string text,double size,bool muted)
    {var label=new TextBlock{Text=text,ToolTip=text,FontSize=size,Margin=new Thickness(0,2,0,2),TextTrimming=TextTrimming.CharacterEllipsis,TextWrapping=TextWrapping.NoWrap};label.SetResourceReference(TextBlock.ForegroundProperty,muted?"MutedTextBrush":"TextBrush");return label;}
    private static Button Button(string text,Action action)
    {
        var button=InstancePageUi.Button(text,action);
        button.FontSize=13;
        button.Padding=new Thickness(10,0,10,0);
        if(text is "删除" or "删除所选" or "移除")button.SetResourceReference(ForegroundProperty,"DangerBrush");
        return button;
    }
}
