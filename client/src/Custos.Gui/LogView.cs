using System.Globalization;
using System.Text;
using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Text;

namespace Custos.Gui;

/// <summary>
/// The "Log" tab: lines of the service in real time (docs/client-ui.md, 2.4). Warnings in amber, errors in red, each
/// with its icon. Follows the newest line unless the user scrolled up.
/// </summary>
internal sealed class LogView : ListView
{
    private readonly ContextMenuStrip _menu = new();

    public LogView()
    {
        View = View.Details;
        FullRowSelect = true;
        HideSelection = false;
        DoubleBuffered = true;
        HeaderStyle = ColumnHeaderStyle.Nonclickable;
        Columns.Add(Strings.ColumnTime, 130);
        Columns.Add(Strings.ColumnMessage, 900);

        _menu.Items.Add(Strings.MenuCopy, null, (_, _) => CopySelected());
        _menu.Items.Add(Strings.MenuSelectAll, null, (_, _) => SelectAll());
        ContextMenuStrip = _menu;
    }

    public void Reset(IList<LogLine> lines)
    {
        BeginUpdate();
        try
        {
            Items.Clear();
            Items.AddRange([.. lines.Select(MakeItem)]);
        }
        finally
        {
            EndUpdate();
        }

        ScrollToEnd();
    }

    public void Append(IReadOnlyList<LogLine> lines)
    {
        bool follow = Items.Count == 0 || IsLastVisible();
        BeginUpdate();
        try
        {
            Items.AddRange([.. lines.Select(MakeItem)]);
            int extra = Items.Count - ServiceMonitor.MaxLogLines;
            for (int i = 0; i < extra; i++)
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
            text.Append(item.Text).Append('\t').AppendLine(item.SubItems[1].Text);
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

    private static ListViewItem MakeItem(LogLine line)
    {
        var local = line.Time.ToLocalTime();
        string time = local.Date == DateTime.Today
            ? local.ToString("T", CultureInfo.CurrentCulture)
            : local.ToString("d", CultureInfo.CurrentCulture) + " " + local.ToString("T", CultureInfo.CurrentCulture);
        var item = new ListViewItem([time, line.Text]);
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

/// <summary>The "Run history" tab: the runs of the selected task, newest first (docs/client-ui.md, 2.4).</summary>
internal sealed class HistoryView : UserControl
{
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        ShowItemToolTips = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };

    private readonly Label _message = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = SystemColors.GrayText,
    };

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
        Controls.Add(_list);
        Controls.Add(_message);
        ShowMessage(Strings.HistorySelectTask);
    }

    public ListView List => _list;

    public void ShowMessage(string text)
    {
        _message.Text = text;
        _message.Visible = true;
        _list.Visible = false;
    }

    public void ShowRuns(List<RunRecord> runs)
    {
        if (runs.Count == 0)
        {
            ShowMessage(Strings.HistoryEmpty);
            return;
        }

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var run in runs)
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

                _list.Items.Add(item);
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        _message.Visible = false;
        _list.Visible = true;
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
