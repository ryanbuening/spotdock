using System.Buffers;
using System.IO;
using System.Net.Http;

namespace SpotDock;

/// <summary>
/// A single track source for the window: the system session (SMTC) plus the
/// browser extension through <see cref="BrowserBridge"/>. It exposes exactly
/// the same set of methods as <see cref="MediaService"/>, so the window does
/// not know where the data came from, and commands go back to wherever the
/// track was taken from.
/// </summary>
public sealed class MediaHub
{
    /// <summary>
    /// One per process. The widget has as many windows as there are selected
    /// taskbars, and the track source is shared by all of them.
    ///
    /// The hub used to be created per window — along with its own bridge
    /// listening on 127.0.0.1:45219. The second window no longer got the port:
    ///
    ///     Browser bridge could not listen on 127.0.0.1:45219:
    ///     only one usage of each socket address is normally permitted
    ///
    /// It looked as if the widget did not work on the second monitor: it
    /// showed the track from the system session but not music from the
    /// browser, because the extension had nowhere to reach. Also half the SMTC
    /// calls and a single artwork download for everyone.
    /// </summary>
    public static MediaHub Shared { get; } = new();

    private readonly MediaService _smtc = new();
    private readonly BrowserBridge _browser = new();

    // Five seconds were not enough: browser artwork takes about two even
    // directly, and noticeably longer through a proxy
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Fallback client, deliberately bypassing the proxy. HttpClient
    /// reads the system proxy settings ONCE, at creation, while the widget
    /// lives for weeks: enable a VPN, start the widget, then turn the VPN off -
    /// and until a restart it kept knocking on a dead port ("the remote
    /// computer refused the network connection, 127.0.0.1:2080") and artwork
    /// was simply missing. The retry happens only when the connection itself
    /// failed: if the proxy does not respond there is no tunnel to bypass.</summary>
    private readonly HttpClient _httpDirect =
        new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };

    private volatile bool _useBrowser;
    private volatile bool _browserActive;
    private volatile bool _browserTrackMatches;
    private const int MaxArtBytes = 8 * 1024 * 1024;
    private string _artUrl = "";
    private byte[]? _artBytes;
    private DateTime _artRetryAt;

    public void ClearArtCache()
    {
        _artUrl = "";
        _artBytes = null;
    }

    public event Action? Changed;
    public event Action? TimelineChanged;

    /// <summary>The track is currently taken from the browser.</summary>
    public bool UsingBrowser => _useBrowser;

    /// <summary>The track is currently taken from Spotify Web via the bridge,
    /// or the Spotify Web PWA SMTC session is backed by the bridge.</summary>
    public bool BrowserSourceActive => _browserActive;

    /// <summary>Favorite state in the tab: true/false, or null - the site does
    /// not report it (no button), and then the window shows a neutral plus.</summary>
    public bool? BrowserLiked => _browser.Current is { CanLike: true } b && _browserTrackMatches ? b.Liked : null;

    /// <summary>The tab has a favorite button - the window may show its own.</summary>
    public bool BrowserCanLike => _browserActive && _browser.Current is { CanLike: true };

    private static bool IsSpotifyPwaAppId(string? appId) =>
        (appId ?? "").Contains("open.spotify.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsSpotifyDesktopAppId(string? appId) =>
        (appId ?? "").Contains("spotify", StringComparison.OrdinalIgnoreCase) &&
        !IsSpotifyPwaAppId(appId);

    /// <summary>The active SMTC session belongs to Spotify Desktop (not a
    /// browser-hosted Spotify Web PWA).</summary>
    public bool IsSpotifySmtcSource =>
        !_useBrowser && IsSpotifyDesktopAppId(_smtc.SourceAppId);

    private bool IsSpotifyPwaSmtc =>
        !_useBrowser && IsSpotifyPwaAppId(_smtc.SourceAppId);

    private Task? _init;

    /// <summary>Started once, no matter how many windows ask: they are all
    /// created on one UI thread, so there is no race here, and each window can
    /// await readiness on its own.</summary>
    public Task InitializeAsync() => _init ??= InitializeOnceAsync();

    private async Task InitializeOnceAsync()
    {
        _smtc.Changed += () => Changed?.Invoke();
        _smtc.TimelineChanged += () => TimelineChanged?.Invoke();
        _browser.Changed += () => Changed?.Invoke();
        _browser.TimelineChanged += () => TimelineChanged?.Invoke();
        _browser.FocusReady += OnBrowserFocused;
        if (!TestSwitches.NoBridge) _browser.Start();
        if (!TestSwitches.NoSmtc) await _smtc.InitializeAsync();
    }

    public void Shutdown()
    {
        _smtc.Shutdown();
        _browser.Shutdown();
    }

    /// <summary>Which source is in charge right now. A playing source beats a
    /// paused one; on a tie - the system session, because desktop players give
    /// more (artwork, favorites).</summary>
    private static bool PreferBrowser(TrackInfo? smtc, BrowserState? browser)
    {
        bool smtcOk = smtc != null && !string.IsNullOrWhiteSpace(smtc.Title);
        bool browserOk = browser != null && !string.IsNullOrWhiteSpace(browser.Title);
        if (!browserOk) return false;
        if (!smtcOk) return true;
        if (smtc!.IsPlaying) return false;
        return browser!.Playing;
    }

    private static TrackInfo FromBrowser(BrowserState b) => new(
        b.Title, b.Artist, b.Playing,
        TimeSpan.FromSeconds(b.Position), TimeSpan.FromSeconds(b.Duration), b.At);

    public async Task<TrackInfo?> GetTrackAsync()
    {
        var smtc = await _smtc.GetTrackAsync();
        var browser = _browser.Current;
        _useBrowser = PreferBrowser(smtc, browser);
        bool pwaBridge = IsSpotifyPwaSmtc && browser != null;
        _browserActive = _useBrowser || pwaBridge;
        _browserTrackMatches = _useBrowser || (pwaBridge && SamePlayback(smtc, browser));
        if (!_useBrowser || browser == null || !string.Equals(browser.Art, _artUrl, StringComparison.Ordinal))
            ClearArtCache();
        return _useBrowser ? FromBrowser(browser!) : smtc;
    }

    private static bool SamePlayback(TrackInfo? smtc, BrowserState? browser) =>
        smtc != null && browser != null &&
        !string.IsNullOrWhiteSpace(smtc.Title) && !string.IsNullOrWhiteSpace(browser.Title) &&
        string.Equals(smtc.Title.Trim(), browser.Title.Trim(), StringComparison.OrdinalIgnoreCase);

    public (TimeSpan Position, TimeSpan Duration, bool IsPlaying, DateTime PositionAtUtc)? GetTimeline()
    {
        if (_useBrowser)
        {
            var b = _browser.Current;
            if (b != null)
                return (TimeSpan.FromSeconds(b.Position), TimeSpan.FromSeconds(b.Duration), b.Playing, b.At);
        }
        return _smtc.GetTimeline();
    }

    /// <summary>Artwork: from SMTC it arrives as a stream, from the browser as
    /// a URL that we download and cache ourselves (the same URL for the whole
    /// track).</summary>
    public async Task<byte[]?> GetThumbnailAsync()
    {
        if (!_useBrowser) return await _smtc.GetThumbnailAsync();

        string url = _browser.Current?.Art ?? "";
        if (url.Length == 0) return null;
        // A failure is no longer remembered forever. A failed download used to
        // cache an empty value, the same URL was never requested again, and the
        // track stayed without artwork until the very end
        if (url == _artUrl && (_artBytes != null || DateTime.UtcNow < _artRetryAt))
            return _artBytes;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;

        try
        {
            var bytes = await DownloadArtAsync(uri);
            // Artwork is tens of kilobytes; there is no point downloading anything bigger
            if (bytes == null || bytes.Length > MaxArtBytes) return null;
            _artUrl = url;
            _artBytes = bytes;
            return bytes;
        }
        catch (Exception ex)
        {
            Diag.Once("art-download", "Downloading cover art from the browser failed: " + ex.Message);
            _artUrl = url;
            _artBytes = null;
            _artRetryAt = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            return null;
        }
    }

    /// <summary>Downloads artwork, retrying past the proxy when the connection
    /// is refused - see <see cref="_httpDirect"/>.</summary>
    private async Task<byte[]?> DownloadArtAsync(Uri uri)
    {
        try
        {
            return await DownloadArtWithClientAsync(_http, uri);
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException)
        {
            return await DownloadArtWithClientAsync(_httpDirect, uri);
        }
    }

    private static async Task<byte[]?> DownloadArtWithClientAsync(HttpClient client, Uri uri)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        long? contentLength = response.Content.Headers.ContentLength;
        if (contentLength > MaxArtBytes) return null;

        await using var stream = await response.Content.ReadAsStreamAsync();
        int initialCap = contentLength is > 0 and <= MaxArtBytes ? (int)contentLength.Value : 0;
        using var ms = new MemoryStream(initialCap);
        byte[] buf = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buf)) > 0)
            {
                if (ms.Length + read > MaxArtBytes) return null;
                ms.Write(buf, 0, read);
            }
            return ms.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_useBrowser) _browser.Send("playpause");
        else await _smtc.TogglePlayPauseAsync();
    }

    public async Task NextAsync()
    {
        if (_useBrowser) _browser.Send("next");
        else await _smtc.NextAsync();
    }

    public async Task PreviousAsync()
    {
        if (_useBrowser) _browser.Send("prev");
        else await _smtc.PreviousAsync();
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (_useBrowser) _browser.Send("seek", position.TotalSeconds);
        else await _smtc.SeekAsync(position);
    }

    /// <summary>Toggles the favorite in the tab through the site's own button -
    /// SMTC has no such action at all.</summary>
    public void ToggleBrowserLike()
    {
        if (_browserActive) _browser.Send("like");
    }

    /// <summary>The tab is activated - raise its window. Through the UI thread:
    /// AttachThreadInput requires a message queue on its own thread, and the
    /// socket thread has none. A short pause - the browser does not rewrite the
    /// window title to the tab title instantly, and the right window is found
    /// by that title.</summary>
    private static void OnBrowserFocused(string tabTitle)
    {
        _ = Task.Delay(120).ContinueWith(_ => UiThread.Post(() =>
        {
            try { SourceActivator.BringBrowserToFront(tabTitle); }
            catch (Exception ex) { Diag.Once("browser-front", "Bringing the browser to front failed: " + ex.Message); }
        }));
    }

    /// <summary>Reveal where the sound comes from: the Spotify Web tab through
    /// the extension, or the Spotify desktop app window from SMTC. Returns
    /// false if the source is not recognized - the window then falls back to
    /// focusing Spotify desktop.</summary>
    public bool FocusCurrentSource()
    {
        string appId = _smtc.SourceAppId ?? "";
        Diag.Log($"Focus source: browser={_useBrowser}, bridge={_browserActive}, appId='{appId}'");

        if (_useBrowser || (_browserActive && IsSpotifyPwaSmtc))
        {
            // Lift the foreground lock, otherwise Windows only lets the
            // browser flash on the taskbar
            try { Interop.AllowSetForegroundWindow(Interop.ASFW_ANY); } catch { }
            _browser.Send("focus");
            return true;
        }

        try { return SourceActivator.Activate(appId); }
        catch { return false; }
    }
}
