using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CMUmcl;
using CMUmcl.Community;
using CMUmcl.Content;

internal static partial class Program
{
    private static async Task DependencyUiChecks(Window window, InstalledStateStore installed, bool live)
    {
        var report = new List<object>(); bool complete = false; string stage = "Prepare";
        try
        {
            Click(window, "CommunityNavButton");
            var view = Get<UserControl>(window, "CommunityResourceView");
            var http = new DependencyUiHttp();
            var targets = new ResourceTargets(installed);
            using var cf = live ? null : new CurseForgeSource("fixture-dependency-key", http);
            using var mr = live ? null : new ModrinthSource(http);
            using var install = live ? null : new ResourceInstallService(targets, new MrpackImportService(installed), http, () => "fixture-dependency-key");
            var vm = live ? (CommunityResourceViewModel)view.DataContext : new CommunityResourceViewModel(new ResourceCatalog(mr!, cf!), targets, install!, new ResourceIconLoader());
            view.DataContext = vm;
            if (live) await vm.EnsureLoadedAsync();
            foreach (var source in new[] { ResourceSourceId.Modrinth, ResourceSourceId.CurseForge })
            {
                stage = source + "Search";
                vm.BackToResultsCommand.Execute(null); vm.SelectedTarget = null; vm.OnlyCompatible = false;
                vm.Kind = ResourceKind.Mod; vm.SearchText = "iris";
                vm.SelectedSourceOption = vm.SourceOptions.Single(o => o.Value == source);
                await vm.SearchAsync();
                Check(vm.Results.Count > 0 && !vm.HasError, "dependencies UI: Iris search " + source);
                var card = vm.Results.Where(c => c.Title.IndexOf("iris", StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(c => c.Title.Equals("Iris Shaders", StringComparison.OrdinalIgnoreCase) ? 0 : c.Title.Equals("Iris", StringComparison.OrdinalIgnoreCase) ? 1 : 2).First();
                var all = await vm.LoadVersionsForAsync(card, default);
                Console.WriteLine("DEPENDENCY_METADATA " + source + " versions=" + all.Count + " fabric=" + all.Count(v => v.Loaders.Contains("fabric")) + " required=" + all.Count(v => v.Dependencies.Any(d => d.Kind == DependencyKind.Required)) + " declared=" + all.Sum(v => v.Dependencies.Count));
                var version = all.First(v => v.Loaders.Contains("fabric") && v.Dependencies.Any(d => d.Kind == DependencyKind.Required)
                    && v.GameVersions.Any(g => g.Length > 0 && char.IsDigit(g[0])) && v.Files.Any(f => f.Size > 0 && f.Size <= 32L * 1024 * 1024 && f.HasSecureHash));
                string game = version.GameVersions.First(g => g.Length > 0 && char.IsDigit(g[0]));
                var instance = new LocalInstanceService(installed).Create(new CreateLocalInstanceRequest { DisplayName = "前置验收-" + source, GameVersion = game, Loader = "fabric", LoaderVersion = "acceptance" });
                vm.RefreshTargets(); vm.SelectedTarget = vm.TargetInstances.Single(t => t.Id == instance.Id);
                stage = source + "Dependencies";
                await vm.OpenDetailAsync(card);
                var parent = vm.Detail!;
                await vm.OpenVersionAsync(parent.Versions.Single(v => v.Id == version.Id));
                Check(parent.HasDependencies && parent.Dependencies.Any(d => d.Reference.Kind == DependencyKind.Required), "dependencies UI: selected version displays required dependencies " + source);
                Check(parent.DependencyNotice.Contains("必需前置") && parent.DependencyNotice.Contains("另行安装"), "dependencies UI: required dependencies need separate installation");
                var sodium = parent.Dependencies.First(d => d.Reference.Kind == DependencyKind.Required && d.Title.IndexOf("sodium", StringComparison.OrdinalIgnoreCase) >= 0);
                Check(sodium.CanOpen && !sodium.HasError, "dependencies UI: required Sodium metadata resolves " + source);
                if (!live)
                {
                    Check(parent.Dependencies.Any(d => d.Reference.Kind == DependencyKind.Optional) && parent.Dependencies.Any(d => d.Reference.Kind == DependencyKind.Incompatible), "dependencies UI: optional and conflicting groups stay distinct");
                    Check(http.Downloads == (source == ResourceSourceId.Modrinth ? 0 : 2), "dependencies UI: opening dependency list never silently downloads other mods");
                }
                foreach (int width in new[] { 1200, 980 })
                {
                    window.Width = width; window.Height = width == 980 ? 620 : 800;
                    await Task.Delay(100); window.UpdateLayout();
                    var button = Get<Button>(view, "CommunityInstallButton"); var dialog = Get<Grid>(view, "VersionDialog");
                    Rect bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
                    Rect area = dialog.TransformToAncestor(window).TransformBounds(new Rect(dialog.RenderSize));
                    Check(bounds.Bottom <= area.Bottom + 1 && bounds.Top >= area.Top, "dependencies UI: install action remains visible at " + width);
                    Screenshot(window, source + "-iris-dependencies-" + width + ".png");
                }
                stage = source + "OpenSodium";
                string? exact = sodium.Reference.VersionId;
                await vm.OpenDependencyAsync(sodium);
                Check(vm.Detail != null && vm.Detail.Title.IndexOf("sodium", StringComparison.OrdinalIgnoreCase) >= 0 && vm.BackLabel.Contains("上级"), "dependencies UI: required card opens Sodium with parent return context");
                if (!string.IsNullOrEmpty(exact)) Check(vm.Detail!.SelectedVersion?.Id == exact && vm.Detail.Versions.Count == 1, "dependencies UI: pinned dependency version is preserved exactly");
                await vm.OpenVersionAsync(vm.Detail!.SelectedVersion!);
                await InstallDependencyAcceptance(vm.Detail, instance.InstanceDirectory);
                vm.BackToResultsCommand.Execute(null);
                Check(ReferenceEquals(vm.Detail, parent) && parent.SelectedVersion?.Id == version.Id && parent.SelectedInstance?.Id == instance.Id, "dependencies UI: back restores Iris version and target instance");
                await parent.EnsureDependenciesAsync();
                stage = source + "InstallIris";
                await InstallDependencyAcceptance(parent, instance.InstanceDirectory);
                Check(Directory.GetFiles(Path.Combine(instance.InstanceDirectory, "mods"), "*.jar").Length == 2, "dependencies UI: only explicitly installed Iris and Sodium are committed");
                report.Add(new { source = source.ToString(), required_count = parent.Dependencies.Count(d => d.Reference.Kind == DependencyKind.Required), sodium_resolved = sodium.CanOpen, pinned_version_honored = string.IsNullOrEmpty(exact) || sodium.Resolution?.PinnedVersion?.Id == exact, parent_context_preserved = true, iris_and_sodium_installed_and_verified = true, game });
                Screenshot(window, source + "-iris-installed.png");
                vm.CloseVersionDialogCommand.Execute(null); vm.BackToResultsCommand.Execute(null);
            }
            window.Width = 1200; window.Height = 800;
            await vm.OpenDetailAsync(vm.Results[0]); await vm.OpenVersionAsync(vm.Detail!.SelectedVersion!);
            Click(window, "ThemeToggleButton"); await Task.Delay(100); Screenshot(window, "dependency-dialog-dark.png"); Click(window, "ThemeToggleButton");
            complete = true; stage = "Complete";
            File.WriteAllText(Path.Combine(_output, "result.txt"), $"PASS {_checks} dependency UI checks");
        }
        finally
        {
            File.WriteAllText(Path.Combine(_output, "dependency-report.json"), JsonSerializer.Serialize(new { schema = 1, success = complete, live, stage, only_explicit_downloads = true, game_launched = false, sources = report }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    private static async Task InstallDependencyAcceptance(ResourceDetailViewModel detail, string instanceDirectory)
    {
        await detail.EnsureDependenciesAsync();
        var file = detail.SelectedVersion!.Files.OrderByDescending(f => f.Primary).First();
        Check(file.Size > 0 && file.Size <= 32L * 1024 * 1024 && file.HasSecureHash && detail.CanInstall, "dependencies UI: explicit install selection is bounded and verifiable");
        detail.InstallCommand.Execute(null); await LiveUntil(() => !detail.IsInstalling, TimeSpan.FromMinutes(5));
        string path = Path.Combine(instanceDirectory, "mods", file.FileName);
        Check(detail.Status == "安装完成" && File.Exists(path), "dependencies UI: explicit dependency or parent install completes");
        using var hash = SHA1.Create(); using var stream = File.OpenRead(path);
        string actual = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        Check(new FileInfo(path).Length == file.Size && (string.IsNullOrEmpty(file.Sha1) || actual == file.Sha1.ToLowerInvariant()), "dependencies UI: committed file size and SHA-1 match source metadata");
    }
    private sealed class DependencyUiHttp : HttpMessageHandler
    {
        public int Downloads;
        private static byte[] Bytes(bool sodium) => Encoding.UTF8.GetBytes(sodium ? "synthetic-sodium-dependency" : "synthetic-iris-parent");
        private static string Hash(bool sodium) { using var hash = SHA1.Create(); return BitConverter.ToString(hash.ComputeHash(Bytes(sodium))).Replace("-", "").ToLowerInvariant(); }
        private static object MrVersion(bool sodium) => new { id = sodium ? "pin-sodium" : "iris-version", project_id = sodium ? "sodium" : "iris", version_number = sodium ? "Sodium exact 0.6" : "Iris 1.11.4 Fabric", date_published = "2026-11-01T00:00:00Z", game_versions = new[] { "1.21.1" }, loaders = new[] { "fabric" }, dependencies = sodium ? Array.Empty<object>() : new object[] { new { project_id = (string?)null, version_id = "pin-sodium", dependency_type = "required" }, new { project_id = "modmenu", dependency_type = "optional" }, new { project_id = "conflict", dependency_type = "incompatible" } }, files = new[] { new { filename = sodium ? "sodium.jar" : "iris.jar", url = "https://cdn.modrinth.com/data/" + (sodium ? "sodium.jar" : "iris.jar"), size = Bytes(sodium).Length, primary = true, hashes = new { sha1 = Hash(sodium) } } } };
        private static object CfFile(bool sodium) => new { id = sodium ? 200 : 100, modId = sodium ? 2 : 1, isAvailable = true, displayName = sodium ? "Sodium compatible 0.6" : "Iris 1.11.4 Fabric", fileName = sodium ? "sodium.jar" : "iris.jar", fileLength = Bytes(sodium).Length, gameVersions = new[] { "1.21.1" }, sortableGameVersions = new[] { new { gameVersionName = "Fabric" } }, dependencies = sodium ? Array.Empty<object>() : new object[] { new { modId = 2, relationType = 3 }, new { modId = 3, relationType = 2 }, new { modId = 4, relationType = 5 } }, hashes = new[] { new { algo = 1, value = Hash(sodium) } } };
        private static string Title(string id) => id is "2" or "sodium" ? "Sodium" : id is "3" or "modmenu" ? "Mod Menu" : id is "4" or "conflict" ? "Conflicting Renderer" : "Iris Shaders";
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!; string path = uri.AbsolutePath;
            if (uri.Host.EndsWith("forgecdn.net") || uri.Host == "cdn.modrinth.com") { Downloads++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes(path.Contains("sodium"))) }); }
            if (uri.Host == "api.curseforge.com")
            {
                if (path.EndsWith("/search")) return Task.FromResult(Json(new { data = new[] { new { id = 1, name = "Iris Shaders", summary = "Shader renderer with required Sodium dependency" } } }));
                bool sodium = path.Contains("/mods/2");
                if (path.EndsWith("download-url")) return Task.FromResult(Json(new { data = "https://edge.forgecdn.net/files/1/2/" + (sodium ? "sodium.jar" : "iris.jar") }));
                if (path.EndsWith("/files")) return Task.FromResult(Json(new { data = new[] { CfFile(sodium) } }));
                if (path.Contains("/files/")) return Task.FromResult(Json(new { data = CfFile(sodium) }));
                string id = path.Split('/').Last(); return Task.FromResult(Json(new { data = new { id = int.Parse(id), name = Title(id), summary = "Source-declared dependency fixture", allowModDistribution = true } }));
            }
            if (path.EndsWith("/search")) return Task.FromResult(Json(new { hits = new[] { new { project_id = "iris", slug = "iris", title = "Iris Shaders", description = "Shader renderer with required Sodium dependency", versions = new[] { "1.21.1" }, categories = new[] { "fabric" } } } }));
            if (path.Contains("/version/")) return Task.FromResult(Json(MrVersion(true)));
            if (path.EndsWith("/version")) return Task.FromResult(Json(new[] { MrVersion(path.Contains("/sodium/")) }));
            string project = path.Split('/').Last(); return Task.FromResult(Json(new { id = project, slug = project, title = Title(project), description = "Source-declared dependency fixture" }));
        }
    }
}
