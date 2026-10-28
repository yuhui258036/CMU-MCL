# CMUmcl — 中南大学 Minecraft 社区启动器

CMUmcl 是中南大学 Minecraft 社区（CSU-MC）的官方启动器。
当前版本 **0.1.0** 为第一版，仅实现 **CSU-MC 皮肤站账号登录门**（登录 / 校验 / 刷新 / 角色选择 / 退出登录）。

> 皮肤站：https://csu-mc.magicalsheep.cn （Blessing Skin 6.0.2）
> 仅支持经典 Yggdrasil 认证，**不支持** OIDC / Janus。

## 环境要求

- Windows
- .NET 10 SDK（10.0.403）
- 需要联网访问皮肤站 API

## 构建

```powershell
$env:DOTNET_ROOT = "C:\Users\PC\AppData\Local\Microsoft\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
& "$env:DOTNET_ROOT\dotnet.exe" build "CMUmcl\CMUmcl.csproj" -c Debug
```

构建产物：
```
CMUmcl\bin\Debug\net10.0-windows\CMUmcl.exe
```

## 运行

直接运行 `CMUmcl.exe`，或：

```powershell
& "CMUmcl\bin\Debug\net10.0-windows\CMUmcl.exe"
```

首次启动会显示登录窗口；登录成功后进入主界面（当前为占位界面）。

## 功能说明

- **登录门**：启动后先显示 `LoginWindow`，登录成功前不显示主界面，无跳过登录入口。
- **会话恢复**：若存在已保存会话，先 `/authserver/validate` 校验；403 时尝试 `/authserver/refresh`；刷新成功则保存并进入主界面，均失败则回到登录表单。
- **登录表单**：用户名 / 邮箱 + 密码（`PasswordBox`）+「记住登录」复选框；按钮：登录、注册账号、忘记密码；回车提交、登录中禁用按钮、中文错误提示。
- **角色选择**：登录返回的 `selectedProfile` 缺失且存在多个角色时弹出角色选择对话框。
- **退出登录**：主界面「退出登录」清除已保存会话并返回登录窗口。

## Yggdrasil API

- 根地址：`https://csu-mc.magicalsheep.cn/api/yggdrasil`
- `POST /authserver/authenticate`：登录
- `POST /authserver/validate`：校验（204 = 有效，403 = 无效）
- `POST /authserver/refresh`：刷新令牌
- 注册：`https://csu-mc.magicalsheep.cn/auth/register`
- 登录页：`https://csu-mc.magicalsheep.cn/auth/login`

HTTP 超时约 20 秒，User-Agent 为 `CMUmcl/0.1.0`。

## 项目结构

```
CMUmcl/
├─ CMUmcl.csproj
├─ App.xaml / App.xaml.cs            # 启动门 / 会话恢复 / 窗口流转
├─ LoginWindow.xaml / .xaml.cs       # 登录窗口
├─ ProfileSelectWindow.xaml / .xaml.cs # 角色选择对话框
├─ MainWindow.xaml / .xaml.cs        # 主窗口（占位）
├─ Auth/
│  ├─ YggdrasilClient.cs             # Yggdrasil HTTP 客户端
│  ├─ AuthSession.cs                 # 会话模型
│  └─ AuthStore.cs                   # DPAPI 令牌存储
└─ README-login.md
```

## 安全说明

- 令牌存储在 `%LOCALAPPDATA%\CMUmcl\auth.dat`，使用 DPAPI `DataProtectionScope.CurrentUser` 加密，仅当前 Windows 用户可解密。
- **密码从不落盘**，仅保存在内存中用于本次认证请求。
- `clientToken` 为持久化的稳定 GUID，跨运行保持不变。
- 「记住登录」勾选时保存会话；未勾选时清除已保存会话，仅本次运行内存中保留。

## 已知限制 / 注意事项

- 仅支持经典 Yggdrasil，不支持 OIDC / Janus。
- 主界面为占位界面，无实例列表、下载、启动等能力（后续版本加入）。
- 无社区 / 好友 / 离线 / 微软账户功能。
- DPAPI 绑定当前 Windows 用户与机器；更换用户或重装系统后会话需重新登录。
- 登录门使用皮肤站官方 Web 登录页 / 注册页作为找回与注册入口。
