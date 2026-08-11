# Open Nest Co-op - build and deploy to the game plugin folder.
# Usage: .\scripts\deploy.ps1
# Depends on scripts/env.ps1 (copy from env.example.ps1 and fill local paths).
# NOTE: Keep this file PURE ASCII (PowerShell 5.1 compatibility).
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot

# ---- Load dev environment global variables ----
$envFile = Join-Path $PSScriptRoot "env.ps1"
if (-not (Test-Path $envFile)) {
    Write-Host "Missing $envFile" -ForegroundColor Yellow
    Write-Host "Copy scripts/env.example.ps1 to scripts/env.ps1 and set your game path." -ForegroundColor Yellow
    exit 1
}
. $envFile
if (-not $GameDir -or -not (Test-Path $GameDir)) {
    Write-Host "Invalid GameDir in env.ps1: '$GameDir'" -ForegroundColor Yellow
    Write-Host "Check scripts/env.ps1." -ForegroundColor Yellow
    exit 1
}

$proj = Join-Path $root "src\OpenNestCoop\OpenNestCoop.csproj"

Write-Host "== Building plugin ==" -ForegroundColor Cyan
Write-Host "  GameDir:   $GameDir"
Write-Host "  Config:    $BuildConfig"
dotnet build $proj -c $BuildConfig -p:DeployToGame=true

if ($LASTEXITCODE -ne 0) {
    Write-Host "BUILD FAILED" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "== Deployed to game ==" -ForegroundColor Green
Get-ChildItem $GamePluginsDir | Select-Object Name, Length
Write-Host ""
Write-Host "Launch the game via Steam and open the coop menu (top-left) to test."
