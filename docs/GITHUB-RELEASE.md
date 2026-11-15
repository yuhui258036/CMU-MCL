# GitHub 自动发布

工作流：`.github/workflows/net48-release.yml`

## 触发

- 推送版本标签 `v0.3.48.9` 之类的标签。
- 手动 `workflow_dispatch`，填写版本号，可选择是否发布服务端。
- 创建 `release/net48/<四段版本号>` 分支也可请求发布：`net48-publish.yml` 校验分支版本与项目版本一致后，调用同一个 `net48-release.yml`，启用服务端签名更新发布。该入口只调用 Actions，不接触私有发布凭据。

## 行为

- **GitHub Release**：只上传无 Key 的公开客户端 ZIP 和 `release-info.json`。
- **服务端 net48 feed**：仅当仓库 Secrets 配置完整时执行：
  - 使用 `CURSEFORGE_PROJECT_KEY` 构建带 Key 的客户端；
  - 使用 `UPDATE_SIGNING_KEY_PEM` 生成签名 feed；
  - 通过 SSH 上传带 Key ZIP 和 `latest.json` 到
    `/opt/pcl-cmu/wwwroot/update/windows-x86_64/net48/`；
  - 带 Key ZIP 不会进入 GitHub Release。
- 服务端部署脚本会拒绝降级、校验 ZIP/feed 哈希、保留旧 feed 备份，并支持重复执行同一 feed。

## Secrets

| Secret | 说明 |
| --- | --- |
| `CURSEFORGE_PROJECT_KEY` | CurseForge 项目 Key，仅注入带 Key 构建；不能打印或写入源码 |
| `UPDATE_SIGNING_KEY_PEM` | 更新签名私钥（ECDSA P-256 PKCS#8 PEM），由 `sign-update-feed.ps1` 校验指纹后使用 |
| `DEPLOY_SSH_KEY` | 允许写入 feed 目录的 SSH 私钥 |
| `DEPLOY_KNOWN_HOSTS` | 固定 SSH 主机密钥行，避免首次连接信任 |
| `DEPLOY_HOST` | 可选，默认 `103.24.216.15` |
| `DEPLOY_USER` | 可选，默认 `root` |

`CURSEFORGE_PROJECT_KEY` 或部署凭据缺失时，工作流只发布 GitHub 无 Key Release，不会修改服务器。

## 本地脚本

- `scripts/package-net48-release.ps1`：按已构建输出生成 ZIP，并检查是否携带 Key 资源。
- `scripts/sign-update-feed.ps1`：生成与客户端 `UpdateProtocol` 一致的签名信封；会拒绝不匹配客户端固定公钥的私钥。
- `scripts/deploy-net48-feed.sh`：在服务器上原子替换 feed、校验哈希、拒绝降级。

不要把带 Key 的 ZIP、私钥、`CURSEFORGE_PROJECT_KEY` 或 SSH 私钥提交到仓库或上传到 GitHub Release。

## Installer

- GitHub Release: no-key ZIP plus `CMUmcl-<version>-setup.exe`. The wizard allows a custom install directory, creates Start Menu and optional desktop shortcuts, and keeps `sandbox-data` on uninstall.
- Server downloads: keyed `CMUmcl-<version>-setup-full.exe` under `https://csumcl.mcapcs.cn/downloads/`.
- Build scripts: `scripts/build-net48-installer.ps1` and `installer/CMUmcl.iss`.
- Installers are unsigned; Windows SmartScreen may warn until a trusted signing certificate is configured.
