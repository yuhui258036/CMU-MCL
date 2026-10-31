# CMUmcl 项目交接文档

> **2026-10-31 真实生产更新验收通过**：单次完整 CDN 下载 219 秒、双签和哈希通过；真实 0.1.0 自包含客户端从生产通道升级至 0.2.0 并自动重开，旧/新进程退出码均为 0，无强杀。源站本机读取快，公网速度波动且更新 ZIP 为 no-cache/MISS；缓存优化尚未实施。两个真实测试实例及安装记录保持不变，未读取凭据或私钥。详见 `docs/CLIENT-UPDATE-LIVE-ACCEPTANCE-2026-10-31.md`。下方较早的未完成记录为历史状态。

> **2026-10-31 已发布 0.2.0**：更新通道已切换为真实签名 v2，服务器双签验证与实际 CDN 完整包验签通过（独立下载使用断点续传）；原 .NET 单次完整下载仍待完成，下载速度较慢。旧清单已备份，整合包和两个真实测试实例保持不变。详见 `docs/CLIENT-UPDATE-RELEASE-2026-10-31.md`。以下未上线/未就绪结论为本次发布前的历史验收记录。

> **2026-10-31 最新验收**：生产已确认沿用当前更新公钥，公开 CDN 样本验签通过；真实设置页 12 项隔离流程及 70 项核心测试通过。CDN 基础 HTTPS/Range/ETag 正常，但线上仍为旧 v1、回环下载 URL 和 39 字节测试文件，**生产更新通道尚未就绪，未上线**。后续应由持钥者离线提供真实 v2 签名产物，再做预发布下载验收；不要读取私钥。详见 `docs/PRODUCTION-UPDATE-ACCEPTANCE-2026-10-31.md`。

> **2026-10-31 更新**：客户端自动更新补丁已合回原目录（尚未提交/上线）。.NET 10 Debug、自包含发布、68 项核心测试及 19 项无强杀集成检查通过，旧/新客户端正常退出码均为 0。当前说明见 `docs/CLIENT-AUTO-UPDATE.md`；下方 2026-10-30 内容为历史基线。下一步为确认生产更新公钥、CDN/设置页端到端验收及发布自动化。
>
> 新包：`dist/CMUmcl-win-x64-selfcontained-0.1.0-20261031.zip`；合回备份：`backups/client-update-merge-20261031-015905/`。两个真实测试实例保持原 revision，凭据和密钥未读取；被忽略的服务端/资源站/测试工具/发布目录全部保留。

> 交接时间：2026-10-30  
> 当前分支：`main`  
> 交接前最新提交：`9f52288 docs: record EdgeOne CDN endpoint`  
> 当前状态：客户端和服务端均可运行，CDN 已接入并验证，下一步是客户端自动更新与发布自动化。

---

## 1. 项目概况

CMUmcl 是面向 CSU-MC 社区的 Windows Minecraft 启动器：

- 客户端：WPF `net10.0-windows`
- 启动核心：CmlLib.Core
- 资源站：ASP.NET Core `net9.0` 管理后台 + 静态资源
- 登录：CSU-MC Yggdrasil
- 资源更新：签名 manifest + `.mrpack` 文件索引 + 增量更新
- 当前公网资源入口：`https://csumcl.mcapcs.cn`
- CDN：腾讯 EdgeOne
- 私钥、密钥和服务器凭证不进入 Git

---

## 2. 当前可运行状态

### 客户端

- 本地 Debug 构建成功：0 errors
- 当前运行进程：`dotnet ... CMUmcl.dll`，窗口标题 `CMUmcl`
- 已从 CDN 成功拉取 manifest
- 当前实例：
  - `test`：Minecraft 26.3 / Fabric 0.20.1，revision 4
  - `csu-ysu-union`：Minecraft 26.3 / Fabric 0.19.5，revision 1
- `test` 已实际启动到 `Minecraft* 26.3` 窗口
- 旧 `1.20.1`、`fabric-loader-0.15.11-1.20.1` 和旧测试缓存已清理

### 服务端

- 服务器：`103.24.216.15`
- 公网 HTTPS：`https://csumcl.mcapcs.cn` → EdgeOne → `103.24.216.15:443`
- 管理后台：`127.0.0.1:9443`，只能通过 SSH 隧道访问
- 服务端目录：`/opt/pcl-cmu`
- 服务端程序最近已更新
- 当前 manifest：
  - `test` revision 4
  - `csu-ysu-union` revision 1
- CDN：
  - 证书 `CN=csumcl.mcapcs.cn` 验证 OK
  - manifest 签名验证 OK
  - `.mrpack` / `.files.json` Range 请求正常
  - packs 缓存 `EO-Cache-Status: HIT`
- HTTP 入口当前仍返回 `521`；客户端只使用 HTTPS，不影响当前运行。

---

## 3. 已完成功能

### 账号与启动

- CSU-MC Yggdrasil 登录
- DPAPI 保存会话
- 登录态刷新
- Java 版本选择
- 缺失 Java 自动下载 Mojang runtime
- 下载重试
- 启动后等待真实游戏窗口才显示“游戏已启动”
- 游戏进程提前退出会报错
- 120 秒未检测到窗口会显示“等待窗口/请查看日志”

### 资源站整合包

- 签名资源清单
- SHA 校验
- 服务端整合包下载/安装
- 启动前检查 revision
- 自动更新整合包
- 增量更新：只下载变化文件
- 删除新版本移除的受管文件
- 保留用户自建文件
- overrides 变化时回退完整包
- 下载/安装失败不会启动旧版本
- 设置页可关闭“启动前自动更新整合包”

### 实例

- 本地自定义实例创建
- 创建时下载 Minecraft + 加载器文件
- 成功后写入 `.cmumcl-provisioned.json`，后续启动跳过重复安装
- Fabric / Forge / NeoForge / Quilt 支持
- 默认名称自动生成：`游戏版本 + 加载器 + 加载器版本`
- 用户手动改名后不会被自动覆盖
- 名称/目录冲突自动加后缀
- 文件夹导入
- 拖拽 `.mrpack` 导入
- `.mrpack` 文件选择导入
- 非法文件拒绝
- 导入失败清理副本，不修改源文件
- 实例目录迁移
- 迁移冲突逐项手动改名
- 设置页实例存放位置
- Fabric API 自动安装

### UI

- 三页布局：服务器整合包 / 实例 / 设置
- 现代暗色卡片 UI
- 创建实例改为主窗口内分步流程，不弹新窗口
- 游戏版本列表支持正式版 / 快照 / 愚人节 / 远古版本 / 搜索
- 加载器 chips
- 版本列表
- 下载进度
- CSU-MC 图标和侧栏 Logo

### 资源站后台

- 后台管理员密码使用 PBKDF2 哈希
- manifest ECDSA 签名
- 上传 `.mrpack` 后生成 `.files.json`
- 上传进度 UI（百分比、大小、速度、校验阶段）
- 上传时从 `modrinth.index.json` 自动同步游戏/加载器元数据
- 启动时回填已有实例的元数据
- 上传失败处理、临时文件清理

---

## 4. 关键提交

```text
9f52288 docs: record EdgeOne CDN endpoint
2d6915b feat(cdn): use EdgeOne domain for manifest and pack allowlist
8fe0365 fix(instance): include loader version in default name
1f679e7 feat(instance): import mrpack via picker and drag-drop
5bf8f8a feat(instance): add folder import and equal landing cards
86846b9 feat(ui): use launcher logo in sidebar brand
7bc6f70 feat(ui): use CSU-MC launcher icon
a02a1fd docs: record metadata backfill and successful test launch
fe1af81 docs: record incremental update test result
bf93e50 feat(pack): incremental file-index updates
b5a2ec1 feat(launch): auto-update server packs before launch
edc27f4 docs: add P1363 manifest signature verification helper
```

---

## 5. 代码地图

### 客户端

```text
CMUmcl/
  MainWindow.xaml/.cs
    - 三页布局
    - 实例启动入口
    - 启动前自动更新
    - 拖拽 .mrpack
    - 设置页
  CreateInstanceView.xaml/.cs
    - 新建实例流程
    - 文件夹导入
    - .mrpack 导入
    - 加载器/版本选择
    - 下载进度
  Content/
    ResourceManifestClient.cs
      - CDN BaseUrl
      - manifest 签名验证
      - pack host allowlist
    PackInstaller.cs
      - 完整包安装
      - 增量更新
      - 本地 pack state
    PackFileIndex.cs
      - files.json DTO/验证
    PackIncrementalPlanner.cs
      - 文件 diff 决策
    PackInstallState.cs
      - `.cmumcl-pack-manifest.json`
    OverridesHash.cs
      - overrides 哈希
    PackAutoUpdate.cs
      - 启动前 revision 决策
    InstanceImportService.cs
      - 文件夹导入
    MrpackImportService.cs
      - .mrpack 导入
    LocalInstanceService.cs
    InstanceMetadata.cs
    InstalledStateStore.cs
    LauncherPaths.cs
    VersionCatalogService.cs
    HttpProxyResolver.cs
  Launch/
    LaunchService.cs
    GameWindowWaiter.cs
    ProvisioningMarker.cs
    Forge/
    NeoForge/
```

### 服务端（重要：不在 Git 中）

`admin-backend/` 整个目录被 `.gitignore` 忽略。服务端代码目前只在本地工作区，**没有提交到 GitHub**。

关键文件：

```text
admin-backend/
  AdminRouter.cs
    - 上传 API / 上传进度 HTML
  FileIndex.cs
    - .mrpack 文件索引
    - overrides 哈希
    - Minecraft metadata 解析
  InstanceStore.cs
    - revision / pack / manifest
    - 启动回填 metadata
  Models.cs
    - manifest / pack / minecraft DTO
  ManifestSigner.cs
  Program.cs
  appsettings.json
  deploy/
  keys/
```

迁移到下一个智能体时，必须保留这个本地目录，否则服务端的增量索引、元数据同步和上传进度 UI 会丢失。

同样被忽略的还有：

```text
resource-site/
scripts/
client-harness/
dist/
docs/ops/.Rhistory
```

---

## 6. 服务器和 CDN

### 公网

```text
https://csumcl.mcapcs.cn/manifest.json
https://csumcl.mcapcs.cn/manifest.json.sig
https://csumcl.mcapcs.cn/packs/<id>/<rev>.mrpack
https://csumcl.mcapcs.cn/packs/<id>/<rev>.files.json
https://csumcl.mcapcs.cn/update/windows-x86_64/latest.json
```

### 源站

```text
服务器：103.24.216.15
应用：/opt/pcl-cmu/app
数据：/opt/pcl-cmu/data
静态：/opt/pcl-cmu/wwwroot
私钥：/opt/pcl-cmu/keys/manifest-signing-key.pem
管理员哈希：/opt/pcl-cmu/admin.env
管理端口：127.0.0.1:9443
```

### 访问方式

```powershell
ssh -N -L 19443:127.0.0.1:9443 root@103.24.216.15
```

浏览器打开：

```text
http://127.0.0.1:19443/admin
```

SSH 当前是密码认证；本机尚未配置密钥登录。不要读取或提交 `admin-credentials.txt`。

### CDN 规则

```text
/packs/**          immutable，长期缓存
*.mrpack           immutable
*.files.json       immutable
/manifest.json     no-cache / 短 TTL
/manifest.json.sig no-cache / 短 TTL
/update/latest.json no-cache
/admin/**          不暴露
```

---

## 7. 数据和路径

```text
%LOCALAPPDATA%\CMUmcl
  = junction
  D:\新启动器\CMUmclData

instances/          实例
minecraft/          共享 versions/libraries/assets
cache/packs/        .mrpack 缓存
java/               Java runtime
auth.dat            DPAPI 登录态
settings.json       客户端设置
installed.json      实例记录
```

当前实例记录：

```json
{
  "Instances": {
    "csu-ysu-union": { "revision": 1, "installPath": "csu-ysu-union" },
    "test": { "revision": 4, "installPath": "test" }
  }
}
```

测试实例目前保留，不要删除；用户还在验收。

---

## 8. 构建和测试环境

```powershell
$env:TEMP="D:\新启动器\.tmp"
$env:TMP="D:\新启动器\.tmp"
$env:DOTNET_CLI_HOME="D:\新启动器\.dotnet-home"
$env:NUGET_PACKAGES="D:\新启动器\.nuget\packages"
$dotnet="C:\Users\PC\AppData\Local\Microsoft\dotnet\dotnet.exe"

& $dotnet build "D:\新启动器\CMUmcl\CMUmcl.csproj" -c Debug
```

当前构建状态：

- 客户端：0 errors
- 服务端：0 errors
- 既有警告：NU1510（ProtectedData 引用）为已知无关警告

已知测试：

- SettingsMigrationTest：56/56
- LocalInstanceNaming：16/16
- ProvisioningMarker：22/22
- PackAutoUpdate：8/8
- IncrementalPackClient：8/8 组
- AdminFileIndex：43/43
- AdminMetadataSync：69/69
- InstanceImport：47 项断言
- MrpackImport：18/18
- VersionCatalog：921 个版本
- 真实启动：26.3 + Fabric 0.20.1 已进入 Minecraft 窗口

部分测试位于 `.tmp/`，不是 CI 测试工程。

---

## 9. 已知风险和未完成事项

### 高优先级

1. **客户端自动更新尚未实现**
   - 设计路径已讨论
   - 建议使用 CDN `/update/windows-x86_64/latest.json`
   - 需要签名 zip、独立 updater、备份/替换/重启
   - 当前 `wwwroot/update/...test.bin` 只有 39 字节，不是真实安装包

2. **发布自动化尚未实现**
   - 仍需 GitHub Actions 或本地脚本构建自包含 zip
   - 需要生成签名 `latest.json`
   - 需要真正发布到 CDN

3. **自包含客户端包是旧的**
   - `dist/CMUmcl-win-x64-all-loaders-selfcontained.zip` 早于设置/UI/导入/CDN 改动
   - 当前验证使用的是 Debug `dotnet` 运行，不是发布包

4. **旧客户端兼容性**
   - 当前 CDN 切换后，旧客户端仍只认 IP pack URL
   - 新客户端允许 CDN 域名和旧 IP
   - 第一次必须手动分发新客户端

5. **服务端源码不在 Git**
   - `admin-backend/` 被 `.gitignore` 忽略
   - 交接时必须复制整个目录
   - 建议后续拆出独立服务端仓库或 force-add 源码（不含 keys）

### 中优先级

- CDN 的 HTTP 入口仍返回 521；客户端只用 HTTPS
- EdgeOne 缓存规则建议确认 `manifest.json` no-cache、packs immutable
- 迁移到对象存储（R2/OSS/COS）可进一步降低源站出口压力
- 实例删除/修复入口还没有
- 旧 Fabric/游戏文件清理是手动的，没有自动 GC
- OptiFine / QSL / QFAPI 不支持
- 普通整合包 ZIP（非 `.mrpack`）不支持
- `AutoUpdatePacks` 已实现；客户端自身自动更新尚未实现
- 服务器上传 UI 有进度，但没有后台持久任务队列；大文件上传期间不要关闭页面

### 安全

- `admin-credentials.txt` 位于工作区，曾出现被文件索引器访问的风险；应移出并轮换
- `admin-backend/keys/manifest-signing-key.pem`、`resource-site/keys/` 私钥不能提交
- `resource-site` 的更新私钥和 `client-harness` 公钥是两套不同用途，不要混用
- `admin.env` 不应公开
- SSH 建议改为公钥认证，避免在交互窗口中输入密码

---

## 10. 下一步建议

建议顺序：

1. **完成客户端自动更新**
   - 更新 manifest 检查
   - 版本比较
   - SHA-256 + ECDSA 验签
   - 下载 staging
   - 独立 updater 备份/替换/重启
   - 设置页手动检查 + 启动异步检查

2. **发布自动化**
   - 自包含 win-x64 publish
   - zip 打包
   - 签名并生成 `latest.json`
   - 上传 CDN
   - 版本号自动递增

3. **重新生成自包含包**
   - 用当前代码构建
   - 保留新图标、CDN 配置、导入功能和增量更新
   - 在干净目录做一次启动验证

4. **CDN 收尾**
   - 禁用/重定向 HTTP 521 入口
   - 检查缓存规则
   - 评估对象存储

5. **清理安全项**
   - 移除 `admin-credentials.txt`
   - 轮换管理员密码/相关密钥
   - 配置 SSH 公钥
   - 把服务端源码纳入可追踪仓库（不含私钥）

---

## 11. 交接检查清单

- [ ] 保留 `D:\新启动器` 整个工作区，尤其 `admin-backend/`
- [ ] 不要提交 `admin-backend/keys/`、`resource-site/keys/`、`admin.env`
- [ ] 确认 `git status` 干净
- [ ] 运行客户端 Debug 构建
- [ ] 确认 CDN `manifest.json` 可访问且签名通过
- [ ] 确认 SSH 隧道能打开 `127.0.0.1:9443`
- [ ] 确认 `test` 和 `csu-ysu-union` 实例仍在
- [ ] 不要删除 `%LOCALAPPDATA%\CMUmcl` 的登录态、设置、Java、缓存
- [ ] 从本文件所在的 `docs/HANDOFF.md` 和 `docs/PROGRESS.md` 开始
