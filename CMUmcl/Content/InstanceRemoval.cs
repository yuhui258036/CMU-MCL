// SPDX-License-Identifier: GPL-3.0-only
using System.IO;

namespace CMUmcl.Content;

/// <summary>
/// Removes an instance from the launcher: its folder goes to the Windows Recycle Bin (so a
/// mistaken delete can be undone), then its record is dropped. Only folders inside the
/// instances directory are touched; shared game directories and libraries are left alone.
/// </summary>
internal static class InstanceRemoval
{
    /// <summary>Test hook replacing the Recycle Bin move (path → throws on failure).</summary>
    internal static Action<string>? RecycleOverride { get; set; }

    internal static void Remove(InstalledStateStore installed, string instanceId, string instanceDirectory)
    {
        string full = Path.GetFullPath(instanceDirectory);
        string root = Path.GetFullPath(LauncherPaths.InstancesDirectory).TrimEnd('\\') + "\\";
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("实例目录不在启动器的实例目录内，未删除任何文件。");
        if (Directory.Exists(full))
        {
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("实例目录是链接，未删除任何文件。");
            InstanceMaintenance.EnsureStopped(full);
            if (RecycleOverride != null) RecycleOverride(full);
            else Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(full,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        if (!installed.RemoveRecord(instanceId))
            throw new IOException("实例文件已移到回收站，但无法更新实例列表，请检查数据目录权限。");
    }
}
