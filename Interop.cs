using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SpotDock;

internal static unsafe class Interop
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int GWL_STYLE = -16;
    public const int WS_CAPTION = 0x00C00000;

    public const int SW_RESTORE = 9;
    public const int SW_MINIMIZE = 6;

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string? lpszWindow);

    /// <summary>Left edge (physical px) of the system icon area (clock, network...).</summary>
    public static int? GetTrayNotifyLeft(IntPtr tray)
    {
        IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify == IntPtr.Zero || !GetWindowRect(notify, out RECT r))
            return null;
        return r.Left;
    }

    /// <summary>Pixels of the taskbar currently on screen (to tell whether it is
    /// settled, hidden, or mid reveal/hide animation).
    /// Also returns the bottom of the "home" monitor and the bottom of the work
    /// area - the difference between the two is the REAL drawn height of the bar
    /// (the appbar reservation), valid for any bar height; with auto-hide there is
    /// no reservation and the caller falls back to the 48 DIP heuristic.</summary>
    public static int GetTaskbarVisiblePx(RECT trayRect, out int monitorBottomPx, out int workAreaBottomPx)
    {
        IntPtr mon = GetTrayMonitor(trayRect);
        var mi = new MONITORINFO { cbSize = sizeof(MONITORINFO) };
        if (!GetMonitorInfo(mon, ref mi))
        {
            monitorBottomPx = trayRect.Bottom;
            workAreaBottomPx = trayRect.Bottom;
            return trayRect.Bottom - trayRect.Top; // no info: assume settled
        }
        monitorBottomPx = mi.rcMonitor.Bottom;
        workAreaBottomPx = mi.rcWork.Bottom;
        return Math.Min(trayRect.Bottom, mi.rcMonitor.Bottom) - Math.Max(trayRect.Top, mi.rcMonitor.Top);
    }

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // Window enumeration is needed to find the app that is the source of the
    // sound by its identifier from the system session (SMTC only gives an AUMID string)
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    /// <summary>Window owner: dialogs and popups have one, the app's main
    /// window does not.</summary>
    public const uint GW_HWNDPREV = 3;
    public const uint GW_OWNER = 4;

    public const int SW_SHOW = 5;
    public const int SW_SHOWNOACTIVATE = 4;

    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    // Clipping state PER WINDOW: avoid calling SetWindowRgn when the clip rect
    // has not changed (SetWindowRgn forces DWM to re-clip the layered surface).
    private static readonly Dictionary<IntPtr, (int Width, int VisibleHeight)> _clipState = new();

    /// <summary>Clips the window to what fits above the bottom of the screen while
    /// the taskbar slides (auto-hide). Without clipping, the part that already
    /// "left" kept drawing - visible on a monitor arranged below.
    /// The system owns the region after SetWindowRgn (do not free it).</summary>
    public static void ClipWindowBottom(IntPtr hwnd, int widthPx, int heightPx, int visibleHeightPx)
    {
        if (visibleHeightPx >= heightPx)
        {
            if (_clipState.Remove(hwnd))
                SetWindowRgn(hwnd, IntPtr.Zero, false);
            return;
        }
        int clamped = Math.Max(0, visibleHeightPx);
        if (_clipState.TryGetValue(hwnd, out var prev) && prev.Width == widthPx && prev.VisibleHeight == clamped)
            return;
        IntPtr rgn = CreateRectRgn(0, 0, widthPx, clamped);
        if (rgn == IntPtr.Zero) return;
        if (SetWindowRgn(hwnd, rgn, false) == 0)
            DeleteObject(rgn);
        else
            _clipState[hwnd] = (widthPx, clamped);
    }

    public static void ForgetClippedWindow(IntPtr hwnd) => _clipState.Remove(hwnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowTextW(IntPtr hWnd, char* lpString, int nMaxCount);

    public static string GetWindowTextString(IntPtr hWnd, int knownLength)
    {
        int cap = Math.Clamp(knownLength + 1, 2, 1024);
        char* buf = stackalloc char[cap];
        int n = GetWindowTextW(hWnd, buf, cap);
        return n > 0 ? new string(buf, 0, Math.Min(n, cap - 1)) : "";
    }

    /// <summary>Attach our thread's input queue to another. While attached the
    /// thread counts as "the same" as the one holding the foreground, and
    /// SetForegroundWindow succeeds where Windows otherwise forbids it.</summary>
    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    /// <summary>Keep a library in memory until the process ends, no matter how
    /// many times it is released.</summary>
    public const uint GET_MODULE_HANDLE_EX_FLAG_PIN = 0x00000001;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetModuleHandleEx(uint dwFlags, string lpModuleName, out IntPtr phModule);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public int lParam;
    }

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    /// <summary>True if taskbar auto-hide is on. In that mode maximized windows
    /// cover the whole screen and "look" fullscreen - the fullscreen test stops
    /// being reliable (and is redundant: the widget's visibility already follows
    /// the bar's).</summary>
    public static bool IsAutoHideEnabled()
    {
        var data = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA) };
        return ((ulong)SHAppBarMessage(4 /*ABM_GETSTATE*/, ref data) & 1 /*ABS_AUTOHIDE*/) != 0;
    }

    public const byte VK_SHIFT = 0x10;
    public const byte VK_MENU = 0x12;
    public const byte VK_B = 0x42;
    public const byte VK_VOLUME_DOWN = 0xAE;
    public const byte VK_VOLUME_UP = 0xAF;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>Steps Windows master volume using native media keys (2% per step),
    /// triggering the built-in Windows volume OSD.</summary>
    public static void StepSystemVolume(int steps)
    {
        if (steps == 0) return;
        byte vk = steps > 0 ? VK_VOLUME_UP : VK_VOLUME_DOWN;
        int count = Math.Min(Math.Abs(steps), 10);
        for (int i = 0; i < count; i++)
        {
            keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static extern int GetClassNameW(IntPtr hWnd, char* lpClassName, int nMaxCount);

    [ThreadStatic]
    private static char[]? _classNameBuf;

    private static ReadOnlySpan<char> GetClassNameSpan(IntPtr hwnd)
    {
        var buf = _classNameBuf ??= new char[256];
        int len;
        fixed (char* p = buf)
            len = GetClassNameW(hwnd, p, buf.Length);
        return len > 0 ? buf.AsSpan(0, Math.Min(len, buf.Length)) : ReadOnlySpan<char>.Empty;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    /// <summary>The taskbar's "home" monitor. With auto-hide the bar's rect slides
    /// OFF screen - if a monitor is arranged below, MonitorFromWindow returns that
    /// neighbour and the visibility maths run against the wrong screen. The point
    /// just above the bar does not suffer from that.</summary>
    private static IntPtr GetTrayMonitor(RECT trayRect)
    {
        var pt = new POINT { X = (trayRect.Left + trayRect.Right) / 2, Y = trayRect.Top - 10 };
        return MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
    }

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public const int GWLP_HWNDPARENT = -8;

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>Allows another process to bring its window forward. Without
    /// this Windows does not let the browser rise on a command from outside -
    /// the window only flashes on the taskbar. ASFW_ANY lifts the restriction
    /// for any process.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AllowSetForegroundWindow(int dwProcessId);

    public const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>True if hwnd is in the TOPMOST band and already above target in
    /// Z-order (so SetWindowPos(HWND_TOPMOST) can be skipped).</summary>
    public static bool IsAboveWindow(IntPtr hwnd, IntPtr target)
    {
        if (hwnd == IntPtr.Zero) return false;
        if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0) return false;
        if (target == IntPtr.Zero) return true;
        IntPtr cur = hwnd;
        for (int i = 0; i < 512; i++)
        {
            cur = GetWindow(cur, GW_HWNDPREV);
            if (cur == IntPtr.Zero) return true;
            if (cur == target) return false;
        }
        return false;
    }

    public static void EnsureTopmost(IntPtr hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    private const uint SWP_NOZORDER = 0x0004;

    /// <summary>Moves the window to PHYSICAL coordinates (px). Positioning in raw px
    /// avoids the wrong DIP maths between monitors with different scaling.</summary>
    public static void MoveWindowTo(IntPtr hwnd, int xPx, int yPx) =>
        SetWindowPos(hwnd, IntPtr.Zero, xPx, yPx, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    // System event hook: react the instant the active window changes
    // (clicking the taskbar brings the bar above the widget until we re-assert)
    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private const int DWMWA_CLOAKED = 14;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    /// <summary>A window that really shows something: visible, not minimized,
    /// without an owner, and not "cloaked" according to DWM. The last one
    /// matters: Store apps have plenty of windows that IsWindowVisible reports
    /// as visible even though they are not on screen - without this check one
    /// of those ended up as the topmost window on the monitor.</summary>
    private static bool IsRealWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;
        if (GetWindowRect(hwnd, out RECT r) && (r.Right <= r.Left || r.Bottom <= r.Top)) return false;
        return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    /// <summary>Shell windows that float above everything and do not count as
    /// screen content: they must be SKIPPED when looking for the topmost
    /// window, not treated as a stop. Otherwise one taskbar in the way is
    /// enough for the check to decide "the monitor is free" and look no
    /// further - which is exactly what happened after the second monitor was
    /// disconnected, while its taskbar was still alive and landed on the
    /// remaining screen by coordinates.</summary>
    private static bool IsShellWindow(IntPtr hwnd)
    {
        var cls = GetClassNameSpan(hwnd);
        return cls.SequenceEqual("Shell_TrayWnd")
            || cls.SequenceEqual("Shell_SecondaryTrayWnd")
            || cls.SequenceEqual("XamlExplorerHostIslandWindow");
    }

    /// <summary>This window covers the whole monitor and belongs to an app,
    /// not to the shell.</summary>
    private static bool CoversMonitorAsApp(IntPtr hwnd, IntPtr monitor)
    {
        var cls = GetClassNameSpan(hwnd);
        // The desktop can reach this point too: if it is the topmost one,
        // nothing at all is open on the monitor
        if (cls.SequenceEqual("Progman")
            || cls.SequenceEqual("WorkerW")
            || cls.SequenceEqual("Shell_TrayWnd")
            || cls.SequenceEqual("Shell_SecondaryTrayWnd")
            || cls.SequenceEqual("XamlExplorerHostIslandWindow"))
            return false;

        if (!GetWindowRect(hwnd, out RECT wr))
            return false;

        var mi = new MONITORINFO { cbSize = sizeof(MONITORINFO) };
        if (!GetMonitorInfo(monitor, ref mi))
            return false;

        bool coversMonitor = wr.Left <= mi.rcMonitor.Left && wr.Top <= mi.rcMonitor.Top
            && wr.Right >= mi.rcMonitor.Right && wr.Bottom >= mi.rcMonitor.Bottom;
        if (!coversMonitor)
            return false;

        // Covers the whole screen. These classes serve both harmless shell overlays
        // (Start menu, emoji/dictation, screen snip) and fullscreen UWP games
        // (Store Forza, issue #5) - tell them apart by the process.
        // The process lookup is paid ONLY here: windows that do not cover the
        // screen never reach this point (it used to run for any focused UWP app).
        if (cls.SequenceEqual("Windows.UI.Core.CoreWindow")
            || cls.SequenceEqual("ApplicationFrameWindow"))
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                if (p.ProcessName is "explorer" or "StartMenuExperienceHost" or "SearchHost"
                    or "ShellExperienceHost" or "ShellHost" or "SearchApp" or "SearchUI"
                    or "Cortana" or "LockApp" or "TextInputHost" or "ScreenClippingHost")
                    return false;
            }
            catch
            {
                return false; // process already died: treat as shell (harmless)
            }
        }

        return true;
    }

    [ThreadStatic] private static IntPtr _fsMonTray;
    [ThreadStatic] private static long _fsMonArea;
    [ThreadStatic] private static uint _fsOurs;
    [ThreadStatic] private static IntPtr _fsTop;
    private static readonly EnumWindowsProc FullscreenEnumProc = FullscreenEnumCallback;

    private static bool FullscreenEnumCallback(IntPtr hwnd, IntPtr _)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == _fsOurs) return true; // the widget's own windows do not count
        if (!IsRealWindow(hwnd)) return true;
        if (IsShellWindow(hwnd)) return true; // skip taskbars, keep looking

        // Service overlays covering the screen are not an app. The NVIDIA
        // overlay (CEF-OSC-WIDGET) always holds an invisible 1920x1080
        // window; while ordinary windows are on screen it lies beneath them
        // and does no harm, but after Win+D or with a small window on an
        // empty desktop it ended up as the topmost "large" one - and the
        // widget hid as if from a game. Games and videos are never
        // tool windows and never refuse activation
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((ex & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return true;
        if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) != _fsMonTray) return true;
        if (!GetWindowRect(hwnd, out RECT wr)) return true;

        // Small always-on-top windows - icons, tooltips, third-party
        // utilities - do not occupy the monitor, and what lies beneath them
        // has to be examined. The search used to stop at the first thing it
        // found: on a single monitor a 36x45 window hung on top, the check
        // decided "there is no fullscreen app", and the widget popped up
        // over the playing video
        long area = (long)(wr.Right - wr.Left) * (wr.Bottom - wr.Top);
        if (area * 2 < _fsMonArea) return true;

        _fsTop = hwnd;
        return false;
    }

    /// <summary>
    /// True if this taskbar's monitor is completely covered by someone else's
    /// window - a fullscreen video, a game. The widget then hides.
    ///
    /// It used to look only at the ACTIVE window, and that was not enough:
    /// leave a fullscreen video for another monitor and the window there
    /// became active, the check stopped firing, and the widget popped up over
    /// the video (while the taskbar itself stayed hidden). So we take the
    /// topmost window in Z-order ON ITS OWN monitor, whoever is active.
    /// </summary>
    public static bool IsMonitorCoveredByFullscreen(IntPtr self, IntPtr tray)
    {
        IntPtr monTray = GetWindowRect(tray, out RECT tr)
            ? GetTrayMonitor(tr)
            : MonitorFromWindow(tray, MONITOR_DEFAULTTONEAREST);
        if (monTray == IntPtr.Zero)
            return false;

        var mi = new MONITORINFO { cbSize = sizeof(MONITORINFO) };
        if (!GetMonitorInfo(monTray, ref mi))
            return false;
        long monArea = (long)(mi.rcMonitor.Right - mi.rcMonitor.Left)
                     * (mi.rcMonitor.Bottom - mi.rcMonitor.Top);

        GetWindowThreadProcessId(self, out uint ours);

        _fsMonTray = monTray;
        _fsMonArea = monArea;
        _fsOurs = ours;
        _fsTop = IntPtr.Zero;
        // EnumWindows goes top to bottom in Z-order, so the first window that
        // actually occupies the monitor with itself is the deciding one
        EnumWindows(FullscreenEnumProc, IntPtr.Zero);

        IntPtr top = _fsTop;
        return top != IntPtr.Zero && CoversMonitorAsApp(top, monTray);
    }

    // ---------- Lightweight process presence checks (zero-LOH) ----------

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint STILL_ACTIVE = 259;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        public fixed char szExeFile[260];
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, char* lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [ThreadStatic]
    private static char[]? _imageNameBuf;

    public static bool IsProcessAlive(uint pid, string expectedExeName)
    {
        if (pid == 0) return false;
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            if (!GetExitCodeProcess(h, out uint code) || code != STILL_ACTIVE)
                return false;
            var buf = _imageNameBuf ??= new char[512];
            uint len = (uint)buf.Length;
            fixed (char* p = buf)
            {
                if (!QueryFullProcessImageNameW(h, 0, p, ref len) || len == 0)
                    return false;
            }
            ReadOnlySpan<char> full = buf.AsSpan(0, (int)Math.Min(len, buf.Length));
            int slash = full.LastIndexOfAny('\\', '/');
            ReadOnlySpan<char> fileName = slash >= 0 ? full[(slash + 1)..] : full;
            return fileName.Equals(expectedExeName, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static uint FindProcessIdByName(string exeName)
    {
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE || snap == IntPtr.Zero)
            return 0;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };
            if (!Process32First(snap, ref entry))
                return 0;
            do
            {
                ReadOnlySpan<char> name = new ReadOnlySpan<char>(entry.szExeFile, 260);
                int nul = name.IndexOf('\0');
                if (nul >= 0) name = name[..nul];
                if (name.Equals(exeName, StringComparison.OrdinalIgnoreCase))
                    return entry.th32ProcessID;
            }
            while (Process32Next(snap, ref entry));
            return 0;
        }
        finally
        {
            CloseHandle(snap);
        }
    }

    // ---------- NativeAOT Registry, Theme, Encoding, and MessageBox helpers ----------

    public static readonly HKEY HKEY_CURRENT_USER = new(unchecked((nint)0x80000001));

    public static int? RegGetDword(HKEY root, string subKey, string valueName)
    {
        HKEY key = default;
        fixed (char* pSub = subKey)
        fixed (char* pVal = valueName)
        {
            if (PInvoke.RegOpenKeyEx(root, pSub, 0, REG_SAM_FLAGS.KEY_READ, &key) != WIN32_ERROR.ERROR_SUCCESS)
                return null;
            try
            {
                uint type = 0;
                int data = 0;
                uint size = sizeof(int);
                if (PInvoke.RegQueryValueEx(key, pVal, null, (REG_VALUE_TYPE*)&type, (byte*)&data, &size) == WIN32_ERROR.ERROR_SUCCESS
                    && type == (uint)REG_VALUE_TYPE.REG_DWORD && size == sizeof(int))
                {
                    return data;
                }
                return null;
            }
            finally
            {
                PInvoke.RegCloseKey(key);
            }
        }
    }

    public static bool RegValueExists(HKEY root, string subKey, string valueName)
    {
        HKEY key = default;
        fixed (char* pSub = subKey)
        fixed (char* pVal = valueName)
        {
            if (PInvoke.RegOpenKeyEx(root, pSub, 0, REG_SAM_FLAGS.KEY_READ, &key) != WIN32_ERROR.ERROR_SUCCESS)
                return false;
            try
            {
                return PInvoke.RegQueryValueEx(key, pVal, null, null, null, null) == WIN32_ERROR.ERROR_SUCCESS;
            }
            finally
            {
                PInvoke.RegCloseKey(key);
            }
        }
    }

    public static void RegSetString(HKEY root, string subKey, string valueName, string value)
    {
        HKEY key = default;
        fixed (char* pSub = subKey)
        fixed (char* pVal = valueName)
        fixed (char* pData = value)
        {
            if (PInvoke.RegCreateKeyEx(root, pSub, 0, default, REG_OPEN_CREATE_OPTIONS.REG_OPTION_NON_VOLATILE,
                    REG_SAM_FLAGS.KEY_WRITE, null, &key, null) != WIN32_ERROR.ERROR_SUCCESS)
                return;
            try
            {
                uint bytes = (uint)((value.Length + 1) * sizeof(char));
                PInvoke.RegSetValueEx(key, pVal, 0, REG_VALUE_TYPE.REG_SZ, (byte*)pData, bytes);
            }
            finally
            {
                PInvoke.RegCloseKey(key);
            }
        }
    }

    public static void RegDeleteValue(HKEY root, string subKey, string valueName)
    {
        HKEY key = default;
        fixed (char* pSub = subKey)
        fixed (char* pVal = valueName)
        {
            if (PInvoke.RegOpenKeyEx(root, pSub, 0, REG_SAM_FLAGS.KEY_SET_VALUE, &key) != WIN32_ERROR.ERROR_SUCCESS)
                return;
            try
            {
                PInvoke.RegDeleteValue(key, pVal);
            }
            finally
            {
                PInvoke.RegCloseKey(key);
            }
        }
    }

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppModeNative(int preferredAppMode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemesNative();

    public static void ApplyMenuTheme(bool light)
    {
        try
        {
            // 2 = ForceDark, 3 = ForceLight
            SetPreferredAppModeNative(light ? 3 : 2);
            FlushMenuThemesNative();
        }
        catch { }
    }

    public static byte[] ToOemBytes(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<byte>();
        const uint CP_OEMCP = 1;
        int len = PInvoke.WideCharToMultiByte(CP_OEMCP, 0, text, text.Length, default, 0, null);
        if (len <= 0) return System.Text.Encoding.ASCII.GetBytes(text);
        var bytes = new byte[len];
        fixed (byte* pb = bytes)
            PInvoke.WideCharToMultiByte(CP_OEMCP, 0, text, text.Length, (PSTR)pb, len, null);
        return bytes;
    }

    public static MESSAGEBOX_RESULT ShowMessageBox(IntPtr owner, string text, string caption, MESSAGEBOX_STYLE flags)
    {
        fixed (char* pText = text)
        fixed (char* pCap = caption)
            return PInvoke.MessageBox((HWND)owner, pText, pCap, flags);
    }
}
