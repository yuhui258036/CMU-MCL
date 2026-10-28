# Java 版本选择与运行时自动下载 实施计划（CMUmcl）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers-subagent-driven-development (recommended) or superpowers-executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 启动时按实例的 Minecraft 基础版本自动选择本地兼容 JDK（本地优先），无兼容 JDK 且版本 JSON 声明了 `javaVersion.component` 时自动从 Mojang 官方下载校验并安装对应运行时；既不静默回退到 Java 21，也绝不留半成品运行时。

**Architecture:** 新增两个职责单一组件——`JavaVersionResolver`（读版本 JSON 解析所需主版本 + component，含集中回退映射）与 `MojangRuntimeInstaller`（windows-x64 运行时下载/SHA-1+size 校验/zip-slip 防护/暂存+原子移动/`.complete` 复用）；重写 `JavaLocator` 为「扫描 → 主版本解析 → 选择」三段；`LaunchService` 将 Java 选择从安装前移到原版/Fabric 安装之后，按「本地优先 → 自动下载 → 清晰报错」决策；`LaunchProgress` 增加 `JavaVersion`/`JavaPath` 上报，下载进度复用既有数值字段。

**Tech Stack:** .NET 10 (net10.0-windows, WPF)，CmlLib.Core 4.1.0；新组件仅用 BCL（System.Net.Http / System.Text.Json / System.Security.Cryptography / Microsoft.Win32.Registry），无新增 NuGet 包。

---

## 环境与构建约束（所有命令必须遵守）

- **dotnet 路径**：`C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe`（系统 dotnet 为 9，项目需 .NET 10；SDK 10.0.403 已安装）。
- **工作数据已迁移到 D:**：原先 `%LOCALAPPDATA%\CMUmcl`（约 1.7GB）已移动到 `D:\新启动器\CMUmclData`，并在原路径建立目录联接（junction）；因此 `%LOCALAPPDATA%\CMUmcl\java\runtime` 实际落盘在 D:，C: 当前约 2.5GB 空闲。构建产物、TEMP/TMP、DOTNET_CLI_HOME、NUGET_PACKAGES 均使用 D:。若磁盘不足导致下载失败，属于合法的磁盘错误路径（应清理并重试或释放空间），不视为代码缺陷。
- 每个 `dotnet` 命令前设置环境变量（本机 PowerShell 5.1，逐条命令各自设置，变量不跨调用保留）：

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
```

- 构建命令模板：

```powershell
& $dotnet build "D:\新启动器\CMUmcl\CMUmcl.csproj" -c Debug
```

- 预期成功标识：输出末尾含 `生成成功。` 或 `Build succeeded`，且含 `0 Error(s)`。
- 无 git 仓库、无测试工程。计划使用 `D:\新启动器\.tmp\JavaHarness`（产品源码树之外）作为**临时验证用控制台工程**（非交付物、不进入打包产物）。

## 文件结构

| 文件 | 动作 | 职责 |
| --- | --- | --- |
| `CMUmcl\Launch\JavaVersionResolver.cs` | 新增 | `JavaRequirement` record + `ResolveRequirement`（读 JSON majorVersion/component + 回退映射） |
| `CMUmcl\Launch\JavaLocator.cs` | 重写 | `JavaInfo.Major` + `Scan`/`Select`/`Detect`/`ParseJavaMajor*`（扫描/主版本解析/选择） |
| `CMUmcl\Content\LauncherPaths.cs` | 修改 | `JavaDirectory`/`JavaRuntimeDirectory`/`CreateStagingDirectory` |
| `CMUmcl\Launch\MojangRuntimeInstaller.cs` | 新增 | 运行时下载/校验/zip-slip 防护/暂存+原子移动/`.complete` 复用 |
| `CMUmcl\Launch\LaunchService.cs` | 修改 | Java 选择时机调整 + 本地优先/自动下载/报错 + 进度上报 |
| `CMUmcl\Launch\LaunchProgress.cs` | 修改 | 增加 `JavaVersion`/`JavaPath` |
| `CMUmcl\README-step3.md` | 新增 | 步骤三说明文档 |
| `D:\新启动器\.tmp\JavaHarness\*` | 新增（临时） | 验证用控制台工程（不进入打包） |

---

## 前置准备（开始前执行一次）

- [x] **Step 0.1：创建临时目录并备份将被修改的 4 个源文件**

```powershell
New-Item -ItemType Directory -Force -Path "D:\新启动器\.tmp","D:\新启动器\.dotnet-home","D:\新启动器\.nuget\packages" | Out-Null
$bk = "D:\新启动器\.tmp\backup-step3"
New-Item -ItemType Directory -Force -Path $bk | Out-Null
Copy-Item -LiteralPath "D:\新启动器\CMUmcl\Launch\JavaLocator.cs" -Destination $bk
Copy-Item -LiteralPath "D:\新启动器\CMUmcl\Launch\LaunchService.cs" -Destination $bk
Copy-Item -LiteralPath "D:\新启动器\CMUmcl\Launch\LaunchProgress.cs" -Destination $bk
Copy-Item -LiteralPath "D:\新启动器\CMUmcl\Content\LauncherPaths.cs" -Destination $bk
Get-ChildItem -LiteralPath $bk | Select-Object Name
```

- 预期：打印 `JavaLocator.cs`、`LaunchService.cs`、`LaunchProgress.cs`、`LauncherPaths.cs` 四个文件名。回滚方式：将 `D:\新启动器\.tmp\backup-step3\*.cs` 覆盖回原路径即可。

- [x] **Step 0.2：确认 dotnet 与 SDK 可用**

```powershell
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet --list-sdks
```

- 预期：包含 `10.0.403 [C:\Users\PC\AppData\Local\Microsoft\dotnet\sdk]`。

---

## 任务 1：JavaVersionResolver（需求解析 + 回退映射 + 纯函数测试）

**Files:**
- Create: `D:\新启动器\CMUmcl\Launch\JavaVersionResolver.cs`
- Create: `D:\新启动器\.tmp\JavaHarness\JavaHarness.csproj`
- Create: `D:\新启动器\.tmp\JavaHarness\Program.cs`
- Create: `D:\新启动器\.tmp\JavaHarness\State.cs`
- Create: `D:\新启动器\.tmp\JavaHarness\TestRegistry.cs`
- Create: `D:\新启动器\.tmp\JavaHarness\TestsResolver.cs`

- [x] **Step 1.1：创建 `JavaVersionResolver.cs`**

```csharp
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
        /// &lt;1.17 → 8; 1.17–1.20.4 → 17; 1.20.5–1.21.8 → 21; ≥1.21.9 → 25.
        /// </summary>
        public static int ResolveFallbackMajor(string gameVersion)
        {
            int[] v = ParseVersionSegments(gameVersion);

            if (CompareSegments(v, new[] { 1, 17 }) < 0) return 8;      // < 1.17
            if (CompareSegments(v, new[] { 1, 20, 5 }) < 0) return 17;  // 1.17 – 1.20.4
            if (CompareSegments(v, new[] { 1, 21, 9 }) < 0) return 21;  // 1.20.5 – 1.21.8
            return 25;                                                  // ≥ 1.21.9
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
```

- [x] **Step 1.2：创建验证工程 `JavaHarness.csproj`**

```powershell
New-Item -ItemType Directory -Force -Path "D:\新启动器\.tmp\JavaHarness" | Out-Null
```

文件 `D:\新启动器\.tmp\JavaHarness\JavaHarness.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>JavaHarness</RootNamespace>
    <AssemblyName>JavaHarness</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\CMUmcl\CMUmcl.csproj" />
    <FrameworkReference Include="Microsoft.WindowsDesktop.App" />
  </ItemGroup>

</Project>
```

- [x] **Step 1.3：创建 `Program.cs`（入口，只写一次）**

文件 `D:\新启动器\.tmp\JavaHarness\Program.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace JavaHarness
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            Console.WriteLine("== JavaHarness ==");

            var state = new State
            {
                Download = args.Contains("--download", StringComparer.OrdinalIgnoreCase),
            };

            foreach (Func<State, Task> test in TestRegistry.Tests)
            {
                try
                {
                    await test(state);
                }
                catch (Exception ex)
                {
                    state.Check("unhandled: " + ex.GetType().Name, false);
                    Console.WriteLine("    " + ex.Message);
                }
            }

            Console.WriteLine(state.Failed == 0
                ? "== ALL TESTS PASSED =="
                : $"== {state.Failed} TEST(S) FAILED ==");

            return state.Failed == 0 ? 0 : 1;
        }
    }
}
```

- [x] **Step 1.4：创建 `State.cs` 与 `TestRegistry.cs`**

文件 `D:\新启动器\.tmp\JavaHarness\State.cs`：

```csharp
using System;

namespace JavaHarness
{
    internal sealed class State
    {
        public bool Download { get; set; }
        public int Failed { get; private set; }

        public void Check(string name, bool condition)
        {
            Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
            if (!condition)
                Failed++;
        }
    }
}
```

文件 `D:\新启动器\.tmp\JavaHarness\TestRegistry.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace JavaHarness
{
    internal static class TestRegistry
    {
        public static readonly List<Func<State, Task>> Tests = new();
    }
}
```

- [x] **Step 1.5：创建 `TestsResolver.cs`（任务 1 的纯函数测试）**

文件 `D:\新启动器\.tmp\JavaHarness\TestsResolver.cs`：

```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using CMUmcl.Launch;

namespace JavaHarness
{
    internal static class TestsResolver
    {
        static TestsResolver()
        {
            TestRegistry.Tests.Add(TestResolveFallback);
            TestRegistry.Tests.Add(TestResolveJson);
        }

        private static Task TestResolveFallback(State s)
        {
            Console.WriteLine("-- ResolveRequirement fallback (missing JSON) --");
            string noDir = Path.Combine(Path.GetTempPath(), "no-such-minecraft-" + Guid.NewGuid().ToString("N"));

            s.Check("26.3 → 25", JavaVersionResolver.ResolveRequirement("26.3", noDir).Major == 25);
            s.Check("1.20.1 → 17", JavaVersionResolver.ResolveRequirement("1.20.1", noDir).Major == 17);
            s.Check("1.20.4 → 17", JavaVersionResolver.ResolveRequirement("1.20.4", noDir).Major == 17);
            s.Check("1.20.5 → 21", JavaVersionResolver.ResolveRequirement("1.20.5", noDir).Major == 21);
            s.Check("1.21.8 → 21", JavaVersionResolver.ResolveRequirement("1.21.8", noDir).Major == 21);
            s.Check("1.21.9 → 25", JavaVersionResolver.ResolveRequirement("1.21.9", noDir).Major == 25);
            s.Check("1.16.5 → 8", JavaVersionResolver.ResolveRequirement("1.16.5", noDir).Major == 8);
            s.Check("fallback Component == null", JavaVersionResolver.ResolveRequirement("1.20.1", noDir).Component == null);

            return Task.CompletedTask;
        }

        private static Task TestResolveJson(State s)
        {
            Console.WriteLine("-- ResolveRequirement (JSON) --");
            string tmp = Path.Combine(Path.GetTempPath(), "harness-mc-" + Guid.NewGuid().ToString("N"));
            string vdir = Path.Combine(tmp, "versions", "26.3");
            Directory.CreateDirectory(vdir);

            File.WriteAllText(Path.Combine(vdir, "26.3.json"),
                "{\"javaVersion\":{\"component\":\"java-runtime-epsilon\",\"majorVersion\":25}}");
            JavaRequirement r = JavaVersionResolver.ResolveRequirement("26.3", tmp);
            s.Check("JSON 26.3 → Major 25 + component", r.Major == 25 && r.Component == "java-runtime-epsilon");

            File.WriteAllText(Path.Combine(vdir, "26.3.json"),
                "{\"javaVersion\":{\"component\":\"java-runtime-epsilon\",\"majorVersion\":\"25\"}}");
            JavaRequirement r2 = JavaVersionResolver.ResolveRequirement("26.3", tmp);
            s.Check("JSON string majorVersion → 25", r2.Major == 25 && r2.Component == "java-runtime-epsilon");

            File.WriteAllText(Path.Combine(vdir, "26.3.json"), "{\"id\":\"26.3\"}");
            JavaRequirement r3 = JavaVersionResolver.ResolveRequirement("26.3", tmp);
            s.Check("JSON without javaVersion → fallback 25 + null", r3.Major == 25 && r3.Component == null);

            string mc = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CMUmcl", "minecraft");
            if (Directory.Exists(Path.Combine(mc, "versions", "1.20.1")))
            {
                JavaRequirement rr = JavaVersionResolver.ResolveRequirement("1.20.1", mc);
                s.Check("real 1.20.1 → 17 + java-runtime-gamma",
                    rr.Major == 17 && rr.Component == "java-runtime-gamma");
            }
            else
            {
                Console.WriteLine("SKIP real 1.20.1 (not installed)");
            }

            Directory.Delete(tmp, recursive: true);
            return Task.CompletedTask;
        }
    }
}
```

- [x] **Step 1.6：构建 CMUmcl（验证新文件可编译）**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet build "D:\新启动器\CMUmcl\CMUmcl.csproj" -c Debug
```

- 预期：末尾含 `生成成功。` / `Build succeeded` 且 `0 Error(s)`。

- [x] **Step 1.7：运行任务 1 的纯函数测试**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet run --project "D:\新启动器\.tmp\JavaHarness\JavaHarness.csproj"
```

- 预期：所有行以 `PASS ` 开头（本任务仅有 ResolveRequirement 用例），末尾 `== ALL TESTS PASSED ==`。

- [x] **Step 1.8：实现者自查**

复核 `ResolveRequirement` 的回退边界（`<1.17→8`、`1.20.4→17`、`1.20.5→21`、`1.21.8→21`、`1.21.9→25`）与 JSON 取值（`majorVersion` 数字/字符串均接受，`component` 可选）。若测试未全绿，修正后重跑 Step 1.6–1.7 直至通过。

---

## 任务 2：JavaLocator（扫描 / 主版本解析 / 选择）

**Files:**
- Rewrite: `D:\新启动器\CMUmcl\Launch\JavaLocator.cs`（整体替换为下方完整内容）
- Create: `D:\新启动器\.tmp\JavaHarness\TestsLocator.cs`

- [x] **Step 2.1：整体替换 `JavaLocator.cs`**

删除原文件全部内容，替换为：

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace CMUmcl.Launch
{
    /// <summary>Located Java runtime (javaw.exe path + version + major version).</summary>
    public sealed class JavaInfo
    {
        public string JavaPath { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public int Major { get; init; }
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
        public static IReadOnlyList<JavaInfo> Scan()
        {
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

            var result = new List<JavaInfo>();
            foreach (string root in roots)
            {
                JavaInfo? info = Detect(root);
                if (info != null && info.Major > 0)
                    result.Add(info);
            }
            return result;
        }

        /// <summary>
        /// Selects the best candidate for the required major version: exact match first,
        /// otherwise the lowest major ≥ required. Prefers javaw.exe, then the highest
        /// full version. Returns null when nothing matches (never a lower version).
        /// </summary>
        public static JavaInfo? Select(int requiredMajor, IReadOnlyList<JavaInfo> candidates)
        {
            if (requiredMajor <= 0 || candidates == null)
                return null;

            var eligible = candidates.Where(c => c.Major > 0).ToList();
            if (eligible.Count == 0)
                return null;

            var exact = eligible.Where(c => c.Major == requiredMajor).ToList();
            if (exact.Count > 0)
                return BestOf(exact);

            var higher = eligible.Where(c => c.Major >= requiredMajor).ToList();
            if (higher.Count == 0)
                return null;

            int bestMajor = higher.Min(c => c.Major);
            return BestOf(higher.Where(c => c.Major == bestMajor).ToList());
        }

        // TEMPORARY: compatibility shim so LaunchService keeps compiling until Task 5 removes it.
        public static JavaInfo Locate()
        {
            return Scan().FirstOrDefault()
                ?? throw new LaunchException("未找到 Java 运行环境。请安装 Java 17 或 21，并设置 JAVA_HOME 环境变量后重试。");
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

            string releasePath = Path.Combine(root, "release");
            if (File.Exists(releasePath))
            {
                try
                {
                    string content = File.ReadAllText(releasePath);
                    major = ParseJavaMajorFromRelease(content) ?? 0;
                    version = ExtractReleaseVersion(content) ?? string.Empty;
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

            return new JavaInfo { JavaPath = javaPath, Version = version, Major = major };
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
                .OrderByDescending(c => PreferJavaw(c))
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
                string output = proc.StandardError.ReadToEnd() + proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);
                return output;
            }
            catch
            {
                return string.Empty;
            }
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
```

- [x] **Step 2.2：创建 `TestsLocator.cs`**

文件 `D:\新启动器\.tmp\JavaHarness\TestsLocator.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CMUmcl.Launch;

namespace JavaHarness
{
    internal static class TestsLocator
    {
        static TestsLocator()
        {
            TestRegistry.Tests.Add(TestParseJavaMajor);
            TestRegistry.Tests.Add(TestVersionOutputAndRelease);
            TestRegistry.Tests.Add(TestSelect);
            TestRegistry.Tests.Add(TestSelectionAgainstLocal);
        }

        private static Task TestParseJavaMajor(State s)
        {
            Console.WriteLine("-- ParseJavaMajor --");
            s.Check("1.8.0_402 → 8", JavaLocator.ParseJavaMajor("1.8.0_402") == 8);
            s.Check("1.8 → 8", JavaLocator.ParseJavaMajor("1.8") == 8);
            s.Check("21.0.8 → 21", JavaLocator.ParseJavaMajor("21.0.8") == 21);
            s.Check("26.0.2 → 26", JavaLocator.ParseJavaMajor("26.0.2") == 26);
            s.Check("26.0.2.1 → 26", JavaLocator.ParseJavaMajor("26.0.2.1") == 26);
            s.Check("empty → 0", JavaLocator.ParseJavaMajor("") == 0);
            return Task.CompletedTask;
        }

        private static Task TestVersionOutputAndRelease(State s)
        {
            Console.WriteLine("-- ParseJavaMajorFromVersionOutput / Release --");
            s.Check("openjdk version \"21.0.8\" → 21",
                JavaLocator.ParseJavaMajorFromVersionOutput(
                    "openjdk version \"21.0.8\" 2025-07-15 LTS\r\nOpenJDK 64-Bit Server VM (build 21.0.8+12-LTS-250)") == 21);
            s.Check("release JAVA_VERSION=\"26.0.2.1\" → 26",
                JavaLocator.ParseJavaMajorFromRelease(
                    "IMPLEMENTOR=\"Oracle Corporation\"\nJAVA_VERSION=\"26.0.2.1\"\nJAVA_VERSION_DATE=\"2026-08-18\"") == 26);
            return Task.CompletedTask;
        }

        private static Task TestSelect(State s)
        {
            Console.WriteLine("-- Select --");
            var c21 = new JavaInfo { JavaPath = @"C:\jdk21\bin\javaw.exe", Version = "21.0.8", Major = 21 };
            var c22 = new JavaInfo { JavaPath = @"C:\jdk22\bin\javaw.exe", Version = "22.0.2", Major = 22 };
            var c26 = new JavaInfo { JavaPath = @"C:\jdk26\bin\javaw.exe", Version = "26.0.2.1", Major = 26 };
            var all = new List<JavaInfo> { c21, c22, c26 };

            s.Check("R=26 exact → 26", JavaLocator.Select(26, all)?.Major == 26);
            s.Check("R=25 → 26 (lowest ≥25)", JavaLocator.Select(25, all)?.Major == 26);
            s.Check("R=17 → 21 (lowest ≥17)", JavaLocator.Select(17, all)?.Major == 21);
            s.Check("R=21 exact → 21", JavaLocator.Select(21, all)?.Major == 21);
            s.Check("R=21 {22,26} → 22", JavaLocator.Select(21, new List<JavaInfo> { c22, c26 })?.Major == 22);
            s.Check("R=27 → null", JavaLocator.Select(27, all) == null);

            var java26 = new JavaInfo { JavaPath = @"C:\jdk26b\bin\java.exe", Version = "26.0.3", Major = 26 };
            JavaInfo? pick = JavaLocator.Select(25, new List<JavaInfo> { java26, c26 });
            s.Check("javaw preferred on equal major",
                pick != null && pick.JavaPath.EndsWith("javaw.exe", StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }

        private static Task TestSelectionAgainstLocal(State s)
        {
            Console.WriteLine("-- Selection vs local JDKs (acceptance 1 & 2) --");
            IReadOnlyList<JavaInfo> scan = JavaLocator.Scan();
            foreach (JavaInfo j in scan)
                Console.WriteLine($"  major={j.Major} version={j.Version} path={j.JavaPath}");

            JavaInfo? for263 = JavaLocator.Select(25, scan);
            s.Check("26.3 (R=25) → major 26", for263?.Major == 26);
            s.Check("26.3 → jdk-26.0.2.1 path",
                for263 != null && for263.JavaPath.Contains("jdk-26.0.2.1", StringComparison.OrdinalIgnoreCase));

            JavaInfo? for1201 = JavaLocator.Select(17, scan);
            s.Check("1.20.1 (R=17) → major 21", for1201?.Major == 21);
            s.Check("1.20.1 → jdk-21 path",
                for1201 != null && for1201.JavaPath.Contains("jdk-21", StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }
    }
}
```

- [x] **Step 2.3：构建 CMUmcl**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet build "D:\新启动器\CMUmcl\CMUmcl.csproj" -c Debug
```

- 预期：`0 Error(s)`。（`JavaLocator.Locate()` 仍以临时兼容垫片存在，`LaunchService.cs` 旧代码可继续编译；该垫片在任务 5 删除。）

- [x] **Step 2.4：运行测试工程（含 1.8 归一化 / javaw 偏好 / 本地选择）**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet run --project "D:\新启动器\.tmp\JavaHarness\JavaHarness.csproj"
```

- 预期：`Scan` 打印本机 JDK（`major=21`、`major=22`、`major=26`），`26.3 (R=25) → major 26`、`1.20.1 (R=17) → major 21`、`javaw preferred on equal major` 均为 `PASS`，末尾 `== ALL TESTS PASSED ==`。

- [x] **Step 2.5：实现者自查**

确认 `Scan` 覆盖 7 类来源且按路径去重、`ParseJavaMajor` 的 `1.8` 特判不与 `18.x` 混淆（`"18.0.2"` 不以 `"1.8"` 开头）、`Select` 无匹配返回 `null` 而非抛错。若未全绿，修正后重跑 Step 2.3–2.4。

---

## 任务 3：LauncherPaths 扩展

**Files:**
- Modify: `D:\新启动器\CMUmcl\Content\LauncherPaths.cs`

- [x] **Step 3.1：在 `LauncherPaths.cs` 末尾类内追加三个成员**

用编辑工具将以下旧文本：

```csharp
        /// <summary>Runtime copy of the authlib-injector jar (verified by SHA-256).</summary>
        public static string AuthlibInjectorPath => Path.Combine(RootDirectory, "authlib-injector.jar");
    }
}
```

替换为：

```csharp
        /// <summary>Runtime copy of the authlib-injector jar (verified by SHA-256).</summary>
        public static string AuthlibInjectorPath => Path.Combine(RootDirectory, "authlib-injector.jar");

        /// <summary>Java runtime root directory (%LOCALAPPDATA%\CMUmcl\java).</summary>
        public static string JavaDirectory => Path.Combine(RootDirectory, "java");

        /// <summary>Auto-downloaded Mojang Java runtime directory (%LOCALAPPDATA%\CMUmcl\java\runtime).</summary>
        public static string JavaRuntimeDirectory => Path.Combine(JavaDirectory, "runtime");

        /// <summary>Creates and returns a fresh staging directory under the runtime dir.</summary>
        public static string CreateStagingDirectory()
        {
            string dir = Path.Combine(JavaRuntimeDirectory, ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
```

- [x] **Step 3.2：构建 CMUmcl**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet build "D:\新启动器\CMUmcl\CMUmcl.csproj" -c Debug
```

- 预期：`0 Error(s)`。

- [x] **Step 3.3：实现者自查**

确认新增属性无 `System.IO`/`System` 之外的依赖（`Guid`、`Directory` 已在 `using System;`/`using System.IO;` 范围内）。三处路径均以 `RootDirectory`（`%LOCALAPPDATA%\CMUmcl`）为根。

---

## 任务 4：MojangRuntimeInstaller（运行时自动下载）

**Files:**
- Create: `D:\新启动器\CMUmcl\Launch\MojangRuntimeInstaller.cs`
- Create: `D:\新启动器\.tmp\JavaHarness\TestsInstaller.cs`

- [x] **Step 4.1：创建 `MojangRuntimeInstaller.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using CMUmcl.Content;

namespace CMUmcl.Launch
{
    /// <summary>
    /// Downloads, verifies and installs a single Mojang official Java runtime for
    /// <c>windows-x64</c>. Local JDKs are always preferred by the caller; this is the
    /// last-resort path. Enforces a strict URL host allowlist, SHA-1/size verification,
    /// zip-slip-safe path handling, and staging + atomic move so a half-downloaded
    /// runtime never remains.
    /// </summary>
    public sealed class MojangRuntimeInstaller : IDisposable
    {
        private const string IndexUrl =
            "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

        private const string PlatformKey = "windows-x64";
        private const string CompleteMarker = ".complete";

        private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "piston-meta.mojang.com",
            "piston-data.mojang.com",
        };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly HttpClient _http;

        public MojangRuntimeInstaller()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("CMUmcl/0.1.0");
        }

        /// <summary>
        /// Ensures the given Mojang runtime component (e.g. java-runtime-epsilon) is
        /// installed at %LOCALAPPDATA%\CMUmcl\java\runtime\{component} and returns the
        /// detected JavaInfo. Reuses an existing valid install (marked by .complete).
        /// </summary>
        public async Task<JavaInfo> EnsureRuntimeAsync(
            string component,
            int requiredMajor,
            IProgress<LaunchProgress>? progress,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(component))
                throw new LaunchException("缺少 Java 运行时组件名。");

            ValidateComponentName(component);

            if (!IsWindowsX64())
                throw new LaunchException($"当前仅支持 64 位 Windows，无法自动下载 Java 运行时，请手动安装 Java {requiredMajor}。");

            string finalDir = Path.Combine(LauncherPaths.JavaRuntimeDirectory, component);

            JavaInfo? existing = TryReuse(finalDir, requiredMajor);
            if (existing != null)
                return existing;

            string staging = LauncherPaths.CreateStagingDirectory();
            try
            {
                // 1. Fetch and locate the component in the windows-x64 index.
                string indexJson = await DownloadTextAsync(IndexUrl, ct).ConfigureAwait(false);
                var index = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<RuntimeEntry>>>>(indexJson, JsonOptions)
                    ?? throw new LaunchException("Java 运行时索引为空或格式无效。");

                if (!index.TryGetValue(PlatformKey, out Dictionary<string, List<RuntimeEntry>>? group))
                    throw new LaunchException($"当前平台不支持自动下载该 Java 运行时，请手动安装 Java {requiredMajor}。");

                if (!group.TryGetValue(component, out List<RuntimeEntry>? entries) || entries == null || entries.Count == 0)
                    throw new LaunchException($"当前平台不支持自动下载该 Java 运行时，请手动安装 Java {requiredMajor}。");

                RuntimeEntry entry = entries[0];

                string manifestUrl = entry.Manifest?.Url ?? string.Empty;
                long manifestSize = entry.Manifest?.Size ?? 0;
                string manifestSha1 = entry.Manifest?.Sha1 ?? string.Empty;
                string versionName = entry.Version?.Name ?? string.Empty;

                if (string.IsNullOrWhiteSpace(manifestUrl))
                    throw new LaunchException("Java 运行时清单地址缺失，无法下载。");

                // 2. Download + verify the manifest.
                string manifestPath = Path.Combine(staging, "manifest.json");
                await DownloadToFileAsync(manifestUrl, manifestPath, manifestSize, manifestSha1,
                    "Java 运行时清单校验失败，已中止。", progress, 0, 0, null, 0, ct).ConfigureAwait(false);

                var manifest = JsonSerializer.Deserialize<RuntimeManifest>(
                        await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false), JsonOptions)
                    ?? throw new LaunchException("Java 运行时清单为空或格式无效。");

                Dictionary<string, RuntimeFileEntry> files = manifest.Files ?? new Dictionary<string, RuntimeFileEntry>();

                // 3. Pre-compute totals for progress.
                int totalFiles = files.Count(f => f.Value?.Type == "file");
                long totalBytes = files.Where(f => f.Value?.Type == "file")
                    .Sum(f => f.Value?.Downloads?.Raw?.Size ?? 0);

                var counter = new ByteCounter();
                int done = 0;

                foreach (var kv in files)
                {
                    ct.ThrowIfCancellationRequested();

                    string relative = SanitizeRelativePath(kv.Key);
                    RuntimeFileEntry? fe = kv.Value;

                    if (fe == null)
                        throw new LaunchException("Java 运行时清单条目无效，已中止。");

                    if (fe.Type == "directory")
                    {
                        Directory.CreateDirectory(ResolveUnder(staging, relative));
                        continue;
                    }

                    if (fe.Type != "file")
                        continue; // Ignore unknown types / links.

                    string fileUrl = fe.Downloads?.Raw?.Url ?? string.Empty;
                    long fileSize = fe.Downloads?.Raw?.Size ?? 0;
                    string fileSha1 = fe.Downloads?.Raw?.Sha1 ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(fileUrl))
                        throw new LaunchException($"Java 运行时文件缺少下载地址（{relative}），已中止。");

                    string targetPath = ResolveUnder(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                    await DownloadToFileAsync(fileUrl, targetPath, fileSize, fileSha1,
                        $"Java 运行时文件校验失败（{relative}），已中止。", progress, done, totalFiles, counter, totalBytes, ct).ConfigureAwait(false);

                    done++;
                }

                // 4. Mark complete and atomically move to the final directory.
                File.WriteAllText(Path.Combine(staging, CompleteMarker), string.Empty);

                if (Directory.Exists(finalDir))
                    TryDeleteDirectory(finalDir);

                Directory.CreateDirectory(LauncherPaths.JavaRuntimeDirectory);
                Directory.Move(staging, finalDir);

                // 5. Verify the installed runtime major version.
                JavaInfo? detected = JavaLocator.Detect(finalDir);
                if (detected == null || detected.Major <= 0 || detected.Major < requiredMajor)
                {
                    TryDeleteDirectory(finalDir);
                    throw new LaunchException("Java 运行时校验失败，已清除不完整的下载。");
                }

                return new JavaInfo
                {
                    JavaPath = detected.JavaPath,
                    Version = versionName,
                    Major = detected.Major,
                };
            }
            catch
            {
                TryDeleteDirectory(staging);
                throw;
            }
        }

        // ------------------------------------------------------------------
        // Public static security / verification helpers (unit-testable)
        // ------------------------------------------------------------------

        /// <summary>True when the URL is an absolute HTTPS URL on an allowed host.</summary>
        public static bool IsAllowedUrl(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && AllowedHosts.Contains(uri.Host);
        }

        /// <summary>Throws when a (possibly redirected) final URI is not allowed.</summary>
        public static void EnsureAllowedUri(Uri? uri)
        {
            if (uri == null || uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host))
                throw new LaunchException("Java 运行时下载被重定向到不允许的主机，已中止。");
        }

        /// <summary>
        /// Normalizes a manifest relative path and rejects traversal. Returns the
        /// normalized relative path using the platform directory separator.
        /// </summary>
        public static string SanitizeRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new LaunchException("Java 运行时清单包含空路径，已中止。");

            string normalized = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

            if (Path.IsPathRooted(normalized))
                throw new LaunchException($"检测到非法路径：{path}");

            string[] segments = normalized.Split(Path.DirectorySeparatorChar);
            foreach (string segment in segments)
            {
                if (segment == ".." || segment == ".")
                    throw new LaunchException($"检测到路径越界：{path}");
                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new LaunchException($"检测到非法路径字符：{path}");
            }

            return normalized;
        }

        /// <summary>Resolves a relative path under a base directory and rejects escapes.</summary>
        public static string ResolveUnder(string baseDir, string relativePath)
        {
            string fullBase = Path.GetFullPath(baseDir);
            string fullTarget = Path.GetFullPath(Path.Combine(fullBase, relativePath));

            string baseWithSep = fullBase.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? fullBase
                : fullBase + Path.DirectorySeparatorChar;

            if (!fullTarget.StartsWith(baseWithSep, StringComparison.OrdinalIgnoreCase))
                throw new LaunchException($"目标路径超出运行时目录：{relativePath}");

            return fullTarget;
        }

        /// <summary>True when the file's SHA-1 and size match the expected values.</summary>
        public static bool MatchesSha1AndSize(string filePath, long expectedSize, string expectedSha1)
        {
            try
            {
                var info = new FileInfo(filePath);
                if (expectedSize > 0 && info.Length != expectedSize)
                    return false;

                if (string.IsNullOrWhiteSpace(expectedSha1))
                    return true;

                using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                using var fs = File.OpenRead(filePath);
                var buffer = new byte[81920];
                int read;
                while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                    sha1.AppendData(buffer, 0, read);

                string actual = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
                return string.Equals(actual, expectedSha1.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Internals
        // ------------------------------------------------------------------

        private static bool IsWindowsX64()
            => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
               && Environment.Is64BitOperatingSystem
               && RuntimeInformation.OSArchitecture == Architecture.X64;

        private static void ValidateComponentName(string component)
        {
            if (component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || component.Contains('/')
                || component.Contains('\\')
                || component.Contains(':')
                || component == "."
                || component == "..")
                throw new LaunchException("Java 运行时组件名非法。");
        }

        private static JavaInfo? TryReuse(string finalDir, int requiredMajor)
        {
            if (!Directory.Exists(finalDir))
                return null;

            if (!File.Exists(Path.Combine(finalDir, CompleteMarker)))
                return null;

            JavaInfo? detected = JavaLocator.Detect(finalDir);
            if (detected == null || detected.Major <= 0 || detected.Major < requiredMajor)
                return null;

            return detected;
        }

        private async Task<string> DownloadTextAsync(string url, CancellationToken ct)
        {
            if (!IsAllowedUrl(url))
                throw new LaunchException("Java 运行时下载地址不在允许的主机列表内，已中止。");

            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new LaunchException("网络错误，无法获取 Java 运行时索引，请检查网络连接。", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new LaunchException("获取 Java 运行时索引超时，请稍后再试。", ex);
            }

            using (response)
            {
                EnsureAllowedUri(response.RequestMessage?.RequestUri);

                if (!response.IsSuccessStatusCode)
                    throw new LaunchException($"获取 Java 运行时索引失败（HTTP {(int)response.StatusCode}）。");

                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
        }

        private async Task DownloadToFileAsync(
            string url,
            string targetPath,
            long expectedSize,
            string expectedSha1,
            string failureMessage,
            IProgress<LaunchProgress>? progress,
            int doneFiles,
            int totalFiles,
            ByteCounter? counter,
            long totalBytes,
            CancellationToken ct)
        {
            if (!IsAllowedUrl(url))
                throw new LaunchException("Java 运行时下载地址不在允许的主机列表内，已中止。");

            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new LaunchException("网络错误，无法下载 Java 运行时，请检查网络连接。", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new LaunchException("下载 Java 运行时超时，请稍后再试。", ex);
            }

            using (response)
            {
                EnsureAllowedUri(response.RequestMessage?.RequestUri);

                if (!response.IsSuccessStatusCode)
                    throw new LaunchException($"下载 Java 运行时失败（HTTP {(int)response.StatusCode}）。");

                using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    sha1.AppendData(buffer, 0, read);
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    total += read;

                    if (counter != null)
                    {
                        counter.Value += read;
                        progress?.Report(new LaunchProgress
                        {
                            Stage = "下载 Java",
                            ProgressedTasks = doneFiles,
                            TotalTasks = totalFiles,
                            ProgressedBytes = counter.Value,
                            TotalBytes = totalBytes,
                            Message = $"已下载 {doneFiles}/{totalFiles} 个文件（{FormatUtils.FormatBytes(counter.Value)} / {FormatUtils.FormatBytes(totalBytes)}）",
                        });
                    }
                }

                string actualSha1 = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
                bool sizeOk = expectedSize <= 0 || total == expectedSize;
                bool sha1Ok = string.IsNullOrWhiteSpace(expectedSha1)
                    || string.Equals(actualSha1, expectedSha1.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);

                if (!sizeOk || !sha1Ok)
                    throw new LaunchException(failureMessage);
            }
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
                // Best effort.
            }
        }

        private sealed class ByteCounter
        {
            public long Value;
        }

        private sealed class RuntimeEntry
        {
            public RuntimeManifestRef? Manifest { get; set; }
            public RuntimeVersionInfo? Version { get; set; }
        }

        private sealed class RuntimeManifestRef
        {
            public string? Sha1 { get; set; }
            public long? Size { get; set; }
            public string? Url { get; set; }
        }

        private sealed class RuntimeVersionInfo
        {
            public string? Name { get; set; }
        }

        private sealed class RuntimeManifest
        {
            public Dictionary<string, RuntimeFileEntry>? Files { get; set; }
        }

        private sealed class RuntimeFileEntry
        {
            public string? Type { get; set; }
            public RuntimeDownloads? Downloads { get; set; }
        }

        private sealed class RuntimeDownloads
        {
            public RuntimeRaw? Raw { get; set; }
        }

        private sealed class RuntimeRaw
        {
            public string? Sha1 { get; set; }
            public long? Size { get; set; }
            public string? Url { get; set; }
        }

        public void Dispose() => _http.Dispose();
    }
}
```

- [x] **Step 4.2：创建 `TestsInstaller.cs`（安全 + 下载 + 复用 + 清理测试）**

文件 `D:\新启动器\.tmp\JavaHarness\TestsInstaller.cs`：

```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CMUmcl.Launch;

namespace JavaHarness
{
    internal static class TestsInstaller
    {
        static TestsInstaller()
        {
            TestRegistry.Tests.Add(TestSecurityHelpers);
            TestRegistry.Tests.Add(TestMatchesSha1AndSize);
            TestRegistry.Tests.Add(TestDownloadAsync);
            TestRegistry.Tests.Add(TestReuseAsync);
            TestRegistry.Tests.Add(TestCancelCleanupAsync);
        }

        private static Task TestSecurityHelpers(State s)
        {
            Console.WriteLine("-- Security helpers --");
            s.Check("allow piston-meta https",
                MojangRuntimeInstaller.IsAllowedUrl("https://piston-meta.mojang.com/v1/products/java-runtime/x/all.json"));
            s.Check("allow piston-data https",
                MojangRuntimeInstaller.IsAllowedUrl("https://piston-data.mojang.com/v1/objects/abc/file.dll"));
            s.Check("reject http",
                !MojangRuntimeInstaller.IsAllowedUrl("http://piston-meta.mojang.com/v1/x"));
            s.Check("reject evil host",
                !MojangRuntimeInstaller.IsAllowedUrl("https://evil.example.com/x"));
            s.Check("reject lookalike host",
                !MojangRuntimeInstaller.IsAllowedUrl("https://piston-meta.mojang.com.evil.com/x"));

            s.Check("EnsureAllowedUri ok", TryEnsureAllowed(new Uri("https://piston-data.mojang.com/x")));
            s.Check("EnsureAllowedUri bad host throws", !TryEnsureAllowed(new Uri("https://evil.com/x")));

            s.Check("Sanitize ok",
                MojangRuntimeInstaller.SanitizeRelativePath("bin/javaw.exe")
                == "bin" + Path.DirectorySeparatorChar + "javaw.exe");
            s.Check("Sanitize .. throws", !TrySanitize("../evil"));
            s.Check("Sanitize rooted throws", !TrySanitize("C:\\evil"));
            s.Check("Sanitize empty throws", !TrySanitize(""));

            string tmpBase = Path.Combine(Path.GetTempPath(), "harness-base-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpBase);
            string resolved = MojangRuntimeInstaller.ResolveUnder(tmpBase, "bin" + Path.DirectorySeparatorChar + "javaw.exe");
            s.Check("ResolveUnder ok",
                resolved.StartsWith(Path.GetFullPath(tmpBase) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            s.Check("ResolveUnder escape throws",
                !TryResolveUnder(tmpBase, ".." + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "evil"));
            Directory.Delete(tmpBase, recursive: true);
            return Task.CompletedTask;
        }

        private static bool TryEnsureAllowed(Uri u)
        {
            try { MojangRuntimeInstaller.EnsureAllowedUri(u); return true; }
            catch (LaunchException) { return false; }
        }

        private static bool TrySanitize(string p)
        {
            try { MojangRuntimeInstaller.SanitizeRelativePath(p); return true; }
            catch (LaunchException) { return false; }
        }

        private static bool TryResolveUnder(string b, string r)
        {
            try { MojangRuntimeInstaller.ResolveUnder(b, r); return true; }
            catch (LaunchException) { return false; }
        }

        private static Task TestMatchesSha1AndSize(State s)
        {
            Console.WriteLine("-- MatchesSha1AndSize --");
            string f = Path.Combine(Path.GetTempPath(), "harness-sha-" + Guid.NewGuid().ToString("N") + ".bin");
            byte[] data = System.Text.Encoding.UTF8.GetBytes("hello java runtime");
            File.WriteAllBytes(f, data);
            string realSha1 = Convert.ToHexString(
                System.Security.Cryptography.SHA1.HashData(data)).ToLowerInvariant();

            s.Check("match", MojangRuntimeInstaller.MatchesSha1AndSize(f, data.Length, realSha1));
            s.Check("hash mismatch → false",
                !MojangRuntimeInstaller.MatchesSha1AndSize(f, data.Length, new string('0', 40)));
            s.Check("size mismatch → false",
                !MojangRuntimeInstaller.MatchesSha1AndSize(f, data.Length + 1, realSha1));

            File.Delete(f);
            return Task.CompletedTask;
        }

        private static async Task TestDownloadAsync(State s)
        {
            if (!s.Download)
            {
                Console.WriteLine("SKIP TestDownloadAsync (需要 --download)");
                return;
            }

            Console.WriteLine("-- EnsureRuntimeAsync(java-runtime-epsilon, 25) --");
            Console.WriteLine("   提示：下载约 100MB 到 %LOCALAPPDATA%\\CMUmcl\\java\\runtime（C: 盘，当前剩余约 0.8GB）。");
            using var installer = new MojangRuntimeInstaller();
            var progress = new Progress<LaunchProgress>(p =>
            {
                if (p.TotalBytes > 0)
                    Console.Write($"\r   {p.ProgressedTasks}/{p.TotalTasks} 个文件  {p.ProgressedBytes}/{p.TotalBytes} B   ");
            });

            try
            {
                JavaInfo info = await installer.EnsureRuntimeAsync("java-runtime-epsilon", 25, progress, CancellationToken.None);
                Console.WriteLine();
                s.Check("downloaded major == 25", info.Major == 25);
                s.Check("javaw exists", File.Exists(info.JavaPath));

                string expected = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CMUmcl", "java", "runtime", "java-runtime-epsilon", "bin", "javaw.exe");
                s.Check("path equals expected",
                    string.Equals(info.JavaPath, expected, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("  EXCEPTION: " + ex.Message);
                s.Check("download completed without exception", false);
            }
        }

        private static async Task TestReuseAsync(State s)
        {
            if (!s.Download)
            {
                Console.WriteLine("SKIP TestReuseAsync (需要 --download)");
                return;
            }

            Console.WriteLine("-- .complete reuse (no re-download) --");
            string finalDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CMUmcl", "java", "runtime", "java-runtime-epsilon");
            string marker = Path.Combine(finalDir, ".complete");

            s.Check("final dir exists", Directory.Exists(finalDir));
            s.Check(".complete exists", File.Exists(marker));

            DateTime before = File.GetLastWriteTimeUtc(marker);
            using var installer = new MojangRuntimeInstaller();
            JavaInfo info = await installer.EnsureRuntimeAsync("java-runtime-epsilon", 25, null, CancellationToken.None);
            DateTime after = File.GetLastWriteTimeUtc(marker);

            s.Check("reuse returned major 25", info.Major == 25);
            s.Check("no re-download (.complete mtime unchanged)", before == after);
            s.Check("no leftover staging dirs", !HasStagingDirs());
        }

        private static async Task TestCancelCleanupAsync(State s)
        {
            if (!s.Download)
            {
                Console.WriteLine("SKIP TestCancelCleanupAsync (需要 --download)");
                return;
            }

            Console.WriteLine("-- partial download cleanup (cancel) --");
            const string component = "java-runtime-delta";
            string finalDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CMUmcl", "java", "runtime", component);

            if (Directory.Exists(finalDir))
            {
                Console.WriteLine("  SKIP: " + component + " 已存在，跳过取消清理测试。");
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var installer = new MojangRuntimeInstaller();
            try
            {
                await installer.EnsureRuntimeAsync(component, 21, null, cts.Token);
                Console.WriteLine("  WARN: 取消前竟已完成下载（网络过快）。");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("  cancelled as expected");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  unexpected exception: " + ex.GetType().Name + " " + ex.Message);
            }

            s.Check("no leftover staging dirs after cancel", !HasStagingDirs());
            s.Check("final dir absent after cancel", !Directory.Exists(finalDir));
        }

        private static bool HasStagingDirs()
        {
            string runtimeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CMUmcl", "java", "runtime");
            if (!Directory.Exists(runtimeDir))
                return false;
            return Directory.EnumerateDirectories(runtimeDir)
                .Any(d => Path.GetFileName(d).StartsWith(".staging-", StringComparison.Ordinal));
        }
    }
}
```

- [x] **Step 4.3：构建 CMUmcl**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet build "D:\新启动器\CMUmcl\CMUmcl.csproj" -c Debug
```

- 预期：`0 Error(s)`。

- [x] **Step 4.4：运行测试工程（安全校验，默认模式不触发大下载）**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet run --project "D:\新启动器\.tmp\JavaHarness\JavaHarness.csproj"
```

- 预期：安全用例如下均为 `PASS`：`allow piston-meta https`、`allow piston-data https`、`reject http`、`reject evil host`、`reject lookalike host`、`EnsureAllowedUri bad host throws`、`Sanitize .. throws`、`Sanitize rooted throws`、`Sanitize empty throws`、`ResolveUnder escape throws`、`hash mismatch → false`、`size mismatch → false`；三个下载/复用/取消用例如期打印 `SKIP ... (需要 --download)`；末尾 `== ALL TESTS PASSED ==`。

- [x] **Step 4.5：实现者自查**

确认：索引取 `windows-x64` 分组下 component 数组首元素；manifest SHA-1/size 与每个文件 SHA-1/size 均校验；重定向后调用 `EnsureAllowedUri` 校验最终 URI 主机；`SanitizeRelativePath`+`ResolveUnder` 拒绝 `..`/根路径/空路径/非法字符；失败统一走外层 `catch` 删暂存；成功后先删无效最终目录再 `Directory.Move`。若未全绿，修正后重跑 Step 4.3–4.4。

---

## 任务 5：LaunchService + LaunchProgress 集成

**Files:**
- Modify: `D:\新启动器\CMUmcl\Launch\JavaLocator.cs`（删除临时 `Locate()` 垫片）
- Modify: `D:\新启动器\CMUmcl\Launch\LaunchService.cs`（两处编辑）
- Modify: `D:\新启动器\CMUmcl\Launch\LaunchProgress.cs`（一处编辑）

- [x] **Step 5.1：删除 `JavaLocator.cs` 中的临时 `Locate()` 垫片**

将以下旧文本：

```csharp
        // TEMPORARY: compatibility shim so LaunchService keeps compiling until Task 5 removes it.
        public static JavaInfo Locate()
        {
            return Scan().FirstOrDefault()
                ?? throw new LaunchException("未找到 Java 运行环境。请安装 Java 17 或 21，并设置 JAVA_HOME 环境变量后重试。");
        }

        /// <summary>
        /// Detects a JDK at the given root directory (parent of bin). Returns null when
```

替换为：

```csharp
        /// <summary>
        /// Detects a JDK at the given root directory (parent of bin). Returns null when
```

- [x] **Step 5.2：编辑 `LaunchService.cs` —— 删除安装前的旧 Java 定位**

将以下旧文本：

```csharp
            // 5. Locate Java.
            progress?.Report(new LaunchProgress { Stage = "准备", Message = "正在检测 Java…" });
            JavaInfo java = JavaLocator.Locate();

            // 6. Build shared Minecraft path + launcher.
```

替换为：

```csharp
            // 6. Build shared Minecraft path + launcher.
```

- [x] **Step 5.3：编辑 `LaunchService.cs` —— 在加载器解析之后、构建 MSession 之前插入 Java 选择**

将以下旧文本：

```csharp
                default:
                    throw new LaunchException(UnsupportedLoaderMessage);
            }

            // 9. Build MSession.
```

替换为：

```csharp
                default:
                    throw new LaunchException(UnsupportedLoaderMessage);
            }

            // 8.5 Select a compatible Java runtime (local first, auto-download as fallback).
            JavaRequirement requirement = JavaVersionResolver.ResolveRequirement(gameVersion, LauncherPaths.MinecraftDirectory);

            IReadOnlyList<JavaInfo> javaCandidates = JavaLocator.Scan();
            JavaInfo? java = JavaLocator.Select(requirement.Major, javaCandidates);

            if (java == null && requirement.Component != null)
            {
                progress?.Report(new LaunchProgress { Stage = "下载 Java", Message = "未找到兼容的本地 Java，正在自动下载 Java 运行时…" });
                using var installer = new MojangRuntimeInstaller();
                java = await installer.EnsureRuntimeAsync(requirement.Component, requirement.Major, progress, ct).ConfigureAwait(false);
            }

            if (java == null)
            {
                if (javaCandidates.Count == 0)
                    throw new LaunchException($"未找到 Java 运行环境。请安装 Java {requirement.Major} 并重试。");

                string detected = string.Join(", ", javaCandidates.Select(c => c.Major).Distinct().OrderBy(m => m));
                throw new LaunchException(
                    $"未找到满足 Minecraft {gameVersion} 的 Java（需要 Java {requirement.Major}）。已检测到的 Java 主版本：{detected}。请手动安装 Java {requirement.Major} 后重试。");
            }

            progress?.Report(new LaunchProgress
            {
                Stage = "启动",
                Message = $"已选择 Java {java.Major}（{java.JavaPath}）",
                JavaVersion = java.Major.ToString(),
                JavaPath = java.JavaPath,
            });

            // 9. Build MSession.
```

- [x] **Step 5.4：编辑 `LaunchProgress.cs` —— 增加可选字段**

将以下旧文本：

```csharp
        /// <summary>Total bytes (0 when unknown).</summary>
        public long TotalBytes { get; init; }
    }
}
```

替换为：

```csharp
        /// <summary>Total bytes (0 when unknown).</summary>
        public long TotalBytes { get; init; }

        /// <summary>Selected Java major version (informational; e.g. "26").</summary>
        public string? JavaVersion { get; init; }

        /// <summary>Selected Java executable path (informational).</summary>
        public string? JavaPath { get; init; }
    }
}
```

- [x] **Step 5.5：统一构建 CMUmcl（`Locate()` 垫片已移除、LaunchService 已接入的构建点）**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet build "D:\新启动器\CMUmcl\CMUmcl.csproj" -c Debug
```

- 预期：`0 Error(s)`，无 `JavaLocator.Locate` 未定义、无 `JavaRequirement`/`MojangRuntimeInstaller` 未找到等错误。

- [x] **Step 5.6：实现者自查**

确认 Java 选择确实发生在原版/Fabric 安装（原第 7/8 步）之后；本地命中时不 new 下载器；未命中且 `Component != null` 时才下载；未命中且 `Component == null` 时抛出含所需版本与已检测主版本的错误；`JavaPath = java.JavaPath`（第 10 步）引用的是新选中的 `java`。若构建失败，按编译错误逐条修正后重跑 Step 5.5。

---

## 任务 6：端到端验证

**Files:** 无新增产品代码；运行测试工程（两种模式）+ 最终构建 + 产物检查。

- [x] **Step 6.1：运行测试工程（默认模式：纯函数 + 安全 + 本地选择，不触发下载）**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet run --project "D:\新启动器\.tmp\JavaHarness\JavaHarness.csproj"
```

- 预期：全部 `PASS`，关键行包括：
  - `26.3 (R=25) → major 26`、`26.3 → jdk-26.0.2.1 path`（验收 1）
  - `1.20.1 (R=17) → major 21`、`1.20.1 → jdk-21 path`（验收 2）
  - `javaw preferred on equal major`（验收 §4）
  - 安全用例全 `PASS`（验收 5/6 的纯函数部分）
  - 末尾 `== ALL TESTS PASSED ==`

- [x] **Step 6.2：运行下载端到端测试（`java-runtime-epsilon` 直接下载校验）**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet run --project "D:\新启动器\.tmp\JavaHarness\JavaHarness.csproj" -- --download
```

- 预期：
  - `downloaded major == 25`、`javaw exists`、`path equals expected` 为 `PASS`（验收 3：`java-runtime-epsilon` 下载校验安装成功，返回 `...\java\runtime\java-runtime-epsilon\bin\javaw.exe`，主版本 25）。
  - `.complete reuse`：`final dir exists`、`.complete exists`、`reuse returned major 25`、`no re-download (.complete mtime unchanged)`、`no leftover staging dirs` 均为 `PASS`（复用且不重复下载）。
  - `partial download cleanup`：`no leftover staging dirs after cancel`、`final dir absent after cancel` 均为 `PASS`（取消后暂存与最终目录均被清理）。
  - 末尾 `== ALL TESTS PASSED ==`。

- [x] **Step 6.3：人工核对运行时落盘与 `.complete`**

```powershell
$rt = "$env:LOCALAPPDATA\CMUmcl\java\runtime"
Get-ChildItem -LiteralPath $rt -Force | Select-Object Name, Mode
"--- epsilon 关键文件 ---"
Test-Path -LiteralPath "$rt\java-runtime-epsilon\.complete"
Test-Path -LiteralPath "$rt\java-runtime-epsilon\bin\javaw.exe"
Test-Path -LiteralPath "$rt\java-runtime-epsilon\release"
"--- 无 staging 残留 ---"
(Get-ChildItem -LiteralPath $rt -Directory -Force | Where-Object { $_.Name -like '.staging-*' }).Count
```

- 预期：目录含 `java-runtime-epsilon`；三个 `Test-Path` 均返回 `True`；最后一行输出 `0`（无 staging 残留）。

- [x] **Step 6.4：最终构建（确认净态可编译，无警告阻断）**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet build "D:\新启动器\CMUmcl\CMUmcl.csproj" -c Debug
```

- 预期：`0 Error(s)`（验收 7）；产物 `CMUmcl\bin\Debug\net10.0-windows\CMUmcl.exe` 与 `CMUmcl.dll` 均存在。

- [x] **Step 6.5：产物存在性确认**

```powershell
Get-Item -LiteralPath "D:\新启动器\CMUmcl\bin\Debug\net10.0-windows\CMUmcl.exe" | Select-Object FullName, Length, LastWriteTime
```

- 预期：打印 `CMUmcl.exe` 的完整路径、大小（约 16 万字节）与最新时间戳。

- [x] **Step 6.6：Java 25+ 模块访问风险观察（不阻塞、不擅自改）**

> 已用临时 LaunchHarness 实际启动 `csu-ysu-union`（26.3 + Fabric 0.19.5）：选中 JDK 26，进程运行超过 60 秒并进入主菜单；无 `InaccessibleObjectException` / 模块 opens 错误，无需 `--add-opens`。仅有 Mixin final field 与 `sun.misc.Unsafe` 的非致命弃用警告；Realms NPE 因 authlib-injector 指向 CSU Yggdrasil，属预期。

若后续真实启动 26.3（`csu-ysu-union`）出现 JVM 模块访问报错（如 `module java.base does not "opens ..."` 之类），**记录完整报错原文**到本计划的「风险与回滚」小节并转交后续阶段处理；**不得**为绕过该错误擅自加入无关改动（如 `--add-opens` 开关、降级 Java 等）。

- [x] **Step 6.7：实现者自查**

对照「最终验收清单」逐条确认。任何未通过项回到对应任务修正并重跑该任务与 Step 6.1/6.4。

---

## 任务 7：打包（README + dist\CMUmcl-step3.zip + 产物 exe）

**Files:**
- Create: `D:\新启动器\CMUmcl\README-step3.md`
- Create: `D:\新启动器\dist\CMUmcl-step3.zip`（若已存在则**拒绝覆盖**，改用 `CMUmcl-step3b.zip`）

- [x] **Step 7.1：创建 `README-step3.md`**

文件 `D:\新启动器\CMUmcl\README-step3.md`：

```markdown
# CMUmcl — 步骤三：Java 版本选择与运行时自动下载

本步骤在步骤二（原版/Fabric 启动）之上加入**按 Minecraft 版本自动选择兼容 Java** 与 **Mojang 官方运行时自动下载兜底**。

## 行为

- 需求解析：读取 `%LOCALAPPDATA%\CMUmcl\minecraft\versions\{version}\{version}.json` 的 `javaVersion.majorVersion`（必需）与 `javaVersion.component`（可选）；JSON 缺失/非法时回退集中映射（<1.17→8、1.17–1.20.4→17、1.20.5–1.21.8→21、≥1.21.9→25）。
- 本地扫描：`JAVA_HOME` 及兄弟目录、`Program Files\Java`（含 x86）、常见厂商目录、`%USERPROFILE%\.jdks`、`PATH`、注册表（尽力而为），按路径去重；主版本优先读 `release` 的 `JAVA_VERSION`，回退 `java -version`（`1.8.x → 8`）。
- 选择：精确主版本优先；否则“最低主版本 ≥ 所需”；同主版本优先 `javaw.exe`、再取完整版本号最高；无满足项绝不回退到低版本。
- 自动下载（仅本地无兼容 JDK 且存在 `component` 时）：仅 `windows-x64`；从 Mojang 官方索引按 component 取 manifest；逐文件校验 SHA-1/size；URL 主机白名单（`piston-meta.mojang.com` / `piston-data.mojang.com`）+ zip-slip 路径防护；下载到暂存目录校验通过后原子移动到 `%LOCALAPPDATA%\CMUmcl\java\runtime\{component}`；写 `.complete` 标记复用。
- 失败：任何一步失败清理暂存目录、抛中文 `LaunchException`，不留半成品；既不静默回退 Java 21。

## 新增/改动文件

- 新增 `Launch\JavaVersionResolver.cs`
- 新增 `Launch\MojangRuntimeInstaller.cs`
- 修改 `Launch\JavaLocator.cs`
- 修改 `Launch\LaunchService.cs`
- 修改 `Launch\LaunchProgress.cs`
- 修改 `Content\LauncherPaths.cs`

## 运行时目录

| 用途 | 路径 |
|------|------|
| Java 根目录 | `%LOCALAPPDATA%\CMUmcl\java` |
| 自动下载运行时 | `%LOCALAPPDATA%\CMUmcl\java\runtime\{component}` |
| 暂存目录（临时） | `%LOCALAPPDATA%\CMUmcl\java\runtime\.staging-{guid}` |

## 构建与运行

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
& "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe" build "CMUmcl\CMUmcl.csproj" -c Debug
& "CMUmcl\bin\Debug\net10.0-windows\CMUmcl.exe"
```
```

- [x] **Step 7.2：打包为 `dist\CMUmcl-step3.zip`（仅源码，遵循 step1/step2 惯例；不覆盖既有包）**

```powershell
$root = "D:\新启动器"
$dist = Join-Path $root "dist"
$src = Join-Path $root "CMUmcl"
$staging = Join-Path $dist "staging-step3"
$zip = Join-Path $dist "CMUmcl-step3.zip"

if (Test-Path -LiteralPath $zip) { throw "CMUmcl-step3.zip 已存在，拒绝覆盖，请改用 CMUmcl-step3b.zip 并更新变量 `$zip`。" }
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }

New-Item -ItemType Directory -Force -Path $staging | Out-Null
$dest = Join-Path $staging "CMUmcl"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

Get-ChildItem -LiteralPath $src -Force | Where-Object { $_.Name -notin @('bin','obj') } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $dest -Recurse -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Write-Output "created $zip"
Get-Item -LiteralPath $zip | Select-Object FullName, Length

Remove-Item -LiteralPath $staging -Recurse -Force
```

- 预期：打印 `created D:\新启动器\dist\CMUmcl-step3.zip` 及文件大小；`dist` 目录下 `CMUmcl-step1.zip`、`CMUmcl-step2.zip` 保持不变。

- [x] **Step 7.3：核对包内容包含新增文件**

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::OpenRead("D:\新启动器\dist\CMUmcl-step3.zip")
$names = $z.Entries | ForEach-Object { $_.FullName }
"JavaVersionResolver: " + [bool]($names -contains "CMUmcl/Launch/JavaVersionResolver.cs")
"MojangRuntimeInstaller: " + [bool]($names -contains "CMUmcl/Launch/MojangRuntimeInstaller.cs")
"README-step3: " + [bool]($names -contains "CMUmcl/README-step3.md")
"total entries: " + $z.Entries.Count
$z.Dispose()
```

- 预期：三项均为 `True`；`total entries` 比 step2（32）多出 3 个新增/改动文件的净变化（新增 2 个 .cs + 1 个 README = 33 项，若含其他差异以实际为准）。

- [x] **Step 7.4：实现者自查**

确认未覆盖 `CMUmcl-step1.zip`/`CMUmcl-step2.zip`；`bin`/`obj` 未被打入包；产物 exe 位于 `D:\新启动器\CMUmcl\bin\Debug\net10.0-windows\CMUmcl.exe`。

---

## 最终验收清单（映射到任务）

1. `csu-ysu-union`（26.3）选中 `jdk-26.0.2.1`（主版本 26）、不触发下载 → 任务 2 Step 2.4 / 任务 6 Step 6.1 的 `26.3 (R=25) → major 26` + `jdk-26.0.2.1 path`。
2. 1.20.1 选中最低 ≥17 的 `jdk-21`、不触发下载 → 任务 6 Step 6.1 的 `1.20.1 (R=17) → major 21` + `jdk-21 path`。
3. 所需 25 + component 存在 + 本地仅 21/22 → 自动下载 `java-runtime-epsilon`，选中 `...\java-runtime-epsilon\bin\javaw.exe`（25）→ 任务 6 Step 6.2 `TestDownloadAsync` + Step 6.3 落盘核对。
4. 所需 25 + component 缺失 + 本地仅 21/22 → 抛含 25 与 {21,22} 的 `LaunchException`，不静默回退 → 由 `LaunchService` 报错分支实现（任务 5 Step 5.3），纯逻辑由 `Select` 返回 `null`（任务 2 Step 2.4 `R=27 → null`）保证不产生低版本回退。
5. 下载中途失败 / 哈希与大小不符 / manifest 含 `..` 或根路径 → 抛清晰错误且暂存/最终目录清理 → 任务 4 Step 4.4（安全纯函数）+ 任务 6 Step 6.2（取消清理）。
6. 非 64 位 / component 不存在 / URL 非白名单主机 → 抛对应错误、不写入 → 任务 4 Step 4.4（`reject evil host` 等）+ `IsWindowsX64`/索引缺失分支（任务 4 Step 4.1 代码）。
7. `dotnet build`（net10.0-windows）成功 → 任务 6 Step 6.4。

## 风险与回滚

- **Java 25+ JVM 模块访问**：26.3 真实启动若报模块访问错误，仅记录原文并转后续阶段，不擅自加 `--add-opens` 或降级 Java（任务 6 Step 6.6）。
- **磁盘空间**：工作数据已迁移到 `D:\新启动器\CMUmclData`，`%LOCALAPPDATA%\CMUmcl` 为指向它的 junction；运行时下载实际落 D:，C: 约 2.5GB 空闲。下载测试前仍应确认空间足够；磁盘不足失败属合法磁盘错误路径。
- **回退映射边界失准**：映射仅为 JSON 缺失时的安全网，集中一处（`ResolveFallbackMajor`）并注释；JSON 可用时以 `javaVersion.majorVersion` 为准。
- **扫描覆盖差异**：多来源并集 + 去重，注册表失败不致命；未覆盖时仍会给出清晰报错或触发自动下载。
- **无 git**：回滚 = 用 `D:\新启动器\.tmp\backup-step3\*.cs` 覆盖回原路径（见前置准备 Step 0.1）。
- **无测试工程常驻**：验证用 `JavaHarness` 位于 `D:\新启动器\.tmp`（非交付物），未在 `CMUmcl` 生产代码中增加任何测试钩子；所有为可测而公开的均为真实安全/解析函数（`IsAllowedUrl`/`EnsureAllowedUri`/`SanitizeRelativePath`/`ResolveUnder`/`MatchesSha1AndSize`/`ParseJavaMajor*`）。

## 执行状态

- **Task 1 完成（2026-10-28）**：`JavaVersionResolver.cs` 已新增；CMUmcl 构建 0 错误（2 个既有 NU1510 警告）；resolver harness 12/12 PASS。临时 harness 的测试注册问题已修正，确认不是空跑。
- **工作数据迁移（2026-10-28）**：`%LOCALAPPDATA%\CMUmcl` 已迁移到 `D:\新启动器\CMUmclData` 并建立 junction；当前 C: 约 2.5GB 空闲。
- **Task 2 完成（2026-10-28）**：`JavaLocator.cs` 重写；CMUmcl 构建 0 错误；harness 31/31 PASS（含 26.3→JDK 26、1.20.1→JDK 21 本地验收）。临时 `Locate()` 兼容 shim 保留，Task 5 删除。
- **Task 3 完成（2026-10-28）**：`LauncherPaths.cs` 增加 Java/runtime/staging 路径；构建 0 错误。
- **Task 4 完成（2026-10-28）**：`MojangRuntimeInstaller.cs` 与安全测试完成；构建 0 错误；安全测试 16 PASS / 3 SKIP（大下载留给 Task 6）。
- **Task 5 完成（2026-10-28）**：`LaunchService` 在安装后选择 Java；本地优先、自动下载兜底；`LaunchProgress` 增加 Java 字段；`Locate()` 垫片已删除；构建 0 错误，纯测试全部 PASS。
- **Task 6 完成（2026-10-28）**：默认测试全 PASS；直接下载 `java-runtime-epsilon` 成功（100.2MB / 411 files，约 38.6s），`.complete` 复用与取消清理 PASS；最终构建 0 错误；exe 162,304 bytes。实际 26.3 UI 启动未执行（Step 6.6 待观察）。
- **网络观察**：本地代理 127.0.0.1:7890 偶发 TLS reset；两次失败均清理干净，重试后成功。当前下载器没有显式重试机制，社区分发前建议评估加入 3 次重试。
- **Task 7 完成（2026-10-28）**：`README-step3.md` 与 `dist\CMUmcl-step3.zip`（376,978 bytes，35 entries）已生成；step1/step2 未覆盖；exe 162,304 bytes。
- **实际启动验证（2026-10-28）**：临时 LaunchHarness 调用 `LaunchService` 启动 `csu-ysu-union`，选中 JDK 26，进程运行 >60s 并进入主菜单；无 Java 25+ 模块访问错误；仅非致命弃用警告。游戏进程已清理，无残留。
- **全部 7 个任务完成**：Java 选择、自动下载、集成、验证、打包均已通过。注意：当前 `CMUmcl.exe` 为 framework-dependent（需要 .NET 10）；直接双击在只有 .NET 8/9 的机器上会报 framework not found，分发前需评估 self-contained publish。
