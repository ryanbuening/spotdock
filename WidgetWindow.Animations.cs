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

}
