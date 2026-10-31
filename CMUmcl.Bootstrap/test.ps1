param(
    [Parameter(Mandatory=$true)][string]$ZigPath,
    [Parameter(Mandatory=$true)][string]$RuntimeRoot,
    [Parameter(Mandatory=$true)][string]$InstallerPath,
    [Parameter(Mandatory=$true)][string]$RuntimeConfigPath,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new isolated test directory.' }
& (Join-Path $PSScriptRoot 'build.ps1') -ZigPath $ZigPath -OutputDirectory $output
Copy-Item -LiteralPath $RuntimeConfigPath -Destination (Join-Path $output 'CMUmcl.runtimeconfig.json')
$tampered = Join-Path $output 'tampered.exe'
$bytes = [IO.File]::ReadAllBytes($InstallerPath)
$bytes[2048] = $bytes[2048] -bxor 32
[IO.File]::WriteAllBytes($tampered, $bytes)
$fixture = Join-Path $output 'Fixture.exe'
Push-Location $PSScriptRoot
try {
    & $ZigPath cc -target x86_64-windows-gnu -O2 -s -municode '-Wl,--subsystem,windows' tests-client.c -o $fixture
    if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed.' }
    & $ZigPath cc -target x86_64-windows-gnu -O2 -s -municode -Wall -Wextra tests.c -o (Join-Path $output 'Bootstrap.Tests.exe') -lwinhttp -lbcrypt -lwintrust -lcrypt32 -lcomctl32 -lshell32 -lole32 -luuid -lgdi32 -ladvapi32
    if ($LASTEXITCODE -ne 0) { throw 'Tests compilation failed.' }
} finally { Pop-Location }
@"
[Preview]
Executable=$fixture
Runtime=$RuntimeRoot
WorkingDirectory=$output
"@ | Set-Content -LiteralPath (Join-Path $output 'preview.ini') -Encoding Unicode
& (Join-Path $output 'Bootstrap.Tests.exe') $RuntimeRoot $InstallerPath $tampered | Tee-Object (Join-Path $output 'result.txt')
if ($LASTEXITCODE -ne 0) { throw 'Bootstrap component tests failed.' }
Start-Sleep -Milliseconds 300
if ([IO.File]::ReadAllText((Join-Path $output 'started.txt'),[Text.Encoding]::Unicode) -ne $RuntimeRoot) { throw 'Fixture runtime inheritance check failed.' }
Write-Output 'PASS isolated preview process received the selected runtime root.'
