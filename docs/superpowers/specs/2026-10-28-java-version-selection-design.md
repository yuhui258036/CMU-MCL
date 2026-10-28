# Java 版本选择与运行时自动下载设计（CMUmcl）

日期：2026-10-28
范围：仅 CMUmcl（WPF / .NET 10 / CmlLib.Core 4.1.0）客户端，不改动任何产品代码（本文件是设计规格）。

---

## 目标

启动时根据实例对应的 Minecraft 基础版本自动选择本地已安装且兼容的 JDK，而不是像现在这样固定取第一个候选（实际总是偏好 jdk-21）。所需 Java 主版本来自已安装的基础版本 JSON 的 `javaVersion.majorVersion`；本地无精确匹配时选择“最低的、主版本 ≥ 所需”的 JDK。当本地没有任何兼容 JDK 时，若该版本 JSON 声明了 `javaVersion.component`，则自动从 Mojang 官方下载、校验并安装对应的 Java 运行时到 CMUmcl 数据目录并使用它；本地 JDK 始终优先，自动下载仅是最后手段。只有既无本地兼容 JDK、又无法自动下载（无 component，或平台不支持）时，才抛出带所需版本与已检测版本明细的清晰错误，绝不静默回退到 Java 21。

## 非目标

- 不提供手动指定 Java 的 UI 或配置文件。
- 本轮不实现 Forge / NeoForge / Quilt 的启动支持，因此不解析这些加载器专属的 Java 上下限；这是分阶段范围而非产品永久不支持，下一阶段接入加载器启动时会同步补齐其 Java 约束。
- 不改动登录校验、authlib-injector 注入、内存参数（512/4096）、实例目录（game_directory）、服务器快速进入（quick-play）逻辑。
- 不改动 PCL-CE 或 YSUMCL/Axolotl 等其他工程，仅参考其实现思路。
- 不支持非 64 位 Windows 架构（32 位 / arm64 / 非 Windows）的运行时自动下载，这些平台回退到手动安装提示。
- 不下载除 Mojang 官方运行时之外的任何第三方 JDK；不做“全局 Java 管理/切换”类功能。

## 后续阶段（不在本轮）

- Forge / NeoForge / Quilt 的启动支持是下一阶段。
- 该阶段将包括：加载器安装、版本档案（version profile）合并、processors/依赖解析、Quilt/Fabric 兼容处理，以及这些加载器版本档案中声明的专属 Java 上下限。
- 本设计中的 `JavaRequirement`（§1）与 `MojangRuntimeInstaller`（§7）应保持可扩展，以便后续加入这些加载器专属 Java 约束的解析；本轮不做任何实现。

## 背景 / 现状

- 当前 `Launch\JavaLocator.cs` 的 `Locate()` 返回第一个存在的候选，枚举顺序里 `jdk-21` 排在 `jdk-17 / jdk-22 / jdk-26` 之前，因此几乎总是选中 Java 21。
- 当前 `Launch\LaunchService.cs` 在安装原版/Fabric **之前**（第 5 步）就调用 `JavaLocator.Locate()`，并把该路径写死进 `MLaunchOption.JavaPath`。
- 目标机器当前实例：`csu-ysu-union`（Minecraft 26.3 + Fabric 0.19.5），官方版本 JSON 标注 `javaVersion.majorVersion = 25`，用 Java 21 启动失败。
- 本地 JDK：`C:\Program Files\Java\jdk-21`（21.0.8）、`jdk-22`（22.0.2）、`jdk-26.0.2.1`（26.0.2.1）。
- 既有测试实例：1.20.1 + Fabric 0.15.11，官方 `javaVersion.majorVersion = 17`，本地无 JDK 17，审批结论是用最低的 ≥17 即 jdk-21。
- 已安装基础版本 JSON 同时含 `javaVersion.component` 与 `javaVersion.majorVersion`（已实测）：26.3 → `java-runtime-epsilon` / 25；1.20.1 → `java-runtime-gamma` / 17。
- Mojang 官方运行时索引：`https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json`。索引按 OS 架构分组；`windows-x64` 下 `java-runtime-epsilon` = Java 25.0.1（manifest SHA-1 `b374544c680d965fb5535977d7cb04c6befe1930`、size 152664、82 个目录 + 411 个文件、约 100.2 MB），`java-runtime-gamma` = Java 17.0.15。
- 运行时条目 JSON 含 `manifest.sha1 / manifest.size / manifest.url / version.name`。运行时 manifest 含 `files` 映射，条目 `type` = `directory` 或 `file`；`file` 有 `downloads.raw.sha1 / size / url`；该 Windows 运行时无 `links`；文件 URL 用 `piston-data.mojang.com` 主机。
- CmlLib.Core 虽内置 Java 下载能力（`JavaFileExtractor` / `MinecraftJavaManifestResolver`），但本设计选择自建一个职责单一、可独立单元测试的精简下载器（只做“按 component 安装单个运行时”），便于严格控制 URL 白名单、哈希校验与路径防护，并沿用现有 `LaunchException` 文案体系。
- 参考实现：
  - PCL-CE 在 `ModLaunch.cs`（约 876–1023）读 `javaVersion.majorVersion` 与加载器约束，在 `ModJava.cs`（约 34–214）选 Java，扫描位于 `PCL.Core\Minecraft\Java`；其支持每实例/全局 Java 与自动下载，本设计仅借鉴其“本地优先 + 自动下载兜底”的思路，不引入全局/每实例 Java 配置。
  - YSUMCL/Axolotl fork 在 `mod.rs`（约 486–494）从版本档案读所需主版本，在 `api\jre.rs`（约 414–478）做“精确 → 最近更高”选择，在 `util\jre.rs`（约 746–880）解析 JDK `release` 文件；本设计的本地选择算法沿用该行为，自动下载为新增部分。

## 设计

### 1. 所需 Java 版本解析（version resolution）

- 输入是基础游戏版本号 `gameVersion`（即 `InstanceMetadata.Game`，`LaunchService` 第 2 步已读取），对 Fabric 也直接使用该基础版本号，**不解析** Fabric 档案里的 `inheritsFrom`。
- 读取文件：`%LOCALAPPDATA%\CMUmcl\minecraft\versions\{gameVersion}\{gameVersion}.json`，取 `javaVersion.majorVersion`（整数）与 `javaVersion.component`（字符串，可选）。
- 返回值类型改为 `JavaRequirement`（record）：`int Major` + `string? Component`。`Component` 在 JSON 缺失/非法、或 JSON 存在但无 `component` 字段时为 `null`。
- `majorVersion` 可能是 JSON 数字，也可能是数字字符串（CmlLib 用 `NumberToStringValueConverter` 兼容两种），解析时两者都接受；非法（≤0）视为缺失。
- 该文件在 `InstallAsync(gameVersion)` 后必然落盘；Java 解析必须在原版安装（第 7 步）与 Fabric 安装（第 8 步）**之后**执行，以保证基础版本 JSON 已存在。
- 若文件缺失、不可读、JSON 无 `javaVersion` 或 `majorVersion` 缺失/非法（≤0），回退到下面的版本映射（按数值元组比较游戏版本号，分割 `.`），此时 `Component = null`：

| 游戏版本范围 | 回退所需主版本 |
| --- | --- |
| < 1.17 | 8 |
| 1.17 – 1.20.4 | 17 |
| 1.20.5 – 1.21.8 | 21 |
| ≥ 1.21.9 | 25 |

- 该映射是二级安全网，仅在 JSON 不可用时生效；JSON 可用时以 `javaVersion.majorVersion` 为准。映射边界（尤其 `≥ 1.21.9 → 25`）为最佳努力值，见“风险”。
- 说明：1.17 官方标注 `majorVersion = 16`，但 16 已停止维护，回退映射统一归入 17（17 可正常运行 1.17）；仅影响 JSON 缺失这一罕见路径。

### 2. JDK 扫描来源（scan sources）

扫描后得到候选目录集合（每个候选取其 JDK 根目录 = `bin` 的父目录），按完整路径去重（Windows 下不区分大小写），仅保留存在 `bin\javaw.exe` 或 `bin\java.exe` 的候选。来源包括（顺序不决定最终选择，仅决定稳定枚举顺序）：

1. `JAVA_HOME` 环境变量指向的目录。
2. `JAVA_HOME` 所在父目录下的兄弟 JDK（若 `JAVA_HOME` 位于某个 JDK 根目录下）。
3. `%ProgramFiles%\Java` 与 `%ProgramFiles(x86)%\Java` 下的所有子目录。
4. 常见厂商目录（`%ProgramFiles%` 与 `%ProgramFiles(x86)%` 下）：`Eclipse Adoptium`、`Microsoft`、`Zulu`、`Amazon Corretto`、`BellSoft`、`GraalVM`。
5. `%USERPROFILE%\.jdks`（JetBrains / IDEA 管理的 JDK）。
6. `PATH` 各分段中包含 `java.exe` / `javaw.exe` 的目录（等价于 `where java`）。
7. Windows 注册表（尽力而为，读取失败不致命）：`HKLM\SOFTWARE\JavaSoft\JDK` 与 `HKLM\SOFTWARE\JavaSoft\Java Development Kit`（含 `WOW6432Node` 与 `HKCU` 同名键）中的 `JavaHome` 值。

### 3. Java 主版本解析（parsing）

对每个候选 JDK 根目录确定主版本 `Major`（int）：

1. 优先读取 `{根目录}\release` 文件中的 `JAVA_VERSION` 属性值。
2. 回退执行 `{根目录}\bin\java.exe -version`，取 stderr 首行引号内的版本串。
3. 归一化：`1.8.0_402` / `1.8` → `8`；`21.0.8` / `22.0.2` / `26.0.2` → 取首个 `.` 前的整数。
4. 若两步均无法得到有效主版本，该候选不进入选择排序（无法证明兼容），其路径仅用于诊断信息。

`JavaInfo` 增加 `int Major`（沿用现有 `JavaPath`、`Version` 字符串字段）。§3 的解析逻辑同时复用于本地候选与下载后运行时的最终校验，确保两种来源用同一套“主版本判定”规则。

### 4. Java 选择算法（selection）

给定所需主版本 R 与带主版本的候选集合：

1. 优先精确匹配 `Major == R`；有多个时优先 `javaw.exe`，再取完整版本号最高者。
2. 无精确匹配时，取“主版本最小且 ≥ R”的候选；同等主版本下优先 `javaw.exe`，再取完整版本号最高者。
3. 两者皆无，返回“未命中”信号（由启动流程决定是自动下载还是抛错），绝不回退到任意更低版本或固定 21。

例：R=25 且候选 {21,22,26} → 26；R=17 且候选 {21,22,26} → 21（最低 ≥17）；R=21 且候选 {22,26} → 22。

### 5. 启动流程调整（launch flow）

在 `LaunchService.LaunchAsync` 内：

- 删除原第 5 步（安装前的 `JavaLocator.Locate()`）。
- 在原版/Fabric 安装完成后（现第 8 步之后、构建 `MLaunchOption` 之前）插入：
  1. 用 `gameVersion` 与 `LauncherPaths.MinecraftDirectory` 解析需求 `(R, C)`（见 §1）。
  2. `JavaLocator.Scan()` 得到本地候选列表。
  3. `JavaLocator.Select(R, candidates)`：
     - 命中 → 使用本地 JDK（本地 JDK 永远优先）。
     - 未命中且 `C != null` → `await new MojangRuntimeInstaller().EnsureRuntimeAsync(C, R, progress, ct)`，使用返回的 `JavaInfo`（见 §7）。
     - 未命中且 `C == null` → 抛出 `LaunchException`（手动安装提示，见“错误处理”）。
  4. `MLaunchOption.JavaPath = java.JavaPath`。
- 其余步骤（第 3 步会话校验、第 4 步 authlib、第 9–11 步 MSession / MLaunchOption / 启动）与参数保持原样。

### 6. 进度上报（progress reporting）

- `LaunchProgress` 增加可选字段 `JavaVersion`（字符串）与 `JavaPath`（字符串）。
- 选中后上报一条 `Stage = "启动"`、`Message = $"已选择 Java {major}（{path}）"` 的记录，并填充 `JavaVersion` / `JavaPath`，让用户在卡片状态文本中直接看到实际使用的 Java（现有 `PackCardViewModel.ReportLaunchProgress` 会展示 `Message`，无需改 UI）。
- 自动下载期间上报 `Stage = "下载 Java"`：`ProgressedTasks` / `TotalTasks` = 已完成/总文件数，`ProgressedBytes` / `TotalBytes` = 已完成/总字节数，`Message` 同时含文件数与字节数（如 `已下载 3/411 个文件（12.3 MB / 100.2 MB）`）。复用 `LaunchProgress` 既有数值字段，不新增数值字段；`PackCardViewModel` 已有 `TotalTasks`/`TotalBytes` 两种进度渲染，无需改 UI。

### 7. 运行时自动下载（Mojang runtime installer）

新增独立组件 `MojangRuntimeInstaller`（位于 `CMUmcl\Launch`），职责单一：给定 `component`（如 `java-runtime-epsilon`）与所需主版本 R，安装一个可用的 Mojang 官方 Java 运行时并返回其 `JavaInfo`。仅在启动流程“本地无兼容 JDK 且存在 component”时被调用。

**平台选择**

- 目标平台固定为 `windows-x64`（应用为 `net10.0-windows` WPF，仅在 64 位 Windows 运行）。
- 以 `Environment.Is64BitOperatingSystem`（或 `RuntimeInformation.OSArchitecture == Architecture.X64`）判定为真才继续；否则抛 `LaunchException`：“当前仅支持 64 位 Windows，无法自动下载 Java 运行时，请手动安装 Java {R}。”

**目录布局**

- 运行时根目录：`%LOCALAPPDATA%\CMUmcl\java\runtime`。
- 最终目录：`%LOCALAPPDATA%\CMUmcl\java\runtime\{component}`。`component` 直接作为目录名使用，使用前必须校验为“安全单段名”（不含 `\`、`/`、`..`、`.`、冒号及非法文件名字符），否则抛错。
- 暂存目录：`%LOCALAPPDATA%\CMUmcl\java\runtime\.staging-{guid}`（每次下载新建；失败删除；成功移动到最终目录）。

**复用已安装运行时**

- 若最终目录已存在、含 `.complete` 标记，且 `bin\javaw.exe`（回退 `bin\java.exe`）能按 §3 解析出主版本且 `Major >= R`（优先精确），则直接复用，不重复下载。

**下载与校验流程**（全部 HTTPS，先校验 URL 再下载）

1. 拉取官方运行时索引 `https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json`，取 `windows-x64` 分组。
2. 在该分组中查找 `component`；找不到 → 抛 `LaunchException`：“当前平台不支持自动下载该 Java 运行时，请手动安装 Java {R}。”
3. 从条目读 `manifest.sha1 / manifest.size / manifest.url / version.name`。
4. 下载 manifest 到暂存目录，校验 SHA-1 与 size（不符 → 清理并抛“Java 运行时清单校验失败，已中止。”）。
5. 解析 manifest 的 `files` 映射：`type = "directory"` 的条目只创建目录；`type = "file"` 的条目依次下载 `downloads.raw.url` 到暂存目录对应相对路径，逐个校验 `downloads.raw.sha1` 与 `size`（不符 → 清理并抛“Java 运行时文件校验失败（{相对路径}），已中止。”）。Windows 下无需处理可执行位；不处理、不跟随 `links`（该 Windows 运行时为空）。
6. 全部成功后写入 `.complete` 标记文件；删除可能存在的无效最终目录；`Directory.Move` 将暂存目录原子移动到最终目录。
7. 用 §3 的解析逻辑（先 `release` 文件、回退 `bin\java.exe -version`）校验安装结果的 `Major >= R`（优先精确）。失败 → 删除最终目录并抛“Java 运行时校验失败，已清除不完整的下载。”
8. 返回 `new JavaInfo { JavaPath = <bin\javaw.exe，回退 bin\java.exe>, Version = <version.name>, Major = <解析值> }`。

**安全约束**

- URL 白名单：仅允许 `piston-meta.mojang.com`（索引与 manifest）与 `piston-data.mojang.com`（文件）两个主机的绝对 HTTPS URL。下载前校验 URL 主机；`HttpClientHandler` 允许重定向，但重定向后必须再次校验最终响应 URI 主机仍在白名单，否则拒绝并清理。
- SHA-1 与 size 双重校验 manifest 与每一个文件（流式 `IncrementalHash.CreateHash(SHA1)` + 字节计数，参照 `PackInstaller.DownloadToFileAsync` 的实现方式）。
- 路径防护（zip-slip）：对 manifest 中每个文件的相对路径做规范化，拒绝空路径、根路径、`..` / `.` 段、非法文件名字符；`Path.GetFullPath` 解析后必须仍在暂存目录之下（参照 `PackInstaller.SanitizeRelativePath` / `ResolveUnder`）。
- 不执行任何下载内容；仅在校验全部通过后，对最终目录中的 `bin\java.exe` 运行 `-version` 读取版本号（只读校验，非执行 payload）。

**进度与失败**

- 下载期间通过 `LaunchProgress` 上报 `Stage = "下载 Java"`（见 §6）。
- 任一失败（网络、HTTP 非 2xx、哈希/大小不符、磁盘错误、用户取消）→ 删除暂存目录，抛出带中文文案的 `LaunchException`，绝不留半成品运行时。

## 错误处理

- JSON 缺失/非法 → 使用 §1 回退映射，不视为致命错误（此时 `Component = null`，无法自动下载）。
- 本地无兼容 JDK（无论是扫描不到任何 JDK，还是有 JDK 但都不满足 R），且 `Component == null`：
  - 扫描不到任何 JDK → `LaunchException`：“未找到 Java 运行环境。请安装 Java {R} 并重试。”
  - 有 JDK 但不满足 R → `LaunchException`：“未找到满足 Minecraft {gameVersion} 的 Java（需要 Java {R}）。已检测到的 Java 主版本：{逗号分隔列表}。请手动安装 Java {R} 后重试。” 消息必须同时包含所需主版本与已检测主版本清单。
- 本地无兼容 JDK，且 `Component != null`：
  - 自动下载成功 → 正常启动。
  - 自动下载失败（网络/哈希/平台不支持/找不到组件/磁盘错误等）→ 抛 §7 对应的 `LaunchException`，消息给出具体失败原因，并在可确定时附上所需主版本与“可手动安装 Java {R}”的指引；无半成品残留。
- 所有错误沿用现有 `LaunchException`（中文友好文案），由 `MainWindow.LaunchAsync` 的 catch 统一弹窗展示。

## 文件改动清单

- 新增 `CMUmcl\Launch\JavaVersionResolver.cs`：定义 `JavaRequirement`（`int Major; string? Component`）与静态方法 `ResolveRequirement(string gameVersion, string minecraftDir)`，实现 §1（读 JSON 的 `majorVersion` + `component`，回退映射时 `Component = null`）。
- 新增 `CMUmcl\Launch\MojangRuntimeInstaller.cs`：实现 §7，含运行时索引/manifest 的 DTO、SHA-1/size 流式下载、URL 白名单与路径防护；公开 `Task<JavaInfo> EnsureRuntimeAsync(string component, int requiredMajor, IProgress<LaunchProgress>? progress, CancellationToken ct)`。
- 修改 `CMUmcl\Launch\JavaLocator.cs`：
  - `JavaInfo` 增加 `int Major`。
  - 用 `Scan()`（返回去重后的 `IReadOnlyList<JavaInfo>`，实现 §2 来源与 §3 解析）替换现有 `Locate()` 的“取第一个”逻辑。
  - 新增 `Select(int requiredMajor, IReadOnlyList<JavaInfo> candidates)`（实现 §4，未命中返回 `null` 而非抛错）。
- 修改 `CMUmcl\Launch\LaunchService.cs`：按 §5 调整选择时机与调用，实例化并调用 `MojangRuntimeInstaller`，按 §6 上报下载与选择进度。
- 修改 `CMUmcl\Launch\LaunchProgress.cs`：增加 `JavaVersion` / `JavaPath` 可选字段（下载进度复用已有 `ProgressedBytes`/`TotalBytes`/`ProgressedTasks`/`TotalTasks`）。
- 修改 `CMUmcl\Content\LauncherPaths.cs`：增加 `JavaDirectory`（`%LOCALAPPDATA%\CMUmcl\java`）、`JavaRuntimeDirectory`（`%LOCALAPPDATA%\CMUmcl\java\runtime`）属性，及创建全新暂存目录的辅助（`%LOCALAPPDATA%\CMUmcl\java\runtime\.staging-{guid}`）。
- 不改动其他任何产品文件；不改 UI。

## 验证 / 测试

验收标准（全部需通过）：

1. 实例 `csu-ysu-union`（Minecraft 26.3 + Fabric 0.19.5）启动时选中 `C:\Program Files\Java\jdk-26.0.2.1`（主版本 26），不触发任何下载，不再因 Java 21 启动失败。
2. 实例 1.20.1 + Fabric 0.15.11（官方 majorVersion=17，本地无 JDK 17）启动时选中最低 ≥17 的 `jdk-21`，不触发下载。
3. 人为构造“所需 25、component 存在、本地仅 21/22”的场景 → 自动下载 `java-runtime-epsilon`，校验通过后选中 `%LOCALAPPDATA%\CMUmcl\java\runtime\java-runtime-epsilon\bin\javaw.exe`（主版本 25）。
4. 人为构造“所需 25、component 缺失、本地仅 21/22”的场景 → 抛出包含所需 25 与已检测 {21,22} 的清晰 `LaunchException`，不静默回退到 21。
5. 下载中途失败 / manifest 或文件哈希与大小不符 / manifest 含 `..` 或根路径 → 抛出清晰 `LaunchException`，暂存目录与最终目录均被清除，无半成品残留。
6. 非 64 位架构、component 在索引中不存在、或下载 URL 不在白名单主机 → 抛对应 `LaunchException`，不产生任何写入。
7. `dotnet build`（`net10.0-windows`）成功，无编译错误。

补充校验（纯函数级，建议单元测试；因项目暂无测试工程，可在计划阶段评估是否引入最小测试工程或用手工脚本验证）：

- `ParseJavaMajor`：`"1.8.0_402"` → 8；`"21.0.8"` → 21；`"26.0.2"` → 26。
- `ResolveRequirement`：26.3 的 JSON 缺省时回退 `Major=25, Component=null`；1.20.1 回退 `17`；含 `javaVersion.majorVersion=25, component=java-runtime-epsilon` 时取 JSON 值。
- `Select`：精确命中优先；R=17 无 17 时选 21；R=25 时选 26；无满足项时返回 `null`（不抛错）。

自动下载的验证方式（不改变正常行为）：`MojangRuntimeInstaller` 是独立可调用组件，可编写一次性直接调用其 `EnsureRuntimeAsync("java-runtime-epsilon", 25, null, CancellationToken.None)` 的控制台脚本（或临时测试入口）来端到端验证下载、SHA-1/size 校验、路径防护与最终目录落盘。因本机存在 JDK 26，正常启动不会触发下载；上述直接调用不受“本地 JDK 优先”逻辑影响，可安全验证下载器本身。

## 风险与缓解

1. **`release` / `java -version` 解析对 Java 8 与未来版本格式脆弱。** 缓解：集中一个 `ParseJavaMajor` 归一化函数，先读 `release` 再回退 `-version`，失败即排除该候选而非崩溃；对 `1.8.x` 做专门处理。
2. **回退映射边界（尤其 `≥1.21.9 → 25`）可能随 Mojang 版本策略变化而失准。** 缓解：映射仅作 JSON 缺失时的安全网，JSON 可用时以 `javaVersion.majorVersion` 为准；映射表集中在一处并配注释。
3. **注册表 / 厂商目录 / `.jdks` 扫描在不同机器上覆盖不一致，可能漏掉某些 JDK。** 缓解：扫描是多来源并集 + 去重，`JAVA_HOME`、`Program Files\Java`、`PATH` 三项已覆盖绝大多数场景；注册表读取为尽力而为、失败不致命，未覆盖时用户仍会得到清晰的“已检测主版本”报错或触发自动下载。
4. **扫描/解析引入的进程调用（`java -version`）与磁盘遍历可能拖慢启动。** 缓解：优先读 `release` 文件（纯文件读取，无需起进程），仅在其缺失时起进程；扫描源固定且候选数量小，单次启动开销可忽略。
5. **自动下载依赖网络与 Mojang 服务器可用性，可能失败或较慢。** 缓解：本地 JDK 始终优先，多数机器不触发下载；下载失败即清理并给出可手动安装的中文提示；已安装运行时带 `.complete` 标记复用，不重复下载。
6. **哈希/大小不符或下载中途失败可能留下半成品运行时。** 缓解：全部下载到独立暂存目录并逐个校验，失败即删暂存；成功才原子移动到最终目录并写 `.complete`，最终目录要么完整可用、要么不存在。
7. **manifest 中的路径可能尝试逃逸（zip-slip），或 URL 指向非官方主机。** 缓解：URL 白名单（`piston-meta.mojang.com` / `piston-data.mojang.com`）+ 路径规范化与根目录校验 + 不跟随 `links`、不执行下载内容，仅对最终 `bin\java.exe` 做只读版本校验。
8. **运行时体积较大（约 100 MB），首次下载耗时。** 缓解：流式下载并上报文件数与字节进度；该路径仅在无本地兼容 JDK 时触发，属低频场景。

## 决策摘要

- 所需 Java 主版本来自基础版本 JSON `javaVersion.majorVersion`；Fabric 用基础版本号而非 `inheritsFrom`。
- 需求解析改为返回 `JavaRequirement { Major, Component }`：`component` 来自 `javaVersion.component`（可选，缺失/JSON 不可用时为 `null`）。
- JSON 缺失/非法时使用集中维护的回退映射（<1.17→8、1.17–1.20.4→17、1.20.5–1.21.8→21、≥1.21.9→25），此时 `Component = null`。
- 扫描本地 JDK：`JAVA_HOME` 及兄弟目录、`Program Files\Java`、常见厂商目录、`.jdks`、`PATH`、注册表（尽力而为），去重。
- 主版本解析优先读 `release` 的 `JAVA_VERSION`，回退 `java -version`，`1.8.x → 8`；同一逻辑复用于下载后运行时校验。
- 选择：精确主版本优先；否则最低 ≥ 所需；本地 JDK 永远优先，无可用且存在 component 时自动下载，否则抛清晰错误，绝不静默回退 Java 21。
- 自动下载仅支持 `windows-x64`，从 Mojang 官方索引按 `component` 取 manifest，校验 SHA-1/size，逐个下载文件到暂存目录，成功后原子移动到 `%LOCALAPPDATA%\CMUmcl\java\runtime\{component}`。
- 安全：仅允许 `piston-meta.mojang.com` 与 `piston-data.mojang.com` 的 HTTPS URL；zip-slip 路径防护；不执行下载内容；失败清理暂存、不留半成品。
- Java 选择移至原版/Fabric 安装之后执行；下载进度经 `LaunchProgress`（`下载 Java` 阶段）上报，选中的 Java 版本与路径（`已选择 Java {major}（{path}）`）一并上报。
- 不做手动 Java UI、不做全局/每实例 Java 管理；本轮不实现 Forge / NeoForge / Quilt 启动及其加载器专属 Java 约束（留待下一阶段，见“后续阶段”）。
