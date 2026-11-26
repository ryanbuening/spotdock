using System.Diagnostics;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SpotDock;

internal sealed class WidgetWindow
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

    // ---------- Positioning ----------

    private IntPtr GetTargetTray()
    {
        if (_trayCache != IntPtr.Zero && Interop.IsWindow(_trayCache)
            && (DateTime.UtcNow - _trayCacheAt).TotalSeconds < 2)
            return _trayCache;
        _trayCache = Interop.FindWindow("Shell_TrayWnd", null);
        _trayCacheAt = DateTime.UtcNow;
        return _trayCache;
    }

    private void RememberWidgetsAnchor(double rightPx)
    {
        bool known = _settings.WidgetsAnchor.TryGetValue(PrimaryTray, out double old);
        if (known && Math.Abs(old - rightPx) < 8) return;
        _settings.WidgetsAnchor[PrimaryTray] = rightPx;
        if (DateTime.UtcNow - _anchorSavedAt < TimeSpan.FromMinutes(5)) return;
        _anchorSavedAt = DateTime.UtcNow;
        try { _settings.Save(); } catch { }
    }

    private int LeftFallbackPx(Interop.RECT r)
    {
        if (_settings.WidgetsAnchor.TryGetValue(PrimaryTray, out double remembered))
        {
            double limit = r.Left + (r.Right - r.Left) * 0.25;
            if (remembered > r.Left && remembered < limit)
                return (int)remembered + 8;
        }
        return r.Left + 12;
    }

    private bool IsTaskbarLeftAlignedCached()
    {
        if (DateTime.UtcNow - _leftAlignedAt < TimeSpan.FromSeconds(2)) return _leftAlignedCached;
        _leftAlignedAt = DateTime.UtcNow;
        _leftAlignedCached = Interop.RegGetDword(
            Interop.HKEY_CURRENT_USER,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
            "TaskbarAl") == 0;
        return _leftAlignedCached;
    }

    private bool IsAutoHideEnabledCached()
    {
        if (!_autoHideQueryRunning && DateTime.UtcNow - _autoHideAt >= TimeSpan.FromSeconds(10))
        {
            _autoHideQueryRunning = true;
            Task.Run(() =>
            {
                bool autoHide = Interop.IsAutoHideEnabled();
                UiThread.Post(() =>
                {
                    _autoHideQueryRunning = false;
                    if (_closed) return;
                    _autoHideAt = DateTime.UtcNow;
                    if (_autoHideCached != autoHide)
                    {
                        _autoHideCached = autoHide;
                        UpdatePosition();
                    }
                });
            });
        }
        return _autoHideCached;
    }

    private void EnsureTrayLocationHook(IntPtr tray)
    {
        if (tray == _hookedTray) return;
        if (_trayLocHook != IntPtr.Zero)
        {
            Interop.UnhookWinEvent(_trayLocHook);
            _trayLocHook = IntPtr.Zero;
        }
        _hookedTray = tray;
        uint tid = Interop.GetWindowThreadProcessId(tray, out uint pid);
        _trayLocProc ??= (_, _, hwnd, idObject, _, _, _) =>
        {
            if (hwnd == _hookedTray && idObject == 0)
            {
                if (_updateQueued) return;
                _updateQueued = true;
                UiThread.Post(UpdatePosition);
                return;
            }

            if (_settings.ManualX.ContainsKey(PrimaryTray) && !_settings.StretchText)
                return;

            if ((DateTime.UtcNow - _anchorsDirtyAt).TotalMilliseconds < 150) return;
            _anchorsDirtyAt = DateTime.UtcNow;
            _anchorsDirty = true;
            if (_updateQueued) return;
            _updateQueued = true;
            UiThread.Post(UpdatePosition);
        };
        _trayLocHook = Interop.SetWinEventHook(
            Interop.EVENT_OBJECT_LOCATIONCHANGE, Interop.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _trayLocProc, pid, tid, Interop.WINEVENT_OUTOFCONTEXT);
    }

    private void UpdatePosition()
    {
        _updateQueued = false;
        if (_closed || _hwnd == HWND.Null) return;

        IntPtr tray = GetTargetTray();
        if (tray == IntPtr.Zero || !Interop.GetWindowRect(tray, out var r))
        {
            CancelRide();
            _barWasHidden = false;
            HideWidget();
            return;
        }

        EnsureTrayLocationHook(tray);

        int trayHeightPx = r.Bottom - r.Top;
        int visiblePx = Interop.GetTaskbarVisiblePx(r, out int monitorBottomPx, out int workAreaBottomPx);

        if (!Interop.GetWindowRect(HwndPtr, out var w))
            return;

        RecalcSize(updateWindow: true);
        int winWidth = _winWidthPx;
        int winHeight = _winHeightPx;

        int barBandPx = GetTaskbarBandPx(tray, r, monitorBottomPx, workAreaBottomPx);
        int belowEdgeTopPx = monitorBottomPx - 2 + (barBandPx - winHeight) / 2;

        if (_rideAnimating)
        {
            if (_rideDown && visiblePx >= trayHeightPx - 4)
            {
                CancelRide();
                StartRide(w.Left, w.Top, r.Bottom - barBandPx + (barBandPx - winHeight) / 2,
                    down: false, winWidth, winHeight, monitorBottomPx);
            }
            else if (!_rideDown && visiblePx <= 8)
            {
                CancelRide();
                StartRide(w.Left, w.Top, belowEdgeTopPx,
                    down: true, winWidth, winHeight, monitorBottomPx);
            }
            return;
        }

        double hiddenPhase = 1.0 - Math.Clamp((double)visiblePx / trayHeightPx, 0, 1);
        bool pinned = _dragging || _dragHoldPending || _scrubbing || _contextMenuOpen;

        if (visiblePx <= 8)
        {
            if (pinned) return;
            if (_visible && IsAutoHideEnabledCached())
            {
                StartRide(w.Left, w.Top, belowEdgeTopPx, down: true, winWidth, winHeight, monitorBottomPx, hiddenPhase);
                return;
            }
            _barWasHidden = true;
            HideWidget();
            return;
        }

        if (visiblePx < trayHeightPx - 4)
        {
            if (_visible && !pinned && IsAutoHideEnabledCached())
                StartRide(w.Left, w.Top, belowEdgeTopPx, down: true, winWidth, winHeight, monitorBottomPx, hiddenPhase);
            return;
        }

        bool hide = !_spotifyPresent
                    || (!TestSwitches.NoFullscreen && !IsAutoHideEnabledCached()
                        && IsFullscreenNow(tray));
        if (hide)
        {
            _barWasHidden = false;
            HideWidget();
            return;
        }

        if (tray != _anchorsTray)
        {
            _anchorsTray = tray;
            _lastAnchorQuery = DateTime.MinValue;
            lock (_anchorLock)
            {
                _widgetsRightPx = null;
                _startLeftPx = null;
                _taskEndPx = null;
            }
        }

        if (!(_settings.ManualX.ContainsKey(PrimaryTray) && !_settings.StretchText))
            RefreshAnchors(tray);

        double? widgetsRightPx, startLeftPx, taskEndPx;
        lock (_anchorLock)
        {
            widgetsRightPx = _widgetsRightPx;
            startLeftPx = _startLeftPx;
            taskEndPx = _taskEndPx;
        }
        if (widgetsRightPx.HasValue) RememberWidgetsAnchor(widgetsRightPx.Value);

        const int VerticalNudgePx = 3;
        int topPx = r.Bottom - barBandPx + (barBandPx - winHeight) / 2 + VerticalNudgePx;

        bool rightAnchored = false;
        int rightAnchorLeftLimitPx = r.Left + 12;
        int leftPx, rightLimitPx;

        if (_settings.ManualX.TryGetValue(PrimaryTray, out double manualX))
        {
            int? notifyLeftManual = Interop.GetTrayNotifyLeft(tray);
            bool haveGap = _settings.ManualGap.TryGetValue(PrimaryTray, out double gap);
            if (!haveGap && notifyLeftManual.HasValue)
            {
                gap = notifyLeftManual.Value - (manualX + winWidth);
                _settings.ManualGap[PrimaryTray] = gap;
                haveGap = true;
                UiThread.Post(() => _settings.Save());
            }
            leftPx = haveGap && notifyLeftManual.HasValue
                ? (int)Math.Round(notifyLeftManual.Value - gap - winWidth)
                : (int)manualX;
            leftPx = Math.Max(r.Left + 4, Math.Min(leftPx, r.Right - winWidth - 4));
            rightLimitPx = r.Right - 4;
        }
        else if (!IsTaskbarLeftAlignedCached())
        {
            if (!startLeftPx.HasValue && _visible)
            {
                leftPx = w.Left;
                rightLimitPx = r.Right - 4;
            }
            else if (!startLeftPx.HasValue)
            {
                if (_anchorsMissingSince == DateTime.MinValue)
                    _anchorsMissingSince = DateTime.UtcNow;
                if (DateTime.UtcNow - _anchorsMissingSince < TimeSpan.FromSeconds(4))
                {
                    HideWidget();
                    return;
                }
                leftPx = widgetsRightPx.HasValue ? (int)widgetsRightPx.Value + 8 : LeftFallbackPx(r);
                rightLimitPx = r.Right - 4;
            }
            else
            {
                _anchorsMissingSince = DateTime.MinValue;
                leftPx = widgetsRightPx.HasValue ? (int)widgetsRightPx.Value + 8 : LeftFallbackPx(r);
                rightLimitPx = (int)startLeftPx.Value - 2;
            }
        }
        else
        {
            rightAnchored = true;
            int? notifyLeftPx = Interop.GetTrayNotifyLeft(tray);
            rightLimitPx = (notifyLeftPx ?? (r.Right - 220)) - 8;
            if (taskEndPx.HasValue)
                rightAnchorLeftLimitPx = (int)taskEndPx.Value + 8;
            leftPx = Math.Max(rightAnchorLeftLimitPx, rightLimitPx - winWidth);
        }

        if (_spotifyPresent)
        {
            int availPx = rightAnchored ? rightLimitPx - rightAnchorLeftLimitPx : rightLimitPx - leftPx;
            bool leftAnchorKnown = rightAnchored ? taskEndPx.HasValue : widgetsRightPx.HasValue;
            if (!ApplyResponsiveLayout(availPx / _dpiScale, leftAnchorKnown, (r.Right - r.Left) / _dpiScale))
            {
                _barWasHidden = false;
                HideWidget();
                return;
            }

            RecalcSize(updateWindow: true);
            if (_winWidthPx != winWidth || _winHeightPx != winHeight)
            {
                winWidth = _winWidthPx;
                winHeight = _winHeightPx;
                topPx = r.Bottom - barBandPx + (barBandPx - winHeight) / 2 + VerticalNudgePx;
                if (_settings.ManualX.TryGetValue(PrimaryTray, out double mx))
                {
                    int? nlm = Interop.GetTrayNotifyLeft(tray);
                    bool hg = _settings.ManualGap.TryGetValue(PrimaryTray, out double g);
                    leftPx = hg && nlm.HasValue ? (int)Math.Round(nlm.Value - g - winWidth) : (int)mx;
                    leftPx = Math.Max(r.Left + 4, Math.Min(leftPx, r.Right - winWidth - 4));
                }
                else if (rightAnchored)
                {
                    leftPx = Math.Max(rightAnchorLeftLimitPx, rightLimitPx - winWidth);
                }
            }
        }

        if (_barWasHidden && !_dragging && IsAutoHideEnabledCached())
        {
            _barWasHidden = false;
            StartRide(leftPx, belowEdgeTopPx, topPx, down: false, winWidth, winHeight, monitorBottomPx);
            return;
        }
        _barWasHidden = false;

        if (!_dragging && (Math.Abs(w.Left - leftPx) > 1 || Math.Abs(w.Top - topPx) > 1))
            MoveOrSlideTo(w.Left, w.Top, leftPx, topPx);

        Interop.ClipWindowBottom(HwndPtr, winWidth, winHeight, monitorBottomPx - topPx);

        ShowWidget();

        if (DateTime.UtcNow - _topmostAt > TimeSpan.FromSeconds(1))
        {
            _topmostAt = DateTime.UtcNow;
            ReassertTopmost();
        }
    }

    private void ShowWidget()
    {
        if (_renderDirty) RenderNow();
        if (!_visible)
        {
            Interop.ShowWindow(HwndPtr, Interop.SW_SHOWNOACTIVATE);
            _visible = true;
            ReassertTopmost();
        }
        else if (!Interop.IsWindowVisible(HwndPtr))
        {
            Interop.ShowWindow(HwndPtr, Interop.SW_SHOWNOACTIVATE);
        }
    }

    private void HideWidget()
    {
        if (!_visible && !Interop.IsWindowVisible(HwndPtr)) return;
        CancelDragHold();
        _pressed = false;
        _dragging = false;
        _dragMoved = false;
        StopSlide();
        CancelMiddlePress(animate: false);
        _volumeWheelAccum = 0;
        ResetProgressInteraction();
        _rootHovered = false;
        _hoveredButton = HitZone.None;
        ApplyOpacity(animate: false);
        _titleHovered = false;
        ParkMarquee();
        if (_visible || Interop.IsWindowVisible(HwndPtr))
        {
            Interop.ShowWindow(HwndPtr, 0 /*SW_HIDE*/);
            _visible = false;
        }
    }

    private static int GetTaskbarBandPx(IntPtr tray, Interop.RECT r, int monitorBottomPx, int workAreaBottomPx)
    {
        int trayHeightPx = r.Bottom - r.Top;
        int reservedPx = monitorBottomPx - workAreaBottomPx;
        return reservedPx > 8
            ? Math.Min(trayHeightPx, reservedPx)
            : Math.Min(trayHeightPx, (int)Math.Round(48 * Interop.GetDpiForWindow(tray) / 96.0));
    }

    private bool AnyPopupOpen => _contextMenuOpen || _scrubPill.IsVisible;

    private void ReassertTopmost()
    {
        if (!_visible || AnyPopupOpen || _hwnd == HWND.Null) return;
        if (!Interop.IsAboveWindow(HwndPtr, GetTargetTray()))
            Interop.EnsureTopmost(HwndPtr);
    }

    private bool IsFullscreenNow(IntPtr tray)
    {
        if (!_fullscreenQueryRunning && DateTime.UtcNow - _fullscreenAt >= TimeSpan.FromSeconds(1))
        {
            _fullscreenQueryRunning = true;
            IntPtr self = HwndPtr;
            Task.Run(() =>
            {
                bool full = Interop.IsMonitorCoveredByFullscreen(self, tray);
                UiThread.Post(() =>
                {
                    _fullscreenQueryRunning = false;
                    if (_closed) return;
                    _fullscreenAt = DateTime.UtcNow;
                    if (_fullscreenNow != full)
                    {
                        _fullscreenNow = full;
                        UpdatePosition();
                    }
                });
            });
        }
        return _fullscreenNow;
    }

    // ---------- Slide & Ride animations ----------

    private void MoveOrSlideTo(int curLeft, int curTop, int leftPx, int topPx)
    {
        if (curTop != topPx || Math.Abs(leftPx - curLeft) > 260)
        {
            StopSlide();
            Interop.MoveWindowTo(HwndPtr, leftPx, topPx);
            return;
        }

        _slideToX = leftPx;
        _slideY = topPx;
        if (_slideAnimating) return;

        _slideFromX = curLeft;
        _slideStartMs = NowMs();
        _slideAnimating = true;
        EnsureAnimTimer();
    }

    private void StopSlide() => _slideAnimating = false;

    private void StartRide(int leftPx, int fromTopPx, int toTopPx, bool down,
        int winWidth, int winHeight, int monitorBottomPx, double startPhase = 0)
    {
        startPhase = Math.Clamp(startPhase, 0, 1);
        double t0 = down ? Math.Cbrt(startPhase) : 1 - Math.Cbrt(1 - startPhase);
        _rideStartOffsetMs = t0 * RideDurationMs;
        _rideStartMs = NowMs();
        _rideAnimating = true;
        _rideDown = down;
        _rideLeftPx = leftPx;
        _rideFromTopPx = fromTopPx;
        _rideToTopPx = toTopPx;
        _rideWinW = winWidth;
        _rideWinH = winHeight;
        _rideMonBottomPx = monitorBottomPx;
        StopSlide();

        double eased0 = down ? t0 * t0 * t0 : 1 - Math.Pow(1 - t0, 3);
        int startTopPx = (int)Math.Round(fromTopPx + (toTopPx - fromTopPx) * eased0);
        Interop.MoveWindowTo(HwndPtr, leftPx, startTopPx);
        Interop.ClipWindowBottom(HwndPtr, winWidth, winHeight, monitorBottomPx - startTopPx);
        ShowWidget();
        ReassertTopmost();
        EnsureAnimTimer();
    }

    private void CancelRide()
    {
        _rideAnimating = false;
        StopSlide();
    }

    // ---------- Responsive layout ----------

    private bool ApplyResponsiveLayout(double availableDip, bool leftAnchorKnown, double barWidthDip)
    {
        double avail = availableDip;
        const double IconBtn = 27;
        const double PlayBtn = 25;
        double BasePart = 1 + (_settings.ShowArt ? 34 + 9 : 0) + 3;

        double used = BasePart + MinTextWidth + PlayBtn;
        if (avail < used) return false;

        bool prev = false, next = false, likeSlot = false;
        Take(ref prev);
        Take(ref next);
        Take(ref likeSlot);

        bool showLike = likeSlot && _canLike;
        double extraText = likeSlot && !showLike ? IconBtn : 0;
        double minText = MinTextWidth + extraText;
        double maxText = MaxTextWidth + extraText;

        double room = Math.Min(maxText, minText + (avail - used));
        double text;
        if (_settings.StretchText && leftAnchorKnown)
        {
            double cap = Math.Max(maxText, barWidthDip / 2 + extraText);
            text = Math.Max(minText, Math.Min(minText + (avail - used), cap));
        }
        else
        {
            text = Math.Max(minText, room);
        }

        bool visChanged = _showPrevSlot != prev || _showNextSlot != next || _showLikeSlot != showLike;
        _showPrevSlot = prev;
        _showNextSlot = next;
        _showLikeSlot = likeSlot;

        if (visChanged || Math.Abs(_textWidthTarget - text) > 1)
        {
            if (visChanged || Math.Abs(_textWidthDip - text) > 220 || !_visible)
                SetTextWidthNow(text);
            else
                AnimateTextWidth(text);
        }
        return true;

        void Take(ref bool flag)
        {
            if (used + IconBtn <= avail)
            {
                flag = true;
                used += IconBtn;
            }
        }
    }

    private void AnimateTextWidth(double to)
    {
        _textWidthFrom = _textWidthDip;
        _textWidthTarget = to;
        _textWidthAnimStartMs = NowMs();
        _textWidthAnimating = true;
        EnsureAnimTimer();
    }

    private void SetTextWidthNow(double width)
    {
        _textWidthAnimating = false;
        _textWidthDip = width;
        _textWidthTarget = width;
        UpdateMarquee();
        InvalidateRender();
    }

    // ---------- Anchors background poll ----------

    private void RefreshAnchors(IntPtr tray)
    {
        if (TestSwitches.NoUia) return;
        bool full = IsTaskbarLeftAlignedCached();
        double minGapMs = _anchorsDirty ? 60 : (full ? 5000 : 1000);
        if ((DateTime.UtcNow - _lastAnchorQuery).TotalMilliseconds < minGapMs)
            return;
        _anchorsDirty = false;
        if (_anchorQueryRunning && (DateTime.UtcNow - _lastAnchorQuery).TotalSeconds < 15)
            return;

        _anchorQueryRunning = true;
        _lastAnchorQuery = DateTime.UtcNow;
        Task.Run(() =>
        {
            bool moved = false;
            try
            {
                var (ok, widgetsRight, startLeft, taskButtonsRight) = TaskbarAnchors.Get(tray, full);
                lock (_anchorLock)
                {
                    if (tray != _anchorsTray) return;
                    if (!ok) return;
                    if (!startLeft.HasValue && _startLeftPx.HasValue && ++_startMissingReads < 3)
                        return;
                    bool startVanished = !startLeft.HasValue && _startLeftPx.HasValue;
                    _startMissingReads = 0;
                    double? newWidgets = startVanished ? (widgetsRight ?? _widgetsRightPx) : widgetsRight;
                    moved = newWidgets != _widgetsRightPx || startLeft != _startLeftPx;
                    _widgetsRightPx = newWidgets;
                    _taskEndPx = startVanished || !full ? (taskButtonsRight ?? _taskEndPx) : taskButtonsRight;
                    _startLeftPx = startLeft;
                }
            }
            finally
            {
                _anchorQueryRunning = false;
            }

            if (moved && !_closed)
                UiThread.Post(UpdatePosition);
        });
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

    // ---------- Progress bar & Scrubbing ----------

    private void UpdateProgressUi()
    {
        bool show = ShowProgressBar;
        RecalcSize(updateWindow: true);
        if (!show)
        {
            StopProgressTimer();
            if (_progressHovered || _scrubbing || _scrubPill.IsVisible)
                ResetProgressInteraction();
            return;
        }
        if (_scrubbing) return;

        TimeSpan pos = _basePosition;
        if (_isPlayingUi)
            pos += DateTime.UtcNow - _basePositionAt;

        _progressFraction = Math.Clamp(pos.TotalMilliseconds / _duration.TotalMilliseconds, 0, 1);

        if (_progressHovered && _scrubPill.IsVisible && _lastScrubDuration != _duration)
            UpdateScrubPill(_hoverFraction);

        if (_isPlayingUi)
            StartProgressTimer();
        else
            StopProgressTimer();

        InvalidateRender();
    }

    private unsafe void StartProgressTimer()
    {
        if (_progressTimerRunning || _hwnd == HWND.Null) return;
        _progressTimerRunning = true;
        PInvoke.SetTimer(_hwnd, TIMER_PROGRESS, 200, null);
    }

    private unsafe void StopProgressTimer()
    {
        if (!_progressTimerRunning || _hwnd == HWND.Null) return;
        _progressTimerRunning = false;
        PInvoke.KillTimer(_hwnd, TIMER_PROGRESS);
    }

    private void AdvanceProgress()
    {
        if (_closed || _duration <= TimeSpan.Zero || !_isPlayingUi || !ShowProgressBar)
        {
            StopProgressTimer();
            return;
        }
        if (_scrubbing) return;
        TimeSpan pos = _basePosition + (DateTime.UtcNow - _basePositionAt);
        _progressFraction = Math.Clamp(pos.TotalMilliseconds / _duration.TotalMilliseconds, 0, 1);
        InvalidateRender();
        RenderNow();
    }

    private void SetProgressBarExpanded(bool expanded, bool animate)
    {
        if (_progressExpanded == expanded && animate) return;
        _progressExpanded = expanded;
        double targetH = expanded ? 5.0 : 3.0;
        if (expanded) _progressRadiusDip = 2.5;

        if (!animate)
        {
            _progressHeightAnimating = false;
            _progressHeightDip = targetH;
            _progressHeightTarget = targetH;
            _progressRadiusDip = expanded ? 2.5 : 1.5;
            InvalidateRender();
            return;
        }

        _progressHeightFrom = _progressHeightDip;
        _progressHeightTarget = targetH;
        _progressHeightStartMs = NowMs();
        _progressHeightAnimating = true;
        EnsureAnimTimer();
    }

    private double GetProgressFractionFromDip(double xDip)
    {
        double trackW = Math.Max(0, ComputeWindowWidthDip() - 2.0);
        return trackW > 0 ? Math.Clamp((xDip - 1.0) / trackW, 0, 1) : 0;
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        int totalSeconds = (int)Math.Round(t.TotalSeconds);
        int hours = totalSeconds / 3600;
        int minutes = (totalSeconds % 3600) / 60;
        int seconds = totalSeconds % 60;
        return hours > 0 ? $"{hours}:{minutes:D2}:{seconds:D2}" : $"{minutes}:{seconds:D2}";
    }

    private void UpdateScrubPill(double fraction)
    {
        if (!ShowProgressBar || _hwnd == HWND.Null || !Interop.GetWindowRect(HwndPtr, out var w))
            return;

        var target = TimeSpan.FromTicks((long)(_duration.Ticks * fraction));
        _lastScrubPosText = FormatTime(target);
        _lastScrubDurText = " / " + FormatTime(_duration);
        _lastScrubDuration = _duration;

        double trackW = Math.Max(1, ComputeWindowWidthDip() - 2.0);
        double xDip = 1.0 + fraction * trackW;
        int centerX = w.Left + (int)Math.Round(xDip * _dpiScale);

        int bottomY = w.Top + (int)Math.Round(4.0 * _dpiScale);
        IntPtr tray = GetTargetTray();
        if (tray != IntPtr.Zero && Interop.GetWindowRect(tray, out var r))
        {
            Interop.GetTaskbarVisiblePx(r, out int monitorBottomPx, out int workAreaBottomPx);
            int barBandPx = GetTaskbarBandPx(tray, r, monitorBottomPx, workAreaBottomPx);
            int barTopPx = r.Bottom - barBandPx;
            bottomY = barTopPx + (int)Math.Round(4.0 * _dpiScale);
        }

        _scrubPill.ShowAt(centerX, bottomY, _lastScrubPosText, _lastScrubDurText, _lightTheme == true, _dpiScale);
    }

    private void HideScrubPreview(bool animate)
    {
        _scrubPill.Hide();
        SetProgressBarExpanded(false, animate);
        InvalidateRender();
    }

    private void CancelScrub(bool keepHoverIfOver)
    {
        bool wasScrubbing = _scrubbing;
        _scrubbing = false;
        if (wasScrubbing && PInvoke.GetCapture() == _hwnd)
            PInvoke.ReleaseCapture();

        UpdateProgressUi();
        ApplyOpacity(animate: true);

        if (keepHoverIfOver && ShowProgressBar &&
            Interop.GetCursorPos(out var pt) && IsCursorOverProgress(pt))
        {
            _progressHovered = true;
            SetProgressBarExpanded(true, animate: true);
            _hoverFraction = GetProgressFractionFromScreen(pt);
            UpdateScrubPill(_hoverFraction);
            InvalidateRender();
            return;
        }

        _progressHovered = false;
        HideScrubPreview(animate: true);
    }

    private void ResetProgressInteraction()
    {
        _progressHovered = false;
        bool wasScrubbing = _scrubbing;
        _scrubbing = false;
        if (wasScrubbing && PInvoke.GetCapture() == _hwnd)
            PInvoke.ReleaseCapture();
        HideScrubPreview(animate: false);
        if (wasScrubbing)
        {
            UpdateProgressUi();
            ApplyOpacity(animate: false);
        }
    }

    private unsafe bool IsCursorOverProgress(Interop.POINT screenPt)
    {
        var p = new Point(screenPt.X, screenPt.Y);
        PInvoke.ScreenToClient(_hwnd, &p);
        return HitTestDip(p.X / _dpiScale, p.Y / _dpiScale) == HitZone.Progress;
    }

    private unsafe double GetProgressFractionFromScreen(Interop.POINT screenPt)
    {
        var p = new Point(screenPt.X, screenPt.Y);
        PInvoke.ScreenToClient(_hwnd, &p);
        return GetProgressFractionFromDip(p.X / _dpiScale);
    }

    // ---------- Opacity & Hover ----------

    private void ApplyOpacity(bool animate = false, bool previewIdle = false)
    {
        float target = (float)(!previewIdle && (_rootHovered || _scrubbing) ? 1.0 : _settings.Opacity);
        if (!animate || Math.Abs(_curOpacity - target) < 0.005f)
        {
            _opacityAnimating = false;
            _curOpacity = target;
            _opacityTarget = target;
            InvalidateRender();
            return;
        }
        _opacityFrom = _curOpacity;
        _opacityTarget = target;
        _opacityAnimStartMs = NowMs();
        _opacityAnimating = true;
        EnsureAnimTimer();
    }

    private void SetHoverTarget(HitZone zone, float target)
    {
        long now = NowMs();
        switch (zone)
        {
            case HitZone.Like: StartHover(ref _likeHover, target, now); break;
            case HitZone.Prev: StartHover(ref _prevHover, target, now); break;
            case HitZone.PlayPause: StartHover(ref _playHover, target, now); break;
            case HitZone.Next: StartHover(ref _nextHover, target, now); break;
        }
    }

    private void StartHover(ref HoverAnim anim, float target, long now)
    {
        if (Math.Abs(anim.Target - target) < 0.01f) return;
        anim.From = anim.Value;
        anim.Target = target;
        anim.StartMs = now;
        anim.Animating = true;
        EnsureAnimTimer();
    }

    private void PopLikeIcon(bool add)
    {
        _likePopAdd = add;
        _likePopStartMs = NowMs();
        _likePopAnimating = true;
        EnsureAnimTimer();
    }

    private void CancelMiddlePress(bool animate = true)
    {
        if (!_middlePressed && !_nextScaleAnimating && Math.Abs(_nextScale - 1f) < 0.005f) return;
        _middlePressed = false;
        SetNextIconMiddlePressed(false, animate);
    }

    private void SetNextIconMiddlePressed(bool pressed, bool animate = true)
    {
        if (pressed)
        {
            if (!_showNextSlot) return;
            _nextScaleFrom = _nextScale;
            _nextScaleTarget = 0.85f;
            _nextScaleDurMs = 60;
            _nextScaleStartMs = NowMs();
            _nextScaleAnimating = true;
            EnsureAnimTimer();
            return;
        }

        if (!animate)
        {
            _nextScaleAnimating = false;
            _nextScale = 1f;
            InvalidateRender();
            return;
        }
        _nextScaleFrom = _nextScale;
        _nextScaleTarget = 1f;
        _nextScaleDurMs = 100;
        _nextScaleStartMs = NowMs();
        _nextScaleAnimating = true;
        EnsureAnimTimer();
    }

    // ---------- Animation Loop ----------

    private unsafe void EnsureAnimTimer()
    {
        if (_animTimerRunning || _hwnd == HWND.Null) return;
        _animTimerRunning = true;
        PInvoke.SetTimer(_hwnd, TIMER_ANIM, 16, null);
    }

    private unsafe void StopAnimTimerIfIdle()
    {
        bool any = _opacityAnimating || _textWidthAnimating || _artAnimating ||
                   _likeHover.Animating || _prevHover.Animating || _playHover.Animating || _nextHover.Animating ||
                   _likePopAnimating || _nextScaleAnimating ||
                   _marqueeRunning || _marqueeReturning ||
                   _progressHeightAnimating || _slideAnimating || _rideAnimating;
        if (!any && _animTimerRunning && _hwnd != HWND.Null)
        {
            _animTimerRunning = false;
            PInvoke.KillTimer(_hwnd, TIMER_ANIM);
        }
    }

    private void TickAnimations()
    {
        long now = NowMs();
        bool needRender = false;

        if (_opacityAnimating)
        {
            double t = Math.Clamp((now - _opacityAnimStartMs) / OpacityFadeMs, 0, 1);
            double e = 1 - Math.Pow(1 - t, 3);
            _curOpacity = (float)(_opacityFrom + (_opacityTarget - _opacityFrom) * e);
            if (t >= 1) { _curOpacity = _opacityTarget; _opacityAnimating = false; }
            needRender = true;
        }

        needRender |= StepHover(ref _likeHover, now);
        needRender |= StepHover(ref _prevHover, now);
        needRender |= StepHover(ref _playHover, now);
        needRender |= StepHover(ref _nextHover, now);

        if (_textWidthAnimating)
        {
            double t = Math.Clamp((now - _textWidthAnimStartMs) / SizeAnimMs, 0, 1);
            double e = 1 - Math.Pow(1 - t, 3);
            _textWidthDip = _textWidthFrom + (_textWidthTarget - _textWidthFrom) * e;
            if (t >= 1)
            {
                _textWidthDip = _textWidthTarget;
                _textWidthAnimating = false;
                UpdateMarquee();
            }
            RecalcSize(updateWindow: false);
            needRender = true;
        }

        if (_artAnimating)
        {
            double t = Math.Clamp((now - _artAnimStartMs) / ArtFadeMs, 0, 1);
            if (_artFadeFromEmpty)
            {
                _artBaseOpacity = (float)t;
                if (t >= 1) { _artBaseOpacity = 1f; _artAnimating = false; }
            }
            else
            {
                _artTopOpacity = (float)t;
                if (t >= 1)
                {
                    D2DRenderer.ReleaseBitmap(_artBase);
                    _artBase = _artTop;
                    _artTop = IntPtr.Zero;
                    _artBaseOpacity = 1f;
                    _artTopOpacity = 0f;
                    _artAnimating = false;
                }
            }
            needRender = true;
        }

        if (_likePopAnimating)
        {
            double elapsed = now - _likePopStartMs;
            if (_likePopAdd)
            {
                if (elapsed < 115)
                    _likeScale = (float)(1.0 + (0.7 - 1.0) * (elapsed / 115.0));
                else if (elapsed < 300)
                {
                    double t = (elapsed - 115.0) / 185.0;
                    double e = 1 - Math.Pow(1 - t, 2);
                    _likeScale = (float)(0.7 + (1.35 - 0.7) * e);
                }
                else if (elapsed < 550)
                {
                    double t = (elapsed - 300.0) / 250.0;
                    double e = 1 - Math.Pow(1 - t, 2);
                    _likeScale = (float)(1.35 + (1.0 - 1.35) * e);
                }
                else
                {
                    _likeScale = 1f;
                    _likePopAnimating = false;
                }
            }
            else
            {
                if (elapsed < 150)
                    _likeScale = (float)(1.0 + (0.75 - 1.0) * (elapsed / 150.0));
                else if (elapsed < 430)
                {
                    double t = (elapsed - 150.0) / 280.0;
                    double e = 1 - Math.Pow(1 - t, 2);
                    _likeScale = (float)(0.75 + (1.0 - 0.75) * e);
                }
                else
                {
                    _likeScale = 1f;
                    _likePopAnimating = false;
                }
            }
            needRender = true;
        }

        if (_nextScaleAnimating)
        {
            double t = Math.Clamp((now - _nextScaleStartMs) / _nextScaleDurMs, 0, 1);
            double e = _nextScaleTarget > _nextScaleFrom ? (1 - Math.Pow(1 - t, 3)) : t;
            _nextScale = (float)(_nextScaleFrom + (_nextScaleTarget - _nextScaleFrom) * e);
            if (t >= 1) { _nextScale = _nextScaleTarget; _nextScaleAnimating = false; }
            needRender = true;
        }

        if (_marqueeRunning)
        {
            double elapsedSec = (now - _marqueeStartMs) / 1000.0 - MarqueeStartDelaySeconds;
            if (elapsedSec > 0)
            {
                double dist = _marqueeDistance;
                double scrollSec = Math.Max(1.2, dist / ScrollSpeed);
                double ramp = Math.Min(0.45, scrollSec / 5.0);
                double speed = dist / (scrollSec - ramp);
                double rampDist = speed * ramp / 2.0;
                double cycle = scrollSec + MarqueeLapPauseSeconds;
                double phase = elapsedSec % cycle;

                if (phase < ramp)
                {
                    double u = phase / ramp;
                    _marqueeOffsetX = (float)(-rampDist * u * u);
                }
                else if (phase < scrollSec - ramp)
                {
                    double u = (phase - ramp) / (scrollSec - 2 * ramp);
                    _marqueeOffsetX = (float)(-(rampDist + (dist - 2 * rampDist) * u));
                }
                else if (phase < scrollSec)
                {
                    double u = (phase - (scrollSec - ramp)) / ramp;
                    double inv = 1 - u;
                    _marqueeOffsetX = (float)(-(dist - rampDist * inv * inv));
                }
                else
                {
                    _marqueeOffsetX = (float)-dist;
                }
                needRender = true;
            }
        }
        else if (_marqueeReturning)
        {
            double t = Math.Clamp((now - _marqueeStartMs) / MarqueeReturnMs, 0, 1);
            double e = 1 - Math.Pow(1 - t, 3);
            _marqueeOffsetX = (float)(_marqueeReturnFrom + (_marqueeReturnTarget - _marqueeReturnFrom) * e);
            if (t >= 1) ParkMarquee();
            needRender = true;
        }

        if (_progressHeightAnimating)
        {
            double t = Math.Clamp((now - _progressHeightStartMs) / 120.0, 0, 1);
            double e = 1 - Math.Pow(1 - t, 3);
            _progressHeightDip = _progressHeightFrom + (_progressHeightTarget - _progressHeightFrom) * e;
            if (t >= 1)
            {
                _progressHeightDip = _progressHeightTarget;
                _progressHeightAnimating = false;
                if (!_progressExpanded) _progressRadiusDip = 1.5;
            }
            needRender = true;
        }

        if (_slideAnimating)
        {
            double t = Math.Clamp((now - _slideStartMs) / SlideMs, 0, 1);
            if (t >= 1)
            {
                _slideAnimating = false;
                Interop.MoveWindowTo(HwndPtr, _slideToX, _slideY);
            }
            else
            {
                double e = 1 - Math.Pow(1 - t, 3);
                int x = (int)Math.Round(_slideFromX + (_slideToX - _slideFromX) * e);
                Interop.MoveWindowTo(HwndPtr, x, _slideY);
            }
        }

        if (_rideAnimating)
        {
            double t = Math.Min(1.0, (_rideStartOffsetMs + (now - _rideStartMs)) / RideDurationMs);
            double eased = _rideDown ? t * t * t : 1 - Math.Pow(1 - t, 3);
            int topPx = (int)Math.Round(_rideFromTopPx + (_rideToTopPx - _rideFromTopPx) * eased);
            Interop.MoveWindowTo(HwndPtr, _rideLeftPx, topPx);
            Interop.ClipWindowBottom(HwndPtr, _rideWinW, _rideWinH, _rideMonBottomPx - topPx);
            if (t >= 1.0)
            {
                _rideAnimating = false;
                if (_rideDown)
                {
                    _barWasHidden = true;
                    HideWidget();
                }
            }
        }

        if (needRender)
        {
            _renderDirty = true;
            RenderNow();
        }

        StopAnimTimerIfIdle();
    }

    private static bool StepHover(ref HoverAnim anim, long now)
    {
        if (!anim.Animating) return false;
        double dur = anim.Target > anim.From ? 80.0 : 120.0;
        double t = Math.Clamp((now - anim.StartMs) / dur, 0, 1);
        double e = 1 - Math.Pow(1 - t, 3);
        anim.Value = (float)(anim.From + (anim.Target - anim.From) * e);
        if (t >= 1) { anim.Value = anim.Target; anim.Animating = false; }
        return true;
    }

    private void InvalidateRender() => _renderDirty = true;

    private void RenderNow()
    {
        if (_closed || _hwnd == HWND.Null) return;
        _renderDirty = false;

        var state = new WidgetRenderState
        {
            DpiScale = _dpiScale,
            WidthPx = _winWidthPx,
            HeightPx = _winHeightPx,
            WidthDip = ComputeWindowWidthDip(),
            HeightDip = ComputeWindowHeightDip(),
            LightTheme = _lightTheme == true,
            WidgetOpacity = _curOpacity,
            ContentVisible = _spotifyPresent,
            ShowArt = _settings.ShowArt,
            ShowProgress = ShowProgressBar,
            TextWidthDip = _textWidthDip,
            ShowLike = _showLikeSlot && _canLike,
            ShowPrev = _showPrevSlot,
            ShowNext = _showNextSlot,
            Title = _titleShown,
            Artist = _artistShown,
            MarqueeOffsetX = _marqueeOffsetX,
            MarqueeDistance = (float)_marqueeDistance,
            MarqueeShowSecond = _marqueeRunning || _marqueeReturning,
            ArtBase = _artBase,
            ArtTop = _artTop,
            ArtBaseOpacity = _artBaseOpacity,
            ArtTopOpacity = _artTopOpacity,
            IsPlaying = _isPlayingUi,
            Liked = _liked,
            LikeScale = _likeScale,
            NextScale = _nextScale,
            LikeHover = _likeHover.Value,
            PrevHover = _prevHover.Value,
            PlayHover = _playHover.Value,
            NextHover = _nextHover.Value,
            LikePressed = _pressedButton == HitZone.Like,
            PrevPressed = _pressedButton == HitZone.Prev,
            PlayPressed = _pressedButton == HitZone.PlayPause,
            NextPressed = _pressedButton == HitZone.Next,
            ProgressFraction = _scrubbing ? _scrubFraction : _progressFraction,
            ProgressHoverFraction = _hoverFraction,
            ProgressHovered = _progressHovered,
            Scrubbing = _scrubbing,
            ProgressHeightDip = _progressHeightDip,
            ProgressRadiusDip = _progressRadiusDip,
        };

        D2DRenderer.Shared.RenderWidget(_surface, _hwnd, in state);
    }

    // ---------- Hit testing ----------

    private HitZone HitTestDip(double xDip, double yDip)
    {
        double w = ComputeWindowWidthDip();
        double h = ComputeWindowHeightDip();
        if (xDip < 0 || yDip < 0 || xDip > w || yDip > h) return HitZone.None;
        if (!_spotifyPresent) return HitZone.Root;
        if (ShowProgressBar && yDip >= 36) return HitZone.Progress;
        if (yDip < 2 || yDip > 36) return HitZone.Root;

        double x = 1 + (_settings.ShowArt ? 34 + 9 : 0);
        if (xDip >= x && xDip < x + _textWidthDip) return HitZone.Text;
        x += _textWidthDip + 3;

        if (_showLikeSlot && _canLike)
        {
            if (xDip >= x && xDip < x + 25) return HitZone.Like;
            x += 27;
        }
        if (_showPrevSlot)
        {
            if (xDip >= x && xDip < x + 25) return HitZone.Prev;
            x += 27;
        }
        if (xDip >= x && xDip < x + 25) return HitZone.PlayPause;
        x += 25;
        if (_showNextSlot)
        {
            x += 2;
            if (xDip >= x && xDip < x + 25) return HitZone.Next;
        }
        return HitZone.Root;
    }

    // ---------- Mouse & Actions ----------

    private unsafe void OnMouseMove(int xPx, int yPx)
    {
        if (!_mouseTracking)
        {
            var tme = new TRACKMOUSEEVENT
            {
                cbSize = (uint)sizeof(TRACKMOUSEEVENT),
                dwFlags = TRACKMOUSEEVENT_FLAGS.TME_LEAVE,
                hwndTrack = _hwnd,
            };
            PInvoke.TrackMouseEvent(&tme);
            _mouseTracking = true;
        }

        if (!_rootHovered)
        {
            _rootHovered = true;
            ApplyOpacity(animate: true);
        }

        if (_dragging)
        {
            Interop.GetCursorPos(out var cur);
            int dxPx = cur.X - _dragStartScreen.X;
            if (Math.Abs(dxPx) > 1) _dragMoved = true;
            if (_dragMoved)
            {
                int newLeft = _dragStartLeftPx + dxPx;
                IntPtr trayNow = GetTargetTray();
                if (trayNow != IntPtr.Zero && Interop.GetWindowRect(trayNow, out var tr))
                    newLeft = Math.Max(tr.Left + 4, Math.Min(newLeft, tr.Right - _winWidthPx - 4));
                Interop.MoveWindowTo(HwndPtr, newLeft, _dragTopPx);
            }
            return;
        }

        if (_dragHoldPending) return;

        double xDip = xPx / _dpiScale;
        double yDip = yPx / _dpiScale;

        if (_scrubbing)
        {
            _scrubFraction = GetProgressFractionFromDip(xDip);
            _hoverFraction = _scrubFraction;
            UpdateScrubPill(_scrubFraction);
            InvalidateRender();
            RenderNow();
            return;
        }

        HitZone zone = HitTestDip(xDip, yDip);

        // Title marquee hover
        bool overText = zone == HitZone.Text;
        if (overText != _titleHovered)
        {
            _titleHovered = overText;
            if (_titleHovered && _marqueeOverflows && !_marqueeRunning)
                StartMarqueeAnimation();
            else if (!_titleHovered && _marqueeRunning)
                GlideMarqueeBack();
        }

        // Icon button hover
        HitZone btn = zone is HitZone.Like or HitZone.Prev or HitZone.PlayPause or HitZone.Next
            ? zone : HitZone.None;
        if (btn != _hoveredButton)
        {
            if (_hoveredButton != HitZone.None) SetHoverTarget(_hoveredButton, 0f);
            _hoveredButton = btn;
            if (_hoveredButton != HitZone.None) SetHoverTarget(_hoveredButton, 1f);
        }

        // Progress hover
        bool overProgress = zone == HitZone.Progress && _duration > TimeSpan.Zero;
        if (overProgress)
        {
            _hoverFraction = GetProgressFractionFromDip(xDip);
            if (!_progressHovered)
            {
                _progressHovered = true;
                SetProgressBarExpanded(true, animate: true);
            }
            UpdateScrubPill(_hoverFraction);
            InvalidateRender();
            RenderNow();
        }
        else if (_progressHovered)
        {
            _progressHovered = false;
            HideScrubPreview(animate: true);
            RenderNow();
        }
    }

    private void OnMouseLeave()
    {
        _mouseTracking = false;
        CancelMiddlePress();
        _volumeWheelAccum = 0;
        _rootHovered = false;
        ApplyOpacity(animate: true);

        if (_titleHovered)
        {
            _titleHovered = false;
            if (_marqueeRunning) GlideMarqueeBack();
        }
        if (_hoveredButton != HitZone.None)
        {
            SetHoverTarget(_hoveredButton, 0f);
            _hoveredButton = HitZone.None;
        }
        if (_progressHovered && !_scrubbing)
        {
            _progressHovered = false;
            HideScrubPreview(animate: true);
            RenderNow();
        }
    }

    private void StartDragHold()
    {
        if (_hwnd == HWND.Null) return;
        _dragHoldPending = true;
        unsafe { PInvoke.SetTimer(_hwnd, TIMER_DRAG_HOLD, DragHoldMs, null); }
    }

    private void CancelDragHold()
    {
        if (!_dragHoldPending) return;
        _dragHoldPending = false;
        if (_hwnd != HWND.Null)
            PInvoke.KillTimer(_hwnd, TIMER_DRAG_HOLD);
    }

    private void BeginDraggingFromHold()
    {
        if (_hwnd != HWND.Null)
            PInvoke.KillTimer(_hwnd, TIMER_DRAG_HOLD);
        if (!_dragHoldPending || _closed) return;

        _dragHoldPending = false;
        _pressed = false;
        _dragging = true;
        _dragMoved = false;

        CancelMiddlePress();
        _volumeWheelAccum = 0;
        ResetProgressInteraction();

        Interop.GetCursorPos(out _dragStartScreen);
        if (Interop.GetWindowRect(HwndPtr, out var w))
        {
            _dragStartLeftPx = w.Left;
            _dragTopPx = w.Top;
        }

        unsafe
        {
            PInvoke.SetCursor(PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_SIZEALL));
        }
    }

    private void RestoreHandCursor()
    {
        unsafe { PInvoke.SetCursor(PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_HAND)); }
    }

    private void OnLButtonDown(int xPx, int yPx)
    {
        double xDip = xPx / _dpiScale;
        double yDip = yPx / _dpiScale;

        HitZone zone = HitTestDip(xDip, yDip);
        if (zone == HitZone.Progress)
        {
            if (_duration <= TimeSpan.Zero) return;
            _scrubbing = true;
            _progressHovered = true;
            _scrubFraction = GetProgressFractionFromDip(xDip);
            _hoverFraction = _scrubFraction;
            SetProgressBarExpanded(true, animate: true);
            ApplyOpacity(animate: true);
            PInvoke.SetCapture(_hwnd);
            UpdateScrubPill(_scrubFraction);
            InvalidateRender();
            RenderNow();
            return;
        }

        if (zone is HitZone.Like or HitZone.Prev or HitZone.PlayPause or HitZone.Next)
        {
            _pressedButton = zone;
            PInvoke.SetCapture(_hwnd);
            InvalidateRender();
            RenderNow();
            return;
        }

        _pressed = true;
        PInvoke.SetCapture(_hwnd);
        StartDragHold();
    }

    private async void OnLButtonUp(int xPx, int yPx)
    {
        try
        {
            CancelDragHold();

            if (_dragging)
            {
                _dragging = false;
                if (PInvoke.GetCapture() == _hwnd) PInvoke.ReleaseCapture();
                RestoreHandCursor();
                if (_dragMoved && Interop.GetWindowRect(HwndPtr, out var w))
                {
                    _settings.ManualX[PrimaryTray] = w.Left;
                    IntPtr trayNow = GetTargetTray();
                    int? notifyLeft = trayNow != IntPtr.Zero ? Interop.GetTrayNotifyLeft(trayNow) : null;
                    if (notifyLeft.HasValue)
                        _settings.ManualGap[PrimaryTray] = notifyLeft.Value - w.Right;
                    else
                        _settings.ManualGap.Remove(PrimaryTray);
                    _settings.Save();
                }
                return;
            }

            double xDip = xPx / _dpiScale;
            double yDip = yPx / _dpiScale;

            if (_scrubbing)
            {
                _scrubFraction = GetProgressFractionFromDip(xDip);
                _hoverFraction = _scrubFraction;
                _scrubbing = false;
                if (PInvoke.GetCapture() == _hwnd) PInvoke.ReleaseCapture();

                if (_duration <= TimeSpan.Zero)
                {
                    ResetProgressInteraction();
                    return;
                }

                var target = TimeSpan.FromTicks((long)(_duration.Ticks * _scrubFraction));
                _seekAt = DateTime.UtcNow;
                _basePosition = target;
                _basePositionAt = DateTime.UtcNow;

                HitZone zoneNow = HitTestDip(xDip, yDip);
                bool overHit = zoneNow == HitZone.Progress;
                _progressHovered = overHit;
                _rootHovered = zoneNow != HitZone.None;

                UpdateProgressUi();
                if (overHit)
                    UpdateScrubPill(_hoverFraction);
                else
                    HideScrubPreview(animate: true);
                ApplyOpacity(animate: true);
                RenderNow();

                await _media.SeekAsync(target);
                return;
            }

            if (_pressedButton != HitZone.None)
            {
                HitZone released = _pressedButton;
                _pressedButton = HitZone.None;
                if (PInvoke.GetCapture() == _hwnd) PInvoke.ReleaseCapture();
                InvalidateRender();
                RenderNow();

                if (HitTestDip(xDip, yDip) == released)
                {
                    switch (released)
                    {
                        case HitZone.PlayPause: await DoPlayPauseAsync(); break;
                        case HitZone.Next: await _media.NextAsync(); break;
                        case HitZone.Prev: await _media.PreviousAsync(); break;
                        case HitZone.Like: await DoLikeAsync(); break;
                    }
                }
                return;
            }

            if (_pressed)
            {
                _pressed = false;
                if (PInvoke.GetCapture() == _hwnd) PInvoke.ReleaseCapture();
                if (HitTestDip(xDip, yDip) != HitZone.None)
                {
                    if (!_media.FocusCurrentSource())
                        SpotifyActions.FocusRunningSpotifyWindow();
                }
            }
        }
        catch (Exception ex)
        {
            Diag.Log(ex.ToString());
        }
    }

    private void OnMButtonDown()
    {
        if (_dragging || _scrubbing || !_spotifyPresent || _contextMenuOpen ||
            DateTime.UtcNow - _menuClosedAt < TimeSpan.FromMilliseconds(200))
            return;
        _middlePressed = true;
        SetNextIconMiddlePressed(true);
    }

    private async void OnMButtonUp(int xPx, int yPx)
    {
        try
        {
            if (!_middlePressed) return;
            _middlePressed = false;
            SetNextIconMiddlePressed(false);
            if (_dragging || !_spotifyPresent || _contextMenuOpen) return;
            if (HitTestDip(xPx / _dpiScale, yPx / _dpiScale) == HitZone.None) return;
            await _media.NextAsync();
        }
        catch (Exception ex)
        {
            Diag.Log(ex.ToString());
        }
    }

    private void OnMouseWheel(int delta)
    {
        if (_dragging || _scrubbing || !_spotifyPresent || _contextMenuOpen ||
            DateTime.UtcNow - _menuClosedAt < TimeSpan.FromMilliseconds(200))
            return;
        if (_volumeWheelAccum != 0 && Math.Sign(_volumeWheelAccum) != Math.Sign(delta))
            _volumeWheelAccum = 0;
        _volumeWheelAccum += delta;
        int steps = _volumeWheelAccum / 120;
        if (steps == 0) return;
        _volumeWheelAccum -= steps * 120;
        Interop.StepSystemVolume(steps);
    }

    private async Task DoPlayPauseAsync()
    {
        _isPlayingUi = !_isPlayingUi;
        _playToggledAt = DateTime.UtcNow;
        if (!_isPlayingUi)
            _basePosition += DateTime.UtcNow - _basePositionAt;
        _basePositionAt = DateTime.UtcNow;
        UpdateProgressUi();
        InvalidateRender();
        RenderNow();
        await _media.TogglePlayPauseAsync();
    }

    private async Task DoLikeAsync()
    {
        if (_media.BrowserSourceActive)
        {
            if (!_media.BrowserCanLike) return;
            bool add = _liked != true;
            _liked = add;
            _likedOptimisticAt = DateTime.UtcNow;
            PopLikeIcon(add);
            InvalidateRender();
            RenderNow();
            _media.ToggleBrowserLike();
            return;
        }

        if (!_spotifyProc || !_media.IsSpotifySmtcSource || _liked == true) return;
        _liked = true;
        _likedOptimisticAt = DateTime.UtcNow;
        PopLikeIcon(true);
        InvalidateRender();
        RenderNow();

        bool ok = await Task.Run(() => _uia.AddToFavorites());
        if (!ok)
            await Task.Run(() => _uia.AddToFavoritesByClick());
        _ = ReconcileLikeLaterAsync();
    }

    private async Task ReconcileLikeLaterAsync()
    {
        await Task.Delay(4000);
        if (_closed) return;
        _uiaDirty = true;
        UiThread.Post(() => _ = RefreshTrackAsync());
    }

    // ---------- Native Win32 Context Menu ----------

    private unsafe void ShowContextMenu()
    {
        CancelDragHold();
        _pressed = false;
        CancelMiddlePress();
        _volumeWheelAccum = 0;
        ResetProgressInteraction();

        Interop.ApplyMenuTheme(_lightTheme == true);

        IntPtr fg = Interop.GetForegroundWindow();
        IntPtr fgBefore = fg == HwndPtr ? IntPtr.Zero : fg;

        HMENU menu = PInvoke.CreatePopupMenu();
        HMENU brightMenu = PInvoke.CreatePopupMenu();
        if (menu == HMENU.Null)
        {
            if (brightMenu != HMENU.Null) PInvoke.DestroyMenu(brightMenu);
            return;
        }

        try
        {
            fixed (char* pHeader = L.MenuHeader(UpdateService.CurrentVersion))
                PInvoke.AppendMenu(menu, (MENU_ITEM_FLAGS)(MF_STRING | MF_DISABLED | MF_GRAYED), 0, pHeader);
            PInvoke.AppendMenu(menu, (MENU_ITEM_FLAGS)MF_SEPARATOR, 0, null);

            if (brightMenu != HMENU.Null)
            {
                int curPct = (int)Math.Round(_settings.Opacity * 100);
                int closest = BrightnessPresets.OrderBy(p => Math.Abs(p - curPct)).First();
                for (int i = 0; i < BrightnessPresets.Length; i++)
                {
                    int pct = BrightnessPresets[i];
                    AppendMenuStr(brightMenu, IDM_BRIGHTNESS_BASE + (uint)i, $"{pct}%", pct == closest);
                }
                fixed (char* pText = L.OpacityMenu)
                    PInvoke.AppendMenu(menu, (MENU_ITEM_FLAGS)MF_POPUP, (nuint)brightMenu.Value, pText);
            }

            AppendMenuStr(menu, IDM_ART, L.ShowArt, _settings.ShowArt);
            AppendMenuStr(menu, IDM_PROGRESS, L.ProgressBar, _settings.ShowProgress);

            bool autoStart = PackagedApp.IsPackaged ? IsPackagedAutoStartCached() : IsAutoStartEnabled();
            AppendMenuStr(menu, IDM_AUTOSTART, L.AutoStart, autoStart);

            if (UpdateService.IsConfigured && !PackagedApp.IsPackaged)
            {
                string updateText = _pendingUpdate is { } u ? L.UpdateAvailable(u.Version) : L.CheckUpdates;
                AppendMenuStr(menu, IDM_UPDATE, updateText, false);
            }

            PInvoke.AppendMenu(menu, (MENU_ITEM_FLAGS)MF_SEPARATOR, 0, null);
            AppendMenuStr(menu, IDM_EXIT, L.Exit, false);

            Interop.GetCursorPos(out var curPt);
            int menuY = curPt.Y;
            IntPtr tray = GetTargetTray();
            if (tray != IntPtr.Zero && Interop.GetWindowRect(tray, out var tr))
            {
                Interop.GetTaskbarVisiblePx(tr, out int monitorBottomPx, out int workAreaBottomPx);
                int barBandPx = GetTaskbarBandPx(tray, tr, monitorBottomPx, workAreaBottomPx);
                menuY = tr.Bottom - barBandPx;
            }

            _contextMenuOpen = true;
            int ex = Interop.GetWindowLong(HwndPtr, Interop.GWL_EXSTYLE);
            Interop.SetWindowLong(HwndPtr, Interop.GWL_EXSTYLE, ex & ~Interop.WS_EX_NOACTIVATE);
            Interop.SetForegroundWindow(HwndPtr);

            uint cmd = (uint)PInvoke.TrackPopupMenuEx(
                menu,
                (uint)(TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON |
                       TRACK_POPUP_MENU_FLAGS.TPM_BOTTOMALIGN | TRACK_POPUP_MENU_FLAGS.TPM_LEFTALIGN),
                curPt.X, menuY, _hwnd, null).Value;

            Interop.SetWindowLong(HwndPtr, Interop.GWL_EXSTYLE, ex | Interop.WS_EX_NOACTIVATE);
            _contextMenuOpen = false;
            _menuClosedAt = DateTime.UtcNow;

            if (fgBefore != IntPtr.Zero && Interop.GetForegroundWindow() != fgBefore)
                Interop.SetForegroundWindow(fgBefore);

            if (cmd != 0)
                HandleMenuCommand(cmd);
        }
        finally
        {
            _contextMenuOpen = false;
            PInvoke.DestroyMenu(menu);
            if (brightMenu != HMENU.Null) PInvoke.DestroyMenu(brightMenu);
        }
    }

    private static unsafe void AppendMenuStr(HMENU menu, uint id, string text, bool isChecked)
    {
        uint flags = MF_STRING | (isChecked ? MF_CHECKED : 0u);
        fixed (char* pText = text)
            PInvoke.AppendMenu(menu, (MENU_ITEM_FLAGS)flags, id, pText);
    }

    private void HandleMenuCommand(uint cmd)
    {
        if (cmd >= IDM_BRIGHTNESS_BASE && cmd < IDM_BRIGHTNESS_BASE + (uint)BrightnessPresets.Length)
        {
            int pct = BrightnessPresets[cmd - IDM_BRIGHTNESS_BASE];
            _settings.Opacity = Math.Clamp(pct / 100.0, 0.2, 1.0);
            _settings.Save();
            ApplyOpacity(animate: true);
            return;
        }

        switch (cmd)
        {
            case IDM_ART:
                _settings.ShowArt = !_settings.ShowArt;
                _settings.Save();
                _artDirty = true;
                _ = RefreshTrackAsync();
                UpdatePosition();
                break;

            case IDM_PROGRESS:
                _settings.ShowProgress = !_settings.ShowProgress;
                _settings.Save();
                UpdateProgressUi();
                UpdatePosition();
                break;

            case IDM_AUTOSTART:
                ToggleAutoStart();
                break;

            case IDM_UPDATE:
                _ = CheckAndApplyUpdateAsync();
                break;

            case IDM_EXIT:
                Diag.Log("Quit from the menu.");
                Program.IntentionalExit = true;
                UiThread.Shutdown();
                break;
        }
    }

    private bool _packagedAutoStartCached;

    private bool IsPackagedAutoStartCached()
    {
        _ = RefreshPackagedAutoStartAsync();
        return _packagedAutoStartCached;
    }

    private async Task RefreshPackagedAutoStartAsync()
    {
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
            _packagedAutoStartCached = task.State is Windows.ApplicationModel.StartupTaskState.Enabled
                or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
        }
        catch { }
    }

    private static bool IsAutoStartEnabled() =>
        Interop.RegValueExists(Interop.HKEY_CURRENT_USER, RunKeyPath, RunValueName);

    private async void ToggleAutoStart()
    {
        try
        {
            if (PackagedApp.IsPackaged)
            {
                try
                {
                    var task = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
                    bool enabled = task.State is Windows.ApplicationModel.StartupTaskState.Enabled
                        or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
                    if (!enabled)
                    {
                        var state = await task.RequestEnableAsync();
                        _packagedAutoStartCached = state == Windows.ApplicationModel.StartupTaskState.Enabled;
                    }
                    else
                    {
                        task.Disable();
                        _packagedAutoStartCached = false;
                    }
                }
                catch { }
                return;
            }

            if (IsAutoStartEnabled())
            {
                Interop.RegDeleteValue(Interop.HKEY_CURRENT_USER, RunKeyPath, RunValueName);
            }
            else if (Environment.ProcessPath is { Length: > 0 } exePath)
            {
                Interop.RegSetString(Interop.HKEY_CURRENT_USER, RunKeyPath, RunValueName, $"\"{exePath}\"");
            }
        }
        catch (Exception ex)
        {
            Diag.Log(ex.ToString());
        }
    }

    private async Task CheckAndApplyUpdateAsync()
    {
        if (!UpdateService.IsConfigured)
        {
            Interop.ShowMessageBox(HwndPtr, L.UpdateNotConfigured(UpdateService.CurrentVersion), L.AppTitle,
                MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_ICONINFORMATION);
            return;
        }

        try
        {
            var update = _pendingUpdate ?? await UpdateService.CheckAsync();
            if (update == null)
            {
                Interop.ShowMessageBox(HwndPtr, L.UpdateLatest(UpdateService.CurrentVersion), L.AppTitle,
                    MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_ICONINFORMATION);
                return;
            }

            var answer = Interop.ShowMessageBox(HwndPtr,
                L.UpdatePrompt(update.Value.Version, UpdateService.CurrentVersion),
                L.AppTitle, MESSAGEBOX_STYLE.MB_YESNO | MESSAGEBOX_STYLE.MB_ICONINFORMATION);
            if (answer == MESSAGEBOX_RESULT.IDYES)
                await UpdateService.DownloadAndApplyAsync(update.Value.Url);
        }
        catch (Exception ex)
        {
            Interop.ShowMessageBox(HwndPtr, L.UpdateError(ex.Message), L.AppTitle,
                MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_ICONWARNING);
        }
    }

    private async Task CheckUpdatesQuietlyAsync()
    {
        var ct = _closeCts.Token;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            while (!_closed)
            {
                try
                {
                    var update = await UpdateService.CheckAsync();
                    if (update != null && !_closed)
                        _pendingUpdate = update;
                }
                catch { }
                await Task.Delay(TimeSpan.FromHours(6), ct);
            }
        }
        catch (OperationCanceledException) { }
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
