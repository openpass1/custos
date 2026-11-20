using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media;
using Custos.Desktop;

[assembly: SupportedOSPlatform("windows")]

namespace Custos.Windows;

internal static class Program
{
    /// <summary>Given at logon: start in the tray, without the window.</summary>
    private const string TrayArgument = "--tray";

    // Local\ objects exist once per Windows session, so every session gets its own interface (section 3.1). The names
    // are those of the Windows Forms Custos.exe this program replaced.
    private const string InstanceMutexName = @"Local\CUSTOS.Gui";
    private const string ActivateEventName = @"Local\CUSTOS.Gui.Activate";

    /// <summary>
    /// <c>Custos.exe [--tray] [--demo]</c>: the window and the tray icon of the CUSTOS service on this computer;
    /// <c>--demo</c> shows made-up tasks and needs no service. A copy started from the tray menu with administrator
    /// rights only starts or stops the service.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is [ServiceControl.StartArgument or ServiceControl.StopArgument])
        {
            return ServiceControl.Run(start: args[0] == ServiceControl.StartArgument);
        }

        using var instance = new Mutex(initiallyOwned: false, InstanceMutexName, out bool firstInSession);
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        if (!firstInSession)
        {
            // Already running in this session: ask that copy to show its window and quit.
            activate.Set();
            return 0;
        }

        App.Demo = args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase) || a.Equals("/demo", StringComparison.OrdinalIgnoreCase));
        App.StartHidden = args.Contains(TrayArgument, StringComparer.OrdinalIgnoreCase);
        DesktopPlatform.Current = new WindowsPlatform();
        ErrorLog.Install();

        var wait = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => App.Activate(), null, Timeout.Infinite, executeOnlyOnce: false);
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            wait.Unregister(null);
        }
    }

    /// <summary>Also used by the XAML previewer of an IDE.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new FontManagerOptions { DefaultFamilyName = Fonts.Roboto })
        .LogToTrace();
}
