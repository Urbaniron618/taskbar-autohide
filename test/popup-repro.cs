// test/popup-repro.cs
//
// Test harness for the two reported failures:
//   "a popup closes and the taskbar stops auto-hiding"
//   "opening an already-open document from the taskbar does the same"
//
// Both were caused by the foreground window ending up on something the tool
// would not judge: a hidden helper window, or nothing at all. This harness
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
        int hold = args.Length > 1 ? int.Parse(args[1]) : 4000;
        if (mode == "frame") return Frame(hold);
        if (mode == "steal") return Steal(hold);
        if (mode == "focus") return Focus(args[2], hold);
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
