# ============================================================================
#  Open Nest Co-op - Dev Environment Global Variables (TEMPLATE)
#  NOTE: Keep this file PURE ASCII (PowerShell 5.1 compatibility).
# ============================================================================
#  Usage:
#    1) Copy this file to scripts/env.ps1
#    2) Fill in your local game path / params in env.ps1
#    3) scripts/deploy.ps1 sources env.ps1 automatically (no manual edit)
#
#  Open-source note: this template is committed to the repo as a reference.
#  env.ps1 (with your absolute local paths) is ignored by .gitignore.
#
#  These variables are consumed by:
#    - scripts/deploy.ps1 (build + deploy)
#    - GameDir is exported as an MSBuild env var, read by
#      src/OpenNestCoop/OpenNestCoop.csproj as $(GameDir) / $(BepInExDir).
# ============================================================================

# --- Game install directory (Steam library path) ---
$GameDir = "C:\path\to\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator"

# --- Second game install (local dual-instance client / MelonLoader packaging source) ---
# 本地双开客户端用的第二个安装（如 MelonLoader 版）；只有一个安装则留空。
$ClientGame = "C:\path\to\Second\Iron Nest Heavy Turret Simulator"

# --- Steam AppID of the game ---
$SteamAppId = 2950790

# --- Build configuration (Debug / Release) ---
$BuildConfig = "Release"

# --- Plugin folder name (deploy target dir name) ---
$PluginName = "OpenNestCoop"

# --- Derived paths (usually no need to change) ---
$BepInExDir = Join-Path $GameDir "BepInEx"
$GamePluginsDir = Join-Path $BepInExDir ("plugins\" + $PluginName)

# --- Export as env vars (for MSBuild/csproj $(GameDir)) ---
$env:GameDir = $GameDir
$env:SteamAppId = "$SteamAppId"
