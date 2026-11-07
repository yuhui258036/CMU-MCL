using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CMUmcl;
using CMUmcl.Appearance;
using CMUmcl.Community;
using CMUmcl.Content;
using CMUmcl.Launch;

internal static partial class Program
{
    private static T Get<T>(UserControl root, string name) where T : class => (T)root.FindName(name);
    private static CommunityResourceViewModel CreateCommunityFixture(InstalledStateStore installed)
    {
        var http = new ResourceUiTransport();
        var targets = new ResourceTargets(installed);
        return new CommunityResourceViewModel(new ResourceCatalog(new ModrinthSource(http), new CurseForgeSource("fixture-ui-key", http)), targets,
            new ResourceInstallService(targets, new MrpackImportService(installed), http, () => "fixture-ui-key"), new ResourceIconLoader());
    }
    private static async Task NavigationResourceChecks(MainWindow window, InstalledStateStore installed, LaunchService launch)
    {
        var create = Get<CreateInstanceView>(window, "CreateInstanceView");
        create.Open(new LocalInstanceService(installed), launch);
        SetPrivate(create, "_selectedGameVersion", new GameVersionRow(new MinecraftVersionInfo("1.21.1", "release", DateTimeOffset.UtcNow, MinecraftVersionCategory.Release)));
        Get<TextBox>(create, "NameBox").Text = "切页不中断测试";
        var gate = new TaskCompletionSource<bool>();
        CancellationToken operationToken = default;
        create.ProvisionOverride = async (_, progress, ct) =>
        {
            operationToken = ct;
            progress.Report(new LaunchProgress { Stage = "下载", Message = "受控下载夹具" });
            await gate.Task;
            ct.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(_output, "download-finished.txt"), "verified fixture completion");
        };
        Task operation = CallAsync(create, "InstallAsync");
        await Until(() => create.HasActiveOperation);
        var draft = (LocalInstanceResult)typeof(CreateInstanceView).GetField("_result", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(create)!;
        Check(!installed.ListRecords().ContainsKey(draft.Id), "instance: unfinished download is not published to installed records");
        Check(!new ResourceTargets(installed).EnumerateInstances().Any(t => t.Id == draft.Id), "instance: unfinished download cannot be selected for community installation");
        object services = typeof(CreateInstanceView).GetField("_catalog", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(create)!;
        foreach (string page in new[] { "SettingsNavButton", "CommunityNavButton", "PacksNavButton", "InstancesNavButton" })
        {
            Click(window, page);
            Check(!operationToken.IsCancellationRequested && !operation.IsCompleted, "instance: navigation preserves pending download " + page);
            Check(Get<Border>(window, "CreateActivityBanner").IsVisible, "instance: task return entry visible " + page);
            if (page == "InstancesNavButton") Check(!Get<ItemsControl>(window, "InstancesList").Items.OfType<InstanceCardViewModel>().Any(i => i.InstanceId == draft.Id), "instance: pending download stays out of instance cards");
            if (page == "CommunityNavButton") {
                await Task.Delay(100); window.UpdateLayout();
                var bar = Get<Border>(window, "CreateActivityBanner"); var resource = Get<UserControl>(window, "CommunityResourceView"); var pager = Get<FrameworkElement>(resource, "ResourcePagingBar");
                Rect activity = bar.TransformToAncestor(window).TransformBounds(new Rect(bar.RenderSize)); Rect paging = pager.TransformToAncestor(window).TransformBounds(new Rect(pager.RenderSize));
                Check(!activity.IntersectsWith(paging) && activity.Top >= paging.Bottom, "instance: docked task status does not cover community pagination");
                var initial = (CommunityResourceViewModel)resource.DataContext;
                Check(initial.Results.Count > 0 && !initial.IsLoading, "resource: entering community automatically loads initial list");
                var cards = initial.Results.ToArray(); await initial.EnsureLoadedAsync(); Check(cards.SequenceEqual(initial.Results), "resource: subsequent entries preserve already-loaded cards");
                Screenshot(window, "taskbar-and-pagination.png");
            }
        }
        Screenshot(window, "instance-background-download.png");
        Click(window, "NewInstanceButton");
        Check(create.IsVisible && create.HasActiveOperation && ReferenceEquals(services, typeof(CreateInstanceView).GetField("_catalog", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(create)), "instance: reopening resumes same services and download");
        Click(window, "SettingsNavButton");
        gate.SetResult(true); await operation; await Task.Delay(50);
        Check(installed.ListRecords().ContainsKey(draft.Id), "instance: completed preparation publishes installed record");
        Check(File.Exists(Path.Combine(_output, "download-finished.txt")) && !operationToken.IsCancellationRequested, "instance: hidden download completes without cancellation");
        Check(Get<Grid>(window, "SettingsScroll").IsVisible && Get<Border>(window, "CreateActivityBanner").IsVisible, "instance: completion preserves current page with result entry");
        Call(window, "ResumeCreateActivity_Click", window, new RoutedEventArgs());
        await Task.Delay(100); window.UpdateLayout();
        Check(create.IsVisible && Get<FrameworkElement>(create, "SuccessPanel").IsVisible, "instance: completed task resumes success page");
        create.Close(); create.Open(new LocalInstanceService(installed), launch);
        Check(!create.HasPendingTask && Get<FrameworkElement>(create, "LandingPanel").IsVisible, "instance: finished flow can start fresh after explicit close");
        create.ProvisionOverride = null; create.Close();
        Click(window, "InstancesNavButton");
        Check(Get<ItemsControl>(window, "InstancesList").Items.OfType<InstanceCardViewModel>().Any(i => i.InstanceId == draft.Id), "instance: ready instance appears after successful completion");
        create.Open(new LocalInstanceService(installed), launch);
        SetPrivate(create, "_selectedGameVersion", new GameVersionRow(new MinecraftVersionInfo("1.21.1", "release", DateTimeOffset.UtcNow, MinecraftVersionCategory.Release)));
        Get<TextBox>(create, "NameBox").Text = "失败后重试";
        create.ProvisionOverride = (_, _, _) => throw new LaunchException("受控准备失败");
        await CallAsync(create, "InstallAsync");
        var failed = (LocalInstanceResult)typeof(CreateInstanceView).GetField("_result", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(create)!;
        Check(!installed.ListRecords().ContainsKey(failed.Id) && Directory.Exists(failed.InstanceDirectory), "instance: failed preparation preserves draft without publishing it");
        create.ProvisionOverride = (_, _, _) => Task.CompletedTask;
        await CallAsync(create, "InstallAsync");
        Check(installed.ListRecords().ContainsKey(failed.Id), "instance: successful retry publishes the same draft");
        create.Close();
        create.Open(new LocalInstanceService(installed), launch);
        SetPrivate(create, "_selectedGameVersion", new GameVersionRow(new MinecraftVersionInfo("1.21.1", "release", DateTimeOffset.UtcNow, MinecraftVersionCategory.Release)));
        Get<TextBox>(create, "NameBox").Text = "取消准备";
        create.ProvisionOverride = (_, _, ct) => Task.Delay(Timeout.Infinite, ct);
        Task canceledPreparation = CallAsync(create, "InstallAsync");
        var canceledDraft = (LocalInstanceResult)typeof(CreateInstanceView).GetField("_result", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(create)!;
        create.Close(); await canceledPreparation;
        Check(!installed.ListRecords().ContainsKey(canceledDraft.Id), "instance: canceled preparation is never published as ready");
        create.ProvisionOverride = null;

        var preferences = new SettingsStore(); var prior = preferences.Load(); string? priorJava = prior.JavaPath;
        prior.JavaPath = CreateJavaFixture("draft-java", "21.0.5", "x64"); preferences.Save(prior);
        try
        {
            var draftService = new LocalInstanceService(installed);
            var cachedDraft = draftService.Create(new CreateLocalInstanceRequest { DisplayName = "未登记缓存准备", GameVersion = "1.21.1", Loader = "vanilla", DeferRegistration = true });
            string cachedVersion = "acceptance-cached-draft";
            string cachedDirectory = Path.Combine(LauncherPaths.MinecraftDirectory, "versions", cachedVersion); Directory.CreateDirectory(cachedDirectory);
            File.WriteAllText(Path.Combine(cachedDirectory, cachedVersion + ".json"), "{}");
            Check(ProvisioningMarker.TryWrite(cachedDraft.InstanceDirectory, ProvisioningMarker.ComputeSignature("1.21.1", "vanilla", ""), cachedVersion), "instance: cached draft fixture is prepared independently of installed records");
            string preparedVersion = await launch.PrepareLocalInstanceAsync(cachedDraft);
            Check(preparedVersion == cachedVersion && !installed.ListRecords().ContainsKey(cachedDraft.Id), "instance: real preparation API works for an unregistered draft without publishing early");
            draftService.RegisterPrepared(cachedDraft);
            Check(installed.ListRecords().ContainsKey(cachedDraft.Id), "instance: prepared draft is published only by explicit completion");
        }
        finally { var restore = preferences.Load(); restore.JavaPath = priorJava; preferences.Save(restore); }

        Check(new ResourceVersion { GameVersions = new[] { "Client", "Fabric", "1.21.1" } }.GameLabel == "1.21.1", "resource: game groups exclude CurseForge client and loader metadata");
        // Real production source adapters and installer with isolated HTTP fixtures.
        var transport = new ResourceUiTransport();
        using var cf = new CurseForgeSource("fixture-ui-key", transport);
        using var mr = new ModrinthSource(transport);
        var targets = new ResourceTargets(installed);
        var service = new LocalInstanceService(installed);
        var target = service.Create(new CreateLocalInstanceRequest { DisplayName = "社区界面验收", GameVersion = "1.21.1", Loader = "fabric", LoaderVersion = "0.16.10" });
        using var install = new ResourceInstallService(targets, new MrpackImportService(installed), transport, () => "fixture-ui-key");
        var vm = new CommunityResourceViewModel(new ResourceCatalog(mr, cf), targets, install, new ResourceIconLoader());
        Click(window, "CommunityNavButton");
        var view = Get<UserControl>(window, "CommunityResourceView"); view.DataContext = vm;
        vm.RefreshTargets(); vm.SelectedTarget = vm.TargetInstances.Single(t => t.Id == target.Id);
        vm.OnlyCompatible = false; vm.SearchText = "Sodium";
        foreach (var source in new[] { ResourceSourceId.CurseForge, ResourceSourceId.Modrinth })
        {
            vm.SelectedSourceOption = vm.SourceOptions.Single(o => o.Value == source);
            await vm.SearchAsync(); await Task.Delay(100); window.UpdateLayout();
            Check(vm.Results.Count == 50 && !vm.HasError && !vm.IsDetailPage, "resource: selected source full-width results " + source);
            Check(vm.NextPageCommand.CanExecute(null), "resource: next page becomes enabled after search");
            vm.NextPageCommand.Execute(null); await Until(() => !vm.IsLoading);
            Check(vm.PageLabel == "第 2 页" && vm.Results.Count == 3 && !vm.NextPageCommand.CanExecute(null), "resource: pagination uses real source offsets and end state");
            vm.FirstPageCommand.Execute(null); await Until(() => !vm.IsLoading);
            foreach (var size in new[] { (width: 1200d, height: 800d), (width: 980d, height: 620d) })
            {
                window.Width = size.width; window.Height = size.height; await Task.Delay(80); window.UpdateLayout();
                var filter = Get<FrameworkElement>(view, "SearchFilters");
                Check(filter.ActualWidth <= view.ActualWidth && filter.ActualWidth > 500, "resource: labeled filters fit " + size.width);
                var input = Get<TextBox>(view, "ResourceSearchText"); var sourceBox = Get<ComboBox>(view, "SourceFilter");
                input.ApplyTemplate(); sourceBox.ApplyTemplate();
                var textFrame = (Border)input.Template.FindName("InputFrame", input); var comboFrame = (Border)sourceBox.Template.FindName("border", sourceBox);
                Check(textFrame.CornerRadius == new CornerRadius(6) && comboFrame.CornerRadius == new CornerRadius(6) && sourceBox.FocusVisualStyle == null && Math.Abs(input.ActualHeight-sourceBox.ActualHeight)<1, "resource: inputs use the launcher rounded style at equal height " + size.width);
                Check(Descendants(sourceBox).OfType<TextBlock>().Any(t => t.Text == vm.SelectedSourceOption!.Label), "resource: selector renders display label rather than type name");
                sourceBox.IsDropDownOpen = true; await Task.Delay(50); Check(((System.Windows.Controls.Primitives.Popup)sourceBox.Template.FindName("PART_Popup", sourceBox)).IsOpen, "resource: source selector still opens its menu"); sourceBox.IsDropDownOpen = false;
                Screenshot(window, source + "-results-" + size.width + ".png");
            }
            int grants = transport.Grants;
            await vm.OpenDetailAsync(vm.Results[0]); await Task.Delay(100); window.UpdateLayout();
            Check(vm.IsDetailPage && vm.HasDetail && !vm.IsDetailLoading && !vm.ShowResults, "resource: separate project version page " + source);
            Check(transport.Grants == grants, "resource: opening project does not resolve every file grant");
            var versions = Get<ListBox>(view, "VersionList");
            Check(versions.Items.Count == 8 && versions.SelectedItem != null, "resource: grouped versions render with a selection");
            Check(!vm.IsVersionDialogOpen, "resource: version list stays full height before selecting an install");
            var website = Get<Button>(view, "ProjectWebsiteButton");
            foreach (double scale in new[] { 1d, 1.25d, 1.5d, 2d })
            {
                website.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
                window.UpdateLayout();
                var bounds = website.TransformToAncestor(window).TransformBounds(new Rect(website.RenderSize));
                Check(bounds.Right <= window.ActualWidth && bounds.Left > 223 && bounds.Height >= 20*scale, "resource: website selector fits resized header scale " + scale);
            }
            website.LayoutTransform = System.Windows.Media.Transform.Identity; window.UpdateLayout();
            var menu = CommunityResourceView.CreateProjectWebsiteMenu(vm.Detail!);
            Check(menu.Items.Count == 2, "resource: both platform destinations available");
            foreach (MenuItem item in menu.Items) Check(CommunityHosts.IsAllowedProjectLink(item.Tag as string), "resource: website destination remains allowlisted");
            Check(vm.Detail!.GetPlatformUrl(ResourceSourceId.CurseForge).StartsWith("https://www.curseforge.com/minecraft/"), "resource: CurseForge destination independent of selected download source");
            menu.PlacementTarget=website; menu.IsOpen=true; await Task.Delay(80); menu.UpdateLayout();
            Check(menu.ActualWidth>100 && menu.ActualHeight>30, "resource: website choices visibly render");
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth),(int)Math.Ceiling(menu.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(menu);var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using(var image=File.Create(Path.Combine(_output,source+"-website-choices.png")))png.Save(image);
            menu.IsOpen=false;
            website.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(30);
            var menuField=typeof(CommunityResourceView).GetField("_websiteMenu",BindingFlags.Instance|BindingFlags.NonPublic)!;
            var active=(ContextMenu)menuField.GetValue(view)!;
            Check(active.IsOpen,"resource: website button opens dropdown");
            website.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(!active.IsOpen,"resource: repeated website click closes dropdown");
            Check(active.Template!=null && active.Items.OfType<MenuItem>().All(item=>item.Template.Triggers.OfType<Trigger>().Any(t=>t.Property==MenuItem.IsHighlightedProperty && t.Setters.OfType<Setter>().Any(v=>v.TargetName=="ItemSurface"))),"resource: dropdown owns full themed surface and highlighted item background");
            vm.OpenVersionCommand.Execute(vm.Detail!.SelectedVersion); await Task.Delay(80);
            Check(Get<Grid>(view, "VersionDialog").IsVisible, "resource: selecting version opens installation dialog");
            Check(vm.Detail!.CanInstall, "resource: selected compatible version can install");
            Screenshot(window, source + "-detail-980.png");
            vm.Detail.InstallCommand.Execute(null); await Until(() => !vm.Detail.IsInstalling);
            Check(vm.Detail.Status == "安装完成", "resource: formal install command completes " + source);
            Check(transport.Grants == grants + (source == ResourceSourceId.CurseForge ? 1 : 0), "resource: only selected CurseForge version receives official grant");
            Check(transport.CredentialIsolation, "resource: file credentials stay isolated by source");
            vm.BackToResultsCommand.Execute(null);
            Check(vm.ShowResults && vm.Results.Count == 50, "resource: back restores results without another search");
        }
        window.Width = 1200; window.Height = 800;
        Click(window, "ThemeToggleButton"); await Task.Delay(100);
        var textColor = ((System.Windows.Media.SolidColorBrush)view.FindResource("TextBrush")).Color;
        Check(Descendants(view).OfType<TextBlock>().Where(t => t.Text == "Sodium").All(t => ((System.Windows.Media.SolidColorBrush)t.Foreground).Color == textColor), "resource: dark result titles use readable theme foreground");
        Screenshot(window, "resource-dark-results.png");
        await vm.OpenDetailAsync(vm.Results[1]); await Task.Delay(100); Screenshot(window, "resource-dark-detail.png");
        vm.OpenVersionCommand.Execute(vm.Detail!.SelectedVersion); await Task.Delay(80); Screenshot(window, "resource-dark-version-dialog.png");
        Check(Descendants(Get<ListBox>(view, "VersionList")).OfType<TextBlock>().Where(t => t.Text.StartsWith("Sodium 0.6")).All(t => ((System.Windows.Media.SolidColorBrush)t.Foreground).Color == textColor), "resource: dark version rows use readable theme foreground");
        Click(window, "ThemeToggleButton");
        File.WriteAllText(Path.Combine(_output, "result.txt"), $"PASS {_checks} navigation/resource checks");
    }

    private sealed class ResourceUiTransport : HttpMessageHandler
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes("verified community UI download fixture");
        private string Hash => BitConverter.ToString(SHA1.Create().ComputeHash(_bytes)).Replace("-", "").ToLowerInvariant();
        public int Grants;
        public bool CredentialIsolation = true;
        private object CfFile(int n) => new { id = 100 + n, modId = 1, isAvailable = true, fileName = "cf-ui-" + n + ".jar", displayName = "Sodium 0.6." + n + " for Fabric", fileDate = "2026-11-01T00:00:00Z", fileLength = _bytes.Length, gameVersions = new[] { "1.21.1" }, sortableGameVersions = new[] { new { gameVersionName = "Fabric" } }, hashes = new[] { new { algo = 1, value = Hash } } };
        private object CfProject(int n) => new { id = n + 1, name = n == 0 ? "Sodium" : "Sodium 扩展资源 " + n, summary = "性能优化模组，展示较长描述在紧凑结果行中的换行和省略效果。", downloadCount = 12345678, allowModDistribution = true, dateModified = "2026-11-01T00:00:00Z", authors = new[] { new { name = "Community Author" } }, latestFiles = new[] { CfFile(0) } };
        private static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!; string path = uri.AbsolutePath;
            int pageSize = uri.Query.Contains("pageSize=25") || uri.Query.Contains("limit=25") ? 25 : 50;
            if (uri.Host.EndsWith("forgecdn.net") || uri.Host == "cdn.modrinth.com")
            {
                bool key = request.Headers.Contains("x-api-key");
                CredentialIsolation &= key == uri.Host.EndsWith("forgecdn.net") && !request.Headers.Contains("Authorization") && !request.Headers.Contains("Cookie");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_bytes) });
            }
            if (uri.Host == "api.curseforge.com")
            {
                if (path.EndsWith("download-url")) { Grants++; return Task.FromResult(Json(new { data = "https://edge.forgecdn.net/files/1/2/cf-ui-0.jar" })); }
                if (path.EndsWith("/search")) return Task.FromResult(Json(new { data = Enumerable.Range(0, uri.Query.Contains("index=50") ? 3 : pageSize).Select(CfProject).ToArray() }));
                if (path.EndsWith("/files")) return Task.FromResult(Json(new { data = Enumerable.Range(0, 8).Select(CfFile).ToArray() }));
                if (path.Contains("/files/")) return Task.FromResult(Json(new { data = CfFile(int.Parse(path.Split('/').Last()) - 100) }));
                return Task.FromResult(Json(new { data = CfProject(0) }));
            }
            if (path.EndsWith("/search")) return Task.FromResult(Json(new { hits = Enumerable.Range(0, uri.Query.Contains("offset=50") ? 3 : pageSize).Select(n => new { project_id = "mr-ui-" + n, slug = "mr-ui-" + n, title = n == 0 ? "Sodium" : "Sodium 扩展资源 " + n, description = "性能优化模组，展示较长描述在紧凑结果行中的换行和省略效果。", author = "Community Author", downloads = 12345678, date_modified = "2026-11-01T00:00:00Z", versions = new[] { "1.21.1" }, categories = new[] { "fabric" } }).ToArray() }));
            return Task.FromResult(Json(Enumerable.Range(0, 8).Select(n => new { id = "mr-" + n, version_number = "Sodium 0.6." + n + " for Fabric", date_published = "2026-11-01T00:00:00Z", game_versions = new[] { "1.21.1" }, loaders = new[] { "fabric" }, files = new[] { new { filename = "mr-ui-" + n + ".jar", size = _bytes.Length, primary = true, url = "https://cdn.modrinth.com/data/mr-ui/versions/" + n + "/mr-ui-" + n + ".jar", hashes = new { sha1 = Hash } } } }).ToArray()));
        }
    }
}
