#Requires -Version 5.1
<#
  Installs (or removes) taskbar-autohide.

  Install : copy the exe to -InstallDir, start it, add a Startup shortcut so it
            runs at logon.
  Remove  : stop the process, delete the Startup shortcut, turn the OS auto-hide
            setting back off, delete the copied exe.

  ASCII only (Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI).
#>
param(
    [switch]$Uninstall,
    [string]$InstallDir = (Join-Path $env:USERPROFILE 'claude-scripts')
)

$ErrorActionPreference = 'Stop'

$exe     = Join-Path $InstallDir 'taskbar-autohide.exe'
$startup = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\taskbar-autohide.lnk'

if ($Uninstall) {
    Get-Process taskbar-autohide -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 400
    if (Test-Path $exe) { & $exe --off }
    Remove-Item $startup -Force -ErrorAction SilentlyContinue
    Remove-Item $exe -Force -ErrorAction SilentlyContinue
    Write-Host 'Uninstalled: process stopped, autostart removed, auto-hide restored to OFF.'
    return
}

$source = Join-Path $PSScriptRoot 'taskbar-autohide.exe'
if (-not (Test-Path $source)) { throw "taskbar-autohide.exe missing next to this script - run .\build.ps1 first" }

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Get-Process taskbar-autohide -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
Copy-Item $source $exe -Force
Start-Process -FilePath $exe

$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut($startup)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $InstallDir
$lnk.Description = 'Auto-hide the taskbar while a window is maximized'
$lnk.Save()

Write-Host "Installed : $exe (running)"
Write-Host "Autostart : $startup"
Write-Host 'To remove: .\install.ps1 -Uninstall'
