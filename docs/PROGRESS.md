# CMUmcl 项目进度表单

> 最后更新：2026-10-28  
> 用途：每次继续开发前先看“当前阶段 / 下一步”，避免忘记进度。

## 一句话状态

CMUmcl 已能登录、拉取签名整合包、安装、按 Minecraft 版本选择 Java，并在缺少兼容 Java 时自动下载 Mojang runtime；26.3 已实际启动到主菜单。自包含 win-x64 包已生成并验证独立启动。自动下载已加 3 次重试。Forge、NeoForge、Quilt 均已通过端到端安装并启动到主菜单。下一步做设置页 / 客户端自动更新 / 发布自动化。

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
  - [ ] **6.5 客户端自动更新**：检查 GitHub Release/更新清单，下载并替换自包含版本（参考 PCL / YSUMCL 的 updater）
- [ ] **7. 社区验收**：真实整合包、真实玩家、正式域名/证书

## 当前基线

| 项目 | 状态 |
| --- | --- |
| 分支 | `main` |
| 本地提交 | 见 `git log -1` |
| 远程 | `origin/main`，GitHub 私有仓库 `yuhui258036/CMU-MCL` |
| 构建 | `dotnet build` exit 0，0 errors（2 个既有 NU1510 警告） |
| 测试 | JavaHarness：默认 55 PASS；自动下载模式 65 PASS；ForgeHarness：默认 66 PASS，`--install`/`--launch` 通过；NeoForge 包装器：安装/跳过/启动通过；Quilt：1.21.1 主菜单启动通过 |
| 实际启动 | 26.3 + Fabric 0.19.5，使用 JDK 26，进入主菜单；无 Java 25+ 模块访问错误 |
| 自动 Java | `java-runtime-epsilon` / Java 25.0.1，约 100.2MB，411 files，`.complete` 正常 |
| 数据目录 | `%LOCALAPPDATA%\CMUmcl` → junction → `D:\新启动器\CMUmclData` |
| C: 空间 | 约 2.54GB 空闲 |
| 客户端包 | `D:\新启动器\dist\CMUmcl-step3.zip`（35 entries） |
| 自包含包 | `D:\新启动器\dist\CMUmcl-win-x64-neoforge-selfcontained.zip`（57.88MB，263 entries，含 Forge + NeoForge）；隔离 .NET 环境独立启动 MainWindow 通过 |
| Forge | 1.20.1 + 47.4.28；`forge-test` 安装/启动/跳过/损坏修复通过；universal SHA-1 `1c2208dc…` |
| NeoForge | 1.21.1 + 21.1.265；CmlLib.Core.Installer.NeoForge 4.0.1（MIT）；安装/跳过/主菜单启动通过；Forge 回归通过 |
| Quilt | 1.21.1 + 0.20.0-beta.9；CmlLib 内置 QuiltInstaller；安装/依赖下载/主菜单启动通过 |
| 服务端 | 签名 manifest + 管理后台可用；`CSUxYSU union server` 已上传 revision 1 |
| 备份 | 加固备份 `backups\PCL-CMU-server-backup-20261028-1541.tar.gz` |

## 关键路径

- 客户端：`D:\新启动器\CMUmcl`
- 可执行文件：`CMUmcl\bin\Debug\net10.0-windows\CMUmcl.exe`（framework-dependent）
- 自包含目录：`D:\新启动器\dist\CMUmcl-win-x64-neoforge`；自包含 zip：`D:\新启动器\dist\CMUmcl-win-x64-neoforge-selfcontained.zip`
- Forge 源码包：`D:\新启动器\dist\CMUmcl-step4-source.zip`（含 LICENSE/NOTICE；NeoForge 后续代码以 git 为准）
- 规格：`docs\superpowers\specs\2026-10-28-java-version-selection-design.md`
- 计划：`docs\superpowers\plans\2026-10-28-java-version-selection-plan.md`
- 自动下载运行时：`D:\新启动器\CMUmclData\java\runtime`
- 管理后台：`D:\新启动器\admin-backend`
- 管理凭证：`D:\新启动器\admin-credentials.txt`（不要提交/外发）

## 已知问题 / 风险

- `CMUmcl.exe` 默认构建仍是 framework-dependent；当前自包含包为 Forge + NeoForge 版本，分发用 `CMUmcl-win-x64-neoforge-selfcontained.zip`。
- 发布自动化未配置：GitHub Actions 可自动构建并上传 Release；客户端自动更新尚未实现。
- Forge 下载已加独立 3 次重试（临时网络/408/429/5xx，间隔 1s/2s）；若网络持续不稳定仍可能失败。
- Forge 1.20.1 在 JDK 21 上已验证可启动；官方目标仍是 Java 17，后续可评估精确约束。
- 自动下载已加 3 次重试（仅临时网络 / 408 / 429 / 5xx，间隔 1s/2s）；若代理持续不稳定仍可能失败。
- 已知加载器已全部支持：原版 / Fabric / Forge / NeoForge / Quilt。
- `CSUxYSU union server` 的服务器地址目前为空，需要后台补上。
- 服务端 manifest 私钥仍在服务器上；后续可评估离线签名。
- Git push 走 HTTPS + 本机代理；直连 GitHub 可能卡住。

## 下次继续时填写

- 日期：
- 本次目标：
- 选择的下一步：
  - [ ] 自包含发布
  - [ ] Quilt / 自包含重建
  - [ ] 自动下载重试
  - [ ] 客户端自动更新
  - [ ] 设置页 / 强制更新
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
