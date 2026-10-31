using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CMUmcl.Update;

internal static class UpdateUiFixture
{
    public static async Task PrepareAsync(string fixtureRoot, string oldTemplate, string newTemplate)
    {
        string source = Path.GetFullPath(".");
        string root = Path.GetFullPath(fixtureRoot);
        if (!root.StartsWith(Path.Combine(source, "outputs") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("UI fixtures must be under the workspace outputs directory.");
        if (Directory.Exists(root)) throw new IOException("Refusing to overwrite an existing fixture.");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "fixture.marker"), "Isolated UI acceptance. No production credentials. Never upload this directory.");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); // Private material never leaves memory.
        string publicPem = key.ExportSubjectPublicKeyInfoPem();
        File.WriteAllText(Path.Combine(root, "public-key.pem"), publicPem);
        string sdk = Environment.GetEnvironmentVariable("CMUMCL_DOTNET10")
            ?? throw new InvalidOperationException("Set CMUMCL_DOTNET10 to the existing SDK executable.");
        async Task Command(params string[] arguments)
        {
            var start = new ProcessStartInfo(sdk) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = source,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "false";
            start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string log = await stdout + await stderr;
            File.AppendAllText(Path.Combine(root, "fixture-build.log"), log);
            Console.WriteLine(log);
            if (process.ExitCode != 0) throw new Exception("Fixture updater build failed.");
        }
        var updaterSource = Path.Combine(root, "updater-source");
        Directory.CreateDirectory(updaterSource);
        string protocol = File.ReadAllText(Path.Combine(source, "CMUmcl", "Update", "UpdateProtocol.cs"));
        var oldLines = UpdateProtocol.PublicKey.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToArray();
        var newLines = publicPem.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToArray();
        for (int i = 0; i < oldLines.Length; i++) protocol = protocol.Replace(oldLines[i], newLines[i]);
        File.WriteAllText(Path.Combine(updaterSource, "UpdateProtocol.cs"), protocol);
        File.Copy(Path.Combine(source, "CMUmcl", "Update", "UpdateTransaction.cs"), Path.Combine(updaterSource, "UpdateTransaction.cs"));
        File.Copy(Path.Combine(source, "CMUmcl.Updater", "Program.cs"), Path.Combine(updaterSource, "Program.cs"));
        var updaterProject = Path.Combine(updaterSource, "FixtureUpdater.csproj");
        File.WriteAllText(updaterProject, """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>WinExe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWindowsForms>true</UseWindowsForms><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AssemblyName>CMUmcl.Updater</AssemblyName></PropertyGroup></Project>
            """);
        // Do not depend on another developer's ignored .tmp/.nuget directories.
        // The usual NuGet configuration and NUGET_PACKAGES environment apply.
        var selfContained = Environment.GetEnvironmentVariable("CMUMCL_UI_SELF_CONTAINED") ?? "true";
        if (selfContained is not ("true" or "false")) throw new ArgumentException("CMUMCL_UI_SELF_CONTAINED must be true or false.");
        await Command("restore", updaterProject, "-r", "win-x64", "-p:SelfContained=" + selfContained, "-p:NuGetAudit=false");
        var updaterPublish = Path.Combine(root, "updater-publish");
        await Command("publish", updaterProject, "--no-restore", "-c", "Release", "-r", "win-x64", "--self-contained", selfContained,
            "-p:UseSharedCompilation=false", "-o", updaterPublish);

        static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
        foreach (var (template, name) in new[] { (oldTemplate, "old-app"), (newTemplate, "new-app") })
        {
            var app = Path.Combine(root, name);
            CopyTree(Path.GetFullPath(template), app);
            File.Copy(Path.Combine(app, "CMUmcl.UpdateUiAcceptance.exe"), Path.Combine(app, "CMUmcl.exe"), overwrite: true);
            CopyTree(updaterPublish, Path.Combine(app, "updater"));
        }
        string package = Path.Combine(root, "package.zip");
        ZipFile.CreateFromDirectory(Path.Combine(root, "new-app"), package, CompressionLevel.Optimal, includeBaseDirectory: false);
        byte[] archiveHash;
        using (var stream = File.OpenRead(package)) archiveHash = SHA256.HashData(stream);
        UpdateSignature Sign(byte[] hash) => new("ECDSA-P256-SHA256", "ieee-p1363-base64",
            Convert.ToBase64String(key.SignHash(hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        var release = new UpdateRelease(2, "windows-x86_64", "0.2.0",
            "https://csumcl.mcapcs.cn/update/windows-x86_64/ui-acceptance-only-0.2.0.zip", new FileInfo(package).Length,
            Convert.ToHexString(archiveHash), Sign(archiveHash), "仅用于隔离设置页验收，不会上线。");
        UpdateEnvelope Envelope(UpdateRelease value)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(value, UpdateProtocol.JsonOptions);
            return new(Convert.ToBase64String(payload), Sign(SHA256.HashData(payload)));
        }
        var manifests = Path.Combine(root, "manifests");
        Directory.CreateDirectory(manifests);
        void Save(string name, UpdateEnvelope envelope) =>
            File.WriteAllBytes(Path.Combine(manifests, name), JsonSerializer.SerializeToUtf8Bytes(envelope, UpdateProtocol.JsonOptions));
        var available = Envelope(release);
        Save("available.json", available);
        Save("no-update.json", Envelope(release with { Version = "0.1.0" }));
        Save("bad-hash.json", Envelope(release with { Sha256 = new string('0', 64) }));
        Save("tampered.json", available with { Payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(release with { Version = "9.9.9" }, UpdateProtocol.JsonOptions)) });
        File.WriteAllText(Path.Combine(manifests, "legacy.json"), """{"formatVersion":1,"version":"0.1.0-test","url":"https://example.invalid/legacy.bin"}""");
        var resourceManifest = System.Text.Encoding.UTF8.GetBytes("""{"formatVersion":1,"instances":[]}""");
        File.WriteAllBytes(Path.Combine(root, "resource-manifest.json"), resourceManifest);
        File.WriteAllText(Path.Combine(root, "resource-manifest.json.sig"), JsonSerializer.Serialize(new
        {
            algorithm = "ECDSA-P256-SHA256",
            signature = Convert.ToBase64String(key.SignData(resourceManifest, HashAlgorithmName.SHA256)),
        }));
        File.WriteAllText(Path.Combine(root, "scenario.txt"), "available");
        File.WriteAllText(Path.Combine(root, "fixture-info.json"), JsonSerializer.Serialize(new
        {
            Source = source, CreatedUtc = DateTimeOffset.UtcNow, TestOnly = true, ProductionPrivateKeyRead = false,
            PublicKeySPKISHA256 = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())),
            PackageSHA256 = Convert.ToHexString(archiveHash), PackageSize = release.Size,
        }, UpdateProtocol.JsonOptions));
        Console.WriteLine("UI fixture ready: " + root);
    }
}
