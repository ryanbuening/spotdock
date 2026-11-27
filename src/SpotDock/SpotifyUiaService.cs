using System.Diagnostics;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace SpotDock;

/// <summary>
/// Reads and controls the favorites state through the native COM UI Automation
/// tree of the Spotify window (Chromium).
/// </summary>
public sealed unsafe class SpotifyUiaService
{
    private static readonly string[] PlaylistTerms =
        { "playlist", "spellista", "spilleliste", "soittolista", "afspeellijst" };

    private static bool IsLikedName(string name) =>
        PlaylistTerms.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase));

    private static void LogI18nNames(string likeName)
    {
        if (System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("en", StringComparison.OrdinalIgnoreCase))
            return;
        if (likeName.Length > 0)
            Diag.Once("i18n-like:" + likeName, "[i18n] like button name: " + likeName);
    }

    private readonly object _lock = new();
    private IUIAutomation* _uia;
    private IUIAutomationTreeWalker* _walker;
    private IUIAutomationCondition* _buttonCond;
    private IUIAutomationCondition* _checkBoxCond;
    private IUIAutomationCondition* _groupCond;
    private IUIAutomationCondition* _hyperlinkCond;
    private IUIAutomationElement* _controlsGroup;

    private bool EnsureUia()
    {
        NativeUia.EnsureComInitialized();
        if (_uia != null) return true;
        _uia = NativeUia.CreateAutomation();
        if (_uia == null) return false;

        try
        {
            _walker = _uia->ControlViewWalker;
            _buttonCond = NativeUia.CreateIntPropertyCondition(
                _uia, UIA_PROPERTY_ID.UIA_ControlTypePropertyId, (int)UIA_CONTROLTYPE_ID.UIA_ButtonControlTypeId);
            _checkBoxCond = NativeUia.CreateIntPropertyCondition(
                _uia, UIA_PROPERTY_ID.UIA_ControlTypePropertyId, (int)UIA_CONTROLTYPE_ID.UIA_CheckBoxControlTypeId);
            _groupCond = NativeUia.CreateIntPropertyCondition(
                _uia, UIA_PROPERTY_ID.UIA_ControlTypePropertyId, (int)UIA_CONTROLTYPE_ID.UIA_GroupControlTypeId);
            _hyperlinkCond = NativeUia.CreateIntPropertyCondition(
                _uia, UIA_PROPERTY_ID.UIA_ControlTypePropertyId, (int)UIA_CONTROLTYPE_ID.UIA_HyperlinkControlTypeId);
        }
        catch
        {
            return false;
        }

        return _walker != null && _buttonCond != null && _checkBoxCond != null &&
               _groupCond != null && _hyperlinkCond != null;
    }

    public (bool? Liked, bool Fresh) GetState(string? expectedTitle = null)
    {
        if (!Monitor.TryEnter(_lock, TimeSpan.FromSeconds(3)))
            return (null, false);
        try
        {
            IUIAutomationElement* trackInfo = null;
            IUIAutomationElement* like = null;
            try
            {
                if (!EnsureUia()) return (null, false);
                EnsureGroups();

                bool fresh = true;
                bool? liked = null;
                trackInfo = FindTrackInfoGroup();
                if (expectedTitle is { Length: > 0 } && trackInfo != null)
                {
                    string groupName = NativeUia.GetCurrentName(trackInfo);
                    fresh = groupName.Contains(expectedTitle, StringComparison.OrdinalIgnoreCase);
                }
                like = FindLikeButton(trackInfo);
                string likeName = NativeUia.GetCurrentName(like);
                if (like != null)
                    liked = IsLikedName(likeName);

                LogI18nNames(likeName);
                return (liked, fresh);
            }
            catch
            {
                Invalidate();
                return (null, false);
            }
            finally
            {
                NativeUia.SafeRelease(ref like);
                NativeUia.SafeRelease(ref trackInfo);
            }
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    public bool AddToFavorites() => DoWithRetry(() =>
    {
        IUIAutomationElement* trackInfo = null;
        IUIAutomationElement* like = null;
        try
        {
            trackInfo = FindTrackInfoGroup();
            like = FindLikeButton(trackInfo);
            if (like == null) return (bool?)null;

            string name = NativeUia.GetCurrentName(like);
            if (IsLikedName(name))
                return true;

            Guid expIid = typeof(IUIAutomationExpandCollapsePattern).GUID;
            IUIAutomationExpandCollapsePattern* expand = null;
            try
            {
                expand = (IUIAutomationExpandCollapsePattern*)like->GetCurrentPatternAs(
                    UIA_PATTERN_ID.UIA_ExpandCollapsePatternId, &expIid);
                if (expand != null) { expand->Expand(); return true; }
            }
            catch { }
            finally { NativeUia.SafeRelease(ref expand); }

            Guid togIid = typeof(IUIAutomationTogglePattern).GUID;
            IUIAutomationTogglePattern* toggle = null;
            try
            {
                toggle = (IUIAutomationTogglePattern*)like->GetCurrentPatternAs(
                    UIA_PATTERN_ID.UIA_TogglePatternId, &togIid);
                if (toggle != null) { toggle->Toggle(); return true; }
            }
            catch { }
            finally { NativeUia.SafeRelease(ref toggle); }

            Guid invIid = typeof(IUIAutomationInvokePattern).GUID;
            IUIAutomationInvokePattern* invoke = null;
            try
            {
                invoke = (IUIAutomationInvokePattern*)like->GetCurrentPatternAs(
                    UIA_PATTERN_ID.UIA_InvokePatternId, &invIid);
                if (invoke != null) { invoke->Invoke(); return true; }
            }
            catch { }
            finally { NativeUia.SafeRelease(ref invoke); }

            return false;
        }
        finally
        {
            NativeUia.SafeRelease(ref like);
            NativeUia.SafeRelease(ref trackInfo);
        }
    });

    public bool AddToFavoritesByClick()
    {
        if (!Monitor.TryEnter(_lock, TimeSpan.FromSeconds(3)))
            return false;
        try
        {
            IUIAutomationElement* trackInfo = null;
            IUIAutomationElement* like = null;
            IUIAutomationElement* trackInfo2 = null;
            IUIAutomationElement* like2 = null;
            IUIAutomationElement* trackInfo3 = null;
            IUIAutomationElement* like3 = null;
            try
            {
                if (!EnsureUia()) return false;
                EnsureGroups();
                trackInfo = FindTrackInfoGroup();
                like = FindLikeButton(trackInfo);
                if (like == null) return false;
                if (IsLikedName(NativeUia.GetCurrentName(like)))
                    return true;

                IntPtr wnd = SpotifyActions.FindSpotifyMainWindow();
                if (wnd == IntPtr.Zero) return false;

                IntPtr prevFg = Interop.GetForegroundWindow();
                bool wasMinimized = Interop.IsIconic(wnd);
                Interop.GetCursorPos(out var prevCursor);
                try
                {
                    if (wasMinimized)
                        Interop.ShowWindow(wnd, Interop.SW_RESTORE);
                    Interop.SetForegroundWindow(wnd);
                    Thread.Sleep(450);

                    trackInfo2 = FindTrackInfoGroup();
                    like2 = FindLikeButton(trackInfo2);
                    if (like2 == null) return false;
                    if (!NativeUia.TryGetCurrentRect(like2, out RECT r)) return false;

                    int cx = (r.left + r.right) / 2;
                    int cy = (r.top + r.bottom) / 2;
                    Interop.SetCursorPos(cx, cy);
                    Thread.Sleep(60);
                    Interop.mouse_event(Interop.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                    Interop.mouse_event(Interop.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(350);

                    trackInfo3 = FindTrackInfoGroup();
                    like3 = FindLikeButton(trackInfo3);
                    string after = NativeUia.GetCurrentName(like3);
                    return IsLikedName(after);
                }
                finally
                {
                    Interop.SetCursorPos(prevCursor.X, prevCursor.Y);
                    if (wasMinimized)
                        Interop.ShowWindow(wnd, Interop.SW_MINIMIZE);
                    if (prevFg != IntPtr.Zero)
                        Interop.SetForegroundWindow(prevFg);
                }
            }
            catch
            {
                Invalidate();
                return false;
            }
            finally
            {
                NativeUia.SafeRelease(ref like);
                NativeUia.SafeRelease(ref trackInfo);
                NativeUia.SafeRelease(ref like2);
                NativeUia.SafeRelease(ref trackInfo2);
                NativeUia.SafeRelease(ref like3);
                NativeUia.SafeRelease(ref trackInfo3);
            }
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    private bool DoWithRetry(Func<bool?> action)
    {
        if (!Monitor.TryEnter(_lock, TimeSpan.FromSeconds(3)))
            return false;
        try
        {
            if (!EnsureUia()) return false;
            IntPtr fg = Interop.GetForegroundWindow();
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        EnsureGroups();
                        bool? result = action();
                        if (result is bool ok) return ok;
                    }
                    catch { }
                    Invalidate();
                }
                return false;
            }
            finally
            {
                RestoreForeground(fg);
            }
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    private bool HasDescendant(IUIAutomationElement* parent, IUIAutomationCondition* cond)
    {
        if (parent == null || cond == null) return false;
        IUIAutomationElement* found = null;
        try
        {
            found = parent->FindFirst(TreeScope.TreeScope_Descendants, cond);
            return found != null;
        }
        catch { return false; }
        finally { NativeUia.SafeRelease(ref found); }
    }

    private IUIAutomationElement* FindTrackInfoInBar(IUIAutomationElement* bar)
    {
        if (bar == null) return null;
        IUIAutomationElementArray* siblings = null;
        try
        {
            siblings = bar->FindAll(TreeScope.TreeScope_Children, _groupCond);
            if (siblings == null) return null;
            int count = siblings->Length;
            for (int i = 0; i < count; i++)
            {
                IUIAutomationElement* sibling = siblings->GetElement(i);
                if (sibling == null) continue;
                if (HasDescendant(sibling, _hyperlinkCond) && HasDescendant(sibling, _buttonCond))
                    return sibling;
                NativeUia.SafeRelease(ref sibling);
            }
            return null;
        }
        catch { return null; }
        finally { NativeUia.SafeRelease(ref siblings); }
    }

    private IUIAutomationElement* FindTrackInfoGroup()
    {
        if (_controlsGroup == null) return null;
        IUIAutomationElement* bar = NativeUia.GetParent(_walker, _controlsGroup);
        if (bar == null) return null;
        try
        {
            return FindTrackInfoInBar(bar);
        }
        finally
        {
            NativeUia.SafeRelease(ref bar);
        }
    }

    private IUIAutomationElement* FindLikeButton(IUIAutomationElement* trackInfo)
    {
        if (trackInfo == null) return null;
        IUIAutomationElementArray* buttons = null;
        try
        {
            buttons = trackInfo->FindAll(TreeScope.TreeScope_Descendants, _buttonCond);
            if (buttons == null) return null;
            int count = buttons->Length;
            return count > 0 ? buttons->GetElement(count - 1) : null;
        }
        catch { return null; }
        finally { NativeUia.SafeRelease(ref buttons); }
    }

    private void Invalidate() => NativeUia.SafeRelease(ref _controlsGroup);

    private void EnsureGroups()
    {
        if (_controlsGroup != null)
        {
            try
            {
                _ = _controlsGroup->CurrentControlType;
                return;
            }
            catch
            {
                Invalidate();
            }
        }

        var procs = Process.GetProcessesByName("Spotify");
        try
        {
            foreach (var proc in procs)
            {
                if (proc.MainWindowHandle == IntPtr.Zero) continue;
                IUIAutomationElement* root = null;
                try
                {
                    root = _uia->ElementFromHandle((HWND)proc.MainWindowHandle);
                    if (root != null && FindInWindow(root)) return;
                }
                catch { }
                finally
                {
                    NativeUia.SafeRelease(ref root);
                }
            }
        }
        finally
        {
            foreach (var proc in procs) proc.Dispose();
        }
    }

    private bool FindInWindow(IUIAutomationElement* root)
    {
        IUIAutomationElementArray* checkboxes = null;
        try
        {
            checkboxes = root->FindAll(TreeScope.TreeScope_Descendants, _checkBoxCond);
            if (checkboxes == null) return false;
            int count = checkboxes->Length;
            for (int i = 0; i < count; i++)
            {
                IUIAutomationElement* checkbox = null;
                IUIAutomationElement* controls = null;
                IUIAutomationElementArray* buttons = null;
                IUIAutomationElement* bar = null;
                IUIAutomationElement* trackInfo = null;
                try
                {
                    checkbox = checkboxes->GetElement(i);
                    if (checkbox == null) continue;

                    controls = NativeUia.GetParent(_walker, checkbox);
                    if (controls == null) continue;

                    buttons = controls->FindAll(TreeScope.TreeScope_Children, _buttonCond);
                    if (buttons == null || buttons->Length != 4) continue;

                    bar = NativeUia.GetParent(_walker, controls);
                    if (bar == null) continue;

                    trackInfo = FindTrackInfoInBar(bar);
                    if (trackInfo == null) continue;

                    _controlsGroup = controls;
                    controls = null;
                    return true;
                }
                catch { }
                finally
                {
                    NativeUia.SafeRelease(ref trackInfo);
                    NativeUia.SafeRelease(ref bar);
                    NativeUia.SafeRelease(ref buttons);
                    NativeUia.SafeRelease(ref controls);
                    NativeUia.SafeRelease(ref checkbox);
                }
            }
            return false;
        }
        catch { return false; }
        finally
        {
            NativeUia.SafeRelease(ref checkboxes);
        }
    }

    private static void RestoreForeground(IntPtr before)
    {
        if (before == IntPtr.Zero) return;
        Thread.Sleep(80);
        if (Interop.GetForegroundWindow() != before)
            Interop.SetForegroundWindow(before);
    }
}
