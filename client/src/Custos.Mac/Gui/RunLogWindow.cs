using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Custos.Core.Agent;
using Custos.Core.Ipc;
using Custos.Gui;

namespace Custos.Mac.Gui;

/// <summary>The log of one run (a double click in «История запусков»): its lines, filtered by level and text.</summary>
internal static class RunLogWindow
{
    public static async Task ShowAsync(Window owner, ServiceMonitor monitor, string runId, string taskName, DateTimeOffset started)
    {
        var window = new Window
        {
            Title = Strings.Format(Strings.RunLogTitle, taskName, Describe.Time(started)),
            Width = 1000,
            Height = 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var level = Ui.Choice([(LogFilterLevels.All, Strings.LogShowAll), (LogFilterLevels.Problems, Strings.LogShowProblems),
            (LogFilterLevels.Errors, Strings.LogShowErrors)], LogFilterLevels.All, 240);
        var search = Ui.TextBox(null, Strings.LogSearch, 260);
        var text = new SelectableTextBlock { FontFamily = new FontFamily("Menlo, monospace"), FontSize = 12.5, TextWrapping = TextWrapping.NoWrap };
        var note = Ui.Note(string.Empty);
        var refresh = Ui.Outlined(Strings.RunLogRefresh, () => { });
        refresh.OnClick(LoadAsync, ex => note.Text = ex.Message);
        level.SelectionChanged += (_, _) => Safe.Run(LoadAsync, ex => note.Text = ex.Message);
        search.KeyUp += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                Safe.Run(LoadAsync, ex => note.Text = ex.Message);
            }
        };
        var top = Ui.Horizontal(level, search, refresh);
        top.Margin = new Thickness(16, 12);
        note.Margin = new Thickness(16, 0, 16, 8);
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(note, Dock.Bottom);
        dock.Children.Add(top);
        dock.Children.Add(note);
        dock.Children.Add(new Border
        {
            Classes = { "pane" },
            Margin = new Thickness(12, 0, 12, 8),
            Child = new ScrollViewer { Content = text, Padding = new Thickness(12), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
        });
        window.Content = dock;
        window.Opened += (_, _) => Safe.Run(LoadAsync, ex => note.Text = ex.Message);
        await window.ShowDialog(owner);

        async Task LoadAsync()
        {
            var result = await monitor.RequestAsync(PipeProtocol.Methods.RunLog,
                new RunLogRequest { RunId = runId, Level = Ui.Read(level), Search = string.IsNullOrWhiteSpace(search.Text) ? null : search.Text.Trim() },
                IpcJsonContext.Default.RunLogRequest, IpcJsonContext.Default.RunLogResult, TimeSpan.FromMinutes(1));
            if (!result.Found)
            {
                text.Text = string.Empty;
                note.Text = Strings.Format(Strings.RunLogGone, result.RetentionDays);
                return;
            }

            text.Text = string.Join('\n', result.Lines.Select(l => $"{l.Time.ToLocalTime():HH:mm:ss}  {Level(l.Level)}  {l.Text}"));
            note.Text = (result.Matched > result.Lines.Count
                ? Strings.Format(Strings.RunLogTruncated, result.Total, result.Matched, result.Lines.Count)
                : Strings.Format(Strings.RunLogCount, result.Total, result.Lines.Count)) + (result.Path is { } path ? "  ·  " + path : string.Empty);
        }

        static string Level(string level) => level switch
        {
            LogLineLevels.Error => "ERR",
            LogLineLevels.Warning => "WRN",
            LogLineLevels.Detail => "   ",
            _ => "   ",
        };
    }
}
