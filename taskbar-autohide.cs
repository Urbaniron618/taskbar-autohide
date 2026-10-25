// taskbar-autohide.cs
//
// Shows the taskbar while the desktop or a normal window is focused, and turns
// the taskbar's auto-hide ON while a maximized / fullscreen window is focused.
// Move the mouse to the bottom edge to peek at the taskbar.
//
// Event driven on purpose: it installs a WinEvent hook for foreground changes
// and window moves instead of polling, so it reacts in ~0.1 s and burns no CPU
// while idle. Nothing here touches the GPU; the only GPU work is the taskbar's
// own slide animation when the state changes.
//
// Build (no SDK needed, ships with .NET Framework):
//   %SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /optimize+ ^
//     /target:winexe /out:taskbar-autohide.exe taskbar-autohide.cs
//
// Usage:
//   taskbar-autohide.exe        run in the background (one instance only)
//   taskbar-autohide.exe --off  restore auto-hide OFF and exit
//
// ASCII only (PowerShell 5.1 / csc read BOM-less UTF-8 as ANSI on this machine).

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class Program
{
    // WinEvent constants
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    const int OBJID_WINDOW = 0;
    const int CHILDID_SELF = 0;

    // AppBar constants
    const int ABM_SETSTATE = 10;
    const int ABS_AUTOHIDE = 1;
    const int ABS_ALWAYSONTOP = 2;
    const uint MONITOR_DEFAULTTONEAREST = 2;
    const uint MONITOR_DEFAULTTOPRIMARY = 1;
    const int HWND_MESSAGE = -3;

    delegate void WinEventProc(IntPtr hWinEventHook, uint evt, IntPtr hwnd,
                               int idObject, int idChild, uint thread, uint time);

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
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string cls, string name);
    [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(int msg, ref APPBARDATA d);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod,
        WinEventProc cb, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hHook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);

    // Shell surfaces that must not flip the state: taskbar itself, Start, search,
    // flyouts, alt-tab. Ignoring them keeps a maximized window's state intact
    // while the user pokes at the shell.
    static readonly string[] Ignored = {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "TaskListThumbnailWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "MultitaskingViewFrame",
        "ForegroundStaging", "TopLevelWindowForOverflowXamlIsland",
        "TaskView", "Shell_InputSwitchTopLevelWindow", "ThumbnailDeviceHelperWnd"
    };

    static readonly string[] Desktop = { "Progman", "WorkerW" };

    static WinEventProc _proc;           // must stay referenced or the hook dies
    static IntPtr _hookFg;
    static IntPtr _hookLoc;
    static Timer _debounce;
    static Timer _safety;                // periodic reality check, see OnSafety
    static bool _pending;                // a debounce tick is already armed
    static string _applied;              // last state we set, for the log only
    static DateTime _lastApply = DateTime.MinValue;
    static DateTime _lastSafetyApply = DateTime.MinValue;   // see OnSafety
    static string _lastSafetyTarget;
    static readonly object Gate = new object();        // guards _debounce.Change only
    static readonly object ApplyLock = new object();   // serializes decision + apply
    static bool _log;
    static string _logPath;

    static int Main(string[] args)
    {
        foreach (string a in args)
        {
            string key = a.TrimStart('-', '/').ToLowerInvariant();
            if (key == "off")
            {
                SetAutoHide(false);
                return 0;
            }
            if (key == "log")
            {
                _log = true;
                _logPath = Path.Combine(
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                    "taskbar-autohide.log");
            }
        }

        bool created;
        using (new Mutex(true, "Local\\TaskbarAutoHide", out created))
        {
            if (!created) return 0;      // already running

            // A message-only window gives this thread a queue the hook can post to.
            CreateWindowEx(0, "STATIC", "taskbar-autohide", 0,
                0, 0, 0, 0, new IntPtr(HWND_MESSAGE), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            _proc = new WinEventProc(OnWinEvent);
            _hookFg = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
            _hookLoc = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
            _debounce = new Timer(OnDebounce, null, Timeout.Infinite, Timeout.Infinite);
            // Explorer restarts (taskbar recreated, auto-hide back to "off"), the
            // user flipping the setting, a missed event: none of those produce a
            // hook callback, so re-check reality on a slow timer.
            _safety = new Timer(OnSafety, null, 2000, 2000);

            Apply();                     // settle the initial state

            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (_hookFg != IntPtr.Zero) UnhookWinEvent(_hookFg);
            if (_hookLoc != IntPtr.Zero) UnhookWinEvent(_hookLoc);
        }
        return 0;
    }

    static void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd,
                           int idObject, int idChild, uint thread, uint time)
    {
        // Location events fire constantly for every window on screen; only the
        // foreground window's own resize/move matters here (maximize, restore).
        if (evt == EVENT_OBJECT_LOCATIONCHANGE)
        {
            if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
            if (hwnd != GetForegroundWindow()) return;
            Log("event: location " + DescribeForeground());
        }
        else if (evt == EVENT_SYSTEM_FOREGROUND)
        {
            Log("event: foreground -> " + DescribeForeground());
        }

        // Arm once per burst, never restart it: a maximize animation pumps out
        // location events for several hundred ms, so resetting the timer on each
        // one would delay the switch until the animation had finished.
        lock (Gate)
        {
            if (_debounce != null && !_pending)
            {
                _pending = true;
                _debounce.Change(45, Timeout.Infinite);
            }
        }
    }

    static void OnDebounce(object state)
    {
        lock (Gate) { _pending = false; }
        try { Apply(); }
        catch { }
    }

    // Slow reality check. Event-driven applies are never suppressed by this;
    // the guard only stops the timer from hammering a state that refuses to
    // change (each SHAppBarMessage call costs 250-450 ms of blocked time).
    static void OnSafety(object state)
    {
        try
        {
            string target = Target();
            if (target == null) return;
            string actual = ActualState();
            if (actual == null || actual == target) { _lastSafetyTarget = null; return; }

            if (target == _lastSafetyTarget &&
                (DateTime.UtcNow - _lastSafetyApply).TotalSeconds < 6) return;

            Apply();
        }
        catch { }
    }

    // Serialized on purpose: SHAppBarMessage blocks for 200-500 ms while explorer
    // animates the taskbar, and a second timer tick during that window would act
    // on a decision that is already stale.
    static void Apply()
    {
        lock (ApplyLock)
        {
            string target = Target();
            if (target == null) return;                 // shell surface: leave as-is

            // Decide from the measured state, not from what we last set. ABM_SETSTATE
            // does not persist to StuckRects3 and explorer resets it when the taskbar
            // is recreated, so a cached value drifts and can stick forever.
            string actual = ActualState();
            if (actual == null) return;

            Log("evaluate: fg=" + DescribeForeground() + " target=" + target + " actual=" + actual);

            if (target == actual) { _applied = target; return; }

            double since = (DateTime.UtcNow - _lastApply).TotalMilliseconds;
            if (since < 120)
            {
                Log("  cooldown, retry in " + (int)(120 - since) + " ms");
                lock (Gate) { _pending = true; _debounce.Change((int)(120 - since) + 10, Timeout.Infinite); }
                return;
            }
            Log("  apply " + target + " (showing/hiding)");
            SetAutoHide(target == "on");
            _applied = target;
            _lastApply = DateTime.UtcNow;
            _lastSafetyApply = DateTime.UtcNow;
            _lastSafetyTarget = target;
            Log("  APPLIED " + target);
            // No verification here on purpose: SHAppBarMessage can return before
            // explorer has finished moving the work area, so sampling now gives
            // false failures. OnSafety re-checks two seconds later.
        }
    }

    static string DescribeForeground()
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return "(none)";
        return ClassOf(h) + (IsZoomed(h) ? "[zoomed]" : "") + (IsFullscreen(h) ? "[full]" : "");
    }

    static void Log(string message)
    {
        if (!_log || _logPath == null) return;
        try
        {
            FileInfo fi = new FileInfo(_logPath);
            if (fi.Exists && fi.Length > 262144) File.Delete(_logPath);
            File.AppendAllText(_logPath,
                DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
        }
        catch { }
    }

    // "on" = hide the taskbar, "off" = show it, null = do not change anything.
    static string Target()
    {
        IntPtr h = GetForegroundWindow();
        // No foreground window means the lock screen / secure desktop / a session
        // transition is in progress. That is not "the user is on the desktop", so
        // leave the state alone instead of toggling the taskbar on every lock.
        if (h == IntPtr.Zero) return null;

        string cls = ClassOf(h);
        if (Array.IndexOf(Desktop, cls) >= 0) return "off";
        if (Array.IndexOf(Ignored, cls) >= 0) return null;

        // Tray icon hosts (TranslucentTB's "TrayWindow", and the like) are
        // invisible windows that take the foreground when you click the tray.
        // They are not something the user is looking at, so leave the state
        // alone instead of popping the taskbar over a maximized window.
        if (!IsWindowVisible(h)) return null;

        if (IsIconic(h)) return "off";
        if (IsZoomed(h)) return "on";
        if (IsFullscreen(h)) return "on";
        return "off";
    }

    // "on" = the taskbar reserves no space in the primary work area (auto-hidden),
    // "off" = it does. Cheap enough (two user32 calls) to run on every decision.
    static string ActualState()
    {
        MONITORINFO mi = new MONITORINFO();
        mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        if (!GetMonitorInfo(MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY), ref mi))
            return null;
        int monitor = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
        int work = mi.rcWork.Bottom - mi.rcWork.Top;
        return (monitor - work >= 8) ? "off" : "on";
    }

    static bool IsFullscreen(IntPtr h)
    {
        RECT r;
        if (!GetWindowRect(h, out r)) return false;
        MONITORINFO mi = new MONITORINFO();
        mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        if (!GetMonitorInfo(MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST), ref mi)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top
            && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    static string ClassOf(IntPtr h)
    {
        StringBuilder sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    static void SetAutoHide(bool on)
    {
        APPBARDATA d = new APPBARDATA();
        d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
        d.hWnd = FindWindow("Shell_TrayWnd", null);
        d.lParam = (IntPtr)(on ? (ABS_AUTOHIDE | ABS_ALWAYSONTOP) : ABS_ALWAYSONTOP);
        SHAppBarMessage(ABM_SETSTATE, ref d);
    }
}
