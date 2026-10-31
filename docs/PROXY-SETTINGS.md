# 启动器代理设置

入口为“设置 → 代理”。命名及字段参考 [HMCL DownloadSettingsPage](https://github.com/HMCL-dev/HMCL/blob/main/HMCL/src/main/java/org/jackhuang/hmcl/ui/main/DownloadSettingsPage.java)。

支持使用系统代理、不使用代理、HTTP、SOCKS5，以及主机、端口、身份验证、账户和密码。主机填写域名或 IPv4/IPv6，不含协议、路径或端口；端口范围 1～65535。有效配置自动保存并用于新请求，未填完或非法值保留最后一份有效配置并显示提示。保存失败沿用设置页重试机制。

默认使用系统代理，遵循 .NET 的环境变量/Windows 用户代理解析；“不使用代理”明确设置 `UseProxy=false`，不再探测本机 7890/7891/1080。旧 `CMUMCL_PROXY` 专用变量不再使用；标准 `HTTP_PROXY`、`HTTPS_PROXY`、`ALL_PROXY`、`NO_PROXY` 只在系统代理模式中按 .NET 规则生效。不会修改 Windows 代理设置，也不能绕过系统 VPN/TUN。

## 请求范围与生命周期

登录认证、资源清单、整合包及单文件下载、mrpack 导入、版本列表、Java/Forge/Fabric API 下载、客户端更新、背景图片统一使用 `Network/ProxyRoutingHandler`。CmlLib 的原版版本查询和下载通过 `MinecraftLauncherParameters.CreateDefault(path, httpClient)` 注入同一客户端；第三方内部默认 HttpClient 使用进程级 `IWebProxy` 后备。

代理设置改变后，下一个请求使用新的连接池，包括代理账户/密码变更。旧池等待现有响应或流释放后销毁，不取消已经开始的下载。设置不自动重试已经失败的请求，可重新点击下载/刷新。第三方库内部已有的连接可能继续复用，必要时重试或重启启动器。设置不会为启动后的 Java 游戏进程或其子进程设置网络代理。

代理密码用 DPAPI CurrentUser 加密后保存到 `proxyPasswordProtected`，不放入 URL 或源站 Authorization 头；界面使用 PasswordBox。配置复制到另一 Windows 用户/机器后若密码无法解密，需重新输入；错误时不静默直连。系统代理凭据由系统代理实现提供。

网络层仍执行原来的 HTTPS、重定向、签名、哈希和下载大小校验，没有新增 TLS 证书绕过。

## 本地验收（2026-10-31）

发布版本设置/背景/代理验收 **114 项通过**，资源下载与跨后端发布安装回归 **62 项通过**。代理测试使用仅监听 127.0.0.1 的临时 HTTP 源站及 HTTP/SOCKS5 服务，覆盖：

- 已创建客户端在直连、HTTP 和 SOCKS5 间切换。
- HTTP 407、SOCKS5 账户密码认证；HTTPS 经 CONNECT，请求被拒绝时不改走直连。
- 修改设置时原有流式下载继续完成，GetStreamAsync 的流可以正常读取和释放。
- 代理凭据不发送给直连源站，配置不包含明文测试密码。
- 非法保存配置不静默回退直连，设置控件验证及自动保存。
- 第三方默认 HttpClient 遵循进程级代理。

本轮没有使用真实账号经公网代理登录，没有进行真实游戏启动或线上部署。HTTPS 用本地代理拒绝 CONNECT 的用例检查路径和失败行为，没有绕过证书验证搭建伪 TLS。

候选、预览、截图及日志在 `F:\Projects\all\csumc\proxy-settings-20261031`。启动入口为 `启动开发预览.ps1`；预览继续使用独立数据目录。

## 占用快照

2026-10-31 22:33（Asia/Shanghai），新版独立预览打开主页并显示网络背景，间隔 5 秒采样三次：工作集 187.08～202.78 MiB，私有内存 200.52～215.75 MiB。不包含 Minecraft Java 进程，也不包含其他旧验收窗口；不是整合包安装/游戏启动时的峰值测量。

| 内容 | 文件大小合计 |
| --- | ---: |
| 正式候选目录，含依赖及更新器 | 3.39 MiB |
| 独立开发预览程序 | 4.29 MiB |
| 当前预览设置与背景缓存 | 0.20 MiB |
| 共享 .NET Core 10.0.14 文件 | 76.32 MiB |
| 共享 Windows Desktop 10.0.14 文件 | 94.41 MiB |
| 本地开发 tools，含 SDK/NuGet 等 | 1474.72 MiB |

这是框架依赖发布，3.39 MiB 不包含共享 .NET 运行时；运行环境主要共享文件合计 170.73 MiB。开发 SDK/NuGet 不需要分发给用户。实际游戏、Java、模组、世界、整合包缓存另外占用空间，当前预览没有安装真实游戏。背景 API 随机返回不同图片，其解码大小及缓存大小会使内存与磁盘数据变化。统计为文件逻辑大小，不是 NTFS 分配簇占用；原始采样见 `resource-usage.json`。

参考：[.NET HttpClient.DefaultProxy](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.defaultproxy?view=net-10.0)、[CmlLib 4.1.0 实际版本的注入入口](https://github.com/CmlLib/CmlLib.Core/blob/ffa2e908a9aa545c13175ec7b700472220e6d280/src/LauncherParameters.cs)。
