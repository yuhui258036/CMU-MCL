# Forge 加载器安装与启动设计（CMUmcl）

日期：2026-10-28
范围：仅 CMUmcl（WPF / .NET 10 / CmlLib.Core 4.1.0）客户端。本文件是设计规格，不改动任何产品代码、不包含实现计划。

---

## 目标

在现有“原版 / Fabric”启动能力之上，接入现代 Forge（`install_profile` 形式）的安装与启动：给定实例元数据中的 Minecraft 基础版本（1.20.1）与 Forge 加载器版本（47.4.28），自动从 Forge Maven 下载并校验安装器、解析 `install_profile.json` 与 `version.json`、安装库与内嵌工件、用已选 Java 按序运行 processors、落盘合并后的 Forge 版本档案，并交由 CmlLib.Core 下载剩余依赖并启动到游戏。实现方式为在 GPL-3.0-only 许可下移植 / 重实现 YSUMCL/Axolotl 的 Forge 安装器逻辑（route 2），不引入 CmlLib.Core.Installer.Forge，不复制任何 PCL 代码。第一版只覆盖 Forge 1.20.1 现代安装器，验证实例为本地合成的 `forge-test`（MC 1.20.1 + Forge 47.4.28，无 mods，revision 1），不依赖任何线上整合包。

## 非目标

- 不实现 NeoForge / Quilt / 旧版 Forge（1.12 及更早的 `versionInfo` / `install` 传统安装流程）。本轮遇到它们仍抛出清晰错误。
- 不引入或调用 `CmlLib.Core.Installer.Forge`，也不使用 CmlLib 的任何 Forge 高级安装器 / 广告页 / 交互式 GUI（无 Forge 官方下载页广告）。
- 不复制 PCL 代码或 PCL 品牌；PCL-CE 核心采用 PCL Limited Distribution License，禁止纳入。
- 不实现 Forge 版本列表 UI / 手动选择 Forge 版本；加载器版本完全来自实例元数据（`loaderVersion`）。
- 不解析 Forge 版本档案中声明的专属 Java 上下限（本轮沿用基础版本 1.20.1 的 `javaVersion.majorVersion = 17`；Forge 1.20.1 需 Java 17，本机选择器取“最低 ≥ 17”即 jdk-21，见“风险”）。
- 不改动登录校验、authlib-injector 注入、内存参数（512/4096）、实例目录 `game_directory`、服务器快速进入（quick-play）、Java 本地扫描与自动下载的既有行为。
- 不做“手动指定 Java 的 UI”或“全局/每实例 Java 管理”。

## 后续阶段（不在本轮）

- NeoForge / Quilt 启动、旧版 Forge（`install`/`versionInfo`）支持。
- Forge 版本档案专属 Java 上下限解析（1.20.2+ / NeoForge 的 `javaVersion` 覆盖）。
- Forge 版本列表（Maven `maven-metadata.xml` + `promotions_slim.json`）解析与 UI 展示；本轮直接由 `(gameVersion, loaderVersion)` 拼 URL，不拉取 promotions。

## 背景 / 现状

- 当前 `Launch\LaunchService.cs` 第 8 步的 `switch` 只处理 `vanilla`/空 与 `fabric`，`forge`/`neoforge`/`quilt` 一律抛 `UnsupportedLoaderMessage`（“当前版本先支持原版/Fabric，Forge/NeoForge 下一步接入。”）。
- Java 选择已实现：`JavaVersionResolver`（读基础版本 JSON 的 `javaVersion.majorVersion` + `component`）、`JavaLocator`（扫描 + `Select`）、`MojangRuntimeInstaller`（自动下载兜底）。当前 Java 选择发生在第 8.5 步（加载器解析之后）。
- CmlLib.Core 4.1.0 作为原版/Fabric 的基础安装器与启动器；已确认其 `MinecraftProcessBuilder` 支持 `${library_directory}`、`${classpath_separator}`、`${classpath}` 变量替换、`ConcatInheritedJvmArguments`（继承合并 JVM 参数）以及 `TryAddClassPath`（缺失 `-cp` 时补齐），且自带 Forge 式 `-p` 模块路径的单元测试用例，因此可以直接承接本设计写出的 Forge 版本档案的启动。
- 参考实现（GPL-3.0-only，仅作行为/结构参照，不复制 PCL）：
  - `Axolotl-YSUMCL\packages\app-lib\src\api\loader_metadata.rs`：Forge 元数据（约 910–1013）、安装器解析（约 1701–2050）、`InstallerProfile`/`Processor`/`SidedDataEntry` 结构（约 155–183）、Forge URL 常量（约 41–49）。
  - `Axolotl-YSUMCL\packages\daedalus\src\modded.rs`：`PartialVersionInfo`/`Processor`/`SidedDataEntry`；`daedalus\src\lib.rs` 的 `get_path_from_artifact`（Maven 坐标 → 相对路径）。
  - `Axolotl-YSUMCL\packages\app-lib\src\launcher\mod.rs`（约 1416–1653）与 `launcher\args.rs`（约 591–689）：processors 执行、data map 构建、`{KEY}`/`[coordinate]` 参数解析、主类读取。
- 已实测 Forge 1.20.1 安装器（`forge-1.20.1-47.4.28-installer.jar`，8,859,760 字节）内部结构，本轮设计的字段/流程均基于该真实文件，关键事实如下：
  - `install_profile.json`：`spec=1`、`profile=forge`、`version=1.20.1-forge-47.4.28`、`path=null`、`minecraft=1.20.1`；`data` 含 `MAPPINGS`/`MOJMAPS`/`MERGED_MAPPINGS`/`BINPATCH`/`MC_UNPACKED`/`MC_SLIM`/`MC_EXTRA`/`MC_SRG`/`PATCHED` 及 `*_SHA`、`MCP_VERSION`；10 个 processors；`libraries` 同时含“安装器依赖”（installertools、jarsplitter、binarypatcher、ForgeAutoRenamingTool、srgutils、asm、gson 等）与**运行时 Forge 库**（`net.minecraftforge:fmlcore`、`javafmllanguage`、`lowcodelanguage`、`mclanguage`、`net.minecraftforge:forge:1.20.1-47.4.28:universal`）。
  - `version.json`：`id=1.20.1-forge-47.4.28`、`inheritsFrom=1.20.1`、`mainClass=cpw.mods.bootstraplauncher.BootstrapLauncher`；`arguments.game`（`--launchTarget forgeclient …`）与 `arguments.jvm`（`-DlibraryDirectory=${library_directory}`、`-p` 模块路径、`--add-opens` 等）；`libraries` 是**游戏运行库**（securejarhandler、bootstraplauncher、asm 9.9.1、accesstransformers、modlauncher、unsafe、mixin、fmlloader、fmlearlydisplay、JarJar* 等），但**缺少** `fmlcore`/`javafmllanguage`/`lowcodelanguage`/`mclanguage`/`forge:universal`——这些只出现在 `install_profile.libraries`。
  - 关键结论（Task 7 端到端验证修正）：最终版本档案的 `libraries` 必须是 `version.json.libraries` 加上 `install_profile.libraries` 中**仅运行时 Forge 库**的过滤并集，而不是把 `install_profile.libraries` 全量合并。原因：`fmlcore`/`javafmllanguage`/`lowcodelanguage`/`mclanguage`/`forge:universal` 等运行时库只出现在 `install_profile.libraries`（`version.json` 缺失它们），但 `install_profile.libraries` 同时含 installertools、jarsplitter、binarypatcher、ForgeAutoRenamingTool、旧版 asm/guava 等安装器专用工具；若全量并入最终版本档案，这些工具会进入游戏类路径，启动时抛 `ResolutionException`。因此筛选规则为：`install_profile.libraries` 中只保留其 Maven 版本等于 Forge 工件版本（`{gameVersion}-{loaderVersion}`，即 `1.20.1-47.4.28`）的库；这些库正好是 fmlcore/javafmllanguage/lowcodelanguage/mclanguage/forge:universal。所有 `install_profile.libraries`（含安装器工具）仍会下载以支撑 processors 运行，只是不进入最终游戏版本档案。
  - `data.BINPATCH.client` 为 `/data/client.lzma`（`/` 前缀表示内嵌于安装器 jar 的工件）；其余 `data` 值多为 `[maven:coordinate]`（指向已下载或由 processors 产生的库文件）。
  - 客户端需运行的 processors：`MCP_DATA`、`DOWNLOAD_MOJMAPS`、`MERGE_MAPPING`、`jarsplitter`(client)、`ForgeAutoRenamingTool`、`binarypatcher` 共 6 个；`EXTRACT_FILES`、两个 `BUNDLER_EXTRACT`、`jarsplitter`(server) 共 4 个因 `sides:["server"]` 跳过。
  - 本机已装 vanilla 1.20.1（`versions\1.20.1\1.20.1.jar` 23,028,853 字节、`javaVersion.majorVersion=17`、`downloads.client_mappings` 存在），`forge-test` 实例可直接复用。

## 许可证与合规

- **CMUmcl 项目整体采用 GPL-3.0-only 许可**：既包括既有 CMUmcl 代码，也包括本轮新增的 Forge 移植/重实现代码。仓库根级新增 `LICENSE`（GNU GPL-3.0-only 全文）声明项目整体许可。
- **第三方依赖保留各自许可，不受项目整体许可影响**：`CmlLib.Core` 为 MIT，`authlib-injector.jar` 等随包二进制按各自上游许可分发（在 `NOTICE` 中列明）。
- 新增 `NOTICE`（注明“本项目的 Forge 安装器逻辑移植/重实现自 Axolotl Launcher（YSUMCL fork）”，列出所参照文件：`packages/app-lib/src/api/loader_metadata.rs`、`packages/app-lib/src/launcher/mod.rs`、`packages/app-lib/src/launcher/args.rs`、`packages/daedalus/src/modded.rs`、`packages/daedalus/src/lib.rs`，并说明其 GPL-3.0-only 许可与上游版权归属）。
- 每个新增的 Forge 安装器源文件顶部携带简短头注，形如：

```csharp
// SPDX-License-Identifier: GPL-3.0-only
// 本文件移植/重实现自 Axolotl Launcher（YSUMCL fork）的 Forge 安装器逻辑
//（packages/app-lib/src/api/loader_metadata.rs 与 packages/app-lib/src/launcher/{mod,args}.rs，
//   GPL-3.0-only）。详见仓库根 LICENSE 与 NOTICE。
```

- 明确记录：开发期间仓库可保持私有；但**分发二进制必须同时提供对应源码**（GPL-3.0 第 6 条），且随包附带 LICENSE/NOTICE。该说明写入 NOTICE。
- 不引入任何 PCL 代码、PCL 字符串或 PCL 品牌；Forge 相关逻辑只以 Axolotl/YSUMCL 为 GPL 参照。
- 现状盘点：本轮之前 CMUmcl 未含 LICENSE/NOTICE；本轮新增的 `LICENSE`/`NOTICE` 覆盖**整个项目**（既有代码 + 新增 Forge 代码）。第三方依赖（`CmlLib.Core` 为 MIT、`authlib-injector.jar` 等随包二进制）保留各自许可，仅在其自身许可下随包分发。

## 架构

新增一个职责单一的 `CMUmcl\Launch\Forge\` 子目录，把 Forge 安装拆成四个可独立测试的组件，`LaunchService` 只做一次薄调用：

1. **ForgeModels.cs** — 纯数据模型 + Maven 坐标解析：
   - `InstallerProfile { string? Minecraft; Dictionary<string,SidedDataEntry> Data; List<ForgeProcessor> Processors; List<ForgeLibrary> Libraries; ForgeInstall? Install; }`
   - `ForgeProcessor { string Jar; List<string> Classpath; List<string> Args; Dictionary<string,string>? Outputs; List<string>? Sides; }`
   - `SidedDataEntry { string Client; string Server; }`
   - `ForgeLibrary { string Name; ForgeLibraryDownloads? Downloads; }`、`ForgeLibraryDownloads { ForgeLibraryDownload? Artifact; }`、`ForgeLibraryDownload { string? Path; string Url; string? Sha1; long Size; }`
   - `ForgeVersionJson`（部分版本档案：`Id`、`InheritsFrom`、`MainClass`、`Arguments`、`Libraries`、`Logging`、`ReleaseTime`、`Time`、`Type`）。
   - 静态 `MavenArtifact`：把 `group:artifact:version[:classifier][@ext]` 解析成 `(Group, Artifact, Version, Classifier, Extension)` 与库相对路径（`get_path_from_artifact` 的移植：`group.replace('.','/')/artifact/version/artifact-version[-classifier].ext`，无 classifier 且无 `@ext` 时扩展名取 `jar`）。
2. **ForgeInstallerParser.cs** — 从安装器 jar 读 `install_profile.json` 与 `version.json`，按“运行时库过滤”规则合并库（见下方 install_profile 解析一节）、提取内嵌工件、产出最终版本档案文档。纯逻辑（输入字节数组 + 目录），可单测。
3. **ForgeProcessorRunner.cs** — 用已选 Java 按序运行 processors，负责 data map 构建、`{KEY}`/`[coordinate]` 参数解析、主类读取、退出码与 outputs 校验。可对参数解析/主类读取做纯函数级单测。
4. **ForgeLoaderInstaller.cs** — 编排器：解析实例元数据 → 解析安装器 URL → 下载 + SHA-1 校验安装器 jar → 调 parser → 下载安装器库 → 调 processor runner → 写版本档案 + 标记 → 复用/跳过判定。持有 `HttpClient`（`IDisposable`），与现有 `MojangRuntimeInstaller`/`PackInstaller` 一致。

`LaunchService` 在 `switch` 中新增 `case "forge"`：`versionName = await ForgeLoaderInstaller.InstallAsync(gameVersion, loaderVersion, java, launcher.MinecraftPath, progress, ct)`。Forge 安装器只把版本档案与库写入 CmlLib 共享目录，启动仍由 `MinecraftLauncher.InstallAsync(versionName)` + `BuildProcessAsync(versionName, option)` 完成（与 Fabric 同路径）。

### 启动流程调整（Java 顺序 + Forge 接线）

当前 `LaunchService.LaunchAsync` 顺序：7 装原版 → 8 解析加载器 → 8.5 选 Java → 9/10/11 建进程。调整为：

1. 步骤 1–6 不变（revision 校验、读实例、会话校验、authlib、建 `MinecraftPath`/`MinecraftLauncher`）。
2. 步骤 7：`launcher.InstallAsync(gameVersion, …)` 装原版（不变）。
3. **步骤 7.5（前移）**：Java 选择——把现 8.5 的逻辑整体移到加载器解析**之前**。原因：Forge 的 processors 需要先有 Java 才能运行，且原版安装完成即可读 `versions\{gameVersion}\{gameVersion}.json` 解析需求。对 vanilla/Fabric 无行为差异（其需求同样只依赖 `gameVersion`）。选择结果 `java` 存为局部变量。
4. 步骤 8：`switch` 中 `forge` 分支调用 `ForgeLoaderInstaller.InstallAsync(...)`（内部用 `java` 的**控制台 java.exe** 跑 processors），得到 `versionName = "1.20.1-forge-47.4.28"`；`fabric`/`vanilla` 分支不变；`neoforge`/`quilt` 仍抛 `UnsupportedLoaderMessage`。
5. 步骤 9/10/11：`MSession`、`MLaunchOption`（`JavaPath = java.JavaPath` 即 javaw.exe）、`BuildProcessAsync(versionName, option)`、`process.Start()` 不变。

控制台 java 派生：`JavaInfo.JavaPath` 恒为 `{JDK 根}\bin\javaw.exe`；processors 需要控制台进程以便捕获输出，取 `Path.Combine(Path.GetDirectoryName(java.JavaPath), "java.exe")`，若不存在则回退 `java.JavaPath`。启动游戏仍用 `javaw.exe`（无控制台窗口）。

## Forge 版本解析（resolution）

- 输入：`gameVersion`（`InstanceMetadata.Game`）与 `loaderVersion`（`InstanceMetadata.LoaderVersion`）。
- 安装器 URL 由 Maven 坐标直拼（本轮不做 promotions/maven-metadata 解析）：

```
https://maven.minecraftforge.net/net/minecraftforge/forge/{mc}-{forge}/forge-{mc}-{forge}-installer.jar
```

  例：`…/forge/1.20.1-47.4.28/forge-1.20.1-47.4.28-installer.jar`。
- SHA-1 取自同路径的 `forge-{mc}-{forge}-installer.jar.sha1`（Maven 惯例），下载安装器后必须校验；`.sha1` 拉取失败或哈希不符 → 中止并清理。
- 安装器 jar 下载到 `{MinecraftDirectory}\.forge-staging-{guid}\installer.jar`；解析在内存完成，`INSTALLER` 指向该文件，安装成功或失败后随暂存目录一并清理。
- 版本 id 固定为 `{gameVersion}-forge-{loaderVersion}`（即 `1.20.1-forge-47.4.28`），与 version.json 自身 `id` 一致；落盘时**强制**使用该 id，不信任 jar 内 `id`。
- 若 `loaderVersion` 为空或 `gameVersion` 为空 → 抛“缺少 Forge 版本信息”类错误；不猜测版本。

## install_profile 解析（ForgeInstallerParser）

输入：安装器 jar 字节（内存 `ZipArchive`）与安装器 URL；输出：`ParsedForgeInstaller { ForgeVersionJson VersionJson; List<ForgeLibrary> Libraries; List<ForgeProcessor> Processors; Dictionary<string,SidedDataEntry> Data; List<EmbeddedArtifact> Embedded; }`。

1. 读 `install_profile.json` 与 `version.json`（读 `version.json`；若缺失读 `json` 字段指向的路径；若两者皆无且存在内联 `versionInfo` → 判为旧版，抛清晰错误“暂不支持该 Forge 版本（旧版安装流程）”）。
2. `version.json` 若缺 `inheritsFrom`，回填 `install_profile.minecraft`（对应 Axolotl `parse_installer` 的 `inheritsFrom` 补写）。
3. 合并库（运行时库过滤，Task 7 验证结论）：`最终库 = version.json.libraries ∪ (install_profile.libraries 中 Maven 版本 == {gameVersion}-{loaderVersion} 的运行时 Forge 库)`，按 `name` 精确去重（首个出现的条目保留其下载元数据）。过滤规则只保留其 Maven 版本等于 Forge 工件版本（`1.20.1-47.4.28`）的库，即 `fmlcore`/`javafmllanguage`/`lowcodelanguage`/`mclanguage`/`forge:universal`；installertools、ForgeAutoRenamingTool、binarypatcher、旧版 asm/guava 等安装器专用工具因版本不同被排除在最终版本档案之外（否则进入游戏类路径会导致启动 `ResolutionException`）。合并结果必须包含 `fmlcore`/`javafmllanguage`/`lowcodelanguage`/`mclanguage`/`forge:universal`，否则启动必失败。全部 `install_profile.libraries`（含被排除的安装器工具）仍在“库下载”阶段下载，以支撑 processors 运行。
4. 内嵌工件提取（`data` 值以 `/` 开头，如 `BINPATCH.client = "/data/client.lzma"`）：
   - 从 jar 读取该路径的字节，写入 `{MinecraftDirectory}\.forge-staging-{guid}` 暂存目录下的同名相对路径（暂存目录随安装结束清理）；
   - 把该 `data` 值改写为该暂存文件的**绝对路径**。本轮采用直写绝对路径，不引入 Axolotl 式 `com.axolotl.loader-installer:embedded:…` 合成坐标（processors 只跑一次、不惰性复现，无需坐标间接层）。解析器对处理器参数仍要识别三种表示：`[coordinate]` → 库路径、`{KEY}` → data 值（值为 `[coordinate]` 时先解析为库路径）、其余 → 字面量。
   - 对 jar 中 `maven/**` 条目（如旧式内嵌 maven 工件）本轮不做通用扫描（1.20.1 仅用 `/data/*.lzma`）；如后续版本需要再补。
5. 最终版本档案文档 = version.json 内容 + 强制 `id` + 合并后的 `libraries`（每个库保留 `name` 与 `downloads`），**丢弃** `data`/`processors` 字段（CmlLib 不需要、避免解析未知字段）。`mainClass`/`arguments`/`logging`/`releaseTime`/`time`/`type` 原样保留。

## 库下载与内嵌工件落盘

- **安装器库**（`install_profile.libraries` 全部条目，含 installertools/ForgeAutoRenamingTool/binarypatcher 等安装器专用工具与 processors 的 `jar`/`classpath` 引用的坐标）必须在本机运行 processors **之前**全部落盘到 `{MinecraftDirectory}\libraries`。注意：这里下载的是 `install_profile.libraries` 全集，与“最终版本档案只保留运行时 Forge 库”是两回事——安装器专用工具仅供 processors 运行，不进入最终游戏版本档案：
  - 对每个库按 `downloads.artifact.url` 下载到 `downloads.artifact.path`（或由 `name` 坐标推导的相对路径）；
   - 全部 HTTPS，且主机必须在白名单内：`maven.minecraftforge.net`、`files.minecraftforge.net`、`libraries.minecraft.net`、`repo1.maven.org`、`repo.maven.apache.org`（1.20.1 的安装器库实际只用 `maven.minecraftforge.net` 与 `libraries.minecraft.net` 两个主机；`files.minecraftforge.net` 仅在未启用的 mirrorList 中出现）；每个库必须有 `sha1`，下载后流式校验 SHA-1 与 size，不符 → 清理并中止；
  - 已存在且 SHA-1 匹配的文件跳过下载（幂等）。
- **版本库**（最终版本档案中的游戏运行库，如 securejarhandler、bootstraplauncher、asm 9.9.1 等）交给 CmlLib 的 `InstallAsync(versionName)` 下载（它们都带标准 `downloads.artifact.url`/`sha1`），Forge 安装器不重复下载。
- **内嵌工件**（`/data/client.lzma` 等）由 parser 提取到暂存目录，processor 运行时经 `{BINPATCH}` 引用其绝对路径；安装全部成功后随暂存目录清理。注意区分：内嵌工件是 processor 的**输入**（仅安装期需要、放暂存），而 processor 的 **outputs**（MC_SLIM/MC_EXTRA/MC_SRG/PATCHED/mappings）由 processors 写入库目录、长期保留。
- 路径防护：对每个库相对路径与内嵌条目路径做规范化（拒绝 `..`/`.`/根路径/非法字符），`Path.GetFullPath` 后必须仍在库目录或暂存目录之下（复用 `MojangRuntimeInstaller.SanitizeRelativePath`/`ResolveUnder` 的同一套规则，抽为共享助手或复制同语义实现）。

## processor 执行（ForgeProcessorRunner）

- **data map 构建**（客户端侧固定注入，叠加 `install_profile.data` 的 client 值）：
  - `SIDE = "client"`
  - `MINECRAFT_JAR = {MinecraftDirectory}\versions\{gameVersion}\{gameVersion}.jar`（执行前校验存在）
  - `MINECRAFT_VERSION = gameVersion`
  - `ROOT = {MinecraftDirectory}`
  - `INSTALLER = 安装器 jar 的绝对路径`
  - `LIBRARY_DIR = {MinecraftDirectory}\libraries`
  - 以及 `install_profile.data` 每个键的 `client` 值（其中 `[coordinate]` 在替换时解析为库路径）。
- **顺序与 side 过滤**：按 `processors` 数组顺序执行；跳过 `sides` 存在且不含 `"client"` 的处理器（`sides` 缺失视为两端都跑）。1.20.1 客户端跑 6 个、跳 4 个。
- **命令行**：`java -cp <classpath> <Main-Class> <args…>`，其中：
  - classpath = `processor.classpath` 各坐标 + `processor.jar` 坐标，按库路径用 `;`（Windows）拼接；
  - 主类 = 打开 `processor.jar` 对应的库文件，读 `META-INF/MANIFEST.MF` 的 `Main-Class` 行（移植 Axolotl `get_processor_main_class`）；缺失 → 抛“找不到处理器主类”；
  - `args` 逐项解析（移植 `get_processor_arguments`）：整项为 `[coordinate]` → 库路径；否则对每个 `{KEY}` 用 data 值替换（data 值若为 `[coordinate]` 先解析为库路径，否则取字面量；data 值带 `'…'`/`"…"` 引号时先剥引号）；
  - 工作目录设为 `ROOT`（与 Forge 官方安装器一致，避免相对路径产物落错位置）；
  - 用 `ProcessStartInfo`：`UseShellExecute=false`、重定向 stdout/stderr、`CreateNoWindow=true`；**实时流式捕获**并记录到日志（启动进度 `Message` 里只放阶段摘要，不刷屏），超时与取消受 `CancellationToken` 控制；
  - 退出码非 0 → 收集 stderr 尾部，抛“Forge 处理器执行失败（{jar}）：{stderr 摘要}”。
- **outputs 校验**：处理器若声明 `outputs`（如 jarsplitter 的 `{MC_SLIM} → {MC_SLIM_SHA}`、binarypatcher 隐式 `--output`），执行后解析输出路径与期望 SHA-1（`{…_SHA}` 值剥引号），校验文件存在且 SHA-1 匹配；不匹配 → 抛错。该逻辑同时用于“跳过重装”的本地校验。
- **DOWNLOAD_MOJMAPS 处理器**：该处理器（installertools 的 `--task DOWNLOAD_MOJMAPS`）会在其子 JVM 内自行从 Mojang 官方下载 1.20.1 的 mojmap（`client.txt`）写到 `{MOJMAPS}`。本轮**直接执行**该处理器（忠实移植）；其网络访问目标为 Mojang 官方（与 vanilla 安装同信任域），不受我方 Maven 白名单约束。后续可优化为：读取已装 vanilla 1.20.1 的 `downloads.client_mappings` 预先落盘并跳过该处理器（本轮不做，避免过度设计）。

## 版本档案落盘与“跳过重装”

- 落盘 `{MinecraftDirectory}\versions\{id}\{id}.json`（合并后的版本档案）。
- 同时落盘 `{versions}\{id}\install_profile.json`（原始安装器 profile，供跳过校验）与 `{versions}\{id}\.forge-complete`（单行，内容为安装器 jar 的 SHA-1）。
- **跳过重装判定**（不信任目录名，必须本地校验真实文件）：仅当以下**全部**满足才跳过：
  1. `{id}.json` 存在且可解析，且 `inheritsFrom == gameVersion`；
  2. `install_profile.json` 与 `.forge-complete` 存在，且 `.forge-complete` 记录的安装器 SHA-1 等于“由 (gameVersion, loaderVersion) 确定版本”所对应的已知值（用安装时记录的 SHA-1，不需联网重取）；
  3. 由本地 `install_profile.json` 重新合并出的库清单中，每个库文件都存在，且其中声明了 `sha1` 的库（尤其 `forge:universal`、`fmlcore`、`fmlloader`）SHA-1 匹配；
  4. 本地 `install_profile.json` 声明的 processors 的 `outputs` 文件存在且 SHA-1 匹配。
  - 任一项不满足 → 重新走完整安装（重新下载安装器、重跑 processors，幂等覆盖）。
- 安装成功后刷新 CmlLib 视图：`LaunchService` 每次启动新建 `MinecraftLauncher(minecraftPath)`，其 `InstallAsync(versionName)` 直接从磁盘读取刚写入的版本档案，无需额外刷新调用；仅需保证“写版本档案”先于“`InstallAsync(versionName)`”。

## 错误处理（中文 `LaunchException`）

- Forge 版本信息缺失/空 → “缺少 Forge 版本信息，无法安装该加载器。”
- 安装器下载失败 / HTTP 非 2xx / `.sha1` 缺失 / 哈希或大小不符 → “下载 Forge 安装器失败（{原因}），已中止。” 并清理暂存。
- 解析失败（缺 `install_profile.json`、缺 `version.json`、JSON 非法、`inheritsFrom` 缺失且无 `minecraft`）→ “解析 Forge 安装器失败（{原因}）。当前版本仅支持 Forge 1.20.1 现代安装器。”
- 旧版安装流程（内联 `versionInfo` 或 `install` 字段）→ “暂不支持该 Forge 版本的旧版安装流程，请使用 1.20.1 或更新版本。”
- 库下载失败 / 白名单外 URL / 哈希不符 → “安装 Forge 依赖库失败（{坐标}），已中止。” 并清理。
- 内嵌条目路径越界 → “检测到 Forge 安装器内非法路径：{path}。” 并中止。
- processor 主类缺失 / 退出码非 0 / outputs 校验失败 → “Forge 处理器执行失败（{jar}）。{stderr 摘要}” 并清理。
- 所有失败统一清理 `.forge-staging-*` 暂存目录与半成品（已写库文件不动，靠哈希幂等下次补），抛 `LaunchException`，由 `MainWindow` 统一弹窗。
- 全程遵守 `CancellationToken`；取消 → 清理暂存、抛“已取消”。

## 安全

- 下载白名单：安装器 jar 与安装器库仅允许 `maven.minecraftforge.net`、`files.minecraftforge.net`、`libraries.minecraft.net`、`repo1.maven.org`、`repo.maven.apache.org` 的绝对 HTTPS URL；`HttpClientHandler` 允许重定向但重定向后校验最终 URI 主机仍在白名单。
- 哈希：安装器 jar 校验 `.sha1`；每个安装器库校验 `downloads.artifact.sha1` 与 size（流式 `IncrementalHash` + 字节计数，参照 `MojangRuntimeInstaller`/`PackInstaller`）。
- 路径：库相对路径与内嵌条目做 zip-slip 防护（拒绝 `..`/`.`/根/非法字符 + 根目录校验）。
- 不执行任何下载内容（安装器 jar 只解压解析，绝不运行其 `net/minecraftforge/installer/*` 主类）；唯一执行的 Java 是“我方用已选 Java 运行 processors”，其 classpath/参数完全来自我方解析的 `install_profile.json`。
- 无 Forge 官方下载页广告、无交互式安装 GUI、无 `-Xmx` 之外的任何额外 JVM 参数注入。
- 暂存清理：`.forge-staging-{guid}` 失败即删；成功后删暂存（内嵌输入工件只在此暂存，processor 输出在库目录、不受清理影响）。

## 文件改动清单

新增（`CMUmcl\Launch\Forge\`）：

- `ForgeModels.cs`：DTO + `MavenArtifact` 坐标解析（含 `get_path_from_artifact` 移植）。
- `ForgeInstallerParser.cs`：`parse_installer` 移植（读两 JSON、合并库、提取内嵌工件、产出最终版本档案文档）。
- `ForgeProcessorRunner.cs`：processor 执行 + data map + `get_processor_arguments`/`get_processor_main_class` 移植 + outputs 校验。
- `ForgeLoaderInstaller.cs`：编排（URL 解析、下载校验、库安装、调用 parser/runner、落盘、跳过判定、清理）。

修改：

- `CMUmcl\Launch\LaunchService.cs`：新增 `case "forge"`；Java 选择前移到原版安装之后、加载器解析之前（步骤 7.5）；`neoforge`/`quilt` 仍抛原错误。
- `CMUmcl\PackCardViewModel.cs`：`HasLoaderNote` 分支把 `forge` 从“当前先支持原版/Fabric”提示中移除（仅 `neoforge`/`quilt` 保留提示）。
- `CMUmcl\Content\LauncherPaths.cs`（可选）：如需要，新增 `CreateForgeStagingDirectory()`（`{MinecraftDirectory}\.forge-staging-{guid}`）与 `ForgeVersionDirectory(id)` 助手；否则路径在 `ForgeLoaderInstaller` 内拼 `MinecraftDirectory` 即可，不改此文件。

新增（许可证）：

- 仓库根 `LICENSE`（GPL-3.0-only 全文）。
- 仓库根 `NOTICE`（Axolotl/YSUMCL 移植来源与 GPL 说明 + “分发需附源码”声明）。

不改动其他任何产品文件；不新增 NuGet 依赖（复用 `System.IO.Compression`、`System.Text.Json`、`System.Security.Cryptography`）。

## 验证 / 测试

验收标准（全部通过）：

1. 构造本地合成实例 `forge-test`（`instances\forge-test\instance.json`：`game=1.20.1`、`loader=forge`、`loaderVersion=47.4.28`、`revision=1`、`serverAddress=127.0.0.1`，`installed.json` 记 revision 1，无 mods），不依赖线上整合包。
2. 点“启动游戏”后：装原版 1.20.1（幂等，不重复下载已装内容）→ 选 Java（1.20.1 需 17，本机选最低 ≥17 = jdk-21）→ Forge 安装（下载安装器、校验、解析、下载安装器库、跑 6 个客户端 processors）→ 写 `1.20.1-forge-47.4.28` 版本档案 → `InstallAsync` 补版本库 → 启动进程；日志显示 Forge 加载并进入主菜单（或至少稳定进入 Forge 启动流程、无启动崩溃日志）。
3. 第二次启动：触发“跳过重装”判定，不再重跑 processors（日志可见跳过），仍能正常启动。
4. 校验产物：`versions\1.20.1-forge-47.4.28\1.20.1-forge-47.4.28.json` 存在且 `inheritsFrom=1.20.1`、`libraries` 共 34 条，含 `net.minecraftforge:forge:1.20.1-47.4.28:universal` 与 `fmlcore`/`fmlloader`，且**不含** installertools/ForgeAutoRenamingTool/binarypatcher 等安装器专用工具；`libraries\net\minecraftforge\forge\1.20.1-47.4.28\forge-1.20.1-47.4.28-universal.jar` 存在且 SHA-1 = `1c2208dc1db2a61ddb6d5dcbe8eae4fd44629e9d`。
5. 无 Forge 广告页 / 无交互 GUI 进程；不重复装原版（仅一次原版安装日志）。
6. 人为破坏（删除 forge universal jar、或改坏版本档案 `inheritsFrom`、或改坏 `.forge-complete`）→ 下次启动自动重装而非报错/假跳过。
7. 失败路径：断网下载安装器 / 库哈希不符 / 处理器退出码非 0 / 取消 → 抛清晰中文错误，`.forge-staging-*` 被清理，无半成品。
8. `dotnet build`（`net10.0-windows`）0 错误。

纯函数级单测（建议在计划阶段评估引入最小 xUnit 工程或手工脚本验证，因项目暂无可测试工程）：

- `MavenArtifact.ToRelativePath`：`net.minecraftforge:forge:1.20.1-47.4.28:universal` → `net/minecraftforge/forge/1.20.1-47.4.28/forge-1.20.1-47.4.28-universal.jar`；`de.oceanlabs.mcp:mcp_config:1.20.1-20230612.114412@zip` → `…/mcp_config-1.20.1-20230612.114412.zip`。
- `ResolveProcessorArg`：`[coord]` → 库路径；`{KEY}` → data 值；`{KEY}/suffix` → 值 + suffix；`{MC_SLIM_SHA}` 值 `'de86b…'` 剥引号。
- `ForgeInstallerParser`：给定 1.20.1 安装器字节，合并库含 `fmlcore`/`forge:universal` 且共 34 条、不含 installertools/ForgeAutoRenamingTool、`data.BINPATCH.client` 被改写为可解析路径、`version.json.id` 被强制为 `1.20.1-forge-47.4.28`。
- 跳过判定：满足/破坏各项条件时的真/假。
- 路径防护：`..`、根路径、非法字符被拒绝。

## 风险与缓解

1. **1.20.1 Forge 在 Java 21 上存在已知兼容问题（部分 mod 或 FML 早期日志报错）。** 缓解：本轮选择器按“最低 ≥ 17”会选 jdk-21；先以 jdk-21 跑通基线验收，若主菜单/进服失败，再评估“Forge 1.20.1 强制 17”的约束解析（已在后续阶段清单）；失败现象会体现在 Forge 启动日志，便于定位。
2. **把安装器专用库（installertools/ForgeAutoRenamingTool/binarypatcher 等）错误并入游戏类路径。** 缓解：采用“运行时库过滤”规则——`install_profile.libraries` 中只有 Maven 版本等于 Forge 工件版本（`{gameVersion}-{loaderVersion}`）的库进入最终版本档案，安装器专用工具被排除（Task 7 端到端已验证该过滤避免启动 `ResolutionException`）；安装器专用工具仍在“库下载”阶段下载供 processors 运行。验收第 4 条显式校验最终档案共 34 条且不含 installertools/ForgeAutoRenamingTool。
3. **DOWNLOAD_MOJMAPS 处理器在其子 JVM 内自下载 Mojang mappings，网络失败会导致整个安装失败。** 缓解：处理器退出码/超时被捕获并转为清晰中文错误；后续可改为“预读已装 vanilla 1.20.1 的 `downloads.client_mappings` 落盘 + 跳过该处理器”以去网络依赖。
4. **`path:null` 且 forge jar 以 `:universal` 分类器出现在 install_profile.libraries，而非 version.json.libraries。** 缓解：合并逻辑按坐标去重后天然包含 universal jar；验收第 4 条显式校验该 jar 的 SHA-1。
5. **安装器/库下载或 processors 失败留下半成品。** 缓解：全程暂存 + 哈希校验 + 失败清理；库文件写库目录靠哈希幂等补，跳过判定要求 outputs 与库哈希匹配。
6. **路径越界（zip-slip）或非白名单 URL。** 缓解：路径规范化 + 根目录校验 + HTTPS 主机白名单（安装器库与安装器 jar）；内嵌条目写入前同样校验。
7. **过度信任目录名导致跳过但产物损坏。** 缓解：跳过判定强制校验版本档案 `inheritsFrom`、库文件存在与 SHA-1、processors outputs，绝不只看目录存在。
8. **CmlLib 对 Forge 版本档案的启动兼容性。** 缓解：已核实 CmlLib.Core 4.1.0 支持 `${library_directory}`/`${classpath_separator}`/`${classpath}`、继承 JVM 参数合并与 `-p` 模块路径（含内置测试用例）；验收以真实启动到主菜单为准，若仍不兼容再在计划阶段评估补丁（非本轮设计假设）。

## 决策摘要

- 采用 route 2：在 GPL-3.0-only 下移植/重实现 Axolotl/YSUMCL 的 Forge 安装器逻辑；不复制 PCL；不引入 CmlLib.Core.Installer.Forge；CmlLib.Core 继续作为原版安装器与启动器。
- 许可：CMUmcl 项目整体采用 GPL-3.0-only（既有代码 + 本轮新增的 Forge 移植/重实现代码）；第三方依赖（`CmlLib.Core` MIT、`authlib-injector.jar` 等）保留各自许可；新增 `LICENSE`（GPL-3.0-only 全文）与 `NOTICE`（Axolotl/YSUMCL 移植归属 + “分发需附源码”声明）；新增 Forge 源文件带 GPL 头注；开发期仓库可私有，分发必须附带源码。
- 第一版仅支持 Forge 1.20.1 现代安装器（`install_profile.json` + `version.json`）；旧版（`versionInfo`/`install`）与 NeoForge/Quilt 明确不支持并抛清晰错误。
- Forge 安装器 URL 由 `(gameVersion, loaderVersion)` 直拼 Maven，SHA-1 取自同名 `.sha1`；版本 id 固定 `{mc}-forge-{forge}`。
- 解析器合并库采用“运行时库过滤”：`version.json.libraries` + `install_profile.libraries` 中 Maven 版本等于 Forge 工件版本（`{gameVersion}-{loaderVersion}`）的运行时 Forge 库（fmlcore/javafmllanguage/lowcodelanguage/mclanguage/forge:universal），按坐标去重；installertools/ForgeAutoRenamingTool 等安装器专用工具不进入最终版本档案，但仍在库下载阶段下载供 processors 运行（Task 7 验证：最终档案 34 条库、不含安装器工具）。
- 内嵌工件（`data` 值以 `/` 开头）从安装器 jar 提取到暂存并改写为绝对路径；`[coordinate]` 解析为库路径，`{KEY}` 解析为 data 值。
- processors：客户端注入 `SIDE/MINECRAFT_JAR/MINECRAFT_VERSION/ROOT/INSTALLER/LIBRARY_DIR` + `install_profile.data`，按序执行、按 `sides` 过滤，命令 `java -cp <classpath+jar> <Main-Class-from-manifest> <args>`，工作目录为 Minecraft 目录，校验退出码与 `outputs` SHA-1。
- Java 选择前移到原版安装之后、加载器解析之前；processors 用派生出的控制台 `java.exe`，启动游戏用 `javaw.exe`；沿用 1.20.1 的 `majorVersion=17`（本机选 jdk-21）。
- 版本档案落盘到 `versions\{id}\`，附带 `install_profile.json` 与 `.forge-complete`；跳过重装必须校验档案 `inheritsFrom`、库存在 + SHA-1、processors outputs，绝不只信目录名。
- 安全：HTTPS 主机白名单、SHA-1/size 校验、zip-slip 路径防护、暂存 + 失败清理、无广告/无交互 GUI、不执行安装器 jar 自身主类。
- 测试以本地合成 `forge-test` 实例（1.20.1 + 47.4.28）端到端启动到主菜单为准，辅以解析/路径/参数/跳过判定的纯函数级单测。
