// test/popup-repro.cs
//
// Test harness for the reported failures:
//   "a popup closes and the taskbar stops auto-hiding"
//   "opening an already-open document from the taskbar does the same"
//   "bilibili: opening a video from a maximized page flashes the taskbar"
//   "Steam Big Picture: the taskbar does not auto-hide"
//
// The first two were caused by the foreground window ending up on something the
// tool would not judge: a hidden helper window, or nothing at all. This harness
// recreates that state without WeChat or WPS.
//
//   popup-repro.exe frame <ms>   create a maximized window and hold it (the
//                                "app behind the popup")
//   popup-repro.exe popup <ms>   create a small visible window, take the
//                                foreground, hide it after 1.5 s, then keep
//                                printing what owns the foreground
//   popup-repro.exe steal <ms>   hand the foreground to a never-shown window of
//                                a live process (WeChat / WPS helper window)
//   popup-repro.exe focus <match> <ms>
//                                activate the first visible window whose class
//                                or title contains <match>, like a taskbar click
//   popup-repro.exe borderless <ms>
//                                a borderless window the size of the monitor
//                                minus 1 px, never maximized - Steam Big
//                                Picture's shape (v1.4's second failure)
//   popup-repro.exe large <ms>   a plain window at 90% x 99% of the work area -
//                                the bilibili client's video window (v1.4's
//                                first failure)
//   popup-repro.exe small <ms>   the control: 50% x 50%, the taskbar belongs on
//                                screen
//
// Build:
//   csc /nologo /target:exe /out:popup-repro.exe test\popup-repro.cs
//
// ASCII only.

using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class Repro
{
    const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    const uint WS_POPUP = 0x80000000;
    const uint WS_CAPTION = 0x00C00000;
    const uint WS_SYSMENU = 0x00080000;
    const uint WS_VISIBLE = 0x10000000;
    const int SW_HIDE = 0;
    const int SW_MAXIMIZE = 3;
    const int SW_SHOW = 5;
    const uint GW_HWNDNEXT = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x, y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern IntPtr SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG m, IntPtr h,
        uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "popup";
        // focus takes <match> <ms>; every other mode takes <ms>.
        if (mode == "focus") return Focus(args[1], args.Length > 2 ? int.Parse(args[2]) : 4000);
        int hold = args.Length > 1 ? int.Parse(args[1]) : 4000;
        if (mode == "frame") return Frame(hold);
        if (mode == "steal") return Steal(hold);
        if (mode == "tool") return Tool(hold);
        if (mode == "borderless") return Borderless(hold);
        if (mode == "large") return Percent("repro large", 90, 99, hold);
        if (mode == "small") return Percent("repro small", 50, 50, hold);
        return Popup(hold);
    }

    // Activate a real window by class or title substring, the way a taskbar
    // click does, and hold the foreground there.
    static int Focus(string match, int hold)
    {
        IntPtr h = IntPtr.Zero;
        IntPtr w = GetTopWindow(IntPtr.Zero);
        while (w != IntPtr.Zero)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassName(w, sb, sb.Capacity);
            StringBuilder t = new StringBuilder(256);
            GetWindowText(w, t, t.Capacity);
            if (IsWindowVisible(w) && (sb.ToString().IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0
                || t.ToString().IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0))
            { h = w; break; }
            w = GetWindow(w, GW_HWNDNEXT);
        }
        if (h == IntPtr.Zero) { Console.WriteLine("no window matching " + match); return 1; }
        ForceForeground(h);
        SleepPump(300);
        Console.WriteLine("focus " + Describe(GetForegroundWindow()));
        SleepPump(hold);
        return 0;
    }

    // A never-shown window takes the foreground and keeps it. This is the state
    // WeChat leaves behind when a chat popup closes, and the one WPS produces
    // when a document is activated from the taskbar: GetForegroundWindow
    // returns a window nobody can see.
    static int Steal(int hold)
    {
        IntPtr h = CreateWindowEx(0, "STATIC", "repro hidden owner", WS_POPUP,
            0, 0, 100, 100, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        ForceForeground(h);
        SleepPump(200);
        Console.WriteLine("hidden hwnd=" + h + " fg=" + Describe(GetForegroundWindow()));
        SleepPump(hold);
        return 0;
    }

    // The maximized window that sits behind the popup.
    static int Frame(int hold)
    {
        IntPtr h = CreateWindowEx(0, "STATIC", "repro frame",
            WS_OVERLAPPEDWINDOW | WS_VISIBLE, 200, 120, 800, 600,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        ShowWindow(h, SW_MAXIMIZE);
        ForceForeground(h);
        Console.WriteLine("frame hwnd=" + h + " zoomed=" + IsZoomed(h));
        SleepPump(600);
        Console.WriteLine("fg=" + Describe(GetForegroundWindow()));
        SleepPump(hold);
        return 0;
    }

    // A visible *tool* window takes the foreground in front of a maximized
    // window - the shape WeChat's Qt tool popups (Qt5QWindowToolSaveBits) have.
    // A tool window is never a window the user works in; the taskbar has to keep
    // following the window behind it.
    static int Tool(int hold)
    {
        IntPtr frame = CreateWindowEx(0, "STATIC", "repro tool frame",
            WS_OVERLAPPEDWINDOW | WS_VISIBLE, 200, 120, 800, 600,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        ShowWindow(frame, SW_MAXIMIZE);
        ForceForeground(frame);
        SleepPump(800);
        Console.WriteLine("frame " + Describe(frame) + " fg=" + Describe(GetForegroundWindow()));

        IntPtr tip = CreateWindowEx(0x00000080 /* WS_EX_TOOLWINDOW */, "STATIC",
            "repro tool", WS_POPUP | WS_VISIBLE, 700, 500, 240, 120,
            frame, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        ShowWindow(tip, SW_SHOW);
        ForceForeground(tip);
        SleepPump(300);
        Console.WriteLine("tool  " + Describe(tip) + " fg=" + Describe(GetForegroundWindow()));

        int step = 500;
        for (int waited = 0; waited < hold; waited += step)
        {
            SleepPump(step);
            Console.WriteLine("+" + (waited + step) + " ms : fg=" + Describe(GetForegroundWindow()));
        }
        return 0;
    }

    // A small window that takes the foreground and then vanishes, the way a
    // notification / picker popup does - except that this process stays alive,
    // exactly like WeChat or WPS keep their hidden helper windows around.
    static int Popup(int hold)
    {
        IntPtr h = CreateWindowEx(0, "STATIC", "repro popup",
            WS_POPUP | WS_CAPTION | WS_SYSMENU | WS_VISIBLE, 500, 400, 320, 180,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        ShowWindow(h, SW_SHOW);
        ForceForeground(h);
        Console.WriteLine("popup hwnd=" + h);

        SleepPump(1500);
        Console.WriteLine("before hide : fg=" + Describe(GetForegroundWindow()));

        ShowWindow(h, SW_HIDE);

        // ... and hand the foreground to a hidden window, which is what these
        // apps do instead of giving it back to the window underneath. No pause in
        // between: the tool's 45 ms debounce has to land on the hidden window,
        // the way it does with WeChat and WPS.
        Console.WriteLine("right after hide: fg=" + Describe(GetForegroundWindow()));
        IntPtr helper = CreateWindowEx(0, "STATIC", "repro hidden helper", WS_POPUP,
            0, 0, 100, 100, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        ForceForeground(helper);
        SleepPump(150);
        Console.WriteLine("after  hide : fg=" + Describe(GetForegroundWindow()));

        int step = 500;
        for (int waited = 0; waited < hold; waited += step)
        {
            SleepPump(step);
            Console.WriteLine("+" + (waited + step) + " ms : fg=" + Describe(GetForegroundWindow()));
        }
        return 0;
    }

    // Steam Big Picture's shape: borderless, the size of the monitor but one
    // pixel short, and never ShowWindow(SW_MAXIMIZE). v1.3 called that "not
    // fullscreen" and left the taskbar out over the whole picture.
    static int Borderless(int hold)
    {
        MONITORINFO mi = Primary();
        IntPtr h = CreateWindowEx(0, "STATIC", "repro borderless",
            WS_POPUP | WS_VISIBLE, mi.rcMonitor.Left, mi.rcMonitor.Top,
            mi.rcMonitor.Right - mi.rcMonitor.Left,
            mi.rcMonitor.Bottom - mi.rcMonitor.Top - 1,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        ShowWindow(h, SW_SHOW);
        ForceForeground(h);
        SleepPump(300);
        Console.WriteLine("borderless " + Describe(h) + " (monitor minus 1 px, never maximized)");
        SleepPump(hold);
        return 0;
    }

    // The bilibili client's video window: a plain, never-maximized window sized
    // to a percentage of the work area. v1.3 judged it by IsZoomed, said "small
    // window", and the taskbar came out over the video and back in again when
    // the list window returned.
    static int Percent(string title, int wPct, int hPct, int hold)
    {
        MONITORINFO mi = Primary();
        int w = mi.rcWork.Right - mi.rcWork.Left;
        int ht = mi.rcWork.Bottom - mi.rcWork.Top;
        IntPtr h = CreateWindowEx(0, "STATIC", title, WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            mi.rcWork.Left, mi.rcWork.Top, w * wPct / 100, ht * hPct / 100,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        ShowWindow(h, SW_SHOW);
        ForceForeground(h);
        SleepPump(300);
        Console.WriteLine(title + " " + Describe(h) + " (" + wPct + "% x " + hPct + "% of work area)");
        SleepPump(hold);
        return 0;
    }

    static MONITORINFO Primary()
    {
        MONITORINFO mi = new MONITORINFO();
        mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        GetMonitorInfo(MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY), ref mi);
        return mi;
    }

    static void ForceForeground(IntPtr h)
    {
        IntPtr fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
        uint myThread = GetCurrentThreadId();
        if (fgThread != 0) AttachThreadInput(myThread, fgThread, true);
        SetForegroundWindow(h);
        if (fgThread != 0) AttachThreadInput(myThread, fgThread, false);
    }

    // Sleep without going unresponsive, so Windows does not ghost the window.
    static void SleepPump(int ms)
    {
        int waited = 0;
        MSG m;
        while (waited < ms)
        {
            while (PeekMessage(out m, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
            {
                TranslateMessage(ref m);
                DispatchMessage(ref m);
            }
            Thread.Sleep(10);
            waited += 10;
        }
    }

    static string Describe(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(none)";
        StringBuilder sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        string cls = sb.ToString();
        return "hwnd=" + h + " class=" + cls + (IsWindowVisible(h) ? "" : "[hidden]")
            + (IsZoomed(h) ? "[zoomed]" : "");
    }
}
