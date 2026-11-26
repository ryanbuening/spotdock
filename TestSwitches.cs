using System.IO;

namespace SpotDock;

/// <summary>
/// TEMPORARY: switches for tracking down a native memory leak (2026-10-31).
/// The widget gains ~60 MB per hour while the managed heap stays at 10 MB, so
/// something native is leaking. Third-party libraries injected into the process
/// (RTSS, Windhawk, Bonjour) were ruled out by elimination, so the cause is in
/// our code - the remaining question is which part.
///
/// Enabled by a file next to the exe, the same way as bridge-debug.txt. Checked
/// once at startup; which switches fired is visible in errors.log.
///
/// Delete along with their call sites once the leak is found.
/// </summary>
internal static class TestSwitches
{
    private static bool Flag(string name) =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, name));

    /// <summary>Do not poll the taskbar through UI Automation.</summary>
    public static readonly bool NoUia = Flag("no-uia.txt");

    /// <summary>Do not look for a fullscreen window (window scan 5 times a second).</summary>
    public static readonly bool NoFullscreen = Flag("no-fullscreen.txt");

    /// <summary>Do not bring up the system media session.</summary>
    public static readonly bool NoSmtc = Flag("no-smtc.txt");

    /// <summary>Do not bring up the browser extension bridge.</summary>
    public static readonly bool NoBridge = Flag("no-bridge.txt");

    public static void LogActive()
    {
        var on = new System.Collections.Generic.List<string>();
        if (NoUia) on.Add("no-uia");
        if (NoFullscreen) on.Add("no-fullscreen");
        if (NoSmtc) on.Add("no-smtc");
        if (NoBridge) on.Add("no-bridge");
        if (on.Count > 0)
            Diag.Log("TEST SWITCHES ACTIVE: " + string.Join(", ", on));
    }
}
