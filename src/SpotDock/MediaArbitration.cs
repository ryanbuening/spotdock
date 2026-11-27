namespace SpotDock;

public sealed record TrackInfo(string Title, string Artist, bool IsPlaying,
    TimeSpan Position, TimeSpan Duration, DateTime PositionAtUtc);

/// <summary>
/// Pure, platform-independent arbitration helpers shared by <see cref="MediaHub"/>
/// so source selection and Spotify app-ID classification can be unit-tested directly.
/// </summary>
internal static class MediaArbitration
{
    public static bool IsSpotifyPwaAppId(string? appId) =>
        (appId ?? "").Contains("open.spotify.com", StringComparison.OrdinalIgnoreCase);

    public static bool IsSpotifyDesktopAppId(string? appId) =>
        (appId ?? "").Contains("spotify", StringComparison.OrdinalIgnoreCase) &&
        !IsSpotifyPwaAppId(appId);

    /// <summary>Which source is in charge right now. A playing source beats a
    /// paused one; on a tie - the system session, because desktop players give
    /// more (artwork, favorites).</summary>
    public static bool PreferBrowser(TrackInfo? smtc, BrowserState? browser)
    {
        bool smtcOk = smtc != null && !string.IsNullOrWhiteSpace(smtc.Title);
        bool browserOk = browser != null && !string.IsNullOrWhiteSpace(browser.Title);
        if (!browserOk) return false;
        if (!smtcOk) return true;
        if (smtc!.IsPlaying) return false;
        return browser!.Playing;
    }

    public static bool SamePlayback(TrackInfo? smtc, BrowserState? browser) =>
        smtc != null && browser != null &&
        !string.IsNullOrWhiteSpace(smtc.Title) && !string.IsNullOrWhiteSpace(browser.Title) &&
        string.Equals(smtc.Title.Trim(), browser.Title.Trim(), StringComparison.OrdinalIgnoreCase);
}
