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
