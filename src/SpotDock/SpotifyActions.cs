using System.Diagnostics;

namespace SpotDock;

internal static class SpotifyActions
{
    /// <summary>
    /// Locates the main top-level window of the running Spotify desktop process.
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
