using System.Diagnostics;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SpotDock;

internal sealed partial class WidgetWindow
{
    private const string ClassName = "SpotDock_Main";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "SpotDock";
    private const string StartupTaskId = "SpotDockStartup";

    private const uint TIMER_POSITION = 1;
    private const uint TIMER_TRACK = 2;
    private const uint TIMER_PROGRESS = 3;
    private const uint TIMER_ANIM = 4;
    private const uint TIMER_DRAG_HOLD = 5;
    private const uint DragHoldMs = 250;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_GRAYED = 0x00000001;
    private const uint MF_DISABLED = 0x00000002;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_POPUP = 0x00000010;
    private const uint MF_SEPARATOR = 0x00000800;

    private const uint IDM_ART = 1002;
    private const uint IDM_PROGRESS = 1003;
    private const uint IDM_AUTOSTART = 1004;
    private const uint IDM_UPDATE = 1006;
    private const uint IDM_EXIT = 1007;
    private const uint IDM_STRETCH_TEXT = 1008;
    private const uint IDM_BRIGHTNESS_BASE = 2000;
    private static readonly int[] BrightnessPresets = { 20, 30, 40, 50, 60, 75, 90, 100 };

    private enum HitZone { None, Root, Text, Like, Prev, PlayPause, Next, Progress }

    private static ushort _classAtom;
    private static WidgetWindow? _current;
    private static bool _updateCheckStarted;
    private static bool _recreatePending;
    private static (Version Version, string Url)? _pendingUpdate;

    private static Interop.WinEventDelegate? _fgProc;
    private static IntPtr _fgHook;

    public static bool HasWindow => _current != null && !_current._closed;

    public static void ShowPrimary()
    {
        if (_current != null && !_current._closed) return;
        try
        {
            var win = new WidgetWindow();
            win.CreateAndInitialize();
        }
        catch (Exception ex)
        {
            _current = null;
            Diag.Log($"Widget window failed to create: {ex}");
        }
    }

    private readonly MediaHub _media = MediaHub.Shared;
    private readonly SpotifyUiaService _uia = new();
    private readonly WidgetSettings _settings = WidgetSettings.Shared;
    private readonly LayeredSurface _surface = new();
    private readonly ScrubPillWindow _scrubPill = new();
    private readonly CancellationTokenSource _closeCts = new();

    private HWND _hwnd;
    private unsafe IntPtr HwndPtr => (IntPtr)_hwnd.Value;
    private bool _visible;
    private bool _closed;
    private bool _contextMenuOpen;
    private DateTime _menuClosedAt = DateTime.MinValue;

    private Action? _mediaChanged;
    private Action? _mediaTimeline;

    // Layout & size
    private const double MaxTextWidth = 96;
    private const double MinTextWidth = 60;
    private double _textWidthDip = MaxTextWidth;
    private double _textWidthTarget = MaxTextWidth;
    private double _textWidthFrom = MaxTextWidth;
    private bool _textWidthAnimating;
    private long _textWidthAnimStartMs;
    private const double SizeAnimMs = 140;

    private bool _showPrevSlot;
    private bool _showNextSlot;
    private bool _showLikeSlot;
    private bool _canLike;
    private bool? _liked;

    private double _dpiScale = 1.0;
    private int _winWidthPx = 1;
    private int _winHeightPx = 36;

    // Theme
    private bool? _lightTheme;
    private bool _lightCached;
    private DateTime _lightAt = DateTime.MinValue;

    // Caches
    private bool _fullscreenNow;
    private bool _fullscreenQueryRunning;
    private DateTime _fullscreenAt = DateTime.MinValue;
    private DateTime _topmostAt = DateTime.MinValue;
    private bool _leftAlignedCached;
    private DateTime _leftAlignedAt = DateTime.MinValue;
    private bool _autoHideCached;
    private bool _autoHideQueryRunning;
    private DateTime _autoHideAt = DateTime.MinValue;
    private IntPtr _trayCache;
    private DateTime _trayCacheAt;
    private DateTime _anchorSavedAt;
    private DateTime _anchorsMissingSince = DateTime.MinValue;

    // Taskbar anchors
    private readonly object _anchorLock = new();
    private double? _widgetsRightPx;
    private double? _startLeftPx;
    private double? _taskEndPx;
    private DateTime _lastAnchorQuery = DateTime.MinValue;
    private bool _anchorQueryRunning;
    private IntPtr _anchorsTray;
    private int _startMissingReads;
    private volatile bool _anchorsDirty;
    private DateTime _anchorsDirtyAt = DateTime.MinValue;
    private IntPtr _trayLocHook;
    private IntPtr _hookedTray;
    private Interop.WinEventDelegate? _trayLocProc;
    private bool _updateQueued;

    // Track state
    private string _titleShown = L.IdleTitle;
    private string _artistShown = L.NothingPlaying;
    private string _lastTrackKey = "";
    private bool _artDirty = true;
    private bool _refreshing;
    private bool _spotifyPresent;
    private bool _spotifyProc;
    private DateTime _spotifyProcAt = DateTime.MinValue;
    private uint _spotifyPid;
    private DateTime _trackNullSince = DateTime.MinValue;

    private TimeSpan _duration;
    private TimeSpan _basePosition;
    private DateTime _basePositionAt;
    private bool _isPlayingUi;

    private bool? _uiaLiked;
    private DateTime _lastUiaStateAt = DateTime.MinValue;
    private bool _uiaDirty = true;
    private int _settleToken;
    private bool _lastStateFresh = true;

    private DateTime _playToggledAt = DateTime.MinValue;
    private DateTime _likedOptimisticAt = DateTime.MinValue;
    private DateTime _seekAt = DateTime.MinValue;

    // Interaction state
    private bool _dragHoldPending;
    private bool _dragging;
    private bool _dragMoved;
    private Interop.POINT _dragStartScreen;
    private int _dragStartLeftPx;
    private int _dragTopPx;
    private bool _pressed;
    private HitZone _pressedButton = HitZone.None;
    private HitZone _hoveredButton = HitZone.None;
    private bool _mouseTracking;
    private bool _rootHovered;
    private bool _middlePressed;
    private int _volumeWheelAccum;

    // Animations
    private bool _animTimerRunning;
    private bool _renderDirty = true;

    private float _curOpacity = 0.5f;
    private float _opacityFrom = 0.5f;
    private float _opacityTarget = 0.5f;
    private bool _opacityAnimating;
    private long _opacityAnimStartMs;
    private const double OpacityFadeMs = 150;

    private struct HoverAnim
    {
        public float Value;
        public float From;
        public float Target;
        public bool Animating;
        public long StartMs;
    }

    private HoverAnim _likeHover, _prevHover, _playHover, _nextHover;

    // Album art crossfade
    private IntPtr _artBase;
    private IntPtr _artTop;
    private float _artBaseOpacity = 1f;
    private float _artTopOpacity;
    private bool _artAnimating;
    private bool _artFadeFromEmpty;
    private long _artAnimStartMs;
    private const double ArtFadeMs = 250;

    // Like & Next pop scale
    private float _likeScale = 1f;
    private bool _likePopAnimating;
    private bool _likePopAdd;
    private long _likePopStartMs;

    private float _nextScale = 1f;
    private float _nextScaleFrom = 1f;
    private float _nextScaleTarget = 1f;
    private bool _nextScaleAnimating;
    private long _nextScaleStartMs;
    private double _nextScaleDurMs = 60;

    // Marquee
    private const double ScrollSpeed = 85;
    private const double MarqueeGap = 40;
    private const double MarqueeStartDelaySeconds = 0.2;
    private const double MarqueeLapPauseSeconds = 1.2;
    private const double MarqueeReturnMs = 280;
    private string _marqueeKey = "";
    private string _marqueeTextKey = "";
    private bool _titleHovered;
    private bool _marqueeRunning;
    private bool _marqueeReturning;
    private bool _marqueeOverflows;
    private double _marqueeTextWidth;
    private double _marqueeDistance;
    private float _marqueeOffsetX;
    private float _marqueeReturnFrom;
    private float _marqueeReturnTarget;
    private long _marqueeStartMs;

    // Progress & Scrubbing
    private bool _progressTimerRunning;
    private bool _progressHovered;
    private bool _scrubbing;
    private bool _progressExpanded;
    private double _progressFraction;
    private double _hoverFraction;
    private double _scrubFraction;
    private double _progressHeightDip = 3.0;
    private double _progressHeightFrom = 3.0;
    private double _progressHeightTarget = 3.0;
    private bool _progressHeightAnimating;
    private long _progressHeightStartMs;
    private double _progressRadiusDip = 1.5;
    private TimeSpan _lastScrubDuration;
    private string _lastScrubPosText = "";
    private string _lastScrubDurText = "";

    // Horizontal slide & auto-hide ride
    private bool _slideAnimating;
    private double _slideFromX;
    private int _slideToX, _slideY;
    private long _slideStartMs;
    private const double SlideMs = 150;

    private bool _barWasHidden;
    private bool _rideAnimating;
    private bool _rideDown;
    private int _rideLeftPx, _rideFromTopPx, _rideToTopPx, _rideWinW, _rideWinH, _rideMonBottomPx;
    private double _rideStartOffsetMs;
    private long _rideStartMs;
    private const double RideDurationMs = 220;

    private const int PrimaryTray = 0;

    private static long NowMs() => Environment.TickCount64;

    private unsafe void CreateAndInitialize()
    {
        _current = this;
        var hMod = PInvoke.GetModuleHandle((PCWSTR)null);
        fixed (char* pClass = ClassName)
        {
            if (_classAtom == 0)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &WndProc,
                    hInstance = hMod,
                    lpszClassName = pClass,
                };
                _classAtom = PInvoke.RegisterClassEx(&wc);
            }

            const WINDOW_EX_STYLE exStyle =
                WINDOW_EX_STYLE.WS_EX_LAYERED |
                WINDOW_EX_STYLE.WS_EX_TOOLWINDOW |
                WINDOW_EX_STYLE.WS_EX_NOACTIVATE |
                WINDOW_EX_STYLE.WS_EX_TOPMOST;

            _hwnd = PInvoke.CreateWindowEx(
                exStyle,
                pClass,
                null,
                WINDOW_STYLE.WS_POPUP,
                0, 0, 100, 36,
                HWND.Null,
                HMENU.Null,
                hMod,
                (void*)GCHandle.ToIntPtr(GCHandle.Alloc(this)));
        }

        if (_hwnd == HWND.Null)
        {
            _current = null;
            throw new InvalidOperationException("CreateWindowEx failed for WidgetWindow.");
        }

        uint dpi = Interop.GetDpiForWindow(HwndPtr);
        _dpiScale = dpi > 0 ? dpi / 96.0 : 1.0;

        ApplyThemeIfChanged();
        ApplySettingsUi();

        PInvoke.SetTimer(_hwnd, TIMER_POSITION, 200, null);
        PInvoke.SetTimer(_hwnd, TIMER_TRACK, 10000, null);

        WidgetSettings.Changed += OnSettingsChanged;
        EnsureForegroundHook();

        _mediaChanged = () =>
        {
            _artDirty = true;
            _uiaDirty = true;
            UiThread.Post(() => _ = RefreshTrackAsync());
        };
        _mediaTimeline = () => UiThread.Post(RefreshTimeline);
        _media.Changed += _mediaChanged;
        _media.TimelineChanged += _mediaTimeline;

        UpdatePosition();

        if (UpdateService.IsConfigured && !PackagedApp.IsPackaged && !_updateCheckStarted)
        {
            _updateCheckStarted = true;
            _ = CheckUpdatesQuietlyAsync();
        }

        _ = InitMediaAndFirstRefreshAsync();
    }

    private async Task InitMediaAndFirstRefreshAsync()
    {
        await _media.InitializeAsync();
        if (_closed) return;
        UiThread.Post(() => _ = RefreshTrackAsync());
    }

    private static void EnsureForegroundHook()
    {
        if (_fgHook != IntPtr.Zero) return;
        _fgProc = (_, _, _, _, _, _, _) =>
        {
            if (_current is not { } win || win._closed) return;
            if (!win._visible && !win._fullscreenNow) return;
            UiThread.Post(() =>
            {
                if (win._closed) return;
                if (win._visible) win.ReassertTopmost();
                win._fullscreenAt = DateTime.MinValue;
                win.UpdatePosition();
            });
        };
        _fgHook = Interop.SetWinEventHook(
            Interop.EVENT_SYSTEM_FOREGROUND, Interop.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _fgProc, 0, 0, Interop.WINEVENT_OUTOFCONTEXT);
    }

    private void OnSettingsChanged()
    {
        UiThread.Post(() =>
        {
            if (_closed) return;
            ApplySettingsUi();
            UpdatePosition();
            _ = RefreshTrackAsync();
        });
    }

    private void ApplySettingsUi()
    {
        if (!_settings.ShowArt)
        {
            SetAlbumArt(IntPtr.Zero);
            _media.ClearArtCache();
        }
        ApplyOpacity(animate: false);
        RecalcSize(updateWindow: true);
        InvalidateRender();
    }

    private bool ShowProgressBar =>
        _settings.ShowProgress && _spotifyPresent && _duration > TimeSpan.Zero;

    private double ComputeWindowWidthDip()
    {
        double w = 1 + (_settings.ShowArt ? 34 + 9 : 0) + _textWidthDip + 3;
        if (_showLikeSlot && _canLike) w += 27;
        if (_showPrevSlot) w += 27;
        w += 25; // Play/Pause
        if (_showNextSlot) w += 27;
        return w;
    }

    private double ComputeWindowHeightDip() => 2 + 34 + (ShowProgressBar ? 8 : 0);

    private void RecalcSize(bool updateWindow)
    {
        if (_hwnd == HWND.Null) return;
        uint dpi = Interop.GetDpiForWindow(HwndPtr);
        if (dpi > 0) _dpiScale = dpi / 96.0;

        int newW = Math.Max(1, (int)Math.Round(ComputeWindowWidthDip() * _dpiScale));
        int newH = Math.Max(1, (int)Math.Round(ComputeWindowHeightDip() * _dpiScale));
        if (newW != _winWidthPx || newH != _winHeightPx)
        {
            _winWidthPx = newW;
            _winHeightPx = newH;
            _renderDirty = true;
            if (updateWindow) RenderNow();
        }
    }

    // ---------- Track refresh ----------

    private bool AcceptPlayingState(bool incoming) =>
        incoming == _isPlayingUi || DateTime.UtcNow - _playToggledAt > TimeSpan.FromSeconds(2);

    private void SetCanLike(bool canLike)
    {
        if (_canLike == canLike) return;
        _canLike = canLike;
        UpdatePosition();
    }

    private void RefreshSpotifyPresence()
    {
        if (_media.IsSpotifySmtcSource)
        {
            _spotifyProc = true;
            _spotifyProcAt = DateTime.UtcNow;
            return;
        }
        if (DateTime.UtcNow - _spotifyProcAt < TimeSpan.FromSeconds(10))
            return;
        _spotifyProcAt = DateTime.UtcNow;
        if (_spotifyPid != 0 && Interop.IsProcessAlive(_spotifyPid, "Spotify.exe"))
        {
            _spotifyProc = true;
            return;
        }
        _spotifyPid = Interop.FindProcessIdByName("Spotify.exe");
        _spotifyProc = _spotifyPid != 0;
    }

    private async Task RefreshTrackAsync()
    {
        if (_refreshing || _closed) return;
        _refreshing = true;
        try
        {
            TrackInfo? track = null;
            try { track = await _media.GetTrackAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }

            if (_closed) return;

            bool noData = track == null || string.IsNullOrWhiteSpace(track.Title);
            if (noData && _lastTrackKey.Length > 0)
            {
                if (_trackNullSince == DateTime.MinValue)
                    _trackNullSince = DateTime.UtcNow;
                if (DateTime.UtcNow - _trackNullSince < TimeSpan.FromSeconds(1.2))
                {
                    _ = QuickRecheckAsync();
                    return;
                }
            }
            else if (!noData)
            {
                _trackNullSince = DateTime.MinValue;
            }

            _spotifyPresent = !noData;

            if (!_spotifyPresent)
            {
                _lastTrackKey = "";
                _titleShown = L.IdleTitle;
                _artistShown = L.NothingPlaying;
                _liked = null;
                SetCanLike(false);
                SetAlbumArt(IntPtr.Zero);
                _duration = TimeSpan.Zero;
                UpdateMarquee();
                InvalidateRender();
                UpdateProgressUi();
                UpdatePosition();
                return;
            }

            _duration = track!.Duration;
            if (AcceptPlayingState(track.IsPlaying))
            {
                TimeSpan pos = track.Position;
                DateTime posAt = track.PositionAtUtc;
                bool stale = pos > _duration ||
                             (track.Title + "|" + track.Artist != _lastTrackKey &&
                              DateTime.UtcNow - posAt > TimeSpan.FromSeconds(5));
                if (stale)
                {
                    pos = TimeSpan.Zero;
                    posAt = DateTime.UtcNow;
                }
                if (!(DateTime.UtcNow - _seekAt < TimeSpan.FromSeconds(3) && posAt < _seekAt))
                {
                    _basePosition = pos;
                    _basePositionAt = posAt;
                }
                _isPlayingUi = track.IsPlaying;
            }
            UpdateProgressUi();

            _titleShown = track.Title;
            _artistShown = track.Artist;
            UpdateMarquee();
            InvalidateRender();

            string key = track.Title + "|" + track.Artist;
            bool keyChanged = key != _lastTrackKey;
            if (keyChanged)
            {
                _likedOptimisticAt = DateTime.MinValue;
                if (_scrubbing)
                    CancelScrub(keepHoverIfOver: false);
                else if (_progressHovered && _scrubPill.IsVisible && _duration > TimeSpan.Zero)
                    UpdateScrubPill(_hoverFraction);
            }

            if (_media.IsSpotifySmtcSource)
                RefreshSpotifyPresence();
            else
                _spotifyProc = false;

            bool desktopSpotify = _media.IsSpotifySmtcSource && _spotifyProc;
            bool canLike = _media.BrowserSourceActive
                ? _media.BrowserCanLike
                : desktopSpotify;
            SetCanLike(canLike);

            if (desktopSpotify && (keyChanged || _uiaDirty || DateTime.UtcNow - _lastUiaStateAt > TimeSpan.FromSeconds(5)))
            {
                _uiaDirty = false;
                var state = (Liked: (bool?)null, Fresh: false);
                try { state = await Task.Run(() => _uia.GetState(track.Title)).WaitAsync(TimeSpan.FromSeconds(8)); }
                catch (TimeoutException) { }
                if (_closed) return;
                _lastStateFresh = state.Fresh;
                _uiaLiked = state.Fresh ? state.Liked : null;
                _lastUiaStateAt = DateTime.UtcNow;
            }
            if (desktopSpotify && keyChanged)
                _ = SettleStateAsync();

            bool? liked = desktopSpotify ? _uiaLiked : null;
            if (_media.BrowserSourceActive)
            {
                liked = _media.BrowserLiked;
                if (liked != _liked && DateTime.UtcNow - _likedOptimisticAt < TimeSpan.FromSeconds(2.5))
                    liked = _liked;
            }
            if (!_media.BrowserSourceActive && liked == false &&
                DateTime.UtcNow - _likedOptimisticAt < TimeSpan.FromSeconds(8))
                liked = true;
            _liked = liked;
            InvalidateRender();

            if (_settings.ShowArt && (keyChanged || _artDirty))
            {
                _lastTrackKey = key;
                _artDirty = false;
                byte[]? bytes = null;
                try { bytes = await _media.GetThumbnailAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { }
                if (_closed) return;

                IntPtr bmp = IntPtr.Zero;
                if (bytes != null)
                {
                    double dpi = _dpiScale;
                    var decoded = await Task.Run(() => D2DRenderer.DecodeArtPixels(bytes, dpi));
                    if (_closed) return;
                    if (decoded is { } d)
                    {
                        try { bmp = D2DRenderer.Shared.CreateArtBitmap(d.Width, d.Height, d.Pixels); }
                        catch { bmp = IntPtr.Zero; }
                    }
                }
                if (_settings.ShowArt && !_closed)
                    SetAlbumArt(bmp);
                else if (bmp != IntPtr.Zero)
                    D2DRenderer.ReleaseBitmap(bmp);
            }
            else if (keyChanged)
            {
                _lastTrackKey = key;
            }

            UpdatePosition();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void SetAlbumArt(IntPtr newArt)
    {
        if (newArt == IntPtr.Zero)
        {
            _artAnimating = false;
            D2DRenderer.ReleaseBitmap(_artBase);
            D2DRenderer.ReleaseBitmap(_artTop);
            _artBase = IntPtr.Zero;
            _artTop = IntPtr.Zero;
            _artBaseOpacity = 1f;
            _artTopOpacity = 0f;
            InvalidateRender();
            return;
        }

        if (_artBase == IntPtr.Zero)
        {
            _artBase = newArt;
            D2DRenderer.ReleaseBitmap(_artTop);
            _artTop = IntPtr.Zero;
            _artBaseOpacity = 0f;
            _artTopOpacity = 0f;
            _artFadeFromEmpty = true;
            _artAnimStartMs = NowMs();
            _artAnimating = true;
            EnsureAnimTimer();
            InvalidateRender();
            return;
        }

        // Crossfade new art over current base
        if (_artTop != IntPtr.Zero)
        {
            D2DRenderer.ReleaseBitmap(_artBase);
            _artBase = _artTop;
            _artTop = IntPtr.Zero;
        }
        _artBaseOpacity = 1f;
        _artTop = newArt;
        _artTopOpacity = 0f;
        _artFadeFromEmpty = false;
        _artAnimStartMs = NowMs();
        _artAnimating = true;
        EnsureAnimTimer();
        InvalidateRender();
    }

    private async void RefreshTimeline()
    {
        try
        {
            (TimeSpan Position, TimeSpan Duration, bool IsPlaying, DateTime PositionAtUtc)? tlMaybe = null;
            try { tlMaybe = await Task.Run(() => _media.GetTimeline()).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { }
            if (_closed || tlMaybe is not { } tl) return;
            if (DateTime.UtcNow - _seekAt < TimeSpan.FromSeconds(3) && tl.PositionAtUtc < _seekAt)
                return;
            _duration = tl.Duration;
            if (AcceptPlayingState(tl.IsPlaying) && tl.Position <= tl.Duration)
            {
                _basePosition = tl.Position;
                _basePositionAt = tl.PositionAtUtc;
                _isPlayingUi = tl.IsPlaying;
            }
            UpdateProgressUi();
            InvalidateRender();
        }
        catch (Exception ex)
        {
            Diag.Log(ex.ToString());
        }
    }

    private async Task QuickRecheckAsync()
    {
        await Task.Delay(350);
        if (!_closed) UiThread.Post(() => _ = RefreshTrackAsync());
    }

    private async Task SettleStateAsync()
    {
        int token = ++_settleToken;
        for (int i = 0; i < 12; i++)
        {
            await Task.Delay(500);
            if (_closed || token != _settleToken) return;
            _uiaDirty = true;
            await RefreshTrackAsync();
            if (_lastStateFresh) return;
        }
    }

    // ---------- Theme ----------

    private bool IsSystemLightThemeCached()
    {
        if (DateTime.UtcNow - _lightAt < TimeSpan.FromSeconds(2)) return _lightCached;
        _lightAt = DateTime.UtcNow;
        _lightCached = Interop.RegGetDword(
            Interop.HKEY_CURRENT_USER,
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme") == 1;
        return _lightCached;
    }

    private void ApplyThemeIfChanged()
    {
        bool light = IsSystemLightThemeCached();
        if (_lightTheme == light) return;
        _lightTheme = light;
        ResetMarquee();
        InvalidateRender();
    }

    // ---------- Title marquee ----------

    private void ResetMarquee()
    {
        _marqueeKey = "";
        _marqueeTextKey = "";
    }

    private void ParkMarquee()
    {
        _marqueeRunning = false;
        _marqueeReturning = false;
        _marqueeOffsetX = 0f;
        InvalidateRender();
    }

    private void StartMarqueeAnimation()
    {
        _marqueeReturning = false;
        _marqueeRunning = true;
        _marqueeDistance = _marqueeTextWidth + MarqueeGap;
        _marqueeOffsetX = 0f;
        _marqueeStartMs = NowMs();
        EnsureAnimTimer();
    }

    private void GlideMarqueeBack()
    {
        _marqueeRunning = false;
        float curX = _marqueeOffsetX;
        double dist = _marqueeDistance;
        if (Math.Abs(curX) < 0.5 || (dist > 0 && Math.Abs(curX + dist) < 0.5))
        {
            ParkMarquee();
            return;
        }
        _marqueeReturnFrom = curX;
        _marqueeReturnTarget = dist <= 0 || Math.Abs(curX) <= dist / 2 ? 0f : (float)-dist;
        _marqueeStartMs = NowMs();
        _marqueeReturning = true;
        EnsureAnimTimer();
    }

    private void UpdateMarquee()
    {
        double clipWidth = Math.Max(0, _textWidthDip);
        string textKey = $"{_titleShown}|{_dpiScale:0.##}";
        string key = $"{textKey}|{clipWidth:0}";
        if (key == _marqueeKey) return;
        bool widthOnly = textKey == _marqueeTextKey;
        _marqueeKey = key;
        _marqueeTextKey = textKey;

        if (!widthOnly)
            _marqueeTextWidth = D2DRenderer.Shared.MeasureTitleWidth(_titleShown);

        _marqueeOverflows = _marqueeTextWidth - clipWidth > 4;

        if (widthOnly)
        {
            if (_marqueeRunning && !_marqueeOverflows)
                GlideMarqueeBack();
            else if (!_marqueeRunning && !_marqueeReturning && _titleHovered && _marqueeOverflows)
                StartMarqueeAnimation();
            return;
        }

        if (_titleHovered && _marqueeOverflows)
            StartMarqueeAnimation();
        else
            ParkMarquee();
    }

    // ---------- Destroy & Recreate ----------

    private unsafe void OnDestroyed()
    {
        if (_closed) return;
        _closed = true;

        CancelDragHold();
        PInvoke.KillTimer(_hwnd, TIMER_POSITION);
        PInvoke.KillTimer(_hwnd, TIMER_TRACK);
        StopProgressTimer();
        if (_animTimerRunning)
        {
            _animTimerRunning = false;
            PInvoke.KillTimer(_hwnd, TIMER_ANIM);
        }

        if (_trayLocHook != IntPtr.Zero)
        {
            Interop.UnhookWinEvent(_trayLocHook);
            _trayLocHook = IntPtr.Zero;
        }

        try { _closeCts.Cancel(); } catch { }
        _closeCts.Dispose();

        Interop.ForgetClippedWindow(HwndPtr);
        _scrubPill.Dispose();
        _surface.Dispose();
        D2DRenderer.ReleaseBitmap(_artBase);
        D2DRenderer.ReleaseBitmap(_artTop);
        _artBase = IntPtr.Zero;
        _artTop = IntPtr.Zero;

        if (_mediaChanged != null) _media.Changed -= _mediaChanged;
        if (_mediaTimeline != null) _media.TimelineChanged -= _mediaTimeline;
        WidgetSettings.Changed -= OnSettingsChanged;

        if (_current == this) _current = null;
        _updateCheckStarted = false;

        if (!Program.IntentionalExit && !_recreatePending)
        {
            _recreatePending = true;
            _ = RecreateAfterTaskbarRestartAsync();
        }
    }

    private static async Task RecreateAfterTaskbarRestartAsync()
    {
        try
        {
            Diag.Log("Widget window destroyed (taskbar gone) — waiting for the bar to come back.");
            for (int i = 0; i < 3600; i++)
            {
                await Task.Delay(1000);
                if (Interop.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero)
                {
                    await Task.Delay(2000);
                    if (i > 10) Diag.Log($"Taskbar came back after {i}s — recreating the widget.");
                    UiThread.Post(() =>
                    {
                        ShowPrimary();
                        if (!HasWindow)
                            Diag.Log("Taskbar is back but no widget window could be created.");
                    });
                    return;
                }
            }
            Diag.Log("No taskbar for an hour — giving up and exiting.");
            Program.IntentionalExit = true;
            UiThread.Shutdown();
        }
        finally
        {
            _recreatePending = false;
        }
    }

    // ---------- Window Procedure ----------

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        if (msg == 0x0081 /*WM_NCCREATE*/)
        {
            var cs = (CREATESTRUCTW*)lParam.Value;
            if (cs != null && cs->lpCreateParams != null)
            {
                PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA, (nint)cs->lpCreateParams);
            }
            return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        nint ptr = PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA);
        if (ptr == 0)
            return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);

        var handle = GCHandle.FromIntPtr(ptr);
        if (handle.Target is not WidgetWindow win)
            return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);

        try
        {
            switch (msg)
            {
                case PInvoke.WM_TIMER:
                {
                    nuint id = wParam.Value;
                    if (id == TIMER_POSITION)
                    {
                        win.UpdatePosition();
                        win.UpdateProgressUi();
                        win.ApplyThemeIfChanged();
                        if (win._visible && win._renderDirty) win.RenderNow();
                    }
                    else if (id == TIMER_TRACK)
                    {
                        _ = win.RefreshTrackAsync();
                    }
                    else if (id == TIMER_PROGRESS)
                    {
                        win.AdvanceProgress();
                    }
                    else if (id == TIMER_ANIM)
                    {
                        win.TickAnimations();
                    }
                    else if (id == TIMER_DRAG_HOLD)
                    {
                        win.BeginDraggingFromHold();
                    }
                    return default;
                }

                case PInvoke.WM_MOUSEMOVE:
                {
                    int x = unchecked((short)(lParam.Value & 0xFFFF));
                    int y = unchecked((short)((lParam.Value >> 16) & 0xFFFF));
                    win.OnMouseMove(x, y);
                    return default;
                }

                case PInvoke.WM_MOUSELEAVE:
                    win.OnMouseLeave();
                    return default;

                case PInvoke.WM_LBUTTONDOWN:
                {
                    int x = unchecked((short)(lParam.Value & 0xFFFF));
                    int y = unchecked((short)((lParam.Value >> 16) & 0xFFFF));
                    win.OnLButtonDown(x, y);
                    return default;
                }

                case PInvoke.WM_LBUTTONUP:
                {
                    int x = unchecked((short)(lParam.Value & 0xFFFF));
                    int y = unchecked((short)((lParam.Value >> 16) & 0xFFFF));
                    win.OnLButtonUp(x, y);
                    return default;
                }

                case PInvoke.WM_RBUTTONUP:
                    if (win._scrubbing)
                        win.CancelScrub(keepHoverIfOver: true);
                    else
                        win.ShowContextMenu();
                    return default;

                case PInvoke.WM_MBUTTONDOWN:
                    win.OnMButtonDown();
                    return default;

                case PInvoke.WM_MBUTTONUP:
                {
                    int x = unchecked((short)(lParam.Value & 0xFFFF));
                    int y = unchecked((short)((lParam.Value >> 16) & 0xFFFF));
                    win.OnMButtonUp(x, y);
                    return default;
                }

                case PInvoke.WM_MOUSEWHEEL:
                {
                    int delta = unchecked((short)((wParam.Value >> 16) & 0xFFFF));
                    win.OnMouseWheel(delta);
                    return default;
                }

                case PInvoke.WM_CAPTURECHANGED:
                    win.CancelDragHold();
                    win._pressed = false;
                    if (win._dragging) { win._dragging = false; win._dragMoved = false; }
                    if (win._scrubbing) win.CancelScrub(keepHoverIfOver: false);
                    if (win._pressedButton != HitZone.None)
                    {
                        win._pressedButton = HitZone.None;
                        win.InvalidateRender();
                        win.RenderNow();
                    }
                    return default;

                case PInvoke.WM_SETCURSOR:
                    if (unchecked((ushort)(lParam.Value & 0xFFFF)) == 1 /*HTCLIENT*/)
                    {
                        var curId = win._dragging ? PInvoke.IDC_SIZEALL : PInvoke.IDC_HAND;
                        PInvoke.SetCursor(PInvoke.LoadCursor(HINSTANCE.Null, curId));
                        return (LRESULT)1;
                    }
                    break;

                case PInvoke.WM_DPICHANGED:
                case PInvoke.WM_DISPLAYCHANGE:
                case PInvoke.WM_SETTINGCHANGE:
                    win._lightAt = DateTime.MinValue;
                    win._leftAlignedAt = DateTime.MinValue;
                    win._autoHideAt = DateTime.MinValue;
                    win._fullscreenAt = DateTime.MinValue;
                    win.ApplyThemeIfChanged();
                    win.RecalcSize(updateWindow: true);
                    win.UpdatePosition();
                    return default;

                case PInvoke.WM_DESTROY:
                    win.OnDestroyed();
                    PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA, 0);
                    handle.Free();
                    return default;
            }
        }
        catch (Exception ex)
        {
            Diag.Log(ex.ToString());
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
