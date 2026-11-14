using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Monitor;

namespace Custos.Gui;

/// <summary>
/// The window of a user who gets only the state of the backups (tray modes «Только состояние» and «Скрыт», section 4):
/// the list of tasks with their last and next runs and the progress of the one running, and the state of the service.
/// No buttons for changes; the task list remembers its column widths like the main window.
/// </summary>
internal sealed class StatusForm : Form
{
    private const int StateIconSize = 16;

    private readonly ServiceMonitor _monitor;
    private readonly UiState _uiState = UiState.Load();
    private readonly TaskListView _tasks = new() { Dock = DockStyle.Fill };
    private readonly Label _noConnection = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.GrayText, Padding = new Padding(24) };
    private readonly Label _summary = new() { Dock = DockStyle.Top, AutoSize = false, Height = 34, Padding = new Padding(12, 10, 12, 0) };
    private readonly ToolStripStatusLabel _serviceStatus = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private ImageList? _stateImages;

    public StatusForm(ServiceMonitor monitor)
    {
        _monitor = monitor;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = Strings.StatusTitle;
        Icon = AppIcons.App();
        Size = new Size(820, 420);
        MinimumSize = new Size(520, 260);
        StartPosition = FormStartPosition.CenterScreen;

        var note = new Label
        {
            Text = Strings.StatusNote,
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 30,
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(12, 6, 12, 0),
        };
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 0) };
        body.Controls.Add(_tasks);
        body.Controls.Add(_noConnection);
        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_serviceStatus);
        statusStrip.Items.Add(new ToolStripStatusLabel(Core.CustosInfo.Version));

        Controls.Add(body);
        Controls.Add(_summary);
        Controls.Add(note);
        Controls.Add(statusStrip);
        ResumeLayout(performLayout: true);

        _stateImages = Glyphs.StateImages(LogicalToDeviceUnits(StateIconSize));
        _tasks.SmallImageList = _stateImages;
        _tasks.SetColumns(_uiState.TaskColumns);
        _monitor.StatusChanged += OnChanged;
        _monitor.SnapshotChanged += OnChanged;
        _monitor.ProgressChanged += OnProgressChanged;
        ShowState();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The interface stays in the tray (or without an icon): the window only hides, like the main window.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }

        var state = UiState.Load();
        state.TaskColumns = _tasks.GetColumnWidths();
        state.Save();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _monitor.StatusChanged -= OnChanged;
            _monitor.SnapshotChanged -= OnChanged;
            _monitor.ProgressChanged -= OnProgressChanged;
            _stateImages?.Dispose();
            _stateImages = null;
        }

        base.Dispose(disposing);
    }

    private void OnChanged(object? sender, EventArgs e) => ShowState();

    private void OnProgressChanged(object? sender, EventArgs e)
    {
        var current = _monitor.Snapshot?.Current;
        var progress = _monitor.Progress;
        if (current is not null && progress is not null && progress.RunId == current.RunId)
        {
            _tasks.ShowRunning(current.TaskId, TaskStates.RunningText(current.Stopping, progress.Paused, progress.Progress.Percent));
        }

        _summary.Text = Capitalize(Summary(_monitor));
    }

    private void ShowState()
    {
        // Like the main window: the monitor drops the snapshot when the connection ends.
        var status = _monitor.Status;
        var snapshot = _monitor.Snapshot;
        bool connected = snapshot is not null;
        _tasks.Visible = connected;
        _noConnection.Visible = !connected;
        _noConnection.Text = connected ? string.Empty : Strings.Format(Strings.NoConnection, status.Problem ?? ServiceMonitor.Describe(status.State));
        _tasks.ShowTasks(connected ? snapshot : null);
        _summary.Text = Capitalize(Summary(_monitor));

        string server = snapshot?.Server is { State: not ServerLinkStates.Off } link ? " · " + ServerLink.StateText(link.State).TrimEnd('.') : string.Empty;
        _serviceStatus.Text = Capitalize(ServiceMonitor.Describe(status.State)) + server;
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], Thread.CurrentThread.CurrentCulture) + text[1..];

    /// <summary>One line on top: what runs now, or how the tasks ended, as the tray icon says it.</summary>
    internal static string Summary(ServiceMonitor monitor)
    {
        var snapshot = monitor.Snapshot;
        if (snapshot is null)
        {
            return string.Empty;
        }

        if (snapshot.Current is { } current)
        {
            var progress = monitor.Progress;
            double percent = progress?.RunId == current.RunId ? progress.Progress.Percent : current.Progress?.Percent ?? 0;
            return Strings.Format(Strings.TrayRunning, current.TaskName, (int)Math.Floor(percent));
        }

        string? Worst(string status) => snapshot.Tasks.Where(t => t.Enabled)
            .FirstOrDefault(t => snapshot.States.FirstOrDefault(s => string.Equals(s.TaskId, t.Id, StringComparison.OrdinalIgnoreCase))?.LastRun?.Status == status)
            ?.Name;

        return Worst(RunStatuses.Error) is { } failed ? Strings.Format(Strings.TrayError, failed)
            : (Worst(RunStatuses.Warning) ?? Worst(RunStatuses.Aborted)) is { } warned ? Strings.Format(Strings.TrayWarning, warned)
            : Strings.TrayOk;
    }
}
