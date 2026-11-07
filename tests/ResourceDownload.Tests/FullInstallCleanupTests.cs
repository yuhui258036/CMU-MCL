using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using CMUmcl.Content;

// A full reinstall over an existing pack must not leave jars that the previous revision managed.
internal static class FullInstallCleanupTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "cmumcl-full-install-" + Guid.NewGuid().ToString("N"));
        string? oldRoot = LauncherPaths.RootOverride;
        LauncherPaths.RootOverride = Path.Combine(root, "data");
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            byte[] modA1 = Encoding.UTF8.GetBytes("mod-a revision 1"), modA2 = Encoding.UTF8.GetBytes("mod-a revision 2"), dropped = Encoding.UTF8.GetBytes("dropped mod");
            var installed = new InstalledStateStore();
            using var installer = new PackInstaller(installed, new FixtureTransport(blobs));

            await installer.InstallAsync(Publish(blobs, 1, ("mods/a-1.jar", modA1), ("mods/dropped.jar", dropped)));
            string mods = Path.Combine(LauncherPaths.InstancesDirectory, installed.GetInstallPath("full-cleanup"), "mods");
            check(File.Exists(Path.Combine(mods, "a-1.jar")) && File.Exists(Path.Combine(mods, "dropped.jar")), "full install: first revision installs managed mods");
            File.WriteAllText(Path.Combine(mods, "user-added.jar"), "user");

            // No file index is published, so the update takes the full-install path.
            await installer.InstallAsync(Publish(blobs, 2, ("mods/a-2.jar", modA2)));
            check(File.Exists(Path.Combine(mods, "a-2.jar")) && !File.Exists(Path.Combine(mods, "a-1.jar")) && !File.Exists(Path.Combine(mods, "dropped.jar")),
                "full install: update removes jars the previous revision managed");
            check(File.ReadAllText(Path.Combine(mods, "user-added.jar")) == "user", "full install: update keeps user-added mods");
        }
        finally
        {
            LauncherPaths.RootOverride = oldRoot;
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static ManifestInstance Publish(Dictionary<string, byte[]> blobs, int revision, params (string Path, byte[] Content)[] files)
    {
        var entries = files.Select(file =>
        {
            string name = $"r{revision}-{Path.GetFileName(file.Path)}";
            blobs["/files/" + name] = file.Content;
            return new
            {
                path = file.Path,
                downloads = new[] { $"https://{ResourceManifestClient.AllowedPackHost}/files/{name}" },
                hashes = new { sha1 = Compat.Hex(Compat.Sha1(file.Content)), sha512 = Compat.Hex(Compat.Sha512(file.Content)) },
                fileSize = file.Content.Length,
            };
        }).ToArray();
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var index = zip.CreateEntry("modrinth.index.json").Open())
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new
            {
                formatVersion = 1, game = "minecraft", versionId = revision.ToString(), name = "Full cleanup", files = entries,
                dependencies = new Dictionary<string, string> { ["minecraft"] = "1.21.1", ["fabric-loader"] = "0.16.10" },
            });
            index.Write(json, 0, json.Length);
        }
        byte[] pack = buffer.ToArray();
        blobs[$"/packs/full-cleanup-{revision}.mrpack"] = pack;
        return new ManifestInstance
        {
            Id = "full-cleanup", Name = "Full cleanup", Revision = revision,
            Minecraft = new ManifestMinecraft { GameVersion = "1.21.1", Loader = "fabric", LoaderVersion = "0.16.10" },
            Pack = new ManifestPack
            {
                Kind = "mrpack", Url = $"https://{ResourceManifestClient.AllowedPackHost}/packs/full-cleanup-{revision}.mrpack",
                Size = pack.Length, Sha256 = Compat.Hex(Compat.Sha256(pack)),
            },
        };
    }

    private sealed class FixtureTransport(Dictionary<string, byte[]> blobs) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.Host != ResourceManifestClient.AllowedPackHost || !blobs.TryGetValue(uri.AbsolutePath, out byte[]? body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request });
        }
    }
}
