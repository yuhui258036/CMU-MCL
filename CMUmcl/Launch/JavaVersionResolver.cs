using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CMUmcl.Launch
{
    /// <summary>
    /// Required Java major version, plus the optional Mojang runtime component name
    /// (e.g. java-runtime-epsilon) that can be auto-downloaded when no local JDK matches.
    /// </summary>
    public sealed record JavaRequirement(int Major, string? Component);

    /// <summary>
    /// Resolves the required Java major version (and optional Mojang runtime component)
    /// for a Minecraft game version by reading the installed version JSON, falling back
    /// to a central mapping when the JSON is unavailable.
    /// </summary>
    public static class JavaVersionResolver
    {
        /// <summary>
        /// Reads {minecraftDir}\versions\{gameVersion}\{gameVersion}.json and returns
        /// javaVersion.majorVersion + javaVersion.component. On any failure (missing
        /// file, malformed JSON, missing/invalid majorVersion) falls back to the mapping
        /// table with Component = null.
        /// </summary>
        public static JavaRequirement ResolveRequirement(string gameVersion, string minecraftDir)
        {
            string versionJson = Path.Combine(minecraftDir, "versions", gameVersion, gameVersion + ".json");

            if (File.Exists(versionJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(versionJson));
                    if (doc.RootElement.TryGetProperty("javaVersion", out JsonElement jv))
                    {
                        int major = ParseMajorVersion(jv);
                        if (major > 0)
                        {
                            string? component = null;
                            if (jv.ValueKind == JsonValueKind.Object
                                && jv.TryGetProperty("component", out JsonElement comp)
                                && comp.ValueKind == JsonValueKind.String)
                            {
                                string? c = comp.GetString();
                                if (!string.IsNullOrWhiteSpace(c))
                                    component = c;
                            }
                            return new JavaRequirement(major, component);
                        }
                    }
                }
                catch (JsonException) { /* fall back */ }
                catch (IOException) { /* fall back */ }
                catch (UnauthorizedAccessException) { /* fall back */ }
            }

            return new JavaRequirement(ResolveFallbackMajor(gameVersion), null);
        }

        /// <summary>
        /// Fallback mapping used only when the version JSON is unavailable:
        /// &lt;1.17 → 8; 1.17 → 16; 1.18–1.20.4 → 17; 1.20.5–1.21.x → 21; ≥26.1 → 25.
        /// </summary>
        public static int ResolveFallbackMajor(string gameVersion)
        {
            int[] v = ParseVersionSegments(gameVersion);

            if (CompareSegments(v, new[] { 1, 17 }) < 0) return 8;      // < 1.17
            if (CompareSegments(v, new[] { 1, 18 }) < 0) return 16;
            if (CompareSegments(v, new[] { 1, 20, 5 }) < 0) return 17;
            if (CompareSegments(v, new[] { 26, 1 }) < 0) return 21;
            return 25;
        }

        private static int ParseMajorVersion(JsonElement jv)
        {
            if (jv.ValueKind != JsonValueKind.Object)
                return 0;

            if (!jv.TryGetProperty("majorVersion", out JsonElement mv))
                return 0;

            if (mv.ValueKind == JsonValueKind.Number && mv.TryGetInt32(out int num))
                return num > 0 ? num : 0;

            if (mv.ValueKind == JsonValueKind.String && int.TryParse(mv.GetString(), out int str))
                return str > 0 ? str : 0;

            return 0;
        }

        private static int[] ParseVersionSegments(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return Array.Empty<int>();

            var list = new List<int>();
            foreach (string raw in version.Split('.'))
            {
                int end = 0;
                while (end < raw.Length && char.IsDigit(raw[end])) end++;
                if (end == 0) break;
                if (!int.TryParse(raw.Substring(0, end), out int n)) break;
                list.Add(n);
            }
            return list.ToArray();
        }

        private static int CompareSegments(int[] a, int[] b)
        {
            int max = Math.Max(a.Length, b.Length);
            for (int i = 0; i < max; i++)
            {
                int av = i < a.Length ? a[i] : 0;
                int bv = i < b.Length ? b[i] : 0;
                if (av != bv) return av.CompareTo(bv);
            }
            return 0;
        }
    }
}
