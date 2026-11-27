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

}
