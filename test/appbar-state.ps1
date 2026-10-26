#Requires -Version 5.1
<#
  Prints the taskbar's actual auto-hide state, measured the same way the tool
  measures it: is the primary work area shorter than the monitor by a taskbar?

  "hidden" = auto-hide is ON, the taskbar reserves no space.
  "shown"  = auto-hide is OFF, the taskbar reserves its space.
  ASCII only (Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI).
#>
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AppBarState
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO
    { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFO mi);
    public static string Read()
    {
        MONITORINFO mi = new MONITORINFO();
        mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        GetMonitorInfo(MonitorFromWindow(IntPtr.Zero, 1), ref mi);
        int mon  = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
        int work = mi.rcWork.Bottom - mi.rcWork.Top;
        return (mon - work >= 8 ? "shown" : "hidden") + " (monitor " + mon + " / work " + work + ")";
    }
}
'@
[AppBarState]::Read()
