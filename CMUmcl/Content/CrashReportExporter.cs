// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.IO.Compression;
using System.Text;

namespace CMUmcl.Content;

/// <summary>What the launcher knew about a game run that ended abnormally.</summary>
internal sealed class CrashContext
{
    public string InstanceName { get; init; } = "";
    public string GameDirectory { get; init; } = "";
    public string? GameVersion { get; init; }
    public string? Loader { get; init; }
    public string? LoaderVersion { get; init; }
    public string? VersionName { get; init; }
    public string? JavaPath { get; init; }
    public int? JavaMajor { get; init; }
    public int? ExitCode { get; set; }
    public string? Hint { get; set; }
    public DateTime ExitedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// Packs the files people ask for when a game crashes: latest.log, the newest crash report,
/// the newest JVM fatal error log and a short environment summary. Known secrets are masked.
/// </summary>
internal static class CrashReportExporter
{
    private const int MaxLogBytes = 4 * 1024 * 1024;
    internal const string Masked = "<已隐藏>";

    internal static string DefaultFileName(CrashContext context)
    {
        string name = new string(context.InstanceName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        if (name.Length == 0) name = "实例";
        return $"CMUmcl-崩溃报告-{name}-{context.ExitedAt:yyyyMMdd-HHmmss}.zip";
    }

    internal static void Export(CrashContext context, string target, IEnumerable<string?> secrets)
    {
        string[] masks = secrets.Where(s => !string.IsNullOrEmpty(s) && s!.Length >= 8).Select(s => s!).Distinct().ToArray();
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                AddText(zip, "launcher-info.txt", Describe(context));
                AddLog(zip, Path.Combine(context.GameDirectory, "logs", "latest.log"), "logs/latest.log", masks);
                if (Newest(Path.Combine(context.GameDirectory, "crash-reports"), "*.txt") is { } crash)
                    AddLog(zip, crash, "crash-reports/" + Path.GetFileName(crash), masks);
                if (Newest(context.GameDirectory, "hs_err_pid*.log") is { } fatal)
                    AddLog(zip, fatal, Path.GetFileName(fatal), masks);
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static string Describe(CrashContext context)
    {
        var text = new StringBuilder();
        text.AppendLine("CMUmcl 崩溃报告");
        text.AppendLine("启动器版本：" + CMUmcl.Update.LauncherUpdateClient.CurrentVersion.ToString(3));
        text.AppendLine("系统：" + Environment.OSVersion + (Environment.Is64BitOperatingSystem ? " x64" : " x86"));
        text.AppendLine("实例：" + context.InstanceName);
        text.AppendLine("Minecraft：" + (context.GameVersion ?? "未知"));
        text.AppendLine("加载器：" + (string.IsNullOrWhiteSpace(context.Loader) ? "原版" : context.Loader + " " + context.LoaderVersion));
        if (!string.IsNullOrWhiteSpace(context.VersionName)) text.AppendLine("版本名：" + context.VersionName);
        text.AppendLine("Java：" + (context.JavaMajor is int major ? $"Java {major}（{context.JavaPath}）" : "未知"));
        text.AppendLine("退出代码：" + (context.ExitCode?.ToString() ?? "未知"));
        if (!string.IsNullOrWhiteSpace(context.Hint)) text.AppendLine("诊断提示：" + context.Hint);
        text.AppendLine("退出时间：" + context.ExitedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        string mods = Path.Combine(context.GameDirectory, "mods");
        if (Directory.Exists(mods))
        {
            var files = Directory.EnumerateFiles(mods).Select(Path.GetFileName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            text.AppendLine();
            text.AppendLine($"模组（{files.Length}）：");
            foreach (string? file in files) text.AppendLine("  " + file);
        }
        return text.ToString();
    }

    private static string? Newest(string directory, string pattern)
    {
        if (!Directory.Exists(directory)) return null;
        return new DirectoryInfo(directory).EnumerateFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
    }

    private static void AddLog(ZipArchive zip, string path, string entry, string[] masks)
    {
        if (!File.Exists(path)) return;
        // Keep the tail of very large logs: the failure is normally at the end.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxLogBytes) stream.Seek(-MaxLogBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        string content = reader.ReadToEnd();
        foreach (string mask in masks) content = content.Replace(mask, Masked);
        AddText(zip, entry, content);
    }

    private static void AddText(ZipArchive zip, string entry, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(entry, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
