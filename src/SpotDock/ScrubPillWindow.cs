using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SpotDock;

internal sealed unsafe class ScrubPillWindow : IDisposable
{
    private const string ClassName = "SpotDock_ScrubPill";
    private static ushort _classAtom;

    private HWND _hwnd;
    private readonly LayeredSurface _surface = new();
    private bool _visible;

    public bool IsVisible => _visible;

    public void EnsureCreated()
    {
        if (_hwnd != HWND.Null) return;
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
                WINDOW_EX_STYLE.WS_EX_TRANSPARENT |
                WINDOW_EX_STYLE.WS_EX_NOACTIVATE |
                WINDOW_EX_STYLE.WS_EX_TOOLWINDOW |
                WINDOW_EX_STYLE.WS_EX_TOPMOST;

            _hwnd = PInvoke.CreateWindowEx(
                exStyle,
                pClass,
                null,
                WINDOW_STYLE.WS_POPUP,
                0, 0, 1, 1,
                HWND.Null,
                HMENU.Null,
                hMod,
                null);
        }
    }

    public void ShowAt(int centerScreenX, int bottomScreenY, string posText, string durText, bool lightTheme, double dpiScale)
    {
        EnsureCreated();
        if (_hwnd == HWND.Null) return;

        var (wPx, hPx) = D2DRenderer.Shared.MeasureScrubPill(posText, durText, dpiScale, out float posW);
        if (wPx <= 0 || hPx <= 0) return;

        var ptDst = new Point
        {
            X = centerScreenX - wPx / 2,
            Y = bottomScreenY - hPx,
        };

        D2DRenderer.Shared.RenderScrubPill(_surface, _hwnd, &ptDst, wPx, hPx, posText, durText, posW, lightTheme, dpiScale);
        if (!_visible)
        {
            Interop.ShowWindow((IntPtr)_hwnd.Value, Interop.SW_SHOWNOACTIVATE);
            _visible = true;
        }
        Interop.EnsureTopmost((IntPtr)_hwnd.Value);
    }

    public void Hide()
    {
        if (_hwnd != HWND.Null && _visible)
        {
            Interop.ShowWindow((IntPtr)_hwnd.Value, 0 /*SW_HIDE*/);
            _visible = false;
        }
    }

    public void Dispose()
    {
        Hide();
        if (_hwnd != HWND.Null)
        {
            PInvoke.DestroyWindow(_hwnd);
            _hwnd = HWND.Null;
        }
        _surface.Dispose();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam) =>
        PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
}
