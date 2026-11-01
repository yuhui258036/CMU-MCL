using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using CMUmcl.Content;

namespace CMUmcl.Appearance;

public static class AppearanceManager
{
    private static LauncherTheme _mode;
    private static double _opacity;
    private static bool _subscribed;
    private static BackgroundController? _background;
    private static readonly HashSet<Window> Windows = [];
    private static (bool Dark, bool Image)? _palette;
    private static (bool Dark, double Opacity, BitmapSource? Image)? _backdrop;
    public static bool IsDark { get; private set; }
    public static string? BackgroundError => _background?.Error;
    public static bool BackgroundLoading => _background?.IsLoading == true;
    public static bool HasBackground => _background?.Image != null;
    public static string? CurrentBackgroundPath => _background?.CurrentPath;
    public static event Action? BackgroundChanged;

    public static void Initialize(AppSettings settings)
    {
        _ = ApplyAsync(settings);
        if (_subscribed) return;
        _subscribed = true;
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        if (Application.Current != null) Application.Current.Exit += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
            _background?.Dispose(); _background = null;
            _subscribed = false;
        };
    }

    private static void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (_mode != LauncherTheme.System || dispatcher == null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(() => { Render(); BackgroundChanged?.Invoke(); });
    }

    public static Task ApplyAsync(AppSettings settings)
    {
        _mode = settings.Theme;
        _opacity = Math.Clamp(double.IsFinite(settings.BackgroundOpacity) ? settings.BackgroundOpacity : 0.25, 0.05, 1);
        if (_background == null)
        {
            _background = new BackgroundController(Path.Combine(LauncherPaths.CacheDirectory, "backgrounds"));
            _background.Changed += () => { Render(); BackgroundChanged?.Invoke(); };
        }
        Render();
        BackgroundChanged?.Invoke();
        return _background.ConfigureAsync(settings);
    }

    public static Task NextBackgroundAsync() => _background?.NextAsync() ?? Task.CompletedTask;

    // Pause only automatic wallpaper work; downloads, authentication and updates keep running.
    public static void TrackWindow(Window window)
    {
        if (!Windows.Add(window)) return;
        window.StateChanged += WindowStateChanged;
        window.IsVisibleChanged += WindowVisibilityChanged;
        window.Closed += WindowClosed;
        UpdateActivity();
    }
    private static void WindowStateChanged(object? sender, EventArgs e) => UpdateActivity();
    private static void WindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateActivity();
    private static void WindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        Windows.Remove(window);
        window.StateChanged -= WindowStateChanged;
        window.IsVisibleChanged -= WindowVisibilityChanged;
        window.Closed -= WindowClosed;
        UpdateActivity();
    }
    private static void UpdateActivity() => _background?.SetSuspended(!Windows.Any(w => w.IsVisible && w.WindowState != WindowState.Minimized));

    private static void Render()
    {
        var resources = Application.Current?.Resources;
        if (resources == null) return;
        IsDark = _mode == LauncherTheme.Dark || _mode == LauncherTheme.System && SystemUsesDark();
        var image = _background?.Image;
        var palette = (IsDark, image != null);
        if (_palette != palette)
        {
            var colors = new Dictionary<string, string>
            {
                ["BgBrush"] = IsDark ? "#1C1C1E" : "#FAFAFA",
                ["PanelBrush"] = IsDark ? "#2E2E32" : "#FFFFFF",
                ["SidebarBrush"] = IsDark ? "#232326" : "#F5F5F7",
                ["PopupBrush"] = IsDark ? "#2E2E32" : "#FFFFFF",
                ["PanelBorderBrush"] = IsDark ? "#45454B" : "#E4E4E9",
                ["SeparatorBrush"] = IsDark ? "#9696A3" : "#74747F",
                ["TextBrush"] = IsDark ? "#E4E4E7" : "#18181B",
                ["MutedTextBrush"] = IsDark ? "#C3C3CD" : "#50505B",
                ["AccentBrush"] = IsDark ? "#D8783A" : "#C2410C",
                ["AccentHoverBrush"] = IsDark ? "#E28A51" : "#9A3412",
                ["AccentPressedBrush"] = IsDark ? "#B86635" : "#7C2D12",
                ["AccentSoftBrush"] = IsDark ? "#47372E" : "#FFF1E6",
                ["ActionBrush"] = IsDark ? "#F4F4F5" : "#18181B",
                ["ActionTextBrush"] = IsDark ? "#18181B" : "#FFFFFF",
                ["HoverBrush"] = IsDark ? "#14FFFFFF" : "#100E3263",
                ["PressedBrush"] = "#19000000",
                ["ErrorBrush"] = IsDark ? "#FF9292" : "#B32E40",
                ["DangerBrush"] = IsDark ? "#FF9292" : "#B32E40",
                ["SuccessBrush"] = IsDark ? "#68D8A0" : "#187347",
                ["SuccessSoftBrush"] = IsDark ? "#203A30" : "#E5F5EC",
            };
            foreach (var pair in colors)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
                // WPF styles may freeze a resource as soon as it is published.
                if (image != null) brush.Opacity = pair.Key switch
                {
                    "PanelBrush" => IsDark ? 0.92 : 0.94,
                    "SidebarBrush" => IsDark ? 0.90 : 0.92,
                    "AccentSoftBrush" => 0.82,
                    _ => 1,
                };
                brush.Freeze();
                resources[pair.Key] = brush;
            }
            resources["PageOverlayBrush"] = image != null ? Brushes.Transparent : resources["BgBrush"];
            var aliases = new Dictionary<string, string>
            {
                ["CivBg"]="PageOverlayBrush", ["CivCard"]="PanelBrush", ["CivBorder"]="PanelBorderBrush",
                ["CivBorderStrong"]="PanelBorderBrush", ["CivText"]="TextBrush", ["CivTextMuted"]="MutedTextBrush",
                ["CivTextFaint"]="MutedTextBrush", ["CivAccent"]="AccentBrush", ["CivAccentSoft"]="AccentSoftBrush",
                ["CivAccentBorder"]="AccentSoftBrush", ["CivSuccess"]="SuccessBrush", ["CivSuccessSoft"]="SuccessSoftBrush",
                ["CivDanger"]="ErrorBrush", ["CivIconTile"]="PanelBrush",
            };
            foreach (var pair in aliases) resources[pair.Key] = resources[pair.Value];
            _palette = palette;
        }
        var backdrop = (IsDark, _opacity, image);
        if (_backdrop == backdrop) return;
        Brush background = (Brush)resources["BgBrush"];
        if (image != null)
        {
            var bounds = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(background, null, new RectangleGeometry(bounds)));
            group.Children.Add(new ImageDrawing(image, bounds));
            var wash = ((SolidColorBrush)background).Clone();
            wash.Opacity = 1 - _opacity;
            group.Children.Add(new GeometryDrawing(wash, null, new RectangleGeometry(bounds)));
            background = new DrawingBrush(group) { Stretch = Stretch.UniformToFill };
            background.Freeze();
        }
        resources["WindowBackdropBrush"] = background;
        _backdrop = backdrop;
    }

    private static bool SystemUsesDark()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0; }
        catch { return false; }
    }

    public static BitmapSource LoadImage(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("请选择本机图片文件。");
        using var stream = File.OpenRead(path);
        if (stream.Length > 20 * 1024 * 1024) throw new IOException("背景图片不能超过 20 MB。");
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        int width = decoder.Frames[0].PixelWidth, height = decoder.Frames[0].PixelHeight;
        if (width <= 0 || height <= 0 || (long)width * height > 100_000_000) throw new IOException("图片尺寸过大或无效。");
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        if (width >= height) bitmap.DecodePixelWidth = Math.Min(width, 1920);
        else bitmap.DecodePixelHeight = Math.Min(height, 1920);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static string ImportBackground(string source)
    {
        // Validate before touching the saved preference. Own the copy so moving the original is safe.
        LoadImage(source);
        string directory = Path.Combine(LauncherPaths.RootDirectory, "backgrounds");
        Directory.CreateDirectory(directory);
        using var stream = File.OpenRead(source);
        string stem = Path.GetFileNameWithoutExtension(source);
        stem = new string(stem.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).Take(40).ToArray());
        string name = stem + "_" + Convert.ToHexString(SHA256.HashData(stream))[..12] + Path.GetExtension(source).ToLowerInvariant();
        string target = Path.Combine(directory, name);
        if (!File.Exists(target)) File.Copy(source, target);
        return target;
    }
}
