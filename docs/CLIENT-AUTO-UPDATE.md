# 客户端自动更新（2026-10-31）

> **2026-10-31 真实生产更新验收通过**：单次完整 CDN 下载 219 秒、双签和哈希通过；真实 0.1.0 自包含客户端从生产通道升级至 0.2.0 并自动重开，旧/新进程退出码均为 0，无强杀。源站本机读取快，公网速度波动且更新 ZIP 为 no-cache/MISS；缓存优化尚未实施。两个真实测试实例及安装记录保持不变，未读取凭据或私钥。详见 `docs/CLIENT-UPDATE-LIVE-ACCEPTANCE-2026-10-31.md`。下方较早的未完成记录为历史状态。

实现最初在独立工作区开发，2026-10-31 已合回 `D:\新启动器`，并在原目录重新完成构建和隔离验收。合回前的原文件及补丁备份保存在 `backups/client-update-merge-20261031-015905/`。服务端和被忽略的本地目录继续整体保留；未读取指定凭据或密钥目录，没有提交、推送或上线。

## 当前交付状态

- 启动主窗口时异步检查；检查失败仅显示在设置页，不阻止游戏功能。
- 设置页手动检查、下载进度和取消；校验后询问是否关闭并更新。
- 包下载到临时目录，不边下载边覆盖安装目录。
- 独立 updater 在临时目录运行；重新验签、解压预检、等待父进程退出并取得安装互斥锁后替换。
- 备份所有将覆盖的旧文件；替换失败自动回滚，保留备份和恢复记录。
- 成功后重启新客户端；重启失败会提示手动打开。
- 文件先写入同目录临时文件，再替换目标；文件锁失败时保留未被替换的旧文件。
- 下载、解压、备份/替换以及 updater 复制前检查可用空间，额外保留 64 MiB；预检失败时不开始覆盖程序。
- 只覆盖新包中的程序文件；不删除旧依赖或用户额外文件。
- 新增按安装目录的单实例锁，避免更新期间另一个客户端打开同一套程序。
- 可用 `--data-directory <绝对路径>` 指定隔离数据根目录；认证存储也使用该根目录，更新重启会保留该参数。默认路径不变。

**尚未上线**：CDN 现有 39 字节测试文件不能使用。新格式必须先由发布端生成；首次新版仍需手动分发。当前版本号仍为 `0.1.0`，正式发布更新时必须显式递增。

## 更新清单 v2

固定入口：`https://csumcl.mcapcs.cn/update/windows-x86_64/latest.json`。

```json
{
  "payload": "<下面 release JSON 的原始 UTF-8 字节的 Base64>",
  "signature": {
    "algorithm": "ECDSA-P256-SHA256",
    "format": "ieee-p1363-base64",
    "value": "<对 payload 解码后原始字节的 ECDSA 签名>"
  }
}
```

release JSON：

```json
{
  "formatVersion": 2,
  "platform": "windows-x86_64",
  "version": "0.2.0",
  "url": "https://csumcl.mcapcs.cn/update/windows-x86_64/CMUmcl-0.2.0.zip",
  "size": 123456789,
  "sha256": "<ZIP 的 64 位十六进制 SHA-256>",
  "archiveSignature": {
    "algorithm": "ECDSA-P256-SHA256",
    "format": "ieee-p1363-base64",
    "value": "<ZIP 原始字节的 ECDSA 签名>"
  },
  "notes": "版本说明"
}
```

签名均为 NIST P-256 / SHA-256 / IEEE P1363 的 64 字节 `r || s`，再转 Base64。ZIP 可流式计算 SHA-256 后使用 `ECDsa.SignHash`，等同于对完整 ZIP 使用 `SignData`。payload 不做重新序列化：发布端先生成最终 UTF-8 字节，再签名和 Base64。这样版本号、平台、URL、大小和哈希也受签名保护。

客户端嵌入的是现有 `client-harness/update-signing-public.pem` 的公开公钥，与整合包 manifest 公钥独立。生产公钥选用需要可信确认，不能从联网清单自动下载公钥替代此步骤；本次确认和公开样本验签结果如下。

**2026-10-31 后续验收**：用户已明确确认生产沿用当前公钥，SPKI SHA-256 为 `20C98D68C2E761E57EC40C3AD0A173EDE4528279464A5E643E456C5DE3C47583`。嵌入值、公开 PEM 一致，CDN 现有已签测试文件验签通过；无需替换当前公钥。新的生产 v2 清单及真正 ZIP 尚未提供，仍需对其双签验证，不能把公开测试样本等同于新生产包验收。详见 `docs/PRODUCTION-UPDATE-ACCEPTANCE-2026-10-31.md`。

只接受三段/四段数字版本，按数字比较；相同版本、降级和 `0.1.0-test` 等预发布字符串不安装。ZIP 必须在根目录包含 `CMUmcl.exe`、`CMUmcl.dll` 和 `updater/CMUmcl.Updater.exe`，DLL 程序集版本必须与签名版本一致。包大小不超过 1 GiB，解压不超过 4 GiB，最多 20000 个条目。

下载仅允许 CDN 指定 `/update/windows-x86_64/*.zip` HTTPS 地址，不跟随重定向；保留正常 TLS 验证。不兼容旧的未签名元数据格式，旧测试清单会被安全拒绝。

## 构建与发布

源码保持 `net10.0-windows`。在具备 .NET 10 SDK 的原工作区执行：

```powershell
$dotnet = 'C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe'
& $dotnet build .\CMUmcl\CMUmcl.csproj -c Debug
& $dotnet publish .\CMUmcl\CMUmcl.csproj -c Release -r win-x64 --self-contained true -p:Version=0.2.0 -o .\dist\CMUmcl-0.2.0
```

发布目标会将独立 updater 发布到输出的 `updater/`，包含自身运行时；更新时整目录复制到临时目录运行，避免正在运行的 updater 锁住安装目录。

将**发布目录中的内容**打包到 ZIP 根目录，不在 ZIP 外包一层目录。离线签署 ZIP 和 v2 payload；先上传不可变版本 ZIP，最后更新 `latest.json`。更新入口需配置 no-cache，规则应匹配 `/update/windows-x86_64/latest.json`。本次未上传、签署或发布任何生产更新。

## 验证

运行 `dotnet run --project tests/LauncherUpdate.Tests`（测试项目使用可用的 .NET 9 SDK，无第三方依赖）。测试使用内存中新生成的临时密钥，不触碰生产密钥或真实实例。

已验证签名篡改、错误哈希/大小、错误平台、版本比较、恶意 URL、ZIP 路径穿越/设备名称/重复名称/符号链接、缺失程序、替换失败回滚、删除本次新增文件、设置/实例/用户额外文件保留，以及备份不被覆盖。

最终结果：68 项核心断言通过；独立下载并校验微软官方 SHA-512 的 .NET SDK 10.0.403 / Runtime 10.0.14 完成 win-x64 自包含发布，客户端与 updater 均带独立运行时。仅有已知 NU1510 警告。独立 updater 在无已安装运行时路径下启动测试通过（无参数按预期返回 2）。

原目录复跑的 19 项隔离集成检查通过：完整约 110 MiB 签名自包含 ZIP 从实际 WPF 0.1.0 客户端升级到 0.2.0、父进程存活时不替换、旧客户端正常退出码 0、更新后重开 WPF 窗口、新客户端正常退出码 0、旧 DLL 备份哈希、用户文件保留、真实文件锁引起回滚，以及签名元数据篡改拒绝。验收专用 updater 与生产进程逻辑/事务源码完全一致，协议源码仅替换临时公钥，使用 .NET 10 运行时执行；测试私钥只在内存中生成，没有读取生产密钥。正式自包含 updater 另做独立启动验证。

退出误判已定位并修复：原验收程序依赖 `Process.MainWindowHandle` 的缓存，曾将 WM_CLOSE 发给旧句柄，真实登录窗口仍在运行。诊断日志证明客户端正常收到关闭后会完整走过 `Shutdown`、服务释放与互斥锁释放。验收改为枚举并核对 PID、可见性和标题，向实际窗口发送 WM_CLOSE，并预先保留进程句柄以可靠取得退出码。曾实测旧缓存句柄为 856282、真实窗口为 24185564；请求真实窗口正常关闭后进程退出码为 0。

基础集成报告中 `parentExitSimulated=false`、`forcedTerminations=0`、`sourceDirectory="D:\\新启动器"`；验收源码不再包含 `Process.Kill`，正常路径和错误提示关闭均无强杀兜底。实际 updater 仍等待父进程自然退出，超时则报错，不强杀。该次基础报告当时未覆盖 CDN、公钥选用及设置页点击，后续验收结论见下段。

**后续补充已完成**：70 项核心测试、真实设置页的 12 项隔离 UI 观察通过，包括取消下载、拒绝坏签名/哈希、取消安装、确认安装后正常退出及重启。生产公钥选用已确认；真实 CDN 的 HTTPS/哈希/Range/缓存重验证通过，但当前生产清单仍是 v1 + 回环 URL + 39 字节测试 `.bin`，生产新版更新链路未就绪。没有上线。内部测试注入只供独立验收程序集使用，生产构造流程不允许变更公钥或域名。旧清单现在显示明确中文提示，详见上述生产验收报告。

第一次完整包验收曾耗尽 C 盘空间，只影响隔离副本；已清理用户明确授权的该次验收目录，并补充安装前空间检查。复跑时空间预检安全拒绝过一次不足的更新，未开始覆盖程序。清理本次下载的临时运行时缓存后，使用已有完整签名包和本机 .NET 10，正常退出、替换、重启、回滚均通过。

**已合回 `D:\新启动器`，改动尚未提交。** 当前会话已将原目录列为可写工作区；使用本机已有 .NET 10 SDK 完成 Debug 构建、自包含发布和无强杀完整验收。只有已知 NU1510 警告。两个真实测试实例目录仍在，安装记录哈希与合回前一致：`4DB4158E36BDC2150C60BA26250F07D0359D44C4F7B6464FAD2F84A226F4BE32`，revision 仍为 `test` 4、`csu-ysu-union` 1。没有上线。

本轮验收报告：`outputs/auto-update-acceptance-20261030-180010/report.json`（目录时间为 UTC）。自包含构建：`dist/CMUmcl-win-x64-selfcontained-0.1.0-20261031.zip`，SHA-256：`324FD0F70B9C822098F5E937FA6BC187F26798616A133DD9B4F047A623B10853`，同目录附 `.sha256`。保留所有旧发布包。此包未签署为生产更新，不应直接上传到更新通道。

复跑核心测试：`dotnet run --project tests/LauncherUpdate.Tests`。完整集成测试可设置 `CMUMCL_DOTNET10` 为可访问的 .NET 10 SDK 的 `dotnet.exe` 绝对路径，再运行 `dotnet run --project tests/LauncherUpdate.Tests -- --acceptance`；需当前工作区额外有至少 2 GiB 空间。测试输出均位于当前工作区，源码版本保持 0.1.0；0.2.0 只作为隔离更新目标。

验收必须使用单独的干净程序目录和测试数据根目录；不要删除 `test`（revision 4）或 `csu-ysu-union`（revision 1），不要用真实实例做失败注入。

## 备份与恢复

- 安装备份：`<程序目录>/.update-backups/<UTC时间>-<随机ID>/`。
- `recovery.json` 记录本次涉及的每个文件是否原先存在；`status.txt` 为 `applying`、`complete` 或 `rolled-back`。
- 旧程序文件保存在备份目录的 `files/` 下，与恢复记录分开。
- 失败日志：`%TEMP%/CMUmcl-updates/<随机ID>/result.txt`，更新程序同时显示错误与备份路径。
- 如果机器断电、磁盘故障或回滚也被文件锁阻止，需关闭全部客户端后按恢复记录从备份复制旧文件；`Existed=false` 的文件仅在确认是该次更新创建时删除。不自动恢复不可信/不完整的备份。
- 暂存目录和备份目前保留供排查，不自动清理，需在确认验收完成后手动清理对应更新目录。
- 当前完整 ZIP 不提供二进制差分，安装目录需要普通用户可写；不自动提权。安装前下载/校验和备份失败不会覆盖旧客户端。
