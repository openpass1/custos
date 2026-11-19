using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Gui;
using Custos.Mac.Gui;
using State = Custos.Mac.Gui.App.MenuBarState;

namespace Custos.Mac.Tests;

/// <summary>The icon in the menu bar says what the tray icon of Windows says.</summary>
public sealed class MenuBarTests
{
    [Fact]
    public void Without_the_service_the_icon_is_grey()
    {
        Assert.Equal(State.Down, App.Describe(ServiceState.Unavailable, Snapshot(), null).State);
        Assert.Equal(State.Down, App.Describe(ServiceState.Running, null, null).State);
    }

    [Fact]
    public void All_well_when_every_enabled_task_succeeded()
    {
        var (state, text) = App.Describe(ServiceState.Running, Snapshot(("A", RunStatuses.Success, true), ("B", RunStatuses.Error, false)), null);
        Assert.Equal(State.Ok, state);
        Assert.Equal(Strings.TrayOk, text);
    }

    [Fact]
    public void An_error_wins_over_a_warning()
    {
        var (state, text) = App.Describe(ServiceState.Running,
            Snapshot(("A", RunStatuses.Warning, true), ("B", RunStatuses.Error, true), ("C", RunStatuses.Aborted, true)), null);
        Assert.Equal(State.Error, state);
        Assert.Equal(Strings.Format(Strings.TrayError, "B"), text);
    }

    [Fact]
    public void A_stopped_run_needs_a_look()
    {
        Assert.Equal(State.Warning, App.Describe(ServiceState.Running, Snapshot(("A", RunStatuses.Aborted, true)), null).State);
    }

    [Fact]
    public void A_run_in_progress_shows_its_percent_from_the_latest_progress()
    {
        var snapshot = Snapshot(("A", RunStatuses.Error, true));
        snapshot.Current = new CurrentRun { RunId = "r1", TaskId = "A", TaskName = "A", Trigger = RunTriggers.Manual, Progress = Progress(10) };
        var progress = new RunProgressEvent { RunId = "r1", TaskId = "A", Progress = Progress(42.7) };

        var (state, text) = App.Describe(ServiceState.Running, snapshot, progress);

        Assert.Equal(State.Running, state);
        Assert.Equal(Strings.Format(Strings.TrayRunning, "A", 42), text);
    }

    private static AgentSnapshot Snapshot(params (string Name, string Status, bool Enabled)[] tasks) => new()
    {
        Tasks = [.. tasks.Select(t => new TaskDefinition { Id = t.Name, Name = t.Name, Enabled = t.Enabled })],
        States = [.. tasks.Select(t => new TaskState
        {
            TaskId = t.Name,
            LastRun = new RunRecord { RunId = "r-" + t.Name, TaskId = t.Name, TaskName = t.Name, BackupType = BackupTypes.Full, Trigger = RunTriggers.Schedule, StartedAt = DateTimeOffset.UnixEpoch, Status = t.Status },
        })],
    };

    private static BackupProgress Progress(double percent) => new() { BytesDone = (long)(percent * 10), BytesTotal = 1000 };
}
