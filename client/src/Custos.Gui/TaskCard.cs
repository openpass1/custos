using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Text;

namespace Custos.Gui;

/// <summary>
/// The card of the selected task (docs/client-ui.md, 2.3): state, name, the task in plain words, the progress while it
/// runs, and the main settings as "caption — value" rows.
/// </summary>
internal sealed class TaskCard : UserControl
{
    private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12, 10, 12, 10) };
    private readonly TableLayoutPanel _layout = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
    private readonly PictureBox _icon = new() { SizeMode = PictureBoxSizeMode.CenterImage, Size = new Size(28, 28), Margin = new Padding(0, 0, 8, 0) };
    private readonly Label _name = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 0), UseMnemonic = false };
    private readonly Label _subtitle = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 8) };
    private readonly Label _description = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 10), UseMnemonic = false };
    private readonly GroupBox _progressBox = new() { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10, 6, 10, 8), Margin = new Padding(0, 0, 0, 10) };
    private readonly ProgressTable _progress = new(detailed: false) { Dock = DockStyle.Fill };
    private readonly TableLayoutPanel _details = new() { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
    private readonly Label _empty = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.GrayText };
    private readonly Font _nameFont;
    private StateIcon? _iconState;
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
    private string? _taskId;
    private int _detailCount;

    public TaskCard()
    {
        SuspendLayout();
        _nameFont = new Font(Font.FontFamily, Font.SizeInPoints * 1.35f, FontStyle.Bold);
        _name.Font = _nameFont;
        _empty.Text = Strings.CardNoSelection;

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        header.Controls.Add(_icon, 0, 0);
        header.Controls.Add(_name, 1, 0);
        header.Controls.Add(_subtitle, 1, 1);
        header.SetRowSpan(_icon, 2);

        _progressBox.Text = Strings.CardProgress;
        _progressBox.Controls.Add(_progress);

        _details.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _layout.Controls.Add(header);
        _layout.Controls.Add(_description);
        _layout.Controls.Add(_progressBox);
        _layout.Controls.Add(_details);
        _scroll.Controls.Add(_layout);

        Controls.Add(_scroll);
        Controls.Add(_empty);
        ResumeLayout(performLayout: true);
        _clock.Tick += (_, _) => _progress.ShowElapsed();
        ShowTask(null, null, null);
    }

    /// <summary>Shows a task with its state; null shows "select a task".</summary>
    public void ShowTask(TaskDefinition? task, AgentSnapshot? snapshot, RunProgressEvent? progress)
    {
        _taskId = task?.Id;
        _scroll.Visible = task is not null;
        _empty.Visible = task is null;
        if (task is null || snapshot is null)
        {
            _clock.Stop();
            return;
        }

        var state = snapshot.States.FirstOrDefault(s => SameId(s.TaskId, task.Id));
        var current = snapshot.Current is { } run && SameId(run.TaskId, task.Id) ? run : null;

        _scroll.SuspendLayout();
        SetIcon(current is not null ? StateIcon.Running
            : state?.Queued == true ? StateIcon.Queued
            : !task.Enabled ? StateIcon.Disabled
            : TaskStates.Icon(state?.LastRun?.Status));
        _name.Text = task.Name;
        _subtitle.Text = Strings.Format(Strings.CardSubtitle, Words.BackupType(task.BackupType), task.Enabled ? Strings.CardEnabled : Strings.CardDisabled);
        _description.Text = TaskDescriber.Describe(task);
        ShowProgress(current, progress);

        _details.SuspendLayout();
        _detailCount = 0;
        AddDetail(Strings.CardSources, task.Sources.Count == 0 ? Strings.CardNone
            : string.Join(Environment.NewLine, task.Sources.Select(s => s.Recursive ? s.Path : Strings.Format(Strings.CardNotRecursive, s.Path))));
        AddDetail(Strings.CardDestinations, task.Destinations.Count == 0 ? Strings.CardNone
            : string.Join(Environment.NewLine, task.Destinations.Select(d => DescribeDestination(d, snapshot))));
        AddDetail(Strings.CardSchedule, TaskDescriber.ScheduleWithCatchUp(task.Schedule));
        AddDetail(Strings.CardLastSuccess, state?.LastSuccess is { } success
            ? Strings.Format(Strings.CardRunSummary, Ui.Time(success.FinishedAt ?? success.StartedAt), Ui.Number(success.FilesProcessed), Sizes.Format(success.BytesWritten))
            : Strings.CardNever);
        if (state?.LastRun is { } last && last.Status is not null && !RunStatuses.IsSuccessful(last.Status))
        {
            AddDetail(Strings.CardLastRun, Strings.Format(Strings.CardFailedRun, Ui.Time(last.StartedAt), Words.Status(last.Status), last.ErrorMessage ?? string.Empty).TrimEnd(' ', ':', '—'),
                Glyphs.ErrorText);
        }

        AddDetail(Strings.CardNextRun, !task.Enabled ? Strings.CardDisabledNoRuns
            : state?.NextRun is { } next ? Ui.Time(next)
            : Strings.CardOnRequest);
        if (task.UpdatedAt is not null || task.UpdatedBy is not null)
        {
            AddDetail(Strings.CardChanged, Strings.Format(Strings.CardChangedBy, Ui.Time(task.UpdatedAt), DescribeAuthor(task.UpdatedBy)));
        }

        if (!string.IsNullOrWhiteSpace(task.Comment))
        {
            AddDetail(Strings.CardComment, task.Comment);
        }

        // Rows left over from a task with more details go away.
        while (_details.Controls.Count > _detailCount * 2)
        {
            _details.Controls[_details.Controls.Count - 1].Dispose();
        }

        _details.ResumeLayout(performLayout: true);
        _scroll.ResumeLayout(performLayout: true);
    }

    /// <summary>Updates only the progress block, between snapshots.</summary>
    public void ShowProgress(CurrentRun? current, RunProgressEvent? progress)
    {
        if (current is null || !SameId(current.TaskId, _taskId))
        {
            _progressBox.Visible = false;
            _clock.Stop();
            return;
        }

        _progress.ShowRun(current, progress);
        _progressBox.Visible = true;
        _clock.Start();
    }

    /// <summary>"PC01\admin" for changes made on this computer, "monitoring site (login)" for changes from the server.</summary>
    public static string DescribeAuthor(string? updatedBy) => updatedBy switch
    {
        null => "—",
        _ when updatedBy.StartsWith("local:", StringComparison.Ordinal) => updatedBy["local:".Length..],
        _ when updatedBy.StartsWith("server:", StringComparison.Ordinal) => Strings.Format(Strings.CardChangedOnServer, updatedBy["server:".Length..]),
        _ => updatedBy,
    };

    public static string DescribeDestination(TaskDestination destination, AgentSnapshot snapshot)
    {
        string text = TaskDescriber.Destination(destination);
        if (destination.CredentialId is { } id
            && snapshot.Credentials.FirstOrDefault(c => SameId(c.Id, id)) is { } credential)
        {
            text = Strings.Format(Strings.CardAsAccount, text, credential.DisplayName);
        }

        return text;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clock.Dispose();
            _nameFont.Dispose();
            _icon.Image?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);

        // Drawn again at the new size by the next ShowTask.
        _iconState = null;
    }

    private void SetIcon(StateIcon state)
    {
        if (_iconState == state)
        {
            return;
        }

        var old = _icon.Image;
        _icon.Image = Glyphs.Draw(state, LogicalToDeviceUnits(24));
        _iconState = state;
        old?.Dispose();
    }

    /// <summary>
    /// The next "caption — value" row. The labels of the previous task are reused: the card is filled again with every
    /// snapshot, and new labels each time would use up the window handles of the process within hours.
    /// </summary>
    private void AddDetail(string caption, string value, Color? color = null)
    {
        Label captionLabel;
        Label valueLabel;
        if ((_detailCount * 2) + 1 < _details.Controls.Count)
        {
            captionLabel = (Label)_details.Controls[_detailCount * 2];
            valueLabel = (Label)_details.Controls[(_detailCount * 2) + 1];
        }
        else
        {
            captionLabel = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 3, 12, 5) };
            valueLabel = new Label { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 5), UseMnemonic = false };
            _details.Controls.Add(captionLabel);
            _details.Controls.Add(valueLabel);
        }

        captionLabel.Text = caption;
        valueLabel.Text = value;
        valueLabel.ForeColor = color ?? SystemColors.ControlText;
        _detailCount++;
    }

    private static bool SameId(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
