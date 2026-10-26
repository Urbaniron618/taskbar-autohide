// test/appbar-probe.cs
//
// Measures the taskbar from every angle the tool could use, so a "the taskbar
// will not auto-hide" report can be pinned to a mechanism instead of a guess.
//
//   appbar-probe.exe state           configured autohide, work area, tray rect
//   appbar-probe.exe on | off        set autohide through ABM_SETSTATE
//   appbar-probe.exe peek <ms>       move the pointer to the bottom edge, watch
//                                    the tray rect, then put the pointer back
//   appbar-probe.exe watch <ms>      print the tray rect every 100 ms
//
// Build:
//   csc /nologo /target:exe /out:test\appbar-probe.exe test\appbar-probe.cs
//
// ASCII only.

using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class Probe
{
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(int msg, ref APPBARDATA d);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string cls, string name);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);

    static void Main(string[] a)
    {
        string mode = a.Length > 0 ? a[0] : "state";
        if (mode == "on") { Set(true); return; }
        if (mode == "off") { Set(false); return; }
        if (mode == "watch") { Watch(a.Length > 1 ? int.Parse(a[1]) : 3000); return; }
        if (mode == "peek") { Peek(a.Length > 1 ? int.Parse(a[1]) : 3000); return; }
        if (mode == "chain") { Chain(a.Length > 1 ? a[1] : ""); return; }
        Report("state");
    }

    static void Set(bool on)
    {
        APPBARDATA d = new APPBARDATA();
        d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
        d.hWnd = FindWindow("Shell_TrayWnd", null);
        d.lParam = (IntPtr)(on ? (1 | 2) : 2);   // ABS_AUTOHIDE | ABS_ALWAYSONTOP
        DateTime t0 = DateTime.Now;
        SHAppBarMessage(10, ref d);              // ABM_SETSTATE
        Console.WriteLine("setstate " + (on ? "on" : "off") + " took "
            + (int)(DateTime.Now - t0).TotalMilliseconds + " ms");
        Thread.Sleep(400);
        Report("after");
    }

    static void Peek(int hold)
    {
        POINT p; GetCursorPos(out p);
        MONITORINFO mi = Monitor();
        Console.WriteLine("cursor was " + p.X + "," + p.Y);
        SetCursorPos(mi.rcMonitor.Right / 2, mi.rcMonitor.Bottom - 1);
        int step = 150;
        for (int t = 0; t < hold; t += step)
        {
            Thread.Sleep(step);
            Console.WriteLine("+ " + (t + step) + " ms  " + Rect());
        }
        Console.WriteLine("-- cursor leaves the bottom edge --");
        SetCursorPos(mi.rcMonitor.Right / 2, mi.rcMonitor.Bottom / 2);
        for (int t = 0; t < 3000; t += step)
        {
            Thread.Sleep(step);
            Console.WriteLine("+ " + (t + step) + " ms  " + Rect());
        }
        SetCursorPos(p.X, p.Y);
    }

    static void Watch(int ms)
    {
        int step = 100;
        for (int t = 0; t < ms; t += step)
        {
            Thread.Sleep(step);
            Console.WriteLine("+ " + (t + step) + " ms  " + Rect());
        }
    }

    static void Report(string tag)
    {
        APPBARDATA d = new APPBARDATA();
        d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
        d.hWnd = FindWindow("Shell_TrayWnd", null);
        IntPtr st = SHAppBarMessage(4, ref d);   // ABM_GETSTATE

        MONITORINFO mi = Monitor();
        int mon = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
        int work = mi.rcWork.Bottom - mi.rcWork.Top;

        POINT p; GetCursorPos(out p);
        IntPtr fg = GetForegroundWindow();
        StringBuilder sb = new StringBuilder(256);
        GetClassName(fg, sb, sb.Capacity);

        Console.WriteLine(tag
            + "  getstate=" + st + " (autohide=" + (((int)st & 1) != 0 ? "1" : "0")
            + " ontop=" + (((int)st & 2) != 0 ? "1" : "0") + ")"
            + "  monitor=" + mon + " work=" + work + " -> " + (mon - work >= 8 ? "reserves-space" : "no-space")
            + "  tray=" + Rect()
            + "  cursor=" + p.X + "," + p.Y
            + "  fg=" + sb + (IsWindowVisible(fg) ? "" : "[hidden]")
            + (IsIconic(fg) ? "[iconic]" : "") + (IsZoomed(fg) ? "[zoomed]" : ""));
    }

    // The foreground's ancestry: what a stand-in has to work with when the
    // foreground itself is not a window the user can see.
    static void Chain(string match)
    {
        IntPtr fg = GetForegroundWindow();
        long n;
        if (long.TryParse(match, out n))
        {
            Console.WriteLine("hwnd: " + Info(new IntPtr(n)));
            Walk(new IntPtr(n));
            return;
        }
        Console.WriteLine("foreground: " + Info(fg));
        Walk(fg);
        if (match != "")
        {
            IntPtr h = GetTopWindowWalk();
            while (h != IntPtr.Zero)
            {
                StringBuilder sb = new StringBuilder(256);
                GetClassName(h, sb, sb.Capacity);
                if (IsWindowVisible(h) && sb.ToString().IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("match: " + Info(h));
                    Walk(h);
                }
                h = GetWindow(h, 2);
            }
        }
    }

    static void Walk(IntPtr h)
    {
        IntPtr root = GetAncestor(h, 2);        // GA_ROOT
        IntPtr owner = GetAncestor(h, 3);       // GA_ROOTOWNER
        IntPtr parent = GetParent(h);
        IntPtr gwOwner = GetWindow(h, 4);       // GW_OWNER
        Console.WriteLine("  GA_ROOT      = " + Info(root));
        Console.WriteLine("  GA_ROOTOWNER = " + Info(owner));
        Console.WriteLine("  GetParent    = " + Info(parent));
        Console.WriteLine("  GW_OWNER     = " + Info(gwOwner));
    }

    static IntPtr GetTopWindowWalk() { return GetTopWindow(IntPtr.Zero); }

    static string Info(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(none)";
        StringBuilder sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        StringBuilder t = new StringBuilder(256);
        GetWindowText(h, t, t.Capacity);
        RECT r; GetWindowRect(h, out r);
        return sb + (IsWindowVisible(h) ? "" : "[hidden]")
            + (IsMinimized(h) ? "[min]" : "") + (IsMaximized(h) ? "[max]" : "")
            + " [" + r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) + "]"
            + " hwnd=" + h + " \"" + t + "\"";
    }

    static bool IsMinimized(IntPtr h) { return IsIconic(h); }
    static bool IsMaximized(IntPtr h) { return IsZoomed(h); }

    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);

    static MONITORINFO Monitor()
    {
        MONITORINFO mi = new MONITORINFO();
        mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        GetMonitorInfo(MonitorFromWindow(IntPtr.Zero, 1), ref mi);
        return mi;
    }

    static string Rect()
    {
        IntPtr h = FindWindow("Shell_TrayWnd", null);
        if (h == IntPtr.Zero) return "(no tray)";
        RECT r; GetWindowRect(h, out r);
        return "[" + r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) + "]"
            + (IsWindowVisible(h) ? "" : "[hidden]");
    }
}
