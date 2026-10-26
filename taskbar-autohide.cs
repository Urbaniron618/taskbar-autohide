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
// Two things the appbar state cannot tell you, and v1.2 has to:
//   * The tray window can be *parked* on screen while auto-hide says "on". A
//     transition to auto-hide made while the pointer is on the taskbar does it
//     (clicking a taskbar button is exactly that), and nothing in explorer ever
//     moves the tray back - the work area is already correct, so nothing here
//     used to notice. See TrayOut / Verify.
//   * The shell itself can hold the focus - a taskbar click, Start, a flyout -
//     without the user having switched to another document. Judging the taskbar
//     from a stand-in window in that moment is what made it pop out and stay
//     out. See ShellSurfaces / Decide.
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
    const uint GA_ROOTOWNER = 3;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int DWMWA_CLOAKED = 14;

    delegate void WinEventProc(IntPtr hWinEventHook, uint evt, IntPtr hwnd,
                               int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

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
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetWindowLongW(IntPtr hWnd, int index);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr,
        out int value, int size);

    // Surfaces that belong to the shell itself. While one of them has the focus
    // the user is poking at the shell - a taskbar button, Start, search, the
    // alt-tab switcher, a flyout - not switching between documents. The taskbar
    // keeps the state it had; the window the user ends up looking at fires its
    // own foreground event a moment later.
    //
    // Judging these from a stand-in window instead (what v1.1 did) is how a
    // click on the taskbar turned into "the topmost other window is not
    // maximized, so show the taskbar" - and the taskbar then stayed out.
    static readonly string[] ShellSurfaces = {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "TaskListThumbnailWnd", "XamlExplorerHostIslandWindow",
        "XamlExplorerHostIslandWindow_WASDK", "MultitaskingViewFrame",
        "TopLevelWindowForOverflowXamlIsland", "TaskView", "TaskSwitcherWnd",
        "Shell_InputSwitchTopLevelWindow"
    };

    // Never a window a user can be looking at, but not a shell surface either:
    // while one of these holds the focus an activation is still in flight, so
    // the state is judged from a stand-in window (see Decide).
    static readonly string[] HelperWindows = {
        "ForegroundStaging", "Windows.UI.Core.CoreWindow",
        "ThumbnailDeviceHelperWnd", "ApplicationManager_DesktopShellWindow"
    };

    static readonly string[] Desktop = { "Progman", "WorkerW" };

    static WinEventProc _proc;           // must stay referenced or the hook dies
    static readonly IntPtr[] _hooks = new IntPtr[4];
    static Timer _debounce;
    static Timer _safety;                // periodic reality check, see OnSafety
    static Timer _verify;                // post-apply check of the tray itself
    static bool _pending;                // a debounce tick is already armed
    static bool _standInArmed;           // a stand-in decision waits for the settle delay
    static IntPtr _subject;              // window the last decision was based on
    static IntPtr _lastUsable;           // last window the user really had in front
    static string _lastSkip;             // last "cannot decide" reason, for the log only
    static DateTime _lastApply = DateTime.MinValue;
    static DateTime _lastSafetyApply = DateTime.MinValue;   // see OnSafety
    static string _lastSafetyTarget;
    static int _unwedges;                // corrective toggles since the tray last moved
    static int _trayParked;              // visible tray height on the last verify pass
    static DateTime _trayParkedAt = DateTime.MinValue;
    static int _defers;                  // applies held back while the pointer is on the tray
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
            _verify = new Timer(OnVerify, null, Timeout.Infinite, Timeout.Infinite);
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

    static void ArmVerify(int ms)
    {
        if (_verify != null) _verify.Change(ms, Timeout.Infinite);
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
            if (actual == null) return;

            if (actual != v.State)
            {
                if (v.State == _lastSafetyTarget &&
                    (DateTime.UtcNow - _lastSafetyApply).TotalSeconds < 6) return;
                Apply();
                return;
            }

            // State agrees; the tray may still be parked on screen.
            if (v.State == "on") Verify();
        }
        catch { }
    }

    static void OnVerify(object state)
    {
        try { Verify(); }
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

            LogEvent("evaluate: " + v.Why + " target=" + v.State + " actual=" + actual
                + " tray=" + (TrayOut() ? "out" : "in"));

            if (v.State == actual)
            {
                // Worth a line on its own: it is the proof that the stand-in path
                // ran and agreed with what the taskbar shows.
                if (v.StandIn) Log("stand-in " + v.State + " confirmed: " + v.Why);
                _standInArmed = false;
                _defers = 0;
                // The appbar state is right, the tray may not be - see Verify.
                if (v.State == "on" && TrayOut() && !PointerOnTray()) ArmVerify(250);
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

            // Turning auto-hide ON while the pointer is on the taskbar leaves the
            // tray parked on screen (measured on 26300: it stays out until the
            // state is toggled by hand). The pointer is there because the user
            // just clicked a taskbar button; wait for them to move into the
            // window and apply then. 250 ms is enough to catch that, and a
            // pointer that stays put for 10 s is a deliberate peek anyway.
            if (v.State == "on" && PointerOnTray() && _defers < 40)
            {
                _defers++;
                LogEvent("  defer: pointer on the taskbar");
                Arm(250, true);
                return;
            }
            _defers = 0;

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
            // false failures. The verify timer re-checks once it has settled.
            if (v.State == "on") ArmVerify(520);
        }
    }

    // The appbar state and the tray's position are two different things, and it
    // is the position the user sees. While auto-hide is on the tray is expected
    // to sit 2 px below the bottom edge; a tray that is still on screen with the
    // pointer nowhere near it is a peek the shell never took back. Nothing in
    // explorer recovers from that on its own (measured: it stays out for
    // minutes), so toggle auto-hide off and on again - that is what moves it,
    // and it is the only thing that does: re-sending the same state is a no-op.
    static void Verify()
    {
        // Same lock as Apply: this moves the taskbar, and a decision landing in
        // the middle of the off/on pair would act on a half-applied state.
        lock (ApplyLock)
        {
            if (ActualState() != "on") { _trayParked = 0; _unwedges = 0; return; }

            int px = TrayVisiblePixels();
            if (px <= 4) { _trayParked = 0; _unwedges = 0; return; }   // tucked away: fine
            if (PointerOnTray()) { _trayParked = 0; return; }          // a peek in progress

            // A peek the pointer has just left keeps sliding for ~600 ms, and the
            // slide changes the visible height on every sample. Only a tray that
            // sits in the same place for most of a second was left behind.
            if (px != _trayParked)
            {
                _trayParked = px;
                _trayParkedAt = DateTime.UtcNow;
                ArmVerify(450);
                return;
            }
            if ((DateTime.UtcNow - _trayParkedAt).TotalMilliseconds < 800)
            {
                ArmVerify(450);
                return;
            }

            if (++_unwedges > 3) return;    // a shell that keeps refusing: stop fighting it
            Log("wedge: tray parked on screen with auto-hide on -> toggling");
            SetAutoHide(false);
            Thread.Sleep(150);
            SetAutoHide(true);
            _lastApply = DateTime.UtcNow;
            _lastSafetyApply = DateTime.UtcNow;
            _lastSafetyTarget = "on";
            ArmVerify(700);                 // and look again once it has settled
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

        if (h == IntPtr.Zero) { v.Why = "fg=none"; return v; }

        string cls = ClassOf(h);

        // The desktop is in front: the taskbar belongs on screen.
        if (Array.IndexOf(Desktop, cls) >= 0)
        {
            v.State = "off"; v.Subject = h; v.Why = "fg=" + cls;
            return v;
        }

        bool shell = Array.IndexOf(ShellSurfaces, cls) >= 0;
        bool helper = Array.IndexOf(HelperWindows, cls) >= 0;
        bool visible = IsWindowVisible(h);

        // A real window the user can see and is looking at.
        if (!shell && !helper && visible)
        {
            v.State = IsIconic(h) ? "off" : StateOf(h);
            v.Subject = h; v.Why = "fg=" + cls;
            if (!IsIconic(h)) _lastUsable = h;
            return v;
        }

        // The shell has the focus: a taskbar click, Start, search, alt-tab, a
        // flyout. The user is poking at the shell, not changing what is on
        // screen, so the taskbar keeps whatever state it had - the window they
        // are actually working in fires its own foreground event right after.
        // Guessing from another window here is what used to pop the taskbar out
        // on a taskbar click and leave it out.
        if (shell) { v.Why = "fg=shell:" + cls; return v; }

        // The foreground is unusable - a hidden helper (WeChat hands one the
        // focus when a chat popup closes, WPS when a document is activated from
        // the taskbar). Judge by, in order: the window the helper belongs to,
        // the topmost window the user can see, the window they were last really
        // working in.
        string why = "fg=" + (helper ? "helper:" : "") + cls + (visible ? "" : "[hidden]");
        IntPtr s = IntPtr.Zero;
        string how = null;

        IntPtr owner = GetAncestor(h, GA_ROOTOWNER);
        if (owner != IntPtr.Zero && owner != h && IsUserWindow(owner)) { s = owner; how = "owner"; }
        if (s == IntPtr.Zero) { s = TopmostUserWindow(); if (s != IntPtr.Zero) how = "z-order"; }
        if (s == IntPtr.Zero && IsUserWindow(_lastUsable)) { s = _lastUsable; how = "last"; }
        if (s == IntPtr.Zero) { v.Why = why + " -> no-candidate"; return v; }

        v.Subject = s;
        v.State = StateOf(s);
        v.StandIn = true;
        v.Why = why + " -> stand-in(" + how + ")=" + Describe(s);
        return v;
    }

    static string StateOf(IntPtr h)
    {
        return (IsZoomed(h) || IsFullscreen(h)) ? "on" : "off";
    }

    // Topmost window a user could plausibly be looking at, in z-order. Skips the
    // desktop, the shell surfaces and helpers above, and anything invisible,
    // minimized, cloaked (a suspended UWP app), tool or no-activate (a tray /
    // overlay host), or parked off screen.
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
        if (h == IntPtr.Zero) return false;
        if (!IsWindowVisible(h)) return false;
        if (IsIconic(h)) return false;

        string cls = ClassOf(h);
        if (Array.IndexOf(Desktop, cls) >= 0) return false;
        if (Array.IndexOf(ShellSurfaces, cls) >= 0) return false;
        if (Array.IndexOf(HelperWindows, cls) >= 0) return false;
        if (IsCloaked(h)) return false;

        int ex = GetWindowLongW(h, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
        if ((ex & WS_EX_NOACTIVATE) != 0) return false;

        RECT r;
        if (!GetWindowRect(h, out r)) return false;
        if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return false;

        // On screen. Apps keep helper windows WS_VISIBLE at -32000,-32000 or
        // just outside the frame; they are real windows by every API here and
        // would be picked as "the topmost thing the user sees".
        MONITORINFO mi;
        if (!PrimaryMonitor(out mi)) return true;
        if (r.Right <= mi.rcMonitor.Left || r.Left >= mi.rcMonitor.Right) return false;
        if (r.Bottom <= mi.rcMonitor.Top || r.Top >= mi.rcMonitor.Bottom) return false;
        return true;
    }

    static bool IsCloaked(IntPtr h)
    {
        int cloaked;
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, sizeof(int)) != 0) return false;
        return cloaked != 0;
    }

    // "on" = the taskbar reserves no space in the primary work area (auto-hide is
    // on), "off" = it does. This is the appbar's own idea of the state, and it
    // says nothing about where the tray window actually is - see TrayOut.
    static string ActualState()
    {
        MONITORINFO mi;
        if (!PrimaryMonitor(out mi)) return null;
        int monitor = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
        int work = mi.rcWork.Bottom - mi.rcWork.Top;
        return (monitor - work >= 8) ? "off" : "on";
    }

    // Is more than a sliver of the tray window on screen? Auto-hidden keeps it
    // two pixels past the bottom edge; shown/peeked puts the whole 48 px on it.
    static bool TrayOut()
    {
        return TrayVisiblePixels() > 4;
    }

    // How many pixels of the tray window the user can see (0 when it is tucked
    // under the bottom edge, and for any taskbar that is not on the bottom).
    static int TrayVisiblePixels()
    {
        IntPtr t = FindWindow("Shell_TrayWnd", null);
        if (t == IntPtr.Zero) return 0;
        RECT r;
        if (!GetWindowRect(t, out r)) return 0;

        MONITORINFO mi;
        if (!PrimaryMonitor(out mi)) return 0;
        if (r.Top < mi.rcMonitor.Top + (mi.rcMonitor.Bottom - mi.rcMonitor.Top) / 2)
            return 0;                                       // not a bottom taskbar
        int visible = Math.Min(r.Bottom, mi.rcMonitor.Bottom) - Math.Max(r.Top, mi.rcMonitor.Top);
        return visible > 0 ? visible : 0;
    }

    // Is the pointer on the taskbar (or on the bottom edge where an auto-hidden
    // one slides out)? While it is, the tray being on screen is the user's own
    // peek and must not be fought.
    static bool PointerOnTray()
    {
        POINT p;
        if (!GetCursorPos(out p)) return false;
        MONITORINFO mi;
        if (!PrimaryMonitor(out mi)) return false;

        int bottom = mi.rcMonitor.Bottom;
        int top = bottom - 48;
        IntPtr t = FindWindow("Shell_TrayWnd", null);
        RECT r;
        if (t != IntPtr.Zero && GetWindowRect(t, out r))
        {
            int h = r.Bottom - r.Top;
            if (h >= 20 && h <= 120) top = bottom - h;
            if (TrayOut()) top = r.Top;
        }
        return p.Y >= top - 2 && p.Y <= bottom;
    }

    static bool PrimaryMonitor(out MONITORINFO mi)
    {
        mi = new MONITORINFO();
        mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        return GetMonitorInfo(MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY), ref mi);
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
