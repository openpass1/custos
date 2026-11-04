using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Text;

namespace Custos.Gui;

/// <summary>
/// Progress of the run in progress as "caption — value" rows with a bar: short in the card, full on the "Progress"
/// tab. Shown only in the window, never as a popup (section 5 of the spec).
/// </summary>
internal sealed class ProgressTable : TableLayoutPanel
{
    private readonly Label _task = Value();
    private readonly Label _trigger = Value();
    private readonly Label _phase = Value();
    private readonly Label _file = Value();
    private readonly Label _files = Value();
    private readonly Label _bytes = Value();
    private readonly Label _speed = Value();
    private readonly Label _elapsed = Value();
    private readonly Label _remaining = Value();
    private readonly ProgressBar _bar = new() { Maximum = 1000, Dock = DockStyle.Fill, Height = 18, Margin = new Padding(0, 6, 0, 6) };
    private CurrentRun? _run;

    public ProgressTable(bool detailed)
    {
        AutoSize = true;
        ColumnCount = 2;
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        if (detailed)
        {
            AddRow(Strings.ProgressTask, _task);
        }

        AddRow(Strings.ProgressTrigger, _trigger);
        AddRow(Strings.ProgressPhase, _phase);
        if (detailed)
        {
            _file.AutoSize = false;
            _file.AutoEllipsis = true;
            _file.Dock = DockStyle.Fill;
            _file.Height = 20;
            AddRow(Strings.ProgressFile, _file);
        }

        Controls.Add(_bar);
        SetColumnSpan(_bar, 2);
        AddRow(Strings.ProgressFiles, _files);
        AddRow(Strings.ProgressBytes, _bytes);
        AddRow(Strings.ProgressSpeed, _speed);
        if (detailed)
        {
            AddRow(Strings.ProgressElapsed, _elapsed);
        }

        AddRow(Strings.ProgressRemaining, _remaining);
    }

    /// <summary>Fills the rows; returns the progress shown, for the list of destinations.</summary>
    public BackupProgress ShowRun(CurrentRun run, RunProgressEvent? progress)
    {
        _run = run;
        bool fresh = progress is not null && progress.RunId == run.RunId;
        var p = fresh ? progress!.Progress : run.Progress ?? new BackupProgress();
        bool paused = fresh ? progress!.Paused : run.Paused;
        _task.Text = run.TaskName;
        _trigger.Text = Words.Trigger(run.Trigger, run.ScheduledFor, run.RequestedBy);
        string phase = PhaseText(p.Phase);
        _phase.Text = run.Stopping ? Strings.ResultStopping : paused ? Strings.Format(Strings.ProgressPausedPhase, phase) : phase;
        _file.Text = p.CurrentFile ?? string.Empty;
        _bar.Value = Math.Clamp((int)Math.Round(p.Percent * 10), 0, _bar.Maximum);
        _files.Text = p.FilesTotal > 0 ? Strings.Format(Strings.ProgressOf, Ui.Number(p.FilesDone), Ui.Number(p.FilesTotal)) : Ui.Number(p.FilesDone);
        _bytes.Text = p.BytesTotal > 0 ? Strings.Format(Strings.ProgressOf, Sizes.Format(p.BytesDone), Sizes.Format(p.BytesTotal)) : Sizes.Format(p.BytesDone);
        _speed.Text = p.SpeedBps > 0 && !paused ? Sizes.FormatSpeed(p.SpeedBps) : "—";
        _remaining.Text = p.EtaS is { } eta && !paused ? Sizes.FormatDuration(TimeSpan.FromSeconds(eta)) : "—";
        ShowElapsed();
        return p;
    }

    /// <summary>Refreshes "elapsed"; called every second while a run goes on.</summary>
    public void ShowElapsed()
    {
        if (_run is not null)
        {
            _elapsed.Text = Sizes.FormatDuration(DateTimeOffset.UtcNow - _run.StartedAt);
        }
    }

    public static string PhaseText(string phase) => phase switch
    {
        BackupPhases.Vss => Strings.PhaseVss,
        BackupPhases.Scan => Strings.PhaseScan,
        BackupPhases.Copy => Strings.PhaseCopy,
        BackupPhases.Verify => Strings.PhaseVerify,
        BackupPhases.Retention => Strings.PhaseRetention,
        BackupPhases.PreEvents or BackupPhases.PostEvents => Strings.PhaseEvents,
        _ => phase,
    };

    private void AddRow(string caption, Label value)
    {
        Controls.Add(new Label
        {
            Text = caption,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 3, 12, 3),
        });
        Controls.Add(value);
    }

    private static Label Value() => new() { AutoSize = true, Margin = new Padding(0, 3, 0, 3) };
}

/// <summary>The "Progress" tab: the full progress table and the state of every destination.</summary>
internal sealed class ProgressView : UserControl
{
    private readonly ProgressTable _table = new(detailed: true) { Dock = DockStyle.Top, Padding = new Padding(8) };
    private readonly ListView _destinations = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };

    private readonly Label _idle = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = SystemColors.GrayText,
    };

    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };

    public ProgressView()
    {
        SuspendLayout();
        _destinations.Columns.Add(Strings.ColumnDestination, 320);
        _destinations.Columns.Add(Strings.ColumnState, 120);
        _destinations.Columns.Add(Strings.ColumnError, 400);
        _idle.Text = Strings.ProgressIdle;

        // Docking goes from the last control added to the first: the table on top, the list below it.
        Controls.Add(_destinations);
        Controls.Add(_table);
        Controls.Add(_idle);
        ResumeLayout(performLayout: true);
        _clock.Tick += (_, _) => _table.ShowElapsed();
        ShowRun(null, null);
    }

    /// <summary>Shows a run, or the "nothing is running" note when <paramref name="run"/> is null.</summary>
    public void ShowRun(CurrentRun? run, RunProgressEvent? progress)
    {
        bool running = run is not null;
        _table.Visible = running;
        _destinations.Visible = running;
        _idle.Visible = !running;
        if (run is null)
        {
            _clock.Stop();
            return;
        }

        var shown = _table.ShowRun(run, progress);
        ShowDestinations(shown.Destinations);
        _clock.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clock.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ShowDestinations(List<DestinationProgress> destinations)
    {
        if (_destinations.Items.Count != destinations.Count)
        {
            _destinations.BeginUpdate();
            _destinations.Items.Clear();
            foreach (var _ in destinations)
            {
                _destinations.Items.Add(new ListViewItem([string.Empty, string.Empty, string.Empty]));
            }

            _destinations.EndUpdate();
        }

        for (int i = 0; i < destinations.Count; i++)
        {
            var d = destinations[i];
            var item = _destinations.Items[i];
            SetText(item.SubItems[0], d.Label);
            SetText(item.SubItems[1], StateText(d.State));
            SetText(item.SubItems[2], d.Error ?? string.Empty);
            var color = d.State == DestinationStates.Failed ? Glyphs.ErrorText : SystemColors.WindowText;
            if (item.ForeColor != color)
            {
                item.ForeColor = color;
            }
        }
    }

    private static void SetText(ListViewItem.ListViewSubItem item, string text)
    {
        if (item.Text != text)
        {
            item.Text = text;
        }
    }

    private static string StateText(string state) => state switch
    {
        DestinationStates.Pending => Strings.DestinationPending,
        DestinationStates.Writing => Strings.DestinationWriting,
        DestinationStates.Done => Strings.DestinationDone,
        DestinationStates.Failed => Strings.DestinationFailed,
        _ => state,
    };
}
