param(
    [Parameter(Mandatory=$true)][string]$ZigPath,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$ZigPath = (Resolve-Path -LiteralPath $ZigPath).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
Push-Location $PSScriptRoot
try {
    & $ZigPath rc /fo (Join-Path $output 'bootstrap.res') bootstrap.rc
    if ($LASTEXITCODE -ne 0) { throw 'Resource compilation failed.' }
    & $ZigPath cc -target x86_64-windows-gnu -O2 -s -municode '-Wl,--subsystem,windows' -Wall -Wextra bootstrap.c (Join-Path $output 'bootstrap.res') -o (Join-Path $output 'CMUmcl.Start.exe') -lwinhttp -lbcrypt -lwintrust -lcrypt32 -lcomctl32 -lshell32 -lole32 -luuid -lgdi32 -ladvapi32
    if ($LASTEXITCODE -ne 0) { throw 'Bootstrap compilation failed.' }
} finally { Pop-Location }
