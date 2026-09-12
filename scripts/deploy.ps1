# Open Nest Co-op - build and deploy to BOTH game ends (BepInEx G + MLL D).
# Usage:
#   .\scripts\deploy.ps1            # 默认双端：BepInEx(G 盘 BepInEx\plugins) + MLL(D 盘 Mods\UserLibs)
#   .\scripts\deploy.ps1 -BepOnly   # 只部署 BepInEx 端（G 盘）
#   .\scripts\deploy.ps1 -MllOnly   # 只部署 MLL 端（D 盘）
# Depends on scripts/env.ps1 (copy from env.example.ps1 and fill local paths).
#   GameDir = G 盘 BepInEx 宿主；ClientGame = D 盘 MLL（MelonLoader）客户端。
# NOTE: Keep this file PURE ASCII (PowerShell 5.1 compatibility).
[CmdletBinding()]
param(
    [switch]$BepOnly,   # 只部署 BepInEx 端（G 盘）
    [switch]$MllOnly    # 只部署 MLL 端（D 盘）
)
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

# 默认双端；-BepOnly / -MllOnly 只部署一端
$wantBep = -not $MllOnly
$wantMll = -not $BepOnly

$bepProj = Join-Path $root "src\OpenNestCoop\OpenNestCoop.csproj"
$mlProj  = Join-Path $root "src\OpenNestCoop.MelonMod\OpenNestCoop.MelonMod.csproj"

# 玩家化身 bundle（方案A）：若仓库 model\player.bundle 存在则同步到两端游戏 Models\，
# 供 AnimatorAvatarVisualProvider 运行时加载（见 tools/playerbundle/）。
$bundleSrc = Join-Path $root "model\player.bundle"

# ============ 1. BepInEx 端（G 盘）============
if ($wantBep) {
    Write-Host ""
    Write-Host "== [1/2] BepInEx 版 -> $GameDir ==" -ForegroundColor Cyan
    Write-Host "  Config:    $BuildConfig"
    dotnet build $bepProj -c $BuildConfig -p:DeployToGame=true
    if ($LASTEXITCODE -ne 0) {
        Write-Host "BEPINEX BUILD FAILED" -ForegroundColor Red
        exit 1
    }
    Write-Host "== Deployed BepInEx ==" -ForegroundColor Green
    Get-ChildItem $GamePluginsDir | Select-Object Name, Length
    # bundle -> G 端 Models\
    if (Test-Path $bundleSrc) {
        $gameModels = Join-Path $GameDir "Models"
        if (-not (Test-Path $gameModels)) { New-Item -ItemType Directory -Path $gameModels | Out-Null }
        Copy-Item $bundleSrc (Join-Path $gameModels "player.bundle") -Force
        Write-Host "Copied player.bundle -> $gameModels" -ForegroundColor Cyan
    } else {
        Write-Host "No model\player.bundle (build it via tools/playerbundle/ if you want Animator avatars)" -ForegroundColor DarkGray
    }
} else {
    Write-Host ""
    Write-Host "== 跳过 BepInEx 端（-MllOnly）==" -ForegroundColor DarkGray
}

# ============ 2. MLL 端（D 盘，MelonLoader）============
if ($wantMll) {
    if (-not $ClientGame) {
        Write-Host ""
        Write-Host "No ClientGame (MLL 端路径) - set \$ClientGame in scripts/env.ps1. Skipping MLL deploy." -ForegroundColor Yellow
    }
    elseif (-not (Test-Path $ClientGame)) {
        Write-Host ""
        Write-Host "ClientGame not found: '$ClientGame' - skipping MLL deploy." -ForegroundColor Yellow
    }
    else {
        Write-Host ""
        Write-Host "== [2/2] MelonLoader 版 -> $ClientGame ==" -ForegroundColor Cyan
        dotnet build $mlProj -c $BuildConfig
        if ($LASTEXITCODE -ne 0) {
            Write-Host "MLL BUILD FAILED" -ForegroundColor Red
            exit 1
        }
        # 标准 MelonLoader 部署结构：Mods\OpenNestCoop.MelonMod.dll + UserLibs\ 依赖
        $mlBin      = Join-Path $root "src\OpenNestCoop.MelonMod\bin\Release\net6.0"
        $clientMods = Join-Path $ClientGame "Mods"
        $clientLibs = Join-Path $ClientGame "UserLibs"
        if (-not (Test-Path $clientMods)) { New-Item -ItemType Directory -Path $clientMods | Out-Null }
        if (-not (Test-Path $clientLibs)) { New-Item -ItemType Directory -Path $clientLibs | Out-Null }
        Copy-Item (Join-Path $mlBin "OpenNestCoop.MelonMod.dll") (Join-Path $clientMods "OpenNestCoop.MelonMod.dll") -Force
        Copy-Item (Join-Path $mlBin "LiteNetLib.dll")            (Join-Path $clientLibs "LiteNetLib.dll")            -Force
        Copy-Item (Join-Path $mlBin "SharpGLTF.Core.dll")        (Join-Path $clientLibs "SharpGLTF.Core.dll")        -Force
        Copy-Item (Join-Path $mlBin "SharpGLTF.Runtime.dll")     (Join-Path $clientLibs "SharpGLTF.Runtime.dll")     -Force
        Write-Host "== Deployed MLL ==" -ForegroundColor Green
        Get-ChildItem $clientMods | Select-Object Name, Length
        # bundle -> D 端 Models\
        if (Test-Path $bundleSrc) {
            $clientModels = Join-Path $ClientGame "Models"
            if (-not (Test-Path $clientModels)) { New-Item -ItemType Directory -Path $clientModels | Out-Null }
            Copy-Item $bundleSrc (Join-Path $clientModels "player.bundle") -Force
            Write-Host "Copied player.bundle -> $clientModels" -ForegroundColor Cyan
        }
    }
} else {
    Write-Host ""
    Write-Host "== 跳过 MLL 端（-BepOnly）==" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "Deploy done. Launch both games and open the coop menu (top-left) to test." -ForegroundColor Green
Write-Host "NOTE: close the games first if dll is locked (file in use)." -ForegroundColor DarkGray
