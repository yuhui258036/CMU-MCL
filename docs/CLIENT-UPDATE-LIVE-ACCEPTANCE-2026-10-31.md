# 生产单次下载、性能排查与真实客户端更新验收

日期：2026-10-31（北京时间）。**验收通过：单次完整下载通过，真实自包含客户端通过生产 CDN 从 0.1.0 升级到 0.2.0，旧、新客户端均正常退出，强杀次数为 0。**

## 单次完整下载

先前 C 盘任务未留下成功记录，仅有约 52 MiB 部分文件；本轮发现 C 盘无剩余空间。不能仅凭旧任务缺少结果判定 CDN 故障，也未拿断点续传替代单次下载。

本轮把程序、数据、暂存和下载均放到 D 盘，在相同生产域名及固定公钥下重新执行不间断、不续传的完整 GET：

- 14:40:19 至 14:43:58，耗时 218.973 秒。
- 115553249 字节，约 0.50 MiB/s。
- 元数据签名、ZIP 签名、SHA-256、大小全部通过；Range 206 与 latest.json no-cache 通过。
- SHA-256：49cb77915f6ff8cb9a39f904e3e54c10f10e85d69e91ddc935a51684787408b4。
- 默认 TLS 验证，不跟随重定向。

记录：outputs/live-update-acceptance-20261031/network/cdn-verification.json、single-download-timing.json。

## 下载慢的排查

| 测试 | 结果 |
| --- | --- |
| 源站本机 HTTPS 完整读取 110 MiB ZIP | 0.916 秒，约 120 MiB/s；使用服务器公开证书验证 TLS，没有跳过验证 |
| 服务器访问 CDN 的 1 MiB Range | 0.753 秒，约 1.33 MiB/s |
| 本机 curl 普通 GET 40 秒样本 | 约 0.62 MiB/s，首响应约 4.76 秒 |
| 本机 1 MiB 已有整合包 | 2.31 秒，EO-Cache-Status: HIT，immutable |
| 本机 1 MiB 更新 ZIP | 4.50 秒，EO-Cache-Status: MISS，Cache-Control: no-cache |
| .NET 默认/直连样本 | 出现超时和明显波动；另一轮分别 30.27 秒和 5.64 秒，不能据单次对比认定代理为根因 |

源站本机处理与磁盘吞吐充足，较慢的环节在公网/CDN 传输路径；更新 ZIP 当前未命中缓存。现有证据不足以进一步把所有慢速归因于某个代理、回源链路或具体 EdgeOne 配置。并发诊断和网络波动也会影响样本速度。

可明确改进的是缓存策略：为含版本及哈希、发布后不覆盖的 /update/windows-x86_64/*.zip 配置长期节点缓存和合适的响应头，latest.json 继续 no-cache；可结合分片回源优化大文件传输。需在改动后用重复请求的 HIT、完整下载速度和签名再验证效果。本轮未更改源站程序、CDN 配置或系统代理，不声称已经修复速度。

官方配置依据：[EdgeOne 节点缓存 TTL](https://edgeone.ai/document/46175)、[内容缓存规则](https://edgeone.ai/document/54213)、[分片回源](https://edgeone.ai/document/46966)。

C 盘空间问题已规避：清理了本轮未启动的不完整副本，把三个既有公开诊断/上传文件移动并保留在 network/previous-artifacts；C 盘恢复约 224 MiB，D 盘仍有约 46 GiB。没有清理真实实例或用户资料。

## 实际客户端验收

使用原始 0.1.0 生产自包含构建的独立副本，目录 outputs/live-update-acceptance-20261031/app，数据根目录为同级 data，TEMP/TMP 指向同级 temp。

这次没有测试壳、合成身份、替换公钥、注入网络或测试 updater。用户亲自登录；聊天工具没有查看、复制或解密任何认证文件，也没有代填账号或密码。

1. 实际设置页识别生产 0.2.0 清单，使用固定生产公钥和生产 CDN 下载。
2. 实际 UI 显示下载进度和取消按钮；重复检查、安装按钮在下载时禁用。
3. 客户端暂存完整包 SHA-256 与上述生产包完全一致。包文件写入时间约为 14:42:26 至 14:49:45；这次实际 UI 下载约 7 分 20 秒。
4. 安装确认后，旧进程 PID 12584 于 14:50:10 正常退出，退出码 0；本轮自动化未点击安装确认。
5. 独立生产 updater 重新验签、备份和替换，result.txt 记录更新成功，备份状态 complete。
6. 新进程 PID 23184 于 14:50:13 自动重开，程序集为 0.2.0.0，与发布构建 DLL 哈希一致。
7. 数据根目录参数保留；设置页启动检查和再次手动检查都显示“当前版本 0.2.0 已是最新”，下载按钮禁用。
8. 用正常窗口关闭操作退出新客户端，14:52:55 退出码 0；没有强制终止任何客户端。
9. 旧 DLL 备份 SHA-256 与更新前一致；隔离数据标记及额外 user-note.txt 均保留。

安装前 DLL：BA7AA37005CB5B2A400205D2747934B314389EBC5275E9641ED1CEDA411F5A47。
安装后 DLL：E70920894797A4B334CD51AA203CA9923056489D0F653024E50BB9553CB03E2B。

备份：outputs/live-update-acceptance-20261031/app/.update-backups/20261031-065012-3980064ef9034481b0a9159de9f045ea。

## 保留与材料

真实 test revision 4、csu-ysu-union revision 1 的目录均保留；CMUmclData/installed.json 哈希仍为 4DB4158E36BDC2150C60BA26250F07D0359D44C4F7B6464FAD2F84A226F4BE32。线上整合包 manifest 及其签名哈希也与发布前一致。本轮没有读取凭据、私钥或 admin.env，没有重新签名、上传、部署或提交 Git。

汇总：outputs/live-update-acceptance-20261031/summary.json。另附 old-client-exit.json、new-client-exit.json，以及 network 下测速、源站只读检查和公开 CDN 校验记录。验收程序均已正常关闭；用户登录产生的隔离应用数据保留，不读取或清理。