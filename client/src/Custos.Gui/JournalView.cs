using Custos.Core;
using Custos.Core.Agent;
using Custos.Core.Ipc;

namespace Custos.Gui;

/// <summary>
/// The "Log" tab (docs/client-ui.md, 2.4): what the service does now, the last lines of all tasks. Above the lines: which
/// messages to show, only the tasks selected in the list, a search, and the folder of the log files.
/// </summary>
internal sealed class JournalView : UserControl
{
    private static readonly string[] Levels = [LogFilterLevels.All, LogFilterLevels.Problems, LogFilterLevels.Errors];

    private readonly ComboBox _level = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190, Margin = new Padding(3, 3, 12, 3) };
    private readonly CheckBox _selectedOnly = new() { Text = Strings.JournalSelectedOnly, AutoSize = true, Margin = new Padding(3, 5, 12, 3) };
    private readonly TextBox _search = new() { PlaceholderText = Strings.LogSearch, Width = 220, Margin = new Padding(3, 3, 12, 3) };
    private readonly Label _count = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 7, 6, 3) };
    private readonly Button _folder = new() { Text = Strings.LogFolder, AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
    private readonly ToolTip _tips = new() { AutoPopDelay = 20000 };
    private readonly System.Windows.Forms.Timer _typing = new() { Interval = 250 };
    private HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private LogFilter _text = LogFilter.All;
    private bool _loading;

    public JournalView()
    {
        _level.Items.AddRange([Strings.LogShowAll, Strings.LogShowProblems, Strings.LogShowErrors]);
        _level.SelectedIndex = 0;
        var show = new Label { Text = Strings.JournalShow, AutoSize = true, Margin = new Padding(3, 7, 0, 3) };
        var help = Ui.HelpIcon(_tips, Strings.Format(Strings.JournalHelp, LiveLogCapacity, CustosInfo.JournalDirectory));
        help.Margin = new Padding(3, 5, 6, 3);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(2, 2, 2, 0) };
        bar.Controls.AddRange([show, _level, _selectedOnly, _search, _count, help, _folder]);

        _level.SelectedIndexChanged += (_, _) => OnFilterChanged();
        _selectedOnly.CheckedChanged += (_, _) => OnFilterChanged();
        _search.TextChanged += (_, _) =>
        {
            _typing.Stop();
            _typing.Start();
        };
        _typing.Tick += (_, _) =>
        {
            _typing.Stop();
            ApplyFilter();
        };
        _folder.Click += (_, _) => Ui.OpenFolder(this, CustosInfo.LogsDirectory);

        // Docking goes from the last control added to the first: the bar, then the lines.
        Controls.Add(Log);
        Controls.Add(bar);
        Log.SetFilter(Matches);
        UpdateCount();
    }

    /// <summary>The level or "only selected tasks" was changed by the user (to remember them).</summary>
    public event EventHandler? FilterChanged;

    /// <summary>As many lines as the live log of the service keeps.</summary>
    public static int LiveLogCapacity => ServiceMonitor.MaxLogLines;

    public LogView Log { get; } = new() { Dock = DockStyle.Fill };

    /// <summary>One of <see cref="LogFilterLevels"/>.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Level
    {
        get => Levels[Math.Max(0, _level.SelectedIndex)];
        set => SetQuietly(() => _level.SelectedIndex = Math.Max(0, Array.IndexOf(Levels, value)));
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool SelectedOnly
    {
        get => _selectedOnly.Checked;
        set => SetQuietly(() => _selectedOnly.Checked = value);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Search
    {
        get => _search.Text;
        set
        {
            _search.Text = value;
            _typing.Stop();
            ApplyFilter();
        }
    }

    public void Reset(IList<LogLine> lines)
    {
        Log.Reset(lines);
        UpdateCount();
    }

    public void Append(IReadOnlyList<LogLine> lines)
    {
        Log.Append(lines);
        UpdateCount();
    }

    /// <summary>The tasks selected in the list; with "only selected tasks" the lines follow them.</summary>
    public void SetSelectedTasks(IEnumerable<string> taskIds)
    {
        var selected = new HashSet<string>(taskIds, StringComparer.OrdinalIgnoreCase);
        if (selected.SetEquals(_selected))
        {
            return;
        }

        _selected = selected;
        if (_selectedOnly.Checked)
        {
            ApplyFilter();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
            _typing.Dispose();
        }

        base.Dispose(disposing);
    }

    private bool Matches(LogLine line)
    {
        if (!LogFilterLevels.Matches(Level, line.Level))
        {
            return false;
        }

        // Without a selection the filter by task has nothing to follow: all lines.
        if (_selectedOnly.Checked && _selected.Count > 0 && (line.TaskId is null || !_selected.Contains(line.TaskId)))
        {
            return false;
        }

        return _text.Matches(line);
    }

    private void OnFilterChanged()
    {
        if (_loading)
        {
            return;
        }

        ApplyFilter();
        FilterChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyFilter()
    {
        _text = new LogFilter(LogFilterLevels.All, _search.Text);
        Log.SetFilter(Matches);
        UpdateCount();
    }

    private void SetQuietly(Action change)
    {
        _loading = true;
        try
        {
            change();
        }
        finally
        {
            _loading = false;
        }

        ApplyFilter();
    }

    private void UpdateCount() => _count.Text = Strings.Format(Strings.JournalCount, Log.Items.Count, Log.SourceCount);
}
