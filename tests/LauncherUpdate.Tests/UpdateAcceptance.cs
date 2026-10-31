using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CMUmcl.Update;

internal static class UpdateAcceptance
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newName, string existingName, IntPtr reserved);
    private delegate bool WindowCallback(IntPtr window, IntPtr argument);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr argument);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static IntPtr FindFixtureWindow(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint windowPid);
            if (windowPid != pid || !IsWindowVisible(window) || !IsWindowEnabled(window)) return true;
            var title = new System.Text.StringBuilder(256);
            GetWindowText(window, title, title.Capacity);
            if (!title.ToString().StartsWith("CMUmcl", StringComparison.Ordinal)) return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static async Task CloseNormallyAsync(Process process)
    {
        if (process.HasExited) return;
        // Retain a live handle for clients discovered after restart so ExitCode remains
        // queryable after the process disappears from the system process list.
        _ = process.SafeHandle;
        try { await Task.Run(() => process.WaitForInputIdle(10_000)); }
        catch (InvalidOperationException) { /* A console-hosted updater may create its dialog later. */ }
        var ready = Stopwatch.StartNew();
        IntPtr window = IntPtr.Zero;
        while (!process.HasExited)
        {
            process.Refresh();
            window = FindFixtureWindow(process.Id);
            if (window != IntPtr.Zero) break;
            if (ready.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("No closeable fixture window: " + process.Id);
            await Task.Delay(100);
        }
        if (process.HasExited) return;
        Console.WriteLine($"NORMAL CLOSE: pid={process.Id} hwnd={window} cached-hwnd={process.MainWindowHandle}");
        if (!PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "WM_CLOSE rejected for isolated fixture");
        // Use the actual process signal, rather than accepting a vanished window as an exit.
        if (!await Task.Run(() => process.WaitForExit(20_000)))
            throw new TimeoutException("Normal close did not terminate fixture process: " + process.Id);
        Console.WriteLine("NORMAL EXIT: " + process.Id + " code=" + process.ExitCode);
    }

    public static async Task<int> RunAsync()
    {
        var source = Path.GetFullPath(".");
        var root = Path.Combine(source, "outputs", "auto-update-acceptance-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        var ownedProcesses = new List<(int Id, long StartTicks)>();
        void Track(Process process) => ownedProcesses.Add((process.Id, process.StartTime.ToUniversalTime().Ticks));
        void Check(bool valid, string name)
        {
            if (!valid) throw new Exception("FAIL: " + name);
            checks.Add(name);
            Console.WriteLine("PASS: " + name);
        }
        var sdk = Environment.GetEnvironmentVariable("CMUMCL_DOTNET10") ?? Path.Combine(source, "work", "dotnet10", "dotnet.exe");
        var published = Path.Combine(source, "outputs", "net10-selfcontained");
        var baseline = Path.Combine(root, "baseline");
        var install = Path.Combine(root, "install");
        var data = Path.Combine(root, "isolated-data");
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256); // Never persisted.
        var report = Path.Combine(root, "report.json");

        async Task Command(string file, params string[] arguments)
        {
            var start = new ProcessStartInfo(file) { WorkingDirectory = source, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "false";
            start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var log = await stdout + await stderr;
            await File.AppendAllTextAsync(Path.Combine(root, "build.log"), log);
            Console.WriteLine(log);
            if (process.ExitCode != 0) throw new Exception("Build command failed: " + file);
        }
        async Task Publish(string version)
        {
            Console.WriteLine("Publishing .NET 10 self-contained " + version);
            await Command(sdk, "publish", "CMUmcl/CMUmcl.csproj", "--no-restore", "-c", "Release", "-r", "win-x64",
                "--self-contained", "true", "-p:Version=" + version, "-p:SkipUpdaterBuildCopy=true",
                "-p:UseSharedCompilation=false", "-p:TargetPlatformMoniker=Windows%2cVersion=7.0", "-o", published);
        }
        void CleanOwned(string path)
        {
            var absolute = Path.GetFullPath(path);
            if (!absolute.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Refusing cleanup outside acceptance workspace: " + absolute);
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
        }
        static byte[] Hash(string path)
        {
            using var stream = File.OpenRead(path);
            return SHA256.HashData(stream);
        }
        UpdateSignature Sign(byte[] hash) => new("ECDSA-P256-SHA256", "ieee-p1363-base64",
            Convert.ToBase64String(signingKey.SignHash(hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        async Task WaitUntil(Func<bool> condition, int seconds, string description)
        {
            var timeout = Stopwatch.StartNew();
            while (!condition())
            {
                if (timeout.Elapsed.TotalSeconds > seconds) throw new TimeoutException(description);
                await Task.Delay(150);
            }
        }
        Process StartClient()
        {
            var start = new ProcessStartInfo(Path.Combine(install, "CMUmcl.exe")) { UseShellExecute = false,
                WorkingDirectory = install, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--data-directory");
            start.ArgumentList.Add(data);
            var process = Process.Start(start)!;
            Track(process);
            return process;
        }
        async Task CloseClient(Process process)
        {
            await CloseNormallyAsync(process);
        }
        Process StartUpdater(string stage, string dll)
        {
            var start = new ProcessStartInfo(sdk) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = stage };
            start.ArgumentList.Add(dll);
            start.ArgumentList.Add(stage);
            var process = Process.Start(start)!;
            Track(process);
            return process;
        }

        try
        {
            UpdateProtocol.EnsureFreeSpace(root, 2L * 1024 * 1024 * 1024);
            await Publish("0.1.0");
            var coreFiles = Directory.EnumerateFiles(published, "CMUmcl.*", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(Path.Combine(published, "updater"), "CMUmcl.Updater.*"))
                .Select(path => Path.GetRelativePath(published, path)).ToArray();
            foreach (var relative in coreFiles)
            {
                var target = Path.Combine(baseline, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(published, relative), target);
            }
            await Publish("0.2.0");
            var package = Path.Combine(root, "full-client-0.2.0-test.zip");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                var marker = zip.CreateEntry("update-acceptance-marker.txt");
                using (var writer = new StreamWriter(marker.Open())) writer.Write("new marker");
                foreach (var file in Directory.EnumerateFiles(published, "*", SearchOption.AllDirectories))
                    zip.CreateEntryFromFile(file, Path.GetRelativePath(published, file).Replace('\\', '/'), CompressionLevel.Optimal);
            }
            foreach (var relative in coreFiles) File.Copy(Path.Combine(baseline, relative), Path.Combine(published, relative), overwrite: true);
            Check(AssemblyName.GetAssemblyName(Path.Combine(published, "CMUmcl.dll")).Version == new Version(0, 1, 0, 0), "production output restored to 0.1.0");
            Check(File.Exists(Path.Combine(published, "coreclr.dll")) && File.Exists(Path.Combine(published, "updater", "coreclr.dll")), "client and updater both include independent runtimes");

            // Compile the exact updater source with only its pinned PUBLIC key changed for this test.
            var twinSource = Path.Combine(root, "updater-test-build");
            Directory.CreateDirectory(twinSource);
            var protocol = File.ReadAllText(Path.Combine(source, "CMUmcl", "Update", "UpdateProtocol.cs"));
            // Preserve raw-string indentation while replacing the key's individual base64 lines.
            var oldLines = UpdateProtocol.PublicKey.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToArray();
            var newLines = signingKey.ExportSubjectPublicKeyInfoPem().Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToArray();
            for (int i = 0; i < oldLines.Length; i++) protocol = protocol.Replace(oldLines[i], newLines[i]);
            File.WriteAllText(Path.Combine(twinSource, "UpdateProtocol.cs"), protocol);
            File.Copy(Path.Combine(source, "CMUmcl", "Update", "UpdateTransaction.cs"), Path.Combine(twinSource, "UpdateTransaction.cs"));
            File.Copy(Path.Combine(source, "CMUmcl.Updater", "Program.cs"), Path.Combine(twinSource, "Program.cs"));
            File.WriteAllText(Path.Combine(twinSource, "UpdaterTwin.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>WinExe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWindowsForms>true</UseWindowsForms><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AssemblyName>CMUmcl.Updater</AssemblyName></PropertyGroup></Project>
                """);
            var restoreArguments = new List<string> { "restore", Path.Combine(twinSource, "UpdaterTwin.csproj"),
                "-p:NuGetAudit=false", "-p:TargetPlatformMoniker=Windows%2cVersion=7.0" };
            var localConfig = Path.Combine(source, "work", "NuGet.Config");
            if (File.Exists(localConfig)) { restoreArguments.Add("--configfile"); restoreArguments.Add(localConfig); }
            await Command(sdk, restoreArguments.ToArray());
            await Command(sdk, "build", Path.Combine(twinSource, "UpdaterTwin.csproj"), "--no-restore", "-c", "Release", "-p:UseSharedCompilation=false", "-p:TargetPlatformMoniker=Windows%2cVersion=7.0");
            var twin = Path.Combine(twinSource, "bin", "Release", "net10.0-windows", "CMUmcl.Updater.dll");

            // Only generated build copies are cleaned; never remove an existing SDK installation.
            CleanOwned(Path.Combine(source, "CMUmcl", "bin", "Release"));
            CleanOwned(Path.Combine(source, "CMUmcl.Updater", "bin", "Release"));
            CleanOwned(Path.Combine(source, "outputs", "publish-smoke"));
            Console.WriteLine("Full package bytes: " + new FileInfo(package).Length);

            void ResetInstall()
            {
                CleanOwned(install);
                Directory.CreateDirectory(install);
                foreach (var file in Directory.EnumerateFiles(published, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(install, Path.GetRelativePath(published, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (!CreateHardLinkW(target, file, IntPtr.Zero)) throw new IOException("Cannot hardlink isolated install: " + Marshal.GetLastWin32Error());
                }
                Directory.CreateDirectory(Path.Combine(install, "instances", "test"));
                File.WriteAllText(Path.Combine(install, "instances", "test", "preserve.txt"), "isolated instance");
                File.WriteAllText(Path.Combine(install, "update-acceptance-marker.txt"), "old marker");
            }
            ResetInstall();
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "preserve-user-data.txt"), "isolated data");
            Directory.CreateDirectory(Path.Combine(install, "instances", "test"));
            File.WriteAllText(Path.Combine(install, "instances", "test", "preserve.txt"), "isolated instance");
            File.WriteAllText(Path.Combine(install, "update-acceptance-marker.txt"), "old marker");
            var oldDllHash = Hash(Path.Combine(install, "CMUmcl.dll"));
            var zipHash = Hash(package);
            var release = new UpdateRelease(2, "windows-x86_64", "0.2.0", "https://csumcl.mcapcs.cn/update/windows-x86_64/acceptance-only.zip",
                new FileInfo(package).Length, Convert.ToHexString(zipHash), Sign(zipHash), "ISOLATED TEST ONLY; NOT A PRODUCTION RELEASE");
            var payload = JsonSerializer.SerializeToUtf8Bytes(release, UpdateProtocol.JsonOptions);
            var envelope = new UpdateEnvelope(Convert.ToBase64String(payload), Sign(SHA256.HashData(payload)));
            string MakeStage(string name, int parentPid, long ticks, bool tampered = false)
            {
                var stage = Path.Combine(root, name);
                Directory.CreateDirectory(stage);
                if (!CreateHardLinkW(Path.Combine(stage, "package.zip"), package, IntPtr.Zero)) throw new IOException("Cannot link signed package");
                var currentEnvelope = tampered ? envelope with { Payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(release with { Version = "9.9.9" }, UpdateProtocol.JsonOptions)) } : envelope;
                File.WriteAllBytes(Path.Combine(stage, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(currentEnvelope, UpdateProtocol.JsonOptions));
                File.WriteAllText(Path.Combine(stage, "job.json"), JsonSerializer.Serialize(new UpdateJob(install, parentPid, ticks, "0.1.0", data), UpdateProtocol.JsonOptions));
                return stage;
            }
            void CleanStage(string stage)
            {
                foreach (var extracted in Directory.EnumerateDirectories(stage, "apply-*")) CleanOwned(extracted);
                var backups = Path.Combine(install, ".update-backups");
                if (Directory.Exists(backups))
                    foreach (var backup in Directory.EnumerateDirectories(backups)) CleanOwned(Path.Combine(backup, "files"));
            }

            using var oldClient = StartClient();
            await WaitUntil(() => { oldClient.Refresh(); return oldClient.MainWindowHandle != IntPtr.Zero || oldClient.HasExited; }, 30, "old client window");
            Check(!oldClient.HasExited, ".NET 10 self-contained old client starts using isolated data");
            var successStage = MakeStage("success", oldClient.Id, oldClient.StartTime.ToUniversalTime().Ticks);
            var updateStarted = DateTime.UtcNow;
            using var updater = StartUpdater(successStage, twin);
            await WaitUntil(() => Directory.EnumerateDirectories(successStage, "apply-*").Any(), 30, "updater extraction");
            await Task.Delay(1200);
            Check(!updater.HasExited && oldDllHash.SequenceEqual(Hash(Path.Combine(install, "CMUmcl.dll"))), "no replacement before parent exits");
            await CloseClient(oldClient);
            Check(oldClient.HasExited && oldClient.ExitCode == 0, "parent client exits normally with code 0");
            await updater.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
            Check(updater.ExitCode == 0, "real updater exits successfully after parent closes");
            Check(AssemblyName.GetAssemblyName(Path.Combine(install, "CMUmcl.dll")).Version == new Version(0, 2, 0, 0), "full signed package installs actual 0.2.0 WPF client");
            Process? restarted = null;
            await WaitUntil(() =>
            {
                foreach (var process in Process.GetProcessesByName("CMUmcl"))
                {
                    try
                    {
                        if (process.MainModule?.FileName == Path.Combine(install, "CMUmcl.exe") && process.StartTime.ToUniversalTime() >= updateStarted)
                        {
                            process.Refresh();
                            if (process.MainWindowHandle == IntPtr.Zero) continue;
                            restarted = process;
                            Track(process);
                            return true;
                        }
                    }
                    catch (InvalidOperationException) { }
                }
                return false;
            }, 30, "restarted client window");
            Check(restarted != null, "updated self-contained client restarts into its window");
            await CloseClient(restarted!);
            Check(restarted!.HasExited && restarted.ExitCode == 0, "restarted client exits normally with code 0");
            var successBackup = Directory.GetDirectories(Path.Combine(install, ".update-backups")).Single();
            Check(oldDllHash.SequenceEqual(Hash(Path.Combine(successBackup, "files", "CMUmcl.dll"))), "old DLL backup matches original SHA-256");
            Check(File.ReadAllText(Path.Combine(successBackup, "status.txt")) == "complete", "complete backup journal");
            Check(File.ReadAllText(Path.Combine(data, "preserve-user-data.txt")) == "isolated data" &&
                File.ReadAllText(Path.Combine(install, "instances", "test", "preserve.txt")) == "isolated instance", "isolated data and unmanaged instance preserved");
            Check(oldDllHash.SequenceEqual(Hash(Path.Combine(published, "CMUmcl.dll"))), "atomic update leaves original self-contained build unchanged");
            CleanStage(successStage);

            File.Copy(Path.Combine(successBackup, "status.txt"), Path.Combine(root, "success-backup-status.txt"));
            File.Copy(Path.Combine(successBackup, "recovery.json"), Path.Combine(root, "success-recovery.json"));
            ResetInstall();
            var failureStage = MakeStage("locked-file-rollback", int.MaxValue, 0);
            using (var locked = new FileStream(Path.Combine(install, "CMUmcl.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var failingUpdater = StartUpdater(failureStage, twin))
            {
                await WaitUntil(() => File.Exists(Path.Combine(failureStage, "result.txt")), 120, "rollback failure log");
                var error = File.ReadAllText(Path.Combine(failureStage, "result.txt"));
                Check(error.Contains("已恢复旧客户端"), "real updater rolls back after OS file-lock failure");
                await CloseNormallyAsync(failingUpdater);
            }
            Check(oldDllHash.SequenceEqual(Hash(Path.Combine(install, "CMUmcl.dll"))), "locked original DLL remains intact");
            Check(File.ReadAllText(Path.Combine(install, "update-acceptance-marker.txt")) == "old marker", "earlier replaced file restored on rollback");
            Check(Directory.EnumerateDirectories(Path.Combine(install, ".update-backups")).Any(backup => File.ReadAllText(Path.Combine(backup, "status.txt")) == "rolled-back"), "rollback journal records success");
            CleanStage(failureStage);

            var tamperedStage = MakeStage("tampered-manifest", int.MaxValue, 0, tampered: true);
            using (var rejectingUpdater = StartUpdater(tamperedStage, twin))
            {
                await WaitUntil(() => File.Exists(Path.Combine(tamperedStage, "result.txt")), 20, "tampered manifest rejection");
                Check(File.ReadAllText(Path.Combine(tamperedStage, "result.txt")).Contains("签名校验失败"), "real updater rejects signed-metadata tampering");
                await CloseNormallyAsync(rejectingUpdater);
            }
            Check(!Directory.EnumerateDirectories(tamperedStage, "apply-*").Any() && oldDllHash.SequenceEqual(Hash(Path.Combine(install, "CMUmcl.dll"))), "tampering rejected before extraction or replacement");
            File.WriteAllText(report, JsonSerializer.Serialize(new { Status = "PASS", Checks = checks, Sdk = "10.0.403", Runtime = "10.0.14", ProductionKeysRead = false,
                Published = false, TestTrustRootOnly = true, ParentExitSimulated = false, ForcedTerminations = 0,
                SourceDirectory = source, PackageSha256 = Convert.ToHexString(zipHash) }, UpdateProtocol.JsonOptions));
            Console.WriteLine("ACCEPTANCE PASS. Report: " + report);
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { Status = "FAIL", Checks = checks, Error = error.ToString() }, UpdateProtocol.JsonOptions));
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            foreach (var owned in ownedProcesses)
            {
                try
                {
                    using var process = Process.GetProcessById(owned.Id);
                    if (process.StartTime.ToUniversalTime().Ticks == owned.StartTicks && !process.HasExited)
                    { await CloseNormallyAsync(process); }
                }
                catch { }
            }
        }
    }

    public static async Task<int> RunPreparedAsync(string preparedRoot)
    {
        var source = Path.GetFullPath(".");
        var root = Path.GetFullPath(preparedRoot);
        if (!root.StartsWith(Path.Combine(source, "outputs", "auto-update-acceptance-"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Prepared acceptance must be inside this workspace's generated output directory.");
        var report = Path.Combine(root, "resumed-report.json");
        var install = Path.Combine(root, "install");
        var published = Path.Combine(source, "outputs", "net10-selfcontained");
        var data = Path.Combine(root, "isolated-data");
        var successStage = Path.Combine(root, "success");
        var envelopeBytes = File.ReadAllBytes(Path.Combine(successStage, "latest.json"));
        var package = Path.Combine(root, "full-client-0.2.0-test.zip");
        var twin = Path.Combine(root, "updater-test-build", "bin", "Release", "net10.0-windows", "CMUmcl.Updater.dll");
        var host = Environment.GetEnvironmentVariable("CMUMCL_DOTNET10") ?? Path.Combine(source, "work", "dotnet10", "dotnet.exe");
        var checks = new List<string>();
        var owned = new List<(int Id, long Ticks)>();
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            checks.Add(message);
            Console.WriteLine("PASS: " + message);
        }
        void Clean(string directory)
        {
            var path = Path.GetFullPath(directory);
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup");
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        void ResetInstall()
        {
            Clean(install);
            Directory.CreateDirectory(install);
            foreach (var file in Directory.EnumerateFiles(published, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(install, Path.GetRelativePath(published, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!CreateHardLinkW(target, file, IntPtr.Zero)) throw new IOException("Isolated install hardlink failed");
            }
            Directory.CreateDirectory(Path.Combine(install, "instances", "test"));
            File.WriteAllText(Path.Combine(install, "instances", "test", "preserve.txt"), "isolated instance");
            File.WriteAllText(Path.Combine(install, "update-acceptance-marker.txt"), "old marker");
        }
        static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
        Process Track(Process process) { owned.Add((process.Id, process.StartTime.ToUniversalTime().Ticks)); return process; }
        Process Updater(string stage)
        {
            var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = stage };
            start.ArgumentList.Add(twin);
            start.ArgumentList.Add(stage);
            return Track(Process.Start(start)!);
        }
        async Task Wait(Func<bool> condition, string reason, int seconds = 60)
        {
            var timer = Stopwatch.StartNew();
            while (!condition())
            {
                if (timer.Elapsed.TotalSeconds > seconds) throw new TimeoutException(reason);
                await Task.Delay(150);
            }
        }
        void WriteJob(string stage, int parent, long ticks, byte[] envelope)
        {
            Directory.CreateDirectory(stage);
            if (!File.Exists(Path.Combine(stage, "package.zip")) && !CreateHardLinkW(Path.Combine(stage, "package.zip"), package, IntPtr.Zero))
                throw new IOException("Cannot link test archive");
            File.WriteAllBytes(Path.Combine(stage, "latest.json"), envelope);
            File.WriteAllText(Path.Combine(stage, "job.json"), JsonSerializer.Serialize(new UpdateJob(install, parent, ticks, "0.1.0", data), UpdateProtocol.JsonOptions));
            if (File.Exists(Path.Combine(stage, "result.txt"))) File.Delete(Path.Combine(stage, "result.txt"));
        }
        async Task StopFixture(Process process)
        {
            await CloseNormallyAsync(process);
        }
        try
        {
            Check(File.ReadAllText(Path.Combine(root, "updater-test-build", "UpdateTransaction.cs")) == File.ReadAllText(Path.Combine(source, "CMUmcl", "Update", "UpdateTransaction.cs")) &&
                File.ReadAllText(Path.Combine(root, "updater-test-build", "Program.cs")) == File.ReadAllText(Path.Combine(source, "CMUmcl.Updater", "Program.cs")), "test updater transaction and process code match production sources");
            foreach (var extraction in Directory.GetDirectories(successStage, "apply-*")) Clean(extraction);
            ResetInstall();
            Check(new FileInfo(package).Length > 100 * 1024 * 1024, "using complete .NET 10 self-contained update archive");
            Check(File.Exists(Path.Combine(published, "coreclr.dll")) && File.Exists(Path.Combine(published, "updater", "coreclr.dll")), "production client and updater have independent .NET 10 runtimes");
            var original = Hash(Path.Combine(install, "CMUmcl.dll"));
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "preserve-user-data.txt"), "isolated data");
            var start = new ProcessStartInfo(Path.Combine(install, "CMUmcl.exe")) { UseShellExecute = false, WorkingDirectory = install, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--data-directory");
            start.ArgumentList.Add(data);
            using var parent = Track(Process.Start(start)!);
            await Wait(() => { parent.Refresh(); return parent.MainWindowHandle != IntPtr.Zero || parent.HasExited; }, "old client window", 30);
            Check(!parent.HasExited, "old .NET 10 self-contained WPF client opens with isolated data");
            WriteJob(successStage, parent.Id, parent.StartTime.ToUniversalTime().Ticks, envelopeBytes);
            using var updater = Updater(successStage);
            await Wait(() => Directory.GetDirectories(successStage, "apply-*").Any(), "update staging");
            await Task.Delay(1000);
            Check(!updater.HasExited && original == Hash(Path.Combine(install, "CMUmcl.dll")), "updater waits without replacement while parent is alive");
            var restartAfter = DateTime.UtcNow;
            await StopFixture(parent);
            Check(parent.HasExited && parent.ExitCode == 0, "parent client exits normally with code 0, without termination fallback");
            await Wait(() => File.Exists(Path.Combine(successStage, "result.txt")), "update result");
            Check(File.ReadAllText(Path.Combine(successStage, "result.txt")).StartsWith("更新成功"), "signed full-package replacement succeeds");
            await updater.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Check(updater.ExitCode == 0 && AssemblyName.GetAssemblyName(Path.Combine(install, "CMUmcl.dll")).Version == new Version(0, 2, 0, 0), "actual WPF client upgraded from 0.1.0 to 0.2.0");
            Process? restarted = null;
            await Wait(() =>
            {
                foreach (var candidate in Process.GetProcessesByName("CMUmcl"))
                {
                    try
                    {
                        if (candidate.MainModule?.FileName != Path.Combine(install, "CMUmcl.exe") || candidate.StartTime.ToUniversalTime() < restartAfter) continue;
                        candidate.Refresh();
                        if (candidate.MainWindowHandle == IntPtr.Zero) continue;
                        restarted = Track(candidate);
                        return true;
                    }
                    catch (InvalidOperationException) { }
                }
                return false;
            }, "updated client restart", 30);
            Check(restarted != null, "updated self-contained client automatically reopens its WPF window");
            await StopFixture(restarted!);
            Check(restarted!.HasExited && restarted.ExitCode == 0, "restarted client also exits normally with code 0");
            var backup = Directory.GetDirectories(Path.Combine(install, ".update-backups")).Single();
            Check(Hash(Path.Combine(backup, "files", "CMUmcl.dll")) == original && File.ReadAllText(Path.Combine(backup, "status.txt")) == "complete", "complete backup includes original client DLL SHA-256");
            Check(File.ReadAllText(Path.Combine(install, "instances", "test", "preserve.txt")) == "isolated instance" &&
                File.ReadAllText(Path.Combine(data, "preserve-user-data.txt")) == "isolated data", "isolated instance and external user data preserved");
            Check(Hash(Path.Combine(published, "CMUmcl.dll")) == original, "original self-contained build remains unchanged");
            File.Copy(Path.Combine(backup, "status.txt"), Path.Combine(root, "success-backup-status.txt"), overwrite: true);
            File.Copy(Path.Combine(backup, "recovery.json"), Path.Combine(root, "success-recovery.json"), overwrite: true);
            foreach (var extraction in Directory.GetDirectories(successStage, "apply-*")) Clean(extraction);
            ResetInstall();

            var lockedStage = Path.Combine(root, "locked-file-rollback");
            WriteJob(lockedStage, int.MaxValue, 0, envelopeBytes);
            using (var fileLock = new FileStream(Path.Combine(install, "CMUmcl.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var failing = Updater(lockedStage))
            {
                await Wait(() => File.Exists(Path.Combine(lockedStage, "result.txt")), "locked-file rollback");
                Check(File.ReadAllText(Path.Combine(lockedStage, "result.txt")).Contains("已恢复旧客户端"), "real file lock causes updater to restore old version");
                await CloseNormallyAsync(failing);
            }
            Check(Hash(Path.Combine(install, "CMUmcl.dll")) == original && File.ReadAllText(Path.Combine(install, "update-acceptance-marker.txt")) == "old marker", "locked DLL and previously replaced marker restored");
            var rollback = Directory.GetDirectories(Path.Combine(install, ".update-backups")).Single();
            Check(File.ReadAllText(Path.Combine(rollback, "status.txt")) == "rolled-back", "rollback journal confirms complete restoration");
            File.Copy(Path.Combine(rollback, "status.txt"), Path.Combine(root, "rollback-backup-status.txt"), overwrite: true);
            foreach (var extraction in Directory.GetDirectories(lockedStage, "apply-*")) Clean(extraction);
            Clean(Path.Combine(rollback, "files"));

            var envelope = JsonSerializer.Deserialize<UpdateEnvelope>(envelopeBytes, UpdateProtocol.JsonOptions)!;
            var released = JsonSerializer.Deserialize<UpdateRelease>(Convert.FromBase64String(envelope.Payload), UpdateProtocol.JsonOptions)!;
            var tampered = envelope with { Payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(released with { Version = "9.9.9" }, UpdateProtocol.JsonOptions)) };
            var tamperedStage = Path.Combine(root, "tampered-manifest");
            WriteJob(tamperedStage, int.MaxValue, 0, JsonSerializer.SerializeToUtf8Bytes(tampered, UpdateProtocol.JsonOptions));
            using (var rejecting = Updater(tamperedStage))
            {
                await Wait(() => File.Exists(Path.Combine(tamperedStage, "result.txt")), "signature rejection", 20);
                Check(File.ReadAllText(Path.Combine(tamperedStage, "result.txt")).Contains("签名校验失败"), "tampered version metadata rejected by real updater");
                await CloseNormallyAsync(rejecting);
            }
            Check(!Directory.GetDirectories(tamperedStage, "apply-*").Any() && Hash(Path.Combine(install, "CMUmcl.dll")) == original, "tampering rejected before extraction and replacement");
            File.WriteAllText(report, JsonSerializer.Serialize(new { Status = "PASS", Checks = checks, Sdk = "10.0.403", Runtime = "10.0.14", TestTrustRootOnly = true,
                ParentExitSimulated = false, ForcedTerminations = 0, SourceDirectory = source, ProductionKeysRead = false, Published = false }, UpdateProtocol.JsonOptions));
            Console.WriteLine("ACCEPTANCE PASS: " + report);
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { Status = "FAIL", Checks = checks, Error = error.ToString() }, UpdateProtocol.JsonOptions));
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            foreach (var item in owned)
            {
                try
                {
                    using var process = Process.GetProcessById(item.Id);
                    if (process.StartTime.ToUniversalTime().Ticks == item.Ticks && !process.HasExited) { await CloseNormallyAsync(process); }
                }
                catch { }
            }
        }
    }
}
