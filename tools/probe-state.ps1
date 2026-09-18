# 独立验证 ImeBind 的效果
# 用 pwsh 7 运行：pwsh -NoProfile -File tools\probe-state.ps1
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $dir          # exe 和 imebind.log 在仓库根目录
$exe = Join-Path $root 'ImeBind.exe'
$log = Join-Path $root 'imebind.log'

Add-Type -TypeDefinition @"
using System;using System.Text;using System.Runtime.InteropServices;
public class W {
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int m);
 [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int m);
}
"@

function Show-Foreground {
    $h = [W]::GetForegroundWindow()
    $pid2 = 0
    [void][W]::GetWindowThreadProcessId($h, [ref]$pid2)
    $cls = New-Object Text.StringBuilder 256; [void][W]::GetClassName($h, $cls, 256)
    $title = New-Object Text.StringBuilder 256; [void][W]::GetWindowText($h, $title, 256)
    $exeName = ''
    try { $exeName = (Get-Process -Id $pid2 -ErrorAction Stop).ProcessName + '.exe' } catch { $exeName = '?' }
    "前台窗口: $exeName (pid=$pid2) class=$($cls.ToString()) title=$($title.ToString())"
}

"=== 1) 进程状态 ==="
foreach ($n in 'dota2', 'ImeBind') {
    $p = Get-Process $n -ErrorAction SilentlyContinue
    if ($p) { "  $n 正在运行  PID=" + ($p.Id -join ',') } else { "  $n 未运行" }
}
Show-Foreground

"=== 2) 本进程读到的激活输入法（仅供对照）==="
"  注意：TSF 的激活输入法是每线程状态，这里读到的是本进程自己的，读不到前台进程用的是哪个输入法。"
"  微软拼音   = 0804:{81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E}{FA550B04-5AD7-411F-A5AC-CA038EC515D7}"
"  微信输入法 = 0804:{86598FB9-66A2-463E-B9C2-AEB906D477AD}{607FDF85-FCC8-4DBD-A365-41296F980C9C}"
& $exe --probe
Start-Sleep -Milliseconds 400
Get-Content $log -Tail 2 -Encoding UTF8 | ForEach-Object { "  " + $_ }

"=== 3) 前台进程里加载了哪个输入法的 TIP DLL ==="
$h = [W]::GetForegroundWindow()
$fpid = 0
[void][W]::GetWindowThreadProcessId($h, [ref]$fpid)
if ($fpid -ne 0) {
    try {
        $mods = (Get-Process -Id $fpid).Modules | Where-Object { $_.ModuleName -match 'wetype|ChsIME|TextInputHost|msctf' } |
                Select-Object ModuleName, FileName
        if ($mods) { $mods | ForEach-Object { "  " + $_.ModuleName + "   " + $_.FileName } }
        else { "  前台进程里没有 wetype/IME 相关模块" }
    } catch { "  无法枚举前台进程模块: " + $_.Exception.Message }
}

"=== 4) ImeBind 日志尾部（规则应用记录）==="
Get-Content $log -Tail 18 -Encoding UTF8 | ForEach-Object { "  " + $_ }
