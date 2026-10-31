using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMUmcl.Update;

if (args.Length == 4 && args[0] == "--prepare-ui-fixture")
{
    await UpdateUiFixture.PrepareAsync(args[1], args[2], args[3]);
    return;
}

if (args.Length > 0 && args[0] == "--acceptance")
{
    Environment.ExitCode = await UpdateAcceptance.RunAsync();
    return;
}
if (args.Length == 2 && args[0] == "--resume-acceptance")
{
    Environment.ExitCode = await UpdateAcceptance.RunPreparedAsync(args[1]);
    return;
}

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed++;
}
void Reject(Action action, string name)
{
    try { action(); } catch { passed++; return; }
    throw new Exception("FAIL: accepted " + name);
}
var root = Path.GetFullPath(Path.Combine("outputs", "update-tests-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var publicKey = key.ExportSubjectPublicKeyInfoPem();
using (var productionPublic = ECDsa.Create())
{
    productionPublic.ImportFromPem(UpdateProtocol.PublicKey);
    Check(Convert.ToHexString(SHA256.HashData(productionPublic.ExportSubjectPublicKeyInfo())) ==
        "20C98D68C2E761E57EC40C3AD0A173EDE4528279464A5E643E456C5DE3C47583",
        "owner-confirmed production public key fingerprint");
}
try
{
    UpdateProtocol.ReadRelease(Encoding.UTF8.GetBytes("""{"formatVersion":1,"version":"0.1.0-test","signature":{"algorithm":"ECDSA-P256-SHA256","format":"ieee-p1363-base64","value":"test"}}"""));
    throw new Exception("FAIL: legacy feed accepted");
}
catch (InvalidDataException) { passed++; }
UpdateSignature Sign(byte[] bytes) => new("ECDSA-P256-SHA256", "ieee-p1363-base64",
    Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
byte[] Envelope(UpdateRelease release)
{
    var payload = JsonSerializer.SerializeToUtf8Bytes(release, UpdateProtocol.JsonOptions);
    return JsonSerializer.SerializeToUtf8Bytes(new UpdateEnvelope(Convert.ToBase64String(payload), Sign(payload)), UpdateProtocol.JsonOptions);
}
string Zip(params string[] names)
{
    var file = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
    using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
    foreach (var name in names)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write("new " + name);
    }
    return file;
}
string Extract(string zip) => Path.Combine(root, "extract-" + Path.GetFileNameWithoutExtension(zip));
string[] required = ["CMUmcl.exe", "CMUmcl.dll", "updater/CMUmcl.Updater.exe"];
var archive = Zip(required);
var bytes = File.ReadAllBytes(archive);
var release = new UpdateRelease(2, "windows-x86_64", "0.2.0", "https://csumcl.mcapcs.cn/update/windows-x86_64/0.2.0.zip",
    bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), Sign(bytes), "test");
Check(UpdateProtocol.ReadRelease(Envelope(release), publicKey) == release, "signed manifest");
Reject(() => UpdateProtocol.ReadRelease(Envelope(release)), "untrusted signing key");
UpdateProtocol.VerifyArchive(archive, release, publicKey); passed++;
Reject(() => UpdateProtocol.VerifyArchive(archive, release with { Size = bytes.Length + 1 }, publicKey), "size mismatch");
Reject(() => UpdateProtocol.VerifyArchive(archive, release with { Sha256 = new string('0', 64) }, publicKey), "hash mismatch");
Reject(() => UpdateProtocol.VerifyArchive(archive, release with { ArchiveSignature = Sign([1, 2, 3]) }, publicKey), "zip signature mismatch");
Reject(() => UpdateProtocol.ReadRelease(Encoding.UTF8.GetBytes("{}"), publicKey), "old or missing envelope");
var envelope = JsonSerializer.Deserialize<UpdateEnvelope>(Envelope(release), UpdateProtocol.JsonOptions)!;
var forgedPayload = JsonSerializer.SerializeToUtf8Bytes(release with { Version = "9.9.9" }, UpdateProtocol.JsonOptions);
Reject(() => UpdateProtocol.ReadRelease(JsonSerializer.SerializeToUtf8Bytes(envelope with { Payload = Convert.ToBase64String(forgedPayload) }), publicKey), "version tampering");
foreach (var bad in new[] { "0.1.0-test", "1.0", "-1.2.3", "1.2.3.4.5", "1.2..3", "" })
    Reject(() => UpdateProtocol.ParseVersion(bad), "bad version " + bad);
Check(UpdateProtocol.ParseVersion("0.2.0") > UpdateProtocol.ParseVersion("0.1.99"), "version ordering");
Check(UpdateProtocol.ParseVersion("0.1.0") == UpdateProtocol.ParseVersion("0.1.0.0"), "version normalization");
Check(UpdateProtocol.ParseVersion("0.10.0") > UpdateProtocol.ParseVersion("0.2.0"), "numeric comparison");
foreach (var bad in new[] { "http://csumcl.mcapcs.cn/update/windows-x86_64/a.zip", "https://evil.example/update/windows-x86_64/a.zip",
    "https://csumcl.mcapcs.cn:9443/update/windows-x86_64/a.zip", "https://csumcl.mcapcs.cn/packs/a.zip",
    "https://user@csumcl.mcapcs.cn/update/windows-x86_64/a.zip", "https://csumcl.mcapcs.cn/update/windows-x86_64/a.bin" })
    Reject(() => UpdateProtocol.ReadRelease(Envelope(release with { Url = bad }), publicKey), "unsafe URL");
Reject(() => UpdateProtocol.ReadRelease(Envelope(release with { Platform = "linux" }), publicKey), "wrong platform");
Reject(() => UpdateProtocol.ReadRelease(Envelope(release with { FormatVersion = 1 }), publicKey), "wrong format");
Reject(() => UpdateProtocol.ReadRelease(Envelope(release with { Size = UpdateProtocol.MaxArchiveSize + 1 }), publicKey), "oversized archive");
Reject(() => UpdateProtocol.ReadRelease(Envelope(release with { Sha256 = new string('z', 64) }), publicKey), "invalid hash");
foreach (var bad in new[] { "../outside.txt", "C:/evil.txt", "\\\\server\\share\\evil", "a/../../evil", "CON.txt", "dir/NUL",
    "CMUmclData/settings.json", "settings.json", "auth.dat", "instances/test/file", "keys/key.pem", ".update-backups/file", "a:b", "a./file", "a /file" })
{
    var zip = Zip([.. required, bad]);
    Reject(() => UpdateProtocol.ExtractArchive(zip, Extract(zip)), "archive path " + bad);
}
var duplicate = Zip([.. required, "cmumcl.EXE"]);
Reject(() => UpdateProtocol.ExtractArchive(duplicate, Extract(duplicate)), "Windows case collision");
var missing = Zip("CMUmcl.exe");
Reject(() => UpdateProtocol.ExtractArchive(missing, Extract(missing)), "incomplete ZIP");
var symlink = Zip(required);
using (var zip = ZipFile.Open(symlink, ZipArchiveMode.Update))
    zip.GetEntry("CMUmcl.exe")!.ExternalAttributes = unchecked((int)0xA1FF0000);
Reject(() => UpdateProtocol.ExtractArchive(symlink, Extract(symlink)), "symlink ZIP");
var extracted = Extract(archive);
var files = UpdateProtocol.ExtractArchive(archive, extracted);
Check(files.Length == 3, "valid archive extracted");

var install = Path.Combine(root, "install");
Directory.CreateDirectory(install);
File.WriteAllText(Path.Combine(install, "CMUmcl.exe"), "old exe");
File.WriteAllText(Path.Combine(install, "CMUmcl.dll"), "old dll");
Directory.CreateDirectory(Path.Combine(install, "instances", "test"));
File.WriteAllText(Path.Combine(install, "instances", "test", "keep"), "game");
File.WriteAllText(Path.Combine(install, "settings.json"), "settings");
File.WriteAllText(Path.Combine(install, "unmanaged.txt"), "user file");
Reject(() => UpdateTransaction.Apply(install, extracted, files, Path.Combine(root, "rollback"), i =>
{
    if (i == 2) throw new IOException("injected replacement failure");
}), "replacement failure rolls back");
Check(File.ReadAllText(Path.Combine(install, "CMUmcl.exe")) == "old exe", "restore exe");
Check(File.ReadAllText(Path.Combine(install, "CMUmcl.dll")) == "old dll", "restore dll");
Check(!File.Exists(Path.Combine(install, "updater", "CMUmcl.Updater.exe")), "new file removed on rollback");
Check(File.ReadAllText(Path.Combine(root, "rollback", "status.txt")) == "rolled-back", "rollback journal");
Reject(() => UpdateTransaction.Apply(install, extracted, files.Reverse().ToArray(), Path.Combine(root, "rollback-created"), i =>
{
    if (i == 2) throw new IOException("failure after adding updater");
}), "rollback removes newly copied files");
Check(!File.Exists(Path.Combine(install, "updater", "CMUmcl.Updater.exe")), "created updater removed by rollback");
Check(File.ReadAllText(Path.Combine(install, "CMUmcl.dll")) == "old dll", "rollback after new file restores existing DLL");
using (var lockedFile = new FileStream(Path.Combine(install, "CMUmcl.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
{
    Reject(() => UpdateTransaction.Apply(install, extracted, files, Path.Combine(root, "rollback-locked")), "locked target rejects update");
    Check(File.ReadAllText(Path.Combine(install, "CMUmcl.exe")) == "old exe", "earlier file restored after real lock failure");
    Check(File.ReadAllText(Path.Combine(root, "rollback-locked", "status.txt")) == "rolled-back", "real lock rollback completes");
}
Check(!Directory.EnumerateFiles(install, "*.cmumcl-update-*.tmp", SearchOption.AllDirectories).Any(), "replacement temporaries removed");
UpdateTransaction.Apply(install, extracted, files, Path.Combine(root, "success")); passed++;
Check(File.ReadAllText(Path.Combine(install, "CMUmcl.exe")) == "new CMUmcl.exe", "new exe installed");
Check(File.ReadAllText(Path.Combine(root, "success", "files", "CMUmcl.exe")) == "old exe", "old exe backed up");
Check(File.ReadAllText(Path.Combine(install, "settings.json")) == "settings", "settings preserved");
Check(File.ReadAllText(Path.Combine(install, "instances", "test", "keep")) == "game", "instance preserved");
Check(File.ReadAllText(Path.Combine(install, "unmanaged.txt")) == "user file", "unmanaged file preserved");
Reject(() => UpdateTransaction.Apply(install, extracted, files, Path.Combine(root, "success")), "existing backup cannot be overwritten");
Reject(() => UpdateTransaction.AssertSafeTarget(install, "../outside"), "target traversal");
Check(UpdateProtocol.InstallLockName(install) == UpdateProtocol.InstallLockName(install + Path.DirectorySeparatorChar), "stable install lock");
Reject(() => UpdateProtocol.EnsureFreeSpace(root, long.MaxValue - UpdateProtocol.DiskReserve), "insufficient disk space fails closed");
Console.WriteLine($"PASS {passed} assertions. Isolated artifacts: {root}");
