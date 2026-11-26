using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpotDock;

[JsonSerializable(typeof(WidgetSettings))]
internal partial class WidgetSettingsJsonContext : JsonSerializerContext
{
}

public class WidgetSettings
{
    /// <summary>Kept for older versions - <see cref="ManualX"/> is what counts today.</summary>
    public bool AutoPosition { get; set; } = true;

    /// <summary>Kept for older versions - <see cref="ManualX"/> is what counts today.</summary>
    public double X { get; set; } = 150;

    /// <summary>Manual position on the primary taskbar (physical px), keyed by
    /// monitor (0 = primary). No entry means position automatically.</summary>
    public Dictionary<int, double> ManualX { get; set; } = new();

    /// <summary>Distance (physical px) between the RIGHT edge of the widget and
    /// the start of the notification area, keyed by monitor (0 = primary). This is
    /// what keeps the widget tracking the tray as it grows or shrinks (a new icon,
    /// the clock changing width). ManualX stays as the fallback for when the
    /// notification area cannot be read.</summary>
    public Dictionary<int, double> ManualGap { get; set; } = new();

    /// <summary>
    /// Right edge of the widgets (weather) button, remembered per taskbar (0 = primary).
    ///
    /// Needed when the taskbar stops drawing it: Windows creates that button
    /// lazily, so until it is first opened it simply does not exist on the
    /// taskbar. Without the memory the widget parked at the very edge and
    /// covered the spot you have to click to make the button appear - leaving
    /// no way to open it. With the remembered edge that spot stays free.
    /// </summary>
    public Dictionary<int, double> WidgetsAnchor { get; set; } = new();

    /// <summary>Idle widget brightness/opacity (0.5 = 50% default; brightens to
    /// 1.0 on hover).</summary>
    public double Opacity { get; set; } = 0.5;

    /// <summary>Show the album cover to the left of the title.</summary>
    public bool ShowArt { get; set; } = true;

    /// <summary>The text column takes all the free space on the taskbar: the
    /// widget stretches from the left anchor to the Start button, instead of
    /// stopping at the width ceiling and leaving an empty strip.</summary>
    public bool StretchText { get; set; } = false;

    /// <summary>Track progress bar at the bottom of the widget (click to seek).</summary>
    public bool ShowProgress { get; set; } = true;

    /// <summary>Schema version of this file. Bumped whenever a default changes
    /// in a way an existing file has to adopt: settings written by an older
    /// build keep their own values otherwise, which looked like the menu doing
    /// nothing at all.</summary>
    public int SettingsVersion { get; set; }

    private static readonly object SaveLock = new();

    private static string Dir => AppPaths.DataDir;

    private static string FilePath => Path.Combine(Dir, "settings.json");

    /// <summary>Single instance shared by every widget window.</summary>
    public static WidgetSettings Shared => _shared ??= Load();
    private static WidgetSettings? _shared;

    /// <summary>Fired after every Save - the other windows re-apply their UI.</summary>
    public static event Action? Changed;

    public static WidgetSettings Load() => LoadFrom(FilePath);

    internal static WidgetSettings LoadFrom(string filePath)
    {
        WidgetSettings s;
        try
        {
            s = JsonSerializer.Deserialize(File.ReadAllText(filePath), WidgetSettingsJsonContext.Default.WidgetSettings) ?? new WidgetSettings();
            // A hand-edited / half-written file can carry null fields -
            // normalize INSIDE the try: broken settings must not block startup
            // (it left an invisible process running forever)
            if (s.ManualX is null) s.ManualX = new Dictionary<int, double>();
            if (s.ManualGap is null) s.ManualGap = new Dictionary<int, double>();
            if (s.WidgetsAnchor is null) s.WidgetsAnchor = new Dictionary<int, double>();
        }
        catch
        {
            s = new WidgetSettings();
        }
        if (!s.AutoPosition && s.ManualX.Count == 0)
            s.ManualX[0] = s.X; // migrate the single manual position
        // v2: default idle brightness is now 50% (brightens to 100% on hover).
        // Existing settings files saved Opacity = 1.0, so migrate them once.
        if (s.SettingsVersion < 2)
        {
            s.Opacity = 0.5;
            s.SettingsVersion = 2;
        }
        // Never below 20%: broken settings with Opacity=0 made the widget
        // invisible with no way to click it back
        s.Opacity = Math.Clamp(s.Opacity, 0.2, 1.0);
        return s;
    }

    public void Save()
    {
        SaveTo(FilePath);
        Changed?.Invoke();
    }

    internal void SaveTo(string filePath)
    {
        // Mirrors of the old format, so a downgrade still reads something valid
        AutoPosition = !ManualX.ContainsKey(0);
        if (ManualX.TryGetValue(0, out double x))
            X = x;
        try
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            // Atomic write: save alongside and swap by rename - a crash or a power
            // cut halfway left a truncated JSON and Load silently reset ALL of the
            // user's settings
            string tmp = filePath + ".tmp";
            lock (SaveLock)
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, WidgetSettingsJsonContext.Default.WidgetSettings));
                File.Move(tmp, filePath, overwrite: true);
            }
        }
        catch { }
    }
}
