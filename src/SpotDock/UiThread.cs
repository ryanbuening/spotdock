using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SpotDock;

internal static unsafe class UiThread
{
    private const uint WM_APP_DISPATCH = PInvoke.WM_APP + 1;

    private sealed class UiSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) =>
            UiThread.Post(() => d(state));

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (IsOnUiThread)
            {
                d(state);
                return;
            }
            using var done = new ManualResetEventSlim(false);
            Exception? error = null;
            UiThread.Post(() =>
            {
                try { d(state); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });
            done.Wait();
            if (error != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        public override SynchronizationContext CreateCopy() => this;
    }

    private static readonly ConcurrentQueue<Action> Queue = new();
    private static readonly UiSynchronizationContext Context = new();
    private static HWND _msgHwnd;
    private static uint _threadId;
    private static ushort _classAtom;
    private static readonly string ClassName = "SpotDock_DispatchMsg";

    public static bool IsOnUiThread => Interop.GetCurrentThreadId() == _threadId;

    public static void Initialize()
    {
        if (_msgHwnd != HWND.Null) return;
        _threadId = Interop.GetCurrentThreadId();
        SynchronizationContext.SetSynchronizationContext(Context);

        var hMod = PInvoke.GetModuleHandle((PCWSTR)null);
        fixed (char* pClass = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &MsgWndProc,
                hInstance = hMod,
                lpszClassName = pClass,
            };
            _classAtom = PInvoke.RegisterClassEx(&wc);
            _msgHwnd = PInvoke.CreateWindowEx(
                0,
                pClass,
                null,
                0,
                0, 0, 0, 0,
                HWND.HWND_MESSAGE,
                HMENU.Null,
                hMod,
                null);
        }
    }

    public static void Post(Action action)
    {
        Queue.Enqueue(action);
        if (_msgHwnd != HWND.Null)
            PInvoke.PostMessage(_msgHwnd, WM_APP_DISPATCH, default, default);
    }

    public static void Shutdown()
    {
        if (IsOnUiThread)
            PInvoke.PostQuitMessage(0);
        else
            Post(() => PInvoke.PostQuitMessage(0));
    }

    public static void RunMessageLoop()
    {
        MSG msg;
        while (PInvoke.GetMessage(&msg, HWND.Null, 0, 0) > 0)
        {
            PInvoke.TranslateMessage(&msg);
            PInvoke.DispatchMessage(&msg);
        }
    }

    public static void Cleanup()
    {
        if (_msgHwnd != HWND.Null)
        {
            PInvoke.DestroyWindow(_msgHwnd);
            _msgHwnd = HWND.Null;
        }
        if (_classAtom != 0)
        {
            fixed (char* pClass = ClassName)
                PInvoke.UnregisterClass(pClass, PInvoke.GetModuleHandle((PCWSTR)null));
            _classAtom = 0;
        }
    }

    private static void DrainQueue()
    {
        if (SynchronizationContext.Current != Context)
            SynchronizationContext.SetSynchronizationContext(Context);
        while (Queue.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { Diag.Log(ex.ToString()); }
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static LRESULT MsgWndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        if (msg == WM_APP_DISPATCH)
        {
            DrainQueue();
            return default;
        }
        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
