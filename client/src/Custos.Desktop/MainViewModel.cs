using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using Custos.Core.Agent;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Monitor;
using Custos.Core.Text;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>
/// What the main window shows, made from <see cref="ServiceMonitor"/> (the same connection to the service as in
/// Custos.exe), and which of its buttons can be used now. What the buttons do is in <see cref="MainActions"/>.
/// </summary>
internal sealed class MainViewModel : Observable, IDisposable
{
    private const int MaxLogRows = ServiceMonitor.MaxLogLines;

    private readonly ServiceMonitor _monitor;
    private readonly bool _demo;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _snackTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private TaskRow? _selected;
    private CardModel? _card;
    private bool _showCard = true;
    private bool _isLogTab = true;
    private bool _isHistoryTab;
    private bool _isProgressTab;
    private string _logLevel = LogFilterLevels.All;
    private bool _logSelectedOnly;
    private string _logCount = string.Empty;
    private string _historyNote = string.Empty;
    private string? _historyTaskId;
    private int _historyGeneration;
    private string _serviceText = string.Empty;
    private bool _serviceOk;
    private string _serverText = string.Empty;
    private string _serverIcon = "cloud_off";
    private string _userText = string.Empty;
    private string _emptyText = string.Empty;
    private string _snackText = string.Empty;
    private bool _snackVisible;
    private readonly string? _knownTheme;
    private string _demoTheme = Themes.Auto;
    private string _colorChoice;
    private IReadOnlyList<TaskRow> _selection = [];
    private string? _selectWhenShown;
    private int _sortColumn;
    private bool _sortAscending = true;

    /// <param name="knownTheme">The theme the service gave last time (<see cref="LastKnown"/>), until it answers.</param>
    public MainViewModel(ServiceMonitor monitor, bool demo, string? knownTheme = null)
    {
        _monitor = monitor;
        _demo = demo;
        _knownTheme = knownTheme;
        ShowsManagement = DesktopPlatform.Current.SupportsManagement;
        ShowsTaskCreation = DesktopPlatform.Current.SupportsTaskCreation;
        _colorChoice = Palettes.Known(Ui.Color);
        ApplyColor();
        _monitor.StatusChanged += (_, _) => ShowStatus();
        _monitor.SnapshotChanged += (_, _) => ShowSnapshot();
        _monitor.ProgressChanged += (_, _) => ShowProgress();
        _monitor.LogReset += (_, _) => RebuildLog();
        _monitor.LogAdded += (_, e) => AddLog(e.Lines);
        _clock.Tick += (_, _) => Progress.Tick();
        _snackTimer.Tick += (_, _) => SnackVisible = false;
        VersionText = "CUSTOS " + Core.CustosInfo.Version;
        ShowStatus();
        ShowSnapshot();
        RebuildLog();
    }

    public ObservableCollection<TaskRow> Tasks { get; } = [];

    public ObservableCollection<LogRow> Log { get; } = [];

    public ObservableCollection<HistoryRow> History { get; } = [];

    public ProgressModel Progress { get; } = new();

    /// <summary>How this user left the window, and the colour they picked (%APPDATA%\CUSTOS\ui.json, shared with Custos.exe).</summary>
    public UiState Ui { get; } = UiState.Load();

    public ServiceMonitor Monitor => _monitor;

    public bool IsDemo => _demo;

    public bool HasSnapshot => _monitor.Snapshot is not null;

    public string TaskCount => _monitor.Snapshot?.Tasks.Count.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "0";

    public TaskRow? SelectedTask
    {
        get => _selected;
        set
        {
            // A row picked by the program (not among the selected ones) becomes the whole selection; this is set before
            // the change is told, so that the list shows the new selection and not the old one.
            if (value != _selected && (value is null ? _selection.Count > 0 : !_selection.Contains(value)))
            {
                _selection = value is null ? [] : [value];
            }

            if (Set(ref _selected, value))
            {
                ShowCardOf();
                ShowCommands();
                if (LogSelectedOnly)
                {
                    RebuildLog();
                }

                LoadHistory();
            }
        }
    }

    public CardModel? Card
    {
        get => _card;
        private set
        {
            if (Set(ref _card, value))
            {
                Changed(nameof(HasCard));
            }
        }
    }

    public bool HasCard => _card is not null;

    /// <summary>The «Карточка» button: the card is shown next to the list.</summary>
    public bool ShowCard { get => _showCard; set => Set(ref _showCard, value); }

    public bool IsLogTab { get => _isLogTab; set => Set(ref _isLogTab, value); }

    public bool IsHistoryTab
    {
        get => _isHistoryTab;
        set
        {
            if (Set(ref _isHistoryTab, value) && value)
            {
                LoadHistory();
            }
        }
    }

    public bool IsProgressTab { get => _isProgressTab; set => Set(ref _isProgressTab, value); }

    public string LogLevel
    {
        get => _logLevel;
        set
        {
            if (Set(ref _logLevel, value))
            {
                Changed(nameof(LogLevelText));
                RebuildLog();
            }
        }
    }

    public string LogLevelText => LogLevel switch
    {
        LogFilterLevels.Problems => Strings.LogShowProblems,
        LogFilterLevels.Errors => Strings.LogShowErrors,
        _ => Strings.LogShowAll,
    };

    public bool LogSelectedOnly
    {
        get => _logSelectedOnly;
        set
        {
            if (Set(ref _logSelectedOnly, value))
            {
                RebuildLog();
            }
        }
    }

    public string LogCount { get => _logCount; private set => Set(ref _logCount, value); }

    public string HistoryNote { get => _historyNote; private set => Set(ref _historyNote, value); }

    public string ServiceText { get => _serviceText; private set => Set(ref _serviceText, value); }

    public bool ServiceOk { get => _serviceOk; private set => Set(ref _serviceOk, value); }

    public string ServerText { get => _serverText; private set => Set(ref _serverText, value); }

    public string ServerIcon { get => _serverIcon; private set => Set(ref _serverIcon, value); }

    public string UserText { get => _userText; private set => Set(ref _userText, value); }

    public string VersionText { get; }

    /// <summary>«Вышла CUSTOS …» above the list while GitHub has a newer version than this one.</summary>
    public bool UpdateVisible { get => _updateVisible; private set => Set(ref _updateVisible, value); }

    public string UpdateText { get => _updateText; private set => Set(ref _updateText, value); }

    public string UpdateButton { get => _updateButton; private set => Set(ref _updateButton, value); }

    /// <summary>Only administrators install, and not twice at once.</summary>
    public bool CanInstallUpdate { get => _canInstallUpdate; private set => Set(ref _canInstallUpdate, value); }

    public bool HasReleasePage { get => _hasReleasePage; private set => Set(ref _hasReleasePage, value); }

    private bool _updateVisible;
    private string _updateText = string.Empty;
    private string _updateButton = Strings.UpdateInstall;
    private bool _canInstallUpdate;
    private bool _hasReleasePage;

    private void ShowUpdate(ProgramUpdateInfo? update)
    {
        UpdateVisible = ShowsManagement && update is { Available: true, Latest: not null };
        if (!UpdateVisible)
        {
            return;
        }

        UpdateText = Strings.Format(Strings.UpdateBanner, update!.Latest, Core.CustosInfo.Version);
        UpdateButton = update.Installing ? Strings.UpdateInstalling : Strings.UpdateInstall;
        CanInstallUpdate = _monitor.Status.Hello?.IsAdmin == true && !update.Installing;
        HasReleasePage = update.PageUrl is not null;
    }

    /// <summary>What the list says while there is no connection.</summary>
    public string EmptyText { get => _emptyText; private set => Set(ref _emptyText, value); }

    /// <summary>The selected rows, in the order of the list: several with Ctrl or Shift. The card, the history and
    /// «Изменить» take <see cref="SelectedTask"/>; switching on and off, running, stopping and deleting take all.</summary>
    public IReadOnlyList<TaskRow> Selection => _selection;

    public bool IsConnected => _demo || (_monitor.IsConnected && _monitor.Snapshot is not null);

    public bool ShowsManagement { get; }

    public bool ShowsTaskCreation { get; }

    /// <summary>Task creation relies on the same permissions as editing in the agent.</summary>
    public bool CanCreate => ShowsTaskCreation && (_demo || _monitor.CanEdit);

    /// <summary>This user may change tasks: an administrator, or everybody when the settings allow it.</summary>
    public bool CanEdit => ShowsManagement && (_demo || _monitor.CanEdit);

    /// <summary>«Изменение — только для администраторов» next to the toolbar.</summary>
    public bool ShowAdminsOnly => ShowsTaskCreation && IsConnected && !CanCreate;

    public bool CanEditOne => CanEdit && _selection.Count == 1;

    public bool CanDelete => CanEdit && _selection.Count > 0 && !_selection.Any(r => r.State == RowState.Running);

    public bool CanToggle => CanEdit && _selection.Count > 0;

    /// <summary>«Включить» when one of the selected tasks is off, «Отключить» when all are on.</summary>
    public bool ToggleOn => _selection.Count == 0 || SelectedDefinitions.Any(t => !t.Enabled);

    public string ToggleText => ToggleOn ? Strings.ToolEnable : Strings.ToolDisable;

    /// <summary>The switch as the tasks are now, as on the editor's switches: right = on. «Включить» shows it left, off.</summary>
    public string ToggleIcon => ToggleOn ? "toggle_off" : "toggle_on";

    /// <summary>One of the selected tasks neither runs nor waits in the queue.</summary>
    public bool CanRun => IsConnected && _selection.Any(r => r.State is not (RowState.Running or RowState.Queued));

    /// <summary>A selected task runs or waits; with nothing selected, the run in progress.</summary>
    public bool CanStop => IsConnected && (_selection.Any(r => r.State is RowState.Running or RowState.Queued)
        || (_selection.Count == 0 && _monitor.Snapshot?.Current is not null));

    public bool CanPause => IsConnected && _monitor.Snapshot?.Current is { Stopping: false } current
        && (_selection.Count == 0 || _selection.Any(r => Describe.SameId(r.TaskId, current.TaskId)));

    /// <summary>The «Пауза» button: is the run in progress paused now.</summary>
    public bool IsPaused => _monitor.Snapshot?.Current is { } current
        && (_monitor.Progress?.RunId == current.RunId ? _monitor.Progress.Paused : current.Paused);

    public string PauseText => IsPaused ? DesktopStrings.Resume : Strings.ToolPause;

    public string PauseIcon => IsPaused ? "play_circle" : "pause";

    /// <summary>Restoring writes anywhere on the computer: administrators only (section 4).</summary>
    public bool CanRestore => ShowsManagement && (_demo || (_monitor.IsConnected && _monitor.Status.Hello?.IsAdmin == true));

    /// <summary>The task of <see cref="SelectedTask"/> as the service has it now.</summary>
    public TaskDefinition? SelectedDefinition =>
        _selected is null ? null : _monitor.Snapshot?.Tasks.FirstOrDefault(t => Describe.SameId(t.Id, _selected.TaskId));

    /// <summary>The tasks of all selected rows, in the order of the list.</summary>
    public List<TaskDefinition> SelectedDefinitions => _monitor.Snapshot is { } snapshot
        ? [.. _selection.Select(r => snapshot.Tasks.FirstOrDefault(t => Describe.SameId(t.Id, r.TaskId))).OfType<TaskDefinition>()]
        : [];

    public string SnackText { get => _snackText; private set => Set(ref _snackText, value); }

    public bool SnackVisible { get => _snackVisible; set => Set(ref _snackVisible, value); }

    /// <summary>The colour scheme of this user (Palettes.cs), picked in the «Настройки» menu and kept in ui.json.</summary>
    public string ColorChoice
    {
        get => _colorChoice;
        set
        {
            if (_colorChoice == value)
            {
                return;
            }

            // The colours first: whoever hears of the change (the title bar of Windows 11) reads the new ones.
            _colorChoice = value;
            ApplyColor();
            Changed(nameof(ColorChoice));
            Ui.Color = value == Palettes.Default ? null : value;
            Ui.Save();
        }
    }

    /// <summary>The theme of the CUSTOS settings, the same for all users of the computer (the made-up one in --demo).</summary>
    public string ThemeNow => _demo ? _demoTheme
        : _monitor.Snapshot?.Settings.Theme is { } theme && Themes.IsKnown(theme) ? theme
        : _knownTheme ?? Themes.Auto;

    public int SortColumn => _sortColumn;

    /// <summary>The list was made anew (tasks added, deleted, sorted): the window selects <see cref="Selection"/> again.</summary>
    public event EventHandler? SelectionRestored;

    public bool SortAscending => _sortAscending;

    public void Start()
    {
        if (_demo)
        {
            DemoData.Feed(_monitor);
        }
        else
        {
            _monitor.Start();
        }
    }

    /// <summary>A click on a column header: sort by it, or turn the order round.</summary>
    public void SortBy(int column)
    {
        _sortAscending = column != _sortColumn || !_sortAscending;
        _sortColumn = column;
        Changed(nameof(SortColumn));
        ShowSnapshot();
    }

    /// <summary>The theme of --demo and of the pictures: there is no service whose settings would have one.</summary>
    public void SetDemoTheme(string theme)
    {
        _demoTheme = Themes.IsKnown(theme) ? theme : Themes.Auto;
        ApplyTheme();
    }

    /// <summary>The rows selected in the list (Ctrl, Shift, Ctrl+A); the first one is <see cref="SelectedTask"/>.</summary>
    public void Select(IReadOnlyList<TaskRow> rows)
    {
        _selection = rows;
        if (rows.Count == 0)
        {
            SelectedTask = null;
        }
        else if (_selected is null || !rows.Contains(_selected))
        {
            SelectedTask = rows[0];
        }

        ShowCommands();
        if (LogSelectedOnly)
        {
            RebuildLog();
        }
    }

    /// <summary>Selects the task once the snapshot with it arrives (after it was created or cloned).</summary>
    public void SelectWhenShown(string taskId)
    {
        _selectWhenShown = taskId;
        if (Tasks.FirstOrDefault(r => Describe.SameId(r.TaskId, taskId)) is { } row)
        {
            _selection = [row];
            SelectedTask = row;
            _selectWhenShown = null;
            SelectionRestored?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A name like the given one that no task has yet: «Новое задание 2».</summary>
    public string UniqueName(string name)
    {
        var tasks = _monitor.Snapshot?.Tasks ?? [];
        string candidate = name;
        for (int i = 2; tasks.Any(t => string.Equals(t.Name, candidate, StringComparison.OrdinalIgnoreCase)); i++)
        {
            candidate = string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{name} {i}");
        }

        return candidate;
    }

    public void Snack(string text)
    {
        SnackText = text;
        SnackVisible = true;
        _snackTimer.Stop();
        _snackTimer.Start();
    }

    public void Dispose()
    {
        _clock.Stop();
        _snackTimer.Stop();
        _monitor.Dispose();
    }

    private void ShowStatus()
    {
        var status = _monitor.Status;
        bool demo = _demo;
        ServiceOk = demo || status.State == ServiceState.Running;
        ServiceText = demo ? Strings.StateRunning : ServiceMonitor.Describe(status.State);
        ShowUserRights();
        EmptyText = status.State switch
        {
            ServiceState.Running => string.Empty,
            ServiceState.Connecting => Describe.Capitalize(ServiceMonitor.Describe(status.State)),
            _ => Strings.Format(Strings.NoConnection, status.Problem ?? Describe.Capitalize(ServiceMonitor.Describe(status.State))),
        };
        ShowCommands();
    }

    /// <summary>Which buttons can be used now: after a change of the connection, the tasks or the selection.</summary>
    private void ShowCommands()
    {
        foreach (string name in (string[])[nameof(IsConnected), nameof(CanCreate), nameof(CanEdit), nameof(ShowAdminsOnly), nameof(CanEditOne), nameof(CanDelete),
            nameof(CanToggle), nameof(ToggleOn), nameof(ToggleText), nameof(ToggleIcon), nameof(CanRun), nameof(CanStop), nameof(CanPause),
            nameof(IsPaused), nameof(PauseText), nameof(PauseIcon), nameof(CanRestore)])
        {
            Changed(name);
        }
    }

    private void ShowSnapshot()
    {
        var snapshot = _monitor.Snapshot;
        if (!ShowsManagement && ShowsTaskCreation)
        {
            ShowUserRights();
        }

        Changed(nameof(HasSnapshot));
        Changed(nameof(TaskCount));
        if (snapshot is null)
        {
            Tasks.Clear();
            SelectedTask = null;
            Card = null;
            Progress.Show(null, null);
            ShowServer(null);
            ShowCommands();
            return;
        }

        var rows = Sort(snapshot.Tasks.Select(t => MakeRow(t, snapshot, _monitor.Progress)));
        if (rows.Select(r => r.TaskId).SequenceEqual(Tasks.Select(r => r.TaskId), StringComparer.OrdinalIgnoreCase))
        {
            for (int i = 0; i < rows.Count; i++)
            {
                Tasks[i].CopyFrom(rows[i]);
            }
        }
        else
        {
            // Tasks were added, deleted or sorted: the same tasks stay selected, or the first one when none is left.
            string? keep = _selected?.TaskId;
            var kept = _selection.Select(r => r.TaskId).ToList();
            Tasks.Clear();
            foreach (var row in rows)
            {
                Tasks.Add(row);
            }

            var again = Tasks.Where(r => kept.Any(id => Describe.SameId(id, r.TaskId))).ToList();
            var first = Tasks.FirstOrDefault(r => Describe.SameId(r.TaskId, keep)) ?? again.FirstOrDefault() ?? Tasks.FirstOrDefault();
            _selection = first is null ? [] : again.Count > 0 ? again : [first];
            SelectedTask = first;
            SelectionRestored?.Invoke(this, EventArgs.Empty);
        }

        if (_selectWhenShown is { } wanted && Tasks.FirstOrDefault(r => Describe.SameId(r.TaskId, wanted)) is { } found)
        {
            _selection = [found];
            SelectedTask = found;
            _selectWhenShown = null;
            SelectionRestored?.Invoke(this, EventArgs.Empty);
        }

        ShowCardOf();
        ShowServer(snapshot.Server);
        ShowUpdate(snapshot.ProgramUpdate);
        ShowProgress();
        ApplyTheme();
        ShowCommands();
        if (_selected is not null && snapshot.States.FirstOrDefault(s => Describe.SameId(s.TaskId, _selected.TaskId)) is { LastRun.RunId: { } lastRun }
            && _historyRunSeen != lastRun)
        {
            LoadHistory();
        }
    }

    private void ShowUserRights() => UserText = _demo ? Strings.Format(Strings.UserWithRights, @"OFFICE\admin", Strings.RightsAdmin)
        : _monitor.Status.Hello?.User is { } user ? Strings.Format(Strings.UserWithRights, user, _monitor.CanEdit ? Strings.RightsAdmin : Strings.RightsUser)
        : string.Empty;

    private string? _historyRunSeen;

    private void ShowProgress()
    {
        var snapshot = _monitor.Snapshot;
        var current = snapshot?.Current;
        Progress.Show(current, _monitor.Progress);
        Changed(nameof(IsPaused));
        Changed(nameof(PauseText));
        Changed(nameof(PauseIcon));
        if (current is not null)
        {
            _clock.Start();
            var (p, paused) = Describe.Latest(current, _monitor.Progress);
            if (Tasks.FirstOrDefault(r => Describe.SameId(r.TaskId, current.TaskId)) is { } row)
            {
                row.Percent = p.Percent;
                row.Result = Describe.RunningText(current.Stopping, paused, p.Percent);
            }

            if (_card is not null && Describe.SameId(_card.TaskId, current.TaskId))
            {
                _card.ShowProgress = true;
                _card.Percent = p.Percent;
                _card.Live = !paused && !current.Stopping;
                _card.ProgressLine = Describe.ProgressLine(current, p, paused);
            }
        }
        else
        {
            _clock.Stop();
            if (_card is not null)
            {
                _card.ShowProgress = false;
            }
        }
    }

    private void ShowServer(ServerLinkInfo? server)
    {
        string host = server?.ServerHost ?? string.Empty;
        (ServerText, ServerIcon) = server?.State switch
        {
            null or ServerLinkStates.Off => (Strings.ServerNotConnected, "cloud_off"),
            ServerLinkStates.Approved => (Strings.Format(Strings.ServerOnline, host), "cloud_done"),
            ServerLinkStates.Pending => (Strings.Format(Strings.ServerPending, host), "cloud_sync"),
            ServerLinkStates.Disabled => (Strings.Format(Strings.ServerDisabled, host), "cloud_off"),
            ServerLinkStates.Connecting => (Strings.Format(Strings.ServerConnecting, host), "cloud_sync"),
            ServerLinkStates.Offline => (Strings.Format(Strings.ServerOffline,
                server.OfflineSince is { } since ? Math.Max(0, (int)(DateTimeOffset.UtcNow - since).TotalMinutes) : 0), "cloud_off"),
            _ => (Strings.Format(Strings.ServerProblem, host, ServerLink.StateText(server.State).TrimEnd('.')), "cloud_off"),
        };
    }

    private void ApplyColor()
    {
        if (Application.Current is { } app)
        {
            Palettes.Apply(app, _colorChoice);
        }
    }

    private void ApplyTheme()
    {
        Changed(nameof(ThemeNow));
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = ThemeNow switch
            {
                Themes.Light => ThemeVariant.Light,
                Themes.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }

    private static TaskRow MakeRow(TaskDefinition task, AgentSnapshot snapshot, RunProgressEvent? progress)
    {
        var state = snapshot.States.FirstOrDefault(s => Describe.SameId(s.TaskId, task.Id));
        var current = snapshot.Current is { } run && Describe.SameId(run.TaskId, task.Id) ? run : null;
        var last = state?.LastRun;
        RowState rowState;
        string result;
        double percent = 0;
        if (current is not null)
        {
            var (p, paused) = Describe.Latest(current, progress);
            rowState = RowState.Running;
            percent = p.Percent;
            result = Describe.RunningText(current.Stopping, paused, p.Percent);
        }
        else if (state?.Queued == true)
        {
            rowState = RowState.Queued;
            result = Strings.ResultQueued;
        }
        else if (!task.Enabled)
        {
            rowState = RowState.Off;
            result = Strings.ResultDisabled;
        }
        else
        {
            rowState = Describe.StateOf(last?.Status);
            result = last is null ? Strings.ResultNeverRun : Words.Status(last.Status);
        }

        return new TaskRow
        {
            TaskId = task.Id,
            Name = task.Name,
            Type = Words.BackupType(task.BackupType),
            Schedule = TaskDescriber.ScheduleWithCatchUp(task.Schedule),
            LastRun = current is not null ? DesktopStrings.Now : Describe.Time(last?.StartedAt),
            LastRunTime = current?.StartedAt ?? last?.StartedAt,
            Result = result,
            NextRun = Describe.Time(state?.NextRun),
            NextRunTime = state?.NextRun,
            State = rowState,
            Percent = percent,
        };
    }

    private List<TaskRow> Sort(IEnumerable<TaskRow> rows)
    {
        var names = StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture, ignoreCase: true);
        Comparison<TaskRow> compare = _sortColumn switch
        {
            1 => (a, b) => names.Compare(a.Type, b.Type),
            2 => (a, b) => names.Compare(a.Schedule, b.Schedule),
            3 => (a, b) => Nullable.Compare(a.LastRunTime, b.LastRunTime),
            4 => (a, b) => a.State.CompareTo(b.State) is var c && c != 0 ? c : names.Compare(a.Result, b.Result),
            5 => (a, b) => (a.NextRunTime, b.NextRunTime) switch
            {
                (null, null) => 0,
                (null, _) => 1,
                (_, null) => -1,
                var (x, y) => x!.Value.CompareTo(y!.Value),
            },
            _ => (a, b) => 0,
        };
        var list = rows.ToList();
        list.Sort((a, b) =>
        {
            int result = compare(a, b);
            if (result == 0)
            {
                result = names.Compare(a.Name, b.Name);
            }

            return _sortAscending ? result : -result;
        });
        return list;
    }

    private void ShowCardOf()
    {
        var snapshot = _monitor.Snapshot;
        var task = _selected is null ? null : snapshot?.Tasks.FirstOrDefault(t => Describe.SameId(t.Id, _selected.TaskId));
        if (task is null || snapshot is null)
        {
            Card = null;
            return;
        }

        var state = snapshot.States.FirstOrDefault(s => Describe.SameId(s.TaskId, task.Id));
        var rows = new List<CardRow> { new(Strings.CardSources, Describe.Sources(task)) };
        if (Describe.Databases(task) is { } databases)
        {
            rows.Add(new(Strings.CardDatabases, databases));
        }

        rows.Add(new(Strings.CardDestinations, task.Destinations.Count == 0 ? Strings.CardNone
            : string.Join(Environment.NewLine, task.Destinations.Select(d => Describe.Destination(d, snapshot)))));
        if (Describe.Protection(task) is { } protection)
        {
            rows.Add(new(Strings.CardProtection, protection, Accent: true));
        }

        rows.Add(new(Strings.CardArchive, Describe.Archive(task)));
        rows.Add(new(Strings.CardRetention, TaskDescriber.Retention(task).TrimEnd('.')));
        rows.Add(new(Strings.CardSchedule, TaskDescriber.ScheduleWithCatchUp(task.Schedule)));
        if (TaskDescriber.FullBackups(task) is { } fullBackups)
        {
            rows.Add(new(Strings.CardFullBackup, fullBackups));
        }

        if (task.Events.Pre.Count > 0)
        {
            rows.Add(new(Strings.CardEventsBefore, string.Join(Environment.NewLine, task.Events.Pre.Select(Describe.Event))));
        }

        var after = task.Events.Post.Select(Describe.Event).ToList();
        if (Describe.After(task.Events.After) is { } power)
        {
            after.Add(power);
        }

        if (after.Count > 0)
        {
            rows.Add(new(Strings.CardEventsAfter, string.Join(Environment.NewLine, after)));
        }

        rows.Add(new(Strings.CardLastSuccess, state?.LastSuccess is { } success
            ? Strings.Format(Strings.CardRunSummary, Describe.Time(success.FinishedAt ?? success.StartedAt), Describe.Number(success.FilesProcessed), Sizes.Format(success.BytesWritten))
            : Strings.CardNever));
        rows.Add(new(Strings.CardNextRun, !task.Enabled ? Strings.CardDisabledNoRuns
            : state?.NextRun is { } next ? Describe.Time(next)
            : Strings.CardOnRequest));
        if (task.UpdatedAt is not null || task.UpdatedBy is not null)
        {
            rows.Add(new(Strings.CardChanged, Strings.Format(Strings.CardChangedBy, Describe.Time(task.UpdatedAt), Describe.Author(task.UpdatedBy))));
        }

        if (!string.IsNullOrWhiteSpace(task.Comment))
        {
            rows.Add(new(Strings.CardComment, task.Comment));
        }

        // A failed last run goes on top, in the error colour, instead of a red line among the others.
        string? error = state?.LastRun is { Status: { } status } last && !RunStatuses.IsSuccessful(status)
            ? Strings.Format(Strings.CardFailedRun, Describe.Time(last.StartedAt), Words.Status(status), last.ErrorMessage ?? string.Empty).TrimEnd(' ', ':', '—')
            : null;

        Card = new CardModel
        {
            TaskId = task.Id,
            Name = task.Name,
            Subtitle = Strings.Format(Strings.CardSubtitle, Words.BackupType(task.BackupType), task.Enabled ? Strings.CardEnabled : Strings.CardDisabled),
            Error = _selected!.State == RowState.Running ? null : error,
            Rows = rows,
            State = _selected.State,
        };
        ShowProgress();
    }

    private void RebuildLog()
    {
        Log.Clear();
        var lines = _monitor.Log;
        for (int i = lines.Count - 1; i >= 0 && Log.Count < MaxLogRows; i--)
        {
            if (Shown(lines[i]))
            {
                Log.Add(Row(lines[i]));
            }
        }

        LogCount = Strings.Format(Strings.JournalCount, Log.Count, lines.Count);
    }

    private void AddLog(IReadOnlyList<LogLine> lines)
    {
        foreach (var line in lines)
        {
            if (Shown(line))
            {
                Log.Insert(0, Row(line));
            }
        }

        while (Log.Count > MaxLogRows)
        {
            Log.RemoveAt(Log.Count - 1);
        }

        LogCount = Strings.Format(Strings.JournalCount, Log.Count, _monitor.Log.Count);
    }

    private bool Shown(LogLine line) =>
        LogFilterLevels.Matches(LogLevel, line.Level)
        && (!LogSelectedOnly || _selected is null || Describe.SameId(line.TaskId, _selected.TaskId));

    internal static LogRow Row(LogLine line) => new()
    {
        Seq = line.Seq,
        Time = line.Time == DateTimeOffset.MinValue ? string.Empty : Describe.LogTime(line.Time),
        Level = line.Level,
        TaskId = line.TaskId,
        Task = string.IsNullOrEmpty(line.TaskName) ? string.Empty : line.TaskName + ": ",
        Text = line.Text,
    };

    private async void LoadHistory()
    {
        if (!_isHistoryTab && _historyTaskId == _selected?.TaskId)
        {
            return;
        }

        int generation = ++_historyGeneration;
        var task = _selected;
        _historyTaskId = task?.TaskId;
        History.Clear();
        if (task is null)
        {
            HistoryNote = Strings.HistorySelectTask;
            return;
        }

        _historyRunSeen = _monitor.Snapshot?.States.FirstOrDefault(s => Describe.SameId(s.TaskId, task.TaskId))?.LastRun?.RunId;
        HistoryNote = string.Empty;
        try
        {
            var result = _demo
                ? DemoData.History(task.TaskId)
                : await _monitor.RequestAsync(PipeProtocol.Methods.HistoryGet, new HistoryRequest { TaskId = task.TaskId },
                    IpcJsonContext.Default.HistoryRequest, IpcJsonContext.Default.HistoryResult);
            if (generation != _historyGeneration)
            {
                return;
            }

            foreach (var run in result.Runs)
            {
                History.Add(new HistoryRow
                {
                    Run = run,
                    Started = Describe.Time(run.StartedAt),
                    Duration = run.FinishedAt is { } finished ? Sizes.FormatDuration(finished - run.StartedAt) : "—",
                    Type = Words.BackupType(run.BackupType),
                    Trigger = Words.Trigger(run.Trigger, run.ScheduledFor, run.RequestedBy),
                    Files = Describe.Number(run.FilesProcessed),
                    Size = Sizes.Format(run.BytesWritten),
                    Result = Words.Status(run.Status),
                    Details = run.ErrorMessage ?? (run.Warnings > 0 ? Strings.Format(Strings.HistoryWarnings, run.Warnings) : Describe.Verify(run.Verify)),
                    State = Describe.StateOf(run.Status),
                });
            }

            HistoryNote = result.Runs.Count == 0 ? Strings.HistoryEmpty : Strings.Format(DesktopStrings.HistoryCount, task.Name, result.Runs.Count);
        }
        catch (ServiceUnavailableException ex)
        {
            if (generation == _historyGeneration)
            {
                HistoryNote = ex.Message;
            }
        }
    }
}

/// <summary>The «Прогресс» tab: the run in progress in detail.</summary>
internal sealed class ProgressModel : Observable
{
    private CurrentRun? _run;
    private bool _active;
    private string _task = string.Empty;
    private string _trigger = string.Empty;
    private string _phase = string.Empty;
    private string _file = string.Empty;
    private string _files = string.Empty;
    private string _bytes = string.Empty;
    private string _speed = string.Empty;
    private string _elapsed = string.Empty;
    private string _remaining = string.Empty;
    private double _percent;
    private bool _live;
    private IReadOnlyList<DestinationRow> _destinations = [];

    public bool Active { get => _active; private set => Set(ref _active, value); }

    public string Task { get => _task; private set => Set(ref _task, value); }

    public string Trigger { get => _trigger; private set => Set(ref _trigger, value); }

    public string Phase { get => _phase; private set => Set(ref _phase, value); }

    public string File { get => _file; private set => Set(ref _file, value); }

    public string Files { get => _files; private set => Set(ref _files, value); }

    public string Bytes { get => _bytes; private set => Set(ref _bytes, value); }

    public string Speed { get => _speed; private set => Set(ref _speed, value); }

    public string Elapsed { get => _elapsed; private set => Set(ref _elapsed, value); }

    public string Remaining { get => _remaining; private set => Set(ref _remaining, value); }

    public double Percent { get => _percent; private set => Set(ref _percent, value); }

    public bool Live { get => _live; private set => Set(ref _live, value); }

    public IReadOnlyList<DestinationRow> Destinations { get => _destinations; private set => Set(ref _destinations, value); }

    /// <summary>Set by the screenshot run, so that «Прошло» does not change from one picture to the next.</summary>
    public static DateTimeOffset? FixedNow { get; set; }

    public void Show(CurrentRun? run, RunProgressEvent? progress)
    {
        _run = run;
        Active = run is not null;
        if (run is null)
        {
            return;
        }

        var (p, paused) = Describe.Latest(run, progress);
        Task = run.TaskName;
        Trigger = Words.Trigger(run.Trigger, run.ScheduledFor, run.RequestedBy);
        string phase = Describe.PhaseText(p.Phase);
        Phase = run.Stopping ? Strings.ResultStopping : paused ? Strings.Format(Strings.ProgressPausedPhase, phase) : phase;
        File = p.CurrentFile ?? string.Empty;
        Files = p.FilesTotal > 0 ? Strings.Format(Strings.ProgressOf, Describe.Number(p.FilesDone), Describe.Number(p.FilesTotal)) : Describe.Number(p.FilesDone);
        Bytes = p.BytesTotal > 0 ? Strings.Format(Strings.ProgressOf, Sizes.Format(p.BytesDone), Sizes.Format(p.BytesTotal)) : Sizes.Format(p.BytesDone);
        Speed = p.SpeedBps > 0 && !paused ? Sizes.FormatSpeed(p.SpeedBps) : "—";
        Remaining = p.EtaS is { } eta && !paused ? Sizes.FormatDuration(TimeSpan.FromSeconds(eta)) : "—";
        Percent = p.Percent;
        Live = !paused && !run.Stopping;
        Destinations = [.. p.Destinations.Select(d => new DestinationRow(d.Label, Describe.DestinationState(d.State), d.Error ?? string.Empty,
            d.State == Core.Backup.DestinationStates.Failed))];
        Tick();
    }

    public void Tick()
    {
        if (_run is not null)
        {
            Elapsed = Sizes.FormatDuration((FixedNow ?? DateTimeOffset.UtcNow) - _run.StartedAt);
        }
    }
}
