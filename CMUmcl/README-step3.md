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
