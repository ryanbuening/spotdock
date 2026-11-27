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

            if (_settings.ManualX.ContainsKey(PrimaryTray))
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
                    || (!IsAutoHideEnabledCached()
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

        if (!_settings.ManualX.ContainsKey(PrimaryTray))
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
            if (!ApplyResponsiveLayout(availPx / _dpiScale))
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

    private bool ApplyResponsiveLayout(double availableDip)
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

        double text = Math.Max(minText, Math.Min(maxText, minText + (avail - used)));

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

}
