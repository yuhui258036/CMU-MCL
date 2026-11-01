# PCL-CMU 魔改版 — Local Resource Test Report

> 以下原有报告是早期 PCL 原型的历史记录，不代表当前 `CMUmcl` WPF 客户端的框架版本、目录或本次验收。

## 2026-10-31 本地下载改进（未提交、未部署）

当前客户端目标为 .NET 10 Windows，使用隔离的 SDK 10.0.403 构建。新增 `tests/ResourceDownload.Tests` 引用实际客户端项目，覆盖清单/签名过渡期重试、强制验签、响应体大小限制、断线后的 Range、取消后重新续传、服务器忽略 Range、错误 Content-Range、资源哈希变化、原文件保护、有限重试和 HTTPS 备用源。

```sh
dotnet run --project tests/ResourceDownload.Tests -c Release
# 可选：与已构建的新版后端进行上传 -> 签名清单 -> 完整/增量安装联测
dotnet run --project tests/ResourceDownload.Tests -c Release -- --backend-assembly /absolute/path/PclCmu.Admin.dll
dotnet run --project tests/LauncherUpdate.Tests -c Release
dotnet build CMUmcl/CMUmcl.csproj -c Release
```

资源测试基础 39 项，其中 7 项直接调用新版 `LauncherUpdateClient.StageAsync`，验证取消后从原位置继续、完整签名 ZIP 与清单交付、解压预检清理、错误压缩包签名和版本拒绝。附加新版后端联测后共 **62 项通过**：真实临时后端接收乱序块并发布三次；实际 `ResourceManifestClient` 验签、`PackInstaller` 首次完整安装、相同 overrides 时仅索引增量更新、删除旧受管文件、保留用户配置与世界、overrides 改变后回退整包并应用新配置。使用临时签名密钥及测试专用处理器，将指定 HTTPS 测试域名转发到自有回环 HTTP 端口；生产 URL 白名单与公钥不变，不将此结果当作真实 CDN/TLS 验收。

既有启动器更新协议/事务测试 70 项通过，客户端构建为 0 警告、0 错误。后端另有真实 Chromium 经本地 HTTPS 代理上传、暂停/刷新续传，以及 .NET 9.0.22 Linux 容器与跨文件系统验收；真实 CDN 与 Linux 生产服务尚未部署这些改动。旧开发预览是独立副本，不会随工作树修改自动更新。

实际设置页升级由用户在独立 `LauncherUpdate.UiAcceptance` 程序中操作：当前源码构建的 0.1.0 正常退出（退出码 0），签名临时包替换为 0.2.0 并重开；新 DLL 与包内 DLL 哈希一致、备份 DLL 与原始 DLL 哈希一致，独立数据根目录和安装目录的测试文件均保留。该测试默认用本地 `HttpMessageHandler` 提供清单和包，临时私钥只在生成夹具时存在于内存，不读取真实账号、生产私钥或生产下载源。框架依赖的验收副本使用隔离 .NET 10 Desktop 运行时。没有启动 Minecraft。

这次界面操作只产生一次完整 ZIP 请求，没有 Range 记录，因此 UI 取消后续传仍未实测，不能与 StageAsync 的程序级取消/续传证据混同。最初自动操作窗口失败的尝试已中止，不计为正常退出通过；成功升级来自之后用户操作的可见副本。用户按 Esc 停止电脑操作后未再继续操作界面。

UI 夹具生成器现在不依赖旧开发机 `.tmp` 配置/生产样本路径：使用常规 NuGet 配置、内存临时签名、自包含或 `CMUMCL_UI_SELF_CONTAINED=false` 框架依赖 updater；默认资源清单也由本地夹具提供。测试传输支持 Range，设置 `CMUMCL_UI_FIXTURE_TIMEOUT_SECONDS`（30–1800）可限制验收窗口运行时间；生产客户端不包含此入口。

整合包、模组和启动器更新使用相同的有界下载器：按受信任哈希与期望大小绑定断点，在校验成功后才替换目标文件；只对瞬时网络/服务错误有限重试。已有文件流式校验，避免按文件大小分配数组。已验签的清单与签名不匹配时最多读取三对，并在重试时要求重新验证缓存。

增量 JSON 索引单独限制为最多 32 MiB 缓冲及 45 秒请求时间，避免误把大响应按整包超时缓冲进内存；整合包和模组仍按流下载，不受 JSON 缓冲上限影响。

---

Actual commands and results from this run.

**Environment**

* OS: Windows 11 (PowerShell 5.1, build 26100)
* SDK: .NET 9.0.308 (no .NET 10 required)
* Target frameworks: `net9.0`

---

## 1. Commands run

```powershell
# (execution policy on this machine blocks unsigned scripts; bypass per-process)
Set-ExecutionPolicy -Scope Process Bypass -Force

# Generate pack + manifest, signing key pair, and signed update metadata
powershell -File scripts\build-pack.ps1
powershell -File scripts\generate-keys.ps1
powershell -File scripts\sign-update.ps1

# Run the full protocol/security suite (builds + starts server + tests + stops)
powershell -File scripts\verify.ps1
```

`scripts/generate-keys.ps1` output:

```
wrote private key: D:\新启动器\resource-site\keys\update-signing-private.pem
wrote public key:  D:\新启动器\client-harness\update-signing-public.pem
```

`scripts/build-pack.ps1` output:

```
built  D:\新启动器\resource-site\wwwroot\packs\test\1.mrpack
  size=312
  sha1=e62e6b6aa61d0e376fd206f4c4bfa951b8d9ffa2
updated D:\新启动器\resource-site\wwwroot\manifest.json
```

`scripts/sign-update.ps1` output:

```
signed D:\新启动器\resource-site\wwwroot\update\windows-x86_64\PCL-CMU-launcher-0.1.0-test.bin
wrote  D:\新启动器\resource-site\wwwroot\update\windows-x86_64\latest.json
```

---

## 2. `scripts/verify.ps1` output

```
=== building ===
=== TLS certificate ===
loopback cert already trusted (thumbprint 4F7D58484430D515E1799057A0405AEC96842057)
=== reset state ===
=== starting server ===
[PASS] listen only on loopback — listeners=127.0.0.1:5443, 127.0.0.1:5080
[PASS] GET /manifest.json -> 200 JSON no-cache ETag — status=200 cache-control='no-cache' etag="1dd662cd3dc3233"
[PASS] conditional GET -> 304 — status=304
[PASS] GET /packs/test/1.mrpack -> 200 len ranges immutable — status=200 content-length=312 accept-ranges='bytes' cache-control='public, max-age=31536000, immutable'
[PASS] pack sha1 matches manifest — actual=e62e6b6aa61d0e376fd206f4c4bfa951b8d9ffa2 expected=e62e6b6aa61d0e376fd206f4c4bfa951b8d9ffa2
[PASS] pack size matches manifest — actual=312 expected=312
[PASS] client-harness protocol run — exit=0
revision: 1
download: ok
sha1: ok
size: ok
server: https://127.0.0.1:5443
update: ok
signature: ok
[PASS] traversal paths -> 404 — /../appsettings.json=404 /packs/../manifest.json=404 /%2e%2e/manifest.json=404
[PASS] GET /packs/ -> 404 (no listing) — status=404
[PASS] GET /unknown -> 404 — status=404
[PASS] HTTP -> HTTPS redirect (301/308) — status=308 location=https://127.0.0.1:5443/manifest.json
[PASS] no secrets in wwwroot JSON
[PASS] private signing key outside wwwroot — privateKey=D:\新启动器\resource-site\keys\update-signing-private.pem keyFilesInWwwroot=
[PASS] server running during tests — pid=16616
[PASS] no server process left after stop — listeners=

=== SUMMARY ===
total=15 passed=15 failed=0
ALL TESTS PASSED
```

`client-harness` (run independently) output:

```
revision: 1
download: ok
sha1: ok
size: ok
server: https://127.0.0.1:5443
update: ok
signature: ok
```

---

## 3. Results table

| # | Check | Result |
|---|-------|--------|
| 1 | Server listens only on loopback (`127.0.0.1:5443`, `127.0.0.1:5080`) | PASS |
| 2 | `GET /manifest.json` → 200, JSON, `Cache-Control: no-cache`, ETag present | PASS |
| 3 | Conditional GET `If-None-Match` → 304 | PASS |
| 4 | `GET /packs/test/1.mrpack` → 200, Content-Length 312, `Accept-Ranges: bytes`, immutable | PASS |
| 5 | Pack SHA-1 == manifest `sha1`; size == manifest `size` | PASS |
| 6 | `client-harness` run (revision/download/sha1/size/update/signature) | PASS |
| 7 | `GET /../appsettings.json`, `/packs/../manifest.json`, `/%2e%2e/manifest.json` → 404 | PASS |
| 8 | `GET /packs/` → 404 (no directory listing) | PASS |
| 9 | `GET /unknown` → 404 | PASS |
| 10 | `http://127.0.0.1:5080/manifest.json` → 308 to HTTPS | PASS |
| 11 | Scan `wwwroot` JSON for `token`/`password`/`secret`/`Authorization` → none | PASS |
| 12 | Private signing key outside `wwwroot` | PASS |
| 13 | No server process left after tests | PASS |

---

## 4. SSL trust caveat

* The default `dotnet dev-certs https` certificate covers only `localhost`;
  because the endpoint is `https://127.0.0.1:5443`, the harness generates a
  self-signed loopback certificate (`DNS:localhost`, `IP:127.0.0.1`, `IP:::1`)
  and trusts it in `CurrentUser\Root` (no UI prompt). Certificate validation is
  **not** disabled on the client.
* Manual trust equivalent: `dotnet dev-certs https --trust` (for `localhost`),
  or import `resource-site/certs/loopback.cer` into `Cert:\CurrentUser\Root`.

## 5. Local run command

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
powershell -File .\scripts\verify.ps1
```

On success it prints `ALL TESTS PASSED` and exits 0; it leaves no server
process behind.

## 2026-10-31：即时保存与整窗背景

本轮取消显式保存/还原按钮，设置修改通过验证即保存；主题切换保留页面、标签和滚动位置。增加整窗背景、本地多图轮换和网络图片 API，采用 HMCL 设置命名及 CC Switch 中性色布局。行为和限制见 [UI-RUNTIME-REFRESH.md](UI-RUNTIME-REFRESH.md)。

- 最终发布程序集的设置/WPF/背景验收：**93 项通过**，包含保存失败重试、重开加载、关闭前 Java 检测、目录选择、新建页面遮挡恢复、多图计时轮换、离线缓存与过期请求竞态。
- 实际 `https://acg.neko-arc.top/api.php`：已通过客户端真实控件输入、下载、解码和全窗显示验收，截图见 `ui-autosave-20261031/release-acceptance/network-background.png`。最后一次修改只修复新建页内容遮挡，最终离线复验未重复请求远程 API。
- 原有资源下载/跨后端发布安装：**62 项通过**；客户端更新协议：**70 项通过**。
- Release 构建无警告、无错误；正式候选与预览的 `CMUmcl.dll` SHA-256 一致，见外层 `ui-autosave-20261031/artifact-manifest.json`。
- 最终截图与测试结果：`ui-autosave-20261031/final-acceptance`；完整日志：`settings-final.log`、`resource-regression.log`、`update-regression.log`。

测试全部使用隔离数据目录和测试实例。本轮未验证实际游戏登录/启动、系统主题事件或真实 EdgeOne 链路，未修改线上服务，未提交/推送代码。

2026-10-31 界面细节：版本标签移到大小与服务器之间；标题栏删除背景切换并统一缩小矢量图标；自动轮换默认关闭。Release 构建成功，既有 93 项设置验收通过，已检查首页截图和预览运行状态。日志与截图：`ui-polish-20261031/acceptance`。正式候选与预览 DLL 哈希一致，Lucide 许可证随发布输出。

2026-10-31 对比度修正：分割线使用独立动态颜色，设置标签增加底色，辅助文字及半透明表面提高对比度。以当前缓存背景、100% 图片不透明度检查亮暗截图；发布构建及既有 93 项设置验收通过，候选与预览 DLL 哈希一致。结果位于 `ui-contrast-20261031`。

2026-10-31 代理设置：新增系统/不使用/HTTP/SOCKS5、主机端口及加密身份验证，即时保存。发布版设置与代理验收 114 项、资源下载回归 62 项通过；未改变线上服务或系统代理。实现边界和实际资源占用见 [PROXY-SETTINGS.md](PROXY-SETTINGS.md)。产物 `proxy-settings-20261031`。
