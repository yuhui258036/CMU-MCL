// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using CmlLib.Core.ProcessBuilder;

namespace CMUmcl.Content
{
    public sealed class InstanceOptions
    {
        public string[] SharedSettings { get; set; } = Array.Empty<string>();
        public string? Name { get; set; }
        public string? Icon { get; set; }
        public JavaInstanceOptions? Java { get; set; }
        public WindowInstanceOptions? Window { get; set; }
        public string[]? JvmArguments { get; set; }
        // null preserves the grouped overrides used by older launcher versions.
        public string[]? OverrideProperties { get; set; }
    }
    public sealed class JavaInstanceOptions
    {
        public string? Path { get; set; }
        public bool AutoMemory { get; set; } = true;
        public int MemoryMb { get; set; } = 4096;
    }
    public sealed class WindowInstanceOptions
    {
        public int Width { get; set; } = 854;
        public int Height { get; set; } = 480;
        public bool FullScreen { get; set; }
    }
    internal static class InstanceOptionsStore
    {
        private static readonly JsonSerializerOptions Json = new() { WriteIndented=true,PropertyNameCaseInsensitive=true };
        internal static readonly string[] Icons = { "grass-block.png", "Fabric.png", "Forge.png", "NeoForge.png", "Quilt.png", "OptiFine.png" };
        internal static readonly string[] OverrideKeys = { "java", "memory", "windowMode", "windowSize", "arguments" };
        internal static bool UsesOverride(InstanceOptions value, string key) => value.OverrideProperties != null
            ? value.OverrideProperties.Contains(key)
            : key switch { "java" or "memory" => value.Java != null, "windowMode" or "windowSize" => value.Window != null, "arguments" => value.JvmArguments != null, _ => false };
        private static string FilePath(string directory) => System.IO.Path.Combine(directory,"launcher-settings.json");
        internal static InstanceOptions Load(string directory)
        {
            if(!File.Exists(FilePath(directory))) return new();
            var file=new FileInfo(FilePath(directory));if(file.Length>65536)throw new InvalidDataException("实例设置文件过大。");
            var value=JsonSerializer.Deserialize<InstanceOptions>(File.ReadAllText(file.FullName),Json) ?? throw new InvalidDataException("实例设置格式无效。");
            Validate(value);return value;
        }
        internal static void Validate(InstanceOptions value)
        {
            if(value.OverrideProperties is { } keys && (keys.Any(x=>!OverrideKeys.Contains(x)) || keys.Distinct().Count()!=keys.Length))throw new InvalidDataException("实例覆盖设置无效。");
            if(((UsesOverride(value,"java") || UsesOverride(value,"memory")) && value.Java==null) || ((UsesOverride(value,"windowMode") || UsesOverride(value,"windowSize")) && value.Window==null) || (UsesOverride(value,"arguments") && value.JvmArguments==null))throw new InvalidDataException("实例覆盖设置缺少对应值。");
            if(value.SharedSettings==null || value.SharedSettings.Any(x=>!InstanceSharing.Names.Contains(x)) || value.SharedSettings.Distinct().Count()!=value.SharedSettings.Length)throw new InvalidDataException("同步项目无效。");
            if(value.Name!=null && (value.Name.Length>80 || value.Name.Any(char.IsControl)))throw new InvalidDataException("实例名称不能超过80字或包含控制字符。");
            if(value.Icon!=null && !Icons.Contains(value.Icon))throw new InvalidDataException("实例图标无效。");
            if(value.Java!=null){if(value.Java.MemoryMb<512 || value.Java.MemoryMb>1048576)throw new InvalidDataException("内存范围为512～1048576 MiB。");if(!string.IsNullOrWhiteSpace(value.Java.Path) && !Compat.IsPathFullyQualified(value.Java.Path))throw new InvalidDataException("Java请使用绝对路径或留空自动选择。");}
            if(value.Window!=null && (value.Window.Width<320 || value.Window.Width>16384 || value.Window.Height<240 || value.Window.Height>16384))throw new InvalidDataException("窗口尺寸范围：宽320～16384，高240～16384。");
            ValidateArguments(value.JvmArguments);
        }
        internal static void ValidateArguments(string[]? args)
        {
            if(args==null)return;
            if(args.Length>128 || args.Sum(x=>x?.Length??0)>16384)throw new InvalidDataException("启动参数过多。");
            foreach(var arg in args)
            {
                if(string.IsNullOrWhiteSpace(arg) || arg.Any(char.IsControl) || !arg.StartsWith("-"))throw new InvalidDataException("每行填写一个完整JVM参数，不能包含控制字符。");
                if(new[]{"-Xmx","-Xms","-cp","-classpath","--class-path","-jar","-Djava.class.path="}.Any(x=>arg.StartsWith(x,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("内存和类路径由启动器管理，请勿在额外参数中覆盖。");
            }
        }
        internal static void Save(string directory,InstanceOptions options)
        {
            Validate(options);
            if(!Directory.Exists(directory))throw new DirectoryNotFoundException();
            var path=FilePath(directory);var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{File.WriteAllText(temp,JsonSerializer.Serialize(options,Json));if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
        internal static AppSettings Resolve(AppSettings global,InstanceOptions options)
        {
            Validate(options);
            var result=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(global))!;
            if(UsesOverride(options,"java"))result.JavaPath=options.Java!.Path;
            if(UsesOverride(options,"memory")){result.AutoMemory=options.Java!.AutoMemory;result.MaxMemoryMb=options.Java.MemoryMb;}
            if(UsesOverride(options,"windowMode"))result.FullScreen=options.Window!.FullScreen;
            if(UsesOverride(options,"windowSize")){result.WindowWidth=options.Window!.Width;result.WindowHeight=options.Window.Height;}
            if(UsesOverride(options,"arguments"))result.ExtraJvmArguments=options.JvmArguments!.ToArray();
            ValidateArguments(result.ExtraJvmArguments);
            if(result.WindowWidth<320 || result.WindowHeight<240 || result.WindowWidth>16384 || result.WindowHeight>16384)throw new InvalidDataException("游戏窗口尺寸无效，请检查全局或实例设置。");
            return result;
        }
        internal static void ApplyLaunchOptions(MLaunchOption option,AppSettings settings)
        {
            option.ScreenWidth=settings.WindowWidth;option.ScreenHeight=settings.WindowHeight;option.FullScreen=settings.FullScreen;
            option.ExtraJvmArguments=(option.ExtraJvmArguments??Array.Empty<MArgument>()).Concat((settings.ExtraJvmArguments??Array.Empty<string>()).Select(x=>new MArgument(x))).ToArray();
        }
    }
}
