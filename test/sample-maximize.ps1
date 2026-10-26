#Requires -Version 5.1
<#
  Samples a window + the tray every 120 ms while an action is performed, so the
  resize the shell does when the work area changes can be seen frame by frame.

    .\test\sample-maximize.ps1 -Title 微信 -Action max
    .\test\sample-maximize.ps1 -Title 微信 -Action hide
    .\test\sample-maximize.ps1 -Title 微信 -Action show

  -Action max   : foreground the window, then WM_SYSCOMMAND SC_MAXIMIZE
  -Action restore / hide / show : as named (hide/show = appbar auto-hide on/off)

  ASCII only (Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI).
#>
param(
    [string]$Title = '',
    [Int64]$Hwnd = 0,
    [ValidateSet('max', 'restore', 'hide', 'show', 'sysmax', 'setpos', 'remax', 'none')]
    [string]$Action = 'none',
    [int]$Samples = 30,
    [int]$IntervalMs = 120
)

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Sampler
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO
    { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] public struct APPBARDATA
    { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(int msg, ref APPBARDATA d);

    public static string Cls(IntPtr h) { StringBuilder sb = new StringBuilder(256); GetClassName(h, sb, sb.Capacity); return sb.ToString(); }
    public static string Txt(IntPtr h) { StringBuilder sb = new StringBuilder(512); GetWindowText(h, sb, sb.Capacity); return sb.ToString(); }

    public static IntPtr Find(string title, long hwnd)
    {
        if (hwnd != 0) return new IntPtr(hwnd);
        IntPtr h = GetTopWindow(IntPtr.Zero);
        int guard = 0;
        while (h != IntPtr.Zero && guard++ < 4000)
        {
            if (Txt(h).IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0 && IsWindowVisible(h)) return h;
            h = GetWindow(h, 2);
        }
        return IntPtr.Zero;
    }

    public static void AutoHide(bool on)
    {
        APPBARDATA d = new APPBARDATA();
        d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
        d.hWnd = FindWindow("Shell_TrayWnd", null);
        d.lParam = (IntPtr)(on ? 3 : 2);
        SHAppBarMessage(10, ref d);
    }

    // Force the window onto the whole work area of its monitor, maximized flag kept.
    public static string FillWorkArea(IntPtr h)
    {
        MONITORINFO mi = new MONITORINFO(); mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        GetMonitorInfo(MonitorFromWindow(h, 2), ref mi);
        int w = mi.rcWork.Right - mi.rcWork.Left, hh = mi.rcWork.Bottom - mi.rcWork.Top;
        bool ok = SetWindowPos(h, IntPtr.Zero, mi.rcWork.Left, mi.rcWork.Top, w, hh, 0x0004 | 0x0010);
        return "setpos " + (ok ? "ok" : "fail") + " -> " + w + "x" + hh;
    }

    // "rect=[x,y wxh] zoom=0 workH=1019 trayPx=48 fg=<class>"
    public static string Sample(IntPtr h)
    {
        RECT r; GetWindowRect(h, out r);
        MONITORINFO mi = new MONITORINFO(); mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        GetMonitorInfo(MonitorFromWindow(IntPtr.Zero, 1), ref mi);
        IntPtr t = FindWindow("Shell_TrayWnd", null);
        RECT tr; GetWindowRect(t, out tr);
        int vis = Math.Min(tr.Bottom, mi.rcMonitor.Bottom) - Math.Max(tr.Top, mi.rcMonitor.Top);
        if (vis < 0) vis = 0;
        return "rect=[" + r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) + "]"
            + " zoom=" + (IsZoomed(h) ? 1 : 0)
            + " work=" + (mi.rcWork.Right - mi.rcWork.Left) + "x" + (mi.rcWork.Bottom - mi.rcWork.Top)
            + " trayPx=" + vis
            + " fg=" + Cls(GetForegroundWindow());
    }
}
'@ -ErrorAction Stop

$h = [Sampler]::Find($Title, $Hwnd)
if ($h -eq [IntPtr]::Zero) { "window not found: $Title"; exit 1 }

'target = ' + [Sampler]::Cls($h) + ' [' + [Sampler]::Txt($h) + ']'
$sw = [Diagnostics.Stopwatch]::StartNew()
$done = $false
for ($i = 0; $i -lt $Samples; $i++) {
    if (-not $done -and $sw.ElapsedMilliseconds -ge 250) {
        $done = $true
        switch ($Action) {
            'max' {
                [void][Sampler]::SetForegroundWindow($h)
                Start-Sleep -Milliseconds 60
                [void][Sampler]::SendMessage($h, 0x0112, [IntPtr]0xF030, [IntPtr]::Zero)  # WM_SYSCOMMAND SC_MAXIMIZE
            }
            'restore' { [void][Sampler]::ShowWindow($h, 9) }
            'hide'    { [Sampler]::AutoHide($true) }
            'show'    { [Sampler]::AutoHide($false) }
            'sysmax'  { [void][Sampler]::SendMessage($h, 0x0112, [IntPtr]0xF030, [IntPtr]::Zero) }
            'setpos'  { [void][Sampler]::FillWorkArea($h) }
            'remax'   { [void][Sampler]::ShowWindow($h, 9); Start-Sleep -Milliseconds 120
                        [void][Sampler]::SendMessage($h, 0x0112, [IntPtr]0xF030, [IntPtr]::Zero) }
        }
        "  <<-- action: $Action"
    }
    ('{0,5} ms  {1}' -f $sw.ElapsedMilliseconds, [Sampler]::Sample($h))
    Start-Sleep -Milliseconds $IntervalMs
}
