# 检查 Dota 2 进程里到底加载了哪个输入法的代码
#
# 原理：微信输入法是"进程内" TIP（InprocServer32 -> wetype_tip.dll），被激活时会把自己注入目标
#       进程；微软拼音是"进程外" TIP（LocalServer32 -> ChsIME.exe），不注入。
#       所以 dota2.exe 里若不存在 wetype_tip*.dll，说明微信输入法从未在游戏进程里被激活 ——
#       这正是"游戏用的是微软拼音"的 OS 层面证据。
#       注意这是单向证据：加载过的 DLL 不会卸载，所以"存在"不能反证，只有"不存在"有意义。
#
# 用法：
#   pwsh -NoProfile -File check-game-ime.ps1          # 立即检查（游戏需已在运行）
#   pwsh -NoProfile -File check-game-ime.ps1 -Wait    # 等游戏启动，等它成为前台后连续采样
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $dir          # imebind.log / rules.txt 在仓库根目录
$log = Join-Path $root 'imebind.log'

$Wait = $false
if ($args -contains '-Wait') { $Wait = $true }

function Sample {
    $p = Get-Process dota2 -ErrorAction SilentlyContinue
    if (-not $p) { return $null }
    $mods = @()
    try { $mods = $p.Modules | Where-Object { $_.ModuleName -like 'wetype*' } | ForEach-Object { $_.ModuleName } } catch { }
    $imebind = Get-Process ImeBind -ErrorAction SilentlyContinue
    return [pscustomobject]@{
        Pid     = $p.Id
        Wetype  = if ($mods) { ($mods -join ', ') } else { '(无 —— 微信输入法未被注入游戏进程)' }
        ImeBind = if ($imebind) { '运行中 pid=' + ($imebind.Id -join ',') } else { '未运行！' }
    }
}

if ($Wait) {
    "等待 dota2.exe 启动 ..."
    while (-not (Get-Process dota2 -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 2 }
    "检测到 dota2.exe，开始连续采样（共 12 次，每 5 秒一次）"
    for ($i = 1; $i -le 12; $i++) {
        $s = Sample
        if ($s) { "  [$i] pid=$($s.Pid)  ImeBind=$($s.ImeBind)  微信 TIP = $($s.Wetype)" }
        Start-Sleep -Seconds 5
    }
} else {
    $s = Sample
    if (-not $s) { "dota2.exe 未在运行。加 -Wait 参数可等它启动。" ; return }
    "dota2.exe pid=$($s.Pid)"
    "ImeBind     : $($s.ImeBind)"
    "微信 TIP    : $($s.Wetype)"
    "规则        : " + ((Get-Content (Join-Path $root 'rules.txt') -Encoding UTF8 | Where-Object { $_ -and -not $_.StartsWith('#') }) -join ' | ')
}

"`n--- imebind.log 里与 dota2 相关的记录 ---"
Get-Content $log -Encoding UTF8 | Select-String -Pattern 'dota2' | Select-Object -Last 8 | ForEach-Object { "  " + $_.Line }
