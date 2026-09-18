# 在启动文件夹创建快捷方式，让 ImeBind 开机自启
# 用法：pwsh -NoProfile -File scripts\install-autostart.ps1
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $dir          # exe 在仓库根目录
$exe = Join-Path $root 'ImeBind.exe'
if (-not (Test-Path $exe)) { throw "找不到 $exe，先运行 tools\build.ps1" }

$startup = [Environment]::GetFolderPath('Startup')
$lnk = Join-Path $startup 'ImeBind.lnk'

$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($lnk)
$sc.TargetPath = $exe
$sc.WorkingDirectory = $root
$sc.WindowStyle = 7          # 最小化；程序本身是 winexe，不会显示窗口
$sc.Description = '按前台程序自动切换输入法'
$sc.Save()

Write-Output ("已创建: " + $lnk)
Write-Output ("下次登录自动启动。立即启动： Start-Process '" + $exe + "'")
