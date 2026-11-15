using Custos.Core.Agent;
using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Model;

namespace Custos.Gui.Tests;

/// <summary>The "Log" tab with its filters, the run history a page at a time, and the log of a run.</summary>
public sealed class JournalTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void The_log_tab_shows_the_task_and_filters_by_level_selected_tasks_and_text(string culture) => Sta.Run(culture, () =>
    {
        using var monitor = Samples.Monitor();
        using var form = new MainForm(monitor);
        form.Show();
        Sta.Pump();
        monitor.SimulateLog(
        [
            new LogLine { Seq = 1, Time = DateTimeOffset.Now, Text = "Service started" },
            new LogLine { Seq = 2, Time = DateTimeOffset.Now, TaskId = Samples.OfficeId, TaskName = "Office documents", Text = "Started" },
            new LogLine { Seq = 3, Time = DateTimeOffset.Now, TaskId = Samples.OfficeId, TaskName = "Office documents", Level = LogLineLevels.Warning, Text = "A file is locked" },
            new LogLine { Seq = 4, Time = DateTimeOffset.Now, TaskId = Samples.PhotosId, TaskName = "Photos", Level = LogLineLevels.Error, Text = Samples.UsbProblem },
        ]);
        Sta.Pump();

        var journal = Assert.Single(Sta.Find<JournalView>(form));
        var log = journal.Log;
        Assert.Equal(4, log.Items.Count);
        Assert.Equal(3, log.Columns.Count);
        Assert.Equal(Strings.ColumnTask, log.Columns[1].Text);
        Assert.Equal("Office documents", log.Items[1].SubItems[1].Text);
        Assert.Equal(string.Empty, log.Items[0].SubItems[1].Text);
        Assert.True(Sta.Shows(journal, Strings.Format(Strings.JournalCount, 4, 4)));

        journal.Level = LogFilterLevels.Problems;
        Assert.Equal(["A file is locked", Samples.UsbProblem], Texts(log));
        journal.Level = LogFilterLevels.Errors;
        Assert.Equal(nameof(StateIcon.Error), Assert.Single(log.Items.Cast<ListViewItem>()).ImageKey);
        Assert.True(Sta.Shows(journal, Strings.Format(Strings.JournalCount, 1, 4)));

        // Only the selected tasks: with nothing selected there is nothing to follow, so all lines stay.
        journal.Level = LogFilterLevels.All;
        journal.SelectedOnly = true;
        Assert.Equal(4, log.Items.Count);
        var list = Assert.Single(Sta.Find<TaskListView>(form));
        list.SelectTask(Samples.OfficeId);
        Sta.Pump();
        Assert.Equal(["Started", "A file is locked"], Texts(log));

        // New lines go through the same filter.
        monitor.SimulateLog(
        [
            new LogLine { Seq = 5, Time = DateTimeOffset.Now, TaskId = Samples.PhotosId, TaskName = "Photos", Text = "Done" },
            new LogLine { Seq = 6, Time = DateTimeOffset.Now, TaskId = Samples.OfficeId, TaskName = "Office documents", Text = "Finished" },
        ]);
        Sta.Pump();
        Assert.Equal(["Started", "A file is locked", "Finished"], Texts(log));

        journal.SelectedOnly = false;
        journal.Search = "LOCKED";
        Assert.Equal(["A file is locked"], Texts(log));
        journal.Search = "photos";
        Assert.Equal([Samples.UsbProblem, "Done"], Texts(log));
        journal.Search = string.Empty;
        Assert.Equal(6, log.Items.Count);

        // The filter is remembered for the next window.
        journal.Level = LogFilterLevels.Problems;
        journal.SelectedOnly = true;
        form.SaveLayout();
        using var again = new MainForm(monitor);
        var next = Assert.Single(Sta.Find<JournalView>(again));
        Assert.Equal(LogFilterLevels.Problems, next.Level);
        Assert.True(next.SelectedOnly);
    });

    [Fact]
    public void Lines_the_service_no_longer_keeps_leave_the_tab() => Sta.Run("en-US", () =>
    {
        var source = new List<LogLine>();
        using var log = new LogView();
        log.Reset(source);
        for (int i = 1; i <= 5; i++)
        {
            source.Add(new LogLine { Seq = i, Time = DateTimeOffset.Now, Text = "line " + i });
        }

        log.Append(source);
        Assert.Equal(5, log.Items.Count);

        // The buffer dropped the two oldest lines and took one more.
        source.RemoveRange(0, 2);
        var added = new LogLine { Seq = 6, Time = DateTimeOffset.Now, Text = "line 6" };
        source.Add(added);
        log.Append([added]);
        Assert.Equal(["line 3", "line 4", "line 5", "line 6"], Texts(log));
    });

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void The_history_comes_a_page_at_a_time_and_opens_the_log_of_a_run(string culture) => Sta.Run(culture, () =>
    {
        using var view = new HistoryView();
        using var host = new Form { Size = new Size(1000, 400) };
        view.Dock = DockStyle.Fill;
        host.Controls.Add(view);
        host.Show();
        var start = new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
        RunRecord Run(int hour, string status) => new()
        {
            RunId = Guid.NewGuid().ToString(), TaskId = Samples.OfficeId, TaskName = "Office documents", BackupType = BackupTypes.Full,
            Trigger = RunTriggers.Schedule, StartedAt = start.AddHours(hour), FinishedAt = start.AddHours(hour).AddMinutes(1), Status = status,
        };

        var more = Assert.Single(Sta.Find<Button>(view), b => b.Text == Strings.HistoryMore);
        int asked = 0;
        view.MoreRequested += (_, _) => asked++;
        view.ShowRuns([Run(3, RunStatuses.Success), Run(2, RunStatuses.Error)], more: true, retentionDays: 365);
        Sta.Pump();
        Assert.Equal(2, view.Count);
        Assert.True(more.Visible);
        Assert.Equal(start.AddHours(2), view.Oldest);
        Assert.True(Sta.Shows(view, Strings.Format(Strings.HistoryShown, 2, Ui.Time(start.AddHours(2)))));
        more.PerformClick();
        Assert.Equal(1, asked);

        // The last page: no button, and how long the history is kept.
        view.ShowRuns([Run(1, RunStatuses.Warning)], more: false, append: true, retentionDays: 365);
        Sta.Pump();
        Assert.Equal(3, view.List.Items.Count);
        Assert.False(more.Visible);
        Assert.True(Sta.Shows(view, Strings.Format(Strings.HistoryShownAll, 3, Ui.Time(start.AddHours(1)), 365)));

        // The first item of the menu of a run (also a double click or Enter) asks for its log.
        RunRecord? opened = null;
        view.OpenLogRequested += (_, e) => opened = e.Run;
        view.List.Items[1].Selected = true;
        Sta.Pump();
        var menu = view.List.ContextMenuStrip!;
        Assert.Equal(Strings.HistoryOpenLog, menu.Items[0].Text);
        menu.Items[0].PerformClick();
        Assert.Equal(start.AddHours(2), opened?.StartedAt);

        // Only problems, and none there.
        int switched = 0;
        view.ProblemsOnlyChanged += (_, _) => switched++;
        Assert.Single(Sta.Find<CheckBox>(view), c => c.Text == Strings.HistoryProblemsOnly).Checked = true;
        Assert.Equal(1, switched);
        view.ShowRuns([]);
        Sta.Pump();
        Assert.True(Sta.Shows(view, Strings.HistoryNoProblems));
        Assert.True(Assert.Single(Sta.Find<CheckBox>(view), c => c.Text == Strings.HistoryProblemsOnly).Visible, "the filter stays to switch it off");
        view.ProblemsOnly = false;
        Assert.Equal(1, switched);
    });

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void The_log_of_a_run_says_how_much_is_shown_or_why_it_is_gone(string culture) => Sta.Run(culture, () =>
    {
        using var monitor = Samples.Monitor();
        var run = new RunRecord
        {
            RunId = Guid.NewGuid().ToString(), TaskId = Samples.OfficeId, TaskName = "Office documents", BackupType = BackupTypes.Full,
            Trigger = RunTriggers.Manual, StartedAt = DateTimeOffset.Now.AddHours(-1), Status = RunStatuses.Warning,
        };
        using var form = new RunLogForm(monitor, run);
        Assert.Contains("Office documents", form.Text, StringComparison.Ordinal);

        form.ShowResult(new RunLogResult
        {
            Found = true,
            Path = @"C:\ProgramData\CUSTOS\logs\runs\2026-11\run.log",
            Total = 3,
            Matched = 3,
            Lines =
            [
                new LogLine { Time = DateTimeOffset.Now, Text = "Started" },
                new LogLine { Time = DateTimeOffset.Now, Level = LogLineLevels.Detail, Text = "Copied a.txt" },
                new LogLine { Time = DateTimeOffset.Now, Level = LogLineLevels.Warning, Text = "Locked b.txt" },
            ],
        });
        Assert.Equal(3, form.Log.Items.Count);
        Assert.Equal(2, form.Log.Columns.Count);
        Assert.Equal(nameof(StateIcon.Warning), form.Log.Items[2].ImageKey);
        Assert.Equal(Strings.Format(Strings.RunLogCount, Ui.Number(3), Ui.Number(3)), form.StatusText);
        Assert.True(Assert.Single(Sta.Find<Button>(form), b => b.Text == Strings.RunLogShowFile).Enabled);

        form.ShowResult(new RunLogResult { Found = true, Path = "x", Total = 150000, Matched = 20000, Lines = [new LogLine { Text = "last" }] });
        Assert.Equal(Strings.Format(Strings.RunLogTruncated, Ui.Number(150000), Ui.Number(20000), Ui.Number(1)), form.StatusText);

        form.ShowResult(new RunLogResult { Found = false, RetentionDays = 180 });
        Assert.Empty(form.Log.Items);
        Assert.Equal(Strings.Format(Strings.RunLogGone, 180), form.StatusText);
        Assert.False(Assert.Single(Sta.Find<Button>(form), b => b.Text == Strings.RunLogShowFile).Enabled);
    });

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void The_settings_keep_logs_and_history_for_their_own_days(string culture) => Sta.Run(culture, () =>
    {
        using var monitor = new ServiceMonitor();
        var snapshot = Samples.Snapshot(1);
        snapshot.Settings.LogRetentionDays = 90;
        snapshot.Settings.HistoryRetentionDays = 730;
        monitor.SimulateSnapshot(snapshot);
        using var settings = new SettingsForm(monitor);
        settings.Show();
        Sta.Pump();

        Assert.True(Sta.Shows(settings, Strings.LogRetention));
        Assert.True(Sta.Shows(settings, Strings.HistoryRetention));
        var days = Sta.Find<NumericUpDown>(settings).Where(n => n.Maximum == 3650).Select(n => n.Value).ToList();
        Assert.Equal([90m, 730m], days);
    });

    private static List<string> Texts(LogView log) =>
        [.. log.Items.Cast<ListViewItem>().Select(i => i.SubItems[^1].Text)];
}
