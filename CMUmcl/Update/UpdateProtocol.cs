using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace CMUmcl.Update;

public sealed record UpdateSignature(string Algorithm, string Format, string Value);
public sealed record UpdateEnvelope(string Payload, UpdateSignature Signature);
public sealed record UpdateRelease(int FormatVersion, string Platform, string Version, string Url,
    long Size, string Sha256, UpdateSignature ArchiveSignature, string Notes = "");

public static class UpdateProtocol
{
    public const string FeedUrl = "https://csumcl.mcapcs.cn/update/windows-x86_64/latest.json";
    public const long MaxArchiveSize = 1024L * 1024 * 1024;
    public const long DiskReserve = 64L * 1024 * 1024;
    public const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEIbIQhvsp2Edlo/HgtF+OQgZTyf8e
        OcNzeWDk1V6WiQfiOzCGQ/HuWmmwzum1PgkNc6sD2lF6mm/y5/p94MjUOw==
        -----END PUBLIC KEY-----
        """;
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static Version ParseVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Split('.').Length is < 3 or > 4 ||
            value.Any(c => !char.IsAsciiDigit(c) && c != '.') || !Version.TryParse(value, out var version))
            throw new InvalidDataException("更新版本号必须为三段或四段数字。测试版本不进入正式更新通道。");
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }

    public static UpdateRelease ReadRelease(byte[] envelopeBytes, string publicKey = PublicKey)
    {
        if (envelopeBytes.Length > 128 * 1024) throw new InvalidDataException("更新清单过大。");
        var envelope = JsonSerializer.Deserialize<UpdateEnvelope>(envelopeBytes, JsonOptions)
            ?? throw new InvalidDataException("更新清单为空。");
        if (string.IsNullOrWhiteSpace(envelope.Payload) || envelope.Signature is null)
            throw new InvalidDataException("更新服务尚未提供可用的签名更新，请稍后重试。");
        byte[] payload = Convert.FromBase64String(envelope.Payload);
        Verify(payload, envelope.Signature, publicKey, isHash: false);
        var release = JsonSerializer.Deserialize<UpdateRelease>(payload, JsonOptions)
            ?? throw new InvalidDataException("更新信息为空。");
        if (release.FormatVersion != 2 || release.Platform != "windows-x86_64")
            throw new InvalidDataException("不支持的更新格式或平台。");
        _ = ParseVersion(release.Version);
        if (release.Size <= 0 || release.Size > MaxArchiveSize || release.Sha256?.Length != 64 ||
            !release.Sha256.All(char.IsAsciiHexDigit)) throw new InvalidDataException("更新大小或哈希无效。");
        ValidateUrl(new Uri(release.Url, UriKind.Absolute));
        ValidateSignature(release.ArchiveSignature);
        return release;
    }

    public static void ValidateUrl(Uri uri)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort ||
            !uri.Host.Equals("csumcl.mcapcs.cn", StringComparison.OrdinalIgnoreCase) ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0 ||
            !uri.AbsolutePath.StartsWith("/update/windows-x86_64/", StringComparison.Ordinal) ||
            !uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包必须来自指定 CDN 的 HTTPS ZIP 地址。");
    }

    private static void ValidateSignature(UpdateSignature signature)
    {
        if (signature is null || signature.Algorithm != "ECDSA-P256-SHA256" ||
            signature.Format != "ieee-p1363-base64" || Convert.FromBase64String(signature.Value).Length != 64)
            throw new InvalidDataException("更新签名格式无效。");
    }

    private static void Verify(byte[] data, UpdateSignature signature, string publicKey, bool isHash)
    {
        ValidateSignature(signature);
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKey);
        var sig = Convert.FromBase64String(signature.Value);
        bool valid = isHash
            ? key.VerifyHash(data, sig, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            : key.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (!valid) throw new CryptographicException("更新签名校验失败。");
    }

    public static void VerifyArchive(string path, UpdateRelease release, string publicKey = PublicKey)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length != release.Size) throw new InvalidDataException("更新包大小不匹配。");
        var hash = SHA256.HashData(stream);
        if (!Convert.ToHexString(hash).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("更新包 SHA-256 校验失败。");
        Verify(hash, release.ArchiveSignature, publicKey, isHash: true);
    }

    public static string InstallLockName(string directory) => "Local\\CMUmcl.Install." +
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())));

    public static void EnsureFreeSpace(string directory, long requiredBytes)
    {
        long available = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
        long required = checked(requiredBytes + DiskReserve);
        if (available < required)
            throw new IOException($"磁盘空间不足，至少需要 {Math.Ceiling(required / 1048576.0):F0} MiB 可用空间，当前 {Math.Floor(available / 1048576.0):F0} MiB。未开始替换客户端。");
    }

    // A signed archive is still checked for traversal, symlinks, duplicate Windows names and user data.
    public static string[] ExtractArchive(string archivePath, string destination)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        if (zip.Entries.Count > 20000) throw new InvalidDataException("更新包文件数过多。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            bool directory = name.EndsWith('/');
            name = name.TrimEnd('/');
            var parts = name.Split('/');
            if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                p.IndexOfAny(['<', '>', ':', '"', '|', '?', '*', '\0']) >= 0 || p.Any(char.IsControl) ||
                IsDeviceName(p)) || !names.Add(name) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || IsUserData(parts[0]))
                throw new InvalidDataException("更新包包含不安全、重复或用户数据路径：" + name);
            expanded = checked(expanded + entry.Length);
            if (expanded > 4 * MaxArchiveSize) throw new InvalidDataException("更新包解压大小超限。");
            if (!directory) files.Add(name);
        }
        if (!files.Contains("CMUmcl.exe", StringComparer.OrdinalIgnoreCase) ||
            !files.Contains("CMUmcl.dll", StringComparer.OrdinalIgnoreCase) ||
            !files.Contains("updater/CMUmcl.Updater.exe", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包缺少客户端或独立更新程序，必须使用根目录为程序文件的完整发布 ZIP。");
        EnsureFreeSpace(destination, expanded);
        Directory.CreateDirectory(destination);
        foreach (var entry in zip.Entries)
        {
            var path = Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: false);
        }
        return files.ToArray();
    }

    private static bool IsDeviceName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
             (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³'));
    }

    private static bool IsUserData(string name) => new[] { "CMUmclData", "instances", "minecraft", "cache",
        "java", "auth.dat", "settings.json", "installed.json", "keys", "admin.env", "admin-credentials.txt",
        ".git", ".update-backups" }.Contains(name, StringComparer.OrdinalIgnoreCase);
}
