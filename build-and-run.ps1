# AltTabReplacer one-shot build + run script.
# Usage:  .\build-and-run.ps1
#
# First run: dotnet restore + build + run.
# Later runs: incremental build.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

Write-Host ''
Write-Host '[1/4] Checking .NET 8 SDK...' -ForegroundColor Cyan
$sdks = & dotnet --list-sdks 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host '  dotnet command not found.' -ForegroundColor Red
    Write-Host '  Install: winget install Microsoft.DotNet.SDK.8' -ForegroundColor Yellow
    exit 1
}
$has8 = $false
foreach ($line in $sdks) {
    if ($line -match '^8\.') { $has8 = $true; break }
}
if (-not $has8) {
    Write-Host '  .NET 8 SDK not found. Currently installed:' -ForegroundColor Red
    & dotnet --list-sdks
    Write-Host ''
    Write-Host '  Note: .NET 9/10 SDK can also build net8.0 projects. Continuing anyway.' -ForegroundColor Yellow
} else {
    Write-Host '  OK' -ForegroundColor Green
}

Write-Host ''
Write-Host '[2/4] dotnet restore...' -ForegroundColor Cyan
& dotnet restore
if ($LASTEXITCODE -ne 0) { Write-Host '  restore failed' -ForegroundColor Red; exit $LASTEXITCODE }
Write-Host '  OK' -ForegroundColor Green

Write-Host ''
Write-Host '[3/4] dotnet build (Debug)...' -ForegroundColor Cyan
& dotnet build -c Debug --nologo
if ($LASTEXITCODE -ne 0) { Write-Host '  build failed' -ForegroundColor Red; exit $LASTEXITCODE }
Write-Host '  OK' -ForegroundColor Green

Write-Host ''
Write-Host '[4/4] launching AltTabReplacer...' -ForegroundColor Cyan
Write-Host '  Press your hotkey (default Alt+Z) to invoke the selector.' -ForegroundColor DarkGray
Write-Host '  To exit: Stop-Process -Name AltTabReplacer  OR  Task Manager.' -ForegroundColor DarkGray
Write-Host ''

& dotnet run --project src/AltTabReplacer.App -c Debug --no-build