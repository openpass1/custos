using System.Globalization;
using System.Text;
using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Text;

namespace Custos.Gui;

/// <summary>
/// A list of log lines (docs/client-ui.md, 2.4): time, task, text; warnings in amber, errors in red, each with its icon.
/// Shows the lines of a source that pass a filter, and follows the newest line unless the user scrolled up.
/// </summary>
internal sealed class LogView : ListView
{
    private readonly ContextMenuStrip _menu = new();
    private readonly bool _showTask;
    private IList<LogLine> _source = [];
    private Func<LogLine, bool> _filter = _ => true;

    public LogView()
        : this(showTask: true)
    {
    }

    public LogView(bool showTask)
    {
        _showTask = showTask;
        View = View.Details;
        FullRowSelect = true;
        HideSelection = false;
        DoubleBuffered = true;
        HeaderStyle = ColumnHeaderStyle.Nonclickable;
        Columns.Add(Strings.ColumnTime, 130);
        if (showTask)
        {
            Columns.Add(Strings.ColumnTask, 150);
        }

        Columns.Add(Strings.ColumnMessage, 900);

        _menu.Items.Add(Strings.MenuCopy, null, (_, _) => CopySelected());
        _menu.Items.Add(Strings.MenuSelectAll, null, (_, _) => SelectAll());
        ContextMenuStrip = _menu;
    }

    /// <summary>All the lines, shown or not.</summary>
    public int SourceCount => _source.Count;

    /// <summary>Shows the lines of <paramref name="lines"/> that pass the filter; the list is kept to add to it later.</summary>
    public void Reset(IList<LogLine> lines)
    {
        _source = lines;
        Rebuild();
    }

    /// <summary>Shows only the lines for which <paramref name="filter"/> is true, of those already there and to come.</summary>
    public void SetFilter(Func<LogLine, bool> filter)
    {
        _filter = filter;
        Rebuild();
    }

    /// <summary>Lines were added at the end of the source (and the oldest may have left it).</summary>
    public void Append(IReadOnlyList<LogLine> lines)
    {
        bool follow = Items.Count == 0 || IsLastVisible();
        BeginUpdate();
        try
        {
            Items.AddRange([.. lines.Where(_filter).Select(MakeItem)]);

            // The lines the source no longer holds leave the list too.
            long first = _source.Count > 0 ? _source[0].Seq : long.MaxValue;
            while (Items.Count > 0 && Items[0].Tag is LogLine { } oldest && oldest.Seq < first)
            {
                Items.RemoveAt(0);
            }
        }
        finally
        {
            EndUpdate();
        }

        if (follow)
        {
            ScrollToEnd();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C)
        {
            CopySelected();
            e.Handled = true;
        }
        else if (e.Control && e.KeyCode == Keys.A)
        {
            SelectAll();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _menu.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Rebuild()
    {
        BeginUpdate();
        try
        {
            Items.Clear();
            Items.AddRange([.. _source.Where(_filter).Select(MakeItem)]);
        }
        finally
        {
            EndUpdate();
        }

        ScrollToEnd();
    }

    private void SelectAll()
    {
        BeginUpdate();
        foreach (ListViewItem item in Items)
        {
            item.Selected = true;
        }

        EndUpdate();
    }

    private void CopySelected()
    {
        if (SelectedItems.Count == 0)
        {
            return;
        }

        var text = new StringBuilder();
        foreach (ListViewItem item in SelectedItems)
        {
            for (int i = 0; i < item.SubItems.Count; i++)
            {
                text.Append(i == 0 ? string.Empty : "\t").Append(item.SubItems[i].Text);
            }

            text.AppendLine();
        }

        try
        {
            Clipboard.SetText(text.ToString());
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another program holds the clipboard.
        }
    }

    private bool IsLastVisible()
    {
        var last = GetItemRect(Items.Count - 1);
        return last.Top < ClientSize.Height;
    }

    private void ScrollToEnd()
    {
        if (Items.Count > 0)
        {
            Items[^1].EnsureVisible();
        }
    }

    private ListViewItem MakeItem(LogLine line)
    {
        string time = string.Empty;
        if (line.Time != DateTimeOffset.MinValue)
        {
            var local = line.Time.ToLocalTime();
            time = local.Date == DateTime.Today
                ? local.ToString("T", CultureInfo.CurrentCulture)
                : local.ToString("d", CultureInfo.CurrentCulture) + " " + local.ToString("T", CultureInfo.CurrentCulture);
        }

        var item = new ListViewItem(_showTask ? [time, line.TaskName ?? string.Empty, line.Text] : [time, line.Text]) { Tag = line };
        switch (line.Level)
        {
            case LogLineLevels.Warning:
                item.ImageKey = nameof(StateIcon.Warning);
                item.ForeColor = Glyphs.WarningText;
                break;
            case LogLineLevels.Error:
                item.ImageKey = nameof(StateIcon.Error);
                item.ForeColor = Glyphs.ErrorText;
                break;
            case LogLineLevels.Detail:
                item.ForeColor = SystemColors.GrayText;
                break;
        }

        return item;
    }
}

/// <summary>A run of the history, for <see cref="HistoryView.OpenLogRequested"/>.</summary>
internal sealed class RunEventArgs(RunRecord run) : EventArgs
{
    public RunRecord Run { get; } = run;
}

/// <summary>
/// The "Run history" tab: the runs of the selected task, newest first, a page at a time (docs/client-ui.md, 2.4).
/// A double click opens the log of a run.
/// </summary>
internal sealed class HistoryView : UserControl
{
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        ShowItemToolTips = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };

    private readonly Label _message = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = SystemColors.GrayText,
    };

    private readonly CheckBox _problemsOnly = new() { Text = Strings.HistoryProblemsOnly, AutoSize = true, Margin = new Padding(3, 4, 12, 2) };
    private readonly Label _hint = new() { Text = Strings.HistoryOpenLogHint, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 2) };
    private readonly Label _shown = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 7, 12, 3) };
    private readonly Button _more = new() { Text = Strings.HistoryMore, AutoSize = true, Visible = false, Margin = new Padding(3, 2, 3, 2) };
    private readonly FlowLayoutPanel _top = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(2, 2, 2, 0) };
    private readonly FlowLayoutPanel _bottom = new() { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, Padding = new Padding(2, 0, 2, 2) };
    private readonly ContextMenuStrip _menu = new();
    private readonly List<RunRecord> _runs = [];
    private bool _settingProblemsOnly;

    /// <summary>The index of the verification column.</summary>
    private const int VerifyColumn = 7;

    public HistoryView()
    {
        _list.Columns.Add(Strings.ColumnStarted, 130);
        _list.Columns.Add(Strings.ColumnDuration, 90);
        _list.Columns.Add(Strings.ColumnType, 110);
        _list.Columns.Add(Strings.ColumnTrigger, 190);
        _list.Columns.Add(Strings.ColumnFiles, 80, HorizontalAlignment.Right);
        _list.Columns.Add(Strings.ColumnSize, 90, HorizontalAlignment.Right);
        _list.Columns.Add(Strings.ColumnResult, 150);
        _list.Columns.Add(Strings.ColumnVerify, 170);
        _list.Columns.Add(Strings.ColumnDetails, 400);

        var open = new ToolStripMenuItem(Strings.HistoryOpenLog, null, (_, _) => OpenSelected()) { ShortcutKeyDisplayString = "Enter" };
        open.Font = new Font(open.Font, FontStyle.Bold);
        _menu.Items.AddRange([open, new ToolStripSeparator(), new ToolStripMenuItem(Strings.LogFolder, null, (_, _) => Ui.OpenFolder(this, Core.CustosInfo.LogsDirectory))]);
        _menu.Opening += (_, e) => e.Cancel = _list.SelectedItems.Count == 0;
        _list.ContextMenuStrip = _menu;
        _list.ItemActivate += (_, _) => OpenSelected();

        _problemsOnly.CheckedChanged += (_, _) =>
        {
            if (!_settingProblemsOnly)
            {
                ProblemsOnlyChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        _more.Click += (_, _) => MoreRequested?.Invoke(this, EventArgs.Empty);
        _top.Controls.AddRange([_problemsOnly, _hint]);
        _bottom.Controls.AddRange([_shown, _more]);

        // Docking goes from the last control added to the first: the bars, then the list.
        Controls.Add(_list);
        Controls.Add(_message);
        Controls.Add(_top);
        Controls.Add(_bottom);
        ShowMessage(Strings.HistorySelectTask);
    }

    /// <summary>"Show more" was pressed: the page after <see cref="Oldest"/> is wanted.</summary>
    public event EventHandler? MoreRequested;

    /// <summary>"Only warnings and errors" was switched by the user.</summary>
    public event EventHandler? ProblemsOnlyChanged;

    /// <summary>The user wants the log of a run (double click, Enter, the menu).</summary>
    public event EventHandler<RunEventArgs>? OpenLogRequested;

    public ListView List => _list;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ProblemsOnly
    {
        get => _problemsOnly.Checked;
        set
        {
            _settingProblemsOnly = true;
            _problemsOnly.Checked = value;
            _settingProblemsOnly = false;
        }
    }

    /// <summary>How many runs are shown.</summary>
    public int Count => _runs.Count;

    /// <summary>When the oldest run shown started.</summary>
    public DateTimeOffset? Oldest => _runs.Count > 0 ? _runs[^1].StartedAt : null;

    /// <summary>A message instead of the runs; with <paramref name="bars"/> the filter stays (a task is selected).</summary>
    public void ShowMessage(string text, bool bars = false)
    {
        _runs.Clear();
        _list.Items.Clear();
        _message.Text = text;
        _message.Visible = true;
        _list.Visible = false;
        _top.Visible = bars;
        _bottom.Visible = false;
    }

    /// <summary>
    /// Shows runs, newest first: in place of those shown, or after them with <paramref name="append"/> (the next page).
    /// <paramref name="more"/> says whether older runs are left; <paramref name="retentionDays"/> how long they are kept.
    /// </summary>
    public void ShowRuns(List<RunRecord> runs, bool more = false, bool append = false, int retentionDays = 0)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (!append && runs.Count == 0)
        {
            ShowMessage(ProblemsOnly ? Strings.HistoryNoProblems : Strings.HistoryEmpty, bars: ProblemsOnly);
            return;
        }

        // A new run arrived while the user looked further down: the same run stays selected and in view.
        string? selected = _list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is RunRecord chosen ? chosen.RunId : null;
        int top = append ? -1 : _list.TopItem?.Index ?? 0;
        _list.BeginUpdate();
        try
        {
            if (!append)
            {
                _runs.Clear();
                _list.Items.Clear();
            }

            _runs.AddRange(runs);
            _list.Items.AddRange([.. runs.Select(MakeItem)]);
            if (!append && selected is not null)
            {
                var again = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag is RunRecord r && r.RunId == selected);
                if (again is not null)
                {
                    again.Selected = true;
                    again.Focused = true;
                }
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        if (top > 0 && top < _list.Items.Count)
        {
            _list.TopItem = _list.Items[top];
        }

        string since = Ui.Time(Oldest);
        _shown.Text = more || retentionDays <= 0
            ? Strings.Format(Strings.HistoryShown, _runs.Count, since)
            : Strings.Format(Strings.HistoryShownAll, _runs.Count, since, retentionDays);
        _more.Visible = more;
        _message.Visible = false;
        _list.Visible = true;
        _top.Visible = true;
        _bottom.Visible = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _menu.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OpenSelected()
    {
        if (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is RunRecord run)
        {
            OpenLogRequested?.Invoke(this, new RunEventArgs(run));
        }
    }

    private static ListViewItem MakeItem(RunRecord run)
    {
        string duration = run.FinishedAt is { } finished ? Sizes.FormatDuration(finished - run.StartedAt) : "—";
        string details = run.ErrorMessage ?? (run.Warnings > 0 ? Strings.Format(Strings.HistoryWarnings, run.Warnings) : run.SetName ?? string.Empty);
        var item = new ListViewItem(
        [
            Ui.Time(run.StartedAt),
            duration,
            Words.BackupType(run.BackupType),
            Words.Trigger(run.Trigger, run.ScheduledFor, run.RequestedBy),
            Ui.Number(run.FilesProcessed),
            Sizes.Format(run.BytesWritten),
            Words.Status(run.Status),
            VerifyText(run.Verify),
            details,
        ])
        {
            Tag = run,
            ImageKey = run.Status is null ? nameof(StateIcon.Running) : TaskStates.Icon(run.Status).ToString(),
            ToolTipText = details,
        };
        if (run.Status is RunStatuses.Error)
        {
            item.ForeColor = Glyphs.ErrorText;
        }

        if (run.Verify is { } verify)
        {
            // The verification cell in its own colour: green when the copy was read back fine, red when not.
            item.UseItemStyleForSubItems = false;
            item.SubItems[VerifyColumn].ForeColor = verify.Ok ? Glyphs.Green : Glyphs.ErrorText;
            for (int i = 0; i < item.SubItems.Count; i++)
            {
                if (i != VerifyColumn)
                {
                    item.SubItems[i].ForeColor = item.ForeColor;
                }
            }
        }

        return item;
    }

    /// <summary>What the verification of a run found: passed for the new copy or for the stored sets, or which sets failed.</summary>
    internal static string VerifyText(RunVerify? verify) => verify switch
    {
        null => "—",
        { Ok: false } => Strings.Format(Strings.HistoryVerifyFailed, string.Join(", ", verify.FailedSets)),
        { Scope: VerifyScopes.Stored } => Strings.Format(Strings.HistoryVerifyOkStored, verify.SetsChecked),
        _ => Strings.HistoryVerifyOkNew,
    };
}
