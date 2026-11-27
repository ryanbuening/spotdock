namespace SpotDock;

/// <summary>
/// Interface strings. The app ships in English only.
/// </summary>
internal static class UiStrings
{
    public const string AppTitle = "SpotDock";
    public const string IdleTitle = "SpotDock";

    public static string FormatVersion(Version v)
    {
        int build = v.Build >= 0 ? v.Build : 0;
        return v.Revision > 0
            ? $"{v.Major}.{v.Minor}.{build}.{v.Revision}"
            : $"{v.Major}.{v.Minor}.{build}";
    }

    // Menu
    public static string MenuHeader(Version v) =>
        $"{AppTitle} v{FormatVersion(v)}";
    public static string OpacityMenu => "Brightness";
    public static string ShowArt => "Show cover art";
    public static string ProgressBar => "Progress bar";
    public static string AutoStart => "Start with Windows";
    public static string CheckUpdates => "Check for updates";
    public static string Exit => "Quit";

    // States
    public static string NothingPlaying => "nothing playing";

    // Updates
    public static string UpdateAvailable(Version v) => $"⬤ Update to v{FormatVersion(v)}";
    public static string UpdateLatest(Version v) => $"You're on the latest version (v{FormatVersion(v)}).";
    public static string UpdateNotConfigured(Version v) =>
        $"Current version: v{FormatVersion(v)}\n\nAutomatic updates are turned off in this build.";
    public static string UpdatePrompt(Version latest, Version current) =>
        $"New version v{FormatVersion(latest)} available (current: v{FormatVersion(current)}).\n\nUpdate now? The widget restarts by itself.";
    public static string UpdateError(string message) => "Could not check for updates: " + message;
}
