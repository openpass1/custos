using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Text;

namespace Custos.Gui.Tests;

/// <summary>The main window with the list, the card and the log, fed with snapshots as the service sends them.</summary>
public sealed class MainFormTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void The_window_follows_the_snapshots_of_the_service(string culture) => Sta.Run(culture, () =>
    {
        using var monitor = Samples.Monitor();
        using var form = new MainForm(monitor);
        form.Show();
        Sta.Pump();

        var list = Assert.Single(Sta.Find<TaskListView>(form));
        var card = Assert.Single(Sta.Find<TaskCard>(form));
        Assert.Equal(3, list.Items.Count);
        Assert.Null(list.SelectedTaskId);
        Assert.True(Sta.Shows(card, Strings.CardNoSelection));

        // A click on a task: the card shows it, with the reason of its last failure.
        list.SelectTask(Samples.PhotosId);
        Sta.Pump();
        Assert.Equal(Samples.PhotosId, list.SelectedTaskId);
        Assert.True(Sta.Shows(card, "Photos"));
        Assert.True(Sta.ShowsPart(card, Samples.UsbProblem));
        Assert.Equal(nameof(StateIcon.Error), Row(list, Samples.PhotosId).ImageKey);

        // No paragraph in plain words above the rows: what it said is in the rows.
        Assert.False(Sta.ShowsPart(card, TaskDescriber.Describe(Samples.Photos())));
        Assert.True(Sta.Shows(card, Strings.CardRetention));
        Assert.True(Sta.Shows(card, TaskDescriber.Retention(Samples.Photos()).TrimEnd('.')));
        Assert.True(Sta.Shows(card, Strings.ArchiveNone));
        Assert.False(Sta.Shows(card, Strings.CardFullBackup));

        // The office task starts: its row shows the progress, from the snapshot and then from the progress events.
        monitor.SimulateSnapshot(Samples.Snapshot(2, running: true));
        Sta.Pump();
        Assert.Equal(nameof(StateIcon.Running), Row(list, Samples.OfficeId).ImageKey);
        Assert.Contains(TaskStates.RunningText(stopping: false, paused: false, 47), Texts(Row(list, Samples.OfficeId)));

        monitor.SimulateProgress(new RunProgressEvent { RunId = Samples.RunId, TaskId = Samples.OfficeId, Progress = Samples.Progress(900) });
        Sta.Pump();
        Assert.Contains(TaskStates.RunningText(stopping: false, paused: false, 90), Texts(Row(list, Samples.OfficeId)));
        Assert.Equal(Samples.PhotosId, list.SelectedTaskId);

        // The live log.
        monitor.SimulateLog(
        [
            new LogLine { Seq = 1, Time = DateTimeOffset.Now, Text = "Office documents: started" },
            new LogLine { Seq = 2, Time = DateTimeOffset.Now, Level = LogLineLevels.Warning, Text = "Office documents: a file is locked" },
            new LogLine { Seq = 3, Time = DateTimeOffset.Now, Level = LogLineLevels.Error, Text = "Photos: " + Samples.UsbProblem },
        ]);
        Sta.Pump();
        var log = Assert.Single(Sta.Find<LogView>(form));
        Assert.Equal(3, log.Items.Count);
        Assert.Equal(nameof(StateIcon.Error), log.Items[2].ImageKey);

        // The selected task is deleted elsewhere: the card goes back to "select a task".
        monitor.SimulateSnapshot(Samples.Snapshot(3, withoutPhotos: true));
        Sta.Pump();
        Assert.Equal(2, list.Items.Count);
        Assert.Null(list.SelectedTaskId);
        Assert.True(Sta.Shows(card, Strings.CardNoSelection));
    });

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void The_card_shows_compression_full_backups_and_events_as_rows(string culture) => Sta.Run(culture, () =>
    {
        using var monitor = Samples.Monitor();
        using var form = new MainForm(monitor);
        form.Show();
        Sta.Pump();
        var snapshot = Samples.Snapshot(2);
        var office = snapshot.Tasks.Single(t => t.Id == Samples.OfficeId);
        office.Archive = new ArchiveOptions { Format = ArchiveFormats.SevenZip, Level = 7, SplitMb = 700, Encrypt = true, EncryptNames = true, PasswordCredentialId = "pw" };
        office.ForceFull = new ForceFullOptions { EveryRuns = 7, Weekday = 7 };
        office.Events.Pre = [new TaskEvent { Kind = TaskEventKinds.ServiceStop, Service = "MySQL80" }];
        office.Events.Post = [new TaskEvent { Kind = TaskEventKinds.ServiceStart, Service = "MySQL80" }];
        office.Events.After = AfterActions.Shutdown;
        office.Destinations[1].WormDays = 7;
        office.Destinations[1].WormLockHours = 3;
        monitor.SimulateSnapshot(snapshot);
        Sta.Pump();

        var list = Assert.Single(Sta.Find<TaskListView>(form));
        var card = Assert.Single(Sta.Find<TaskCard>(form));
        list.SelectTask(Samples.OfficeId);
        Sta.Pump();

        Assert.True(Sta.ShowsPart(card, Strings.ArchiveSevenZip + ", "));
        Assert.True(Sta.Shows(card, TaskCard.DescribeArchive(office)));
        Assert.True(Sta.ShowsPart(card, Strings.CardArchiveEncryptedNames));
        Assert.True(Sta.Shows(card, TaskDescriber.FullBackups(office)!));
        Assert.True(Sta.Shows(card, Strings.Format(Strings.EventShowStop, "MySQL80")));
        Assert.True(Sta.Shows(card, Strings.Format(Strings.EventShowStart, "MySQL80") + Environment.NewLine + Strings.AfterShutdown));

        // WORM has a row of its own, in colour, named by the destination when there are several.
        string worm = Strings.Format(Strings.CardProtectionOf, TaskDescriber.Destination(office.Destinations[1]), Strings.Format(Strings.CardWorm, 7, 3));
        Assert.Equal(worm, TaskCard.DescribeProtection(office));
        Assert.Equal(Glyphs.Blue, Sta.Find<Label>(card).Single(l => l.Text == worm).ForeColor);
        Assert.True(Sta.Shows(card, Strings.CardProtection));

        // A task without it has no such row, and none for an empty comment.
        office.Destinations[1].WormDays = 0;
        office.Comment = " ";
        snapshot.Version++;
        monitor.SimulateSnapshot(snapshot);
        Sta.Pump();
        Assert.False(Sta.Shows(card, Strings.CardProtection));
        Assert.False(Sta.Shows(card, Strings.CardComment));
    });

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void The_card_shows_a_short_progress_and_the_tab_the_details(string culture) => Sta.Run(culture, () =>
    {
        using var monitor = Samples.Monitor();
        using var form = new MainForm(monitor);
        form.Show();
        Sta.Pump();
        var list = Assert.Single(Sta.Find<TaskListView>(form));
        var card = Assert.Single(Sta.Find<TaskCard>(form));
        var tab = Assert.Single(Sta.Find<ProgressView>(form));
        list.SelectTask(Samples.OfficeId);
        monitor.SimulateSnapshot(Samples.Snapshot(2, running: true));
        Sta.Pump();

        // Before the time left is known: what the run is doing.
        var scan = Samples.Progress(0);
        scan.Phase = BackupPhases.Scan;
        monitor.SimulateProgress(new RunProgressEvent { RunId = Samples.RunId, TaskId = Samples.OfficeId, Progress = scan });
        Sta.Pump();
        Assert.Equal(TaskStates.RunningText(stopping: false, paused: false, 0) + " · " + Strings.PhaseScan, card.ProgressLine);

        // Copying: percent and time left in the card, nothing more.
        var copy = Samples.Progress(470);
        copy.EtaS = 125;
        monitor.SimulateProgress(new RunProgressEvent { RunId = Samples.RunId, TaskId = Samples.OfficeId, Progress = copy });
        Sta.Pump();
        string left = Strings.Format(Strings.CardProgressLeft, Sizes.FormatDuration(TimeSpan.FromSeconds(125)));
        Assert.Equal(TaskStates.RunningText(stopping: false, paused: false, 47) + " · " + left, card.ProgressLine);
        Assert.False(Sta.ShowsPart(card, "report.docx"));

        // The current file and the rest are on the "Progress" tab.
        var page = (TabPage)tab.Parent!;
        ((TabControl)page.Parent!).SelectedTab = page;
        Sta.Pump();
        Assert.True(Sta.ShowsPart(tab, "report.docx"));

        // Paused: no time left.
        monitor.SimulateProgress(new RunProgressEvent { RunId = Samples.RunId, TaskId = Samples.OfficeId, Progress = copy, Paused = true });
        Sta.Pump();
        Assert.Equal(TaskStates.RunningText(stopping: false, paused: true, 47), card.ProgressLine);
    });

    [Fact]
    public void The_card_is_switched_off_and_on_and_the_window_remembers_it() => Sta.Run("en-US", () =>
    {
        using var monitor = Samples.Monitor();
        using (var form = new MainForm(monitor))
        {
            form.Show();
            Sta.Pump();
            var list = Assert.Single(Sta.Find<TaskListView>(form));
            list.SelectTask(Samples.OfficeId);
            Sta.Pump();

            // Next to the card the list keeps all six columns (it scrolls sideways when they do not fit).
            Assert.Equal(6, list.Columns.Count);
            list.Columns[2].Width = list.LogicalToDeviceUnits(300);

            CardButton(form).PerformClick();
            Sta.Pump();
            Assert.Equal(6, list.Columns.Count);
            Assert.Equal(Samples.OfficeId, list.SelectedTaskId);

            CardButton(form).PerformClick();
            Sta.Pump();
            Assert.Equal(6, list.Columns.Count);
            Assert.InRange(list.GetColumnWidths()["Schedule"], 299, 301);
            Assert.Equal(Samples.OfficeId, list.SelectedTaskId);
            Assert.True(Sta.Shows(Assert.Single(Sta.Find<TaskCard>(form)), "Office documents"));

            // Closing only hides the window, and writes ui.json.
            CardButton(form).PerformClick();
            form.Close();
            Sta.Pump();
            Assert.False(form.Visible);
            Assert.True(File.Exists(Path.Combine(UiState.Folder, "ui.json")));
        }

        // A new window starts the way the last one was left: without the card, with the same column widths.
        using var again = new MainForm(monitor);
        again.Show();
        Sta.Pump();
        var columns = Assert.Single(Sta.Find<TaskListView>(again));
        Assert.Equal(6, columns.Columns.Count);
        Assert.InRange(columns.GetColumnWidths()["Schedule"], 299, 301);
        Assert.False(CardButton(again).Checked);
    });

    [Fact]
    public void The_status_bar_has_a_link_to_the_ways_of_supporting_the_project() => Sta.Run("ru-RU", () =>
    {
        using var monitor = Samples.Monitor();
        using var form = new MainForm(monitor);
        form.Show();
        Sta.Pump();

        var status = Assert.Single(Sta.Find<StatusStrip>(form));
        var link = status.Items.OfType<ToolStripStatusLabel>().Single(l => l.IsLink);
        Assert.Equal("Поддержать проект", link.Text);
        Assert.Same(link, status.Items[^1]);
        Assert.Equal("https://github.com/openpass1/custos/blob/main/DONATE.md", Core.CustosInfo.DonatePage);
    });

    private static ListViewItem Row(TaskListView list, string taskId) =>
        list.Items.Cast<ListViewItem>().Single(i => string.Equals((string)i.Tag!, taskId, StringComparison.Ordinal));

    private static List<string> Texts(ListViewItem item) => [.. item.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text)];

    private static ToolStripButton CardButton(MainForm form) => Sta.Find<ToolStrip>(form)
        .Where(s => s is not StatusStrip)
        .SelectMany(s => s.Items.OfType<ToolStripButton>())
        .Single(b => string.Equals(b.Text, Strings.ToolCard, StringComparison.Ordinal));
}
