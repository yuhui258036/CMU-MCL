# 首次运行依赖引导器（本地预览）

本轮新增 `CMUmcl.Bootstrap/`，没有迁移 WPF/.NET 版本，没有修改线上服务或发布更新。主程序与独立更新器 DLL 保持上一轮已验收版本。

## 用户流程

完整解压后运行 **CMUmcl.Start.exe**：

1. 检测 x64 .NET 10 Core 与 Desktop Runtime。读取应用本地 `runtime/`、进程环境变量、官方 x64 注册表位置及标准安装目录，并使用微软 hostfxr 解析实际的 `CMUmcl.runtimeconfig.json`。
2. 可用时直接启动同目录的 `CMUmcl.exe`，引导器退出。选择的运行库路径只传给子进程，不修改系统环境变量。
3. 缺少依赖时显示说明、下载进度、取消、失败重试和微软下载页面入口。用户点击“下载并安装”后才下载和安装。
4. 从微软 HTTPS 固定地址下载 **.NET Desktop Runtime 10.0.14 x64**，当前安装包 60,035,296 字节，约 57.25 MiB；不下载 SDK。采用 Windows WinHTTP 自动代理，不读取客户端账号或代理密码。
5. 核对微软发布元数据中的 SHA-512，再使用 Windows Authenticode 验证信任链和签名证书组织 `Microsoft Corporation`。当前签名证书 CN 是 `.NET`，不能把 CN 错当作组织名。
6. 验证期间保持安装文件只读锁，防止验证后被改写；使用官方安装器 `/install /passive /norestart`，通过正常 Windows UAC 请求管理员确认。
7. 安装成功后重新检测，启动客户端。处理取消、安装错误及需要重启的返回值；不主动重启 Windows。安装开始后不强制终止安装器。

引导器为原生 Windows x64 程序，约 **169 KiB**，没有 CLR 入口或 .NET 导入；只使用 Windows 系统 DLL。现有 `CMUmcl.exe` 和 `updater/` 布局保持不变，正式快捷方式应指向 `CMUmcl.Start.exe`。

## 预览

- 入口：`../bootstrap-preview-20261031/预览依赖安装.lnk`
- 等价命令：`../bootstrap-preview-20261031/native/CMUmcl.Start.exe --preview`
- 失败演示：`--preview-failure`，首次演示中途失败，点击重试走成功流程。
- 演示明确提示“不会下载或安装组件”；进度为模拟，完成后自动打开已有隔离 WPF 预览，并关闭引导器。
- `native/preview.ini` 仅在明确的预览模式读取，指向隔离测试程序和 `interactive-preview/isolated-data`。正常运行不读取此配置。
- 完整本地候选包：`../bootstrap-preview-20261031/CMUmcl-with-bootstrap-local.zip`。正常入口是真实检测/安装流程，区别于演示快捷方式；它不是线上签名更新发布物。

## 已验证和限制

- 17 项原生程序内组件检查通过，包括版本/架构筛选、hostfxr 解析、实际微软安装包验证、篡改包哈希/签名拒绝、取消、失败重试、重复点击保护、完成状态和安装开始边界的取消竞争。
- 隔离子进程已收到正确的运行库路径；独立测试确认已有运行库会直接启动客户端，以及不兼容的 runtimeconfig 会被拒绝。
- 引导器自己的 WinHTTP 下载路径实际完成 60,035,296 字节下载，并通过两种验证。不是仅由 Python 下载器代替验证。
- 发布候选、交互预览及最终测试输出中的引导器已统一为同一 SHA-256。客户端及更新器 DLL 与上一轮 122 项设置/代理检查、70 项更新协议检查所用版本相同；本轮没有重复声称这些检查是新增测试。
- 自动化工具能读取实际预览的文字和控件。截图出现 `FrameArrived timed out` / `window capture timed out`，点击出现 `coordinate input geometry is unavailable`。因此取消/重试等由程序内组件测试验证，实际像素外观和用户点击完成路径仍需人工确认。
- **本机没有执行系统运行库安装、UAC 确认、重启或卸载。尚未在干净 Windows 虚拟机完成真实安装到启动的端到端验收。** 演示成功不能代替这项验收。
- 当前实现面向 Windows x64、.NET 10。下载使用系统代理，与安装运行库之后客户端内配置的代理是两套作用范围。
- 安装器版本与 SHA-512 固定在 `runtime-release.h`；每次发布检查微软补丁元数据并更新。未来修改客户端目标框架时，也要同步引导器检测和下载版本。

证据位于 `../bootstrap-preview-20261031/`：`final-tests/result.txt`、`native-download-test.json`、`bypass-tests.txt`、`native-imports.json`、`package-manifest.json`。

## 构建与测试

构建使用本地下载并按官方 SHA-256 验证的 Zig 0.13.0。编译器仅用于开发，不进入发行包，不需要在用户电脑安装。

```powershell
./CMUmcl.Bootstrap/build.ps1 -ZigPath <zig.exe> -OutputDirectory <输出目录>

./CMUmcl.Bootstrap/package.ps1 -ZigPath <zig.exe> -DotnetPath <dotnet.exe> -OutputDirectory <全新输出目录>

./CMUmcl.Bootstrap/test.ps1 -ZigPath <zig.exe> -RuntimeRoot <包含DesktopRuntime的dotnet目录> -InstallerPath <微软官方安装包> -RuntimeConfigPath <发布的CMUmcl.runtimeconfig.json> -OutputDirectory <全新测试目录>
```

不显示界面、不安装的诊断入口：

- `--self-test`：版本规则、x64、错误哈希、未签名文件拒绝。
- `--probe`：输出依赖就绪状态，返回 0 表示就绪、3 表示缺少或不兼容。
- `--verify-file <文件>`：验证固定官方安装包，不运行它。
- `--download-check <不存在的目标文件>`：实际下载并验证，不运行安装器。

参考：[微软 Windows 安装说明](https://learn.microsoft.com/en-us/dotnet/core/install/windows)、[.NET 10 发布元数据](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)。

## 确认后的文案与按钮行为

窗口标题为 `CMUmcl`，大标题为“准备运行环境”；正文为“启动器需要 Microsoft .NET Desktop Runtime 10 (x64) ，将从微软官方下载并安装。”，不再显示补充说明。正式初始状态为“未检测到兼容运行库。安装时 Windows 可能请求管理员确认。”；仅演示增加不实际安装的标识。

首屏按钮为“手动下载 / 退出 / 下载并安装”。空闲时“退出”直接关闭窗口，不开始下载。下载及检查安装包阶段，“退出”原位置变为“取消”；取消只终止本次准备，显示“已取消安装。安装运行组件后才能使用启动器。”，按钮恢复“退出 / 重试”。进入安装阶段后禁用取消，避免强制中断官方安装器。

状态用语采用“正在下载运行组件…”、“正在检查安装包…”、“请允许 Windows 安装提示，并等待安装完成。”、“安装完成，正在打开启动器…”。下载失败提示检查网络或手动下载；验证失败提示重新下载；需要重启时提示“安装完成，请重启电脑后重新打开启动器。”。正式流程与预览流程均在准备成功后自动打开对应客户端，无需第二次点击。

本次追加验收覆盖退出、取消按钮切换、安装阶段禁用取消和完成后自动启动；输出位于 `copy-tests-final/`。修改前源码及文档保存在 `copy-before/`，线上未变更。
