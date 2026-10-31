using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CMUmcl.Update;

public sealed record UpdateJob(string InstallDirectory, int ParentPid, long ParentStartTicks, string CurrentVersion,
    string? DataDirectory = null);
public sealed record BackupEntry(string RelativePath, bool Existed);

public static class UpdateTransaction
{
    public static void AssertSafeTarget(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("目标路径越界。");
        // Walk through every existing parent; never follow junctions into data directories.
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新目标包含链接目录或文件：" + current);
            if (current.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    public static string Apply(string install, string extracted, string[] files, string backupRoot,
        Action<int>? beforeCopy = null)
    {
        if (Directory.Exists(backupRoot)) throw new IOException("备份目录已存在，请先检查前次更新结果。");
        var backupFiles = Path.Combine(backupRoot, "files");
        var entries = files.Select(f => new BackupEntry(f, File.Exists(Path.Combine(install, f)))).ToArray();
        long required = 0;
        long largest = 0;
        foreach (var entry in entries)
        {
            AssertSafeTarget(install, entry.RelativePath);
            long incoming = new FileInfo(Path.Combine(extracted, entry.RelativePath)).Length;
            required = checked(required + incoming + (entry.Existed ? new FileInfo(Path.Combine(install, entry.RelativePath)).Length : 0));
            largest = Math.Max(largest, incoming);
        }
        UpdateProtocol.EnsureFreeSpace(install, checked(required + largest));
        Directory.CreateDirectory(backupRoot);
        // Complete all backups before touching the installation. Keep a recovery journal for power loss.
        foreach (var entry in entries)
        {
            AssertSafeTarget(install, entry.RelativePath);
            AssertSafeTarget(backupFiles, entry.RelativePath);
            if (!entry.Existed) continue;
            var target = Path.Combine(backupFiles, entry.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(install, entry.RelativePath), target);
        }
        File.WriteAllText(Path.Combine(backupRoot, "recovery.json"), JsonSerializer.Serialize(entries, UpdateProtocol.JsonOptions));
        File.WriteAllText(Path.Combine(backupRoot, "status.txt"), "applying");
        int touched = 0;
        try
        {
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                AssertSafeTarget(install, entry.RelativePath);
                var target = Path.Combine(install, entry.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                beforeCopy?.Invoke(i);
                ReplaceFrom(Path.Combine(extracted, entry.RelativePath), target);
                touched = i + 1;
            }
            File.WriteAllText(Path.Combine(backupRoot, "status.txt"), "complete");
            return backupRoot;
        }
        catch (Exception applyError)
        {
            try
            {
                Restore(install, backupRoot, entries.Take(touched).Reverse());
                File.WriteAllText(Path.Combine(backupRoot, "status.txt"), "rolled-back");
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("更新和回滚均失败。请保留备份目录并按 recovery.json 恢复：" + backupRoot,
                    applyError, rollbackError);
            }
            throw new IOException("更新失败，已恢复旧客户端。备份：" + backupRoot, applyError);
        }
    }

    public static void Restore(string install, string backup, IEnumerable<BackupEntry> entries)
    {
        foreach (var entry in entries)
        {
            AssertSafeTarget(install, entry.RelativePath);
            var target = Path.Combine(install, entry.RelativePath);
            if (entry.Existed) ReplaceFrom(Path.Combine(backup, "files", entry.RelativePath), target);
            else if (File.Exists(target)) File.Delete(target);
        }
    }

    private static void ReplaceFrom(string source, string target)
    {
        var temporary = target + ".cmumcl-update-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary, overwrite: false);
            // The old file stays intact if a lock prevents the final rename.
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
