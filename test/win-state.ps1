#Requires -Version 5.1
<#
  Reports one window (by title substring or hwnd) plus the tray/work-area facts
  taskbar-autohide judges on, and can maximize/restore it by force.

    .\test\win-state.ps1 -Title 微信
    .\test\win-state.ps1 -Title 微信 -Maximize
    .\test\win-state.ps1 -Title 微信 -Restore

  ASCII only (Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI).
#>
param(
    [string]$Title = '',
    [string]$Class = '',
    [Int64]$Hwnd = 0,
    [switch]$Maximize,
    [switch]$Restore
)

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
public static class WinState
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO
    { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string c, string n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern int GetWindowLongW(IntPtr h, int i);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public static string Cls(IntPtr h) { StringBuilder sb = new StringBuilder(256); GetClassName(h, sb, sb.Capacity); return sb.ToString(); }
    public static string Txt(IntPtr h) { StringBuilder sb = new StringBuilder(512); GetWindowText(h, sb, sb.Capacity); return sb.ToString(); }

    public static IntPtr Find(string title, string cls, long hwnd)
    {
        if (hwnd != 0) return new IntPtr(hwnd);
        IntPtr h = GetTopWindow(IntPtr.Zero);
        int guard = 0;
        while (h != IntPtr.Zero && guard++ < 4000)
        {
            bool okT = title.Length == 0 || Txt(h).IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0;
            bool okC = cls.Length == 0 || Cls(h).IndexOf(cls, StringComparison.OrdinalIgnoreCase) >= 0;
            if (okT && okC && (title.Length > 0 || cls.Length > 0)) return h;
            h = GetWindow(h, 2);
        }
        return IntPtr.Zero;
    }

    public static string Describe(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(not found)";
        RECT r; GetWindowRect(h, out r);
        int ex = GetWindowLongW(h, -20);
        int st = GetWindowLongW(h, -16);
        uint pid; GetWindowThreadProcessId(h, out pid);
        MONITORINFO mi = new MONITORINFO(); mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        bool haveMon = GetMonitorInfo(MonitorFromWindow(h, 2), ref mi);
        bool full = haveMon
            && r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top
            && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        return Cls(h) + "  pid=" + pid
            + "  vis=" + (IsWindowVisible(h) ? "1" : "0")
            + "  ico=" + (IsIconic(h) ? "1" : "0") + "  zoom=" + (IsZoomed(h) ? "1" : "0")
            + "  full=" + (full ? "1" : "0")
            + "\n  rect=[" + r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) + "]"
            + "  style=0x" + st.ToString("X8") + "  ex=0x" + ex.ToString("X8")
            + (haveMon ? "\n  monitor=[" + mi.rcMonitor.Left + "," + mi.rcMonitor.Top + " "
                + (mi.rcMonitor.Right - mi.rcMonitor.Left) + "x" + (mi.rcMonitor.Bottom - mi.rcMonitor.Top) + "]"
                + "  work=[" + mi.rcWork.Left + "," + mi.rcWork.Top + " "
                + (mi.rcWork.Right - mi.rcWork.Left) + "x" + (mi.rcWork.Bottom - mi.rcWork.Top) + "]" : "")
            + "\n  hwnd=" + h + "  fg=" + (h == GetForegroundWindow() ? "1" : "0")
            + "  title=\"" + Txt(h) + "\"";
    }

    public static string Tray()
    {
        IntPtr t = FindWindow("Shell_TrayWnd", null);
        RECT r; GetWindowRect(t, out r);
        MONITORINFO mi = new MONITORINFO(); mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        GetMonitorInfo(MonitorFromWindow(IntPtr.Zero, 1), ref mi);
        int visible = Math.Min(r.Bottom, mi.rcMonitor.Bottom) - Math.Max(r.Top, mi.rcMonitor.Top);
        return "tray rect=[" + r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) + "]"
            + " visiblePx=" + (visible > 0 ? visible : 0)
            + " workH=" + (mi.rcWork.Bottom - mi.rcWork.Top)
            + " monitorH=" + (mi.rcMonitor.Bottom - mi.rcMonitor.Top);
    }
}
'@ -ErrorAction Stop

$h = [WinState]::Find($Title, $Class, $Hwnd)
if ($Maximize) { [void][WinState]::ShowWindow($h, 3) }   # SW_MAXIMIZE
if ($Restore) { [void][WinState]::ShowWindow($h, 9) }    # SW_RESTORE
if ($Maximize -or $Restore) { Start-Sleep -Milliseconds 900 }

[WinState]::Describe($h)
[WinState]::Tray()
"foreground = " + [WinState]::Describe([WinState]::GetForegroundWindow())
