# CMUmcl — 步骤一：主界面 + 签名整合包目录 + 下载/校验/安装

本步骤在登录门之上加入了第一个主界面步骤：
一个 PCL 风格的简洁外壳、带签名校验的服务器整合包目录，以及
整合包下载 / 校验 / 安装（**尚未接入游戏启动**）。

## 本步骤已实现

- **主界面外壳**（替换原占位 `MainWindow`）
  - 约 1100×700，深色中性主题，简洁布局。
  - 左侧边栏（约 220px）：`CMUmcl` 品牌、导航项 `服务器整合包`（当前激活）、`设置`（占位，禁用，提示“后续版本”）、底部账号卡片（角色名 + `退出登录`）。
  - 顶部标题 `服务器整合包` + `刷新` 按钮 + 状态文本。
  - 内容区为可滚动的整合包卡片：名称、描述、版本号、Minecraft 版本 / 加载器、服务器地址、整合包大小；`下载并安装` / `更新` / `已安装` 按钮；每张卡独立的进度与状态。
  - 空 / 加载中 / 出错三种状态，出错可重试。
  - 无启动按钮，底部提示 `启动功能将在下一步接入`。
- **登录门保持不变**：`MainWindow` 仅在登录成功后显示，退出登录返回登录窗口。

## 清单客户端（新增 `Content/` 命名空间）

- `ResourceManifestClient`
  - 以原始字节下载 `manifest.json` 与 `manifest.json.sig`（`{algorithm,keyId,signature}`）。
  - 在解析前用**内嵌公钥**对清单字节做 ECDSA P-256 / SHA-256 验签，失败即拒绝（fail closed）。
  - 解析 `formatVersion` 与 `instances[]`（id/name/description/icon_url/sort/revision/required/minecraft{game_version,loader,loader_version}/pack{kind,url,sha1,sha256,sha512,size}/server{address,port}）。
  - 下载地址白名单：绝对 HTTPS 且主机为 `103.24.216.15`，否则拒绝。
- `PackInstaller`
  - 下载 `.mrpack` 到 `%LOCALAPPDATA%\CMUmcl\cache\packs\{id}-{revision}.mrpack`，按 SHA-512 → SHA-256 → SHA-1 优先级校验 + 大小校验。
  - 读取压缩包内 `modrinth.index.json`。
  - 创建实例目录 `%LOCALAPPDATA%\CMUmcl\instances\{id}`。
  - 逐个下载 `files[]` 条目到相对路径，校验 SHA-512 / SHA-1；已存在且有效则跳过；防止路径穿越（解析到实例目录内，拒绝 `..` / 根路径 / 非法字符）。
  - 解压 `overrides/` 再解压 `client-overrides/` 到实例目录（防穿越、跳过目录、覆盖已存在文件）。
  - 写入 `instance.json`（id/name/revision/game/loader/server/pack 哈希/installedAt）。
  - 更新 `%LOCALAPPDATA%\CMUmcl\installed.json`（记录 `{id, revision}`）。
  - 通过 `IProgress<PackInstallProgress>` 报告进度（阶段、当前/总文件数、已知字节数、消息）。
  - 无 `files` 的整合包（如当前 test 包）也能正常创建实例与元数据。
  - 安装不依赖 Minecraft / Java / 游戏启动。

## 目录与路径

| 用途 | 路径 |
|------|------|
| 登录令牌 | `%LOCALAPPDATA%\CMUmcl\auth.dat`（DPAPI，已有） |
| 已安装版本状态 | `%LOCALAPPDATA%\CMUmcl\installed.json` |
| 整合包缓存 | `%LOCALAPPDATA%\CMUmcl\cache\packs\{id}\{id}-{revision}.mrpack` |
| 实例目录 | `%LOCALAPPDATA%\CMUmcl\instances\{id}` |

## 尚未实现（下一步）

- 游戏启动（计划用 CmlLib.Core）：加载实例、解析版本 / 依赖、启动 Java。
- `设置` 页面。
- 社区 / 好友 / 公告功能（明确不在范围）。

## 构建与运行

```powershell
$env:DOTNET_ROOT = "C:\Users\PC\AppData\Local\Microsoft\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
& "$env:DOTNET_ROOT\dotnet.exe" build "CMUmcl\CMUmcl.csproj" -c Debug
& "CMUmcl\bin\Debug\net10.0-windows\CMUmcl.exe"
```

## 安全说明

- 清单验签公钥内嵌于程序（SPKI PEM），不从资源站同一信道获取；验签失败拒绝加载。
- 整合包 / 文件下载均做哈希 + 大小校验，任何不匹配中止安装。
- 下载地址（整合包）白名单限制为 `103.24.216.15`；安装文件路径做穿越防护。
- 登录令牌仍由 DPAPI 加密，密码从不落盘。
