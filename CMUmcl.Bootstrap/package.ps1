param(
    [Parameter(Mandatory=$true)][string]$ZigPath,
    [Parameter(Mandatory=$true)][string]$DotnetPath,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw '请选择尚不存在的输出目录，避免混入旧文件。' }
$project = Join-Path $PSScriptRoot '..\CMUmcl\CMUmcl.csproj'
& $DotnetPath publish $project -c Release -p:PublishProfile=SmallFrameworkDependent -p:UseSharedCompilation=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Client publish failed.' }
$buildOutput = Join-Path $output '..\bootstrap-build'
& (Join-Path $PSScriptRoot 'build.ps1') -ZigPath $ZigPath -OutputDirectory $buildOutput
Copy-Item -LiteralPath (Join-Path $buildOutput 'CMUmcl.Start.exe') -Destination $output
@'
请完整解压，然后双击 CMUmcl.Start.exe。
已安装 .NET Desktop Runtime 10 x64 时，直接进入客户端。
缺少运行库时，确认后从微软下载、校验并调用官方安装程序。
Windows 可能请求管理员确认；安装成功后自动启动客户端，不会自动重启电脑。
请保留原来的 CMUmcl.exe 和 updater 文件夹，更新流程仍使用它们。
'@ | Set-Content -LiteralPath (Join-Path $output '启动说明.txt') -Encoding UTF8
Write-Output "客户端与原生引导器已生成：$output"
