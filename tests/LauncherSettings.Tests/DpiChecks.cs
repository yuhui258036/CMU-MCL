using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using CMUmcl;
using CMUmcl.Appearance;
using CMUmcl.Auth;
using CMUmcl.Content;

internal static partial class Program
{
    private static bool _dpiOnly;
    [DllImport("user32.dll")]private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool AreDpiAwarenessContextsEqual(IntPtr first,IntPtr second);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetWindowRect(IntPtr window,out NativeRect rect);
    [StructLayout(LayoutKind.Sequential)]private struct NativeRect {internal int Left,Top,Right,Bottom;}
    private static async Task DpiChecks(MainWindow window,SettingsStore store,YggdrasilClient auth,AuthStore authStore)
    {
        var handle=new WindowInteropHelper(window).Handle;
        var native=new { Dpi=GetDpiForWindow(handle),PerMonitorV2=AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(handle),new IntPtr(-4)),
            MonitorWorkArea=new {SystemParameters.WorkArea.Width,SystemParameters.WorkArea.Height},Screens=System.Windows.Forms.Screen.AllScreens.Select(s=>new {s.DeviceName,Width=s.Bounds.Width,Height=s.Bounds.Height}).ToArray() };
        File.WriteAllText(Path.Combine(_output,"native-dpi.json"),JsonSerializer.Serialize(native));
        Check(native.PerMonitorV2,"test host really runs Per-Monitor V2 on current monitor");
        Check(DpiWindowBehavior.ConstrainPosition(-13,1024,0,1024)==0,"guest 125 percent negative startup position is constrained");
        Check(DpiWindowBehavior.ConstrainPosition(-40,1600,0,1600)==0,"guest 200 percent negative startup position is constrained");
        Check(DpiWindowBehavior.ConstrainPosition(400,800,0,1000)==200,"right overflow is constrained");
        Check(DpiWindowBehavior.ConstrainPosition(-2000,800,-1920,0)==-1920,"negative-coordinate monitor origin is respected");
        Check(DpiWindowBehavior.ConstrainPosition(-900,800,-1920,0)==-900,"valid negative monitor position is retained");
        Check(DpiWindowBehavior.ConstrainPosition(0,800,100,1100)==100,"offset work area is respected");
        File.WriteAllText(Path.Combine(_output,"baseline-capacity.json"),JsonSerializer.Serialize(new {OriginalMinimumWidth=980,OriginalMinimumHeight=620,WidthAt200=960,HeightAt200=500,Fits200Percent=false,Mode="Original XAML capacity calculation for 1920x1000 physical work area"}));
        var results=new List<object>();
        foreach(double scale in new[]{1d,1.25,1.5,2d})
        foreach(var theme in new[]{LauncherTheme.Light,LauncherTheme.Dark})
        {
            var settings=store.Load();settings.Theme=theme;store.Save(settings);await AppearanceManager.ApplyAsync(settings);
            double availableWidth=1920/scale,availableHeight=1000/scale;
            DpiWindowBehavior.Fit(window,availableWidth,availableHeight);
            window.Width=Math.Min(1200,availableWidth);window.Height=Math.Min(800,availableHeight);
            await Task.Delay(30);window.UpdateLayout();
            Check(window.ActualWidth<=availableWidth+1 && window.ActualHeight<=availableHeight+1,"main window fits simulated "+scale+" work area in "+theme);
            // Native rectangles are physical pixels; the simulated work area above is in WPF DIPs.
            double nativeScale=GetDpiForWindow(handle)/96d;
            Check(GetWindowRect(handle,out var nativeRect) && nativeRect.Right-nativeRect.Left<=Math.Ceiling(availableWidth*nativeScale)+1 && nativeRect.Bottom-nativeRect.Top<=Math.Ceiling(availableHeight*nativeScale)+1,"native frame fits simulated "+scale+" work area in "+theme);
            foreach(string page in new[]{"PacksNavButton","InstancesNavButton","RuntimeTab","AppearanceTab","ProxyTab","StorageTab","UpdateTab"})
            {
                if(page.EndsWith("Tab"))Click(window,"SettingsNavButton");
                Click(window,page);window.UpdateLayout();
                string file=$"dpi-{(int)(scale*100)}-{theme}-{page}.png";RenderScaled(window,scale,Path.Combine(_output,file));
                string expected=page.EndsWith("Tab")?"SettingsContentScroll":page=="PacksNavButton"?"CardsScroll":"InstancesScroll";
                var scroll=Get<ScrollViewer>(window,expected);
                // Empty instance view replaces its collapsed scroll area with an empty-state message.
                if(page!="InstancesNavButton")
                {
                    Check(scroll.IsVisible && scroll.ViewportWidth>0 && scroll.ViewportHeight>0,"visible viewport at "+scale+" "+theme+" "+page);
                    var bounds=scroll.TransformToAncestor(window).TransformBounds(new Rect(scroll.RenderSize));
                    Check(bounds.Right<=window.ActualWidth+1 && bounds.Bottom<=window.ActualHeight+1,"viewport stays inside window at "+scale+" "+theme+" "+page);
                }
                results.Add(new {Scale=scale,Theme=theme.ToString(),Page=page,Width=window.ActualWidth,Height=window.ActualHeight,File=file,Mode="Offscreen viewport/raster simulation, not OS DPI switching"});
            }
            var login=new LoginWindow(auth,authStore){ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-16000,Top=-16000};
            login.Show();DpiWindowBehavior.Fit(login,availableWidth,availableHeight);login.UpdateLayout();
            Get<Button>(login,"LoginButton").BringIntoView();login.UpdateLayout();
            Check(login.ActualWidth<=availableWidth+1 && login.ActualHeight<=availableHeight+1,"login fits simulated "+scale+" "+theme);
            var button=Get<Button>(login,"LoginButton");var rectangle=button.TransformToAncestor(login).TransformBounds(new Rect(button.RenderSize));
            Check(rectangle.Top>=39 && rectangle.Bottom<=login.ActualHeight+1,"login action reachable at "+scale+" "+theme);
            RenderScaled(login,scale,Path.Combine(_output,$"dpi-{(int)(scale*100)}-{theme}-login.png"));login.Close();
        }
        DpiWindowBehavior.Fit(window,1920,1000);
        Check(window.MinWidth==980 && window.MinHeight==620,"original minimum constraints restored on larger work area");
        File.WriteAllText(Path.Combine(_output,"dpi-matrix.json"),JsonSerializer.Serialize(results));
        File.WriteAllText(Path.Combine(_output,"result.txt"),"PASS "+_checks+" DPI/layout checks; only native-dpi.json represents actual device DPI");
        Console.WriteLine("PASS "+_checks+" DPI/layout checks");
    }
    private static void RenderScaled(Window window,double scale,string file)
    {
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth*scale),(int)Math.Ceiling(window.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(file);encoder.Save(output);
    }
}
