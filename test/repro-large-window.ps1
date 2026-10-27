#Requires -Version 5.1
<#
  v1.4's two failure points, without Steam or the bilibili client.

  The tool has to hide the taskbar for a foreground window that fills the
  screen even when the window never called ShowWindow(SW_MAXIMIZE):

    borderless  borderless, monitor minus 1 px     -> hidden  (Steam Big Picture)
    large       plain window, 90% x 99% of work    -> hidden  (bilibili player)
    small       plain window, 50% x 50%            -> shown   (control)

  Each case opens a window (popup-repro.exe), waits until that window really
  owns the foreground, prints what the taskbar did, and closes it again. Run it
  with the tool running; it does not start or stop it.

    .\test\repro-large-window.ps1              # all three
    .\test\repro-large-window.ps1 -Mode large  # one

  ASCII only (Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI).
#>
param(
    [ValidateSet('all', 'borderless', 'large', 'small')]
    [string]$Mode = 'all'
)

$ErrorActionPreference = 'Stop'
$here  = Split-Path -Parent $MyInvocation.MyCommand.Path
$repro = Join-Path $here 'popup-repro.exe'
$probe = Join-Path $here 'appbar-probe.exe'
$list  = Join-Path $here 'window-list.ps1'
if (-not (Test-Path $repro)) { throw "popup-repro.exe missing - compile it first (see README, Tests)" }
if (-not (Test-Path $probe)) { throw "appbar-probe.exe missing - compile it first (see README, Tests)" }

if (-not (Get-Process taskbar-autohide -ErrorAction SilentlyContinue)) {
    throw "taskbar-autohide is not running - start it first"
}

function TaskbarState {
    $out = & $probe state
    if ($out -match 'autohide=1') { return 'hidden' }
    if ($out -match 'autohide=0') { return 'shown' }
    return 'unknown'
}

# The foreground has to be the repro window before the state means anything:
# another app can steal it while the case settles, and then the taskbar is
# legitimately judging that app instead.
function WaitForForeground([string]$name, [int]$tries = 8) {
    for ($i = 0; $i -lt $tries; $i++) {
        $line = & $list -Count 1 | Select-Object -First 1
        if ($line -match ('title="repro ' + $name)) { return $line }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

$cases = @(
    @{ Mode = 'borderless'; Want = 'hidden'; Why = 'borderless, monitor minus 1 px, never maximized (Steam Big Picture)' },
    @{ Mode = 'large';      Want = 'hidden'; Why = 'plain window at 90% x 99% of the work area (bilibili player)' },
    @{ Mode = 'small';      Want = 'shown';  Why = 'plain window at 50% x 50% (control)' }
)

$fail = 0
foreach ($c in $cases) {
    if ($Mode -ne 'all' -and $Mode -ne $c.Mode) { continue }

    $p = Start-Process -FilePath $repro -ArgumentList @($c.Mode, '6000') -PassThru
    $fg = WaitForForeground $c.Mode
    Start-Sleep -Milliseconds 900
    $got = TaskbarState

    # Close the window and let the tool settle before the next case.
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 1500

    if (-not $fg) {
        $fail++
        'FAIL  {0,-10} taskbar={1,-6} want={2,-6}  the repro window never got the foreground - {3}' -f $c.Mode, $got, $c.Want, $c.Why
        continue
    }

    $ok = ($got -eq $c.Want)
    if (-not $ok) { $fail++ }
    $verdict = if ($ok) { 'PASS' } else { 'FAIL'; }
    '{0}  {1,-10} taskbar={2,-6} want={3,-6}  {4}' -f $verdict, $c.Mode, $got, $c.Want, $c.Why
}

if ($fail -eq 0) { 'all cases pass' } else { "$fail case(s) failed" }
exit $fail
