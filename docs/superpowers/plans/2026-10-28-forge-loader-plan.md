# Forge 加载器安装与启动 实施计划（CMUmcl）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers-subagent-driven-development (recommended) or superpowers-executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在现有原版/Fabric 启动能力之上接入现代 Forge（`install_profile` 形式）：从 Maven 下载并 SHA-1 校验安装器、解析 `install_profile.json` 与 `version.json`、合并库、提取内嵌工件、用已选 Java 按序运行 6 个客户端 processors、落盘 `1.20.1-forge-47.4.28` 版本档案，交由 CmlLib.Core 补依赖并启动到游戏；全程 GPL-3.0-only 合规、无广告/无交互 GUI、失败自动清理并支持“跳过重装”。

**Architecture:** 新增 `CMUmcl\Launch\Forge\` 下四个职责单一组件——`ForgeModels`（DTO + `MavenArtifact` 坐标解析 + `ForgePathSafety` 路径防护）、`ForgeInstallerParser`（读两 JSON、合并库、提取内嵌工件、产出最终版本档案）、`ForgeProcessorRunner`（data map + `{KEY}`/`[coordinate]` 参数解析 + 主类读取 + outputs 校验 + 进程执行）、`ForgeLoaderInstaller`（编排：URL/SHA-1 下载校验、库安装、processor 运行、落盘、跳过判定、清理）。`LaunchService` 把 Java 选择前移到加载器解析之前，并在 `switch` 中新增 `case "forge"` 做一次薄调用。全部逻辑以 GPL-3.0-only 移植/重实现 Axolotl/YSUMCL（不复制 PCL、不引入 CmlLib.Core.Installer.Forge）。

**Tech Stack:** .NET 10（net10.0-windows，WPF），CmlLib.Core 4.1.0；新组件仅用 BCL（System.IO.Compression / System.Text.Json(.Nodes) / System.Security.Cryptography / System.Net.Http / System.Diagnostics），无新增 NuGet 包。

---

## 环境与构建约束（所有命令必须遵守）

- **dotnet 路径**：`C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe`（系统 dotnet 为 9，项目需 .NET 10；SDK 10.0.403 已装）。
- **数据已迁到 D:**：`%LOCALAPPDATA%\CMUmcl` 是到 `D:\新启动器\CMUmclData` 的目录联接；因此 `minecraft`（`versions`/`libraries`）实际落盘在 D:。C: 当前约 2.5GB 空闲，Forge 库/内嵌工件/暂存全部走 D:。
- **每条 `dotnet` 命令前设置环境变量**（PowerShell 5.1，逐条设置，变量不跨调用保留）：

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
```

- **构建命令模板**（预期末尾含 `生成成功。`/`Build succeeded` 且 `0 Error(s)`，允许既有 2 个 NU1510 警告）：

```powershell
& $dotnet build "D:\新启动器\CMUmcl\CMUmcl.csproj" -c Debug
```

- **测试工程**：`D:\新启动器\.tmp\ForgeHarness`（产品源码树之外，非交付物）。运行：

```powershell
& $dotnet run --project "D:\新启动器\.tmp\ForgeHarness\ForgeHarness.csproj"                 # 纯函数测试（默认，无网络）
& $dotnet run --project "D:\新启动器\.tmp\ForgeHarness\ForgeHarness.csproj" -- --install     # 真实 Forge 安装 + 跳过/损坏修复
& $dotnet run --project "D:\新启动器\.tmp\ForgeHarness\ForgeHarness.csproj" -- --launch      # 端到端 LaunchService 启动
```

- **网络**：Forge Maven / Mojang / Minecraft 下载依赖系统代理（HttpClientHandler 默认）；若直连失败，先确认本机代理 `127.0.0.1:7890` 可用。git 推送用显式代理（见任务 8）。

## 文件结构

| 文件 | 动作 | 职责 |
| --- | --- | --- |
| `LICENSE`（仓库根） | 新增 | GNU GPL-3.0 全文（从本地 Axolotl 副本复制） |
| `NOTICE`（仓库根） | 新增 | Axolotl/YSUMCL 移植归属 + 第三方许可 + “分发需附源码”声明 |
| `.gitignore`（仓库根） | 修改 | 放行根级 `LICENSE`/`NOTICE`（当前 `/*` 规则会忽略它们） |
| `CMUmcl\README-step4.md` | 新增 | 步骤四说明（含许可与合规说明 = README notice） |
| `CMUmcl\Launch\Forge\ForgeModels.cs` | 新增 | DTO + `MavenArtifact` + `ForgePathSafety` |
| `CMUmcl\Launch\Forge\ForgeInstallerParser.cs` | 新增 | `parse_installer` 移植 |
| `CMUmcl\Launch\Forge\ForgeProcessorRunner.cs` | 新增 | processor 执行 + data map + 参数/主类解析 + outputs 校验 |
| `CMUmcl\Launch\Forge\ForgeLoaderInstaller.cs` | 新增 | 编排 + 下载校验 + 跳过判定 + 清理 |
| `CMUmcl\Launch\LaunchService.cs` | 修改 | Java 选择前移 + `case "forge"` |
| `CMUmcl\PackCardViewModel.cs` | 修改 | `forge` 从“不支持”提示中移除 |
| `D:\新启动器\.tmp\ForgeHarness\*` | 新增（临时） | 验证用控制台工程（不进入打包） |

---

## 前置准备（开始前执行一次）

- [ ] **Step 0.1：创建临时目录并备份将被修改的 3 个源文件**

```powershell
New-Item -ItemType Directory -Force -Path "D:\新启动器\.tmp","D:\新启动器\.dotnet-home","D:\新启动器\.nuget\packages" | Out-Null
$bk = "D:\新启动器\.tmp\backup-step4"
New-Item -ItemType Directory -Force -Path $bk | Out-Null
Copy-Item -LiteralPath "D:\新启动器\CMUmcl\Launch\LaunchService.cs" -Destination $bk
Copy-Item -LiteralPath "D:\新启动器\CMUmcl\PackCardViewModel.cs" -Destination $bk
Copy-Item -LiteralPath "D:\新启动器\.gitignore" -Destination $bk
Get-ChildItem -LiteralPath $bk | Select-Object Name
```

- 预期：打印 `LaunchService.cs`、`PackCardViewModel.cs`、`.gitignore` 三个文件名。回滚方式：将 `D:\新启动器\.tmp\backup-step4\*` 覆盖回原路径即可。

- [ ] **Step 0.2：确认 dotnet 与 SDK 可用**

```powershell
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet --list-sdks
```

- 预期：包含 `10.0.403 [C:\Users\PC\AppData\Local\Microsoft\dotnet\sdk]`。

---

## 任务 1：许可证与合规（LICENSE / NOTICE / .gitignore / README / SPDX 头）

**Files:**
- Create: `D:\新启动器\LICENSE`
- Create: `D:\新启动器\NOTICE`
- Modify: `D:\新启动器\.gitignore`
- Create: `D:\新启动器\CMUmcl\README-step4.md`

- [ ] **Step 1.1：创建根级 `LICENSE`（复制 GPL-3.0 全文）**

本地已有 Axolotl（GPL-3.0-only）的规范 LICENSE 副本，直接复制：

```powershell
Copy-Item -LiteralPath "D:\新启动器\Axolotl\apps\app\LICENSE" -Destination "D:\新启动器\LICENSE"
Get-Content -LiteralPath "D:\新启动器\LICENSE" -TotalCount 6
(Get-Content -LiteralPath "D:\新启动器\LICENSE").Count
```

- 预期：前 6 行以 `GNU GENERAL PUBLIC LICENSE` / `Version 3, 29 June 2007` 开头；行数约 `674`。若本地副本缺失，改用 `Invoke-WebRequest -Uri "https://www.gnu.org/licenses/gpl-3.0.txt" -OutFile "D:\新启动器\LICENSE"` 后重新校验前 6 行与行数。

- [ ] **Step 1.2：创建根级 `NOTICE`**

文件 `D:\新启动器\NOTICE`，完整内容：

```text
# NOTICE — CMUmcl

本项目（CMUmcl）整体采用 GNU General Public License v3.0（SPDX: GPL-3.0-only）许可，
包括既有代码与本项目对 Axolotl Launcher（YSUMCL fork）Forge 安装器逻辑的移植/重实现代码。
完整许可文本见同目录 LICENSE 文件。

## Forge 安装器逻辑的移植/重实现来源

本项目的 Forge 安装器逻辑（CMUmcl\Launch\Forge\ 目录下的 ForgeModels.cs、
ForgeInstallerParser.cs、ForgeProcessorRunner.cs、ForgeLoaderInstaller.cs）以行为/结构参照方式
移植/重实现自 Axolotl Launcher（YSUMCL fork）。该项目采用 GPL-3.0-only 许可，版权归其上游作者所有。

参照的文件：
- packages/app-lib/src/api/loader_metadata.rs（InstallerProfile / Processor / SidedDataEntry 结构与 parse_installer）
- packages/app-lib/src/launcher/mod.rs（processors 执行与 data map 构建）
- packages/app-lib/src/launcher/args.rs（get_processor_arguments / get_processor_main_class）
- packages/daedalus/src/modded.rs（PartialVersionInfo / Processor / SidedDataEntry）
- packages/daedalus/src/lib.rs（get_path_from_artifact）

未复制任何 Plain Craft Launcher（PCL）代码、字符串或品牌；PCL-CE 核心采用
PCL Limited Distribution License，禁止纳入本项目。

## 第三方依赖许可

以下第三方组件保留各自上游许可，不受本项目整体 GPL-3.0-only 许可影响，仅在其自身许可下随包分发：
- CmlLib.Core（MIT License）
- authlib-injector.jar（随包二进制，按其上游许可分发）
- 各 NuGet 依赖（按其各自许可分发）

## 分发义务（GPL-3.0 第 6 条）

开发期间本仓库可保持私有；但一旦向他人分发本程序的二进制（目标码），必须同时以
GPL-3.0-only 向接收者提供对应的完整源码，并随包附带本 LICENSE 与 NOTICE 文件。
```

- [ ] **Step 1.3：修改 `.gitignore` 放行根级 `LICENSE`/`NOTICE`**

用编辑工具将以下旧文本：

```text
# Focused repository: only the CMUmcl client source and docs are tracked.
/*
!/.gitignore
!/CMUmcl/
!/docs/
```

替换为：

```text
# Focused repository: only the CMUmcl client source and docs are tracked.
/*
!/.gitignore
!/LICENSE
!/NOTICE
!/CMUmcl/
!/docs/
```

- 预期：编辑成功（`oldString` 唯一命中）。这保证根级 `LICENSE`/`NOTICE` 不再被 `/*` 规则忽略。

- [ ] **Step 1.4：创建 `CMUmcl\README-step4.md`（含 README notice）**

文件 `D:\新启动器\CMUmcl\README-step4.md`，完整内容：

```markdown
# CMUmcl — 步骤四：Forge 加载器安装与启动

本步骤在步骤三（Java 选择 + 运行时自动下载）之上加入现代 Forge（`install_profile` 形式）的安装与启动，
验证实例为本地合成的 `forge-test`（Minecraft 1.20.1 + Forge 47.4.28，无 mods，revision 1）。

## 许可与合规（重要）

- 本项目整体采用 **GPL-3.0-only** 许可（见仓库根 `LICENSE`）。本步骤的 Forge 安装器逻辑移植/重实现自
  Axolotl Launcher（YSUMCL fork），来源文件与第三方依赖许可详见仓库根 `NOTICE`。
- 开发期间仓库可保持私有；但分发二进制时必须同时提供对应源码，并随包附带 `LICENSE` 与 `NOTICE`
  （GPL-3.0 第 6 条）。
- 不引入任何 PCL 代码、不引入 CmlLib.Core.Installer.Forge、不显示 Forge 官方下载页广告、无交互式安装 GUI。

## 行为

- 解析：从 `https://maven.minecraftforge.net/net/minecraftforge/forge/{mc}-{forge}/forge-{mc}-{forge}-installer.jar`
  下载安装器，并用同路径 `.sha1` 校验（主机白名单 + 流式 SHA-1/size 校验 + 暂存 + 失败清理）。
- 解析器：读 `install_profile.json` 与 `version.json`，合并 `version.json.libraries ∪ install_profile.libraries`
  （按坐标去重，补上 `fmlcore`/`javafmllanguage`/`lowcodelanguage`/`mclanguage`/`forge:universal`），
  提取 `data` 中以 `/` 开头的内嵌工件到暂存目录并把值改写为绝对路径，强制版本 id，丢弃 `data`/`processors` 字段。
- 处理器：客户端注入 `SIDE/MINECRAFT_JAR/MINECRAFT_VERSION/ROOT/INSTALLER/LIBRARY_DIR` + `install_profile.data`，
  按序执行、按 `sides` 过滤（1.20.1 跑 6 个、跳 4 个），命令 `java -cp <classpath+jar> <Main-Class-from-manifest> <args>`
  （工作目录为 Minecraft 目录），校验退出码与 `outputs` SHA-1。
- 跳过重装：仅当版本档案 `inheritsFrom`、`.forge-complete`（40 位 hex）、合并库文件存在且 SHA-1、processors outputs
  全部满足时才跳过，绝不只信目录名。
- Java：沿用基础版本 1.20.1 的 `javaVersion.majorVersion = 17`（本机选择器取最低 ≥17 即 jdk-21）；processors 用控制台
  `java.exe`，启动游戏仍用 `javaw.exe`。

## 新增/改动文件

- 新增 `Launch\Forge\ForgeModels.cs` / `ForgeInstallerParser.cs` / `ForgeProcessorRunner.cs` / `ForgeLoaderInstaller.cs`
- 修改 `Launch\LaunchService.cs`（Java 选择前移 + `case "forge"`）
- 修改 `PackCardViewModel.cs`（`forge` 从“暂不支持”提示中移除）
- 新增仓库根 `LICENSE` / `NOTICE`

## 构建与验证

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
& "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe" build "D:\新启动器\CMUmcl\CMUmcl.csproj" -c Debug
```
```

- [ ] **Step 1.5：验证 .gitignore 放行生效**

```powershell
cd "D:\新启动器"
git check-ignore LICENSE NOTICE
git status --short -- LICENSE NOTICE .gitignore
```

- 预期：`git check-ignore` 无输出（表示未被忽略）；`git status` 显示 `?? LICENSE`、`?? NOTICE`、` M .gitignore`（`.gitignore` 为修改）。

- [ ] **Step 1.6：实现者自查**

确认 `LICENSE` 为 GPL-3.0 全文（非空、行数约 674）、`NOTICE` 含 Axolotl 移植归属与“分发需附源码”、`.gitignore` 放行了 `LICENSE`/`NOTICE` 且未放行其它根级文件（`admin-credentials.txt`、`PCL-CE` 等仍被忽略）。若 `git check-ignore` 意外输出 LICENSE/NOTICE，修正 `.gitignore` 后重跑 Step 1.5。

---

## 任务 2：ForgeModels + MavenArtifact + 纯函数测试

**Files:**
- Create: `D:\新启动器\CMUmcl\Launch\Forge\ForgeModels.cs`
- Create: `D:\新启动器\.tmp\ForgeHarness\ForgeHarness.csproj`
- Create: `D:\新启动器\.tmp\ForgeHarness\Program.cs`
- Create: `D:\新启动器\.tmp\ForgeHarness\State.cs`
- Create: `D:\新启动器\.tmp\ForgeHarness\TestRegistry.cs`
- Create: `D:\新启动器\.tmp\ForgeHarness\TestsModels.cs`

- [ ] **Step 2.1：创建 `ForgeModels.cs`**

文件 `D:\新启动器\CMUmcl\Launch\Forge\ForgeModels.cs`，完整内容：

```csharp
// SPDX-License-Identifier: GPL-3.0-only
// 本文件移植/重实现自 Axolotl Launcher（YSUMCL fork）的 Forge 安装器逻辑
//（packages/daedalus/src/lib.rs 的 get_path_from_artifact 与
//   packages/daedalus/src/modded.rs 的 InstallerProfile/Processor/SidedDataEntry 结构，
//   GPL-3.0-only）。详见仓库根 LICENSE 与 NOTICE。

using System;
using System.Collections.Generic;
using System.IO;

namespace CMUmcl.Launch.Forge
{
    /// <summary>install_profile.json 的顶层结构（Forge 现代安装器）。</summary>
    public sealed class InstallerProfile
    {
        public string? Minecraft { get; set; }
        public Dictionary<string, SidedDataEntry> Data { get; set; } = new();
        public List<ForgeProcessor> Processors { get; set; } = new();
        public List<ForgeLibrary> Libraries { get; set; } = new();
    }

    /// <summary>data 映射中按安装端区分的值。</summary>
    public sealed class SidedDataEntry
    {
        public string Client { get; set; } = string.Empty;
        public string Server { get; set; } = string.Empty;
    }

    /// <summary>一个 Forge 处理器（按序执行、按 sides 过滤）。</summary>
    public sealed class ForgeProcessor
    {
        public string Jar { get; set; } = string.Empty;
        public List<string> Classpath { get; set; } = new();
        public List<string> Args { get; set; } = new();
        public Dictionary<string, string>? Outputs { get; set; }
        public List<string>? Sides { get; set; }
    }

    /// <summary>一个库（仅保留 name 与 downloads.artifact，用于下载与校验）。</summary>
    public sealed class ForgeLibrary
    {
        public string Name { get; set; } = string.Empty;
        public ForgeLibraryDownloads? Downloads { get; set; }
    }

    public sealed class ForgeLibraryDownloads
    {
        public ForgeLibraryDownload? Artifact { get; set; }
    }

    public sealed class ForgeLibraryDownload
    {
        public string? Path { get; set; }
        public string Url { get; set; } = string.Empty;
        public string? Sha1 { get; set; }
        public long Size { get; set; }
    }

    /// <summary>解析后的 Maven 坐标。</summary>
    public sealed record MavenCoordinate(string Group, string Artifact, string Version, string? Classifier, string Extension);

    /// <summary>
    /// 把 <c>group:artifact:version[:classifier][@ext]</c> 解析为库相对路径
    ///（移植自 daedalus 的 get_path_from_artifact）。
    /// </summary>
    public static class MavenArtifact
    {
        public static MavenCoordinate Parse(string coordinate)
        {
            if (string.IsNullOrWhiteSpace(coordinate))
                throw new LaunchException($"无法解析 Maven 坐标：{coordinate}");

            string[] parts = coordinate.Split(':');
            if (parts.Length < 3 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
                throw new LaunchException($"无法解析 Maven 坐标：{coordinate}");

            string group = parts[0];
            string artifact = parts[1];

            if (parts.Length == 3)
            {
                string[] ve = parts[2].Split('@');
                return new MavenCoordinate(group, artifact, ve[0], null, ve.Length > 1 ? ve[1] : "jar");
            }

            string[] de = parts[3].Split('@');
            return new MavenCoordinate(group, artifact, parts[2], de[0], de.Length > 1 ? de[1] : "jar");
        }

        public static string ToRelativePath(string coordinate)
        {
            MavenCoordinate c = Parse(coordinate);
            string dir = c.Group.Replace('.', '/');
            string file = string.IsNullOrEmpty(c.Classifier)
                ? $"{c.Artifact}-{c.Version}.{c.Extension}"
                : $"{c.Artifact}-{c.Version}-{c.Classifier}.{c.Extension}";
            return $"{dir}/{c.Artifact}/{c.Version}/{file}";
        }
    }

    /// <summary>Forge 安装器内路径的 zip-slip 防护（与 MojangRuntimeInstaller 同语义）。</summary>
    public static class ForgePathSafety
    {
        public static string SanitizeRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new LaunchException("检测到 Forge 安装器内非法路径：空路径。");

            string normalized = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

            if (Path.IsPathRooted(normalized))
                throw new LaunchException($"检测到 Forge 安装器内非法路径：{path}");

            foreach (string segment in normalized.Split(Path.DirectorySeparatorChar))
            {
                if (segment == ".." || segment == ".")
                    throw new LaunchException($"检测到 Forge 安装器内非法路径：{path}");
                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new LaunchException($"检测到 Forge 安装器内非法路径：{path}");
            }

            return normalized;
        }

        public static string ResolveUnder(string baseDir, string relativePath)
        {
            string fullBase = Path.GetFullPath(baseDir);
            string fullTarget = Path.GetFullPath(Path.Combine(fullBase, relativePath));

            string baseWithSep = fullBase.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? fullBase
                : fullBase + Path.DirectorySeparatorChar;

            if (!fullTarget.StartsWith(baseWithSep, StringComparison.OrdinalIgnoreCase))
                throw new LaunchException($"检测到 Forge 安装器内非法路径：{relativePath}");

            return fullTarget;
        }
    }
}
```

- [ ] **Step 2.2：创建验证工程 `ForgeHarness.csproj`**

```powershell
New-Item -ItemType Directory -Force -Path "D:\新启动器\.tmp\ForgeHarness" | Out-Null
```

文件 `D:\新启动器\.tmp\ForgeHarness\ForgeHarness.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>ForgeHarness</RootNamespace>
    <AssemblyName>ForgeHarness</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\CMUmcl\CMUmcl.csproj" />
    <FrameworkReference Include="Microsoft.WindowsDesktop.App" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2.3：创建 `Program.cs` / `State.cs` / `TestRegistry.cs`（只写一次）**

文件 `D:\新启动器\.tmp\ForgeHarness\Program.cs`：

```csharp
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace ForgeHarness
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("== ForgeHarness ==");

            var state = new State
            {
                Install = args.Contains("--install", StringComparer.OrdinalIgnoreCase),
                Launch = args.Contains("--launch", StringComparer.OrdinalIgnoreCase),
            };

            foreach (Type t in typeof(Program).Assembly.GetTypes()
                         .Where(t => t.Name.StartsWith("Tests", StringComparison.Ordinal)))
            {
                RuntimeHelpers.RunClassConstructor(t.TypeHandle);
            }

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

文件 `D:\新启动器\.tmp\ForgeHarness\State.cs`：

```csharp
using System;

namespace ForgeHarness
{
    internal sealed class State
    {
        public bool Install { get; set; }
        public bool Launch { get; set; }
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

文件 `D:\新启动器\.tmp\ForgeHarness\TestRegistry.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ForgeHarness
{
    internal static class TestRegistry
    {
        public static readonly List<Func<State, Task>> Tests = new();
    }
}
```

- [ ] **Step 2.4：创建 `TestsModels.cs`（任务 2 的纯函数测试）**

文件 `D:\新启动器\.tmp\ForgeHarness\TestsModels.cs`：

```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using CMUmcl.Launch;
using CMUmcl.Launch.Forge;

namespace ForgeHarness
{
    internal static class TestsModels
    {
        static TestsModels()
        {
            TestRegistry.Tests.Add(TestMavenArtifact);
            TestRegistry.Tests.Add(TestForgePathSafety);
        }

        private static Task TestMavenArtifact(State s)
        {
            Console.WriteLine("-- MavenArtifact.ToRelativePath --");
            s.Check("forge universal → jar",
                MavenArtifact.ToRelativePath("net.minecraftforge:forge:1.20.1-47.4.28:universal")
                == "net/minecraftforge/forge/1.20.1-47.4.28/forge-1.20.1-47.4.28-universal.jar");
            s.Check("zip ext",
                MavenArtifact.ToRelativePath("de.oceanlabs.mcp:mcp_config:1.20.1-20230612.114412@zip")
                == "de/oceanlabs/mcp/mcp_config/1.20.1-20230612.114412/mcp_config-1.20.1-20230612.114412.zip");
            s.Check("no classifier no ext → jar",
                MavenArtifact.ToRelativePath("org.ow2.asm:asm:9.9.1")
                == "org/ow2/asm/asm/9.9.1/asm-9.9.1.jar");
            s.Check("classifier no ext → jar",
                MavenArtifact.ToRelativePath("net.minecraftforge:installertools:1.3.4:fatjar")
                == "net/minecraftforge/installertools/1.3.4/installertools-1.3.4-fatjar.jar");
            return Task.CompletedTask;
        }

        private static Task TestForgePathSafety(State s)
        {
            Console.WriteLine("-- ForgePathSafety --");
            s.Check("sanitize ok",
                ForgePathSafety.SanitizeRelativePath("data/client.lzma") == "data" + Path.DirectorySeparatorChar + "client.lzma");
            s.Check(".. rejected", !Try(() => ForgePathSafety.SanitizeRelativePath("../evil")));
            s.Check("rooted rejected", !Try(() => ForgePathSafety.SanitizeRelativePath("C:\\evil")));
            s.Check("empty rejected", !Try(() => ForgePathSafety.SanitizeRelativePath("")));

            string baseDir = Path.Combine(Path.GetTempPath(), "forge-base-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(baseDir);
            string ok = ForgePathSafety.ResolveUnder(baseDir, "a" + Path.DirectorySeparatorChar + "b.jar");
            s.Check("resolve under ok",
                ok.StartsWith(Path.GetFullPath(baseDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            s.Check("resolve escape rejected",
                !Try(() => ForgePathSafety.ResolveUnder(baseDir, ".." + Path.DirectorySeparatorChar + "evil")));
            Directory.Delete(baseDir, recursive: true);
            return Task.CompletedTask;
        }

        private static bool Try(Action a)
        {
            try { a(); return true; }
            catch (LaunchException) { return false; }
        }
    }
}
```

- [ ] **Step 2.5：构建 CMUmcl（验证新文件可编译）**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet build "D:\新启动器\CMUmcl\CMUmcl.csproj" -c Debug
```

- 预期：末尾含 `生成成功。`/`Build succeeded` 且 `0 Error(s)`。

- [ ] **Step 2.6：运行任务 2 的纯函数测试**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet run --project "D:\新启动器\.tmp\ForgeHarness\ForgeHarness.csproj"
```

- 预期：所有行以 `PASS ` 开头，末尾 `== ALL TESTS PASSED ==`。

- [ ] **Step 2.7：提交（仅本地，不推送）**

```powershell
cd "D:\新启动器"
git add "CMUmcl\Launch\Forge\ForgeModels.cs"
git commit -m "feat(forge): add Forge models and Maven artifact path parsing"
```

- 预期：`git log --oneline -1` 显示该提交信息。

---

## 任务 3：ForgeInstallerParser（读两 JSON、合并库、内嵌工件、最终版本档案）

**Files:**
- Create: `D:\新启动器\CMUmcl\Launch\Forge\ForgeInstallerParser.cs`
- Create: `D:\新启动器\.tmp\ForgeHarness\TestsParser.cs`

- [ ] **Step 3.1：下载真实 Forge 安装器 fixture（一次性，供解析测试，不提交）**

```powershell
New-Item -ItemType Directory -Force -Path "D:\新启动器\.tmp\forge-fixture" | Out-Null
$jar = "D:\新启动器\.tmp\forge-fixture\forge-1.20.1-47.4.28-installer.jar"
Invoke-WebRequest -Uri "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.4.28/forge-1.20.1-47.4.28-installer.jar" -OutFile $jar
$sha1 = Invoke-WebRequest -Uri "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.4.28/forge-1.20.1-47.4.28-installer.jar.sha1" -UseBasicParsing
$expected = ($sha1.Content -split '\s')[0].ToLowerInvariant()
$actual = (Get-FileHash -Algorithm SHA1 -LiteralPath $jar).Hash.ToLowerInvariant()
"expected=$expected"
"actual=$actual"
if ($expected -ne $actual) { throw "fixture sha1 mismatch" }
Get-Item -LiteralPath $jar | Select-Object Name, Length
```

- 预期：`expected=` 与 `actual=` 相同（40 位 hex）；`Length` 为 `8859760`。若直连超时，加代理重试：`Invoke-WebRequest ... -Proxy "http://127.0.0.1:7890"`。

- [ ] **Step 3.2：创建 `ForgeInstallerParser.cs`**

文件 `D:\新启动器\CMUmcl\Launch\Forge\ForgeInstallerParser.cs`，完整内容：

```csharp
// SPDX-License-Identifier: GPL-3.0-only
// 本文件移植/重实现自 Axolotl Launcher（YSUMCL fork）的 Forge 安装器逻辑
//（packages/app-lib/src/api/loader_metadata.rs 的 parse_installer，
//   GPL-3.0-only）。详见仓库根 LICENSE 与 NOTICE。

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CMUmcl.Launch.Forge
{
    /// <summary>解析结果。</summary>
    public sealed class ParsedForgeInstaller
    {
        public string VersionId { get; init; } = string.Empty;
        public string InheritsFrom { get; init; } = string.Empty;
        public List<ForgeLibrary> MergedLibraries { get; init; } = new();
        public List<ForgeLibrary> InstallerLibraries { get; init; } = new();
        public List<ForgeProcessor> Processors { get; init; } = new();
        public Dictionary<string, SidedDataEntry> Data { get; init; } = new();
        public List<EmbeddedArtifact> Embedded { get; init; } = new();
        public JsonObject FinalVersionDocument { get; init; } = new();
        public string InstallProfileJson { get; init; } = string.Empty;
    }

    /// <summary>从安装器 jar 提取的内嵌工件。</summary>
    public sealed class EmbeddedArtifact
    {
        public string ArchivePath { get; init; } = string.Empty;
        public string TargetPath { get; init; } = string.Empty;
    }

    /// <summary>
    /// 从安装器 jar 读取 install_profile.json 与 version.json，合并库、提取内嵌工件、
    /// 产出最终版本档案文档（纯逻辑，输入字节数组 + 暂存目录）。
    /// </summary>
    public static class ForgeInstallerParser
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        public static ParsedForgeInstaller Parse(byte[] installerJarBytes, string stagingDir)
        {
            using var ms = new MemoryStream(installerJarBytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);

            string profileJson = ReadZipEntryText(zip, "install_profile.json");
            JsonNode? profileNode = JsonNode.Parse(profileJson);
            if (profileNode is not JsonObject profileObj)
                throw new LaunchException("解析 Forge 安装器失败（install_profile.json 格式无效）。当前版本仅支持 Forge 1.20.1 现代安装器。");

            InstallerProfile profile = JsonSerializer.Deserialize<InstallerProfile>(profileJson, JsonOptions)
                ?? new InstallerProfile();

            // 读取 version.json（缺失时读 json 字段；内联 versionInfo/install → 旧版错误）
            string versionJson = ReadVersionJson(zip, profileObj);
            JsonNode? versionNode = JsonNode.Parse(versionJson);
            if (versionNode is not JsonObject versionObj)
                throw new LaunchException("解析 Forge 安装器失败（version.json 格式无效）。当前版本仅支持 Forge 1.20.1 现代安装器。");

            // inheritsFrom 回填
            if (versionObj["inheritsFrom"] is null)
            {
                if (string.IsNullOrWhiteSpace(profile.Minecraft))
                    throw new LaunchException("解析 Forge 安装器失败（inheritsFrom 缺失且无 minecraft 版本）。当前版本仅支持 Forge 1.20.1 现代安装器。");
                versionObj["inheritsFrom"] = profile.Minecraft;
            }
            string inheritsFrom = versionObj["inheritsFrom"]?.GetValue<string>() ?? string.Empty;

            // version.json 库（typed，仅取 name + downloads.artifact，用于校验）
            var versionLibraries = new List<ForgeLibrary>();
            if (versionObj["libraries"] is JsonArray varr)
                versionLibraries = JsonSerializer.Deserialize<List<ForgeLibrary>>(varr.ToJsonString(), JsonOptions) ?? new List<ForgeLibrary>();

            var versionNames = new HashSet<string>(versionLibraries.Select(l => l.Name), StringComparer.Ordinal);

            // 合并（version 优先，按 name 去重）
            var merged = new List<ForgeLibrary>(versionLibraries);
            var mergedNames = new HashSet<string>(versionNames, StringComparer.Ordinal);
            foreach (ForgeLibrary lib in profile.Libraries)
                if (mergedNames.Add(lib.Name))
                    merged.Add(lib);

            // 提取内嵌工件（data 值以 '/' 开头），并把 client 值改写为绝对路径
            var data = new Dictionary<string, SidedDataEntry>(profile.Data);
            var embedded = new List<EmbeddedArtifact>();
            foreach (var kv in data)
            {
                if (!kv.Value.Client.StartsWith("/", StringComparison.Ordinal))
                    continue;

                string archivePath = kv.Value.Client.TrimStart('/');
                byte[] bytes = ReadZipEntryBytes(zip, archivePath);
                string rel = ForgePathSafety.SanitizeRelativePath(archivePath);
                string target = ForgePathSafety.ResolveUnder(stagingDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
                kv.Value.Client = target;
                embedded.Add(new EmbeddedArtifact { ArchivePath = "/" + archivePath, TargetPath = target });
            }

            // 最终版本档案文档：verbatim version.json 的库 + 序列化的 install_profile 库
            var mergedArray = new JsonArray();
            if (versionObj["libraries"] is JsonArray originalArray)
                foreach (JsonNode? item in originalArray)
                    if (item != null)
                        mergedArray.Add(item.DeepClone());
            foreach (ForgeLibrary lib in profile.Libraries)
                if (!versionNames.Contains(lib.Name))
                    mergedArray.Add(LibraryToJson(lib));

            versionObj["libraries"] = mergedArray;
            versionObj.Remove("data");
            versionObj.Remove("processors");

            return new ParsedForgeInstaller
            {
                VersionId = versionObj["id"]?.GetValue<string>() ?? string.Empty,
                InheritsFrom = inheritsFrom,
                MergedLibraries = merged,
                InstallerLibraries = profile.Libraries,
                Processors = profile.Processors,
                Data = data,
                Embedded = embedded,
                FinalVersionDocument = versionObj,
                InstallProfileJson = profileJson,
            };
        }

        private static JsonObject LibraryToJson(ForgeLibrary lib)
        {
            var artifact = new JsonObject();
            ForgeLibraryDownload? a = lib.Downloads?.Artifact;
            if (!string.IsNullOrWhiteSpace(a?.Path)) artifact["path"] = a.Path;
            if (!string.IsNullOrWhiteSpace(a?.Url)) artifact["url"] = a.Url;
            if (!string.IsNullOrWhiteSpace(a?.Sha1)) artifact["sha1"] = a.Sha1;
            if (a != null && a.Size > 0) artifact["size"] = a.Size;

            var obj = new JsonObject { ["name"] = lib.Name };
            if (artifact.Count > 0)
                obj["downloads"] = new JsonObject { ["artifact"] = artifact };
            return obj;
        }

        private static string ReadVersionJson(ZipArchive zip, JsonObject profileObj)
        {
            if (zip.GetEntry("version.json") != null)
                return ReadZipEntryText(zip, "version.json");

            string? jsonField = profileObj["json"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(jsonField))
                return ReadZipEntryText(zip, jsonField.TrimStart('/'));

            if (profileObj.ContainsKey("versionInfo") || profileObj.ContainsKey("install"))
                throw new LaunchException("暂不支持该 Forge 版本的旧版安装流程，请使用 1.20.1 或更新版本。");

            throw new LaunchException("解析 Forge 安装器失败（缺少 version.json）。当前版本仅支持 Forge 1.20.1 现代安装器。");
        }

        private static string ReadZipEntryText(ZipArchive zip, string path)
            => Encoding.UTF8.GetString(ReadZipEntryBytes(zip, path));

        private static byte[] ReadZipEntryBytes(ZipArchive zip, string path)
        {
            ZipArchiveEntry entry = zip.GetEntry(path)
                ?? throw new LaunchException($"解析 Forge 安装器失败（缺少 {path}）。当前版本仅支持 Forge 1.20.1 现代安装器。");
            using Stream s = entry.Open();
            using var buffer = new MemoryStream();
            s.CopyTo(buffer);
            return buffer.ToArray();
        }
    }
}
```

- [ ] **Step 3.3：创建 `TestsParser.cs`**

文件 `D:\新启动器\.tmp\ForgeHarness\TestsParser.cs`：

```csharp
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CMUmcl.Launch.Forge;

namespace ForgeHarness
{
    internal static class TestsParser
    {
        static TestsParser()
        {
            TestRegistry.Tests.Add(TestParseRealInstaller);
        }

        private static Task TestParseRealInstaller(State s)
        {
            Console.WriteLine("-- ForgeInstallerParser (real 1.20.1 fixture) --");
            string fixture = Path.Combine(@"D:\新启动器\.tmp\forge-fixture", "forge-1.20.1-47.4.28-installer.jar");
            if (!File.Exists(fixture))
            {
                Console.WriteLine("SKIP: fixture missing (run Step 3.1 first)");
                return Task.CompletedTask;
            }

            string staging = Path.Combine(Path.GetTempPath(), "forge-parse-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                ParsedForgeInstaller parsed = ForgeInstallerParser.Parse(File.ReadAllBytes(fixture), staging);

                s.Check("version id", parsed.VersionId == "1.20.1-forge-47.4.28");
                s.Check("inheritsFrom", parsed.InheritsFrom == "1.20.1");

                var names = parsed.MergedLibraries.Select(l => l.Name).ToHashSet(StringComparer.Ordinal);
                s.Check("merged has forge universal", names.Contains("net.minecraftforge:forge:1.20.1-47.4.28:universal"));
                s.Check("merged has fmlcore", names.Any(n => n.StartsWith("net.minecraftforge:fmlcore:", StringComparison.Ordinal)));
                s.Check("merged has fmlloader", names.Any(n => n.StartsWith("net.minecraftforge:fmlloader:", StringComparison.Ordinal)));

                s.Check("10 processors total", parsed.Processors.Count == 10);
                s.Check("6 client processors", parsed.Processors.Count(ForgeProcessorRunner.ShouldRun) == 6);

                s.Check("BINPATCH.client rewritten to absolute path",
                    parsed.Data.TryGetValue("BINPATCH", out var be)
                    && Path.IsPathRooted(be.Client)
                    && File.Exists(be.Client));

                s.Check("final doc has libraries",
                    parsed.FinalVersionDocument["libraries"] is JsonArray arr && arr.Count > 0);
                s.Check("final doc drops data/processors",
                    parsed.FinalVersionDocument["data"] == null && parsed.FinalVersionDocument["processors"] == null);
            }
            finally
            {
                try { Directory.Delete(staging, recursive: true); } catch { }
            }
            return Task.CompletedTask;
        }
    }
}
```

- [ ] **Step 3.4：构建 CMUmcl**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet build "D:\新启动器\CMUmcl\CMUmcl.csproj" -c Debug
```

- 预期：`0 Error(s)`。

- [ ] **Step 3.5：运行测试（含解析用例）**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet run --project "D:\新启动器\.tmp\ForgeHarness\ForgeHarness.csproj"
```

- 预期：`version id`、`inheritsFrom`、`merged has forge universal`、`merged has fmlcore`、`merged has fmlloader`、`10 processors total`、`6 client processors`、`BINPATCH.client rewritten to absolute path`、`final doc has libraries`、`final doc drops data/processors` 全部 `PASS`；末尾 `== ALL TESTS PASSED ==`。

- [ ] **Step 3.6：实现者自查**

确认合并结果含 `net.minecraftforge:forge:1.20.1-47.4.28:universal` 与 `fmlcore`/`fmlloader`（来自 install_profile.libraries，version.json.libraries 缺它们）；`BINPATCH.client` 被改写为暂存目录内的绝对路径且文件确实存在；最终文档丢弃 `data`/`processors`、保留 `libraries`。若 `6 client processors` 不是 6，核对真实 install_profile 的 `sides` 过滤逻辑（`ForgeProcessorRunner.ShouldRun`）。

---

## 任务 4：ForgeProcessorRunner（data map、参数解析、主类、outputs、进程执行）

**Files:**
- Create: `D:\新启动器\CMUmcl\Launch\Forge\ForgeProcessorRunner.cs`
- Create: `D:\新启动器\.tmp\ForgeHarness\TestsProcessor.cs`

- [ ] **Step 4.1：创建 `ForgeProcessorRunner.cs`**

文件 `D:\新启动器\CMUmcl\Launch\Forge\ForgeProcessorRunner.cs`，完整内容：

```csharp
// SPDX-License-Identifier: GPL-3.0-only
// 本文件移植/重实现自 Axolotl Launcher（YSUMCL fork）的 Forge 安装器逻辑
//（packages/app-lib/src/launcher/mod.rs 的 processors 执行与 data map 构建，
//   packages/app-lib/src/launcher/args.rs 的 get_processor_arguments/get_processor_main_class，
//   GPL-3.0-only）。详见仓库根 LICENSE 与 NOTICE。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CMUmcl.Launch.Forge
{
    /// <summary>
    /// 用已选 Java 按序运行 Forge processors：data map 构建、{KEY}/[coordinate] 参数解析、
    /// 主类读取、退出码与 outputs 校验。
    /// </summary>
    public sealed class ForgeProcessorRunner
    {
        public async Task RunAsync(
            string javaExe,
            IReadOnlyList<ForgeProcessor> processors,
            IReadOnlyDictionary<string, SidedDataEntry> installerData,
            string minecraftJar,
            string minecraftVersion,
            string minecraftDir,
            string librariesDir,
            string installerPath,
            IProgress<LaunchProgress>? progress,
            CancellationToken ct)
        {
            if (!File.Exists(minecraftJar))
                throw new LaunchException($"Forge 安装需要 Minecraft {minecraftVersion} 已安装（找不到 {minecraftJar}）。");

            var dataMap = BuildDataMap(minecraftJar, minecraftVersion, minecraftDir, installerPath, librariesDir, installerData);

            int total = processors.Count;
            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                ForgeProcessor processor = processors[i];

                if (!ShouldRun(processor))
                {
                    progress?.Report(new LaunchProgress { Stage = "安装 Forge", Message = $"跳过 Forge 处理器 {i + 1}/{total}（server）" });
                    continue;
                }

                string classpath = BuildClassPath(processor.Classpath.Concat(new[] { processor.Jar }), librariesDir);
                string jarPath = ResolveLibraryPath(processor.Jar, librariesDir);
                string mainClass = GetProcessorMainClass(jarPath);
                var args = processor.Args.Select(a => ResolveProcessorArg(a, librariesDir, dataMap)).ToList();

                progress?.Report(new LaunchProgress { Stage = "安装 Forge", Message = $"运行 Forge 处理器 {i + 1}/{total}" });

                (int exitCode, string stderr) = await RunJavaAsync(javaExe, classpath, mainClass, args, minecraftDir, ct).ConfigureAwait(false);
                if (exitCode != 0)
                    throw new LaunchException($"Forge 处理器执行失败（{processor.Jar}）。{Tail(stderr)}");

                ValidateOutputs(processor, dataMap, librariesDir);
            }
        }

        // ------------------------------------------------------------------
        // 纯函数（可单测）
        // ------------------------------------------------------------------

        public static bool ShouldRun(ForgeProcessor processor)
        {
            if (processor.Sides == null || processor.Sides.Count == 0)
                return true;
            return processor.Sides.Contains("client", StringComparer.OrdinalIgnoreCase);
        }

        public static Dictionary<string, string> BuildClientDataMap(IReadOnlyDictionary<string, SidedDataEntry> installerData)
        {
            var map = new Dictionary<string, string>();
            foreach (var kv in installerData)
                map[kv.Key] = kv.Value.Client;
            return map;
        }

        public static Dictionary<string, string> BuildDataMap(
            string minecraftJar,
            string minecraftVersion,
            string minecraftDir,
            string installerPath,
            string librariesDir,
            IReadOnlyDictionary<string, SidedDataEntry> installerData)
        {
            var map = BuildClientDataMap(installerData);
            map["SIDE"] = "client";
            map["MINECRAFT_JAR"] = minecraftJar;
            map["MINECRAFT_VERSION"] = minecraftVersion;
            map["ROOT"] = minecraftDir;
            map["INSTALLER"] = installerPath;
            map["LIBRARY_DIR"] = librariesDir;
            return map;
        }

        public static string ResolveLibraryPath(string coordinate, string librariesDir)
            => Path.Combine(librariesDir, MavenArtifact.ToRelativePath(coordinate));

        public static string BuildClassPath(IEnumerable<string> coordinates, string librariesDir)
            => string.Join(Path.PathSeparator, coordinates.Select(c => ResolveLibraryPath(c, librariesDir)));

        public static string StripQuotes(string s)
        {
            if (s.Length >= 2
                && ((s[0] == '\'' && s[s.Length - 1] == '\'') || (s[0] == '"' && s[s.Length - 1] == '"')))
                return s.Substring(1, s.Length - 2);
            return s;
        }

        public static string ResolveDataValue(string value, string librariesDir)
        {
            if (value.Length >= 2 && value[0] == '[' && value[value.Length - 1] == ']')
                return ResolveLibraryPath(value.Substring(1, value.Length - 2), librariesDir);
            return StripQuotes(value);
        }

        public static string ResolveProcessorArg(string arg, string librariesDir, IReadOnlyDictionary<string, string> data)
        {
            if (arg.Length >= 2 && arg[0] == '[' && arg[arg.Length - 1] == ']')
                return ResolveLibraryPath(arg.Substring(1, arg.Length - 2), librariesDir);

            string result = arg;
            foreach (var kv in data)
            {
                string value = ResolveDataValue(kv.Value, librariesDir);
                result = result.Replace("{" + kv.Key + "}", value);
            }
            return result;
        }

        public static string GetProcessorMainClass(string jarPath)
        {
            using ZipArchive zip = ZipFile.OpenRead(jarPath);
            ZipArchiveEntry entry = zip.GetEntry("META-INF/MANIFEST.MF")
                ?? throw new LaunchException($"找不到处理器主类：{jarPath}");

            using var reader = new StreamReader(entry.Open());
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string t = line.Replace(" ", string.Empty).Replace("\t", string.Empty);
                if (t.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase))
                {
                    string main = t.Substring("Main-Class:".Length).Trim();
                    if (!string.IsNullOrEmpty(main))
                        return main;
                }
            }

            throw new LaunchException($"找不到处理器主类：{jarPath}");
        }

        public static void ValidateOutputs(ForgeProcessor processor, IReadOnlyDictionary<string, string> data, string librariesDir)
        {
            if (processor.Outputs == null)
                return;

            foreach (var kv in processor.Outputs)
            {
                string outputPath = ResolveProcessorArg(kv.Key, librariesDir, data);
                string expectedSha1 = StripQuotes(ResolveProcessorArg(kv.Value, librariesDir, data));

                if (!File.Exists(outputPath))
                    throw new LaunchException($"Forge 处理器输出缺失：{outputPath}");

                if (!string.IsNullOrWhiteSpace(expectedSha1)
                    && !MojangRuntimeInstaller.MatchesSha1AndSize(outputPath, 0, expectedSha1))
                    throw new LaunchException($"Forge 处理器输出校验失败：{outputPath}");
            }
        }

        // ------------------------------------------------------------------
        // 进程执行
        // ------------------------------------------------------------------

        private static async Task<(int ExitCode, string Stderr)> RunJavaAsync(
            string javaExe,
            string classpath,
            string mainClass,
            IReadOnlyList<string> args,
            string workingDir,
            CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDir,
            };
            psi.ArgumentList.Add("-cp");
            psi.ArgumentList.Add(classpath);
            psi.ArgumentList.Add(mainClass);
            foreach (string a in args)
                psi.ArgumentList.Add(a);

            using var proc = new Process { StartInfo = psi };
            try
            {
                proc.Start();
                Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
                Task<string> stderrTask = proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                string stderr = await stderrTask.ConfigureAwait(false);
                await stdoutTask.ConfigureAwait(false);
                return (proc.ExitCode, stderr);
            }
            finally
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
            }
        }

        private static string Tail(string text, int maxLines = 30)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string[] lines = text.Split('\n');
            int start = Math.Max(0, lines.Length - maxLines);
            return string.Join("\n", lines, start, lines.Length - start).Trim();
        }
    }
}
```

- [ ] **Step 4.2：创建 `TestsProcessor.cs`**

文件 `D:\新启动器\.tmp\ForgeHarness\TestsProcessor.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading.Tasks;
using CMUmcl.Launch;
using CMUmcl.Launch.Forge;

namespace ForgeHarness
{
    internal static class TestsProcessor
    {
        static TestsProcessor()
        {
            TestRegistry.Tests.Add(TestResolveProcessorArg);
            TestRegistry.Tests.Add(TestBuildClassPathAndDataMap);
            TestRegistry.Tests.Add(TestGetProcessorMainClass);
            TestRegistry.Tests.Add(TestShouldRun);
            TestRegistry.Tests.Add(TestValidateOutputs);
        }

        private static Task TestResolveProcessorArg(State s)
        {
            Console.WriteLine("-- ResolveProcessorArg --");
            string libDir = @"C:\mc\libraries";
            var data = new Dictionary<string, string>
            {
                ["SIDE"] = "client",
                ["ROOT"] = @"C:\mc",
                ["MAPPINGS"] = "[net.minecraft:client:1.20.1:mappings@txt]",
                ["MC_SLIM_SHA"] = "'de86b7ca3be2c1cff8bd05d5c3f4a9c1c8a1d4b7'",
                ["BINPATCH"] = @"C:\mc\.forge-staging-abc\data\client.lzma",
            };

            s.Check("[coord] → lib path",
                ForgeProcessorRunner.ResolveProcessorArg("[net.minecraft:client:1.20.1:mappings@txt]", libDir, data)
                == Path.Combine(libDir, "net", "minecraft", "client", "1.20.1", "client-1.20.1-mappings.txt"));
            s.Check("{KEY} literal",
                ForgeProcessorRunner.ResolveProcessorArg("{BINPATCH}", libDir, data) == data["BINPATCH"]);
            s.Check("{KEY}/suffix",
                ForgeProcessorRunner.ResolveProcessorArg("{ROOT}/libraries", libDir, data) == @"C:\mc/libraries");
            s.Check("{KEY} → [coord] value",
                ForgeProcessorRunner.ResolveProcessorArg("{MAPPINGS}", libDir, data)
                == Path.Combine(libDir, "net", "minecraft", "client", "1.20.1", "client-1.20.1-mappings.txt"));
            s.Check("{MC_SLIM_SHA} strips quotes",
                ForgeProcessorRunner.ResolveProcessorArg("{MC_SLIM_SHA}", libDir, data)
                == "de86b7ca3be2c1cff8bd05d5c3f4a9c1c8a1d4b7");
            return Task.CompletedTask;
        }

        private static Task TestBuildClassPathAndDataMap(State s)
        {
            Console.WriteLine("-- BuildClassPath / BuildDataMap --");
            string libDir = @"C:\mc\libraries";
            string cp = ForgeProcessorRunner.BuildClassPath(
                new[] { "org.ow2.asm:asm:9.9.1", "com.example:lib:1.0" }, libDir);
            s.Check("classpath join",
                cp == Path.Combine(libDir, "org", "ow2", "asm", "asm", "9.9.1", "asm-9.9.1.jar")
                    + Path.PathSeparator
                    + Path.Combine(libDir, "com", "example", "lib", "1.0", "lib-1.0.jar"));

            var installerData = new Dictionary<string, SidedDataEntry>
            {
                ["MAPPINGS"] = new SidedDataEntry { Client = "[coord]", Server = "" },
            };
            var map = ForgeProcessorRunner.BuildDataMap(
                @"C:\mc\versions\1.20.1\1.20.1.jar", "1.20.1", @"C:\mc", @"C:\mc\installer.jar", libDir, installerData);
            s.Check("SIDE=client", map["SIDE"] == "client");
            s.Check("MINECRAFT_JAR", map["MINECRAFT_JAR"] == @"C:\mc\versions\1.20.1\1.20.1.jar");
            s.Check("LIBRARY_DIR", map["LIBRARY_DIR"] == libDir);
            s.Check("MAPPINGS carried", map["MAPPINGS"] == "[coord]");
            return Task.CompletedTask;
        }

        private static Task TestGetProcessorMainClass(State s)
        {
            Console.WriteLine("-- GetProcessorMainClass --");
            string jar = Path.Combine(Path.GetTempPath(), "proc-" + Guid.NewGuid().ToString("N") + ".jar");
            using (var zip = ZipFile.Open(jar, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("META-INF/MANIFEST.MF");
                using var w = new StreamWriter(entry.Open());
                w.Write("Manifest-Version: 1.0\r\nMain-Class: com.example.Main\r\n");
            }
            s.Check("main class read", ForgeProcessorRunner.GetProcessorMainClass(jar) == "com.example.Main");
            File.Delete(jar);
            return Task.CompletedTask;
        }

        private static Task TestShouldRun(State s)
        {
            Console.WriteLine("-- ShouldRun (sides) --");
            s.Check("no sides → run", ForgeProcessorRunner.ShouldRun(new ForgeProcessor()));
            s.Check("client side → run", ForgeProcessorRunner.ShouldRun(new ForgeProcessor { Sides = new List<string> { "client" } }));
            s.Check("server side → skip", !ForgeProcessorRunner.ShouldRun(new ForgeProcessor { Sides = new List<string> { "server" } }));
            s.Check("extract side → skip", !ForgeProcessorRunner.ShouldRun(new ForgeProcessor { Sides = new List<string> { "extract" } }));
            return Task.CompletedTask;
        }

        private static Task TestValidateOutputs(State s)
        {
            Console.WriteLine("-- ValidateOutputs --");
            string f = Path.Combine(Path.GetTempPath(), "out-" + Guid.NewGuid().ToString("N") + ".bin");
            byte[] bytes = new byte[] { 1, 2, 3 };
            File.WriteAllBytes(f, bytes);
            string sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

            var data = new Dictionary<string, string> { ["OUT"] = f, ["OUT_SHA"] = sha1 };
            var p = new ForgeProcessor { Outputs = new Dictionary<string, string> { ["{OUT}"] = "{OUT_SHA}" } };

            s.Check("outputs valid ok", Try(() => ForgeProcessorRunner.ValidateOutputs(p, data, @"C:\mc\libraries")));

            data["OUT_SHA"] = new string('0', 40);
            s.Check("outputs mismatch throws", !Try(() => ForgeProcessorRunner.ValidateOutputs(p, data, @"C:\mc\libraries")));

            File.Delete(f);
            return Task.CompletedTask;
        }

        private static bool Try(Action a)
        {
            try { a(); return true; }
            catch (LaunchException) { return false; }
        }
    }
}
```

- [ ] **Step 4.3：构建 CMUmcl**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet build "D:\新启动器\CMUmcl\CMUmcl.csproj" -c Debug
```

- 预期：`0 Error(s)`。

- [ ] **Step 4.4：运行测试（含参数解析/主类/outputs 用例）**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet run --project "D:\新启动器\.tmp\ForgeHarness\ForgeHarness.csproj"
```

- 预期：`ResolveProcessorArg`、`BuildClassPath / BuildDataMap`、`GetProcessorMainClass`、`ShouldRun`、`ValidateOutputs` 各用例全部 `PASS`；末尾 `== ALL TESTS PASSED ==`。

- [ ] **Step 4.5：实现者自查**

确认 `{KEY}/suffix` 用全量替换而非整串匹配；`{MC_SLIM_SHA}` 值剥单引号；`[coord]` 与 data 值中的 `[coord]` 都解析为库路径；主类从 `META-INF/MANIFEST.MF` 的 `Main-Class:` 行读取；`sides` 缺失视为两端都跑；进程用 `ArgumentList` 避免参数转义问题、工作目录为 Minecraft 目录、退出码非 0 抛含 stderr 尾部的中文错误。

---

## 任务 5：ForgeLoaderInstaller（编排：下载校验、库安装、跳过判定、清理）

**Files:**
- Create: `D:\新启动器\CMUmcl\Launch\Forge\ForgeLoaderInstaller.cs`
- Create: `D:\新启动器\.tmp\ForgeHarness\TestsInstaller.cs`

- [ ] **Step 5.1：创建 `ForgeLoaderInstaller.cs`**

文件 `D:\新启动器\CMUmcl\Launch\Forge\ForgeLoaderInstaller.cs`，完整内容：

```csharp
// SPDX-License-Identifier: GPL-3.0-only
// 本文件移植/重实现自 Axolotl Launcher（YSUMCL fork）的 Forge 安装器逻辑
//（packages/app-lib/src/api/loader_metadata.rs 的 resolve_installer_profile，
//   GPL-3.0-only）。详见仓库根 LICENSE 与 NOTICE。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using CmlLib.Core;

namespace CMUmcl.Launch.Forge
{
    /// <summary>
    /// Forge 安装编排器：下载并校验安装器、解析、下载安装器库、运行 processors、
    /// 写版本档案与跳过标记，支持“跳过重装”本地校验。持有 HttpClient（IDisposable）。
    /// </summary>
    public sealed class ForgeLoaderInstaller : IDisposable
    {
        private const string ForgeMavenRoot = "https://maven.minecraftforge.net";
        private const string UserAgent = "CMUmcl/0.1.0";

        private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "maven.minecraftforge.net",
            "files.minecraftforge.net",
            "libraries.minecraft.net",
            "repo1.maven.org",
            "repo.maven.apache.org",
        };

        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly HttpClient _http;

        public ForgeLoaderInstaller()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }

        public static bool IsAllowedUrl(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && AllowedHosts.Contains(uri.Host);
        }

        public static void EnsureAllowedUri(Uri? uri)
        {
            if (uri == null || uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host))
                throw new LaunchException("Forge 下载被重定向到不允许的主机，已中止。");
        }

        public async Task<string> InstallAsync(
            string gameVersion,
            string loaderVersion,
            JavaInfo java,
            MinecraftPath minecraftPath,
            IProgress<LaunchProgress>? progress,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(gameVersion) || string.IsNullOrWhiteSpace(loaderVersion))
                throw new LaunchException("缺少 Forge 版本信息，无法安装该加载器。");

            string versionId = $"{gameVersion}-forge-{loaderVersion}";
            string minecraftDir = minecraftPath.BasePath;
            string librariesDir = minecraftPath.Library;

            if (CanSkipInstall(versionId, gameVersion, minecraftDir, librariesDir))
            {
                progress?.Report(new LaunchProgress { Stage = "安装 Forge", Message = $"Forge {versionId} 已安装，跳过重装。" });
                return versionId;
            }

            string staging = Path.Combine(minecraftDir, ".forge-staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);

            try
            {
                string installerUrl = $"{ForgeMavenRoot}/net/minecraftforge/forge/{versionId}/forge-{versionId}-installer.jar";

                // 1. 下载安装器 + .sha1 校验
                progress?.Report(new LaunchProgress { Stage = "安装 Forge", Message = "正在下载 Forge 安装器…" });
                string installerPath = Path.Combine(staging, "installer.jar");
                string expectedSha1 = await DownloadInstallerSha1Async(installerUrl + ".sha1", ct).ConfigureAwait(false);
                await DownloadToFileAsync(installerUrl, installerPath, 0, expectedSha1, "下载 Forge 安装器失败，已中止。", ct).ConfigureAwait(false);

                // 2. 解析
                progress?.Report(new LaunchProgress { Stage = "安装 Forge", Message = "正在解析 Forge 安装器…" });
                byte[] installerBytes = await File.ReadAllBytesAsync(installerPath, ct).ConfigureAwait(false);
                ParsedForgeInstaller parsed = ForgeInstallerParser.Parse(installerBytes, staging);

                // 3. 下载安装器库
                progress?.Report(new LaunchProgress { Stage = "安装 Forge", Message = "正在下载 Forge 依赖库…" });
                await InstallLibrariesAsync(parsed.InstallerLibraries, librariesDir, progress, ct).ConfigureAwait(false);

                // 4. 运行 processors
                string minecraftJar = Path.Combine(minecraftDir, "versions", gameVersion, gameVersion + ".jar");
                string javaExe = ResolveConsoleJava(java.JavaPath);
                progress?.Report(new LaunchProgress { Stage = "安装 Forge", Message = "正在运行 Forge 处理器…" });
                var runner = new ForgeProcessorRunner();
                await runner.RunAsync(javaExe, parsed.Processors, parsed.Data, minecraftJar, gameVersion, minecraftDir, librariesDir, installerPath, progress, ct).ConfigureAwait(false);

                // 5. 落盘版本档案 + install_profile.json + .forge-complete
                parsed.FinalVersionDocument["id"] = versionId;
                string versionDir = Path.Combine(minecraftDir, "versions", versionId);
                Directory.CreateDirectory(versionDir);

                string versionJsonText = parsed.FinalVersionDocument.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(Path.Combine(versionDir, versionId + ".json"), versionJsonText, ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(versionDir, "install_profile.json"), parsed.InstallProfileJson, ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(versionDir, ".forge-complete"), expectedSha1, ct).ConfigureAwait(false);

                progress?.Report(new LaunchProgress { Stage = "安装 Forge", Message = $"Forge {versionId} 安装完成。" });
                return versionId;
            }
            finally
            {
                TryDeleteDirectory(staging);
            }
        }

        public static bool CanSkipInstall(string versionId, string gameVersion, string minecraftDir, string librariesDir)
        {
            try
            {
                string versionDir = Path.Combine(minecraftDir, "versions", versionId);
                string versionJsonPath = Path.Combine(versionDir, versionId + ".json");
                string installProfilePath = Path.Combine(versionDir, "install_profile.json");
                string completePath = Path.Combine(versionDir, ".forge-complete");

                if (!File.Exists(versionJsonPath) || !File.Exists(installProfilePath) || !File.Exists(completePath))
                    return false;

                JsonNode? vn = JsonNode.Parse(File.ReadAllText(versionJsonPath));
                if (vn is not JsonObject vo)
                    return false;

                string inheritsFrom = vo["inheritsFrom"]?.GetValue<string>() ?? string.Empty;
                if (!string.Equals(inheritsFrom, gameVersion, StringComparison.Ordinal))
                    return false;

                string recorded = File.ReadAllText(completePath).Trim();
                if (recorded.Length != 40 || recorded.Any(c => !Uri.IsHexDigit(c)))
                    return false;

                InstallerProfile profile = JsonSerializer.Deserialize<InstallerProfile>(File.ReadAllText(installProfilePath), JsonOptions)
                    ?? new InstallerProfile();

                // 合并库（version.json ∪ install_profile.libraries），逐个校验存在 + sha1
                var libs = new List<ForgeLibrary>();
                if (vo["libraries"] is JsonArray arr)
                {
                    var vlibs = JsonSerializer.Deserialize<List<ForgeLibrary>>(arr.ToJsonString(), JsonOptions) ?? new List<ForgeLibrary>();
                    libs.AddRange(vlibs);
                }
                var names = new HashSet<string>(libs.Select(l => l.Name), StringComparer.Ordinal);
                foreach (ForgeLibrary l in profile.Libraries)
                    if (names.Add(l.Name))
                        libs.Add(l);

                foreach (ForgeLibrary lib in libs)
                {
                    string rel = string.IsNullOrWhiteSpace(lib.Downloads?.Artifact?.Path)
                        ? MavenArtifact.ToRelativePath(lib.Name)
                        : ForgePathSafety.SanitizeRelativePath(lib.Downloads.Artifact.Path);
                    string p = Path.Combine(librariesDir, rel);
                    if (!File.Exists(p))
                        return false;
                    string sha1 = lib.Downloads?.Artifact?.Sha1 ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(sha1)
                        && !MojangRuntimeInstaller.MatchesSha1AndSize(p, lib.Downloads?.Artifact?.Size ?? 0, sha1))
                        return false;
                }

                // outputs 校验（仅 client 处理器）
                var clientMap = ForgeProcessorRunner.BuildClientDataMap(profile.Data);
                foreach (ForgeProcessor proc in profile.Processors)
                {
                    if (!ForgeProcessorRunner.ShouldRun(proc))
                        continue;
                    ForgeProcessorRunner.ValidateOutputs(proc, clientMap, librariesDir);
                }

                return true;
            }
            catch (Exception ex) when (ex is LaunchException or IOException or JsonException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // 下载
        // ------------------------------------------------------------------

        private async Task<string> DownloadInstallerSha1Async(string sha1Url, CancellationToken ct)
        {
            if (!IsAllowedUrl(sha1Url))
                throw new LaunchException("下载 Forge 安装器失败（.sha1 地址不在允许的主机列表内），已中止。");

            using var request = new HttpRequestMessage(HttpMethod.Get, sha1Url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            EnsureAllowedUri(response.RequestMessage?.RequestUri);
            if (!response.IsSuccessStatusCode)
                throw new LaunchException($"下载 Forge 安装器失败（.sha1 缺失，HTTP {(int)response.StatusCode}），已中止。");

            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string hash = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (hash.Length != 40 || hash.Any(c => !Uri.IsHexDigit(c)))
                throw new LaunchException("下载 Forge 安装器失败（.sha1 内容非法），已中止。");

            return hash.ToLowerInvariant();
        }

        private async Task InstallLibrariesAsync(List<ForgeLibrary> libraries, string librariesDir, IProgress<LaunchProgress>? progress, CancellationToken ct)
        {
            int done = 0;
            foreach (ForgeLibrary lib in libraries)
            {
                ct.ThrowIfCancellationRequested();

                ForgeLibraryDownload? artifact = lib.Downloads?.Artifact;
                string rel = string.IsNullOrWhiteSpace(artifact?.Path)
                    ? MavenArtifact.ToRelativePath(lib.Name)
                    : ForgePathSafety.SanitizeRelativePath(artifact.Path);
                string target = ForgePathSafety.ResolveUnder(librariesDir, rel);

                string url = artifact?.Url ?? string.Empty;
                string sha1 = artifact?.Sha1 ?? string.Empty;
                long size = artifact?.Size ?? 0;

                if (string.IsNullOrWhiteSpace(url))
                    throw new LaunchException($"安装 Forge 依赖库失败（{lib.Name}：缺少下载地址），已中止。");

                if (File.Exists(target) && !string.IsNullOrWhiteSpace(sha1) && MojangRuntimeInstaller.MatchesSha1AndSize(target, size, sha1))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await DownloadToFileAsync(url, target, size, sha1, $"安装 Forge 依赖库失败（{lib.Name}），已中止。", ct).ConfigureAwait(false);

                done++;
                progress?.Report(new LaunchProgress { Stage = "安装 Forge", ProgressedTasks = done, TotalTasks = libraries.Count, Message = $"已下载 Forge 依赖库 {done}/{libraries.Count}" });
            }
        }

        private async Task DownloadToFileAsync(string url, string targetPath, long expectedSize, string expectedSha1, string failureMessage, CancellationToken ct)
        {
            if (!IsAllowedUrl(url))
                throw new LaunchException(failureMessage);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            EnsureAllowedUri(response.RequestMessage?.RequestUri);
            if (!response.IsSuccessStatusCode)
                throw new LaunchException(failureMessage);

            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            await using var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                sha1.AppendData(buffer, 0, read);
                await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
            }

            string actual = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
            bool sizeOk = expectedSize <= 0 || total == expectedSize;
            bool sha1Ok = string.IsNullOrWhiteSpace(expectedSha1)
                || string.Equals(actual, expectedSha1.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);

            if (!sizeOk || !sha1Ok)
                throw new LaunchException(failureMessage);
        }

        // ------------------------------------------------------------------
        // 工具
        // ------------------------------------------------------------------

        private static string ResolveConsoleJava(string javawPath)
        {
            string? dir = Path.GetDirectoryName(javawPath);
            if (!string.IsNullOrEmpty(dir))
            {
                string java = Path.Combine(dir, "java.exe");
                if (File.Exists(java))
                    return java;
            }
            return javawPath;
        }

        private static void TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch { /* best effort */ }
        }

        public void Dispose() => _http.Dispose();
    }
}
```

- [ ] **Step 5.2：创建 `TestsInstaller.cs`（白名单 + 跳过判定，合成目录，无网络）**

文件 `D:\新启动器\.tmp\ForgeHarness\TestsInstaller.cs`：

```csharp
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using CMUmcl.Launch;
using CMUmcl.Launch.Forge;

namespace ForgeHarness
{
    internal static class TestsInstaller
    {
        static TestsInstaller()
        {
            TestRegistry.Tests.Add(TestAllowlist);
            TestRegistry.Tests.Add(TestSkipReinstall);
        }

        private static Task TestAllowlist(State s)
        {
            Console.WriteLine("-- Forge host allowlist --");
            s.Check("maven.minecraftforge.net ok",
                ForgeLoaderInstaller.IsAllowedUrl("https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.4.28/forge-1.20.1-47.4.28-installer.jar"));
            s.Check("libraries.minecraft.net ok",
                ForgeLoaderInstaller.IsAllowedUrl("https://libraries.minecraft.net/org/ow2/asm/asm/9.9.1/asm-9.9.1.jar"));
            s.Check("repo1.maven.org ok",
                ForgeLoaderInstaller.IsAllowedUrl("https://repo1.maven.org/maven2/x"));
            s.Check("files.minecraftforge.net ok",
                ForgeLoaderInstaller.IsAllowedUrl("https://files.minecraftforge.net/net/minecraftforge/forge/x"));
            s.Check("http rejected", !ForgeLoaderInstaller.IsAllowedUrl("http://maven.minecraftforge.net/x"));
            s.Check("evil host rejected", !ForgeLoaderInstaller.IsAllowedUrl("https://evil.com/x"));
            s.Check("lookalike host rejected", !ForgeLoaderInstaller.IsAllowedUrl("https://maven.minecraftforge.net.evil.com/x"));
            return Task.CompletedTask;
        }

        private static Task TestSkipReinstall(State s)
        {
            Console.WriteLine("-- CanSkipInstall (synthetic) --");
            string mc = Path.Combine(Path.GetTempPath(), "skip-" + Guid.NewGuid().ToString("N"));
            string libs = Path.Combine(mc, "libraries");
            string vdir = Path.Combine(mc, "versions", "1.20.1-forge-47.4.28");
            Directory.CreateDirectory(vdir);
            Directory.CreateDirectory(libs);
            try
            {
                s.Check("empty → false", !ForgeLoaderInstaller.CanSkipInstall("1.20.1-forge-47.4.28", "1.20.1", mc, libs));

                string libRel = "net/minecraftforge/forge/1.20.1-47.4.28/forge-1.20.1-47.4.28-universal.jar";
                string libPath = Path.Combine(libs, libRel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(libPath)!);
                byte[] data = new byte[] { 1, 2, 3 };
                File.WriteAllBytes(libPath, data);
                string sha1 = Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

                string goodJson = "{\"id\":\"1.20.1-forge-47.4.28\",\"inheritsFrom\":\"1.20.1\",\"libraries\":[{\"name\":\"net.minecraftforge:forge:1.20.1-47.4.28:universal\",\"downloads\":{\"artifact\":{\"path\":\"net/minecraftforge/forge/1.20.1-47.4.28/forge-1.20.1-47.4.28-universal.jar\",\"sha1\":\"" + sha1 + "\"}}}]}";
                string profileJson = "{\"minecraft\":\"1.20.1\",\"data\":{},\"processors\":[],\"libraries\":[]}";

                File.WriteAllText(Path.Combine(vdir, "1.20.1-forge-47.4.28.json"), goodJson);
                File.WriteAllText(Path.Combine(vdir, "install_profile.json"), profileJson);
                File.WriteAllText(Path.Combine(vdir, ".forge-complete"), new string('a', 40));

                s.Check("valid → true", ForgeLoaderInstaller.CanSkipInstall("1.20.1-forge-47.4.28", "1.20.1", mc, libs));

                File.WriteAllText(Path.Combine(vdir, "1.20.1-forge-47.4.28.json"),
                    "{\"id\":\"1.20.1-forge-47.4.28\",\"inheritsFrom\":\"1.20.2\",\"libraries\":[]}");
                s.Check("bad inheritsFrom → false", !ForgeLoaderInstaller.CanSkipInstall("1.20.1-forge-47.4.28", "1.20.1", mc, libs));

                File.WriteAllText(Path.Combine(vdir, "1.20.1-forge-47.4.28.json"), goodJson);
                File.WriteAllBytes(libPath, new byte[] { 9, 9 });
                s.Check("bad lib sha1 → false", !ForgeLoaderInstaller.CanSkipInstall("1.20.1-forge-47.4.28", "1.20.1", mc, libs));

                File.WriteAllBytes(libPath, data);
                File.WriteAllText(Path.Combine(vdir, ".forge-complete"), "not-a-sha1");
                s.Check("bad .forge-complete → false", !ForgeLoaderInstaller.CanSkipInstall("1.20.1-forge-47.4.28", "1.20.1", mc, libs));
            }
            finally
            {
                try { Directory.Delete(mc, recursive: true); } catch { }
            }
            return Task.CompletedTask;
        }
    }
}
```

- [ ] **Step 5.3：构建 CMUmcl**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet build "D:\新启动器\CMUmcl\CMUmcl.csproj" -c Debug
```

- 预期：`0 Error(s)`。

- [ ] **Step 5.4：运行测试（白名单 + 跳过判定）**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet run --project "D:\新启动器\.tmp\ForgeHarness\ForgeHarness.csproj"
```

- 预期：`Forge host allowlist`、`CanSkipInstall (synthetic)` 各用例全部 `PASS`（`valid → true`、破坏 `inheritsFrom`/库 sha1/`.forge-complete` 后均 `false`）；末尾 `== ALL TESTS PASSED ==`。

- [ ] **Step 5.5：提交**

```powershell
cd "D:\新启动器"
git add "CMUmcl\Launch\Forge\ForgeInstallerParser.cs" "CMUmcl\Launch\Forge\ForgeProcessorRunner.cs" "CMUmcl\Launch\Forge\ForgeLoaderInstaller.cs"
git commit -m "feat(forge): add installer parser, processor runner and orchestrator"
```

- 预期：`git log --oneline -1` 显示该提交信息。

---

## 任务 6：LaunchService 集成（Java 前移 + case "forge"）与 PackCardViewModel

**Files:**
- Modify: `D:\新启动器\CMUmcl\Launch\LaunchService.cs`
- Modify: `D:\新启动器\CMUmcl\PackCardViewModel.cs`

- [ ] **Step 6.1：在 `LaunchService.cs` 顶部加入 `using CMUmcl.Launch.Forge;`**

用编辑工具将以下旧文本：

```csharp
using CMUmcl.Auth;
using CMUmcl.Content;
```

替换为：

```csharp
using CMUmcl.Auth;
using CMUmcl.Content;
using CMUmcl.Launch.Forge;
```

- [ ] **Step 6.2：把 Java 选择前移到原版安装之后、加载器解析之前**

用编辑工具将以下旧文本：

```csharp
            await launcher.InstallAsync(
                gameVersion,
                CreateFileProgress("安装原版", progress),
                CreateByteProgress("安装原版", progress),
                ct).ConfigureAwait(false);

            // 8. Resolve loader.
            string versionName;
```

替换为：

```csharp
            await launcher.InstallAsync(
                gameVersion,
                CreateFileProgress("安装原版", progress),
                CreateByteProgress("安装原版", progress),
                ct).ConfigureAwait(false);

            // 7.5 Select a compatible Java runtime (before loader resolution so Forge
            // processors can run). Same logic as before; only the timing changes.
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

            // 8. Resolve loader.
            string versionName;
```

- [ ] **Step 6.3：删除旧的 8.5 Java 选择块**

用编辑工具将以下旧文本（整块删除，替换为空）：

```csharp
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

替换为：

```csharp
            // 9. Build MSession.
```

- [ ] **Step 6.4：在 switch 中新增 `case "forge"`**

用编辑工具将以下旧文本：

```csharp
                case "forge":
                case "neoforge":
                case "quilt":
                    throw new LaunchException(UnsupportedLoaderMessage);

                default:
                    throw new LaunchException(UnsupportedLoaderMessage);
```

替换为：

```csharp
                case "forge":
                {
                    if (string.IsNullOrWhiteSpace(loaderVersion))
                        throw new LaunchException("缺少 Forge 版本信息，无法安装该加载器。");

                    progress?.Report(new LaunchProgress { Stage = "安装 Forge", Message = "正在安装 Forge…" });
                    using var forgeInstaller = new ForgeLoaderInstaller();
                    versionName = await forgeInstaller.InstallAsync(gameVersion, loaderVersion, java, launcher.MinecraftPath, progress, ct).ConfigureAwait(false);

                    progress?.Report(new LaunchProgress { Stage = "安装 Forge", Message = $"正在下载 Forge 依赖（{versionName}）…" });
                    await launcher.InstallAsync(
                        versionName,
                        CreateFileProgress("安装 Forge", progress),
                        CreateByteProgress("安装 Forge", progress),
                        ct).ConfigureAwait(false);
                    break;
                }

                case "neoforge":
                case "quilt":
                    throw new LaunchException(UnsupportedLoaderMessage);

                default:
                    throw new LaunchException(UnsupportedLoaderMessage);
```

- [ ] **Step 6.5：修改 `PackCardViewModel.cs`（forge 不再提示不支持）**

用编辑工具将以下旧文本：

```csharp
            if (loader is "forge" or "neoforge" or "quilt")
            {
                HasLoaderNote = true;
                LoaderNote = "当前先支持原版/Fabric";
            }
```

替换为：

```csharp
            if (loader is "neoforge" or "quilt")
            {
                HasLoaderNote = true;
                LoaderNote = "当前先支持原版/Fabric";
            }
```

- [ ] **Step 6.6：构建 CMUmcl**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet build "D:\新启动器\CMUmcl\CMUmcl.csproj" -c Debug
```

- 预期：`0 Error(s)`。若报 `java` 可为 null，检查 Step 6.3 是否完全删除旧块、Step 6.2 的 `java` 声明在 `switch` 之前且非空收窄成立。

- [ ] **Step 6.7：运行默认纯函数测试（确认无回归）**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet run --project "D:\新启动器\.tmp\ForgeHarness\ForgeHarness.csproj"
```

- 预期：所有用例 `PASS`，末尾 `== ALL TESTS PASSED ==`。

- [ ] **Step 6.8：实现者自查**

对照 `LaunchService.LaunchAsync`：步骤 7 原版安装 → 7.5 Java 选择 → 8 switch（forge 分支用控制台 java.exe 跑 processors，返回 `versionName`）→ 9/10/11 建进程（游戏仍用 `java.JavaPath` 即 javaw.exe）。`neoforge`/`quilt` 仍抛原 `UnsupportedLoaderMessage`。`PackCardViewModel` 中 `forge` 不再显示“当前先支持原版/Fabric”。用 `git diff --stat` 确认只改了这两个文件。

---

## 任务 7：合成 `forge-test` 实例 + 端到端（安装 / 启动 / 跳过 / 损坏修复）

**Files:**
- Create: `D:\新启动器\.tmp\ForgeHarness\TestsEndToEnd.cs`

- [ ] **Step 7.1：创建 `TestsEndToEnd.cs`（--install 真实安装 + --launch 端到端）**

文件 `D:\新启动器\.tmp\ForgeHarness\TestsEndToEnd.cs`，完整内容：

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CmlLib.Core;
using CMUmcl.Auth;
using CMUmcl.Content;
using CMUmcl.Launch;

namespace ForgeHarness
{
    internal static class TestsEndToEnd
    {
        private const string UniversalSha1 = "1c2208dc1db2a61ddb6d5dcbe8eae4fd44629e9d";
        private const string VersionId = "1.20.1-forge-47.4.28";

        static TestsEndToEnd()
        {
            TestRegistry.Tests.Add(TestInstallAsync);
            TestRegistry.Tests.Add(TestLaunchAsync);
        }

        private static async Task TestInstallAsync(State s)
        {
            if (!s.Install)
            {
                Console.WriteLine("SKIP TestInstallAsync (需要 --install)");
                return;
            }

            Console.WriteLine("-- ForgeLoaderInstaller.InstallAsync (real) --");
            JavaInfo? java = JavaLocator.Select(17, JavaLocator.Scan());
            if (java == null)
            {
                s.Check("jdk >= 17 available", false);
                return;
            }
            Console.WriteLine("  java=" + java.Major + " " + java.JavaPath);

            var mp = new MinecraftPath(LauncherPaths.MinecraftDirectory);
            var progress = new Progress<LaunchProgress>(p => Console.WriteLine("  [" + p.Stage + "] " + p.Message));

            using (var inst = new ForgeLoaderInstaller())
            {
                try
                {
                    string id = await inst.InstallAsync("1.20.1", "47.4.28", java, mp, progress, CancellationToken.None);
                    s.Check("version id", id == VersionId);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  EXCEPTION: " + ex.Message);
                    s.Check("install completed without exception", false);
                    return;
                }
            }

            string vdir = Path.Combine(LauncherPaths.MinecraftDirectory, "versions", VersionId);
            string vjson = Path.Combine(vdir, VersionId + ".json");
            s.Check("version json exists", File.Exists(vjson));
            s.Check("install_profile.json exists", File.Exists(Path.Combine(vdir, "install_profile.json")));
            s.Check(".forge-complete exists", File.Exists(Path.Combine(vdir, ".forge-complete")));

            string universal = Path.Combine(LauncherPaths.MinecraftDirectory, "libraries",
                "net", "minecraftforge", "forge", "1.20.1-47.4.28", "forge-1.20.1-47.4.28-universal.jar");
            s.Check("universal jar exists", File.Exists(universal));
            s.Check("universal jar sha1 == 1c2208dc…", MojangRuntimeInstaller.MatchesSha1AndSize(universal, 0, UniversalSha1));
            s.Check("no staging dirs left", !HasStagingDirs());

            // 第二次运行 → 跳过重装
            Console.WriteLine("-- second run (skip) --");
            using (var inst2 = new ForgeLoaderInstaller())
            {
                string id2 = await inst2.InstallAsync("1.20.1", "47.4.28", java, mp, null, CancellationToken.None);
                s.Check("second run returns same id (skip)", id2 == VersionId);
            }

            // 损坏 → 重装修复
            Console.WriteLine("-- corrupt + reinstall --");
            File.Delete(universal);
            using (var inst3 = new ForgeLoaderInstaller())
            {
                string id3 = await inst3.InstallAsync("1.20.1", "47.4.28", java, mp, null, CancellationToken.None);
                s.Check("reinstalled after corruption", id3 == VersionId);
            }
            s.Check("universal jar restored",
                File.Exists(universal) && MojangRuntimeInstaller.MatchesSha1AndSize(universal, 0, UniversalSha1));
            s.Check("no staging dirs after repair", !HasStagingDirs());
        }

        private static async Task TestLaunchAsync(State s)
        {
            if (!s.Launch)
            {
                Console.WriteLine("SKIP TestLaunchAsync (需要 --launch)");
                return;
            }

            Console.WriteLine("-- LaunchService end-to-end (forge-test) --");

            // 1. 合成 forge-test 实例（本地，不依赖线上整合包）
            string instanceDir = Path.Combine(LauncherPaths.InstancesDirectory, "forge-test");
            Directory.CreateDirectory(instanceDir);
            File.WriteAllText(Path.Combine(instanceDir, "instance.json"),
                "{\"id\":\"forge-test\",\"name\":\"forge-test\",\"revision\":1,\"game\":\"1.20.1\",\"loader\":\"forge\",\"loaderVersion\":\"47.4.28\",\"serverAddress\":\"127.0.0.1\",\"serverPort\":25565}");
            var store = new InstalledStateStore();
            store.SetInstalledRevision("forge-test", 1);

            AuthStore authStore = new AuthStore();
            AuthSession? session = authStore.LoadSession();
            if (session == null)
            {
                Console.WriteLine("AUTH_SESSION_LOADED=False");
                Console.WriteLine("RESULT=ABORTED_NO_SESSION");
                s.Check("auth session loaded", false);
                return;
            }
            Console.WriteLine("AUTH_USERNAME=" + session.Username);

            using YggdrasilClient yggdrasil = new YggdrasilClient();
            using LaunchService launchService = new LaunchService(yggdrasil, authStore, new InstalledStateStore());

            ManifestInstance instance = new ManifestInstance
            {
                Id = "forge-test",
                Name = "forge-test",
                Revision = 1,
                Minecraft = new ManifestMinecraft { GameVersion = "1.20.1", Loader = "forge", LoaderVersion = "47.4.28" },
                Server = new ManifestServer { Address = "127.0.0.1", Port = 25565 },
            };

            var progress = new Progress<LaunchProgress>(p => Console.WriteLine("  [" + p.Stage + "] " + p.Message));

            Process? proc = null;
            try
            {
                LaunchResult result = await launchService.LaunchAsync(instance, session, progress).ConfigureAwait(false);
                proc = result.Process;
                Console.WriteLine("VERSION_NAME=" + result.VersionName);

                var errBuf = new StringBuilder();
                Task? readErr = null;
                if (proc.StartInfo.RedirectStandardError)
                    readErr = Task.Run(() => ReadToBuffer(proc.StandardError, errBuf));

                var deadline = DateTime.UtcNow.AddSeconds(120);
                while (!proc.HasExited && DateTime.UtcNow < deadline)
                    await Task.Delay(250).ConfigureAwait(false);

                if (!proc.HasExited)
                {
                    Console.WriteLine("GAME_RUNNING_AFTER_120S");
                    s.Check("game process running", true);
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    await Task.Delay(1000).ConfigureAwait(false);
                }
                else
                {
                    Console.WriteLine("GAME_EXIT_CODE=" + proc.ExitCode);
                    s.Check("game process running (no early crash)", false);
                }

                if (readErr != null) await readErr.ConfigureAwait(false);
                if (errBuf.Length > 0)
                {
                    Console.WriteLine("=== CAPTURED STDERR (tail 60) ===");
                    Console.WriteLine(Tail(errBuf.ToString(), 60));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("LAUNCH_EXCEPTION_TYPE=" + ex.GetType().FullName);
                Console.WriteLine("LAUNCH_EXCEPTION_MESSAGE=" + ex.Message);
                s.Check("launch completed without exception", false);
            }
            finally
            {
                if (proc != null)
                {
                    try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                }
            }

            string logPath = Path.Combine(instanceDir, "logs", "latest.log");
            if (File.Exists(logPath))
            {
                string log = File.ReadAllText(logPath);
                Console.WriteLine("=== LATEST.LOG (tail 80) ===");
                Console.WriteLine(Tail(log, 80));
                s.Check("log mentions Forge/BootstrapLauncher",
                    log.Contains("BootstrapLauncher", StringComparison.OrdinalIgnoreCase)
                    || log.Contains("fml", StringComparison.OrdinalIgnoreCase)
                    || log.Contains("forge", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                Console.WriteLine("LATEST_LOG_NOT_FOUND");
            }
        }

        private static void ReadToBuffer(StreamReader reader, StringBuilder buf)
        {
            try
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    buf.AppendLine(line);
                    if (buf.Length > 4_000_000)
                        buf.Remove(0, 2_000_000);
                }
            }
            catch { }
        }

        private static string Tail(string text, int lines)
        {
            string[] all = text.Split('\n');
            int start = Math.Max(0, all.Length - lines);
            return string.Join("\n", all, start, all.Length - start);
        }

        private static bool HasStagingDirs()
        {
            string minecraftDir = LauncherPaths.MinecraftDirectory;
            if (!Directory.Exists(minecraftDir))
                return false;
            foreach (string d in Directory.EnumerateDirectories(minecraftDir))
                if (Path.GetFileName(d).StartsWith(".forge-staging-", StringComparison.Ordinal))
                    return true;
            return false;
        }
    }
}
```

- [ ] **Step 7.2：构建 CMUmcl 与 harness**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet build "D:\新启动器\CMUmcl\CMUmcl.csproj" -c Debug
& $dotnet build "D:\新启动器\.tmp\ForgeHarness\ForgeHarness.csproj" -c Debug
```

- 预期：两者 `0 Error(s)`。

- [ ] **Step 7.3：真实 Forge 安装 + 跳过 + 损坏修复（--install）**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet run --project "D:\新启动器\.tmp\ForgeHarness\ForgeHarness.csproj" -- --install
```

- 预期：`version id`、`version json exists`、`install_profile.json exists`、`.forge-complete exists`、`universal jar exists`、`universal jar sha1 == 1c2208dc…`、`no staging dirs left`、`second run returns same id (skip)`、`reinstalled after corruption`、`universal jar restored`、`no staging dirs after repair` 全部 `PASS`；末尾 `== ALL TESTS PASSED ==`。控制台可见 6 条 `运行 Forge 处理器 i/10`（i 为真实索引，客户端处理器）与 4 条 `跳过 Forge 处理器 i/10（server）`。首次安装约需下载安装器 + 依赖库 + 跑 6 个 processors（其中 `DOWNLOAD_MOJMAPS` 会从 Mojang 官方下载 mappings），耗时数分钟；若网络失败按 Step 3.1 加代理。

- [ ] **Step 7.4：端到端启动到主菜单（--launch，需已登录）**

前置：先在图形界面登录一次，使 `%LOCALAPPDATA%\CMUmcl\auth` 存在有效会话（参考 PROGRESS.md 已有登录会话）。

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet run --project "D:\新启动器\.tmp\ForgeHarness\ForgeHarness.csproj" -- --launch
```

- 预期：`VERSION_NAME=1.20.1-forge-47.4.28`；`GAME_RUNNING_AFTER_120S` + `game process running` PASS（进程 120 秒后仍在运行，表示稳定进入 Forge 启动流程，无早期崩溃）；`LATEST.LOG` 尾部含 `BootstrapLauncher`/`fml`/`forge` 关键字；`launch completed without exception` PASS。无 Forge 官方广告页进程（`java -cp` 处理器进程已随安装结束退出，仅剩游戏进程）。日志中“安装原版”仅出现一次（原版幂等，不重复下载）。

- [ ] **Step 7.5：第二次启动验证“跳过重装”**

再次运行：

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet run --project "D:\新启动器\.tmp\ForgeHarness\ForgeHarness.csproj" -- --launch
```

- 预期：控制台出现 `Forge 1.20.1-forge-47.4.28 已安装，跳过重装。`，且不再出现 `运行 Forge 处理器`（无 processors 重跑）；进程仍 `GAME_RUNNING_AFTER_120S`。

- [ ] **Step 7.6：实现者自查**

核对验收：`versions\1.20.1-forge-47.4.28\1.20.1-forge-47.4.28.json` 存在且 `inheritsFrom=1.20.1`、`libraries` 含 `net.minecraftforge:forge:1.20.1-47.4.28:universal` 与 `fmlcore`/`fmlloader`；`libraries\net\minecraftforge\forge\1.20.1-47.4.28\forge-1.20.1-47.4.28-universal.jar` SHA-1 = `1c2208dc1db2a61ddb6d5dcbe8eae4fd44629e9d`；6 个客户端 processors 执行、4 个 server 跳过；第二次启动跳过重装；删除 universal jar 后自动重装；无广告/GUI；`.forge-staging-*` 无残留。

---

## 任务 8：构建 / 打包 / 状态 / 提交推送

**Files:**
- Modify: `D:\新启动器\docs\PROGRESS.md`

- [ ] **Step 8.1：最终构建（Release 与 Debug 0 错误）**

```powershell
$env:TEMP = "D:\新启动器\.tmp"
$env:TMP = "D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME = "D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"
$dotnet = "C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet build "D:\新启动器\CMUmcl\CMUmcl.csproj" -c Release
& $dotnet build "D:\新启动器\CMUmcl\CMUmcl.csproj" -c Debug
```

- 预期：两者末尾均含 `生成成功。`/`Build succeeded` 且 `0 Error(s)`（允许既有 2 个 NU1510 警告）。

- [ ] **Step 8.2：生成 step4 源码 zip（含 LICENSE/NOTICE/README，供分发）**

```powershell
$dist = "D:\新启动器\dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$srcZip = "$dist\CMUmcl-step4-source.zip"
if (Test-Path -LiteralPath $srcZip) { Remove-Item -LiteralPath $srcZip }
Compress-Archive -Path "D:\新启动器\CMUmcl","D:\新启动器\LICENSE","D:\新启动器\NOTICE" -DestinationPath $srcZip
Get-Item -LiteralPath $srcZip | Select-Object Name, Length
```

- 预期：打印 `CMUmcl-step4-source.zip` 及其 `Length`（>0）。该 zip 内含 `CMUmcl\`、`LICENSE`、`NOTICE`，满足“分发需附源码 + LICENSE/NOTICE”。

- [ ] **Step 8.3：更新 `docs\PROGRESS.md`**

用编辑工具将以下旧文本：

```markdown
- [ ] **5. 加载器扩展**：Forge / NeoForge / Quilt 安装、启动、Java 上下限
```

替换为：

```markdown
- [ ] **5. 加载器扩展**：Forge / NeoForge / Quilt 安装、启动、Java 上下限
  - [x] **5.1 Forge**：`install_profile` 现代安装器（1.20.1 + 47.4.28）解析、合并库、内嵌工件、6 个客户端 processors、版本档案落盘、跳过重装、损坏自修复；`forge-test` 端到端启动到主菜单；GPL-3.0-only + LICENSE/NOTICE；未做 NeoForge/Quilt/旧版 Forge/Forge 版本列表 UI。
```

并将“一句话状态”行：

```markdown
CMUmcl 已能登录、拉取签名整合包、安装、按 Minecraft 版本选择 Java，并在缺少兼容 Java 时自动下载 Mojang runtime；26.3 已实际启动到主菜单。自包含 win-x64 包已生成并验证独立启动。自动下载已加 3 次重试。下一步做 Forge / NeoForge / Quilt 支持。
```

替换为：

```markdown
CMUmcl 已能登录、拉取签名整合包、安装、按 Minecraft 版本选择 Java，并在缺少兼容 Java 时自动下载 Mojang runtime；26.3 已实际启动到主菜单。自包含 win-x64 包已生成并验证独立启动。自动下载已加 3 次重试。Forge（1.20.1 + 47.4.28）已通过 `forge-test` 实例端到端安装并启动到主菜单，采用 GPL-3.0-only 移植/重实现 Axolotl/YSUMCL，附 LICENSE/NOTICE。下一步做 NeoForge / Quilt / 旧版 Forge。
```

- [ ] **Step 8.4：核对工作区（不提交数据/密钥）**

```powershell
cd "D:\新启动器"
git status --short
```

- 预期：仅出现以下新增/修改，无 `admin-credentials.txt`、`CMUmclData`、`.tmp`、`dist`、`PCL-CE`、`Axolotl*`、`.nuget`、`.dotnet-home` 等数据/密钥/第三方目录：
  - ` M .gitignore`、` M CMUmcl/Launch/LaunchService.cs`、` M CMUmcl/PackCardViewModel.cs`、` M docs/PROGRESS.md`
  - `?? LICENSE`、`?? NOTICE`、`?? CMUmcl/Launch/Forge/`、`?? CMUmcl/README-step4.md`、`?? docs/superpowers/plans/2026-10-28-forge-loader-plan.md`
  - 注：`docs/superpowers/specs/2026-10-28-forge-loader-design.md`（规格）也可一并提交。
  若 `git status` 出现意外文件，用 `git restore <path>` 或从暂存区排除，绝不提交数据/密钥。

- [ ] **Step 8.5：提交全部改动**

```powershell
cd "D:\新启动器"
git add ".gitignore" "LICENSE" "NOTICE" "CMUmcl" "docs"
git commit -m "feat(forge): add Forge 1.20.1 installer and launch (GPL-3.0-only)"
```

- 预期：`git log --oneline -1` 显示该提交。`git show --stat HEAD` 列出的文件不含数据/密钥/第三方目录。

- [ ] **Step 8.6：推送到远程（本机代理）**

```powershell
cd "D:\新启动器"
git -c http.proxy=http://127.0.0.1:7890 -c https.proxy=http://127.0.0.1:7890 push origin main
```

- 预期：输出 `Writing objects: 100%` 或 `Everything up-to-date`，无 `fatal`。若代理未启动导致卡住，先启动代理（127.0.0.1:7890）后重试。

- [ ] **Step 8.7：实现者自查（最终验收对照）**

逐条核对规格“验证/测试”第 1–8 条：①`forge-test` 本地合成实例；②启动流程（原版→Java→Forge 安装→版本档案→补依赖→启动）；③第二次跳过重装；④版本档案 `inheritsFrom=1.20.1`、`libraries` 含 `forge:universal`/`fmlcore`/`fmlloader`、universal jar SHA-1 = `1c2208dc1db2a61ddb6d5dcbe8eae4fd44629e9d`；⑤无广告/无交互 GUI、不重复装原版；⑥损坏自修复；⑦失败清理（断网/哈希不符/处理器非 0/取消均清理 `.forge-staging-*`）；⑧`dotnet build` 0 错误。全部满足后本计划完成。

---

## 自审记录（plan writer）

- **规格覆盖**：每个规格章节均映射到任务——许可证与合规（任务 1）、`ForgeModels`/MavenArtifact（任务 2）、`ForgeInstallerParser`（任务 3）、`ForgeProcessorRunner`（任务 4）、`ForgeLoaderInstaller`（任务 5）、LaunchService 集成（任务 6）、合成 `forge-test` 端到端（任务 7）、构建/打包/状态/推送（任务 8）。错误处理中文消息、安全白名单/哈希/zip-slip/暂存清理、跳过重装、Java 顺序调整均在对应任务实现与校验。
- **占位符扫描**：无 `TBD`/`TODO`/`implement X`/省略号占位；所有创建文件给出完整内容，所有编辑给出精确 old/new 字符串，所有命令给出精确命令与预期输出。
- **类型一致性**：`ForgeLoaderInstaller.InstallAsync(string,string,JavaInfo,MinecraftPath,IProgress<LaunchProgress>?,CancellationToken)`、`ForgeInstallerParser.Parse(byte[],string)`、`ForgeProcessorRunner.RunAsync(...)`/`ShouldRun`/`BuildDataMap`/`BuildClientDataMap`/`ValidateOutputs`/`ResolveProcessorArg`/`BuildClassPath`/`ResolveLibraryPath`/`ResolveDataValue`/`StripQuotes`/`GetProcessorMainClass` 等签名在任务 2–7 间保持一致；`MinecraftPath` 使用 `BasePath`/`Library`（已反射确认，非 `Libraries`）。
- **合规确认**：`.gitignore` 放行根级 `LICENSE`/`NOTICE`（Step 1.3 + Step 1.5 校验）；不引入 CmlLib.Core.Installer.Forge；不复制 PCL；新 Forge 文件均带 SPDX GPL-3.0-only 头注。
