using Custos.Core.Agent;
using Custos.Core.Ipc;
using Custos.Core.Model;

namespace Custos.Gui;

/// <summary>
/// The log of one run (section 6.10), opened from the run history. The service reads the file and sends the lines that
/// match the filter, so even the log of a big run with a line for every file opens fast: the warnings and errors of it
/// in a moment, all of it up to the last <see cref="RunLogResult.MaxLines"/> lines.
/// </summary>
internal sealed class RunLogForm : Form
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(2);
    private static readonly string[] Levels = [LogFilterLevels.All, LogFilterLevels.Problems, LogFilterLevels.Errors];

    private readonly ServiceMonitor _monitor;
    private readonly RunRecord _run;
    private readonly ComboBox _level = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190, Margin = new Padding(3, 3, 12, 3) };
    private readonly TextBox _search = new() { PlaceholderText = Strings.LogSearch, Width = 240, Margin = new Padding(3, 3, 12, 3) };
    private readonly Button _refresh = new() { Text = Strings.RunLogRefresh, AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
    private readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 6, 8, 6) };
    private readonly Button _showFile = new() { Text = Strings.RunLogShowFile, AutoSize = true, MinimumSize = new Size(110, 0), Enabled = false };
    private readonly Button _close = new() { Text = Strings.ButtonClose, AutoSize = true, MinimumSize = new Size(88, 0), DialogResult = DialogResult.Cancel };
    private readonly System.Windows.Forms.Timer _typing = new() { Interval = 400 };
    private string? _path;
    private int _request;

    public RunLogForm(ServiceMonitor monitor, RunRecord run)
    {
        _monitor = monitor;
        _run = run;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = Strings.Format(Strings.RunLogTitle, run.TaskName, Ui.Time(run.StartedAt));
        Icon = AppIcons.App();
        Size = new Size(960, 600);
        MinimumSize = new Size(600, 360);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        CancelButton = _close;

        _level.Items.AddRange([Strings.LogShowAll, Strings.LogShowProblems, Strings.LogShowErrors]);
        _level.SelectedIndex = 0;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(9, 8, 9, 2) };
        bar.Controls.AddRange([new Label { Text = Strings.JournalShow, AutoSize = true, Margin = new Padding(3, 7, 0, 3) }, _level, _search, _refresh]);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, Padding = new Padding(12, 6, 12, 12) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _status.MaximumSize = new Size(560, 0);
        bottom.Controls.Add(_status, 0, 0);
        bottom.Controls.Add(_showFile, 1, 0);
        bottom.Controls.Add(_close, 2, 0);

        var middle = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 0) };
        middle.Controls.Add(Log);

        // Docking goes from the last control added to the first.
        Controls.Add(middle);
        Controls.Add(bar);
        Controls.Add(bottom);
        ResumeLayout(performLayout: true);

        _level.SelectedIndexChanged += (_, _) => Reload();
        _refresh.Click += (_, _) => Reload();
        _search.TextChanged += (_, _) =>
        {
            _typing.Stop();
            _typing.Start();
        };
        _typing.Tick += (_, _) =>
        {
            _typing.Stop();
            Reload();
        };
        _showFile.Click += (_, _) =>
        {
            if (_path is not null)
            {
                Ui.OpenFolder(this, Path.GetDirectoryName(_path) ?? Core.CustosInfo.LogsDirectory, _path);
            }
        };
    }

    public LogView Log { get; } = new(showTask: false) { Dock = DockStyle.Fill };

    /// <summary>What the status line says now (for the tests).</summary>
    internal string StatusText => _status.Text;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Reload();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _typing.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Shows what the service sent: the lines, or why there are none.</summary>
    internal void ShowResult(RunLogResult result)
    {
        _path = result.Found ? result.Path : null;
        _showFile.Enabled = _path is not null;
        Log.Reset(result.Lines);
        _status.Text = !result.Found ? Strings.Format(Strings.RunLogGone, result.RetentionDays)
            : result.Matched > result.Lines.Count ? Strings.Format(Strings.RunLogTruncated, Ui.Number(result.Total), Ui.Number(result.Matched), Ui.Number(result.Lines.Count))
            : Strings.Format(Strings.RunLogCount, Ui.Number(result.Total), Ui.Number(result.Lines.Count));
    }

    private void Reload()
    {
        int request = ++_request;
        var ask = new RunLogRequest
        {
            RunId = _run.RunId,
            Level = Levels[Math.Max(0, _level.SelectedIndex)],
            Search = string.IsNullOrWhiteSpace(_search.Text) ? null : _search.Text.Trim(),
        };
        UseWaitCursor = true;
        Ui.Run(this, async () =>
        {
            try
            {
                var result = await _monitor.RequestAsync(PipeProtocol.Methods.RunLog, ask,
                    IpcJsonContext.Default.RunLogRequest, IpcJsonContext.Default.RunLogResult, ReadTimeout);
                if (request == _request && !IsDisposed)
                {
                    ShowResult(result);
                }
            }
            finally
            {
                if (request == _request && !IsDisposed)
                {
                    UseWaitCursor = false;
                }
            }
        });
    }
}
