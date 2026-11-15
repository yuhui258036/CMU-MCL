# CMUmcl .NET Framework 4.8

这是通过本地 r18 验收的 Windows x64 / .NET Framework 4.8 源码分支，与 `main` 分开维护。包含实例导入与设置、加载器维护、设置共享、社区资源、模组及资源包管理、原理图目录浏览、皮肤头像与预览、独立更新器。原理图渲染扩展尚未提供。

## 构建与运行

在 Windows 上安装 .NET SDK 和 .NET Framework 4.8 Developer Pack：

```powershell
dotnet build CMUmcl/CMUmcl.csproj -c Release
```

完整程序目录为 `CMUmcl/bin/Release/net48`，请保留依赖、`Assets`、`Licenses` 和 `updater`，不要只复制 EXE。运行需要 Windows x64 和 .NET Framework 4.8；Java 按 Minecraft 版本独立管理。

沿用 r18 的便携数据方式，默认写入程序旁 `sandbox-data`；可通过 `CMUmcl.exe --data-directory <绝对路径>` 指定目录。

## CurseForge 配置

仓库不含 CurseForge Key、带私有 Key 的测试程序或认证数据。普通源码构建可以使用 Modrinth；CurseForge 需要维护者单独配置自己的项目 Key。

代码支持运行环境变量 `CMUMCL_CURSEFORGE_API_KEY`，或私有构建时的可选程序集资源。不要把真实值写入源码、脚本、日志或提交。私有构建使用 `CMUMCL_CF_PRIVATE_BUILD_ROOT` 和 `CMUMCL_CF_BUILD_RESOURCE` 指定隔离输出与外部资源文件；仓库不提供真实资源文件。客户端中嵌入的凭据可以被提取，带凭据程序不属于本次源码发布内容。

## 测试

```powershell
dotnet run --project tests/Community.Tests -c Release
dotnet run --project tests/Bottom.Tests -c Release
dotnet run --project tests/Bottom.Tests -c Release -- --launch-directory-only
dotnet run --project tests/ResourceDownload.Tests -c Release
dotnet build tests/LauncherSettings.Tests -c Release
$source = (Get-Location).Path
$artifacts = Join-Path $env:TEMP ('cmumcl-net48-tests-' + [Guid]::NewGuid().ToString('N'))
& tests/LauncherSettings.Tests/bin/Release/net48/CMUmcl.LauncherSettings.Tests.exe $source $artifacts
```

界面测试使用独立临时数据和模拟接口，需要交互式 Windows 桌面。专项入口见 [测试范围](docs/NET48.md)。

## 发布范围

本分支只包含源码、测试、必要公开资源和许可证，不包含实例、认证、私有测试包、本地验收日志或构建缓存。未创建 GitHub Release、签名更新 feed 或服务器部署。

项目许可证见 [LICENSE](LICENSE)，第三方声明见 [CMUmcl/Licenses](CMUmcl/Licenses)。
