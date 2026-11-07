// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CMUmcl.Community
{
    /// <summary>Result of one merged catalog page plus per-source failure state.</summary>
    internal sealed class CatalogPage
    {
        public List<ResourceProject> Projects = new();
        public bool HasMore;
        public List<CommunitySourceException> Failures = new();
    }

    /// <summary>
    /// Orchestrates the two sources: concurrent fan-out, Source=All page-wise merge (same index,
    /// 25 per source; 50 single-source), (Source, Id) dedupe, page-local sort, partial-failure isolation.
    /// </summary>
    internal sealed class ResourceCatalog
    {
        private readonly IResourceSource[] _sources;

        public ResourceCatalog(params IResourceSource[] sources) { _sources = sources; }

        /// <param name="sourceCompleted">
        /// Called (on a worker thread) with the merged, ordered projects received so far each time a
        /// source finishes, so the UI can show the faster source without waiting for the slower one.
        /// </param>
        public async Task<CatalogPage> SearchAsync(ResourceQuery query, string? targetLoader, string? targetGame, CancellationToken ct,
            Action<IReadOnlyList<ResourceProject>>? sourceCompleted = null)
        {
            var available = _sources.Where(s => s.IsAvailable).ToList();
            var page = new CatalogPage();
            if (available.Count == 0)
            {
                page.Failures.Add(new CommunitySourceException(ResourceSourceId.Modrinth, "没有可用的资源来源。"));
                return page;
            }

            bool allSources = query.Source == null;
            var sourceSet = allSources ? available : available.Where(s => s.SourceId == query.Source).ToList();

            // A selected-but-unavailable source (e.g. CurseForge with no key) must surface as a failure,
            // not a silent empty page.
            if (query.Source != null && sourceSet.Count == 0)
            {
                page.Failures.Add(new CommunitySourceException(query.Source.Value, "所选资源来源当前不可用。"));
                return page;
            }

            int perSourcePageSize = allSources ? 25 : 50;
            var tasks = new List<Task<IReadOnlyList<ResourceProject>>>();
            var received = new IReadOnlyList<ResourceProject>?[sourceSet.Count];
            int slot = 0;
            foreach (IResourceSource src in sourceSet)
            {
                int index = slot++;
                var q = new ResourceQuery
                {
                    SearchText = query.SearchText,
                    Kind = query.Kind,
                    GameVersion = query.GameVersion,
                    Loader = query.Loader,
                    Source = src.SourceId,
                    Sort = query.Sort,
                    Index = query.Index,
                    PageSize = perSourcePageSize,
                };
                tasks.Add(ReportAsync(RunSourceAsync(src, q, page.Failures, ct), index));
            }

            async Task<IReadOnlyList<ResourceProject>> ReportAsync(Task<IReadOnlyList<ResourceProject>> run, int index)
            {
                var list = await run.ConfigureAwait(false);
                if (sourceCompleted != null && list.Count > 0)
                {
                    IReadOnlyList<ResourceProject> sofar;
                    lock (received) { received[index] = list; sofar = Merge(received.Where(r => r != null).SelectMany(r => r!), query, targetLoader, targetGame); }
                    sourceCompleted(sofar);
                }
                return list;
            }

            IReadOnlyList<ResourceProject>[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
            page.Projects = Merge(results.SelectMany(r => r), query, targetLoader, targetGame).ToList();
            page.HasMore = results.Any(r => r.Count >= perSourcePageSize); // any full page implies a possible next page
            return page;
        }

        // Source order is fixed by the source list, so a partial merge already has its final relative order.
        private static IReadOnlyList<ResourceProject> Merge(IEnumerable<ResourceProject> projects, ResourceQuery query, string? targetLoader, string? targetGame)
        {
            // (Source, Id) dedupe (never merge by title).
            var seen = new HashSet<(ResourceSourceId, string)>();
            var deduped = new List<ResourceProject>();
            foreach (ResourceProject p in projects)
                if (seen.Add((p.Source, p.Id)))
                    deduped.Add(p);

            // Project-level compatibility hint (search summary only) for each merged project.
            foreach (ResourceProject p in deduped)
                p.Hint = CompatibilityEvaluator.ProjectHint(p, query.Kind, targetLoader, targetGame);

            // Page-local sort (no cross-source global ranking guarantee).
            return query.Sort switch
            {
                ResourceSort.Downloads => deduped.OrderByDescending(p => p.Downloads).ToList(),
                ResourceSort.LastUpdated => deduped.OrderByDescending(p => p.UpdatedAt ?? DateTimeOffset.MinValue).ToList(),
                ResourceSort.Name => deduped.OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToList(),
                _ => deduped, // Recommended: keep native relevance order (Modrinth page then CurseForge page).
            };
        }

        private static async Task<IReadOnlyList<ResourceProject>> RunSourceAsync(
            IResourceSource src, ResourceQuery q, List<CommunitySourceException> failures, CancellationToken ct)
        {
            try { return await src.SearchAsync(q, ct).ConfigureAwait(false); }
            catch (CommunitySourceException ex) { lock (failures) failures.Add(ex); return Array.Empty<ResourceProject>(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lock (failures) failures.Add(new CommunitySourceException(src.SourceId, ex.Message, null, ex)); return Array.Empty<ResourceProject>(); }
        }

        /// <summary>Returns the available source with the given id, or null when absent/unavailable.</summary>
        public IResourceSource? GetSource(ResourceSourceId id)
            => _sources.FirstOrDefault(s => s.SourceId == id && s.IsAvailable);

        /// <summary>
        /// Dispatches version enumeration to the matching source. Throws <see cref="CommunitySourceException"/>
        /// when the source is not present or unavailable (so the UI can surface the reason).
        /// </summary>
        public async Task<IReadOnlyList<ResourceVersion>> GetVersionsAsync(
            ResourceSourceId sourceId, string projectId, ResourceQuery query, CancellationToken ct)
        {
            IResourceSource? source = GetSource(sourceId);
            if (source == null)
                throw new CommunitySourceException(sourceId, "所选资源来源当前不可用。");
            return await source.GetVersionsAsync(projectId, query, ct).ConfigureAwait(false);
        }

        internal async Task<ResolvedDependency> ResolveDependencyAsync(ResourceSourceId sourceId, ResourceDependency dependency, CancellationToken ct)
        {
            IResourceSource? source = GetSource(sourceId);
            if (source == null) throw new CommunitySourceException(sourceId, "前置模组来源不可用。");
            string? projectId = dependency.ProjectId;
            ResourceVersion? pinned = null;
            if (!string.IsNullOrWhiteSpace(dependency.VersionId) && source is ModrinthSource modrinth)
            {
                pinned = await modrinth.GetVersionAsync(dependency.VersionId!, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(projectId) && projectId != pinned.ProjectId)
                    throw new CommunitySourceException(sourceId, "前置项目与指定版本不匹配。");
                projectId = pinned.ProjectId;
            }
            if (string.IsNullOrWhiteSpace(projectId)) throw new CommunitySourceException(sourceId, "来源没有提供可查询的前置项目编号。");
            ResourceProject project = await source.GetProjectAsync(projectId!, ct).ConfigureAwait(false);
            return new ResolvedDependency(project, pinned);
        }

        /// <summary>
        /// Resolves the download URL for a version file.
        /// Modrinth files already carry their URL; CurseForge files must be resolved through
        /// /files/{fileId}/download-url (version.Id is the numeric file id). Unknown source → null.
        /// </summary>
        public async Task<ResourceFile?> ResolveDownloadAsync(
            ResourceProject project, ResourceVersion version, ResourceFile file, CancellationToken ct)
        {
            switch (project.Source)
            {
                case ResourceSourceId.Modrinth:
                    return file; // URL already present
                case ResourceSourceId.CurseForge:
                {
                    IResourceSource? source = GetSource(ResourceSourceId.CurseForge);
                    if (source is CurseForgeSource curseForge && long.TryParse(version.Id, out long fileId))
                        return await curseForge.ResolveDownloadAsync(project.Id, fileId, file, ct).ConfigureAwait(false);
                    return null;
                }
                default:
                    return null;
            }
        }
    }
}
