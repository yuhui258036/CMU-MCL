using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace CMUmcl;

/// <summary>
/// Modal message in the launcher's own style: it covers the owner with the same dimmed backdrop
/// and popup card as the in-window prompt, follows the current theme, and keeps the synchronous
/// MessageBox-like contract callers rely on.
/// </summary>
internal static class LauncherPrompt
{
    internal static Func<Window,string,string,MessageBoxButton,MessageBoxResult>? ShowOverride;
    internal static MessageBoxResult Show(Window owner,string message,string title="提示",MessageBoxButton buttons=MessageBoxButton.OK,MessageBoxImage image=MessageBoxImage.None,ImageSource? picture=null)
    {
        if(ShowOverride!=null)return ShowOverride(owner,message,title,buttons);
        MessageBoxResult result=buttons switch{MessageBoxButton.YesNo=>MessageBoxResult.No,MessageBoxButton.OK=>MessageBoxResult.OK,_=>MessageBoxResult.Cancel};
        var dialog=new Window
        {
            Title=title,Owner=owner,WindowStyle=WindowStyle.None,AllowsTransparency=true,Background=Brushes.Transparent,
            ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,FontFamily=owner.FontFamily,UseLayoutRounding=true,SnapsToDevicePixels=true,
        };
        dialog.Resources.MergedDictionaries.Add(owner.Resources);
        dialog.SetResourceReference(Control.ForegroundProperty,"TextBrush");

        bool cover=TryGetClientBounds(owner,out Rect bounds);
        if(cover){dialog.WindowStartupLocation=WindowStartupLocation.Manual;dialog.Left=bounds.Left;dialog.Top=bounds.Top;dialog.Width=bounds.Width;dialog.Height=bounds.Height;}
        else{dialog.SizeToContent=SizeToContent.WidthAndHeight;dialog.WindowStartupLocation=WindowStartupLocation.CenterOwner;}

        var card=new Border
        {
            Width=480,MaxWidth=Math.Max(280,(cover?bounds.Width:480)-48),Margin=new Thickness(24),Padding=new Thickness(24),
            HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(6),
            Effect=new DropShadowEffect{BlurRadius=24,ShadowDepth=4,Opacity=0.22,Color=Colors.Black},
        };
        card.SetResourceReference(Border.BackgroundProperty,"PopupBrush");
        card.SetResourceReference(Border.BorderBrushProperty,"PanelBorderBrush");
        var content=new StackPanel();card.Child=content;
        content.Children.Add(new TextBlock{Text=title,FontSize=20,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        var body=new TextBlock{Text=message,Margin=new Thickness(0,16,0,24),TextWrapping=TextWrapping.Wrap,LineHeight=21};
        body.SetResourceReference(TextBlock.ForegroundProperty,"TextBrush");
        content.Children.Add(body);
        if(picture!=null){body.Margin=new Thickness(0,16,0,12);content.Children.Add(new Image{Source=picture,Stretch=Stretch.Uniform,Margin=new Thickness(0,0,0,24),HorizontalAlignment=HorizontalAlignment.Left,MaxHeight=140});}
        var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};content.Children.Add(actions);

        Button? first=null;
        void Add(string label,MessageBoxResult value,bool primary,bool isDefault,bool isCancel)
        {
            var button=new Button{Content=label,MinWidth=80,Margin=new Thickness(actions.Children.Count==0?0:10,0,0,0),IsDefault=isDefault,IsCancel=isCancel};
            button.SetResourceReference(FrameworkElement.StyleProperty,primary?"PrimaryButton":"SecondaryButton");
            button.Click+=(_,_)=>{result=value;dialog.Close();};
            actions.Children.Add(button);first??=button;
        }
        // Destructive confirmations keep the safe choice as the default, as before.
        if(buttons==MessageBoxButton.YesNo){Add("继续",MessageBoxResult.Yes,true,false,false);Add("取消",MessageBoxResult.No,false,true,true);}
        else if(buttons==MessageBoxButton.OKCancel){Add("确定",MessageBoxResult.OK,true,true,false);Add("取消",MessageBoxResult.Cancel,false,false,true);}
        else Add("确定",MessageBoxResult.OK,true,true,true);

        var backdrop=new Grid{Background=cover?new SolidColorBrush(Color.FromArgb(0x66,0,0,0)):Brushes.Transparent};
        backdrop.Children.Add(card);
        dialog.Content=backdrop;
        dialog.Loaded+=(_,_)=>(actions.Children.OfType<Button>().FirstOrDefault(b=>b.IsDefault)??first)?.Focus();
        dialog.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){dialog.Close();e.Handled=true;}};
        dialog.ShowDialog();
        return result;
    }

    /// <summary>For code without a window reference; runs on the UI thread and falls back to a system box only before any window exists.</summary>
    internal static MessageBoxResult ShowForApp(string message,string title="提示",MessageBoxButton buttons=MessageBoxButton.OK,MessageBoxImage image=MessageBoxImage.None)
    {
        var app=Application.Current;
        if(app==null)return MessageBox.Show(message,title,buttons,image);
        return app.Dispatcher.Invoke(()=>
        {
            var owner=app.Windows.OfType<Window>().FirstOrDefault(w=>w.IsActive && w.IsVisible) ?? app.Windows.OfType<MainWindow>().FirstOrDefault(w=>w.IsVisible) ?? app.MainWindow;
            return owner is {IsVisible:true} ? Show(owner,message,title,buttons,image) : MessageBox.Show(message,title,buttons,image);
        });
    }

    // Owner client area in device-independent units, so the backdrop lines up on scaled displays.
    private static bool TryGetClientBounds(Window owner,out Rect bounds)
    {
        bounds=Rect.Empty;
        try
        {
            if(!owner.IsVisible || owner.WindowState==WindowState.Minimized || owner.ActualWidth<=0 || owner.ActualHeight<=0)return false;
            var source=PresentationSource.FromVisual(owner);if(source?.CompositionTarget==null)return false;
            Point origin=source.CompositionTarget.TransformFromDevice.Transform(owner.PointToScreen(new Point(0,0)));
            bounds=new Rect(origin,new Size(owner.ActualWidth,owner.ActualHeight));
            return true;
        }
        catch{return false;}
    }
}
