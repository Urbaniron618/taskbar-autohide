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
//   taskbar-autohide.exe --log  also trace every foreground event, not just the
//                               decisions (taskbar-autohide.log, capped at 256 KB)
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
    const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    const uint EVENT_OBJECT_DESTROY = 0x8001;
    const uint EVENT_OBJECT_HIDE = 0x8003;
    const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    const int OBJID_WINDOW = 0;
    const int CHILDID_SELF = 0;

    // AppBar constants
    const int ABM_SETSTATE = 10;
    const int ABS_AUTOHIDE = 1;
    const int ABS_ALWAYSONTOP = 2;
    const uint MONITOR_DEFAULTTONEAREST = 2;
    const uint MONITOR_DEFAULTTOPRIMARY = 1;
    const int HWND_MESSAGE = -3;

    // Window inspection
    const int GWL_EXSTYLE = -20;
    const uint GW_HWNDNEXT = 2;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int DWMWA_CLOAKED = 14;

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

    // What to do with the taskbar, and what the decision was based on.
    // State is "on" / "off", or null for "leave it alone".
    struct Verdict
    {
        public string State;
        public IntPtr Subject;
        public bool StandIn;     // decided from a substitute for an unusable foreground
        public string Why;
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
    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetWindowLongW(IntPtr hWnd, int index);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr,
        out int value, int size);

    // Shell surfaces that must not flip the state: taskbar itself, Start, search,
    // flyouts, alt-tab. Ignoring them keeps a maximized window's state intact
    // while the user pokes at the shell.
    static readonly string[] Ignored = {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "TaskListThumbnailWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "MultitaskingViewFrame",
        "ForegroundStaging", "TopLevelWindowForOverflowXamlIsland",
        "TaskView", "TaskSwitcherWnd", "Shell_InputSwitchTopLevelWindow",
        "ThumbnailDeviceHelperWnd"
    };

    static readonly string[] Desktop = { "Progman", "WorkerW" };

    static WinEventProc _proc;           // must stay referenced or the hook dies
    static readonly IntPtr[] _hooks = new IntPtr[4];
    static Timer _debounce;
    static Timer _safety;                // periodic reality check, see OnSafety
    static bool _pending;                // a debounce tick is already armed
    static bool _standInArmed;           // a stand-in decision waits for the settle delay
    static IntPtr _subject;              // window the last decision was based on
    static string _lastSkip;             // last "cannot decide" reason, for the log only
    static DateTime _lastApply = DateTime.MinValue;
    static DateTime _lastSafetyApply = DateTime.MinValue;   // see OnSafety
    static string _lastSafetyTarget;
    static readonly object Gate = new object();        // guards _debounce.Change only
    static readonly object SubjectGate = new object(); // guards _subject
    static readonly object ApplyLock = new object();   // serializes decision + apply
    static bool _log;
    static string _logPath;

    static int Main(string[] args)
    {
        _logPath = Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
            "taskbar-autohide.log");

        foreach (string a in args)
        {
            string key = a.TrimStart('-', '/').ToLowerInvariant();
            if (key == "off")
            {
                SetAutoHide(false);
                return 0;
            }
            if (key == "log") _log = true;
        }

        bool created;
        using (new Mutex(true, "Local\\TaskbarAutoHide", out created))
        {
            if (!created) return 0;      // already running

            // A message-only window gives this thread a queue the hook can post to.
            CreateWindowEx(0, "STATIC", "taskbar-autohide", 0,
                0, 0, 0, 0, new IntPtr(HWND_MESSAGE), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            _proc = new WinEventProc(OnWinEvent);
            _hooks[0] = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            _hooks[1] = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            // A window that disappears takes the foreground with it, and some apps
            // hand the foreground to a hidden helper window instead of giving it
            // back. Without these two the taskbar could stay in the state the
            // transient left behind until something else happened to fire.
            _hooks[2] = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE,
                IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            _hooks[3] = SetWinEventHook(EVENT_SYSTEM_MINIMIZEEND, EVENT_SYSTEM_MINIMIZEEND,
                IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

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

            foreach (IntPtr h in _hooks) if (h != IntPtr.Zero) UnhookWinEvent(h);
        }
        return 0;
    }

    // Arm the debounce. Without force an already pending tick is left alone: a
    // maximize animation pumps out location events for several hundred ms, so
    // restarting the timer on each one would delay the switch until the
    // animation had finished.
    static void Arm(int ms, bool force)
    {
        lock (Gate)
        {
            if (_debounce == null) return;
            if (_pending && !force) return;
            _pending = true;
            _debounce.Change(ms, Timeout.Infinite);
        }
    }

    static void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd,
                           int idObject, int idChild, uint thread, uint time)
    {
        if (evt == EVENT_OBJECT_LOCATIONCHANGE)
        {
            // Location events fire constantly for every window on screen; only
            // the foreground window's own resize/move matters here (maximize,
            // restore).
            if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
            if (hwnd != GetForegroundWindow()) return;
            LogEvent("event: location " + Describe(hwnd));
            Arm(45, false);
            return;
        }

        if (evt == EVENT_OBJECT_DESTROY || evt == EVENT_OBJECT_HIDE)
        {
            if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
            bool subject;
            lock (SubjectGate) { subject = (hwnd == _subject); }
            if (!subject && hwnd != GetForegroundWindow()) return;
            LogEvent("event: gone " + Describe(hwnd) + (subject ? " (subject)" : " (foreground)"));
            Arm(140, true);      // let the shell settle on whatever comes next
            return;
        }

        if (evt == EVENT_SYSTEM_MINIMIZEEND)
        {
            // A restored window is activated before it has finished restoring, so
            // the foreground event can arrive while it still reads as minimized.
            LogEvent("event: minimizeend " + Describe(hwnd));
            Arm(60, false);
            return;
        }

        if (evt != EVENT_SYSTEM_FOREGROUND) return;   // EVENT_OBJECT_SHOW rides in the same range
        LogEvent("event: foreground -> " + Describe(hwnd));
        Arm(45, false);
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
            Verdict v = Decide();
            if (v.State == null) return;
            string actual = ActualState();
            if (actual == null || actual == v.State) { _lastSafetyTarget = null; return; }

            if (v.State == _lastSafetyTarget &&
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
            Verdict v = Decide();
            if (v.State == null)
            {
                if (v.Why != _lastSkip) Log("skip: " + v.Why);   // only when it changes
                _lastSkip = v.Why;
                _standInArmed = false;
                return;
            }
            _lastSkip = null;
            lock (SubjectGate) { _subject = v.Subject; }

            // Decide from the measured state, not from what we last set. ABM_SETSTATE
            // does not persist to StuckRects3 and explorer resets it when the taskbar
            // is recreated, so a cached value drifts and can stick forever.
            string actual = ActualState();
            if (actual == null) return;

            LogEvent("evaluate: " + v.Why + " target=" + v.State + " actual=" + actual);

            if (v.State == actual)
            {
                // Worth a line on its own: it is the proof that the stand-in path
                // ran and agreed with what the taskbar shows.
                if (v.StandIn) Log("stand-in " + v.State + " confirmed: " + v.Why);
                _standInArmed = false;
                return;
            }

            // A decision made from a stand-in window is a guess about a state in
            // transition (a popup closing, a taskbar click). Wait for it to settle
            // and look again, so a 100 ms gap between two windows cannot make the
            // taskbar slide twice.
            if (v.StandIn && !_standInArmed)
            {
                _standInArmed = true;
                LogEvent("  stand-in pending, re-checking in 170 ms");
                Arm(170, true);
                return;
            }
            _standInArmed = false;

            double since = (DateTime.UtcNow - _lastApply).TotalMilliseconds;
            if (since < 120)
            {
                LogEvent("  cooldown, retry in " + (int)(120 - since) + " ms");
                Arm((int)(120 - since) + 10, true);
                return;
            }
            Log("change " + actual + " -> " + v.State + ": " + v.Why);
            SetAutoHide(v.State == "on");
            _lastApply = DateTime.UtcNow;
            _lastSafetyApply = DateTime.UtcNow;
            _lastSafetyTarget = v.State;
            LogEvent("  APPLIED " + v.State);
            // No verification here on purpose: SHAppBarMessage can return before
            // explorer has finished moving the work area, so sampling now gives
            // false failures. OnSafety re-checks two seconds later.
        }
    }

    static string Describe(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(none)";
        return ClassOf(h) + (IsWindowVisible(h) ? "" : "[hidden]")
            + (IsIconic(h) ? "[iconic]" : "") + (IsZoomed(h) ? "[zoomed]" : "")
            + (IsFullscreen(h) ? "[full]" : "");
    }

    static void Log(string message)
    {
        try
        {
            FileInfo fi = new FileInfo(_logPath);
            if (fi.Exists && fi.Length > 262144) File.Delete(_logPath);
            File.AppendAllText(_logPath,
                DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
        }
        catch { }
    }

    // Decisions are always logged - a few lines per state change, and exactly
    // what is needed to tell why the taskbar did or did not move. The per-event
    // trace is only written with --log.
    static void LogEvent(string message)
    {
        if (_log) Log(message);
    }

    // "on" = hide the taskbar, "off" = show it, null = do not change anything.
    static Verdict Decide()
    {
        Verdict v = new Verdict();
        IntPtr h = GetForegroundWindow();
        string why = "fg=none";

        if (h != IntPtr.Zero)
        {
            string cls = ClassOf(h);
            bool desktop = Array.IndexOf(Desktop, cls) >= 0;
            bool shell = Array.IndexOf(Ignored, cls) >= 0;
            bool visible = IsWindowVisible(h);

            // The desktop is in front: the taskbar belongs on screen.
            if (desktop)
            {
                v.State = "off"; v.Subject = h; v.Why = "fg=" + cls;
                return v;
            }
            // A real window the user can see and is looking at.
            if (!shell && visible)
            {
                v.State = IsIconic(h) ? "off" : StateOf(h);
                v.Subject = h; v.Why = "fg=" + cls;
                return v;
            }
            why = "fg=" + (shell ? "shell:" : "") + cls + (visible ? "" : "[hidden]");
        }

        // The foreground window is unusable: a shell surface, a hidden helper, or
        // nothing at all. This is where the taskbar used to freeze. WeChat hands
        // the foreground to its own hidden window when a chat popup closes, and
        // WPS does the same when a document is activated from the taskbar - both
        // left "no foreground" behind, the old code answered "leave the state
        // alone", and the taskbar stayed visible over a maximized window.
        // Judge by the topmost window the user can actually see instead.
        IntPtr s = TopmostUserWindow();
        if (s == IntPtr.Zero) { v.Why = why + " -> no-candidate"; return v; }

        v.Subject = s;
        v.State = StateOf(s);
        v.StandIn = true;
        v.Why = why + " -> stand-in=" + Describe(s);
        return v;
    }

    static string StateOf(IntPtr h)
    {
        return (IsZoomed(h) || IsFullscreen(h)) ? "on" : "off";
    }

    // Topmost window a user could plausibly be looking at, in z-order. Skips the
    // desktop, the shell surfaces above, and anything invisible, minimized,
    // cloaked (a suspended UWP app), tool or no-activate (a tray / overlay host).
    static IntPtr TopmostUserWindow()
    {
        IntPtr h = GetTopWindow(IntPtr.Zero);
        int guard = 0;
        while (h != IntPtr.Zero && guard++ < 500)
        {
            if (IsUserWindow(h)) return h;
            h = GetWindow(h, GW_HWNDNEXT);
        }
        return IntPtr.Zero;
    }

    static bool IsUserWindow(IntPtr h)
    {
        if (!IsWindowVisible(h)) return false;
        if (IsIconic(h)) return false;

        string cls = ClassOf(h);
        if (Array.IndexOf(Desktop, cls) >= 0) return false;
        if (Array.IndexOf(Ignored, cls) >= 0) return false;
        if (IsCloaked(h)) return false;

        int ex = GetWindowLongW(h, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
        if ((ex & WS_EX_NOACTIVATE) != 0) return false;

        RECT r;
        if (!GetWindowRect(h, out r)) return false;
        if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return false;
        return true;
    }

    static bool IsCloaked(IntPtr h)
    {
        int cloaked;
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, sizeof(int)) != 0) return false;
        return cloaked != 0;
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
