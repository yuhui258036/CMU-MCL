using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMUmcl.Content;

internal static class BackendInstallTests
{
    public static async Task Run(string backendAssembly, Action<bool, string> check)
    {
        backendAssembly = Path.GetFullPath(backendAssembly);
        if (!File.Exists(backendAssembly)) throw new FileNotFoundException("Build the backend before the cross-repository test.", backendAssembly);
        var root = Path.Combine(Path.GetTempPath(), "cmumcl-backend-install-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "server-data");
        var web = Path.Combine(root, "web");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.Combine(web, "files"));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyPath = Path.Combine(root, "key.pem");
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(Path.Combine(data, "instances.json"), """[{"id":"integration","name":"Isolated integration pack","revision":0}]""");
        var adminPort = FreePort();
        var publicPort = FreePort();
        while (publicPort == adminPort) publicPort = FreePort();
        var adminOrigin = $"http://127.0.0.1:{adminPort}";
        var publicOrigin = $"http://127.0.0.1:{publicPort}";
        // Signed URLs use the client's allowlisted name. Test transports below
        // map ONLY that host to this owned loopback listener; no Internet traffic.
        File.WriteAllText(Path.Combine(root, "appsettings.json"), JsonSerializer.Serialize(new
        {
            PublicBaseUrl = ResourceManifestClient.BaseUrl, AdminPort = adminPort, DataDir = data, WwwRoot = web,
            UploadLimitBytes = 4 * 1024 * 1024, ManifestSigningKeyPath = keyPath,
            ChunkUpload = new { ChunkSizeBytes = 65536, MinFreeBytes = 0, QuotaBytes = 16 * 1024 * 1024,
                PublicIngressEnabled = true, PublicUploadBaseUrl = ResourceManifestClient.BaseUrl, AllowedOrigins = new[] { adminOrigin } },
            Kestrel = new { Endpoints = new { Public = new { Url = publicOrigin }, Admin = new { Url = adminOrigin } } },
            Logging = new { LogLevel = new Dictionary<string, string> { ["Default"] = "Warning" } }
        }));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var salt = RandomNumberGenerator.GetBytes(16);
        var passwordHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100000, HashAlgorithmName.SHA256, 32);
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(backendAssembly);
        start.Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";
        start.Environment["PCL_CMU_ADMIN_PASSWORD_HASH"] = $"pbkdf2$100000${Convert.ToBase64String(salt)}${Convert.ToBase64String(passwordHash)}";
        Process? process = null;
        var errors = new ConcurrentQueue<string>();
        var requests = new ConcurrentQueue<string>();
        var oldRoot = LauncherPaths.RootOverride;
        LauncherPaths.RootOverride = Path.Combine(root, "client-data");
        if (!string.IsNullOrWhiteSpace(SettingsStore.Current.InstancesDirectory))
            throw new InvalidOperationException("Integration tests require default isolated instance settings.");
        using var adminHandler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, CookieContainer = new CookieContainer() };
        using var admin = new HttpClient(adminHandler) { BaseAddress = new Uri(adminOrigin), Timeout = TimeSpan.FromSeconds(15) };
        using var ingress = new HttpClient(new HttpClientHandler { UseProxy = false, UseCookies = false })
            { BaseAddress = new Uri(publicOrigin), Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            process = Process.Start(start)!;
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) errors.Enqueue(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) errors.Enqueue(e.Data); };
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            var listening = false;
            for (var attempt = 0; attempt < 120; attempt++)
            {
                if (process.HasExited) throw new Exception("Test backend exited: " + string.Join("\n", errors));
                try { using var response = await admin.GetAsync("/admin/login"); listening = response.IsSuccessStatusCode; }
                catch (HttpRequestException) { }
                if (listening) break;
                await Task.Delay(50);
            }
            if (!listening) throw new TimeoutException("Test backend did not start.");
            var csrf = adminHandler.CookieContainer.GetCookies(new Uri(adminOrigin + "/admin"))["pcl_cmu_csrf"]!.Value;
            using (var login = await admin.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
                { ["password"] = password, ["_csrf"] = csrf })))
                check(login.StatusCode == HttpStatusCode.Redirect, "cross-repo: isolated backend login");
            admin.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);

            var payloads = new[] { RandomNumberGenerator.GetBytes(130000), RandomNumberGenerator.GetBytes(140000), RandomNumberGenerator.GetBytes(140000) };
            var stableOverride = RandomNumberGenerator.GetBytes(170000);
            var obsolete = RandomNumberGenerator.GetBytes(128);
            File.WriteAllBytes(Path.Combine(web, "files", "obsolete.bin"), obsolete);
            using var manifests = new ResourceManifestClient(new LoopbackTransport(publicPort, requests),
                new Uri(ResourceManifestClient.BaseUrl + "/"), key.ExportSubjectPublicKeyInfoPem());
            var installed = new InstalledStateStore();
            using var installer = new PackInstaller(installed, new LoopbackTransport(publicPort, requests));
            for (var revision = 1; revision <= 3; revision++)
            {
                var payload = payloads[revision - 1];
                File.WriteAllBytes(Path.Combine(web, "files", $"mod-{revision}.bin"), payload);
                var pack = MakePack(root, revision, payload, stableOverride, obsolete);
                await Upload(pack, revision - 1);
                var manifest = await manifests.GetManifestAsync();
                var instance = manifest.Instances.Single();
                check(instance.Revision == revision && instance.Minecraft.GameVersion == "1.21.1",
                    $"cross-repo: client verifies backend signed manifest r{revision}");
                while (requests.TryDequeue(out _)) { }
                await installer.InstallAsync(instance);
                var instancePath = Path.Combine(LauncherPaths.InstancesDirectory, installed.GetInstallPath(instance.Id));
                check(installed.GetInstalledRevision(instance.Id) == revision, $"cross-repo: installed revision r{revision} persisted");
                check(File.ReadAllBytes(Path.Combine(instancePath, "mods", "test.bin")).SequenceEqual(payload),
                    $"cross-repo: downloaded mod content r{revision} matches uploaded index");
                if (revision == 1)
                {
                    check(requests.Contains("/packs/integration/1.mrpack"), "cross-repo: first install downloads the full published pack");
                    check(File.ReadAllText(Path.Combine(instancePath, "config", "test.txt")) == "server-v1", "cross-repo: first install extracts overrides");
                    File.WriteAllText(Path.Combine(instancePath, "config", "test.txt"), "user-config");
                    Directory.CreateDirectory(Path.Combine(instancePath, "saves", "private-world"));
                    File.WriteAllText(Path.Combine(instancePath, "saves", "private-world", "level.dat"), "preserve-world");
                }
                else if (revision == 2)
                {
                    check(requests.Contains("/packs/integration/2.files.json") && !requests.Contains("/packs/integration/2.mrpack"),
                        "cross-repo: unchanged overrides use index-only incremental update");
                    check(!File.Exists(Path.Combine(instancePath, "mods", "obsolete.bin")), "cross-repo: incremental update removes obsolete managed file");
                    check(File.ReadAllText(Path.Combine(instancePath, "config", "test.txt")) == "user-config", "cross-repo: incremental update preserves user config");
                }
                else
                {
                    check(requests.Contains("/packs/integration/3.mrpack"), "cross-repo: changed overrides fall back to full pack");
                    check(File.ReadAllText(Path.Combine(instancePath, "config", "test.txt")) == "server-v3", "cross-repo: changed server overrides are installed");
                }
                if (revision > 1)
                    check(File.ReadAllText(Path.Combine(instancePath, "saves", "private-world", "level.dat")) == "preserve-world",
                        $"cross-repo: update r{revision} preserves user world");
            }
            check(requests.All(p => p.StartsWith('/')), "cross-repo: all client requests stayed on the owned test listener");

            async Task Upload(string path, int baseRevision)
            {
                var bytes = File.ReadAllBytes(path);
                using var response = await admin.PostAsJsonAsync("/admin/api/uploads", new
                {
                    instanceId = "integration", fileName = Path.GetFileName(path), size = bytes.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(bytes)), baseRevision
                });
                response.EnsureSuccessStatusCode();
                using var created = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
                var id = created.RootElement.GetProperty("id").GetString()!;
                var chunkSize = created.RootElement.GetProperty("chunkSize").GetInt32();
                var count = created.RootElement.GetProperty("chunkCount").GetInt32();
                using var renewal = await admin.PostAsync($"/admin/api/uploads/{id}/renew", null);
                renewal.EnsureSuccessStatusCode();
                using var grant = JsonDocument.Parse(await renewal.Content.ReadAsByteArrayAsync());
                var token = grant.RootElement.GetProperty("token").GetString()!;
                // Reverse order verifies that completion uses index order, not arrival order.
                for (var index = count - 1; index >= 0; index--)
                {
                    var chunk = bytes.Skip(index * chunkSize).Take(chunkSize).ToArray();
                    using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/uploads/{id}/chunks/{index}");
                    put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    put.Headers.Add("Origin", adminOrigin);
                    put.Headers.Add("X-Chunk-SHA256", Convert.ToHexString(SHA256.HashData(chunk)));
                    put.Content = new ByteArrayContent(chunk);
                    put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    using var received = await ingress.SendAsync(put);
                    received.EnsureSuccessStatusCode();
                }
                using var complete = await admin.PostAsync($"/admin/api/uploads/{id}/complete", null);
                complete.EnsureSuccessStatusCode();
                for (var attempt = 0; attempt < 200; attempt++)
                {
                    using var statusResponse = await admin.GetAsync($"/admin/api/uploads/{id}");
                    statusResponse.EnsureSuccessStatusCode();
                    using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsByteArrayAsync());
                    var state = status.RootElement.GetProperty("state").GetString();
                    if (state == "completed") { check(status.RootElement.GetProperty("revision").GetInt32() == baseRevision + 1, $"cross-repo: chunk upload published revision {baseRevision + 1}"); return; }
                    if (state == "failed") throw new Exception("Publication failed: " + status.RootElement.GetProperty("error").GetString());
                    await Task.Delay(25);
                }
                throw new TimeoutException("Publication did not complete.");
            }
        }
        finally
        {
            LauncherPaths.RootOverride = oldRoot;
            if (process is not null)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(); process.Dispose();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private static string MakePack(string root, int revision, byte[] payload, byte[] stableOverride, byte[] obsolete)
    {
        object Entry(string path, string file, byte[] content) => new
        {
            path, downloads = new[] { ResourceManifestClient.BaseUrl + "/files/" + file },
            hashes = new { sha1 = Convert.ToHexString(SHA1.HashData(content)), sha512 = Convert.ToHexString(SHA512.HashData(content)) }, fileSize = content.Length
        };
        var files = new List<object> { Entry("mods/test.bin", $"mod-{revision}.bin", payload) };
        if (revision == 1) files.Add(Entry("mods/obsolete.bin", "obsolete.bin", obsolete));
        var path = Path.Combine(root, $"test-{revision}.mrpack");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var index = zip.CreateEntry("modrinth.index.json").Open())
            index.Write(JsonSerializer.SerializeToUtf8Bytes(new { formatVersion = 1, game = "minecraft", versionId = revision.ToString(), name = "Test", files,
                dependencies = new Dictionary<string, string> { ["minecraft"] = "1.21.1", ["fabric-loader"] = "0.16.10" } }));
        using (var configuration = zip.CreateEntry("overrides/config/test.txt").Open()) configuration.Write(Encoding.UTF8.GetBytes(revision == 3 ? "server-v3" : "server-v1"));
        using (var padding = zip.CreateEntry("overrides/test-data.bin").Open()) padding.Write(stableOverride);
        return path;
    }
    private static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
    private sealed class LoopbackTransport(int port, ConcurrentQueue<string> requests) : DelegatingHandler(
        new HttpClientHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.Scheme != "https" || uri.Host != ResourceManifestClient.AllowedPackHost)
                throw new InvalidOperationException("Test refused a non-fixture destination.");
            requests.Enqueue(uri.AbsolutePath);
            request.RequestUri = new UriBuilder(uri) { Scheme = "http", Host = "127.0.0.1", Port = port }.Uri;
            return base.SendAsync(request, ct);
        }
    }
}
