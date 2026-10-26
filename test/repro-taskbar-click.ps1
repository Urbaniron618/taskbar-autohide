#Requires -Version 5.1
<#
  Reproduces "opening an already-open document from the taskbar leaves the
  taskbar out" without asking anyone to click at the right moment: finds the
  app's taskbar button through UI Automation, moves the pointer onto it (which
  peeks an auto-hidden taskbar out), clicks, then watches what the taskbar and
  the tool do.

    .\test\repro-taskbar-click.ps1 -Button WPS
    .\test\repro-taskbar-click.ps1 -Button WPS -Behind WPS

  -Button   substring of the taskbar button's accessible name
  -Behind   class or title of the window that must be in front (activated and
            maximized first, through popup-repro)
  -Watch    how long to sample after the click, ms

  A hidden (auto-hidden) taskbar exposes no buttons to UI Automation, so the
  script peeks it out first, like a hand would.
  ASCII only (Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI).
#>
param(
    [string]$Button = 'WPS',
    [string]$Behind = '',
    [int]$Watch = 4000
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Mouse
{
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);   // LEFTDOWN
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);   // LEFTUP
    }
}
'@

$probe = Join-Path $PSScriptRoot 'appbar-probe.exe'
if (-not (Test-Path $probe)) { throw "missing: $probe (build it first)" }

function State { (((& $probe state) | Out-String) -replace '\s+', ' ').Trim() }

if ($Behind -ne '') {
    & (Join-Path $PSScriptRoot 'popup-repro.exe') focus $Behind 800 | Out-Null
    Start-Sleep -Milliseconds 600
}
'before: ' + (State)

# Park the pointer on the bottom edge (any huge y clamps there) so a hidden
# taskbar slides out; then its buttons exist for UI Automation.
[Mouse]::SetCursorPos(800, 20000) | Out-Null
Start-Sleep -Milliseconds 600
'peeked: ' + (State)

$root = [System.Windows.Automation.AutomationElement]::RootElement
$tray = $null
foreach ($e in $root.FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($e.Current.ClassName -eq 'Shell_TrayWnd') { $tray = $e }
}
if (-not $tray) { 'no taskbar in the UIA tree'; exit 1 }

$btn = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)
$hit = $null
foreach ($b in $tray.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btn)) {
    if ($b.Current.Name -like "*$Button*") { $hit = $b; break }
}
if (-not $hit) { "no taskbar button matching '$Button' (taskbar hidden?)"; exit 1 }

# UI Automation reports physical pixels, and SetCursorPos takes the same ones
# from this process (measured against the probe's reading), so no conversion.
$r = $hit.Current.BoundingRectangle
$x = [int]($r.Left + $r.Width / 2)
$y = [int]($r.Top + $r.Height / 2)
'button: ' + $hit.Current.Name
"click at: $x,$y"

[Mouse]::Click($x, $y)
'clicked: ' + (State)

$step = 250
for ($t = 0; $t -lt $Watch; $t += $step) {
    Start-Sleep -Milliseconds $step
    "+ {0,4} ms  {1}" -f ($t + $step), (State)
}

'-- pointer leaves the taskbar --'
[Mouse]::SetCursorPos(700, 300) | Out-Null
for ($t = 0; $t -lt 3000; $t += $step) {
    Start-Sleep -Milliseconds $step
    "+ {0,4} ms  {1}" -f ($t + $step), (State)
}
