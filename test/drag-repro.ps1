#Requires -Version 5.1
<#
  v1.5.3's failure point: the taskbar pops in and out while a window is dragged.

  A window under the mouse crosses the "covers 85% of the work area" line in
  both directions within a second. Judged while it is being dragged, the
  taskbar came out when the window got small and went away when it got big -
  under the mouse - and the v1.5.2 pre-fill could even move the window itself.

  This script resizes Notepad with the mouse, from a small window to one that
  covers most of the work area, and checks:

    * while the button is down the taskbar stays on screen (never hidden),
    * when it is released the taskbar goes away, because the window ended up
      covering the work area (the ordinary rule, unchanged).

  It moves the real mouse for about 5 seconds and closes Notepad afterwards.
  Run it with the tool running; it does not start or stop it.

    .\test\drag-repro.ps1

  ASCII only (Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI).
#>

$ErrorActionPreference = 'Stop'
$here  = Split-Path -Parent $MyInvocation.MyCommand.Path
$probe = Join-Path $here 'appbar-probe.exe'
if (-not (Test-Path $probe)) { throw "$probe missing - compile it first (see README, Tests)" }
if (-not (Get-Process taskbar-autohide -ErrorAction SilentlyContinue)) {
    throw "taskbar-autohide is not running - start it first"
}

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Drag
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after,
        int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);

    public static IntPtr FindClass(string cls)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            if (sb.ToString() == cls && IsWindowVisible(h)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

}
'@ -ErrorAction Stop

function Taskbar {
    $out = & $probe state
    if ($out -match 'autohide=1') { return 'hidden' }
    if ($out -match 'autohide=0') { return 'shown' }
    return 'unknown'
}

function Rect([IntPtr]$h) {
    $r = New-Object Drag+RECT
    [void][Drag]::GetWindowRect($h, [ref]$r)
    return [pscustomobject]@{ Left = $r.Left; Top = $r.Top; W = $r.Right - $r.Left; H = $r.Bottom - $r.Top }
}

Start-Process notepad.exe | Out-Null
Start-Sleep -Milliseconds 1200
$h = [Drag]::FindClass('Notepad')
if ($h -eq [IntPtr]::Zero) { Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force; throw "notepad not found" }

# Known starting size, so the drag has something to grow from.
[void][Drag]::ShowWindow($h, 9)                                    # SW_RESTORE
[void][Drag]::SetWindowPos($h, [IntPtr]::Zero, 80, 80, 700, 500, 0x0004 -bor 0x0010)
Start-Sleep -Milliseconds 700
$r0 = Rect $h
"notepad [" + $r0.Left + "," + $r0.Top + " " + $r0.W + "x" + $r0.H + "]  taskbar=" + (Taskbar)

$fail = 0

# Drag the bottom-right corner out towards the screen corner.
[Drag]::SetCursorPos($r0.Left + $r0.W - 3, $r0.Top + $r0.H - 3) | Out-Null
Start-Sleep -Milliseconds 150
[Drag]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)                # LEFTDOWN
Start-Sleep -Milliseconds 150

$tx = 1650; $ty = 980
$sx = $r0.Left + $r0.W - 3; $sy = $r0.Top + $r0.H - 3
for ($i = 1; $i -le 24; $i++) {
    [Drag]::SetCursorPos([int]($sx + ($tx - $sx) * $i / 24), [int]($sy + ($ty - $sy) * $i / 24)) | Out-Null
    Start-Sleep -Milliseconds 50
}

$during = @()
for ($i = 0; $i -lt 5; $i++) {
    $during += Taskbar
    Start-Sleep -Milliseconds 200
}
$r1 = Rect $h
"while dragging: window=" + $r1.W + "x" + $r1.H + "  taskbar samples=" + ($during -join ',')

[Drag]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)                # LEFTUP

# The window is big now, so the ordinary rule wants the taskbar gone.
$after = 'shown'
for ($i = 0; $i -lt 12; $i++) {
    Start-Sleep -Milliseconds 250
    $after = Taskbar
    if ($after -eq 'hidden') { break }
}
"after release: window=" + (Rect $h).W + "x" + (Rect $h).H + "  taskbar=" + $after

[Drag]::SetCursorPos(900, 600) | Out-Null
Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force

if ($during -contains 'hidden') {
    $fail++
    'FAIL  the taskbar went away while the window was being dragged'
} else {
    'PASS  the taskbar stayed on screen for the whole drag'
}
if ($after -ne 'hidden') {
    $fail++
    'FAIL  the taskbar did not go away after the window was put down covering the work area'
} else {
    'PASS  released over the work area: taskbar hidden again'
}

if ($fail -eq 0) { 'all cases pass' } else { "$fail case(s) failed" }
exit $fail
