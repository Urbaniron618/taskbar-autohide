#Requires -Version 5.1
<#
  v1.5.6's failure point: the fill was capped at four attempts per window, and
  the count was only reset when a *different* window got filled. After four
  maximizes of the same window (WeChat, an Explorer folder window) the tool
  stopped filling it for good - every later maximize left the 48 px strip of
  desktop below it until the tool was restarted.

  This maximizes and restores the same window again and again, with the taskbar
  shown in between, and requires the window to fill the work area every time.
  The window is popup-repro's "frame" window, so no WeChat is needed.

    .\test\repeat-maximize.ps1            # six cycles (v1.5.6 used to fail #5)
    .\test\repeat-maximize.ps1 -Cycles 10

  Run it with the tool running; it does not start or stop it.

  ASCII only (Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI).
#>
param(
    [int]$Cycles = 6,
    [int]$SettleMs = 2500
)

$ErrorActionPreference = 'Stop'
$here  = Split-Path -Parent $MyInvocation.MyCommand.Path
$repro = Join-Path $here 'popup-repro.exe'
$probe = Join-Path $here 'appbar-probe.exe'
$list  = Join-Path $here 'window-list.ps1'
foreach ($f in @($repro, $probe, $list)) {
    if (-not (Test-Path $f)) { throw "$f missing - see README, Tests" }
}
if (-not (Get-Process taskbar-autohide -ErrorAction SilentlyContinue)) {
    throw "taskbar-autohide is not running - start it first"
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Cycle
{
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after,
        int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool alt);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
}
'@ -ErrorAction Stop

# Taskbar state and monitor height, as the tool reads them.
function Probe {
    $out = & $probe state
    $hidden = $out -match 'autohide=1'
    $mon = 0
    if ($out -match 'monitor=(\d+)') { $mon = [int]$Matches[1] }
    [pscustomobject]@{ Hidden = $hidden; Monitor = $mon; Raw = $out }
}

# The repro window's rectangle, from the same listing used to trace decisions.
function RepoRect {
    $lines = & $list -Count 0 -Match 'repro frame'
    foreach ($l in $lines) {
        if ($l -match 'title="repro frame') {
            if ($l -match '\[(-?\d+),(-?\d+) (\d+)x(\d+)\]') {
                return [pscustomobject]@{
                    Left = [int]$Matches[1]; Top = [int]$Matches[2]
                    W = [int]$Matches[3]; H = [int]$Matches[4]
                }
            }
        }
    }
    return $null
}

function FindRepro {
    foreach ($l in (& $list -Count 0 -Match 'repro frame')) {
        if ($l -match 'title="repro frame"' -and $l -match 'hwnd=(\d+)') { return [IntPtr][int64]$Matches[1] }
    }
    return [IntPtr]::Zero
}

$p = Start-Process -FilePath $repro -ArgumentList @('frame', '60000') -PassThru
$deadline = (Get-Date).AddSeconds(6)
$h = [IntPtr]::Zero
while ((Get-Date) -lt $deadline -and $h -eq [IntPtr]::Zero) {
    Start-Sleep -Milliseconds 200
    $h = FindRepro
}
if ($h -eq [IntPtr]::Zero) {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    throw "the repro window never appeared"
}

$fails = 0
for ($c = 1; $c -le $Cycles; $c++) {
    # A plain window in front: the taskbar belongs on screen. SW_RESTORE is
    # posted to the app's own message loop, so wait until it really is a plain
    # window before sizing it - a SetWindowPos on a still-zoomed window leaves
    # it "maximized", and the tool then puts the taskbar away again.
    $plain = $false
    for ($try = 0; $try -lt 4 -and -not $plain; $try++) {
        [void][Cycle]::ShowWindow($h, 9)                               # SW_RESTORE
        Start-Sleep -Milliseconds 250
        [void][Cycle]::SetWindowPos($h, [IntPtr]::Zero, 210, 210, 900, 700, 0x0004 -bor 0x0010)
        Start-Sleep -Milliseconds 250
        $plain = -not [Cycle]::IsZoomed($h)
    }
    [void][Cycle]::SwitchToThisWindow($h, $true)

    # Wait for the taskbar to come back out (the tool turns auto-hide off for
    # a plain window; a parked tray costs one off/on round trip first).
    $deadline = (Get-Date).AddSeconds(6)
    $shown = $false
    while ((Get-Date) -lt $deadline) {
        if (-not (Probe).Hidden) { $shown = $true; break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $plain -or -not $shown) {
        'FAIL  cycle {0}: the taskbar never came back out before the maximize' -f $c
        $fails++
        continue
    }

    # Maximize it the way the title-bar button does, then let the switch settle.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    [void][Cycle]::SendMessage($h, 0x0112, [IntPtr]0xF030, [IntPtr]::Zero)   # SC_MAXIMIZE
    Start-Sleep -Milliseconds $SettleMs

    $pr = Probe
    $r = RepoRect
    if (-not $r) { 'FAIL  cycle {0}: the repro window disappeared' -f $c; $fails++; continue }
    $strip = $pr.Monitor - ($r.Top + $r.H)
    $ok = $pr.Hidden -and ($strip -le 2)
    '{0}  cycle {1}  {2,5} ms  taskbar={3}  window=[{4},{5} {6}x{7}]  strip={8}px' -f `
        $(if ($ok) { 'PASS' } else { 'FAIL' }), $c, $sw.ElapsedMilliseconds, `
        $(if ($pr.Hidden) { 'hidden' } else { 'shown' }), `
        $r.Left, $r.Top, $r.W, $r.H, $strip
    if (-not $ok) { $fails++ }
}

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 1200

if ($fails -eq 0) {
    'all cycles pass: the window filled the work area every time'
    exit 0
}
'want: taskbar=hidden and strip<=2px on every cycle (v1.5.6 stopped filling after four)'
exit 1
