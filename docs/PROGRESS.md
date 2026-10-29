# CMUmcl 项目进度表单

> 最后更新：2026-10-29
> 用途：每次继续开发前先看“当前阶段 / 下一步”，避免忘记进度。

## 一句话状态

CMUmcl 已能登录、拉取签名整合包、安装、按 Minecraft 版本选择 Java，并在缺少兼容 Java 时自动下载 Mojang runtime；26.3 已实际启动到主菜单。Forge、NeoForge、Quilt 均已通过端到端安装并启动到主菜单。主窗口为现代三页布局；新建实例改为主窗口内分步流程（YSUMCL/PCL/HMCL 参考），支持正式版/快照/愚人节/远古版本筛选、加载器卡片、创建时下载和 Fabric API 自动安装；迁移冲突可逐项改名；启动服务器整合包前可按设置自动更新。等待用户验收。下一步接客户端自动更新和发布自动化。

## 阶段总览

- [x] **0. 方案确定**：放弃 PCL-CE 换皮，改为自研 WPF + CmlLib.Core
- [x] **1. 登录**：CSU-MC Yggdrasil 登录、DPAPI 保存、token 刷新
- [x] **2. 整合包**：签名清单、SHA 校验、下载/安装、实例目录、服务端直连
- [x] **3. 启动**：原版 + Fabric 启动、authlib-injector、内存/目录参数
- [x] **3.5 Java**：按版本要求选择本地 JDK；缺 Java 自动下载 Mojang runtime
- [x] **4. 自包含发布**：已生成并验证不依赖用户安装 .NET 10 的 win-x64 包
- [x] **4.5 自动下载重试**：Mojang runtime 下载遇到临时网络错误自动重试 3 次（1s/2s）
- [x] **5. 加载器扩展**：Forge / NeoForge / Quilt 安装、启动、Java 上下限
  - [x] **5.1 Forge**：`install_profile` 现代安装器（1.20.1 + 47.4.28）解析、合并库、内嵌工件、6 个客户端 processors、版本档案落盘、跳过重装、损坏自修复；`forge-test` 端到端启动到主菜单；GPL-3.0-only + LICENSE/NOTICE；未做 Quilt/旧版 Forge/Forge 版本列表 UI。
  - [x] **5.2 NeoForge**：1.21.1 + 21.1.265，使用 CmlLib.Core.Installer.NeoForge 4.0.1（MIT）；安装、跳过、Forge 回归和主菜单启动通过；未做 Quilt。
  - [x] **5.3 Quilt**：1.21.1 + 0.20.0-beta.9，使用 CmlLib.Core 内置 QuiltInstaller；安装、依赖下载、主菜单启动通过。
- [ ] **6. 设置与更新**：Java/内存/目录设置、启动前强制更新、发布自动化
  - [x] **6.1 设置页**：主窗口内页面；Java 自动/手动、最大内存、版本隔离、根目录打开；待用户验收。
  - [x] **6.1.5 自定义实例创建**：主窗口内分步流程（全新创建/导入入口 → 游戏版本列表 → 加载器卡片 → 加载器版本 → 下载）；本地 `local:{uuid}` ID、独立目录/显示名、冲突自动后缀；创建时下载 Minecraft/加载器文件；Fabric/Quilt 自动安装 Fabric API；Forge/NeoForge 版本列表已接入；创建成功后写入 `.cmumcl-provisioned.json`，再次启动跳过重复安装；待用户验收。
  - [x] **6.1.6 三页布局**：服务器整合包（仅下载/更新）/ 实例（统一启动、新建实例、打开目录）/ 设置；待用户验收。
  - [x] **6.1.7 UI 现代化**：主窗口三页导航/卡片/设置页；创建实例改为无新窗口的主窗口内流程，并与主窗口统一使用暗色主题；游戏版本列表支持正式版/快照/愚人节/远古版本与搜索；加载器使用 YSUMCL 实例图标/HMCL 卡片布局。
  - [x] **6.2 实例存放位置**：可配置实例根目录；迁移已有实例；冲突可逐项手动改目录名/显示名，未改时自动加 `-2` / ` (2)` 后缀；迁移测试 56/56 PASS；待用户验收。
  - [x] **6.3 启动前自动更新整合包**：启动非本地实例前比较清单 revision；开启自动更新时先安装新版本，成功后才启动；检查/更新失败则不启动旧版本；关闭开关时直接启动已安装版本；设置页已加入自动更新开关；决策测试 8/8 PASS。
  - [ ] **6.5 客户端自动更新**：检查 GitHub Release/更新清单，下载并替换自包含版本（参考 PCL / YSUMCL 的 updater）
- [ ] **7. 社区验收**：真实整合包、真实玩家、正式域名/证书

## 当前基线

| 项目 | 状态 |
| --- | --- |
| 分支 | `main` |
| 本地提交 | 见 `git log -1` |
| 远程 | `origin/main`，GitHub 私有仓库 `yuhui258036/CMU-MCL` |
| 构建 | `dotnet build` exit 0，0 errors（2 个既有 NU1510 警告） |
| 测试 | JavaHarness：默认 55 PASS；自动下载模式 65 PASS；ForgeHarness：默认 66 PASS，`--install`/`--launch` 通过；NeoForge 包装器：安装/跳过/启动通过；Quilt：1.21.1 主菜单启动通过；SettingsMigrationTest：56/56 PASS；LocalInstanceNaming：16/16 PASS；ProvisioningMarker：22/22 PASS；PackAutoUpdate：8/8 PASS；VersionCatalog：921 个版本（103 正式版 / 748 快照 / 9 愚人节 / 61 远古版本），Fabric 26.3 255 / Quilt 1.21.1 307 / NeoForge 26.3 95 / Forge 1.21.1 69 个加载器版本；本地代理 127.0.0.1:7890 验证通过；Fabric API 下载校验通过；创建流程已在应用中人工打开验证 |
| 实际启动 | 26.3 + Fabric 0.19.5，使用 JDK 26，进入主菜单；无 Java 25+ 模块访问错误 |
| 自动 Java | `java-runtime-epsilon` / Java 25.0.1，约 100.2MB，411 files，`.complete` 正常 |
| 数据目录 | `%LOCALAPPDATA%\CMUmcl` → junction → `D:\新启动器\CMUmclData` |
| C: 空间 | 约 2.54GB 空闲 |
| 客户端包 | `D:\新启动器\dist\CMUmcl-step3.zip`（35 entries） |
| 自包含包 | `D:\新启动器\dist\CMUmcl-win-x64-all-loaders-selfcontained.zip`（57.88MB，263 entries，含 Fabric/Forge/NeoForge/Quilt）；隔离 .NET 环境独立启动 MainWindow 通过 |
| Forge | 1.20.1 + 47.4.28；`forge-test` 安装/启动/跳过/损坏修复通过；universal SHA-1 `1c2208dc…` |
| NeoForge | 1.21.1 + 21.1.265；CmlLib.Core.Installer.NeoForge 4.0.1（MIT）；安装/跳过/主菜单启动通过；Forge 回归通过 |
| Quilt | 1.21.1 + 0.20.0-beta.9；CmlLib 内置 QuiltInstaller；安装/依赖下载/主菜单启动通过 |
| 服务端 | 签名 manifest + 管理后台可用；`CSUxYSU union server` 已上传 revision 1 |
| 备份 | 加固备份 `backups\PCL-CMU-server-backup-20261028-1541.tar.gz` |

## 关键路径

- 客户端：`D:\新启动器\CMUmcl`
- 可执行文件：`CMUmcl\bin\Debug\net10.0-windows\CMUmcl.exe`（framework-dependent）
- 自包含目录：`D:\新启动器\dist\CMUmcl-win-x64-all-loaders`；自包含 zip：`D:\新启动器\dist\CMUmcl-win-x64-all-loaders-selfcontained.zip`
- Forge 源码包：`D:\新启动器\dist\CMUmcl-step4-source.zip`（含 LICENSE/NOTICE；NeoForge 后续代码以 git 为准）
- 规格：`docs\superpowers\specs\2026-10-28-java-version-selection-design.md`
- 计划：`docs\superpowers\plans\2026-10-28-java-version-selection-plan.md`
- 自动下载运行时：`D:\新启动器\CMUmclData\java\runtime`
- 管理后台：`D:\新启动器\admin-backend`
- 管理凭证：`D:\新启动器\admin-credentials.txt`（不要提交/外发）

## 已知问题 / 风险

- `CMUmcl.exe` 默认构建仍是 framework-dependent；当前自包含包为全加载器版本，分发用 `CMUmcl-win-x64-all-loaders-selfcontained.zip`。
- 发布自动化未配置：GitHub Actions 可自动构建并上传 Release；客户端自动更新尚未实现。
- Forge 下载已加独立 3 次重试（临时网络/408/429/5xx，间隔 1s/2s）；若网络持续不稳定仍可能失败。
- Forge 1.20.1 在 JDK 21 上已验证可启动；官方目标仍是 Java 17，后续可评估精确约束。
- 自动下载已加 3 次重试（仅临时网络 / 408 / 429 / 5xx，间隔 1s/2s）；若代理持续不稳定仍可能失败。
- 已知加载器已全部支持：原版 / Fabric / Forge / NeoForge / Quilt。
- 设置页、实例迁移、自定义实例创建、启动前自动更新已完成，等待用户界面验收；客户端自动更新尚未接入。
- 自定义实例创建时会下载 Minecraft 与加载器文件；成功后写入 `.cmumcl-provisioned.json`，后续启动跳过原版/加载器安装；失败时实例保留，首次启动会自动重试。
- 已有旧实例首次启动仍会安装一次并生成标记，之后不再重复下载；版本 JSON 缺失或元数据变化会使标记失效。
- 2026-10-29 已清空全部 6 个测试实例（`test`、`csu-ysu-union`、`forge-test`、`neoforge-test`、`1`、`1 (2)`），仅保留游戏缓存/Java/设置。
- Forge/NeoForge 版本列表已接入 Maven；NeoForge 源站在当前网络不可达时回退手动输入。
- 实例迁移冲突默认自动加后缀；迁移前可打开逐项手动改名对话框。
- 创建流程目前只支持原版 / Fabric / Forge / NeoForge / Quilt；OptiFine、QSL/QFAPI 与“导入已有”暂未接入。
- 创建实例图标使用 YSUMCL 素材，背景透明；Forge 使用去除白底后的 `Forge.png`。
- 版本列表会自动探测本机代理（`CMUMCL_PROXY` 优先，其次系统代理，最后 127.0.0.1:7890/7891/1080）；若网络仍不可达，Fabric/Quilt/NeoForge 列表会为空而不是崩溃。
- 旧 `CreateInstanceWindow` 保留未调用，后续可删除；新的创建流程是主窗口内的 `CreateInstanceView`。
- 加载器版本页目前显示兼容游戏版本/稳定/构建信息；Forge/NeoForge Maven 不提供发布时间，因此没有 HMCL 截图中的日期列。
- 本地实例暂无删除/修复入口；清单获取失败时本地卡片也随主页面一起不可见。
- `CSUxYSU union server` 的服务器地址目前为空，需要后台补上。
- 服务端 manifest 私钥仍在服务器上；后续可评估离线签名。
- Git push 走 HTTPS + 本机代理；直连 GitHub 可能卡住。

## 下次继续时填写

- 日期：
- 本次目标：
- 选择的下一步：
  - [ ] 设置页 / 强制更新
  - [ ] 客户端自动更新
  - [ ] GitHub Actions 发布自动化
  - [ ] 其他：
- 完成标准：
- 当前阻塞：
- 本次提交：

## 常用命令

```powershell
$env:TEMP="D:\新启动器\.tmp"
$env:TMP="D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME="D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES="D:\新启动器\.nuget\packages"
$dotnet="C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"
& $dotnet build "D:\新启动器\CMUmcl\CMUmcl.csproj" -c Debug
```

```powershell
# Java 纯测试
& $dotnet run --project "D:\新启动器\.tmp\JavaHarness\JavaHarness.csproj"
# 自动下载/复用/清理测试
& $dotnet run --project "D:\新启动器\.tmp\JavaHarness\JavaHarness.csproj" -- --download
```

```powershell
# GitHub push（本机代理）
git -c http.proxy=http://127.0.0.1:7890 -c https.proxy=http://127.0.0.1:7890 push
```
