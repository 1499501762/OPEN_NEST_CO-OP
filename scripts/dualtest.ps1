# Open Nest Co-op - dual-instance test launcher.
# Usage:
#   .\scripts\dualtest.ps1 -Local              # same-machine, NO Steam (TCP loopback)
#   .\scripts\dualtest.ps1                      # two Steam sessions (--autohost/--autojoin)
#   .\scripts\dualtest.ps1 -HostOnly            # only start host
#   .\scripts\dualtest.ps1 -ClientOnly          # only start client
#   .\scripts\dualtest.ps1 -Local -HostGame G:\... -ClientGame D:\...
#
# -Local: host listens on 127.0.0.1:port, client connects -- no Steam needed.
# Without -Local: requires TWO Steam sessions (two accounts) because Steamworks
# rejects two same-AppID processes on one Steam client.
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
    [int]$Port = 29507
)

$ErrorActionPreference = "Stop"

# Load env.ps1 (optional) for default $GameDir
$envFile = Join-Path $PSScriptRoot "env.ps1"
if (Test-Path $envFile) { . $envFile }

if (-not $HostGame) { $HostGame = $GameDir }

if (-not $HostGame -or -not (Test-Path $HostGame)) {
    Write-Host "Host game not found: '$HostGame' (set GameDir in scripts/env.ps1 or pass -HostGame)" -ForegroundColor Yellow
    exit 1
}

$Exe = "Iron Nest Heavy Turret Simulator.exe"
$hostExe = Join-Path $HostGame $Exe
if (-not (Test-Path $hostExe)) { Write-Host "No game exe at $hostExe" -ForegroundColor Yellow; exit 1 }

if (-not $LobbyFile) { $LobbyFile = Join-Path $env:TEMP "open_nest_lobby.txt" }
if (Test-Path $LobbyFile) { Remove-Item $LobbyFile -Force }

function Start-Game {
    param([string]$ExePath, [string]$Arg)
    Write-Host "Launching: $ExePath $Arg" -ForegroundColor Cyan
    Start-Process -FilePath $ExePath -ArgumentList $Arg -WorkingDirectory (Split-Path $ExePath)
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
