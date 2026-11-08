using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Threading;

namespace NetSwitch.App;

internal static class Program
{
    private const string InstanceMutexName = @"Local\NetSwitch.SingleInstance";
    private const string ActivateEventName = @"Local\NetSwitch.Activate";

    // Kept alive for the process lifetime: the mutex is released by the OS on exit, and the
    // listener thread owns the event handle.
    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _activateEvent;

    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsWindows() && !ClaimSingleInstance())
        {
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    [SupportedOSPlatform("windows")]
    private static bool ClaimSingleInstance()
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            SignalExistingInstance();
            return false;
        }

        _activateEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivateEventName);

        var listener = new Thread(ListenForActivation)
        {
            IsBackground = true,
            Name = "NetSwitch.Activation"
        };
        listener.Start();
        return true;
    }

    private static void ListenForActivation()
    {
        while (_activateEvent!.WaitOne())
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (App.MainWindow is { } window)
                {
                    App.Tray?.ShowMainWindow();
                    window.Show();
                    window.Activate();
                }
            });
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SignalExistingInstance()
    {
        // The first instance may still be creating its event; retry briefly instead of
        // silently starting a second instance.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var handle = EventWaitHandle.OpenExisting(ActivateEventName);
                handle.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
