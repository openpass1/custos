using Custos.Core.Backup;
using Custos.Core.Ipc;

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

    private static ListViewItem Row(TaskListView list, string taskId) =>
        list.Items.Cast<ListViewItem>().Single(i => string.Equals((string)i.Tag!, taskId, StringComparison.Ordinal));

    private static List<string> Texts(ListViewItem item) => [.. item.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text)];

    private static ToolStripButton CardButton(MainForm form) => Sta.Find<ToolStrip>(form)
        .Where(s => s is not StatusStrip)
        .SelectMany(s => s.Items.OfType<ToolStripButton>())
        .Single(b => string.Equals(b.Text, Strings.ToolCard, StringComparison.Ordinal));
}
