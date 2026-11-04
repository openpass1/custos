using System.Globalization;
using System.Runtime.ExceptionServices;

// The windows of one test must not meet the windows of another.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Custos.Gui.Tests;

/// <summary>
/// Runs a test the way the interface runs: on its own STA thread with the Windows Forms synchronization context, in a
/// given language, with ui.json in a temporary folder. Errors raised inside window messages fail the test too.
/// </summary>
internal static class Sta
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);
    private static readonly Lock Gate = new();
    private static bool _initialized;

    public static void Run(string culture, Action test)
    {
        InitializeOnce();
        string folder = Path.Combine(Path.GetTempPath(), "custos-gui-tests", Guid.NewGuid().ToString("N"));
        UiState.Folder = folder;
        var errors = new List<Exception>();
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var language = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentCulture = language;
            CultureInfo.CurrentUICulture = language;
            Application.ThreadException += (_, e) => errors.Add(e.Exception);
            using var context = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                test();
            }
#pragma warning disable CA1031 // Whatever the test throws is thrown again on the thread of the test runner.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Window test",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        bool finished = thread.Join(Timeout);
        DeleteFolder(folder);
        if (!finished)
        {
            Assert.Fail("The test did not end within a minute: a message box is probably waiting for a click.");
        }

        failure?.Throw();
        if (errors.Count > 0)
        {
            throw new AggregateException("A window message handler failed.", errors);
        }
    }

    /// <summary>Lets the windows handle what is waiting for them: layout, painting, posted callbacks.</summary>
    public static void Pump()
    {
        for (int i = 0; i < 5; i++)
        {
            Application.DoEvents();
        }
    }

    /// <summary>All controls of a type inside a control, at any depth.</summary>
    public static List<T> Find<T>(Control root)
        where T : Control
    {
        var found = new List<T>();
        foreach (Control child in root.Controls)
        {
            if (child is T match)
            {
                found.Add(match);
            }

            found.AddRange(Find<T>(child));
        }

        return found;
    }

    /// <summary>Whether a visible label inside the control shows exactly this text.</summary>
    public static bool Shows(Control root, string text) =>
        Find<Label>(root).Exists(l => l.Visible && string.Equals(l.Text, text, StringComparison.Ordinal));

    /// <summary>Whether a visible label inside the control shows this text as part of its own.</summary>
    public static bool ShowsPart(Control root, string text) =>
        Find<Label>(root).Exists(l => l.Visible && l.Text.Contains(text, StringComparison.Ordinal));

    private static void InitializeOnce()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            // As Program does through ApplicationConfiguration, before the first window of the process.
            _initialized = true;
            Application.EnableVisualStyles();
            _ = Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        }
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the system to clean up.
        }
    }
}
