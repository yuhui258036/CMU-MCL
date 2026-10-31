# 本地内存与发行体积检查（2026-10-31）

本轮仅修改和测试本地客户端，没有部署、修改线上服务或推送代码。修改前源码备份位于 `../memory-review-20261031/source-backup/`；此前工作区的其他改动保留。

## 占用来源和改动

对已有开发预览 PID 291736 采集了 System.Runtime 计数器，原始数据为 `../memory-review-20261031/live-runtime.json`。该次工作集约 194–196 MiB；GC 最近一次回收后的堆约 7.4–10.8 MiB，GC 提交约 21–24 MiB。不能将进程工作集全部归因于 C# 堆，更不能将虚拟地址空间当成实际 RAM。余下占用包含 WPF 原生图形、图像、运行库映射等；本轮没有做能精确拆分各原生分配栈的堆转储。

已处理的具体问题：

- 原先每次设置保存、背景加载状态变化都会重新发布所有主题资源，引发 WPF 动态资源刷新。现在只有主题/有无背景变化才重建调色板，只有实际图像/透明度/主题变化才重建背景画刷，并冻结可共享画刷。
- 42 DIP 的品牌图标原先直接解码 1172×1172 图片。现在限制解码到 168 像素，保留到 400% 缩放的像素余量；单张 32 位像素数据理论上由 5.24 MiB 降至约 0.11 MiB，不改变原始资源文件。
- 网络背景原先先解码验证，再重新解码用于显示。现在复用验证通过的冻结位图；仍保留尺寸限制、重定向校验、取消、失败保留旧缓存等行为。
- 当所有受跟踪的主窗口/登录窗口隐藏或最小化时，停止自动背景轮换定时器；恢复窗口后重新计时。不丢弃当前壁纸、不改变自动换图偏好，下载、更新及代理传输不由该暂停机制控制。

没有增加周期性强制 GC 或工作集清空；也没有降低壁纸原有解码上限、取消功能或清除用户数据。

## 三轮对照

使用相同 Release 隔离测试程序、同一份固定本地壁纸和 1200×800 窗口。假账号与整合包清单均来自本地 fixture。窗口置于屏幕外避免打扰，分别经过 Normal、Minimized、Normal 状态；这不是前台真实用户或游戏运行场景。每轮为新进程，顺序采样：初始空闲 4 秒、重复应用未变设置 100 次后空闲 4 秒、最小化 10 秒、恢复 4 秒。截图在最后一次采样后生成，未调用强制 GC。

下表为三轮中位数，单位 MiB：

| 场景 | 原工作集 | 新工作集 | 原私有提交 | 新私有提交 |
| --- | ---: | ---: | ---: | ---: |
| 初始空闲 | 184.67 | 160.38 | 173.95 | 154.73 |
| 100 次未变设置应用后 | 177.03 | 153.81 | 163.63 | 145.75 |
| 最小化空闲 | 184.16 | 166.27 | 177.79 | 165.21 |
| 恢复窗口后 | 184.59 | 168.28 | 178.76 | 165.24 |

初始空闲工作集下降约 13%，最小化下降约 10%。最小化后占用不保证低于初始状态，WPF 和操作系统可能重新分配或保留图形缓存。

100 次相同设置重应用的首轮累计托管分配：484,683,968 字节 → 48,456 字节，耗时约 1098 ms → 0.23 ms。这是明确的压力场景，累计分配不是常驻内存，也不是默认空闲时每秒消耗。最小化 10 秒 CPU 时间中位数约 0.125 s → 0.078 s；样本很短，不据此承诺长期 CPU 百分比。

原始数据和汇总：`../memory-review-20261031/{before,after}-{1,2,3}/`、`comparison.json`。测试入口为 `tests/LauncherSettings.Tests/PerformanceProfile.cs`，运行参数 `--profile`，固定图片环境变量 `CMUMCL_PROFILE_IMAGE`。

## 发行体积和 .NET

增加 `CMUmcl/Properties/PublishProfiles/SmallFrameworkDependent.pubxml`，使用共享运行库、正常 apphost，关闭发行调试符号。不使用 WPF 裁剪或 Native AOT。独立更新器继续发布到 `updater/`，并继承调试符号设置。

```powershell
dotnet publish CMUmcl -c Release -p:PublishProfile=SmallFrameworkDependent -o <空的输出目录>
```

本轮 Windows x64 SDK 产物：

| 内容 | 之前 | 本轮 |
| --- | ---: | ---: |
| 客户端解压体积（含独立更新器） | 3.387 MiB | 3.198 MiB |
| ZIP（相同 Deflate 级别） | 2.189 MiB | 2.078 MiB |

发行 ZIP 为 `../memory-review-20261031/CMUmcl-local-small.zip`，文件清单和 SHA-256 为同目录 `package-manifest.json`。它不是签名的线上更新发布物。

`CMUmcl.runtimeconfig.json` 要求 .NET 10 的 `Microsoft.NETCore.App` 和 `Microsoft.WindowsDesktop.App`。当前本来就是 framework-dependent 应用，未捆绑运行库；缺少运行库时 apphost 可提供微软下载链接/Windows 提示。应安装 **.NET Desktop Runtime 10 x64**，不需要开发 SDK，单独普通 .NET Runtime 不包含 WPF。

如果需要“首次启动自动检测、下载、安装后继续启动”的完整体验，需要一个不依赖 .NET 的小型原生引导器或安装器。它应复用已装运行库、从微软官方获取安装包并验证签名，再由用户确认安装；主程序不能在自己必需的运行库缺失时执行这些逻辑。本轮保留官方缺失运行库提示，没有新增自动安装器，也没有在干净 Windows 系统验收该下载界面。

按需下载可以缩小分发包，但运行库安装后仍占磁盘，且不会因此降低运行中的 RAM。本机 .NET 10.0.14 两个共享运行库目录合计约 170.73 MiB，另外还有 host 等文件；可以由同架构、兼容版本的程序共享。`local-preview/tools` 是开发 SDK、NuGet 和下载缓存，约 1.46 GiB，不属于用户发行包；本轮没有删除它。

## Fluffy 压缩包核查

只读检查用户提供的 `D:\GameMods\MonsterHunterWilds\Fluffy Mod Manager-818-3-054-1741022481.zip`（3,164,124 字节），未运行其中程序。

包内主程序为 x64 原生 PE，依赖 OpenGL、FMOD、UnRAR 等，没有 CLR 目录，也没有 `.runtimeconfig.json`、`.deps.json` 或发现 hostfxr/mscoree 引导标记。更新器包含 `VCRUNTIME140.dll` 依赖。综合导入表、包结构和说明文件，这个版本不属于可确认的“.NET 首次启动下载”范例；可能记忆中看到的是 Visual C++ 依赖或更新下载，静态检查不能证明历史交互过程。

## 验收与本地入口

- 122 项设置/代理/背景/WPF 检查通过，新增无变化资源复用、画刷冻结、暂停定时器、设置保存不重新唤醒、多个窗口可见性和恢复后保留图片检查。
- 用最终发布包的 `CMUmcl.dll` 替换隔离预览 DLL 后再次通过同样 122 项；两份 DLL SHA-256 相同。结果：`../memory-review-20261031/shipping-dll-regression/result.txt`。
- 70 项更新协议检查通过，未执行真实线上更新。
- 检查了恢复后的主页面和暗色设置页面截图；测试截图中的个别历史文件名不代表当前实际主题。
- 新预览入口：`../memory-review-20261031/启动开发预览.ps1`。使用独立 `interactive-preview/isolated-data`，复制此前预览偏好与背景，未复制账号文件，也未替换旧预览进程。

参考：[微软 WPF 图形优化](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-2d-graphics-and-imaging)、[缺少 .NET 时的启动与下载行为](https://learn.microsoft.com/en-us/dotnet/core/runtime-discovery/troubleshoot-app-launch)。
