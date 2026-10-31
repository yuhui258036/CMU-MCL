using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CMUmcl.Update;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        string stage = Path.GetFullPath(args[0]);
        string? backup = null;
        bool applied = false;
        try
        {
            var job = JsonSerializer.Deserialize<UpdateJob>(File.ReadAllBytes(Path.Combine(stage, "job.json")), UpdateProtocol.JsonOptions)
                ?? throw new InvalidDataException("缺少更新任务。");
            var install = Path.GetFullPath(job.InstallDirectory);
            if (job.DataDirectory is not null && !Path.IsPathFullyQualified(job.DataDirectory))
                throw new InvalidDataException("重启数据目录必须为绝对路径。");
            if (stage.StartsWith(install.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("暂存目录不能位于程序安装目录内。");
            var release = UpdateProtocol.ReadRelease(File.ReadAllBytes(Path.Combine(stage, "latest.json")));
            if (UpdateProtocol.ParseVersion(release.Version) <= UpdateProtocol.ParseVersion(job.CurrentVersion))
                throw new InvalidDataException("禁止安装相同或更旧版本。");
            var archive = Path.Combine(stage, "package.zip");
            UpdateProtocol.VerifyArchive(archive, release);
            var extracted = Path.Combine(stage, "apply-" + Guid.NewGuid().ToString("N"));
            var files = UpdateProtocol.ExtractArchive(archive, extracted);
            if (AssemblyName.GetAssemblyName(Path.Combine(extracted, "CMUmcl.dll")).Version != UpdateProtocol.ParseVersion(release.Version))
                throw new InvalidDataException("包内版本与更新版本不符。");

            WaitForParent(job);
            using var gate = new Mutex(false, UpdateProtocol.InstallLockName(install));
            bool locked;
            try { locked = gate.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { locked = true; }
            if (!locked) throw new IOException("另一个客户端仍在运行，请关闭后重新更新。");
            try
            {
                // Reject stale jobs if another updater already changed the installation.
                var current = AssemblyName.GetAssemblyName(Path.Combine(install, "CMUmcl.dll")).Version;
                if (current != UpdateProtocol.ParseVersion(job.CurrentVersion))
                    throw new IOException("安装目录版本已变化，请重新打开客户端检查更新。");
                var backups = Path.Combine(install, ".update-backups");
                UpdateTransaction.AssertSafeTarget(install, ".update-backups");
                backup = Path.Combine(backups, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
                UpdateTransaction.Apply(install, extracted, files, backup);
                applied = true;
            }
            finally { gate.ReleaseMutex(); }
            File.WriteAllText(Path.Combine(stage, "result.txt"), "更新成功。备份：" + backup);
            var restart = new ProcessStartInfo(Path.Combine(install, "CMUmcl.exe")) { UseShellExecute = true, WorkingDirectory = install };
            if (job.DataDirectory is not null)
            {
                restart.ArgumentList.Add("--data-directory");
                restart.ArgumentList.Add(job.DataDirectory);
            }
            Process.Start(restart);
            return 0;
        }
        catch (Exception ex)
        {
            var message = (applied ? "更新已安装，但自动重启失败，请手动打开客户端：" : "客户端更新未完成：") + ex.Message + "\n\n日志和暂存目录：" + stage +
                (backup is null ? "" : "\n备份目录：" + backup);
            try { File.WriteAllText(Path.Combine(stage, "result.txt"), ex.ToString()); } catch { }
            MessageBox.Show(message, "CMUmcl 更新", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void WaitForParent(UpdateJob job)
    {
        Process parent;
        try { parent = Process.GetProcessById(job.ParentPid); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            if (parent.StartTime.ToUniversalTime().Ticks != job.ParentStartTicks) return;
            if (!parent.WaitForExit(120_000)) throw new IOException("客户端未退出，已取消替换。");
        }
    }
}
