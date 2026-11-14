using Custos.Core;

namespace Custos.Gui;

internal static class Program
{
    /// <summary>Given by the autostart entry in the registry: start in the tray, without the window.</summary>
    private const string TrayArgument = "--tray";

    // Local\ objects exist once per Windows session, so every session gets its own interface (section 3.1).
    private const string InstanceMutexName = @"Local\CUSTOS.Gui";
    private const string ActivateEventName = @"Local\CUSTOS.Gui.Activate";

    /// <summary>The error log of the window grows up to this size, then starts anew.</summary>
    private const long MaxErrorLogBytes = 1024 * 1024;

    private static bool _showingError;

    [STAThread]
    private static int Main(string[] args)
    {
        // A copy started from the tray menu with administrator rights to start or stop the service: nothing else.
        if (args is [ServiceControl.StartArgument or ServiceControl.StopArgument])
        {
            ApplicationConfiguration.Initialize();
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

        ApplicationConfiguration.Initialize();

        // A mistake in the window must not close it silently or show the .NET crash dialog: the user gets a short
        // message, the details go to a file, and the window keeps working. Backups do not depend on it anyway.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowUnexpected(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteErrorLog(e.ExceptionObject as Exception);

        // The theme the service gave last time: the windows are created in it before the service has answered.
        Theme.Apply(LastKnown.Load().Theme);

        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        // Started at logon: only the tray icon. Started by the user (menu, installer): the window as well.
        bool showWindow = !args.Contains(TrayArgument, StringComparer.OrdinalIgnoreCase);
        using var tray = new TrayContext(activate, showWindow);
        Application.Run(tray);
        return 0;
    }

    /// <summary><c>%APPDATA%\CUSTOS\gui-errors.log</c>, next to ui.json.</summary>
    private static string ErrorLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CUSTOS", "gui-errors.log");

    private static void ShowUnexpected(Exception exception)
    {
        WriteErrorLog(exception);

        // One message at a time: an error repeated by a timer must not pile up message boxes.
        if (_showingError)
        {
            return;
        }

        _showingError = true;
        try
        {
            Ui.ShowError(null, Strings.Format(Strings.ProblemUnexpected, exception.Message, ErrorLogPath));
        }
        finally
        {
            _showingError = false;
        }
    }

    private static void WriteErrorLog(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            string path = ErrorLogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var file = new FileInfo(path);
            if (file.Exists && file.Length > MaxErrorLogBytes)
            {
                file.Delete();
            }

            File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} CUSTOS {CustosInfo.Version}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Nothing more can be done about an error that cannot even be written down.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
