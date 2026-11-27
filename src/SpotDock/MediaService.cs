using Windows.Media.Control;
using Windows.Storage.Streams;

namespace SpotDock;

/// <summary>
/// Reads the current Spotify desktop track through the Windows media API (SMTC).
/// Only Spotify desktop sessions are watched — other players and browsers are
/// ignored.
///
/// Every call into WinRT goes through <see cref="WinRtThread"/> and nowhere
/// else - see the comment there for what happened when they did not.
/// </summary>
public sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    /// <summary>Fired (on a background thread) when the track or the state changes.</summary>
    public event Action? Changed;

    /// <summary>Fired only when position/duration change - happens every few
    /// seconds, so the handler has to stay light.</summary>
    public event Action? TimelineChanged;

    public async Task InitializeAsync()
    {
        // When starting with Windows, WinRT may not be ready yet - a transient
        // failure here left the widget with no track for the WHOLE session.
        // Retry with backoff (4s->64s, ~2 min) before giving up for good.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                _manager = await WinRtThread.Run(() => WinRtThread.Await(
                    GlobalSystemMediaTransportControlsSessionManager.RequestAsync(), 10000));
                if (_manager != null) break;
                throw new TimeoutException("RequestAsync did not complete in 10s.");
            }
            catch (Exception ex)
            {
                if (attempt >= 5)
                {
                    // No media session API (N editions without the Media Feature
                    // Pack, broken WinRT) - leave a trace for the report
                    Diag.Once("smtc-init", "Media session API unavailable (this is why nothing shows as playing): " + ex.Message);
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(4 << attempt));
            }
        }

        await WinRtThread.Run(() =>
        {
            // The callbacks arrive on RPC threads, so they only queue the work -
            // reading sessions from there is what used to scatter WinRT objects
            // across transient apartments
            _manager!.SessionsChanged += (_, _) => WinRtThread.Post(PickSession);
            // Without this a change of the "current" session went unnoticed:
            // the session list did not change (the tab/player was already
            // open), SessionsChanged stayed silent, and the widget stayed
            // bound to the previous session
            _manager.CurrentSessionChanged += (_, _) => WinRtThread.Post(PickSession);
            PickSession();
        });
    }

    /// <summary>Unsubscribes everything - without this a closed window stayed
    /// pinned in memory by the WinRT events and kept processing sessions.</summary>
    public void Shutdown()
    {
        // Short wait: this runs while the app is closing, and a session that
        // hangs must not hold the exit back
        try { WinRtThread.Run(() => { UnwatchAll(); Unsubscribe(Swap(null)); }).Wait(1500); } catch { }
    }

    /// <summary>Puts a new session in place and hands back the old one. Only
    /// ever called on the WinRT thread, and that is what serializes the swaps:
    /// two bursts of session events used to interleave and either duplicate
    /// subscriptions or leave handlers pinned to a dead session.</summary>
    private GlobalSystemMediaTransportControlsSession? Swap(GlobalSystemMediaTransportControlsSession? next)
    {
        var old = _session;
        _session = next;
        return old;
    }

    private void Unsubscribe(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session == null) return;
        try
        {
            session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        catch { }
    }

    /// <summary>
    /// The sessions whose state we watch. We must watch ALL of them, not just
    /// the one being shown.
    ///
    /// A stopped player does not make it into the selection (see
    /// HasSomethingToShow), and it used to have no subscription at all. When
    /// Play is pressed in it, the session list does not change - so
    /// SessionsChanged will not fire, and there is no way to learn that
    /// playback started. That is exactly how the widget missed AIMP starting
    /// and kept showing the browser tab.
    /// </summary>
    private readonly List<GlobalSystemMediaTransportControlsSession> _watched = new();

    private void Rewatch(IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        for (int i = _watched.Count - 1; i >= 0; i--)
        {
            var old = _watched[i];
            if (sessions.Contains(old)) continue;
            try { old.PlaybackInfoChanged -= OnAnyPlaybackInfoChanged; } catch { }
            _watched.RemoveAt(i);
        }
        foreach (var s in sessions)
        {
            if (_watched.Contains(s)) continue;
            try { s.PlaybackInfoChanged += OnAnyPlaybackInfoChanged; } catch { }
            _watched.Add(s);
        }
    }

    private void UnwatchAll()
    {
        foreach (var s in _watched)
        {
            try { s.PlaybackInfoChanged -= OnAnyPlaybackInfoChanged; } catch { }
        }
        _watched.Clear();
    }

    /// <summary>
    /// The libraries that implement the media session. They must be kept in
    /// memory.
    ///
    /// A WinRT object's method table is a COPY of pointers into that library's
    /// code. COM is free to unload the library as soon as no references to its
    /// objects remain, while the .NET-side reference lives until garbage
    /// collection and is released afterwards, in the finalizer - already
    /// pointing into the unloaded image. That is what crashed the process with
    /// an AccessViolation in WinRT.IObjectReference.Finalize() (a known CsWinRT
    /// defect, issue 337). The unloading moment depends on whether players are
    /// still present in the system, so it crashed at different times of day and
    /// never reproduced on the bench.
    ///
    /// The first library is the Windows.Media.Control implementation itself,
    /// the second is the marshaling stub: a session from another process comes
    /// through it, and the method tables of such objects live there.
    /// </summary>
    private static readonly string[] MediaModules =
        { "Windows.Media.MediaControl.dll", "OneCoreUAPCommonProxyStub.dll" };

    private static readonly HashSet<string> PinnedModules = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pins these libraries in memory. Called on every session
    /// selection rather than once: the marshaling stub is only loaded together
    /// with the first session from another process, which may not exist at
    /// startup.</summary>
    private static void PinMediaModules()
    {
        foreach (var name in MediaModules)
        {
            if (PinnedModules.Contains(name)) continue;
            try
            {
                // The library is not loaded yet - no problem, try next time:
                // there is no point loading it deliberately just to pin it
                if (Interop.GetModuleHandleEx(Interop.GET_MODULE_HANDLE_EX_FLAG_PIN, name, out _))
                {
                    PinnedModules.Add(name);
                    // One line per run: it shows that the unload protection is
                    // in place, and by what point
                    Diag.Log($"Pinned {name} in memory (so its vtables stay valid for the finalizer).");
                }
            }
            catch (Exception ex)
            {
                PinnedModules.Add(name); // do not try a second time
                Diag.Once("pin-module", $"Pinning {name} in memory failed: {ex.Message}");
            }
        }
    }

    private static GlobalSystemMediaTransportControlsSessionPlaybackStatus? Status(
        GlobalSystemMediaTransportControlsSession session)
    {
        try { return session.GetPlaybackInfo()?.PlaybackStatus; }
        catch { return null; }
    }

    /// <summary>
    /// A session that has something to show.
    ///
    /// After finishing, an app leaves its session in the Stopped state and it
    /// never goes away. The widget took the "current one according to Windows"
    /// without looking at the state, and a listened-to voice message in
    /// Telegram (AyuGram) hung on the taskbar for hours: "Voice message at
    /// 19:21", status = Stopped, zero duration. Closed is the same thing, the session is
    /// already closed.
    ///
    /// The intermediate Opened and Changing are deliberately considered valid:
    /// Changing flashes by on every track change, and the widget would blink
    /// empty.
    /// </summary>
    private static bool HasSomethingToShow(GlobalSystemMediaTransportControlsSession session) =>
        Status(session) is not (GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped
            or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed);

    private static bool IsSpotifySession(GlobalSystemMediaTransportControlsSession session) =>
        (ReadSourceAppId(session) ?? "").Contains("spotify", StringComparison.OrdinalIgnoreCase);

    private void PickSession()
    {
        if (_manager == null) return;
        PinMediaModules();

        // Spotify desktop only (classic installer & Microsoft Store). Other
        // SMTC players and browser tabs are ignored; Spotify Web comes through
        // BrowserBridge instead.
        GlobalSystemMediaTransportControlsSession? chosen = null;
        try
        {
            var all = _manager.GetSessions();
            var spotify = all.Where(IsSpotifySession).ToList();
            // Watch all Spotify sessions (including Stopped) so pressing Play
            // wakes us up without needing SessionsChanged to fire.
            Rewatch(spotify);
            // Finished sessions do not go into the selection - see
            // HasSomethingToShow
            var active = spotify.Where(HasSomethingToShow).ToList();
            chosen = active.FirstOrDefault(s =>
                         Status(s) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                     ?? active.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Diag.Once("get-sessions", "Reading media sessions failed: " + ex.Message);
        }

        if (Equals(_session, chosen))
        {
            if (chosen == null) Changed?.Invoke();
            return;
        }

        Unsubscribe(Swap(chosen));
        // Read once, here: the AUMID is needed by the UI thread (a click on the
        // title looks for the source window), and it must not touch WinRT
        _sourceAppId = ReadSourceAppId(chosen);
        if (chosen != null)
        {
            // PlaybackInfoChanged is not needed here: Rewatch watches the state,
            // and for all sessions at once
            chosen.MediaPropertiesChanged += OnMediaPropertiesChanged;
            chosen.TimelinePropertiesChanged += OnTimelineChanged;
        }

        Changed?.Invoke();
    }

    private static string? ReadSourceAppId(GlobalSystemMediaTransportControlsSession? session)
    {
        try { return session?.SourceAppUserModelId; }
        catch { return null; }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        Changed?.Invoke();

    /// <summary>The state changed in any of the sessions - the choice has to
    /// be recomputed. The one being shown may have finished and become Stopped,
    /// while a stopped one may have started playing; the session list does not
    /// change, so SessionsChanged will not fire.</summary>
    private void OnAnyPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        WinRtThread.Post(PickSession);
        Changed?.Invoke();
    }

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) =>
        TimelineChanged?.Invoke();

    private volatile string? _sourceAppId;

    /// <summary>The application identifier of the current session (AUMID). It
    /// is used to find the source window and reveal it on click. The value is
    /// captured when the session is chosen: it has no reason to change, and
    /// reading it on demand would mean calling WinRT from the UI thread.</summary>
    public string? SourceAppId => _sourceAppId;

    /// <summary>Quick timeline read (no track properties and no cover art).
    /// Synchronous by contract - the window calls it from its own Task.Run -
    /// but the WinRT part still happens on the WinRT thread.</summary>
    public (TimeSpan Position, TimeSpan Duration, bool IsPlaying, DateTime PositionAtUtc)? GetTimeline()
    {
        var s = _session;
        if (s == null) return null;
        var job = WinRtThread.Run(() => ReadTimeline(s));
        try { return job.Wait(3000) ? job.Result : null; }
        catch { return null; }
    }

    private static (TimeSpan Position, TimeSpan Duration, bool IsPlaying, DateTime PositionAtUtc)?
        ReadTimeline(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            var tl = s.GetTimelineProperties();
            var pi = s.GetPlaybackInfo();
            // Do not show a finished session - a safety net in case the state
            // change event never arrived and the choice was not recomputed
            if (pi?.PlaybackStatus is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped
                or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed)
                return null;
            return (tl?.Position ?? TimeSpan.Zero,
                    tl != null ? tl.EndTime - tl.StartTime : TimeSpan.Zero,
                    pi?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    tl?.LastUpdatedTime.UtcDateTime ?? DateTime.UtcNow);
        }
        catch
        {
            return null;
        }
    }

    public async Task<TrackInfo?> GetTrackAsync()
    {
        var s = _session;
        if (s == null) return null;
        return await WinRtThread.Run(() =>
        {
            try
            {
                var pi = s.GetPlaybackInfo();
                // The same safety net as in ReadTimeline: a listened-to voice
                // message leaves the session in Stopped, and it does not need
                // to be shown
                if (pi?.PlaybackStatus is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped
                    or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed)
                    return null;

                var props = WinRtThread.Await(s.TryGetMediaPropertiesAsync());
                bool playing = pi?.PlaybackStatus ==
                               GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                var tl = s.GetTimelineProperties();
                TimeSpan position = tl?.Position ?? TimeSpan.Zero;
                TimeSpan duration = tl != null ? tl.EndTime - tl.StartTime : TimeSpan.Zero;
                DateTime positionAt = tl?.LastUpdatedTime.UtcDateTime ?? DateTime.UtcNow;

                return new TrackInfo(props?.Title ?? "", props?.Artist ?? "", playing,
                    position, duration, positionAt);
            }
            catch (Exception ex)
            {
                Diag.Once("get-track", "Reading track from the Spotify session failed: " + ex.Message);
                return null;
            }
        });
    }

    public async Task<byte[]?> GetThumbnailAsync()
    {
        var s = _session;
        if (s == null) return null;
        return await WinRtThread.Run(() =>
        {
            try
            {
                var props = WinRtThread.Await(s.TryGetMediaPropertiesAsync());
                if (props?.Thumbnail == null) return null;

                using var stream = WinRtThread.Await(props.Thumbnail.OpenReadAsync(), 5000);
                if (stream == null || stream.Size == 0) return null;

                var bytes = new byte[stream.Size];
                using var reader = new DataReader(stream.GetInputStreamAt(0));
                WinRtThread.AwaitAny(reader.LoadAsync((uint)stream.Size).AsTask(), 5000);
                reader.ReadBytes(bytes);
                return bytes;
            }
            catch
            {
                return null;
            }
        });
    }

    /// <summary>A command for the current session, on the WinRT thread.
    /// Failures are swallowed by design: a player that has just closed is the
    /// normal case here, not an error worth reporting.</summary>
    private Task Command(Action<GlobalSystemMediaTransportControlsSession> action)
    {
        var s = _session;
        if (s == null) return Task.CompletedTask;
        return WinRtThread.Run(() => { try { action(s); } catch { } });
    }

    public Task TogglePlayPauseAsync() =>
        Command(s => WinRtThread.AwaitAny(s.TryTogglePlayPauseAsync().AsTask()));

    public Task NextAsync() =>
        Command(s => WinRtThread.AwaitAny(s.TrySkipNextAsync().AsTask()));

    public Task PreviousAsync() =>
        Command(s => WinRtThread.AwaitAny(s.TrySkipPreviousAsync().AsTask()));

    public Task SeekAsync(TimeSpan position) =>
        Command(s => WinRtThread.AwaitAny(s.TryChangePlaybackPositionAsync(position.Ticks).AsTask()));
}
