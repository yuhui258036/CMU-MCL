# 资源站密钥轮换与服务器连接

适用对象：CMUmcl / PCL-CMU 资源站（`admin-backend`）。

## 0. 先确认位置

资源站实际运行在资源服务器：

```text
服务器：103.24.216.15
程序：/opt/pcl-cmu/app
数据：/opt/pcl-cmu/data
静态资源：/opt/pcl-cmu/wwwroot
签名私钥：/opt/pcl-cmu/keys/manifest-signing-key.pem
管理员密码哈希：/opt/pcl-cmu/admin.env
```

本机 `D:\新启动器\admin-backend` 只是源码、发布产物和本地密钥副本，不是线上服务。

需要轮换的是两类不同凭据：

1. **管理员登录密码**：只影响 `/admin` 登录。
2. **manifest 签名密钥**：影响资源站 `manifest.json.sig` 以及所有客户端的清单验签。

---

## 1. 从本机连接服务器

### 1.1 普通 SSH

PowerShell：

```powershell
ssh root@103.24.216.15
```

如果使用密钥：

```powershell
ssh -i "$env:USERPROFILE\.ssh\你的私钥" root@103.24.216.15
```

第一次连接会提示确认服务器指纹；确认后登录。

登录后检查服务：

```bash
systemctl status pcl-cmu --no-pager
ss -lntp | grep -E '(:443|:9443)'
```

正常情况下：

- `443`：公网 HTTPS 资源站
- `9443`：只绑定 `127.0.0.1` 的管理后台

不要在防火墙上开放 `9443`。

### 1.2 打开管理后台

管理后台只能在服务器本机访问。本机开一个 SSH 隧道：

```powershell
ssh -N -L 9443:127.0.0.1:9443 root@103.24.216.15
```

保持这个 PowerShell 窗口不要关闭，然后浏览器打开：

```text
http://127.0.0.1:9443/admin
```

如果本机 `9443` 已被占用，可以换成本机端口：

```powershell
ssh -N -L 19443:127.0.0.1:9443 root@103.24.216.15
```

然后打开 `http://127.0.0.1:19443/admin`。

### 1.3 检查公网资源站

```powershell
curl.exe -fsS https://103.24.216.15/manifest.json -o manifest.json
curl.exe -fsS https://103.24.216.15/manifest.json.sig -o manifest.json.sig
```

`443` 正常、`9443` 只监听回环地址，是当前预期的部署方式。

---

## 2. 轮换管理员密码

线上使用的是 PBKDF2 哈希，`admin-credentials.txt` 里的明文不是线上实际配置。

### 2.1 在服务器生成新哈希

登录服务器后：

```bash
read -rsp "New admin password: " P; echo
HASH=$(/opt/dotnet/dotnet /opt/pcl-cmu/app/PclCmu.Admin.dll --hash-password "$P")
unset P
printf '%s\n' "$HASH"
```

复制输出的完整哈希（以 `pbkdf2$100000$` 开头）。

### 2.2 更新 admin.env

```bash
nano /opt/pcl-cmu/admin.env
```

把这一行替换为新哈希：

```text
PCL_CMU_ADMIN_PASSWORD_HASH=pbkdf2$100000$...
```

保存后：

```bash
chmod 600 /opt/pcl-cmu/admin.env
systemctl restart pcl-cmu
systemctl status pcl-cmu --no-pager
```

重启后旧管理员会话全部失效，用新密码登录：

```text
http://127.0.0.1:9443/admin
```

### 2.3 清理旧凭据

确认新密码可以登录后：

- 删除或移出 `D:\新启动器\admin-credentials.txt`
- 不要在脚本、聊天记录、Git、桌面文档里保留明文
- 如果该文件里还有 SSH 密码或其他服务器密码，一并更换
- 建议使用密码管理器保存

---

## 3. 轮换 manifest 签名密钥

签名采用 **ECDSA P-256 / SHA-256**，签名值是 IEEE P1363（`r||s`）base64。

相关位置：

```text
服务端私钥：/opt/pcl-cmu/keys/manifest-signing-key.pem
客户端内嵌公钥：CMUmcl/Content/ResourceManifestClient.cs 的 PublicKeyPem
后端 keyId：admin-backend/ManifestSigner.cs 的 KeyId
本地公钥副本：admin-backend/keys/manifest-signing-public.pem
```

### 3.1 先生成新密钥（本机离线目录）

不要把私钥生成到仓库目录内。建议：

```powershell
$keyDir = "D:\secure\cmumcl-keys-2026-11"
New-Item -ItemType Directory -Path $keyDir -Force | Out-Null

$openssl = "C:\Program Files\Git\usr\bin\openssl.exe"
& $openssl ecparam -name prime256v1 -genkey -noout `
  -out "$keyDir\manifest-signing-key-2026-11.pem"
& $openssl ec -in "$keyDir\manifest-signing-key-2026-11.pem" `
  -pubout -out "$keyDir\manifest-signing-public-2026-11.pem"
```

确认：

```powershell
& $openssl pkey -in "$keyDir\manifest-signing-key-2026-11.pem" -pubout
```

不要执行会把私钥内容输出到终端的 `-text` 命令。

### 3.2 更新客户端公钥

编辑：

```text
CMUmcl/Content/ResourceManifestClient.cs
```

把 `PublicKeyPem` 替换为新的 `manifest-signing-public-2026-11.pem` 内容。

同时更新：

```text
admin-backend/ManifestSigner.cs
```

把：

```csharp
public const string KeyId = "cmu-manifest-2026-10";
```

改成新的标识，例如：

```csharp
public const string KeyId = "cmu-manifest-2026-11";
```

然后重新构建/发布客户端和后端。

**重要顺序：**

当前客户端只接受一个内嵌公钥。如果先换服务器私钥、再发客户端，旧客户端会立刻验签失败。

安全做法是：

1. 先给客户端增加“同时接受旧公钥和新公钥”的过渡版本并发布。
2. 再切换服务器私钥。
3. 确认客户端都升级后，再移除旧公钥。

如果暂时不做双公钥兼容，只能在维护窗口内同时发布新客户端并切换服务器，旧客户端会失效。

### 3.3 上传并安装新私钥

本机 PowerShell：

```powershell
$key = "D:\secure\cmumcl-keys-2026-11\manifest-signing-key-2026-11.pem"
scp "$key" root@103.24.216.15:/tmp/manifest-signing-key-2026-11.pem
```

服务器上备份旧私钥：

```bash
mkdir -p /root/pcl-cmu-key-backup
cp -a /opt/pcl-cmu/keys/manifest-signing-key.pem \
  /root/pcl-cmu-key-backup/manifest-signing-key-$(date +%F-%H%M).pem
chmod 600 /root/pcl-cmu-key-backup/*.pem
```

安装新私钥：

```bash
install -m 600 -o root -g root \
  /tmp/manifest-signing-key-2026-11.pem \
  /opt/pcl-cmu/keys/manifest-signing-key.pem
rm -f /tmp/manifest-signing-key-2026-11.pem
```

确认：

```bash
ls -l /opt/pcl-cmu/keys/manifest-signing-key.pem
```

必须是 `-rw------- root root`。

### 3.4 重启并验证

如果只换了私钥而没有改代码：

```bash
systemctl restart pcl-cmu
systemctl status pcl-cmu --no-pager
```

如果改了 `ManifestSigner.KeyId` 或后端代码，先部署新的 `publish`，再重启。

验证服务：

```bash
journalctl -u pcl-cmu -n 50 --no-pager
```

客户端下载并验证：

```powershell
curl.exe -fsS https://103.24.216.15/manifest.json -o manifest.json
curl.exe -fsS https://103.24.216.15/manifest.json.sig -o manifest.json.sig

$sig = (Get-Content .\manifest.json.sig -Raw | ConvertFrom-Json).signature
[IO.File]::WriteAllBytes(
  (Join-Path $PWD 'manifest.sig.bin'),
  [Convert]::FromBase64String($sig)
)

& "C:\Program Files\Git\usr\bin\openssl.exe" dgst -sha256 -verify `
  "D:\secure\cmumcl-keys-2026-11\manifest-signing-public-2026-11.pem" `
  -signature .\manifest.sig.bin .\manifest.json
```

应输出：

```text
Verified OK
```

然后用新客户端实际拉取一次资源清单，确认不再报“清单签名验证失败”。

### 3.5 清理旧密钥

确认新客户端、旧客户端过渡策略和签名验证都正常后：

- 旧私钥只保留在离线备份中，不要继续放在可访问目录
- 本地 `admin-backend/keys/manifest-signing-key.pem` 移出工作区
- 不要提交任何私钥到 Git
- 本地只保留公钥或密码管理器中的恢复材料

---

## 4. 如果怀疑密钥已经暴露

按以下顺序处理：

1. **立即轮换管理员密码**，让旧会话失效。
2. **立即轮换 manifest 签名私钥**，但先准备好客户端公钥切换方案。
3. 删除/移出 `D:\新启动器\admin-credentials.txt`。
4. 把本地 `admin-backend/keys/manifest-signing-key.pem` 移出工作区。
5. 检查服务器日志：

```bash
journalctl -u pcl-cmu --since "2026-10-29 22:00" --no-pager
```

6. 如发现异常登录/上传，检查 `wwwroot/packs` 和 `data/instances.json` 是否有未授权变更。

---

## 5. 回滚

### 管理员密码

从安全位置恢复旧 `admin.env` 并重启，只建议在确认新密码配置失败且没有泄露风险时使用。

### 签名密钥

如果新客户端尚未准备好，旧客户端仍在使用旧公钥：

1. 恢复旧私钥到服务器
2. 恢复旧后端配置/代码
3. 重启服务
4. 确认旧 `manifest.json.sig` 能通过旧客户端验证

因此轮换前必须保留旧私钥和旧后端版本的离线备份，但不要把它们继续放在日常可访问目录。

---

## 6. 检查清单

- [ ] SSH 可以登录资源服务器
- [ ] 确认 `443` 公网、`9443` 仅回环监听
- [ ] 新管理员密码已生成哈希并写入 `admin.env`
- [ ] 新管理员密码可以登录，旧密码不可登录
- [ ] 新签名密钥已离线生成
- [ ] 客户端新公钥已准备并发布
- [ ] 新私钥以 `600` 权限安装到服务器
- [ ] `pcl-cmu` 启动正常
- [ ] `manifest.json.sig` 用新公钥验证 `Verified OK`
- [ ] 新客户端实际拉取清单成功
- [ ] 本地明文凭据和私钥已移出工作区
