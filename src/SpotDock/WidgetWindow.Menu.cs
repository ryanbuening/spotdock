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
    // ---------- Native Win32 Context Menu ----------

    private unsafe void ShowContextMenu()
    {
        CancelDragHold();
        _pressed = false;
        CancelMiddlePress();
        _volumeWheelAccum = 0;
        ResetProgressInteraction();

        Interop.ApplyMenuTheme(_lightTheme == true);

        IntPtr tray = GetTargetTray();
        IntPtr fg = Interop.GetForegroundWindow();
        IntPtr fgBefore = (fg == HwndPtr || fg == tray) ? IntPtr.Zero : fg;

        HMENU menu = PInvoke.CreatePopupMenu();
        HMENU brightMenu = PInvoke.CreatePopupMenu();
        if (menu == HMENU.Null)
        {
            if (brightMenu != HMENU.Null) PInvoke.DestroyMenu(brightMenu);
            return;
        }

        try
        {
            fixed (char* pHeader = UiStrings.MenuHeader(UpdateService.CurrentVersion))
                PInvoke.AppendMenu(menu, (MENU_ITEM_FLAGS)(MF_STRING | MF_DISABLED | MF_GRAYED), 0, pHeader);

            bool autoStart = PackagedApp.IsPackaged ? IsPackagedAutoStartCached() : IsAutoStartEnabled();
            AppendMenuStr(menu, IDM_AUTOSTART, UiStrings.AutoStart, autoStart);

            if (UpdateService.IsConfigured && !PackagedApp.IsPackaged)
            {
                string updateText = _pendingUpdate is { } u ? UiStrings.UpdateAvailable(u.Version) : UiStrings.CheckUpdates;
                AppendMenuStr(menu, IDM_UPDATE, updateText, false);
            }

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
                fixed (char* pText = UiStrings.OpacityMenu)
                    PInvoke.AppendMenu(menu, (MENU_ITEM_FLAGS)MF_POPUP, (nuint)brightMenu.Value, pText);
            }

            AppendMenuStr(menu, IDM_ART, UiStrings.ShowArt, _settings.ShowArt);
            AppendMenuStr(menu, IDM_PROGRESS, UiStrings.ProgressBar, _settings.ShowProgress);

            PInvoke.AppendMenu(menu, (MENU_ITEM_FLAGS)MF_SEPARATOR, 0, null);
            AppendMenuStr(menu, IDM_EXIT, UiStrings.Exit, false);

            Interop.GetCursorPos(out var curPt);
            int menuY = curPt.Y;
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

            if (fgBefore != IntPtr.Zero && Interop.GetForegroundWindow() == HwndPtr)
                Interop.SetForegroundWindow(fgBefore);

            // Dismissing the menu by clicking off often activates the taskbar,
            // which rises above the widget in the topmost band. Reassert above
            // the taskbar immediately on close.
            ReassertTopmost();

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
            Interop.ShowMessageBox(HwndPtr, UiStrings.UpdateNotConfigured(UpdateService.CurrentVersion), UiStrings.AppTitle,
                MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_ICONINFORMATION);
            return;
        }

        try
        {
            var update = _pendingUpdate ?? await UpdateService.CheckAsync();
            if (update == null)
            {
                Interop.ShowMessageBox(HwndPtr, UiStrings.UpdateLatest(UpdateService.CurrentVersion), UiStrings.AppTitle,
                    MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_ICONINFORMATION);
                return;
            }

            var answer = Interop.ShowMessageBox(HwndPtr,
                UiStrings.UpdatePrompt(update.Value.Version, UpdateService.CurrentVersion),
                UiStrings.AppTitle, MESSAGEBOX_STYLE.MB_YESNO | MESSAGEBOX_STYLE.MB_ICONINFORMATION);
            if (answer == MESSAGEBOX_RESULT.IDYES)
                await UpdateService.DownloadAndApplyAsync(update.Value.Url);
        }
        catch (Exception ex)
        {
            Interop.ShowMessageBox(HwndPtr, UiStrings.UpdateError(ex.Message), UiStrings.AppTitle,
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

}
