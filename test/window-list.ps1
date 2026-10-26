#Requires -Version 5.1
<#
  Lists top-level windows in z-order with the fields taskbar-autohide judges on,
  so a decision can be traced against what the desktop actually looks like.

    .\test\window-list.ps1            # z-order, top 25
    .\test\window-list.ps1 -Match wps # only titles/classes containing "wps"

  ASCII only (Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI).
#>
param(
    [int]$Count = 25,
    [string]$Match = '',
    [switch]$All
)

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class WinList
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO
    { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLongW(IntPtr h, int i);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int s);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFO mi);

    public static string Class(IntPtr h) { StringBuilder sb = new StringBuilder(256); GetClassName(h, sb, sb.Capacity); return sb.ToString(); }

    public static string Dump(int count)
    {
        StringBuilder o = new StringBuilder();
        IntPtr fg = GetForegroundWindow();
        o.AppendLine("foreground = " + Describe(fg));
        IntPtr h = GetTopWindow(IntPtr.Zero);
        int i = 0, shown = 0;
        while (h != IntPtr.Zero && shown < count && i < 4000)
        {
            string line = Describe(h);
            if (line != null) { o.AppendLine("  " + line); shown++; }
            h = GetWindow(h, 2);
            i++;
        }
        return o.ToString();
    }

    public static string Describe(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(none)";
        StringBuilder t = new StringBuilder(256);
        GetWindowText(h, t, t.Capacity);
        RECT r; GetWindowRect(h, out r);
        int ex = GetWindowLongW(h, -20);
        int cloaked = 0;
        if (DwmGetWindowAttribute(h, 14, out cloaked, sizeof(int)) != 0) cloaked = -1;
        uint pid; GetWindowThreadProcessId(h, out pid);
        MONITORINFO mi = new MONITORINFO(); mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        bool full = false;
        if (GetMonitorInfo(MonitorFromWindow(h, 2), ref mi))
            full = r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top
                && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;

        return Class(h) + "  vis=" + (IsWindowVisible(h) ? "1" : "0")
            + " ico=" + (IsIconic(h) ? "1" : "0") + " zoom=" + (IsZoomed(h) ? "1" : "0")
            + " full=" + (full ? "1" : "0") + " cloak=" + cloaked
            + " tool=" + ((ex & 0x80) != 0 ? "1" : "0")
            + " noact=" + ((ex & 0x08000000) != 0 ? "1" : "0")
            + " pid=" + pid
            + " [" + r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) + "]"
            + " hwnd=" + h
            + "  title=\"" + t.ToString() + "\"";
    }
}
'@ -ErrorAction Stop

$dump = [WinList]::Dump(400)
$lines = $dump -split "`r?`n"
if ($All) { $lines } else { $lines | Where-Object { $_ -ne '' } | Select-Object -First ($Count + 1) }
if ($Match -ne '') {
    ''
    "--- matching '$Match' ---"
    $lines | Where-Object { $_ -match $Match }
}
