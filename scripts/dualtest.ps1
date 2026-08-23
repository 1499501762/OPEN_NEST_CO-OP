# Open Nest Co-op - dual-instance test launcher.
# Usage:
#   .\scripts\dualtest.ps1 -Local              # same-machine, NO Steam (TCP loopback)
#   .\scripts\dualtest.ps1                      # two Steam sessions (--autohost/--autojoin)
#   .\scripts\dualtest.ps1 -HostOnly            # only start host
#   .\scripts\dualtest.ps1 -ClientOnly          # only start client
#   .\scripts\dualtest.ps1 -Local -HostGame G:\... -ClientGame D:\...
#   .\scripts\dualtest.ps1 -Local -Lag 100 -LagJitter 30   # override simulated delay
#
# -Local: host listens on 127.0.0.1:port, client connects -- no Steam needed.
# Without -Local: requires TWO Steam sessions (two accounts) because Steamworks
# rejects two same-AppID processes on one Steam client.
#
# Constants (port / delay / jitter / lobby file / exe name) live in scripts/env.ps1
# (LocalTestPort / LocalTestLagMs / LocalTestLagJitterMs / LobbyFile / SteamExe).
# Pass -Lag / -LagJitter to override for this run; -Lag 0 disables delay simulation.
#
# NOTE: Keep this file PURE ASCII (PowerShell 5.1 compatibility).
[CmdletBinding()]
param(
    [string]$HostGame,
    [string]$ClientGame,
    [switch]$HostOnly,
    [switch]$ClientOnly,
    [string]$LobbyFile,
    [switch]$Local,
    [int]$Port = -1,
    [int]$Lag = -1,
    [int]$LagJitter = -1,
    [string]$Sync
)

$ErrorActionPreference = "Stop"

# 记录显式传入的 -ClientGame，避免被 env.ps1 覆盖
$clientGameParam = $ClientGame

# Load env.ps1 (optional) for defaults: $GameDir / $ClientGame / $SteamExe /
# $LocalTestPort / $LocalTestLagMs / $LocalTestLagJitterMs / $LobbyFile
$envFile = Join-Path $PSScriptRoot "env.ps1"
if (Test-Path $envFile) { . $envFile }

if (-not $HostGame) { $HostGame = $GameDir }
# 第二个安装（本地双开客户端）：显式 -ClientGame 优先，否则用 env.ps1 的 $ClientGame
if ($clientGameParam) { $ClientGame = $clientGameParam }

if (-not $HostGame -or -not (Test-Path $HostGame)) {
    Write-Host "Host game not found: '$HostGame' (set GameDir in scripts/env.ps1 or pass -HostGame)" -ForegroundColor Yellow
    exit 1
}

# 默认常量（env.ps1 提供，未设置时回退）：
#   SteamExe / LocalTestPort / LocalTestLagMs / LocalTestLagJitterMs / LobbyFile
if (-not $SteamExe) { $SteamExe = "Iron Nest Heavy Turret Simulator.exe" }
if ($Port -lt 0) { $Port = if ($LocalTestPort) { $LocalTestPort } else { 29507 } }
if ($Lag -lt 0) { $Lag = if ($LocalTestLagMs) { $LocalTestLagMs } else { 0 } }
if ($LagJitter -lt 0) { $LagJitter = if ($LocalTestLagJitterMs) { $LocalTestLagJitterMs } else { 0 } }

$Exe = $SteamExe
$hostExe = Join-Path $HostGame $Exe
if (-not (Test-Path $hostExe)) { Write-Host "No game exe at $hostExe" -ForegroundColor Yellow; exit 1 }

if (-not $LobbyFile) { $LobbyFile = Join-Path $env:TEMP "open_nest_lobby.txt" }
if (Test-Path $LobbyFile) { Remove-Item $LobbyFile -Force }

# NOTE: -window-mode borderless lets us position each window on a different
# monitor via MoveWindow (a fullscreen window always grabs the primary display).
$WindowArg = "-window-mode borderless"

# 同步方案：-Sync new → 两端都传 --sync new（--sync old|new，默认不传=old）
$syncArg = ""
if ($Sync) { $syncArg = "--sync $Sync" }

# 网络延迟模拟：--lag <ms>（基础单向延迟）+ --lagjitter <ms>（波动 ±）。Lag=0 时不传。
$lagArg = ""
if ($Lag -gt 0) { $lagArg = "--lag $Lag --lagjitter $LagJitter" }

function Start-Game {
    param([string]$ExePath, [string]$Arg)
    $fullArg = "$Arg $syncArg $lagArg $WindowArg"
    Write-Host "Launching: $ExePath $fullArg" -ForegroundColor Cyan
    Start-Process -FilePath $ExePath -ArgumentList $fullArg -WorkingDirectory (Split-Path $ExePath)
}

if ($Local) {
    # ---- Local TCP loopback mode (NO Steam) ----
    Write-Host "== LOCAL mode (no Steam), port $Port ==" -ForegroundColor Magenta
    if (-not $ClientOnly) {
        Start-Game $hostExe "--local host --localport $Port"
    }
    if (-not $HostOnly) {
        if (-not $ClientGame) {
            Write-Host "No client game path (-ClientGame). Local mode needs a second install to launch the client." -ForegroundColor Yellow
            exit 1
        }
        $clientExe = Join-Path $ClientGame $Exe
        if (-not (Test-Path $clientExe)) { Write-Host "Client game exe not found: $clientExe" -ForegroundColor Yellow; exit 1 }
        # Wait until host port is open (client retries internally too)
        Write-Host "Waiting for host port $Port to open..." -ForegroundColor Yellow
        $deadline = (Get-Date).AddSeconds(60)
        while ((Get-Date) -lt $deadline) {
            $c = New-Object System.Net.Sockets.TcpClient
            try { $c.Connect("127.0.0.1", $Port); $c.Close(); break }
            catch { $c.Close(); Start-Sleep -Milliseconds 500 }
        }
        Write-Host "Host port open. Starting client..." -ForegroundColor Green
        Start-Game $clientExe "--local join --localport $Port"
    }
    Write-Host ""
    Write-Host "Local dual-instance launched (host + client on this machine, no Steam)." -ForegroundColor Green
    Write-Host "Both game processes can share one Steam session now -- they bypass Steam for P2P." -ForegroundColor Green
    exit 0
}

# ---- Steam mode (two Steam sessions) ----
Write-Host "== Steam mode (needs two Steam sessions/accounts) ==" -ForegroundColor Cyan

if (-not $ClientOnly) {
    Write-Host "== Host: auto-create lobby ==" -ForegroundColor Green
    Start-Game $hostExe "--autohost --autolobby `"$LobbyFile`""

    if (-not $HostOnly) {
        Write-Host "Waiting for host lobby file: $LobbyFile" -ForegroundColor Yellow
        $deadline = (Get-Date).AddSeconds(45)
        while (-not (Test-Path $LobbyFile) -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
        }
        if (-not (Test-Path $LobbyFile)) {
            Write-Host "Timed out waiting for host lobby (is Steam logged in?)" -ForegroundColor Red
            exit 1
        }
        Write-Host "Host lobby ready: $(Get-Content $LobbyFile)" -ForegroundColor Green
    }
}

if (-not $HostOnly) {
    if (-not $ClientGame) {
        Write-Host "No client game path (-ClientGame). Auto-joining from THIS install is not possible with same Steam session." -ForegroundColor Yellow
        Write-Host "If you have a second install, pass -ClientGame <path>." -ForegroundColor Yellow
    }
    elseif (-not (Test-Path (Join-Path $ClientGame $Exe))) {
        Write-Host "Client game exe not found: $(Join-Path $ClientGame $Exe)" -ForegroundColor Yellow
    }
    else {
        Write-Host "== Client: auto-join host ==" -ForegroundColor Green
        Start-Game (Join-Path $ClientGame $Exe) "--autojoin --autolobby `"$LobbyFile`""
    }
}

Write-Host ""
Write-Host "Launched. NOTE: each game process needs its OWN Steam session (different account)." -ForegroundColor Magenta
Write-Host "If both games try to use one Steam client, the second SteamAPI_Init will fail." -ForegroundColor Magenta
