using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Custos.Core.Agent;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>
/// The log of one run (section 6.10), opened from the run history. The service reads the file and sends the lines that
/// match the filter, so even the log of a big run with a line for every file opens fast (RunLogForm of Custos.exe).
/// </summary>
internal sealed partial class RunLogWindow : Window
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(2);
    private static readonly string[] Levels = [LogFilterLevels.All, LogFilterLevels.Problems, LogFilterLevels.Errors];

    private readonly ServiceMonitor? _monitor;
    private readonly RunRecord _run;
    private readonly DispatcherTimer _typing = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private string? _path;
    private int _request;

    /// <summary>Only for the XAML previewer of an IDE.</summary>
    public RunLogWindow()
        : this(null, new RunRecord
        {
            RunId = string.Empty,
            TaskId = string.Empty,
            TaskName = string.Empty,
            BackupType = BackupTypes.Full,
            Trigger = string.Empty,
            StartedAt = DateTimeOffset.Now,
        })
    {
    }

    /// <param name="monitor">The connection to the service; null shows only what <see cref="ShowResult"/> is given.</param>
    public RunLogWindow(ServiceMonitor? monitor, RunRecord run)
    {
        _monitor = monitor;
        _run = run;
        InitializeComponent();
        Title = Strings.Format(Strings.RunLogTitle, run.TaskName, Describe.Time(run.StartedAt));
        Level.SelectionChanged += (_, _) => Reload();
        Search.TextChanged += (_, _) =>
        {
            _typing.Stop();
            _typing.Start();
        };
        _typing.Tick += (_, _) =>
        {
            _typing.Stop();
            Reload();
        };
        Opened += (_, _) =>
        {
            TitleBar.Paint(this);
            Reload();
        };
        ActualThemeVariantChanged += (_, _) => TitleBar.Paint(this);
        Closed += (_, _) => _typing.Stop();
    }

    /// <summary>«Показывать» without the colon of the old forms.</summary>
    public static string ShowLabel => Strings.JournalShow.TrimEnd(':', ' ');

    /// <summary>Shows what the service sent: the lines, or why there are none.</summary>
    internal void ShowResult(RunLogResult result)
    {
        _path = result.Found ? result.Path : null;
        ShowFile.IsEnabled = _path is not null;
        Lines.ItemsSource = result.Lines.Select(MainViewModel.Row).ToList();
        Status.Text = !result.Found ? Strings.Format(Strings.RunLogGone, result.RetentionDays)
            : result.Matched > result.Lines.Count
                ? Strings.Format(Strings.RunLogTruncated, Describe.Number(result.Total), Describe.Number(result.Matched), Describe.Number(result.Lines.Count))
            : Strings.Format(Strings.RunLogCount, Describe.Number(result.Total), Describe.Number(result.Lines.Count));
    }

    private void OnRefresh(object? sender, RoutedEventArgs e) => Reload();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnShowFile(object? sender, RoutedEventArgs e)
    {
        if (_path is not null && Path.GetDirectoryName(_path) is { } folder)
        {
            Safe.Run(async () => await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder)));
        }
    }

    private void Reload()
    {
        if (_monitor is null || !IsLoaded)
        {
            return;
        }

        int request = ++_request;
        var ask = new RunLogRequest
        {
            RunId = _run.RunId,
            Level = Levels[Math.Max(0, Level.SelectedIndex)],
            Search = string.IsNullOrWhiteSpace(Search.Text) ? null : Search.Text.Trim(),
        };
        Busy.IsVisible = true;
        Safe.Run(async () =>
        {
            try
            {
                var result = await _monitor.RequestAsync(PipeProtocol.Methods.RunLog, ask,
                    IpcJsonContext.Default.RunLogRequest, IpcJsonContext.Default.RunLogResult, ReadTimeout);
                if (request == _request)
                {
                    ShowResult(result);
                }
            }
            finally
            {
                if (request == _request)
                {
                    Busy.IsVisible = false;
                }
            }
        }, ex => Status.Text = ex.Message);
    }
}
