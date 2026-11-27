using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace SpotDock;

/// <summary>
/// Locates, through native COM UI Automation, the taskbar elements that anchor
/// the widget: the widgets/weather button (left limit) and the Start button
/// (right limit). Values come back in physical screen pixels.
/// </summary>
internal static unsafe class TaskbarAnchors
{
    private static readonly object _lock = new();
    private static IUIAutomation* _uia;
    private static IntPtr _cachedTray;
    private static IUIAutomationElement* _widgetsEl;
    private static IUIAutomationElement* _startEl;
    private static IUIAutomationElement* _rootEl;

    private static IUIAutomationCondition* _widgetsIdCond;
    private static IUIAutomationCondition* _startIdCond;
    private static IUIAutomationCondition* _widgetContentCond;
    private static IUIAutomationCondition* _buttonCond;
    private static IUIAutomationCacheRequest* _widgetCacheReq;
    private static IUIAutomationCacheRequest* _buttonCacheReq;

    private static DateTime _lastStartProbeAt = DateTime.MinValue;
    private static DateTime _lastWidgetsProbeAt = DateTime.MinValue;
    private static readonly TimeSpan StartRetryInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WidgetsRetryInterval = TimeSpan.FromSeconds(30);

    private static int _failStreak;
    private const int FailsBeforeRefind = 5;

    private static bool EnsureUia()
    {
        NativeUia.EnsureComInitialized();
        if (_uia != null) return true;
        _uia = NativeUia.CreateAutomation();
        if (_uia == null) return false;

        try
        {
            _widgetsIdCond = NativeUia.CreateStringPropertyCondition(
                _uia, UIA_PROPERTY_ID.UIA_AutomationIdPropertyId, "WidgetsButton");
            _startIdCond = NativeUia.CreateStringPropertyCondition(
                _uia, UIA_PROPERTY_ID.UIA_AutomationIdPropertyId, "StartButton");

            IUIAutomationCondition* textCond = NativeUia.CreateIntPropertyCondition(
                _uia, UIA_PROPERTY_ID.UIA_ControlTypePropertyId, (int)UIA_CONTROLTYPE_ID.UIA_TextControlTypeId);
            IUIAutomationCondition* imgCond = NativeUia.CreateIntPropertyCondition(
                _uia, UIA_PROPERTY_ID.UIA_ControlTypePropertyId, (int)UIA_CONTROLTYPE_ID.UIA_ImageControlTypeId);
            _widgetContentCond = NativeUia.CreateOrCondition(_uia, textCond, imgCond);
            NativeUia.SafeRelease(ref textCond);
            NativeUia.SafeRelease(ref imgCond);

            _buttonCond = NativeUia.CreateIntPropertyCondition(
                _uia, UIA_PROPERTY_ID.UIA_ControlTypePropertyId, (int)UIA_CONTROLTYPE_ID.UIA_ButtonControlTypeId);

            IUIAutomationCacheRequest* wcr = _uia->CreateCacheRequest();
            if (wcr != null)
            {
                wcr->AddProperty(UIA_PROPERTY_ID.UIA_NamePropertyId);
                wcr->AddProperty(UIA_PROPERTY_ID.UIA_BoundingRectanglePropertyId);
                _widgetCacheReq = wcr;
            }

            IUIAutomationCacheRequest* bcr = _uia->CreateCacheRequest();
            if (bcr != null)
            {
                bcr->AddProperty(UIA_PROPERTY_ID.UIA_ClassNamePropertyId);
                bcr->AddProperty(UIA_PROPERTY_ID.UIA_BoundingRectanglePropertyId);
                _buttonCacheReq = bcr;
            }
        }
        catch
        {
            return false;
        }

        return _widgetsIdCond != null && _startIdCond != null &&
               _widgetContentCond != null && _buttonCond != null &&
               _widgetCacheReq != null && _buttonCacheReq != null;
    }

    private static void Invalidate()
    {
        _cachedTray = IntPtr.Zero;
        NativeUia.SafeRelease(ref _widgetsEl);
        NativeUia.SafeRelease(ref _startEl);
        NativeUia.SafeRelease(ref _rootEl);
        _lastStartProbeAt = DateTime.MinValue;
        _lastWidgetsProbeAt = DateTime.MinValue;
        _failStreak = 0;
    }

    private static (bool, double?, double?, double?) Fail()
    {
        if (++_failStreak >= FailsBeforeRefind) Invalidate();
        return (false, null, null, null);
    }

    private static IUIAutomationElement* TryRoot(IntPtr tray)
    {
        if (_uia == null) return null;
        try { return _uia->ElementFromHandle((HWND)tray); }
        catch { return null; }
    }

    private static IUIAutomationElement* FindFirstDescendant(
        IUIAutomationElement* parent, IUIAutomationCondition* cond)
    {
        if (parent == null || cond == null) return null;
        try { return parent->FindFirst(TreeScope.TreeScope_Descendants, cond); }
        catch { return null; }
    }

    public static (bool Ok, double? widgetsRight, double? startLeft, double? taskButtonsRight) Get(
        IntPtr tray, bool includeTaskButtons = true)
    {
        lock (_lock)
        {
            double? widgetsRight = null, startLeft = null, taskButtonsRight = null;
            try
            {
                if (!EnsureUia()) return (false, null, null, null);

                var now = DateTime.UtcNow;
                if (tray != _cachedTray)
                {
                    Invalidate();
                    _rootEl = TryRoot(tray);
                    if (_rootEl != null)
                    {
                        _widgetsEl = FindFirstDescendant(_rootEl, _widgetsIdCond);
                        _startEl = FindFirstDescendant(_rootEl, _startIdCond);
                    }
                    _cachedTray = tray;
                    _lastWidgetsProbeAt = now;
                    _lastStartProbeAt = now;
                }
                else
                {
                    if (_startEl == null && now - _lastStartProbeAt >= StartRetryInterval)
                    {
                        _lastStartProbeAt = now;
                        if (_rootEl == null) _rootEl = TryRoot(tray);
                        if (_rootEl != null)
                        {
                            _startEl = FindFirstDescendant(_rootEl, _startIdCond);
                            if (_startEl == null) NativeUia.SafeRelease(ref _rootEl);
                        }
                    }
                    if (_widgetsEl == null && now - _lastWidgetsProbeAt >= WidgetsRetryInterval)
                    {
                        _lastWidgetsProbeAt = now;
                        if (_rootEl == null) _rootEl = TryRoot(tray);
                        if (_rootEl != null)
                        {
                            _widgetsEl = FindFirstDescendant(_rootEl, _widgetsIdCond);
                            if (_widgetsEl == null) NativeUia.SafeRelease(ref _rootEl);
                        }
                    }
                }

                if (_widgetsEl != null)
                {
                    if (!NativeUia.TryGetCurrentRect(_widgetsEl, out RECT r))
                    {
                        Invalidate();
                        return (false, null, null, null);
                    }
                    widgetsRight = r.right;

                    IUIAutomationElementArray* found = null;
                    try
                    {
                        found = _widgetsEl->FindAllBuildCache(
                            TreeScope.TreeScope_Descendants, _widgetContentCond, _widgetCacheReq);
                        if (found == null)
                        {
                            Invalidate();
                            return (false, null, null, null);
                        }

                        string shown = NativeUia.GetCurrentName(_widgetsEl);
                        int count = found->Length;

                        double? contentRight = null;
                        double? anyRight = null;
                        for (int i = 0; i < count; i++)
                        {
                            IUIAutomationElement* el = null;
                            try
                            {
                                el = found->GetElement(i);
                                if (el == null) continue;
                                if (!NativeUia.TryGetCachedRect(el, out RECT er) &&
                                    !NativeUia.TryGetCurrentRect(el, out er))
                                    continue;

                                if (anyRight is not double any || er.right > any) anyRight = er.right;

                                string text = NativeUia.GetCachedName(el);
                                if (text.Length == 0) text = NativeUia.GetCurrentName(el);
                                if (text.Length > 0 && shown.Length > 0 &&
                                    !shown.Contains(text, StringComparison.Ordinal))
                                    continue;

                                if (contentRight is not double cur || er.right > cur)
                                    contentRight = er.right;
                            }
                            catch { }
                            finally
                            {
                                NativeUia.SafeRelease(ref el);
                            }
                        }

                        contentRight ??= anyRight;
                        if (contentRight is double cr && cr > r.left && cr < r.right)
                            widgetsRight = cr;
                        else
                            return Fail();
                    }
                    finally
                    {
                        NativeUia.SafeRelease(ref found);
                    }
                }

                if (_startEl != null)
                {
                    if (NativeUia.TryGetCurrentRect(_startEl, out RECT r))
                        startLeft = r.left;
                }

                if (!includeTaskButtons)
                {
                    _failStreak = 0;
                    return (true, widgetsRight, startLeft, null);
                }

                if (_rootEl == null) _rootEl = TryRoot(tray);
                if (_rootEl == null)
                {
                    _failStreak = 0;
                    return (true, widgetsRight, startLeft, null);
                }

                IUIAutomationElementArray* buttons = null;
                try
                {
                    buttons = _rootEl->FindAllBuildCache(
                        TreeScope.TreeScope_Descendants, _buttonCond, _buttonCacheReq);
                    if (buttons != null)
                    {
                        int count = buttons->Length;
                        for (int i = 0; i < count; i++)
                        {
                            IUIAutomationElement* button = null;
                            try
                            {
                                button = buttons->GetElement(i);
                                if (button == null) continue;

                                string cls = NativeUia.GetCachedClassName(button);
                                if (cls.Length == 0) cls = NativeUia.GetCurrentClassName(button);
                                if (!cls.StartsWith("Taskbar.TaskListButton", StringComparison.Ordinal))
                                    continue;

                                if (!NativeUia.TryGetCachedRect(button, out RECT br) &&
                                    !NativeUia.TryGetCurrentRect(button, out br))
                                    continue;

                                if (taskButtonsRight is not double cur || br.right > cur)
                                    taskButtonsRight = br.right;
                            }
                            catch { }
                            finally
                            {
                                NativeUia.SafeRelease(ref button);
                            }
                        }
                    }
                }
                finally
                {
                    NativeUia.SafeRelease(ref buttons);
                }

                _failStreak = 0;
                return (true, widgetsRight, startLeft, taskButtonsRight);
            }
            catch
            {
                Invalidate();
                return (false, null, null, null);
            }
        }
    }
}
