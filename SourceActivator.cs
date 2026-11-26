using System.Diagnostics;

namespace SpotDock;

/// <summary>
/// Reveals the app that is currently making sound. The system session (SMTC)
/// only reports an AUMID string such as "Spotify.exe" or
/// "AyuGram.AyuGramDesktop.08bde0d8..." - that is what we use to find the
/// window.
/// </summary>
internal static class SourceActivator
{
    public static bool Activate(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return false;

        // Store apps may not show their windows in the enumeration (a
        // different host process), but they do activate by AUMID through the
        // shell. A PWA, though, opens a NEW instance on every such launch -
        // the widget then ends up with two Spotifys - so the window that is
        // already running is raised first.
        if (appId.Contains('!'))
        {
            IntPtr running = FindPackagedWindow(appId);
            if (running != IntPtr.Zero)
            {
                if (!Interop.IsWindowVisible(running))
                    Interop.ShowWindow(running, Interop.SW_SHOW);
                if (Interop.IsIconic(running))
                    Interop.ShowWindow(running, Interop.SW_RESTORE);
                BringToFront(running);
                // Handled even if Windows refused the foreground change: a
                // fallback launch here would create the second instance we are
                // trying to avoid
                return Interop.IsWindowVisible(running);
            }

            try
            {
                using var _ = Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + appId)
                {
                    UseShellExecute = true,
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        IntPtr hwnd = FindWindowFor(appId);
        if (hwnd == IntPtr.Zero) return false;

        // A window minimized to the tray is hidden, not minimized - show it first
        if (!Interop.IsWindowVisible(hwnd))
            Interop.ShowWindow(hwnd, Interop.SW_SHOW);
        if (Interop.IsIconic(hwnd))
            Interop.ShowWindow(hwnd, Interop.SW_RESTORE);

        // The same trick as for the browser: a simulated Alt sends a key into
        // someone else's window (in some apps it focuses the menu), while
        // attaching the input thread sends nothing anywhere
        bool ok = BringToFront(hwnd);
        if (Interop.IsWindowVisible(hwnd)) return ok;

        return ShowBySecondLaunch(hwnd) || ok;
    }

    /// <summary>
    /// Last attempt to open the window of a program that keeps it hidden
    /// itself. For AIMP minimized to the tray nothing in ShowWindow helps -
    /// verified with SW_SHOW, SW_SHOWNORMAL and SW_RESTORE: the window stays
    /// hidden even though it becomes active. From the outside it looks as if
    /// clicking the title does nothing.
    ///
    /// What remains is to ask the program itself: almost all such apps run as a
    /// single instance, and a second launch makes the already running copy show
    /// its window while the new one exits at once (verified: only one process
    /// remains).
    ///
    /// This is done ONLY when the window is still hidden. Ordinary programs
    /// never get here, and they will not end up with a second copy.
    /// </summary>
    private static bool ShowBySecondLaunch(IntPtr hwnd)
    {
        try
        {
            Interop.GetWindowThreadProcessId(hwnd, out uint pid);
            using var proc = Process.GetProcessById((int)pid);
            string path = proc.MainModule?.FileName ?? "";
            if (path.Length == 0) return false;
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Diag.Once("activate-relaunch",
                "Asking the player to show its own window failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// An already running window for a packaged app. Activating an AUMID through
    /// the shell starts a NEW instance for a PWA, so the running one is looked up
    /// first: by the process name inside the AUMID (desktop builds, including
    /// ones hidden in the tray), then by the window title.
    /// </summary>
    private static IntPtr FindPackagedWindow(string appId)
    {
        IntPtr byProcess = FindWindowFor(appId);
        if (byProcess != IntPtr.Zero) return byProcess;

        foreach (string hint in TitleHints(appId))
        {
            IntPtr byTitle = FindWindowByTitle(hint);
            if (byTitle != IntPtr.Zero) return byTitle;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Words from the AUMID that may occur in the window title. The part after
    /// "!" is the app's own name ("SpotifyAB...!Spotify"), the package name
    /// carries the brand with a suffix ("SpotifyMusic"), and a few brands differ
    /// from the title - hence the alias table.
    /// </summary>
    private static IEnumerable<string> TitleHints(string appId)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int bang = appId.IndexOf('!');
        string inner = bang >= 0 ? appId[(bang + 1)..] : appId;
        if (inner.Length >= 4 && !inner.Equals("App", StringComparison.OrdinalIgnoreCase) &&
            seen.Add(inner))
            yield return inner;

        string package = bang >= 0 ? appId[..bang] : appId;
        foreach (string part in package.Split(new[] { '.', '_' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length >= 5 &&
                !part.Contains("wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase) &&
                seen.Add(part))
                yield return part;
        }

        foreach (var (needle, title) in TitleAliases)
            if (appId.Contains(needle, StringComparison.OrdinalIgnoreCase) && seen.Add(title))
                yield return title;
    }

    /// <summary>Brands whose package name does not appear in the window title.</summary>
    private static readonly (string Needle, string Title)[] TitleAliases =
    {
        ("spotify", "Spotify"),
    };

    /// <summary>
    /// First visible top-level window whose title contains the hint, preferring
    /// browser windows: a PWA runs inside the browser's process, and an
    /// Electron shell or a message window with a matching title is not the app.
    /// </summary>
    private static IntPtr FindWindowByTitle(string hint)
    {
        IntPtr browser = IntPtr.Zero, other = IntPtr.Zero;
        uint self = (uint)Environment.ProcessId;
        var names = new Dictionary<uint, string>();

        Interop.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!Interop.IsWindowVisible(hwnd)) return true;
                if (Interop.GetWindow(hwnd, Interop.GW_OWNER) != IntPtr.Zero) return true;
                int len = Interop.GetWindowTextLength(hwnd);
                if (len <= 0) return true;

                string title = Interop.GetWindowTextString(hwnd, len);
                if (title.IndexOf(hint, StringComparison.OrdinalIgnoreCase) < 0) return true;

                Interop.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == self) return true; // never our own widget window

                if (!names.TryGetValue(pid, out string? name))
                {
                    try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; }
                    catch { name = ""; }
                    names[pid] = name;
                }

                if (BrowserProcesses.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    if (browser == IntPtr.Zero) browser = hwnd;
                }
                else if (other == IntPtr.Zero) other = hwnd;
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        return browser != IntPtr.Zero ? browser : other;
    }

    /// <summary>Browser processes. The window class alone cannot tell them
    /// apart: Chrome_WidgetWin_1 also belongs to every Electron app (Claude,
    /// Discord).</summary>
    private static readonly string[] BrowserProcesses =
        { "browser", "chrome", "msedge", "opera", "brave", "vivaldi" };

    /// <summary>
    /// Raise the browser window with the tab that is making sound. The
    /// extension has already activated the tab and its title became the window
    /// title - that is what we use to find the right one among several
    /// windows. If no title matches, take the topmost browser window:
    /// EnumWindows goes top to bottom in Z-order.
    /// </summary>
    public static bool BringBrowserToFront(string tabTitle)
    {
        IntPtr byTitle = IntPtr.Zero, topmost = IntPtr.Zero;
        var names = new Dictionary<uint, string>();

        Interop.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!Interop.IsWindowVisible(hwnd)) return true;
                if (Interop.GetWindow(hwnd, Interop.GW_OWNER) != IntPtr.Zero) return true;
                int len = Interop.GetWindowTextLength(hwnd);
                if (len <= 0) return true;

                Interop.GetWindowThreadProcessId(hwnd, out uint pid);
                if (!names.TryGetValue(pid, out string? name))
                {
                    try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; }
                    catch { name = ""; }
                    names[pid] = name;
                }
                if (!BrowserProcesses.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;

                if (topmost == IntPtr.Zero) topmost = hwnd;
                if (tabTitle.Length > 0)
                {
                    string title = Interop.GetWindowTextString(hwnd, len);
                    if (title.StartsWith(tabTitle, StringComparison.Ordinal))
                    {
                        byTitle = hwnd;
                        return false; // found it - stop enumerating
                    }
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        IntPtr target = byTitle != IntPtr.Zero ? byTitle : IntPtr.Zero;
        // With a title in hand and no window carrying it, leave the browser
        // alone: the extension has already focused the right window, and the
        // "topmost browser window" fallback raised a different one (a PWA, for
        // instance, is titled after the app, not the tab)
        if (target == IntPtr.Zero && tabTitle.Length == 0) target = topmost;
        return target != IntPtr.Zero && BringToFront(target);
    }

    /// <summary>
    /// Bring a window to the foreground without taskbar flashing - a trick from
    /// Jarvis (skills/windows). First an honest SetForegroundWindow; if Windows
    /// refuses (the foreground lock, which is what causes the flashing), our
    /// input thread is attached to the thread of the window currently in front
    /// for the duration of the call, and the lock stops applying. Needs no
    /// administrator rights. Unlike a simulated Alt it sends nothing into the
    /// windows: a lone Alt focuses the menu button in a browser.
    /// </summary>
    public static bool BringToFront(IntPtr hwnd)
    {
        // Restore only a minimized window: SW_RESTORE on a fullscreen window
        // would bring it back to its normal size
        if (Interop.IsIconic(hwnd))
            Interop.ShowWindow(hwnd, Interop.SW_RESTORE);

        if (Interop.GetForegroundWindow() == hwnd) return true;
        if (Interop.SetForegroundWindow(hwnd)) return true;

        IntPtr fg = Interop.GetForegroundWindow();
        uint theirs = Interop.GetWindowThreadProcessId(fg, out _);
        uint ours = Interop.GetCurrentThreadId();
        if (theirs == 0 || theirs == ours)
            return Interop.SetForegroundWindow(hwnd);

        Interop.AttachThreadInput(ours, theirs, true);
        try
        {
            Interop.BringWindowToTop(hwnd);
            return Interop.SetForegroundWindow(hwnd);
        }
        finally
        {
            Interop.AttachThreadInput(ours, theirs, false);
        }
    }

    /// <summary>
    /// The main window of the process whose name occurs in the AUMID.
    /// Process.MainWindowHandle is no good here: for apps minimized to the tray
    /// (Telegram, AyuGram) it is zero.
    ///
    /// The absence of an owner is not enough. AIMP minimized to the tray has
    /// eighteen such windows, all hidden: the QFI popup, the info panel, the
    /// tray icon, IPC and DDE windows. What tells them apart is:
    ///
    ///     TAIMPQuickFileInfoWindow 'QFI'   480x260  TOOLWINDOW
    ///     TAIMPMainForm 'Aprile - ...'    1110x626  APPWINDOW CAPTION
    ///
    /// Service windows are marked WS_EX_TOOLWINDOW (they never appear on the
    /// taskbar), the main one is WS_EX_APPWINDOW. Their scores used to come out
    /// equal, the first one won, and the widget "showed the source" by
    /// activating an invisible tooltip window - from the outside the click
    /// simply did not work.
    /// </summary>
    private static IntPtr FindWindowFor(string appId)
    {
        IntPtr best = IntPtr.Zero;
        int bestScore = 0;
        long bestArea = 0;
        var names = new Dictionary<uint, string>();

        Interop.EnumWindows((hwnd, _) =>
        {
            try
            {
                // Dialogs and popups have an owner - we need the main window
                if (Interop.GetWindow(hwnd, Interop.GW_OWNER) != IntPtr.Zero) return true;

                Interop.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return true;

                if (!names.TryGetValue(pid, out string? name))
                {
                    try
                    {
                        using var proc = Process.GetProcessById((int)pid);
                        name = proc.ProcessName;
                    }
                    catch
                    {
                        name = "";
                    }
                    names[pid] = name;
                }

                // Short names would produce false matches inside a long AUMID
                if (name.Length < 3) return true;
                if (appId.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) return true;

                int ex = Interop.GetWindowLong(hwnd, Interop.GWL_EXSTYLE);
                if ((ex & Interop.WS_EX_TOOLWINDOW) != 0) return true;

                // Zero and one-pixel windows are internal message windows
                if (!Interop.GetWindowRect(hwnd, out var r)) return true;
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (r.Right - r.Left <= 1 || r.Bottom - r.Top <= 1) return true;

                int style = Interop.GetWindowLong(hwnd, Interop.GWL_STYLE);
                int score = 1;
                if ((ex & Interop.WS_EX_APPWINDOW) != 0) score += 8;
                if (Interop.IsWindowVisible(hwnd)) score += 4;
                if (Interop.GetWindowTextLength(hwnd) > 0) score += 2;
                if ((style & Interop.WS_CAPTION) == Interop.WS_CAPTION) score += 1;

                // On a tie the larger window wins: the main one is noticeably
                // bigger than the service windows
                if (score > bestScore || (score == bestScore && area > bestArea))
                {
                    bestScore = score;
                    bestArea = area;
                    best = hwnd;
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        return best;
    }
}
