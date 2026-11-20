using Avalonia.Threading;
using Custos.Core;

namespace Custos.Windows;

/// <summary>
/// A mistake in the window must not close it silently: the details go to <c>%APPDATA%\CUSTOS\desktop-errors.log</c>
/// (next to ui.json) and the window keeps working where it can. Backups do not depend on it anyway.
/// </summary>
internal static class ErrorLog
{
    /// <summary>The file grows up to this size, then starts anew.</summary>
    private const long MaxBytes = 1024 * 1024;

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CUSTOS", "desktop-errors.log");

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(e.Exception);
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Write(e.Exception);
            e.Handled = true;
        };
    }

    public static void Write(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var file = new FileInfo(path);
            if (file.Exists && file.Length > MaxBytes)
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
