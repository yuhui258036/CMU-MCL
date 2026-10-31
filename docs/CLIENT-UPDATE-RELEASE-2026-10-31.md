# 0.2.0 服务器发布结果（2026-10-31）

> **2026-10-31 真实生产更新验收通过**：单次完整 CDN 下载 219 秒、双签和哈希通过；真实 0.1.0 自包含客户端从生产通道升级至 0.2.0 并自动重开，旧/新进程退出码均为 0，无强杀。源站本机读取快，公网速度波动且更新 ZIP 为 no-cache/MISS；缓存优化尚未实施。两个真实测试实例及安装记录保持不变，未读取凭据或私钥。详见 `docs/CLIENT-UPDATE-LIVE-ACCEPTANCE-2026-10-31.md`。下方较早的未完成记录为历史状态。

**已发布，CDN 完整产物验签通过（使用断点续传）；单次普通 GET 校验仍在后台运行，尚未完成。**

## 发布与签名

- .NET SDK 10.0.403，Windows x64 自包含，主程序集版本 0.2.0.0，含独立自包含 updater。
- 更新入口 https://csumcl.mcapcs.cn/update/windows-x86_64/latest.json 已切换为签名 v2。
- ZIP https://csumcl.mcapcs.cn/update/windows-x86_64/CMUmcl-0.2.0-49cb77915f6ff8cb.zip
- 大小 115553249 字节；SHA-256 49cb77915f6ff8cb9a39f904e3e54c10f10e85d69e91ddc935a51684787408b4。
- 生产公钥继续使用既有指纹 20C98D68C2E761E57EC40C3AD0A173EDE4528279464A5E643E456C5DE3C47583。
- 用户在可见 PowerShell 中运行本机签名工具；聊天工具未读取私钥，私钥未上传。服务器只使用公钥验证 ZIP 和元数据双签，回执 status=PUBLISHED。
- 旧清单备份：/opt/pcl-cmu/backups/client-update-0.2.0-20261031/latest.json。
- 服务未重启；整合包 manifest.json 及其签名发布前后哈希完全一致。

## 真实 CDN 检查

- 清单 HTTP 200 / Cache-Control: no-cache；固定公钥验签通过，版本、URL、大小、哈希与发布目标一致。
- ZIP HEAD 返回 200 和正确大小；16 字节及 1 MiB Range 返回 206。
- 独立普通完整 GET 在 180 秒诊断时限内收到 99061760 字节，随后以 Range 断点续传补齐 16491489 字节，得到完整 115553249 字节 ZIP。
- 完整 CDN ZIP 使用实际 UpdateProtocol.ReadRelease / VerifyArchive 与固定公钥验证：元数据签名、ZIP 签名、大小、SHA-256 全部通过。
- 全程默认 TLS 验证，未禁用证书检查，未跟随重定向。
- 原 .NET 单次普通 GET 仍在后台运行；截至本报告已确认写入超过 52 MiB，不能把该单次下载标记为已通过。下载较慢，后续可优化大文件缓存/回源，但没有确定具体根因。本轮未重做生产安装与重启，相关设置页隔离验收见此前报告。
- 注意：文件未关闭前目录大小曾显示为 0，并不代表没有下载数据；已用进程写入计数和 curl 最终统计纠正判断。

## 保留与使用

本地 installed.json SHA-256 仍为 4DB4158E36BDC2150C60BA26250F07D0359D44C4F7B6464FAD2F84A226F4BE32；test revision 4、csu-ysu-union revision 1 均保留。没有读取管理员凭据、服务端密钥目录或 admin.env，没有提交或推送 Git。

不具备自动更新功能的旧客户端，首次仍需手动下载并安装本次完整版本。已有 0.2.0 显示最新属于预期行为；本轮没有再发布 0.2.1。

原始材料：C:\Users\PC\Documents\Codex\release-20261031-020。remote-publish.txt 为服务器成功回执；independent-cdn-verification.json 为完整 CDN 产物校验记录；independent-verify/ 保存线上清单和实际 CDN 完整包的硬链接。若随后出现 cdn-verification.json 且 status=PASS，才表示原 .NET 单次普通 GET 也完成了全部检查。