using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CMUmcl.Content
{
    /// <summary>Progress reported while migrating instances between storage directories.</summary>
    public sealed class MigrationProgress
    {
        public string Stage { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public int CurrentInstance { get; set; }
        public int TotalInstances { get; set; }
        public long BytesCopied { get; set; }
        public long TotalBytes { get; set; }
    }

    /// <summary>
    /// Result of a successful <see cref="InstanceMigrationService.MigrateAsync"/> call. The caller
    /// must commit (persist the updated records + settings) and then delete <see cref="SourceDirectories"/>;
    /// on a commit failure the caller should delete <see cref="TargetDirectories"/> to roll back.
    /// </summary>
    public sealed class MigrationResult
    {
        public IReadOnlyDictionary<string, InstanceRecord> UpdatedRecords { get; init; } = new Dictionary<string, InstanceRecord>();

        /// <summary>Old instance directories to delete after a successful commit.</summary>
        public IReadOnlyList<string> SourceDirectories { get; init; } = Array.Empty<string>();

        /// <summary>New instance directories created by this migration (for rollback on commit failure).</summary>
        public IReadOnlyList<string> TargetDirectories { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// One naming conflict found while planning a migration: a source directory whose safe base
    /// folder name already exists in (or is already planned for) the target, together with the
    /// collision-free names the service proposes by default.
    /// </summary>
    public sealed class MigrationConflict
    {
        public string InstanceId { get; init; } = string.Empty;
        public string SourceDisplayName { get; init; } = string.Empty;
        public string SourceRelativePath { get; init; } = string.Empty;
        public string ProposedTargetName { get; init; } = string.Empty;
        public string ProposedDisplayName { get; init; } = string.Empty;
    }

    /// <summary>User-chosen replacement names for a conflicted instance.</summary>
    public sealed class MigrationNameOverride
    {
        public string TargetName { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
    }

    /// <summary>
    /// Migrates installed instances from one storage directory to another with collision-safe naming.
    /// The source directories are never modified here: they are only copied + verified. The caller
    /// decides when (and whether) to commit and delete the originals. On any copy/verification failure
    /// or cancellation, all target copies created so far are removed and the originals are left intact.
    /// </summary>
    public static class InstanceMigrationService
    {
        private const int CopyBufferSize = 81920;

        public static async Task<MigrationResult> MigrateAsync(
            string sourceRoot,
            string targetRoot,
            IReadOnlyDictionary<string, InstanceRecord> records,
            IProgress<MigrationProgress>? progress = null,
            CancellationToken ct = default,
            IReadOnlyDictionary<string, MigrationNameOverride>? overrides = null)
        {
            if (sourceRoot == null) throw new ArgumentNullException(nameof(sourceRoot));
            if (targetRoot == null) throw new ArgumentNullException(nameof(targetRoot));
            if (records == null) throw new ArgumentNullException(nameof(records));

            string sourceFull = NormalizeRoot(sourceRoot, nameof(sourceRoot));
            string targetFull = NormalizeRoot(targetRoot, nameof(targetRoot));

            ValidateTargets(sourceFull, targetFull);

            var updatedRecords = new Dictionary<string, InstanceRecord>(StringComparer.Ordinal);
            var sourceDirectories = new List<string>();
            var targetDirectories = new List<string>();
            var createdTargets = new List<string>();
            var plannedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usedDisplayNames = new HashSet<string>(StringComparer.Ordinal);

            // Validate the overrides up front (before any copy) so an invalid name never leaves
            // half-migrated instances behind. Their names are reserved so non-overridden records
            // keep the existing auto-suffix behavior and never collide with them.
            if (overrides != null)
                ValidateOverrides(targetFull, overrides, plannedNames, usedDisplayNames);

            try
            {
                progress?.Report(new MigrationProgress
                {
                    Stage = "迁移准备",
                    Message = "正在迁移实例…",
                    TotalInstances = records.Count,
                });

                long totalSourceBytes = SumSourceBytes(sourceFull, records);
                EnsureFreeSpace(targetFull, totalSourceBytes);

                int index = 0;
                long copiedBytes = 0;
                foreach (var kv in records)
                {
                    ct.ThrowIfCancellationRequested();

                    string id = kv.Key;
                    InstanceRecord record = kv.Value;

                    string relative = string.IsNullOrWhiteSpace(record.InstallPath) ? id : record.InstallPath!;
                    string sourceDir = Path.Combine(sourceFull, relative);

                    if (!Directory.Exists(sourceDir))
                    {
                        // Nothing to move for this instance; carry its record through unchanged so
                        // the commit does not drop it.
                        updatedRecords[id] = new InstanceRecord
                        {
                            Revision = record.Revision,
                            InstallPath = record.InstallPath,
                            DisplayName = record.DisplayName,
                        };
                        index++;
                        continue;
                    }

                    string targetName;
                    string? displayName;
                    if (overrides != null && overrides.TryGetValue(id, out var nameOverride))
                    {
                        // Already validated in ValidateOverrides; use the user's names verbatim.
                        targetName = nameOverride.TargetName.Trim();
                        displayName = string.IsNullOrWhiteSpace(nameOverride.DisplayName)
                            ? null
                            : nameOverride.DisplayName.Trim();
                    }
                    else
                    {
                        targetName = ResolveTargetName(targetFull, relative, plannedNames);
                        displayName = ResolveDisplayName(record.DisplayName, usedDisplayNames);
                    }

                    progress?.Report(new MigrationProgress
                    {
                        Stage = "复制实例",
                        Message = $"正在复制 {displayName ?? id}…",
                        CurrentInstance = index,
                        TotalInstances = records.Count,
                        BytesCopied = copiedBytes,
                        TotalBytes = totalSourceBytes,
                    });

                    string targetDir = Path.Combine(targetFull, targetName);
                    createdTargets.Add(targetDir);

                    await Task.Run(
                        () => CopyDirectory(sourceDir, targetDir, progress, copiedBytes, totalSourceBytes, index, records.Count, ct),
                        ct).ConfigureAwait(false);

                    ct.ThrowIfCancellationRequested();

                    (long files, long bytes) = GetDirectoryStats(sourceDir);
                    (long targetFiles, long targetBytes) = GetDirectoryStats(targetDir);
                    if (files != targetFiles || bytes != targetBytes)
                        throw new IOException($"实例 {id} 迁移校验失败（文件数量或大小不一致）。");

                    copiedBytes += bytes;

                    plannedNames.Add(targetName);
                    usedDisplayNames.Add(displayName ?? string.Empty);

                    updatedRecords[id] = new InstanceRecord
                    {
                        Revision = record.Revision,
                        InstallPath = targetName,
                        DisplayName = displayName,
                    };
                    sourceDirectories.Add(sourceDir);
                    targetDirectories.Add(targetDir);

                    index++;
                }

                progress?.Report(new MigrationProgress
                {
                    Stage = "完成",
                    Message = "实例迁移完成。",
                    CurrentInstance = records.Count,
                    TotalInstances = records.Count,
                    BytesCopied = copiedBytes,
                    TotalBytes = totalSourceBytes,
                });

                return new MigrationResult
                {
                    UpdatedRecords = updatedRecords,
                    SourceDirectories = sourceDirectories,
                    TargetDirectories = targetDirectories,
                };
            }
            catch
            {
                // Roll back everything created so far (including the in-progress target);
                // originals are never touched.
                foreach (string target in createdTargets)
                    TryDeleteDirectory(target);
                throw;
            }
        }

        // ------------------------------------------------------------------
        // Validation
        // ------------------------------------------------------------------

        private static string NormalizeRoot(string root, string paramName)
        {
            if (string.IsNullOrWhiteSpace(root))
                throw new ArgumentException("目录不能为空。", paramName);

            string full;
            try
            {
                full = Path.GetFullPath(root.Trim());
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"目录无效：{root}", paramName, ex);
            }

            if (!Path.IsPathRooted(full))
                throw new ArgumentException($"目录必须是绝对路径：{root}", paramName);

            return full;
        }

        private static void ValidateTargets(string sourceFull, string targetFull)
        {
            ValidateTargetPaths(sourceFull, targetFull);

            // Verify the target location is writable (create + probe file).
            try
            {
                Directory.CreateDirectory(targetFull);
                string probe = Path.Combine(targetFull, ".cmumcl-write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                throw new IOException($"目标目录不可写：{targetFull}", ex);
            }
        }

        /// <summary>Path-only checks that never touch the filesystem (safe to run while merely detecting conflicts).</summary>
        private static void ValidateTargetPaths(string sourceFull, string targetFull)
        {
            if (string.Equals(sourceFull, targetFull, StringComparison.OrdinalIgnoreCase))
                throw new IOException("目标目录与源目录相同，无需迁移。");

            if (LauncherPaths.IsSameOrUnder(targetFull, sourceFull))
                throw new IOException("目标目录不能位于源目录之内。");

            if (LauncherPaths.IsSameOrUnder(sourceFull, targetFull))
                throw new IOException("目标目录不能包含源目录。");

            // Reject the root of a drive as a target.
            string? targetRoot = Path.GetPathRoot(targetFull);
            if (!string.IsNullOrEmpty(targetRoot)
                && string.Equals(targetFull, targetRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("目标目录不能是磁盘根目录。");
            }
        }

        /// <summary>
        /// Detects naming conflicts that would occur if <paramref name="records"/> were migrated
        /// from <paramref name="sourceRoot"/> to <paramref name="targetRoot"/>. Never modifies any
        /// file or directory. Returns only the records that collide (the safe base target folder
        /// already exists in the target or is already planned by another record), each with the
        /// collision-free target/display names the service would use by default.
        /// </summary>
        public static IReadOnlyList<MigrationConflict> FindConflicts(
            string sourceRoot,
            string targetRoot,
            IReadOnlyDictionary<string, InstanceRecord> records)
        {
            if (sourceRoot == null) throw new ArgumentNullException(nameof(sourceRoot));
            if (targetRoot == null) throw new ArgumentNullException(nameof(targetRoot));
            if (records == null) throw new ArgumentNullException(nameof(records));

            string sourceFull = NormalizeRoot(sourceRoot, nameof(sourceRoot));
            string targetFull = NormalizeRoot(targetRoot, nameof(targetRoot));
            ValidateTargetPaths(sourceFull, targetFull);

            var conflicts = new List<MigrationConflict>();
            var plannedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usedDisplayNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var kv in records)
            {
                string id = kv.Key;
                InstanceRecord record = kv.Value;

                string relative = string.IsNullOrWhiteSpace(record.InstallPath) ? id : record.InstallPath!;
                string sourceDir = Path.Combine(sourceFull, relative);
                if (!Directory.Exists(sourceDir))
                    continue; // Nothing to migrate — the record carries through unchanged.

                string baseName = SafeName(relative);
                bool targetConflict = Directory.Exists(Path.Combine(targetFull, baseName)) || plannedNames.Contains(baseName);

                string targetName = ResolveTargetName(targetFull, relative, plannedNames);
                string? displayName = ResolveDisplayName(record.DisplayName, usedDisplayNames);

                if (targetConflict)
                {
                    conflicts.Add(new MigrationConflict
                    {
                        InstanceId = id,
                        SourceDisplayName = record.DisplayName ?? id,
                        SourceRelativePath = relative,
                        ProposedTargetName = targetName,
                        ProposedDisplayName = displayName ?? string.Empty,
                    });
                }

                plannedNames.Add(targetName);
                usedDisplayNames.Add(displayName ?? string.Empty);
            }

            return conflicts;
        }

        /// <summary>
        /// Validates a user-supplied folder name for use as an instance target directory.
        /// Returns an error message when invalid, or null when the name is safe to use.
        /// </summary>
        public static string? ValidateFolderName(string? name)
        {
            string s = (name ?? string.Empty).Trim();
            if (s.Length == 0)
                return "目录名不能为空。";

            if (s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return "目录名包含非法字符。";

            if (Path.IsPathRooted(s)
                || s.IndexOf(Path.DirectorySeparatorChar) >= 0
                || s.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
            {
                return "目录名必须是单个文件夹名称（不能包含路径分隔符）。";
            }

            if (s is "." or "..")
                return "目录名无效。";

            return null;
        }

        private static void ValidateOverrides(
            string targetFull,
            IReadOnlyDictionary<string, MigrationNameOverride> overrides,
            HashSet<string> plannedNames,
            HashSet<string> usedDisplayNames)
        {
            foreach (var kv in overrides)
            {
                string id = kv.Key;
                MigrationNameOverride o = kv.Value
                    ?? throw new IOException($"实例 {id} 的自定义名称无效。");

                string targetName = o.TargetName?.Trim() ?? string.Empty;

                string? error = ValidateFolderName(targetName);
                if (error != null)
                    throw new IOException($"实例 {id} 的自定义目录名无效：{error}");

                if (Directory.Exists(Path.Combine(targetFull, targetName)))
                    throw new IOException($"目标目录已存在同名文件夹：{targetName}。");

                if (!plannedNames.Add(targetName))
                    throw new IOException($"自定义目录名重复：{targetName}。");

                string? displayName = string.IsNullOrWhiteSpace(o.DisplayName) ? null : o.DisplayName.Trim();
                if (displayName != null)
                    usedDisplayNames.Add(displayName);
            }
        }

        private static long SumSourceBytes(
            string sourceFull,
            IReadOnlyDictionary<string, InstanceRecord> records)
        {
            long total = 0;
            foreach (var kv in records)
            {
                string relative = string.IsNullOrWhiteSpace(kv.Value.InstallPath) ? kv.Key : kv.Value.InstallPath!;
                string sourceDir = Path.Combine(sourceFull, relative);
                if (!Directory.Exists(sourceDir))
                    continue;

                total += GetDirectoryStats(sourceDir).Bytes;
            }

            return total;
        }

        private static void EnsureFreeSpace(string targetFull, long totalBytes)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(targetFull) ?? targetFull);
                if (drive.IsReady && drive.AvailableFreeSpace < totalBytes)
                    throw new IOException("目标磁盘空间不足，无法迁移实例。");
            }
            catch (IOException)
            {
                throw;
            }
            catch
            {
                // Drive info unavailable — proceed and let the copy itself surface any problem.
            }
        }

        // ------------------------------------------------------------------
        // Naming (collision-safe)
        // ------------------------------------------------------------------

        private static string ResolveTargetName(string targetFull, string relative, HashSet<string> plannedNames)
        {
            string baseName = SafeName(relative);
            string candidate = baseName;

            int n = 2;
            while (Directory.Exists(Path.Combine(targetFull, candidate)) || plannedNames.Contains(candidate))
            {
                candidate = baseName + "-" + n;
                n++;
            }

            return candidate;
        }

        private static string? ResolveDisplayName(string? recordedDisplayName, HashSet<string> usedDisplayNames)
        {
            string? baseName = string.IsNullOrWhiteSpace(recordedDisplayName) ? null : recordedDisplayName.Trim();
            if (baseName == null)
                return null;

            string candidate = baseName;
            int n = 2;
            while (usedDisplayNames.Contains(candidate))
            {
                candidate = baseName + " (" + n + ")";
                n++;
            }

            return candidate;
        }

        private static string SafeName(string relative)
        {
            string name = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

            if (string.IsNullOrWhiteSpace(name))
                return "instance";

            // A relative folder name only — no separators, no rooted path, no traversal.
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                if (name.IndexOf(c) >= 0)
                    name = name.Replace(c, '-');
            }

            if (Path.IsPathRooted(name) || name.IndexOf(Path.DirectorySeparatorChar) >= 0 || name.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
            {
                // Fall back to the last path segment, or a placeholder.
                string segment = name.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                int sep = segment.LastIndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
                segment = sep >= 0 ? segment.Substring(sep + 1) : segment;
                name = string.IsNullOrWhiteSpace(segment) ? "instance" : segment;
            }

            if (name is "." or "..")
                return "instance";

            return name;
        }

        // ------------------------------------------------------------------
        // Copy + verification
        // ------------------------------------------------------------------

        private static void CopyDirectory(
            string sourceDir,
            string targetDir,
            IProgress<MigrationProgress>? progress,
            long alreadyCopied,
            long totalBytes,
            int currentInstance,
            int totalInstances,
            CancellationToken ct)
        {
            Directory.CreateDirectory(targetDir);

            long localCopied = 0;
            foreach (string sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                string relative = Path.GetRelativePath(sourceDir, sourceFile);
                string targetFile = Path.Combine(targetDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);

                CopyFile(sourceFile, targetFile, ref localCopied, progress, alreadyCopied, totalBytes, currentInstance, totalInstances, ct);
            }
        }

        private static void CopyFile(
            string sourceFile,
            string targetFile,
            ref long localCopied,
            IProgress<MigrationProgress>? progress,
            long alreadyCopied,
            long totalBytes,
            int currentInstance,
            int totalInstances,
            CancellationToken ct)
        {
            using var source = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize);
            using var target = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize);

            var buffer = new byte[CopyBufferSize];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                target.Write(buffer, 0, read);
                localCopied += read;

                progress?.Report(new MigrationProgress
                {
                    Stage = "复制实例",
                    Message = "正在复制实例…",
                    CurrentInstance = currentInstance,
                    TotalInstances = totalInstances,
                    BytesCopied = alreadyCopied + localCopied,
                    TotalBytes = totalBytes,
                });
            }
        }

        private static (long Files, long Bytes) GetDirectoryStats(string dir)
        {
            long files = 0;
            long bytes = 0;
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                files++;
                bytes += new FileInfo(file).Length;
            }

            return (files, bytes);
        }

        private static void TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best effort — partial copies may remain but never affect the originals.
            }
        }
    }
}
