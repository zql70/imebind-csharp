# 撤销 ImeBind 开机自启，并结束正在运行的实例
# 用法：pwsh -NoProfile -File uninstall-autostart.ps1
$ErrorActionPreference = 'Continue'
$startup = [Environment]::GetFolderPath('Startup')
$lnk = Join-Path $startup 'ImeBind.lnk'
if (Test-Path $lnk) { Remove-Item $lnk -Force; Write-Output ("已删除: " + $lnk) } else { Write-Output "启动项不存在" }

Get-Process ImeBind -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Process -Id $_.Id -Force
    Write-Output ("已结束进程 PID " + $_.Id)
}
Write-Output "完成。ImeBind.exe 本体仍在原目录，未被删除。"
