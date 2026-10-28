# CMUmcl — 步骤二：游戏启动（原版 / Fabric）

本步骤在登录门 + 服务器整合包安装之上加入**游戏启动**能力。
使用 [CmlLib.Core](https://github.com/CmlLib/CmlLib.Core) 4.1.0（MIT）完成
原版 / Fabric 的版本安装与进程启动，并沿用已有的 CSU-MC Yggdrasil 登录会话，
通过 authlib-injector 注入认证，实现皮肤站正版登录联机。

## 支持的加载器

| 加载器 | 状态 |
|--------|------|
| 原版（`vanilla` / 空） | ✅ 支持 |
| Fabric | ✅ 支持（使用 CmlLib.Core 内置 `FabricInstaller`） |
| Forge / NeoForge | ❌ 下一步接入（UI 与启动时均提示） |
| Quilt | ❌ 下一步接入 |

遇到 Forge / NeoForge / Quilt 时，界面与启动流程都会明确提示：
`当前版本先支持原版/Fabric，Forge/NeoForge 下一步接入。`（不会静默失败）。

## 依赖

- NuGet `CmlLib.Core` 4.1.0（MIT）。Fabric 安装器包含在核心库中。
- **未**引入 `CmlLib.Core.Auth.Microsoft`；登录仍使用 CSU-MC 皮肤站的经典 Yggdrasil 会话。
- `authlib-injector.jar`（1.2.8）作为应用内容随包分发：
  - 源文件：`Assets\authlib-injector.jar`（`CopyToOutputDirectory=PreserveNewest`）。
  - 运行时复制到 `%LOCALAPPDATA%\CMUmcl\authlib-injector.jar`（缺失或 SHA-256 不匹配时复制并校验）。
  - SHA-256：`9C7F4343E6C82034958FFB48C14A2CB0C85928BE7283103CE17DA00C6D5A7B10`。

## Java 检测

`Launch\JavaLocator.cs` 按以下顺序查找 `javaw.exe`（优先）：

1. `JAVA_HOME\bin\javaw.exe`
2. `C:\Program Files\Java\jdk-21\bin\javaw.exe`
3. `C:\Program Files\Java\jdk-17\bin\javaw.exe`
4. `C:\Program Files\Java\jdk-22\bin\javaw.exe`
5. `C:\Program Files\Java\jdk-26\bin\javaw.exe`
6. `C:\Program Files\Java\` 下任意 JDK 目录（含 `jdk-26.0.2.1`、`latest` 等）
7. `PATH` 中的 `java`（并尝试同目录 `javaw.exe`）

找不到时抛出中文错误并提示安装 Java 17/21、设置 `JAVA_HOME`。

## 启动流程（`Launch\LaunchService.cs`）

1. 校验已安装 revision == 清单 revision，否则提示“请先安装 / 更新”。
2. 读取实例 `instance.json`，取 game / loader / loaderVersion / server。
3. 启动前校验会话：`/authserver/validate`；403 时 `/authserver/refresh` 并 `AuthStore.SaveSession`；刷新失败提示“登录已过期，请重新登录”。
4. 准备 authlib-injector（复制 + SHA-256 校验）。
5. 定位 Java。
6. 共享 Minecraft 目录 `%LOCALAPPDATA%\CMUmcl\minecraft`（versions/libraries/assets/runtime）。
7. `launcher.InstallAsync(gameVersion, …)` 安装原版。
8. Fabric：`FabricInstaller.Install(gameVersion, loaderVersion, minecraftPath)` 得到版本名，再 `InstallAsync(versionName, …)`；原版直接用 `gameVersion`。
9. `new MSession(角色名, accessToken, selectedProfileId)`。
10. 组装 `MLaunchOption`：
    - `Session`、`Path = launcher.MinecraftPath`、`JavaPath = javaw.exe`
    - `MaximumRamMb = 4096`、`MinimumRamMb = 512`
    - `GameLauncherName = "CMUmcl"`、`GameLauncherVersion = "0.1.0"`
    - `ExtraJvmArguments` 追加 `-javaagent:{authlib.jar}={Yggdrasil API root}`
    - `ArgumentDictionary["game_directory"] = 实例目录`（mods/config 放在实例目录，Minecraft 本体共享）
    - 服务器地址非空且非 `127.0.0.1`/`localhost` 时设置 `ServerIp`/`ServerPort` 快速加入
11. `BuildProcessAsync` + `process.Start()`，返回 `LaunchResult`（进程 / 会话 / 版本名）。

## 内存默认值

- 最大内存：4096 MB
- 最小内存：512 MB

## 目录结构（运行时）

| 用途 | 路径 |
|------|------|
| 共享 Minecraft | `%LOCALAPPDATA%\CMUmcl\minecraft` |
| authlib-injector | `%LOCALAPPDATA%\CMUmcl\authlib-injector.jar` |
| 实例目录 | `%LOCALAPPDATA%\CMUmcl\instances\{id}` |
| 整合包缓存 | `%LOCALAPPDATA%\CMUmcl\cache\packs\{id}\…` |
| 已安装状态 | `%LOCALAPPDATA%\CMUmcl\installed.json` |

## 构建与运行

```powershell
$env:DOTNET_ROOT = "C:\Users\PC\AppData\Local\Microsoft\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:NUGET_PACKAGES = "D:\新启动器\.nuget\packages"   # 若默认缓存不可写
& "$env:DOTNET_ROOT\dotnet.exe" build "CMUmcl\CMUmcl.csproj" -c Debug
& "CMUmcl\bin\Debug\net10.0-windows\CMUmcl.exe"
```

## 如何测试

1. 登录 CSU-MC 皮肤站账号。
2. 主界面选中整合包，点击 `下载并安装`（首次）或 `更新`。
3. 安装完成后按钮变为 `启动游戏`。
4. 点击 `启动游戏`：依次安装原版（/ Fabric）、校验登录、注入 authlib-injector、启动进程。
5. 已安装当前版本时直接 `启动游戏`；启动后状态显示“游戏已启动”。

## 后续工作（Forge / NeoForge）

- 下一步接入 Forge / NeoForge 安装与启动（CmlLib 相关 modloader 或自建安装流程）。
- 可考虑进程退出状态跟踪、崩溃日志展示。

## 安全说明

- 登录会话沿用 DPAPI 加密存储，密码从不落盘。
- authlib-injector 在运行时按 SHA-256 校验，确保未被篡改。
- 启动参数注入 `-javaagent` 指向皮肤站 Yggdrasil API root。
