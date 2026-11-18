# Build ddc.exe + new-ddc.exe (Windows PowerShell 5.1 / pwsh 7)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found: install .NET Framework 4.x.' }

Write-Host "[1/2] Using: $csc"
Write-Host '[2/2] Compiling ...'

& $csc /nologo /target:exe /platform:anycpu /optimize+ /codepage:65001 "/out:$root\ddc.exe" (Join-Path $root 'ddc.cs')
if ($LASTEXITCODE -ne 0) { throw "ddc.exe build failed (exit=$LASTEXITCODE)" }

& $csc /nologo /target:exe /platform:anycpu /optimize+ /codepage:65001 "/out:$root\new-ddc.exe" (Join-Path $root 'new-ddc.cs')
if ($LASTEXITCODE -ne 0) { throw "new-ddc.exe build failed (exit=$LASTEXITCODE)" }

Copy-Item -Force (Join-Path $root 'new-ddc.exe') (Join-Path $root 'new_ddc.exe')

Write-Host '[OK] built: ddc.exe, new-ddc.exe (alias new_ddc.exe)'
Write-Host '     ddc.exe test'
Write-Host '     new-ddc.exe help'