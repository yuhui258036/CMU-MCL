using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using CMUmcl.Content;

namespace CMUmcl.Launch
{
    /// <summary>Located Java runtime (javaw.exe path + version + major version).</summary>
    public sealed class JavaInfo
    {
        public string JavaPath { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public int Major { get; init; }
        public string Architecture { get; init; } = "unknown";
        public string Vendor { get; init; } = string.Empty;
        public int Bits => Architecture is "x86" or "i386" ? 32 : Architecture is "x64" or "aarch64" or "arm64" or "amd64" ? 64 : 0;
        public string DisplayName => $"Java {Version} · {Architecture}" + (Vendor.Length > 0 ? $" · {Vendor}" : "");
        public override string ToString() => DisplayName;
        public bool IsSupportedArchitecture => Architecture switch
        {
            "unknown" or "x86" or "i386" => true,
            "x64" or "amd64" => RuntimeInformation.OSArchitecture is System.Runtime.InteropServices.Architecture.X64 or System.Runtime.InteropServices.Architecture.Arm64,
            "arm64" or "aarch64" => RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64,
            _ => false,
        };
    }

    /// <summary>
    /// Scans the system for local JDKs and selects the best one for a required major
    /// version. Never falls back to an incompatible (lower) version.
    /// </summary>
    public static class JavaLocator
    {
        private static readonly string[] VendorDirectories = new[]
        {
            "Eclipse Adoptium", "Microsoft", "Zulu", "Amazon Corretto", "BellSoft", "GraalVM",
        };

        private static readonly IComparer<string> VersionComparer = new VersionComparerImpl();

        /// <summary>
        /// Scans all known JDK sources and returns a deduplicated list of JavaInfo.
        /// Only entries whose major version could be determined are returned (entries
        /// with an undeterminable major cannot be proven compatible and are excluded).
        /// </summary>
        public static IReadOnlyList<JavaInfo> Scan(System.Threading.CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var roots = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. JAVA_HOME, and 2. its sibling JDKs.
            string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrWhiteSpace(javaHome))
            {
                AddRoot(roots, seen, javaHome);
                string? parent = Path.GetDirectoryName(
                    javaHome.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    foreach (string dir in SafeEnumerateDirectories(parent))
                        AddRoot(roots, seen, dir);
                }
            }

            // 3. %ProgramFiles%\Java / %ProgramFiles(x86)%\Java, and 4. vendor dirs.
            string? pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string? pfx = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            foreach (string? baseDir in new[] { pf, pfx })
            {
                if (string.IsNullOrWhiteSpace(baseDir))
                    continue;

                foreach (string dir in SafeEnumerateDirectories(Path.Combine(baseDir, "Java")))
                    AddRoot(roots, seen, dir);

                foreach (string vendor in VendorDirectories)
                {
                    foreach (string dir in SafeEnumerateDirectories(Path.Combine(baseDir, vendor)))
                        AddRoot(roots, seen, dir);
                }
            }

            // 5. %USERPROFILE%\.jdks
            string? userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                foreach (string dir in SafeEnumerateDirectories(Path.Combine(userProfile, ".jdks")))
                    AddRoot(roots, seen, dir);
            }

            // 6. PATH
            string? path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(path))
            {
                foreach (string dir in path.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir))
                        continue;

                    bool hasJava = File.Exists(Path.Combine(dir, "java.exe"));
                    bool hasJavaw = File.Exists(Path.Combine(dir, "javaw.exe"));
                    if (!hasJava && !hasJavaw)
                        continue;

                    string? root = Path.GetDirectoryName(dir);
                    if (!string.IsNullOrWhiteSpace(root))
                        AddRoot(roots, seen, root);
                }
            }

            // 7. Registry (best effort; failure is non-fatal).
            try
            {
                AddRegistryRoots(roots, seen);
            }
            catch
            {
                // Best effort only.
            }

            // Include runtimes already downloaded by this launcher and the official launcher.
            foreach (string parent in new[] {
                Path.Combine(LauncherPaths.RootDirectory, "java", "runtime"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime") })
            {
                foreach (string component in SafeEnumerateDirectories(parent))
                {
                    AddRoot(roots, seen, component);
                    foreach (string child in SafeEnumerateDirectories(component))
                    {
                        AddRoot(roots, seen, child);
                        foreach (string nested in SafeEnumerateDirectories(child)) AddRoot(roots, seen, nested);
                    }
                }
            }
            var result = new System.Collections.Concurrent.ConcurrentBag<JavaInfo>();
            System.Threading.Tasks.Parallel.ForEach(roots, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, root =>
            {
                JavaInfo? info = Detect(root);
                if (info != null && info.Major > 0)
                    result.Add(info);
            });
            return result.OrderByDescending(j => j.Major).ThenBy(j => j.JavaPath, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        /// <summary>
        /// Selects the best candidate for the required major version: exact match first,
        /// never silently substitutes a newer major. Prefers 64-bit, javaw.exe, then the highest
        /// full version. Returns null when nothing matches (never a lower version).
        /// </summary>
        public static JavaInfo? Select(int requiredMajor, IReadOnlyList<JavaInfo> candidates)
        {
            if (requiredMajor <= 0 || candidates == null)
                return null;

            var eligible = candidates.Where(c => c.Major > 0 && c.IsSupportedArchitecture && (requiredMajor < 21 || c.Bits != 32)).ToList();
            if (eligible.Count == 0)
                return null;

            var exact = eligible.Where(c => c.Major == requiredMajor).ToList();
            if (exact.Count > 0)
                return BestOf(exact);

            // A newer Java is not necessarily compatible with old loaders/mods.
            return null;
        }

        /// <summary>
        /// Detects a JDK at the given root directory (parent of bin). Returns null when
        /// no java/javaw executable exists; returns a JavaInfo with Major=0 when the
        /// version cannot be determined.
        /// </summary>
        public static JavaInfo? Detect(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
                return null;

            string javaw = Path.Combine(root, "bin", "javaw.exe");
            string java = Path.Combine(root, "bin", "java.exe");
            bool hasJavaw = File.Exists(javaw);
            bool hasJava = File.Exists(java);
            if (!hasJavaw && !hasJava)
                return null;

            string javaPath = hasJavaw ? javaw : java;
            string version = string.Empty;
            int major = 0;
            string architecture = "unknown", vendor = string.Empty;

            string releasePath = Path.Combine(root, "release");
            if (File.Exists(releasePath))
            {
                try
                {
                    string content = File.ReadAllText(releasePath);
                    major = ParseJavaMajorFromRelease(content) ?? 0;
                    version = ExtractReleaseVersion(content) ?? string.Empty;
                    architecture = ReleaseValue(content, "OS_ARCH") ?? "unknown";
                    vendor = ReleaseValue(content, "IMPLEMENTOR") ?? string.Empty;
                }
                catch
                {
                    // Fall back to java -version below.
                }
            }

            if (major <= 0)
            {
                string output = RunJavaVersion(hasJava ? java : javaPath);
                major = ParseJavaMajorFromVersionOutput(output) ?? 0;
                if (major > 0)
                    version = output.Split('\n')[0].Trim();
            }

            architecture = ReadPeArchitecture(javaPath) ?? architecture;
            return new JavaInfo { JavaPath = javaPath, Version = version, Major = major, Architecture = architecture, Vendor = vendor };
        }

        /// <summary>
        /// Detects a JDK from a user-supplied path, which may be a direct java.exe/javaw.exe
        /// path, a JDK root directory, or its bin directory. Returns null when the path does
        /// not point to a detectable runtime.
        /// </summary>
        public static JavaInfo? DetectExecutable(string? javaPath)
        {
            if (string.IsNullOrWhiteSpace(javaPath))
                return null;

            string full;
            try
            {
                full = Path.GetFullPath(javaPath.Trim());
            }
            catch
            {
                return null;
            }

            if (File.Exists(full))
            {
                string name = Path.GetFileName(full);
                if (!name.Equals("java.exe", StringComparison.OrdinalIgnoreCase) && !name.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase)) return null;
                string? binDir = Path.GetDirectoryName(full);
                if (binDir != null && Path.GetFileName(binDir).Equals("bin", StringComparison.OrdinalIgnoreCase))
                    return Detect(Path.GetDirectoryName(binDir)!);

                return Detect(binDir ?? full);
            }

            if (Directory.Exists(full))
                return Detect(Path.GetFileName(full).Equals("bin", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(full)! : full);

            return null;
        }

        /// <summary>Normalizes a raw version string to its major version: 1.8.x → 8, 21.0.8 → 21.</summary>
        public static int ParseJavaMajor(string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return 0;

            string v = version.Trim();
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
                v = v.Substring(1, v.Length - 2);

            if (v.StartsWith("1.8", StringComparison.Ordinal))
            {
                if (v.Length == 3) return 8;
                char c = v[3];
                if (c == '.' || c == '_') return 8;
            }

            int dot = v.IndexOf('.');
            string majorPart = dot >= 0 ? v.Substring(0, dot) : v;
            return int.TryParse(majorPart, out int major) && major > 0 ? major : 0;
        }

        /// <summary>Extracts the JAVA_VERSION value from a JDK release file and returns its major.</summary>
        public static int? ParseJavaMajorFromRelease(string releaseContent)
        {
            if (string.IsNullOrWhiteSpace(releaseContent))
                return null;

            foreach (string line in releaseContent.Split('\n'))
            {
                string t = line.Trim();
                if (!t.StartsWith("JAVA_VERSION=", StringComparison.OrdinalIgnoreCase))
                    continue;

                string value = t.Substring("JAVA_VERSION=".Length).Trim().Trim('"');
                int major = ParseJavaMajor(value);
                return major > 0 ? major : (int?)null;
            }
            return null;
        }

        /// <summary>Extracts the quoted version from "java -version" output and returns its major.</summary>
        public static int? ParseJavaMajorFromVersionOutput(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return null;

            string first = output.Split('\n')[0].Trim();
            int open = first.IndexOf('"');
            if (open < 0)
                return null;
            int close = first.IndexOf('"', open + 1);
            if (close < 0)
                return null;

            string version = first.Substring(open + 1, close - open - 1);
            int major = ParseJavaMajor(version);
            return major > 0 ? major : (int?)null;
        }

        private static JavaInfo BestOf(List<JavaInfo> list)
        {
            return list
                .OrderByDescending(c => c.Bits == 64)
                .ThenByDescending(c => PreferJavaw(c))
                .ThenByDescending(c => c.Version, VersionComparer)
                .First();
        }

        private static int PreferJavaw(JavaInfo c)
            => c.JavaPath.EndsWith("javaw.exe", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        private sealed class VersionComparerImpl : IComparer<string>
        {
            public int Compare(string? x, string? y)
            {
                int[] a = ParseSegments(x);
                int[] b = ParseSegments(y);
                int max = Math.Max(a.Length, b.Length);
                for (int i = 0; i < max; i++)
                {
                    int av = i < a.Length ? a[i] : 0;
                    int bv = i < b.Length ? b[i] : 0;
                    if (av != bv) return av.CompareTo(bv);
                }
                return 0;
            }

            private static int[] ParseSegments(string? v)
            {
                if (string.IsNullOrWhiteSpace(v))
                    return Array.Empty<int>();

                string token = v.Trim();
                int sp = token.IndexOfAny(new[] { ' ', '\t' });
                if (sp >= 0) token = token.Substring(0, sp);
                token = token.Trim('"');

                var list = new List<int>();
                foreach (string seg in token.Split('.'))
                {
                    int end = 0;
                    while (end < seg.Length && char.IsDigit(seg[end])) end++;
                    if (end == 0) break;
                    if (!int.TryParse(seg.Substring(0, end), out int n)) break;
                    list.Add(n);
                }
                return list.ToArray();
            }
        }

        private static string? ExtractReleaseVersion(string content)
        {
            foreach (string line in content.Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith("JAVA_VERSION=", StringComparison.OrdinalIgnoreCase))
                    return t.Substring("JAVA_VERSION=".Length).Trim().Trim('"');
            }
            return null;
        }

        private static string RunJavaVersion(string javaPath)
        {
            try
            {
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = javaPath,
                        Arguments = "-version",
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                    },
                };
                proc.Start();
                var stderr = proc.StandardError.ReadToEndAsync();
                var stdout = proc.StandardOutput.ReadToEndAsync();
                if (!proc.WaitForExit(3000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    return string.Empty;
                }
                if (!System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { stderr, stdout }, 1000)) return string.Empty;
                return stderr.Result + stdout.Result;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string? ReleaseValue(string content, string key)
            => content.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.Ordinal))?
                .Substring(key.Length + 1).Trim().Trim('"');

        private static string? ReadPeArchitecture(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var reader = new BinaryReader(stream);
                if (reader.ReadUInt16() != 0x5A4D) return null;
                stream.Position = 0x3c;
                int offset = reader.ReadInt32();
                if (offset < 64 || offset > stream.Length - 6) return null;
                stream.Position = offset;
                if (reader.ReadUInt32() != 0x4550) return null;
                return reader.ReadUInt16() switch { 0x8664 => "x64", 0x14c => "x86", 0xaa64 => "arm64", _ => "unknown" };
            }
            catch { return null; }
        }

        private static void AddRoot(List<string> roots, HashSet<string> seen, string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
                return;

            string full;
            try
            {
                full = Path.GetFullPath(dir.Trim());
            }
            catch
            {
                return;
            }

            if (!File.Exists(Path.Combine(full, "bin", "javaw.exe"))
                && !File.Exists(Path.Combine(full, "bin", "java.exe")))
                return;

            if (seen.Add(full))
                roots.Add(full);
        }

        private static IEnumerable<string> SafeEnumerateDirectories(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                    return Array.Empty<string>();
                return Directory.EnumerateDirectories(path).ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static void AddRegistryRoots(List<string> roots, HashSet<string> seen)
        {
            string[] subKeys = new[]
            {
                @"SOFTWARE\JavaSoft\JDK",
                @"SOFTWARE\JavaSoft\Java Development Kit",
            };

            foreach (string subKey in subKeys)
            {
                foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                {
                    foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    {
                        using RegistryKey? baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using RegistryKey? key = baseKey?.OpenSubKey(subKey);
                        if (key == null)
                            continue;

                        foreach (string name in key.GetSubKeyNames())
                        {
                            using RegistryKey? sub = key.OpenSubKey(name);
                            string? javaHome = sub?.GetValue("JavaHome") as string;
                            if (!string.IsNullOrWhiteSpace(javaHome))
                                AddRoot(roots, seen, javaHome);
                        }
                    }
                }
            }
        }
    }
}
