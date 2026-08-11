# ============================================================
# Open Nest Co-op 打包脚本
# 本地打包四个版本到 release/：
#   1. OpenNestCoop-<ver>-BepInEx-Mod.zip           (BepInEx 光 MOD: dll + LiteNetLib)
#   2. OpenNestCoop-<ver>-MelonLoader-Mod.zip       (MelonLoader 光 MOD: dll + LiteNetLib)
#   3. OpenNestCoop-<ver>-BepInEx-Standalone.zip    (BepInEx 6 加载器 + MOD + 依赖)
#   4. OpenNestCoop-<ver>-MelonLoader-Standalone.zip(MelonLoader 加载器 + MOD + 依赖)
# 用法: powershell -ExecutionPolicy Bypass -File .\scripts\package.ps1
# ============================================================
param(
    [string]$GameDirG = "G:\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator",
    [string]$GameDirD = "D:\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator",
    [string]$Version = "0.1.0"
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # Compress-Archive 大文件时 Write-Progress 会崩，禁用进度条
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root
$OutDir = Join-Path $Root "release"
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

# ---------- 1. 构建 ----------
Write-Host "== 构建 BepInEx 版 =="
dotnet build "src\OpenNestCoop\OpenNestCoop.csproj" -c Release 2>&1 | Select-Object -Last 1
Write-Host "== 构建 MelonLoader 版 =="
dotnet build "src\OpenNestCoop.MelonMod\OpenNestCoop.MelonMod.csproj" -c Release 2>&1 | Select-Object -Last 1

$bepinBin = Join-Path $Root "src\OpenNestCoop\bin\Release\net6.0"
$mlBin    = Join-Path $Root "src\OpenNestCoop.MelonMod\bin\Release\net6.0"

# ---------- 2. staging ----------
$stage = Join-Path $env:TEMP "nest_pkg_$PID"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

function Copy-Into($pkgDir, $src, $relDest) {
    $dest = Join-Path $pkgDir $relDest
    New-Item -ItemType Directory -Path (Split-Path $dest) -Force | Out-Null
    Copy-Item $src $dest -Force
}

function New-Package($pkgName, $pkgDir) {
    $zip = Join-Path $OutDir "$pkgName.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path "$pkgDir\*" -DestinationPath $zip -CompressionLevel Optimal
    Write-Host ("打包完成: {0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
}

$readmeMod = @"
Open Nest Co-op v$Version - 光 MOD 包
=====================================
BepInEx 版 / MelonLoader 版 联机模组（不含模组加载器）。

【安装 - BepInEx 版】
1. 已安装 BepInEx 6 (IL2CPP) 并运行过一次游戏。
2. 解压本包，把 plugins\ 下所有文件复制到:
      <游戏目录>\BepInEx\plugins\
3. 启动游戏，主菜单出现"联机"入口即成功。

【安装 - MelonLoader 版】
1. 已安装 MelonLoader。
2. 解压本包，把 Mods\ 下 dll 复制到 <游戏目录>\Mods\，
   UserLibs\ 下 dll 复制到 <游戏目录>\UserLibs\。
3. 启动游戏，主菜单出现"联机"入口即成功。

【用法】主机创建房间 → 客机加入（Steam 好友/大厅，或本地双开）。
【依赖】LiteNetLib.dll（已包含）。
"@

$readmeStandalone = @"
Open Nest Co-op v$Version - Standalone 包
=========================================
已包含模组加载器（BepInEx 6 或 MelonLoader）+ 联机模组 + 依赖，解压即用。

【安装 - BepInEx Standalone】
1. 把本包所有内容复制/合并到游戏根目录:
      <Steam>\steamapps\common\Iron Nest Heavy Turret Simulator\
   （含 BepInEx\ 文件夹、dotnet\ 文件夹、winhttp.dll、doorstop_config.ini）
2. 首次运行会生成 BepInEx\interop\ 等（若包内已带 interop 则直接可用）。
3. 启动游戏，主菜单出现"联机"入口即成功。

【安装 - MelonLoader Standalone】
1. 把本包所有内容复制/合并到游戏根目录:
      <Steam>\steamapps\common\Iron Nest Heavy Turret Simulator\
   （含 MelonLoader\ 文件夹、version.dll、Mods\、UserLibs\）
2. 启动游戏（version.dll 自动注入 MelonLoader），主菜单出现"联机"入口即成功。

【用法】主机创建房间 → 客机加入（Steam 好友/大厅，或本地双开）。
【说明】Standalone 体积较大是因为包含完整模组加载器运行时。
"@

# ---------- 3. 包 1：BepInEx 光 MOD ----------
$p1 = Join-Path $stage "OpenNestCoop-$Version-BepInEx-Mod"
New-Item -ItemType Directory -Path $p1 -Force | Out-Null
Copy-Into $p1 (Join-Path $bepinBin "OpenNestCoop.dll")  "plugins\OpenNestCoop.dll"
Copy-Into $p1 (Join-Path $bepinBin "LiteNetLib.dll")     "plugins\LiteNetLib.dll"
Set-Content -Path (Join-Path $p1 "README.txt") -Value $readmeMod -Encoding UTF8
New-Package "OpenNestCoop-$Version-BepInEx-Mod" $p1

# ---------- 4. 包 2：MelonLoader 光 MOD ----------
$p2 = Join-Path $stage "OpenNestCoop-$Version-MelonLoader-Mod"
New-Item -ItemType Directory -Path $p2 -Force | Out-Null
Copy-Into $p2 (Join-Path $mlBin "OpenNestCoop.MelonMod.dll") "Mods\OpenNestCoop.MelonMod.dll"
Copy-Into $p2 (Join-Path $mlBin "LiteNetLib.dll")             "UserLibs\LiteNetLib.dll"
Set-Content -Path (Join-Path $p2 "README.txt") -Value $readmeMod -Encoding UTF8
New-Package "OpenNestCoop-$Version-MelonLoader-Mod" $p2

# ---------- 5. 包 3：BepInEx Standalone ----------
Write-Host "== 复制 BepInEx 加载器 (G 盘) =="
$p3 = Join-Path $stage "OpenNestCoop-$Version-BepInEx-Standalone"
New-Item -ItemType Directory -Path $p3 -Force | Out-Null
# BepInEx 全套（core/interop/patchers/plugins/unity-libs/config/cache）
New-Item -ItemType Directory -Path (Join-Path $p3 "BepInEx") -Force | Out-Null
Copy-Item "$GameDirG\BepInEx\*" (Join-Path $p3 "BepInEx") -Recurse -Force
# 移除 BepInEx.MelonLoader.Loader（纯 BepInEx 不需要 ML 桥）
$mlLoader = Join-Path $p3 "BepInEx\plugins\BepInEx.MelonLoader.Loader"
if (Test-Path $mlLoader) { Remove-Item $mlLoader -Recurse -Force }
# .NET 运行时（BepInEx 6 依赖）
Copy-Item "$GameDirG\dotnet" (Join-Path $p3 "dotnet") -Recurse -Force
# doorstop 注入
Copy-Item "$GameDirG\winhttp.dll"        (Join-Path $p3 "winhttp.dll") -Force
Copy-Item "$GameDirG\doorstop_config.ini" (Join-Path $p3 "doorstop_config.ini") -Force
# 覆盖为最新构建的 MOD + 依赖
Copy-Into $p3 (Join-Path $bepinBin "OpenNestCoop.dll") "BepInEx\plugins\OpenNestCoop.dll"
Copy-Into $p3 (Join-Path $bepinBin "LiteNetLib.dll")    "BepInEx\plugins\LiteNetLib.dll"
# 清理运行日志
Remove-Item (Join-Path $p3 "BepInEx\LogOutput.log") -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $p3 "BepInEx\ErrorLog.log") -Force -ErrorAction SilentlyContinue
Set-Content -Path (Join-Path $p3 "README.txt") -Value $readmeStandalone -Encoding UTF8
New-Package "OpenNestCoop-$Version-BepInEx-Standalone" $p3

# ---------- 6. 包 4：MelonLoader Standalone ----------
Write-Host "== 复制 MelonLoader 加载器 (D 盘) =="
$p4 = Join-Path $stage "OpenNestCoop-$Version-MelonLoader-Standalone"
New-Item -ItemType Directory -Path $p4 -Force | Out-Null
# MelonLoader 全套（排除 Logs 日志）
New-Item -ItemType Directory -Path (Join-Path $p4 "MelonLoader") -Force | Out-Null
Copy-Item "$GameDirD\MelonLoader\*" (Join-Path $p4 "MelonLoader") -Recurse -Force
Remove-Item (Join-Path $p4 "MelonLoader\Logs") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $p4 "MelonLoader\Latest.log") -Force -ErrorAction SilentlyContinue
# 注入 dll
Copy-Item "$GameDirD\version.dll" (Join-Path $p4 "version.dll") -Force
# MOD + 依赖（标准 MelonLoader 结构）
Copy-Into $p4 (Join-Path $mlBin "OpenNestCoop.MelonMod.dll") "Mods\OpenNestCoop.MelonMod.dll"
Copy-Into $p4 (Join-Path $mlBin "LiteNetLib.dll")             "UserLibs\LiteNetLib.dll"
Set-Content -Path (Join-Path $p4 "README.txt") -Value $readmeStandalone -Encoding UTF8
New-Package "OpenNestCoop-$Version-MelonLoader-Standalone" $p4

# ---------- 7. 清理 ----------
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "== 打包完成 =="
Get-ChildItem $OutDir -Filter "*.zip" | Select-Object Name, @{N='MB';E={[math]::Round($_.Length/1MB,1)}}
