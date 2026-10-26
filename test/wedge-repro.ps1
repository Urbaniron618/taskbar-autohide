#Requires -Version 5.1
<#
  Regression test for the v1.2 wedge fix.

  A "wedge" is the tray window parked on screen while the appbar state says
  auto-hidden: the work area is full height, so v1.1 believed there was nothing
  to do and the user kept looking at a taskbar that never went away. It is made
  by flipping auto-hide ON while the pointer sits on the taskbar - which is
  exactly what clicking a taskbar button does.

  The script brings its own maximized window in front (so the result does not
  depend on what the desktop happens to be showing), makes the wedge by hand
  with the tool stopped, then starts the built exe and expects the tray to be
  tucked under the bottom edge within a few seconds.

    .\test\wedge-repro.ps1

  Uses the exe next to this script's parent folder (build it first) and leaves
  it running. ASCII only (Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI).
#>
param(
    [int]$SettleMs = 5000
)

$ErrorActionPreference = 'Stop'

$exe   = Join-Path (Split-Path $PSScriptRoot -Parent) 'taskbar-autohide.exe'
$repro = Join-Path $PSScriptRoot 'popup-repro.exe'
$probe = Join-Path $PSScriptRoot 'appbar-probe.exe'
foreach ($f in @($exe, $repro, $probe)) { if (-not (Test-Path $f)) { throw "missing: $f" } }

Add-Type -AssemblyName System.Windows.Forms

# One probe reading, resolved to the facts that matter.
function Read-State {
    $s = ((& $probe state) | Out-String) -replace '\s+', ' '
    [pscustomobject]@{
        Raw      = $s.Trim()
        AutoHide = $s -match 'autohide=1'
        Monitor  = [int]($s -replace '.*monitor=?(\d+).*', '$1')
        Work     = [int]($s -replace '.*work=?(\d+).*', '$1')
        TrayTop  = [int]($s -replace '.*tray=\[\d+,(-?\d+).*', '$1')
        Maxed    = $s -match 'fg=\S+\[zoomed\]'
    }
}
# Auto-hide on, no space reserved, tray on screen: the tray is on screen.
function Test-Wedged($st) { $st.AutoHide -and ($st.Work -eq $st.Monitor) -and ($st.TrayTop -lt $st.Monitor - 4) }
# Auto-hide on, no space reserved, tray tucked under the bottom edge.
function Test-Tucked($st) { $st.AutoHide -and ($st.Work -eq $st.Monitor) -and ($st.TrayTop -ge $st.Monitor - 4) }

Get-Process taskbar-autohide -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$frame = $null
try {
    # A maximized window of our own takes the foreground, so the tool wants
    # auto-hide ON for the whole test.
    $frame = Start-Process $repro -ArgumentList 'frame', '20000' -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 1200
    $st = Read-State
    if (-not $st.Maxed) { 'SKIP - the maximized frame did not take the foreground: ' + $st.Raw; exit 1 }

    & $probe off | Out-Null
    Start-Sleep -Milliseconds 500
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point(800, 1042)
    Start-Sleep -Milliseconds 300
    & $probe on | Out-Null
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point(800, 400)
    Start-Sleep -Milliseconds 1200

    $st = Read-State
    "wedged    : " + $st.Raw
    if (-not (Test-Wedged $st)) {
        'SKIP - this machine did not reproduce the wedge (the tray hid by itself)'
        exit 1
    }

    Start-Process -FilePath $exe
    Start-Sleep -Milliseconds $SettleMs
    $st = Read-State
    "recovered : " + $st.Raw
    if (Test-Tucked $st) {
        'PASS - the tool saw the parked tray and moved it back'
        exit 0
    }
    'FAIL - the tray is still on screen (or auto-hide got turned off instead)'
    exit 2
}
finally {
    if ($frame -and -not $frame.HasExited) { Stop-Process -Id $frame.Id -Force -ErrorAction SilentlyContinue }
}
