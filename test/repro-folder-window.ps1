#Requires -Version 5.1
<#
  v1.5's failure point, without Explorer: a plain window that is exactly the
  work area and never maximized - the shape Explorer gives a folder window it
  had maximized (showCmd=1, rcNormal = the work area, IsZoomed=false).

  The tool has to do both halves: hide the taskbar AND grow the window into the
  work area the taskbar vacated. v1.4 did the first and not the second, leaving
  a 48 px strip of desktop below a window that looked full screen.

    .\test\repro-folder-window.ps1

  Run it with the tool running; it does not start or stop it.

  ASCII only (Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI).
#>

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
    $lines = & $list -Count 0 -Match 'repro workarea'
    foreach ($l in $lines) {
        if ($l -match 'title="repro workarea') {
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

# The foreground has to be the repro window before the state means anything.
function WaitForForeground([int]$tries = 8) {
    for ($i = 0; $i -lt $tries; $i++) {
        if ((& $list -Count 1) -match 'title="repro workarea') { return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

$p = Start-Process -FilePath $repro -ArgumentList @('workarea', '6000') -PassThru
$fg = WaitForForeground

# The taskbar animation plus the tool's 520 ms verify pass: a second is enough.
$deadline = (Get-Date).AddSeconds(4)
$p1 = $null; $r1 = $null
while ((Get-Date) -lt $deadline) {
    $p1 = Probe
    $r1 = RepoRect
    if ($p1.Hidden -and $r1 -and ($r1.Top + $r1.H) -ge $p1.Monitor - 2) { break }
    Start-Sleep -Milliseconds 250
}

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 1500

if (-not $fg -or -not $r1) {
    'FAIL  the repro window never got the foreground or could not be measured'
    '      ' + $p1.Raw
    exit 1
}

$bottom = $r1.Top + $r1.H
$strip  = $p1.Monitor - $bottom
$ok = $p1.Hidden -and ($strip -le 2)

'{0}  taskbar={1}  window=[{2},{3} {4}x{5}]  monitor={6}  strip={7}px' -f `
    $(if ($ok) { 'PASS' } else { 'FAIL' }), `
    $(if ($p1.Hidden) { 'hidden' } else { 'shown' }), `
    $r1.Left, $r1.Top, $r1.W, $r1.H, $p1.Monitor, $strip
if ($ok) {
    'taskbar hidden and the window filled the work area it vacated'
    exit 0
}
'want: taskbar=hidden and strip<=2px (a window left behind shows ~48 px of desktop)'
exit 1
