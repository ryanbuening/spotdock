using System.Reflection;

namespace SpotDock;

internal static class Program
{
    private static Mutex? _mutex;

    public static bool IntentionalExit;

    [STAThread]
    private static void Main()
    {
        _mutex = new Mutex(true, "SpotDock_SingleInstance", out bool isNew);
        if (!isNew)
        {
            Diag.Log("Another copy is already running — exiting.");
            IntentionalExit = true;
            return;
        }

        Diag.Log($"Started, version {Assembly.GetExecutingAssembly().GetName().Version}.");
        TestSwitches.LogActive();

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Diag.Log(ex.ToString());
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Diag.Log(args.Exception.ToString());
            args.SetObserved();
        };

        UiThread.Initialize();
        if (!D2DRenderer.Shared.Initialize())
        {
            Diag.Log("Direct2D / DirectWrite failed to initialize — exiting.");
            IntentionalExit = true;
            UiThread.Cleanup();
            return;
        }

        WidgetWindow.ShowPrimary();
        if (!WidgetWindow.HasWindow)
        {
            Diag.Log("No widget window could be created at startup — exiting so the single-instance mutex is released.");
            IntentionalExit = true;
            D2DRenderer.Shared.Dispose();
            UiThread.Cleanup();
            return;
        }

        try
        {
            UiThread.RunMessageLoop();
        }
        finally
        {
            MediaHub.Shared.Shutdown();
            D2DRenderer.Shared.Dispose();
            UiThread.Cleanup();
        }
    }
}
