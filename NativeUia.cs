using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Accessibility;

namespace SpotDock;

internal static unsafe class NativeUia
{
    [ThreadStatic]
    private static bool _comInitialized;

    public static void EnsureComInitialized()
    {
        if (_comInitialized) return;
        PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
        _comInitialized = true;
    }

    public static IUIAutomation* CreateAutomation()
    {
        EnsureComInitialized();
        Guid clsid = typeof(CUIAutomation).GUID;
        Guid iid = IUIAutomation.IID_Guid;
        IUIAutomation* uia = null;
        HRESULT hr = PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&uia);
        return hr.Succeeded ? uia : null;
    }

    public static void SafeRelease<T>(ref T* ptr) where T : unmanaged
    {
        if (ptr != null)
        {
            ((IUnknown*)ptr)->Release();
            ptr = null;
        }
    }

    public static IUIAutomationCondition* CreateStringPropertyCondition(
        IUIAutomation* uia, UIA_PROPERTY_ID propId, string value)
    {
        fixed (char* pVal = value)
        {
            BSTR bstr = PInvoke.SysAllocString(pVal);
            if (bstr.Value == null) return null;
            try
            {
                VARIANT v = default;
                v.Anonymous.Anonymous.vt = VARENUM.VT_BSTR;
                v.Anonymous.Anonymous.Anonymous.bstrVal = bstr;
                return uia->CreatePropertyCondition(propId, v);
            }
            catch
            {
                return null;
            }
            finally
            {
                PInvoke.SysFreeString(bstr);
            }
        }
    }

    public static IUIAutomationCondition* CreateIntPropertyCondition(
        IUIAutomation* uia, UIA_PROPERTY_ID propId, int value)
    {
        try
        {
            VARIANT v = default;
            v.Anonymous.Anonymous.vt = VARENUM.VT_I4;
            v.Anonymous.Anonymous.Anonymous.lVal = value;
            return uia->CreatePropertyCondition(propId, v);
        }
        catch
        {
            return null;
        }
    }

    public static IUIAutomationCondition* CreateOrCondition(
        IUIAutomation* uia, IUIAutomationCondition* c1, IUIAutomationCondition* c2)
    {
        if (c1 == null || c2 == null) return null;
        try { return uia->CreateOrCondition(c1, c2); }
        catch { return null; }
    }

    public static string ConsumeBstr(BSTR bstr)
    {
        if (bstr.Value == null) return "";
        try
        {
            return Marshal.PtrToStringBSTR(bstr) ?? "";
        }
        finally
        {
            PInvoke.SysFreeString(bstr);
        }
    }

    public static string GetCurrentName(IUIAutomationElement* el)
    {
        if (el == null) return "";
        try { return ConsumeBstr(el->CurrentName); }
        catch { return ""; }
    }

    public static string GetCachedName(IUIAutomationElement* el)
    {
        if (el == null) return "";
        try { return ConsumeBstr(el->CachedName); }
        catch { return ""; }
    }

    public static string GetCachedClassName(IUIAutomationElement* el)
    {
        if (el == null) return "";
        try { return ConsumeBstr(el->CachedClassName); }
        catch { return ""; }
    }

    public static string GetCurrentClassName(IUIAutomationElement* el)
    {
        if (el == null) return "";
        try { return ConsumeBstr(el->CurrentClassName); }
        catch { return ""; }
    }

    public static bool TryGetCurrentRect(IUIAutomationElement* el, out RECT rect)
    {
        rect = default;
        if (el == null) return false;
        try
        {
            RECT r = el->CurrentBoundingRectangle;
            if (IsEmptyRect(r)) return false;
            rect = r;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetCachedRect(IUIAutomationElement* el, out RECT rect)
    {
        rect = default;
        if (el == null) return false;
        try
        {
            RECT r = el->CachedBoundingRectangle;
            if (IsEmptyRect(r)) return false;
            rect = r;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsEmptyRect(RECT r) => r.right <= r.left || r.bottom <= r.top;

    public static IUIAutomationElement* GetParent(IUIAutomationTreeWalker* walker, IUIAutomationElement* el)
    {
        if (walker == null || el == null) return null;
        try { return walker->GetParentElement(el); }
        catch { return null; }
    }
}
