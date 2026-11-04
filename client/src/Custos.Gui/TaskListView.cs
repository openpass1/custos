using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Text;

namespace Custos.Gui;

/// <summary>
/// The list of tasks (docs/client-ui.md, 2.2): one row per task with a state icon. Next to the card it shows three
/// columns, alone all six. Sorted by a click on a column header.
/// </summary>
internal sealed class TaskListView : ListView
{
    private static readonly TaskColumn[] AllColumns =
        [TaskColumn.Name, TaskColumn.Type, TaskColumn.Schedule, TaskColumn.LastRun, TaskColumn.Result, TaskColumn.NextRun];

    private static readonly TaskColumn[] CompactColumns = [TaskColumn.Name, TaskColumn.Result, TaskColumn.NextRun];

    private TaskColumn[] _columns = AllColumns;
    private TaskColumn _sortColumn = TaskColumn.Name;
    private bool _sortAscending = true;
    private List<Row> _rows = [];
    private string? _selectedId;
    private bool _updating;

    public TaskListView()
    {
        View = View.Details;
        FullRowSelect = true;
        MultiSelect = false;
        HideSelection = false;
        DoubleBuffered = true;
        ColumnClick += OnColumnClick;
        SelectedIndexChanged += (_, _) => CheckSelection();
    }

    /// <summary>Raised once when another task gets selected (or none).</summary>
    public event EventHandler? SelectedTaskChanged;

    public enum TaskColumn
    {
        Name,
        Type,
        Schedule,
        LastRun,
        Result,
        NextRun,
    }

    public string? SelectedTaskId => _selectedId;

    public bool Compact { get; private set; }

    /// <summary>Switches between the three columns next to the card and all six.</summary>
    /// <param name="widths">Column widths at 100 % scale by column name; missing ones get defaults.</param>
    public void SetColumns(bool compact, IReadOnlyDictionary<string, int> widths)
    {
        Compact = compact;
        _columns = compact ? CompactColumns : AllColumns;
        BeginUpdate();
        try
        {
            Items.Clear();
            Columns.Clear();
            foreach (var column in _columns)
            {
                int width = widths.TryGetValue(column.ToString(), out int saved) && saved > 10 ? saved : DefaultWidth(column, compact);
                Columns.Add(new ColumnHeader { Text = Title(column, compact), Width = LogicalToDeviceUnits(width), Tag = column });
            }

            Fill();
        }
        finally
        {
            EndUpdate();
        }
    }

    /// <summary>Column widths at 100 % scale, for ui.json.</summary>
    public Dictionary<string, int> GetColumnWidths()
    {
        var result = new Dictionary<string, int>();
        foreach (ColumnHeader header in Columns)
        {
            result[header.Tag!.ToString()!] = (int)Math.Round(header.Width * 96.0 / DeviceDpi);
        }

        return result;
    }

    /// <summary>Shows the tasks of a snapshot; keeps the selection and the scroll position where it can.</summary>
    public void ShowTasks(AgentSnapshot? snapshot)
    {
        _rows = snapshot is null ? [] : [.. snapshot.Tasks.Select(t => MakeRow(t, snapshot))];
        SortRows();
        bool sameOrder = Items.Count == _rows.Count
            && _rows.Select(r => r.TaskId).SequenceEqual(Items.Cast<ListViewItem>().Select(i => (string)i.Tag!));
        if (sameOrder)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                Update(Items[i], _rows[i]);
            }

            return;
        }

        BeginUpdate();
        try
        {
            Fill();
        }
        finally
        {
            EndUpdate();
        }
    }

    /// <summary>Updates the "Running 47 %" of the task in progress between snapshots.</summary>
    public void ShowRunning(string taskId, string text)
    {
        int column = Array.IndexOf(_columns, TaskColumn.Result);
        if (column < 0)
        {
            return;
        }

        foreach (ListViewItem item in Items)
        {
            if (string.Equals((string)item.Tag!, taskId, StringComparison.OrdinalIgnoreCase)
                && item.ImageKey == nameof(StateIcon.Running) && item.SubItems[column].Text != text)
            {
                item.SubItems[column].Text = text;
            }
        }
    }

    public void SelectTask(string taskId)
    {
        foreach (ListViewItem item in Items)
        {
            if (string.Equals((string)item.Tag!, taskId, StringComparison.OrdinalIgnoreCase))
            {
                item.Selected = true;
                item.Focused = true;
                item.EnsureVisible();
                return;
            }
        }
    }

    private void Fill()
    {
        _updating = true;
        try
        {
            Items.Clear();
            foreach (var row in _rows)
            {
                var item = new ListViewItem { Tag = row.TaskId };
                for (int i = 1; i < _columns.Length; i++)
                {
                    item.SubItems.Add(string.Empty);
                }

                Update(item, row);
                Items.Add(item);
                if (string.Equals(row.TaskId, _selectedId, StringComparison.OrdinalIgnoreCase))
                {
                    item.Selected = true;
                    item.Focused = true;
                }
            }
        }
        finally
        {
            _updating = false;
        }

        CheckSelection();
    }

    private void Update(ListViewItem item, Row row)
    {
        for (int i = 0; i < _columns.Length; i++)
        {
            string text = row.Text(_columns[i], Compact);
            if (item.SubItems[i].Text != text)
            {
                item.SubItems[i].Text = text;
            }
        }

        string icon = row.Icon.ToString();
        if (item.ImageKey != icon)
        {
            item.ImageKey = icon;
        }
    }

    private void CheckSelection()
    {
        if (_updating)
        {
            return;
        }

        string? id = SelectedItems.Count > 0 ? (string)SelectedItems[0].Tag! : null;
        if (!string.Equals(id, _selectedId, StringComparison.OrdinalIgnoreCase))
        {
            _selectedId = id;
            SelectedTaskChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnColumnClick(object? sender, ColumnClickEventArgs e)
    {
        var column = _columns[e.Column];
        _sortAscending = column != _sortColumn || !_sortAscending;
        _sortColumn = column;
        SortRows();
        BeginUpdate();
        try
        {
            Fill();
        }
        finally
        {
            EndUpdate();
        }
    }

    private void SortRows()
    {
        // Names are sorted the way people read them, so by the rules of the user's language.
        var names = StringComparer.CurrentCultureIgnoreCase;
        Comparison<Row> compare = _sortColumn switch
        {
            TaskColumn.LastRun => (a, b) => Nullable.Compare(a.LastRun, b.LastRun),
            TaskColumn.NextRun => (a, b) => CompareNullsLast(a.NextRun, b.NextRun),
            TaskColumn.Result => (a, b) => a.Icon.CompareTo(b.Icon) is var c && c != 0 ? c : names.Compare(a.Result, b.Result),
            _ => (a, b) => names.Compare(a.Text(_sortColumn, false), b.Text(_sortColumn, false)),
        };
        _rows.Sort((a, b) =>
        {
            int result = compare(a, b);
            if (result == 0)
            {
                result = names.Compare(a.Name, b.Name);
            }

            return _sortAscending ? result : -result;
        });
    }

    private static int CompareNullsLast(DateTimeOffset? a, DateTimeOffset? b) => (a, b) switch
    {
        (null, null) => 0,
        (null, _) => 1,
        (_, null) => -1,
        _ => a.Value.CompareTo(b.Value),
    };

    private static Row MakeRow(TaskDefinition task, AgentSnapshot snapshot)
    {
        var state = snapshot.States.FirstOrDefault(s => string.Equals(s.TaskId, task.Id, StringComparison.OrdinalIgnoreCase));
        var current = snapshot.Current is { } run && string.Equals(run.TaskId, task.Id, StringComparison.OrdinalIgnoreCase) ? run : null;
        var last = state?.LastRun;
        StateIcon icon;
        string result;
        if (current is not null)
        {
            icon = StateIcon.Running;
            result = TaskStates.RunningText(current);
        }
        else if (state?.Queued == true)
        {
            icon = StateIcon.Queued;
            result = Strings.ResultQueued;
        }
        else if (!task.Enabled)
        {
            icon = StateIcon.Disabled;
            result = Strings.ResultDisabled;
        }
        else
        {
            icon = TaskStates.Icon(last?.Status);
            result = last is null ? Strings.ResultNeverRun : Words.Status(last.Status);
        }

        return new Row(
            task.Id,
            task.Name,
            Words.BackupType(task.BackupType),
            TaskDescriber.ScheduleWithCatchUp(task.Schedule),
            last?.StartedAt,
            result,
            state?.NextRun,
            icon);
    }

    private static string Title(TaskColumn column, bool compact) => column switch
    {
        TaskColumn.Name => Strings.ColumnTask,
        TaskColumn.Type => Strings.ColumnType,
        TaskColumn.Schedule => Strings.ColumnSchedule,
        TaskColumn.LastRun => Strings.ColumnLastRun,
        TaskColumn.Result => Strings.ColumnResult,
        _ => compact ? Strings.ColumnNextShort : Strings.ColumnNextRun,
    };

    private static int DefaultWidth(TaskColumn column, bool compact) => column switch
    {
        TaskColumn.Name => compact ? 190 : 210,
        TaskColumn.Type => 110,
        TaskColumn.Schedule => 220,
        TaskColumn.LastRun => 120,
        TaskColumn.Result => compact ? 140 : 150,
        _ => compact ? 95 : 120,
    };

    private sealed record Row(
        string TaskId,
        string Name,
        string Type,
        string Schedule,
        DateTimeOffset? LastRun,
        string Result,
        DateTimeOffset? NextRun,
        StateIcon Icon)
    {
        public string Text(TaskColumn column, bool compact) => column switch
        {
            TaskColumn.Name => Name,
            TaskColumn.Type => Type,
            TaskColumn.Schedule => Schedule,
            TaskColumn.LastRun => Ui.Time(LastRun),
            TaskColumn.Result => Result,
            _ => compact ? Ui.ShortTime(NextRun) : Ui.Time(NextRun),
        };
    }
}

/// <summary>How the states of tasks and runs are shown.</summary>
internal static class TaskStates
{
    public static StateIcon Icon(string? status) => status switch
    {
        RunStatuses.Success => StateIcon.Success,
        RunStatuses.Warning or RunStatuses.Aborted => StateIcon.Warning,
        RunStatuses.Error => StateIcon.Error,
        null => StateIcon.None,
        _ => StateIcon.Error,
    };

    /// <summary>"Running 47 %", "Paused 47 %", "Stopping…".</summary>
    public static string RunningText(CurrentRun run) => RunningText(run.Stopping, run.Paused, run.Progress?.Percent ?? 0);

    public static string RunningText(bool stopping, bool paused, double percent) => stopping
        ? Strings.ResultStopping
        : Strings.Format(paused ? Strings.ResultPaused : Strings.ResultRunning, (int)Math.Floor(percent));
}
