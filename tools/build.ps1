# 编译 ImeBind.exe（.NET Framework 4.x，C# 5）
# 用 pwsh 7 运行：pwsh -NoProfile -File tools\build.ps1
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $dir          # 仓库根目录：exe / icon.ico / rules.txt 都放这里
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "找不到 csc.exe: $csc" }

$src = Join-Path $root 'src\ImeBind.cs'
$out = Join-Path $root 'ImeBind.exe'
$icon = Join-Path $root 'icon.ico'

# /codepage:65001 必须加：csc 对无 BOM 的源文件按 ANSI(GBK) 解码，中文注释和字符串会乱掉。
# icon.ico 存在时作为 exe 图标编进去（资源管理器/托盘都用它）；用 make-icon.ps1 可重新生成。
# 托盘交互用到 WinForms（NotifyIcon + ContextMenuStrip），所以要多引用两个程序集。
$cscArgs = @('/nologo', '/target:winexe', '/codepage:65001',
             '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
             "/out:$out")
if (Test-Path $icon) { $cscArgs += "/win32icon:$icon" }
else { Write-Output '（未找到 icon.ico，跳过图标；可用 make-icon.ps1 生成）' }
$cscArgs += $src
& $csc @cscArgs

if (Test-Path $out) {
    Get-Item $out | Select-Object Name, Length, LastWriteTime | Format-List | Out-String | Write-Output
} else {
    throw "编译失败，未生成 $out"
}
