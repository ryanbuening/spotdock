namespace SpotDock;

/// <summary>
/// Interface strings. The app ships in English only.
/// </summary>
internal static class L
{
    public const string AppTitle = "SpotDock";
    public const string IdleTitle = "SpotDock";

    // Menu
    public static string MenuHeader(Version v) =>
        $"{AppTitle} v{v.Major}.{v.Minor}.{(v.Build >= 0 ? v.Build : 0)}";
    public static string OpacityMenu => "Brightness";
    public static string ShowArt => "Show cover art";
    public static string StretchText => "Stretch text";
    public static string ProgressBar => "Progress bar";
    public static string AutoStart => "Start with Windows";
    public static string CheckUpdates => "Check for updates";
    public static string Exit => "Quit";

    // States
    public static string NothingPlaying => "nothing playing";

    // Updates
    public static string UpdateAvailable(Version v) => $"⬤ Update to v{v}";
    public static string UpdateLatest(Version v) => $"You're on the latest version (v{v}).";
    public static string UpdateNotConfigured(Version v) =>
        $"Current version: v{v}\n\nAutomatic updates are turned off in this build.";
    public static string UpdatePrompt(Version latest, Version current) =>
        $"New version v{latest} available (current: v{current}).\n\nUpdate now? The widget restarts by itself.";
    public static string UpdateError(string message) => "Could not check for updates: " + message;
}
