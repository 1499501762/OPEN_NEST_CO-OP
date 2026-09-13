# 打开/调用 九宫格切片工具（OpenNestUIKit 用）
#
# 为什么有这个包装脚本：工具在 tools\slice_tool.py，从 scripts\ 或仓库根直接敲 `python slice_tool.py` 会找不到文件。
# 本脚本自己定位仓库根并转发参数，因此从任意 cwd 都能用。
#
# 用法:
#   scripts\slice-tool.ps1                      # 打开图形界面（推荐）
#   scripts\slice-tool.ps1 -SelfTest            # 工具自检（无界面）
#   scripts\slice-tool.ps1 -Deploy              # 把 ref\ui_slices.ini 部署到游戏（BepInEx\config / UserData）
#   scripts\slice-tool.ps1 --tags               # 其余参数原样转给 slice_tool.py
#   scripts\slice-tool.ps1 --set "UI Box line=8,8,8,8,1,sliced,1,separator" -Deploy
#
# 依赖: Python 3 + Pillow（预览渲染）；tkinter 仅 GUI 需要（Python 自带）。
param(
    [switch]$SelfTest,
    [switch]$Deploy,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Args
)

$ErrorActionPreference = 'Stop'

# 仓库根 = 本脚本所在目录的上一级
$root = Split-Path -Parent $PSScriptRoot
$tool = Join-Path $root 'tools\slice_tool.py'
if (-not (Test-Path $tool)) { throw "找不到工具: $tool" }

$py = Get-Command python -ErrorAction SilentlyContinue
if (-not $py) { throw '找不到 python（请确认已安装且加入 PATH）' }

$argvList = @($tool)
if ($SelfTest) { $argvList += '--selftest' }
if ($Args) { $argvList += $Args }
if ($Deploy) { $argvList += '--deploy' }

Write-Host "> python $($argvList -join ' ')" -ForegroundColor DarkGray
# 以仓库根为工作目录运行（工具内部也以仓库根解析相对路径，双保险）
Push-Location $root
try { & python @argvList } finally { Pop-Location }
