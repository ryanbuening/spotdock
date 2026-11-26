using System.Diagnostics;

namespace SpotDock;

internal static class SpotifyActions
{
    /// <summary>
    /// Adds/removes the current track from favorites by sending Spotify's
    /// official shortcut (Alt+Shift+B) to its window. The Windows media API
    /// does not expose "save to favorites", so Spotify has to be focused
    /// briefly. Run this on a background thread (it uses Sleep).
    /// </summary>
    internal static IntPtr FindSpotifyMainWindow()
    {
        var procs = Process.GetProcessesByName("Spotify");
        try
        {
            foreach (var proc in procs)
            {
                if (proc.MainWindowHandle != IntPtr.Zero)
                    return proc.MainWindowHandle;
            }
            return IntPtr.Zero;
        }
        finally
        {
            foreach (var proc in procs) proc.Dispose();
        }
    }

    public static void LikeCurrentTrack()
    {
        IntPtr spotify = FindSpotifyMainWindow();
        if (spotify == IntPtr.Zero) return;
        IntPtr previous = Interop.GetForegroundWindow();

        // While minimized the window may not process the shortcut - restore it briefly
        bool wasMinimized = Interop.IsIconic(spotify);
        if (wasMinimized)
        {
            Interop.ShowWindow(spotify, Interop.SW_RESTORE);
            Thread.Sleep(250);
        }

        // A "tap" on Alt lifts the SetForegroundWindow restriction
        Interop.keybd_event(Interop.VK_MENU, 0, 0, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_MENU, 0, Interop.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Interop.SetForegroundWindow(spotify);
        Thread.Sleep(150);

        Interop.keybd_event(Interop.VK_MENU, 0, 0, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_SHIFT, 0, 0, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_B, 0, 0, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_B, 0, Interop.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_SHIFT, 0, Interop.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_MENU, 0, Interop.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(200);

        if (wasMinimized)
            Interop.ShowWindow(spotify, Interop.SW_MINIMIZE);
        if (previous != IntPtr.Zero)
            Interop.SetForegroundWindow(previous);
    }

    public static void FocusRunningSpotifyWindow()
    {
        IntPtr spotify = FindSpotifyMainWindow();
        if (spotify != IntPtr.Zero)
        {
            Interop.ShowWindow(spotify, Interop.SW_RESTORE);
            Interop.SetForegroundWindow(spotify);
        }
    }
}
