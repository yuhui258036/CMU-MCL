using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMUmcl.Community;
using CMUmcl.Content;
using CMUmcl.Launch;

// Integrity check, Forge Java ceiling, crash report export and Modrinth mod updates.
internal static class LauncherFeatureTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "cmumcl-features-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            JavaChecks(check);
            IntegrityChecks(root, check);
            await LauncherIntegrityChecks(root, check);
            CrashReportChecks(root, check);
            await ModUpdateChecks(root, check);
        }
        finally { try { Directory.Delete(root, true); } catch { /* best effort */ } }
    }

    private static void JavaChecks(Action<bool, string> check)
    {
        check(JavaVersionResolver.ResolveMaximumMajor("1.12.2", "forge") == 8 && JavaVersionResolver.ResolveMaximumMajor("1.7.10", "Forge") == 8,
            "java: LaunchWrapper Forge is capped at Java 8");
        check(JavaVersionResolver.ResolveMaximumMajor("1.16.5", "forge") == null && JavaVersionResolver.ResolveMaximumMajor("1.12.2", "fabric") == null
            && JavaVersionResolver.ResolveMaximumMajor("1.12.2", "") == null, "java: no ceiling for modern Forge, other loaders or vanilla");
    }

    private static void IntegrityChecks(string root, Action<bool, string> check)
    {
        string file = Path.Combine(root, "library.jar");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        check(!InstallIntegrity.IsBroken(file, 3) && !InstallIntegrity.IsBroken(file, 0), "integrity: present file with matching or unknown size is intact");
        check(InstallIntegrity.IsBroken(file, 5) && InstallIntegrity.IsBroken(Path.Combine(root, "missing.jar"), 3), "integrity: size mismatch and missing file are broken");
        check(!InstallIntegrity.IsBroken(null, 3), "integrity: entries without a local path are skipped");
    }

    // Runs the real CmlLib file extraction on a synthetic offline version.
    private static async Task LauncherIntegrityChecks(string root, Action<bool, string> check)
    {
        string minecraft = Path.Combine(root, "minecraft"), version = Path.Combine(minecraft, "versions", "fixture");
        Directory.CreateDirectory(version);
        File.WriteAllText(Path.Combine(version, "fixture.json"), """
            {"id":"fixture","type":"release","mainClass":"net.minecraft.client.main.Main","minecraftArguments":"",
             "libraries":[{"name":"example:lib:1.0","downloads":{"artifact":{"path":"example/lib/1.0/lib-1.0.jar","url":"https://libraries.invalid/lib-1.0.jar","size":3,"sha1":"7037807198c22a7d2b0807371d763779a84fdfcf"}}}]}
            """);
        var launcher = new CmlLib.Core.MinecraftLauncher(new CmlLib.Core.MinecraftPath(minecraft));
        int before = await InstallIntegrity.CountBrokenAsync(launcher, "fixture", CancellationToken.None);
        string library = Path.Combine(minecraft, "libraries", "example", "lib", "1.0", "lib-1.0.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(library)!);
        File.WriteAllBytes(library, new byte[] { 1, 2, 3 });
        int after = await InstallIntegrity.CountBrokenAsync(launcher, "fixture", CancellationToken.None);
        File.WriteAllBytes(library, new byte[] { 1 });
        int truncated = await InstallIntegrity.CountBrokenAsync(launcher, "fixture", CancellationToken.None);
        check(before >= 1 && after == before - 1 && truncated == before, "integrity: CmlLib file list detects a missing or truncated library");
    }

    private static void CrashReportChecks(string root, Action<bool, string> check)
    {
        string game = Path.Combine(root, "game");
        Directory.CreateDirectory(Path.Combine(game, "logs"));
        Directory.CreateDirectory(Path.Combine(game, "crash-reports"));
        Directory.CreateDirectory(Path.Combine(game, "mods"));
        const string token = "secret-access-token-0123456789";
        File.WriteAllText(Path.Combine(game, "logs", "latest.log"), "[main] Setting user: Steve\n--accessToken " + token + "\nException in thread main");
        string older = Path.Combine(game, "crash-reports", "crash-old.txt"), newer = Path.Combine(game, "crash-reports", "crash-new.txt");
        File.WriteAllText(older, "old"); File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-1));
        File.WriteAllText(newer, "---- Minecraft Crash Report ----");
        File.WriteAllText(Path.Combine(game, "mods", "alpha.jar"), "x");
        var context = new CrashContext { InstanceName = "测试:实例", GameDirectory = game, GameVersion = "1.21.1", Loader = "fabric", LoaderVersion = "0.16.10", JavaMajor = 21, JavaPath = @"C:\java\javaw.exe", ExitCode = 1, Hint = "内存不足" };
        check(!CrashReportExporter.DefaultFileName(context).Contains(":"), "crash report: default file name is filesystem safe");
        string target = Path.Combine(root, "report.zip");
        CrashReportExporter.Export(context, target, new[] { token, null, "short" });
        using var zip = ZipFile.OpenRead(target);
        string Read(string name) { using var reader = new StreamReader(zip.GetEntry(name)!.Open()); return reader.ReadToEnd(); }
        check(zip.GetEntry("logs/latest.log") != null && zip.GetEntry("crash-reports/crash-new.txt") != null && zip.GetEntry("crash-reports/crash-old.txt") == null,
            "crash report: contains latest.log and only the newest crash report");
        check(!Read("logs/latest.log").Contains(token) && Read("logs/latest.log").Contains(CrashReportExporter.Masked), "crash report: access token is masked");
        string info = Read("launcher-info.txt");
        check(info.Contains("退出代码：1") && info.Contains("Java 21") && info.Contains("alpha.jar") && info.Contains("内存不足"), "crash report: summary lists exit code, Java, mods and hint");
    }

    private static async Task ModUpdateChecks(string root, Action<bool, string> check)
    {
        string mods = Path.Combine(root, "mods");
        Directory.CreateDirectory(mods);
        string alpha = Path.Combine(mods, "alpha-1.0.jar"), beta = Path.Combine(mods, "beta-2.0.jar"), local = Path.Combine(mods, "local.jar");
        File.WriteAllText(alpha, "alpha 1.0"); File.WriteAllText(beta, "beta 2.0"); File.WriteAllText(local, "unknown to modrinth");
        byte[] alphaNew = Encoding.UTF8.GetBytes("alpha 1.1");
        string alphaHash = Sha1(File.ReadAllBytes(alpha)), betaHash = Sha1(File.ReadAllBytes(beta));
        object Version(string id, string number, string date, string file, byte[]? content = null) => new
        {
            id, version_number = number, date_published = date,
            files = new[] { new { url = "https://cdn.modrinth.com/data/x/" + file, filename = file, primary = true, size = content?.Length ?? 1,
                hashes = new { sha1 = content == null ? "00" : Sha1(content), sha512 = content == null ? "" : Hex(SHA512.Create().ComputeHash(content)) } } },
        };
        string? updateBody = null;
        var api = new Fixture(request =>
        {
            string path = request.RequestUri!.AbsolutePath, body = request.Content!.ReadAsStringAsync().Result;
            if (path == "/v2/version_files")
                return Json(new Dictionary<string, object> { [alphaHash] = Version("a1", "1.0", "2024-01-01T00:00:00Z", "alpha-1.0.jar"), [betaHash] = Version("b2", "2.0", "2024-06-01T00:00:00Z", "beta-2.0.jar") });
            updateBody = body;
            // Beta's "latest" is older than the installed build and must not be offered.
            return Json(new Dictionary<string, object> { [alphaHash] = Version("a2", "1.1", "2024-02-01T00:00:00Z", "alpha-1.1.jar", alphaNew), [betaHash] = Version("b1", "1.9", "2024-03-01T00:00:00Z", "beta-1.9.jar") });
        });
        bool tampered = false;
        var cdn = new Fixture(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(tampered ? Encoding.UTF8.GetBytes("alpha 1.X") : alphaNew) });
        using var service = new ModUpdateService(api, cdn);
        var updates = await service.CheckAsync(new[] { alpha, beta, local }, "1.21.1", "quilt", CancellationToken.None);
        check(updates.Count == 1 && updates[0].Path == alpha && updates[0].CurrentVersion == "1.0" && updates[0].LatestVersion == "1.1" && updates[0].FileName == "alpha-1.1.jar",
            "mod updates: newer compatible Modrinth build found by hash; older and unknown files ignored");
        check(updateBody != null && updateBody.Contains("\"quilt\"") && updateBody.Contains("\"fabric\"") && updateBody.Contains("\"1.21.1\""),
            "mod updates: query is limited to the instance loader family and game version");
        string downloaded = await service.DownloadAsync(updates[0], CancellationToken.None);
        check(File.ReadAllText(downloaded) == "alpha 1.1" && Path.GetFileName(downloaded) == "alpha-1.1.jar", "mod updates: verified download keeps Modrinth file name");
        ModUpdateService.DeleteDownload(downloaded);
        tampered = true;
        bool rejected = false;
        try { await service.DownloadAsync(updates[0], CancellationToken.None); } catch (IOException) { rejected = true; }
        check(rejected, "mod updates: download with wrong hash is rejected");
        check(ModUpdateService.LoadersFor("vanilla") == null && ModUpdateService.LoadersFor("NeoForge")!.Single() == "neoforge", "mod updates: loader mapping");
    }

    private static HttpResponseMessage Json(object value)
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static string Sha1(byte[] data) => Hex(SHA1.Create().ComputeHash(data));
    private static string Hex(byte[] data) => BitConverter.ToString(data).Replace("-", "").ToLowerInvariant();

    private sealed class Fixture(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = reply(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
