using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Text;
using Custos.Gui;

namespace Custos.Mac.Gui;

/// <summary>
/// The task editor of macOS: the pages of the editor of Windows (docs/client-ui.md, 3) in a list on the left. What a
/// page does not show (a Linux server, database servers, cloud drives set up elsewhere) is kept as it was.
/// </summary>
internal sealed class TaskEditorWindow : Window
{
    private readonly ServiceMonitor _monitor;
    private readonly TaskDefinition _task;
    private readonly int? _baseRevision;
    private readonly List<EditorPage> _pages;
    private readonly ContentControl _view = new();
    private readonly TextBlock _error = Ui.Error();
    private readonly Button _save;

    /// <param name="task">A copy to edit; a new task when <paramref name="isNew"/>.</param>
    public TaskEditorWindow(ServiceMonitor monitor, TaskDefinition task, bool isNew)
    {
        _monitor = monitor;
        _task = task;
        _baseRevision = isNew ? null : task.Revision;
        Title = isNew ? Strings.EditorTitleNew : Strings.Format(Strings.EditorTitle, task.Name);
        Width = 960;
        Height = 720;
        MinWidth = 820;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var context = new EditorContext(this, monitor, task);
        _pages =
        [
            new BasicPage(context),
            new SourcesPage(context),
            new DestinationsPage(context),
            new SchedulePage(context),
            new ArchivePage(context),
            new RetentionPage(context),
            new EventsPage(context),
            new AdvancedPage(context),
        ];

        var nav = new ListBox
        {
            Theme = Ui.Theme("NavList"),
            ItemsSource = _pages.Select(p => new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children = { new Icon { Kind = p.Icon, Size = 20 }, new TextBlock { Text = p.Title, VerticalAlignment = VerticalAlignment.Center } },
            }).ToList(),
            Width = 230,
            Margin = new Thickness(12, 12, 0, 12),
        };
        nav.SelectionChanged += (_, _) =>
        {
            if (nav.SelectedIndex >= 0)
            {
                _view.Content = _pages[nav.SelectedIndex].View;
            }
        };

        _save = Ui.Filled(Strings.ButtonOk, () => { });
        _save.OnClick(SaveAsync, ex => Ui.Show(_error, ex.Message));
        _save.IsEnabled = monitor.CanEdit;
        var revision = new TextBlock
        {
            Classes = { "small", "var" },
            VerticalAlignment = VerticalAlignment.Center,
            Text = isNew ? Strings.EditorNewTask
                : Strings.Format(Strings.EditorRevision, task.Revision, Describe.Time(task.UpdatedAt), task.UpdatedBy ?? "—"),
        };
        var bottom = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(24, 10, 20, 14),
            Children =
            {
                new StackPanel { Children = { revision, _error } },
                Ui.Horizontal(Ui.Text(Strings.ButtonCancel, Close), _save),
            },
        };
        Grid.SetColumn(bottom.Children[1], 1);

        var dock = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(nav, Dock.Left);
        dock.Children.Add(bottom);
        dock.Children.Add(new Border { Classes = { "divider" }, Height = 1, Margin = new Thickness(0), [DockPanel.DockProperty] = Dock.Bottom });
        dock.Children.Add(nav);
        dock.Children.Add(new Border { Classes = { "pane" }, Margin = new Thickness(12), Child = _view });
        Content = dock;
        nav.SelectedIndex = 0;
    }

    /// <summary>The saved task, after OK; null when the window was closed without saving.</summary>
    public TaskDefinition? Saved { get; private set; }

    private async Task SaveAsync()
    {
        Ui.Show(_error, null);
        foreach (var page in _pages)
        {
            if (page.Check() is { } problem)
            {
                Ui.Show(_error, $"{page.Title}: {problem}");
                return;
            }
        }

        foreach (var page in _pages)
        {
            page.Save(_task);
        }

        if (_task.Enabled && _task.Sources.Count == 0 && _task.DbServers.Count == 0)
        {
            Ui.Show(_error, Strings.ErrorNoSources);
            return;
        }

        if (_task.Enabled && _task.Destinations.Count == 0)
        {
            Ui.Show(_error, Strings.ErrorNoDestinations);
            return;
        }

        _save.IsEnabled = false;
        try
        {
            var result = await _monitor.RequestAsync(PipeProtocol.Methods.TaskSave, new TaskSaveRequest { Task = _task, BaseRevision = _baseRevision },
                IpcJsonContext.Default.TaskSaveRequest, IpcJsonContext.Default.TaskSaveResult);
            Saved = result.Task;
            Close();
        }
        catch (ServiceUnavailableException ex)
        {
            Ui.Show(_error, ex.Message);
        }
        finally
        {
            _save.IsEnabled = _monitor.CanEdit;
        }
    }
}

/// <summary>What the pages share: the window (for dialogs), the service and the task as it was opened.</summary>
internal sealed class EditorContext(Window window, ServiceMonitor monitor, TaskDefinition task)
{
    public Window Window { get; } = window;

    public ServiceMonitor Monitor { get; } = monitor;

    public TaskDefinition Task { get; } = task;

    /// <summary>The backup type chosen on the first page right now (other pages show what fits it).</summary>
    public string BackupType { get; set; } = task.BackupType;

    public event EventHandler? TypeChanged;

    public void ChangeType(string type)
    {
        BackupType = type;
        TypeChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<Credential> Credentials => Monitor.Snapshot?.Credentials ?? [];

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await Window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return (folders.Count > 0 ? folders[0].TryGetLocalPath() : null);
    }

    public async Task<string?> PickFileAsync(string title)
    {
        var files = await Window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return (files.Count > 0 ? files[0].TryGetLocalPath() : null);
    }
}

/// <summary>One page of the editor.</summary>
internal abstract class EditorPage(EditorContext context)
{
    protected EditorContext Context { get; } = context;

    public abstract string Title { get; }

    public abstract string Icon { get; }

    public abstract Control View { get; }

    /// <summary>What is wrong on the page, or null.</summary>
    public virtual string? Check() => null;

    /// <summary>Writes the fields into the task.</summary>
    public abstract void Save(TaskDefinition task);
}

internal sealed class BasicPage : EditorPage
{
    private static readonly string[] Types = [BackupTypes.Full, BackupTypes.Incremental, BackupTypes.Differential, BackupTypes.Mirror];
    private readonly TextBox _name;
    private readonly CheckBox _enabled;
    private readonly List<RadioButton> _types = [];
    private readonly NumericUpDown _forceEvery;
    private readonly ComboBox _mirrorMode;
    private readonly NumericUpDown _safeDays;
    private readonly TextBox _comment;
    private readonly Control _forceRow;
    private readonly Control _mirrorRow;

    public BasicPage(EditorContext context)
        : base(context)
    {
        var task = context.Task;
        _name = Ui.TextBox(task.Name);
        _enabled = Ui.Check(Strings.EditorEnabled, task.Enabled);
        var typeHints = new Dictionary<string, (string Title, string Hint)>
        {
            [BackupTypes.Full] = (Strings.TypeFullTitle, Strings.TypeFullHint),
            [BackupTypes.Incremental] = (Strings.TypeIncrementalTitle, Strings.TypeIncrementalHint),
            [BackupTypes.Differential] = (Strings.TypeDifferentialTitle, Strings.TypeDifferentialHint),
            [BackupTypes.Mirror] = (Strings.TypeMirrorTitle, Strings.TypeMirrorHint),
        };
        var typePanel = new StackPanel { Spacing = 2 };
        foreach (string type in Types)
        {
            var radio = Ui.Radio("type", typeHints[type].Title, task.BackupType == type, typeHints[type].Hint);
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true)
                {
                    context.ChangeType(type);
                    ShowRows();
                }
            };
            _types.Add(radio);
            typePanel.Children.Add(radio);
        }

        _forceEvery = Ui.Number(task.ForceFull.EveryRuns, 0, 1000);
        _forceRow = Ui.Row(Strings.EditorForceFull, Ui.Horizontal(new TextBlock { Text = Strings.ForceEvery }, _forceEvery, new TextBlock { Text = Strings.ForceEveryRunsSuffix }),
            Strings.ForceFullNote);
        _mirrorMode = Ui.Choice([(MirrorModes.Safe, Strings.MirrorSafe), (MirrorModes.Exact, Strings.MirrorExact)], task.MirrorMode ?? MirrorModes.Safe, 560);
        _safeDays = Ui.Number(task.Retention.SafeMirrorDays, 1, 3650);
        _mirrorRow = Ui.Stack(_mirrorMode, Ui.Horizontal(_safeDays, new TextBlock { Text = Strings.MirrorSafeDays }), Ui.Note(Strings.MirrorNote));
        _comment = Ui.Lines([task.Comment], 70);
        _comment.TextWrapping = TextWrapping.Wrap;

        View = Ui.Page(
            Ui.Title(Strings.PageBasic),
            Ui.Row(Strings.EditorName, _name),
            Ui.Row(string.Empty, _enabled),
            Ui.Heading(Strings.EditorBackupType),
            typePanel,
            _mirrorRow,
            _forceRow,
            Ui.Heading(Strings.EditorComment),
            _comment);
        ShowRows();
    }

    public override string Title => Strings.PageBasic;

    public override string Icon => "edit";

    public override Control View { get; }

    public override string? Check() => string.IsNullOrWhiteSpace(_name.Text) ? Strings.ErrorNameRequired : null;

    public override void Save(TaskDefinition task)
    {
        task.Name = (_name.Text ?? string.Empty).Trim();
        task.Enabled = Ui.Read(_enabled);
        task.BackupType = Context.BackupType;
        task.MirrorMode = task.BackupType == BackupTypes.Mirror ? Ui.Read(_mirrorMode) : task.MirrorMode;
        task.Retention.SafeMirrorDays = Ui.Read(_safeDays);
        task.ForceFull.EveryRuns = task.BackupType is BackupTypes.Incremental or BackupTypes.Differential ? Ui.Read(_forceEvery) : task.ForceFull.EveryRuns;
        task.Comment = (_comment.Text ?? string.Empty).Trim();
    }

    private void ShowRows()
    {
        _forceRow.IsVisible = Context.BackupType is BackupTypes.Incremental or BackupTypes.Differential;
        _mirrorRow.IsVisible = Context.BackupType == BackupTypes.Mirror;
    }
}

internal sealed class SourcesPage : EditorPage
{
    private readonly List<TaskSource> _sources;
    private readonly ListBox _list = Ui.List(200);
    private readonly CheckBox _recursive = Ui.Check(Strings.SourceRecursive, true);
    private readonly TextBox _include;
    private readonly TextBox _exclude;
    private readonly TextBox _excludeDirs;
    private readonly CheckBox _skipHidden;
    private bool _filling;

    public SourcesPage(EditorContext context)
        : base(context)
    {
        var task = context.Task;
        _sources = task.Sources.Select(s => s).ToList();
        _include = Ui.TextBox(string.Join("; ", task.Filters.Include), "*.docx; *.xlsx");
        _exclude = Ui.TextBox(string.Join("; ", task.Filters.Exclude));
        _excludeDirs = Ui.Lines(task.Filters.ExcludeDirs, 80);
        _skipHidden = Ui.Check(MacStrings.FilterSkipHidden, task.Filters.SkipHidden);
        _list.SelectionChanged += (_, _) => ShowSelected();
        _recursive.IsCheckedChanged += (_, _) =>
        {
            if (!_filling && Selected is { Kind: SourceKinds.Folder or SourceKinds.Unc } source)
            {
                source.Recursive = Ui.Read(_recursive);
                Fill();
            }
        };

        var add = Ui.Outlined(MacStrings.SourceAddFolder, () => { });
        add.OnClick(AddFolderAsync);
        var addFile = Ui.Outlined(MacStrings.SourceAddFile, () => { });
        addFile.OnClick(AddFileAsync);
        var addNetwork = Ui.Outlined(MacStrings.SourceAddNetwork, () => { });
        addNetwork.OnClick(AddNetworkAsync);
        var edit = Ui.Text(Strings.ButtonEdit, () => { });
        edit.OnClick(EditAsync);
        var remove = Ui.Text(Strings.ButtonRemove, Remove);

        var notes = new StackPanel();
        if (_sources.Any(s => s.Kind == SourceKinds.Ssh))
        {
            notes.Children.Add(Ui.Note(MacStrings.SourceRemoteKept));
        }

        if (task.DbServers.Count > 0)
        {
            notes.Children.Add(Ui.Note(Strings.Format(MacStrings.DatabasesKept, task.DbServers.Count)));
        }

        View = Ui.Page(
            Ui.Title(Strings.PageSources),
            Ui.Note(MacStrings.SourcesNote),
            _list,
            Ui.Horizontal(add, addFile, addNetwork, edit, remove),
            _recursive,
            notes,
            Ui.Heading(MacStrings.FiltersHeading),
            Ui.Row(MacStrings.FilterInclude, _include, MacStrings.FilterIncludeNote),
            Ui.Row(MacStrings.FilterExclude, _exclude, MacStrings.FilterExcludeNote),
            Ui.Row(MacStrings.FilterExcludeDirs, _excludeDirs, MacStrings.FilterExcludeDirsNote),
            Ui.Row(string.Empty, _skipHidden));
        Fill();
    }

    public override string Title => Strings.PageSources;

    public override string Icon => "folder";

    public override Control View { get; }

    private TaskSource? Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < _sources.Count ? _sources[_list.SelectedIndex] : null;

    public override void Save(TaskDefinition task)
    {
        task.Sources = _sources;
        task.Filters.Include = Masks(_include);
        task.Filters.Exclude = Masks(_exclude);
        task.Filters.ExcludeDirs = Ui.ReadLines(_excludeDirs);
        task.Filters.SkipHidden = Ui.Read(_skipHidden);
    }

    /// <summary>A path a source or destination can have on macOS: from the root, or //server/share.</summary>
    internal static string? PathProblem(string path, bool network)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Strings.ErrorPathRequired;
        }

        bool isNetwork = path.StartsWith("//", StringComparison.Ordinal) || path.StartsWith(@"\\", StringComparison.Ordinal);
        return network && !isNetwork ? Strings.Format(MacStrings.ErrorNotNetwork, path)
            : !network && isNetwork ? Strings.Format(Strings.ErrorUncNeedsNetwork, path)
            : !network && !Core.Backup.PathVariables.LooksAbsolute(path) ? Strings.Format(MacStrings.ErrorPathNotAbsolute, path)
            : null;
    }

    private static List<string> Masks(TextBox box) =>
        (box.Text ?? string.Empty).Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    private void Fill()
    {
        int index = _list.SelectedIndex;
        _list.ItemsSource = _sources.Select(Describe).ToList();
        _list.SelectedIndex = Math.Min(index, _sources.Count - 1);
        ShowSelected();
    }

    private static string Describe(TaskSource source) => source.Kind switch
    {
        SourceKinds.Ssh => $"{Strings.Format(MacStrings.SourceLinux, source.Host)}",
        SourceKinds.File => source.Path,
        _ => source.Recursive ? source.Path : Strings.Format(Strings.CardNotRecursive, source.Path),
    };

    private void ShowSelected()
    {
        _filling = true;
        var source = Selected;
        _recursive.IsEnabled = source is { Kind: SourceKinds.Folder or SourceKinds.Unc };
        _recursive.IsChecked = source?.Recursive ?? true;
        _filling = false;
    }

    private async Task AddFolderAsync()
    {
        if (await Context.PickFolderAsync(MacStrings.SourceAddFolder) is { } path)
        {
            _sources.Add(new TaskSource { Kind = SourceKinds.Folder, Path = path.TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : "/" });
            Fill();
            _list.SelectedIndex = _sources.Count - 1;
        }
    }

    private async Task AddFileAsync()
    {
        if (await Context.PickFileAsync(MacStrings.SourceAddFile) is { } path)
        {
            _sources.Add(new TaskSource { Kind = SourceKinds.File, Path = path, Recursive = false });
            Fill();
            _list.SelectedIndex = _sources.Count - 1;
        }
    }

    private async Task AddNetworkAsync()
    {
        if (await NetworkDialog.Show(Context, MacStrings.SourceNetworkTitle, null, null) is { } chosen)
        {
            _sources.Add(new TaskSource { Kind = SourceKinds.Unc, Path = chosen.Path, CredentialId = chosen.CredentialId });
            Fill();
            _list.SelectedIndex = _sources.Count - 1;
        }
    }

    private async Task EditAsync()
    {
        if (Selected is not { } source || source.Kind == SourceKinds.Ssh)
        {
            return;
        }

        if (source.Kind == SourceKinds.Unc)
        {
            if (await NetworkDialog.Show(Context, MacStrings.SourceNetworkTitle, source.Path, source.CredentialId) is { } chosen)
            {
                source.Path = chosen.Path;
                source.CredentialId = chosen.CredentialId;
                Fill();
            }

            return;
        }

        string? path = await Dialogs.AskText(Context.Window, Strings.SourceEditTitle, Strings.SourcePath, source.Path);
        if (path is null)
        {
            return;
        }

        if (PathProblem(path.Trim(), network: false) is { } problem)
        {
            await Dialogs.Show(Context.Window, problem);
            return;
        }

        source.Path = path.Trim();
        Fill();
    }

    private void Remove()
    {
        if (Selected is { } source)
        {
            _sources.Remove(source);
            Fill();
        }
    }
}

/// <summary>A network folder (SMB) and the account to open it with.</summary>
internal static class NetworkDialog
{
    public static async Task<(string Path, string? CredentialId)?> Show(EditorContext context, string title, string? path, string? credentialId)
    {
        var dialog = new DialogWindow(title, 600);
        var box = Ui.TextBox(path, "//nas/backup/office");
        var account = new AccountPicker(context, CredentialKinds.Windows, credentialId, allowNone: true);
        var error = Ui.Error();
        dialog.Body.Children.Add(Ui.Row(Strings.SourcePath, box, MacStrings.NetworkPathNote, 150));
        dialog.Body.Children.Add(Ui.Row(Strings.DestinationAccount, account.View, MacStrings.NetworkAccountNote, 150));
        dialog.Body.Children.Add(error);
        dialog.AddButton(Strings.ButtonCancel, () => dialog.Close(null));
        dialog.AddButton(Strings.ButtonOk, () =>
        {
            string text = (box.Text ?? string.Empty).Trim().Replace('\\', '/');
            if (SourcesPage.PathProblem(text, network: true) is { } problem)
            {
                Ui.Show(error, problem);
                return;
            }

            dialog.Close(new NetworkChoice(text, account.SelectedId));
        }, main: true);
        return await dialog.ShowDialog<NetworkChoice?>(context.Window) is { } choice ? (choice.Path, choice.CredentialId) : null;
    }

    private sealed record NetworkChoice(string Path, string? CredentialId);
}

/// <summary>A drop-down of the stored accounts of one kind, with «Add…».</summary>
internal sealed class AccountPicker
{
    private readonly EditorContext _context;
    private readonly string _kind;
    private readonly bool _allowNone;
    private readonly ComboBox _box = new() { Width = 300 };
    private List<Credential> _accounts = [];

    public AccountPicker(EditorContext context, string kind, string? selected, bool allowNone)
    {
        _context = context;
        _kind = kind;
        _allowNone = allowNone;
        var add = Ui.Text(Strings.ButtonAddAccount, () => { });
        add.OnClick(AddAsync);
        View = Ui.Horizontal(_box, add);
        Fill(selected);
    }

    public Control View { get; }

    public string? SelectedId
    {
        get
        {
            int index = _box.SelectedIndex - (_allowNone ? 1 : 0);
            return index >= 0 && index < _accounts.Count ? _accounts[index].Id : null;
        }
    }

    private void Fill(string? selected)
    {
        _accounts = _context.Credentials.Where(c => c.Kind == _kind).OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (selected is not null && _accounts.All(c => !string.Equals(c.Id, selected, StringComparison.OrdinalIgnoreCase)))
        {
            _accounts.Add(new Credential { Id = selected, Kind = _kind, User = Strings.AccountUnknown });
        }

        var items = new List<string>();
        if (_allowNone)
        {
            items.Add(Strings.AccountNone);
        }

        items.AddRange(_accounts.Select(c => c.DisplayName));
        _box.ItemsSource = items;
        int index = _accounts.FindIndex(c => string.Equals(c.Id, selected, StringComparison.OrdinalIgnoreCase));
        _box.SelectedIndex = index >= 0 ? index + (_allowNone ? 1 : 0) : 0;
    }

    private async Task AddAsync()
    {
        if (await Dialogs.EditAccount(_context.Window, _context.Monitor, _kind, null) is { } saved)
        {
            // The snapshot with the new account comes a moment later; show it at once.
            _accounts.Add(saved);
            var items = (List<string>)_box.ItemsSource!;
            items.Add(saved.DisplayName);
            _box.ItemsSource = null;
            _box.ItemsSource = items;
            _box.SelectedIndex = items.Count - 1;
        }
    }
}

internal sealed class DestinationsPage : EditorPage
{
    private readonly List<TaskDestination> _destinations;
    private readonly ListBox _list = Ui.List(240);

    public DestinationsPage(EditorContext context)
        : base(context)
    {
        _destinations = context.Task.Destinations.ToList();
        var add = Ui.Outlined(Strings.ButtonAdd, () => { });
        add.OnClick(AddAsync);
        var edit = Ui.Text(Strings.ButtonEdit, () => { });
        edit.OnClick(EditAsync);
        var remove = Ui.Text(Strings.ButtonRemove, Remove);
        _list.DoubleTapped += (_, _) => Safe.Run(EditAsync);
        View = Ui.Page(
            Ui.Title(Strings.PageDestinations),
            Ui.Note(Strings.DestinationsNote),
            _list,
            Ui.Horizontal(add, edit, remove));
        Fill();
    }

    public override string Title => Strings.PageDestinations;

    public override string Icon => "save";

    public override Control View { get; }

    public override void Save(TaskDefinition task) => task.Destinations = _destinations;

    private void Fill()
    {
        int index = _list.SelectedIndex;
        _list.ItemsSource = _destinations.Select(d => DestinationDialog.KindTitle(d.Kind) + " — " + Describe.Destination(d)).ToList();
        _list.SelectedIndex = Math.Min(index, _destinations.Count - 1);
    }

    private async Task AddAsync()
    {
        if (await DestinationDialog.Show(Context, new TaskDestination()) is { } destination)
        {
            _destinations.Add(destination);
            Fill();
        }
    }

    private async Task EditAsync()
    {
        int index = _list.SelectedIndex;
        if (index < 0 || index >= _destinations.Count)
        {
            return;
        }

        if (await DestinationDialog.Show(Context, _destinations[index]) is { } destination)
        {
            _destinations[index] = destination;
            Fill();
        }
    }

    private void Remove()
    {
        int index = _list.SelectedIndex;
        if (index >= 0 && index < _destinations.Count)
        {
            _destinations.RemoveAt(index);
            Fill();
        }
    }
}

internal sealed class SchedulePage : EditorPage
{
    private static readonly string[] Types = [ScheduleTypes.Daily, ScheduleTypes.Weekly, ScheduleTypes.Monthly, ScheduleTypes.Interval, ScheduleTypes.Manual];
    private readonly ComboBox _type;
    private readonly TextBox _times;
    private readonly List<CheckBox> _weekdays = [];
    private readonly TextBox _monthDays;
    private readonly NumericUpDown _interval;
    private readonly CheckBox _catchUp;
    private readonly Control _timesRow;
    private readonly Control _weekdaysRow;
    private readonly Control _monthRow;
    private readonly Control _intervalRow;

    public SchedulePage(EditorContext context)
        : base(context)
    {
        var schedule = context.Task.Schedule;
        _type = Ui.Choice(
        [
            (ScheduleTypes.Daily, Strings.ScheduleDaily), (ScheduleTypes.Weekly, Strings.ScheduleWeekly), (ScheduleTypes.Monthly, Strings.ScheduleMonthly),
            (ScheduleTypes.Interval, Strings.ScheduleByInterval), (ScheduleTypes.Manual, Strings.ScheduleManual),
        ], schedule.Type);
        _times = Ui.TextBox(string.Join(", ", schedule.Times), "21:30", 220);
        var days = new WrapPanel();
        var culture = CultureInfo.CurrentCulture;
        for (int day = 1; day <= 7; day++)
        {
            var name = culture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)(day % 7));
            var box = Ui.Check(name, schedule.DaysOfWeek.Contains(day));
            box.Margin = new Thickness(0, 0, 12, 0);
            _weekdays.Add(box);
            days.Children.Add(box);
        }

        _monthDays = Ui.TextBox(string.Join(", ", schedule.DaysOfMonth.Select(d => d == -1 ? MacStrings.ScheduleLastDay : d.ToString(CultureInfo.InvariantCulture))), "1, 15", 220);
        _interval = Ui.Number(schedule.IntervalMinutes > 0 ? schedule.IntervalMinutes : 60, 1, 1440);
        _catchUp = Ui.Check(Strings.ScheduleCatchUp, schedule.CatchUp);
        _timesRow = Ui.Row(Strings.ScheduleTimes, _times, Strings.ScheduleTimesHelp);
        _weekdaysRow = Ui.Row(Strings.ScheduleWeekdays, days);
        _monthRow = Ui.Row(Strings.ScheduleMonthDays, _monthDays, MacStrings.ScheduleMonthDaysNote);
        _intervalRow = Ui.Row(Strings.ScheduleInterval, Ui.Horizontal(_interval, new TextBlock { Text = MacStrings.Minutes }));
        _type.SelectionChanged += (_, _) => ShowRows();
        View = Ui.Page(
            Ui.Title(Strings.PageSchedule),
            Ui.Row(Strings.ScheduleType, _type),
            _timesRow,
            _weekdaysRow,
            _monthRow,
            _intervalRow,
            Ui.Row(string.Empty, _catchUp, Strings.ScheduleCatchUpHelp),
            Ui.Note(Strings.ScheduleTimeZoneNote));
        ShowRows();
    }

    public override string Title => Strings.PageSchedule;

    public override string Icon => "schedule";

    public override Control View { get; }

    private string Type => Ui.Read(_type);

    public override string? Check()
    {
        if (Type is ScheduleTypes.Daily or ScheduleTypes.Weekly or ScheduleTypes.Monthly)
        {
            var times = Times();
            if (times.Count == 0)
            {
                return Strings.ErrorNoTimes;
            }

            if (times.FirstOrDefault(t => Core.Scheduling.ScheduleCalculator.ParseTime(t) is null) is { } bad)
            {
                return Strings.Format(Strings.ErrorBadTime, bad);
            }
        }

        if (Type == ScheduleTypes.Weekly && !_weekdays.Any(Ui.Read))
        {
            return Strings.ErrorNoWeekdays;
        }

        return Type == ScheduleTypes.Monthly && MonthDays() is not { Count: > 0 } ? Strings.ErrorNoMonthDays : null;
    }

    public override void Save(TaskDefinition task)
    {
        var schedule = task.Schedule;
        schedule.Type = Type;
        if (Type is ScheduleTypes.Daily or ScheduleTypes.Weekly or ScheduleTypes.Monthly)
        {
            schedule.Times = Times();
        }

        schedule.DaysOfWeek = Enumerable.Range(1, 7).Where(d => Ui.Read(_weekdays[d - 1])).ToList();
        if (Type == ScheduleTypes.Monthly)
        {
            schedule.DaysOfMonth = MonthDays() ?? [];
        }

        if (Type == ScheduleTypes.Interval)
        {
            schedule.IntervalMinutes = Ui.Read(_interval);
        }

        schedule.CatchUp = Ui.Read(_catchUp);
    }

    private List<string> Times() =>
        (_times.Text ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>1–31 and -1 (the last day); null when something is not a day.</summary>
    private List<int>? MonthDays()
    {
        var days = new List<int>();
        foreach (string part in (_monthDays.Text ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals(MacStrings.ScheduleLastDay, StringComparison.OrdinalIgnoreCase) || part == "-1")
            {
                days.Add(-1);
            }
            else if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int day) && day is >= 1 and <= 31)
            {
                days.Add(day);
            }
            else
            {
                return null;
            }
        }

        return days.Distinct().ToList();
    }

    private void ShowRows()
    {
        string type = Type;
        _timesRow.IsVisible = type is ScheduleTypes.Daily or ScheduleTypes.Weekly or ScheduleTypes.Monthly;
        _weekdaysRow.IsVisible = type is ScheduleTypes.Daily or ScheduleTypes.Weekly;
        _monthRow.IsVisible = type == ScheduleTypes.Monthly;
        _intervalRow.IsVisible = type == ScheduleTypes.Interval;
        _catchUp.IsEnabled = type != ScheduleTypes.Manual;
    }
}

internal sealed class ArchivePage : EditorPage
{
    private readonly RadioButton _none;
    private readonly RadioButton _zip;
    private readonly RadioButton _sevenZip;
    private readonly ComboBox _level;
    private readonly NumericUpDown _split;
    private readonly CheckBox _encrypt;
    private readonly CheckBox _encryptNames;
    private readonly TextBlock _passwordState = new() { Classes = { "small", "var" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly NumericUpDown _verifyRuns;
    private readonly NumericUpDown _verifyDays;
    private readonly TextBlock _mirrorNote = Ui.Note(Strings.ArchiveMirrorNote);
    private readonly StackPanel _options;
    private string? _passwordId;

    public ArchivePage(EditorContext context)
        : base(context)
    {
        var archive = context.Task.Archive;
        _passwordId = archive.PasswordCredentialId;
        _none = Ui.Radio("format", Strings.ArchiveNone, archive.Format == ArchiveFormats.None);
        _zip = Ui.Radio("format", Strings.ArchiveZip, archive.Format == ArchiveFormats.Zip, MacStrings.ArchiveZipHelp);
        _sevenZip = Ui.Radio("format", Strings.ArchiveSevenZip, archive.Format == ArchiveFormats.SevenZip, Strings.ArchiveSevenZipHelp);
        _level = Ui.Choice([("0", Strings.ArchiveLevel0), ("1", Strings.ArchiveLevel1), ("3", "3"), ("5", Strings.ArchiveLevel5), ("7", "7"), ("9", Strings.ArchiveLevel9)],
            archive.Level.ToString(CultureInfo.InvariantCulture));
        _split = Ui.Number(archive.SplitMb, 0, 1_000_000);
        _encrypt = Ui.Check(Strings.ArchiveEncrypt, archive.Encrypt);
        _encryptNames = Ui.Check(Strings.ArchiveEncryptNames, archive.EncryptNames);
        var setPassword = Ui.Text(Strings.ArchiveSetPassword, () => { });
        setPassword.OnClick(SetPasswordAsync);
        _verifyRuns = Ui.Number(context.Task.Verify.EveryRuns, 0, 1000);
        _verifyDays = Ui.Number(context.Task.Verify.StoredEveryDays, 0, 3650);
        foreach (var radio in new[] { _none, _zip, _sevenZip })
        {
            radio.IsCheckedChanged += (_, _) => ShowRows();
        }

        _encrypt.IsCheckedChanged += (_, _) => ShowRows();
        context.TypeChanged += (_, _) => ShowRows();
        _options = Ui.Stack(
            Ui.Row(Strings.ArchiveLevel, _level, Strings.ArchiveLevelHelp),
            Ui.Row(Strings.ArchiveSplit, Ui.Horizontal(_split, new TextBlock { Text = Strings.ArchiveMb }), Strings.ArchiveSplitHelp),
            Ui.Heading(Strings.ArchiveEncryption),
            _encrypt,
            Ui.Horizontal(setPassword, _passwordState),
            _encryptNames,
            Ui.Note(Strings.ArchivePasswordWarning));
        View = Ui.Page(
            Ui.Title(Strings.PageArchive),
            _mirrorNote,
            Ui.Heading(Strings.ArchiveFormat),
            _none,
            _zip,
            _sevenZip,
            _options,
            Ui.Heading(Strings.ArchiveVerify),
            Ui.Row(Strings.VerifyEveryRuns, _verifyRuns, Strings.VerifyEveryRunsHelp, 300),
            Ui.Row(Strings.VerifyStoredDays, _verifyDays, Strings.VerifyStoredDaysHelp, 300));
        ShowRows();
    }

    public override string Title => Strings.PageArchive;

    public override string Icon => "folder_zip";

    public override Control View { get; }

    private string Format => _zip.IsChecked == true ? ArchiveFormats.Zip : _sevenZip.IsChecked == true ? ArchiveFormats.SevenZip : ArchiveFormats.None;

    public override string? Check() =>
        Format != ArchiveFormats.None && Context.BackupType != BackupTypes.Mirror && Ui.Read(_encrypt) && _passwordId is null
            ? Strings.ArchivePasswordRequired : null;

    public override void Save(TaskDefinition task)
    {
        var archive = task.Archive;
        archive.Format = Context.BackupType == BackupTypes.Mirror ? ArchiveFormats.None : Format;
        archive.Level = int.Parse(Ui.Read(_level), CultureInfo.InvariantCulture);
        archive.SplitMb = Format == ArchiveFormats.SevenZip ? Ui.Read(_split) : 0;
        archive.Encrypt = archive.Format != ArchiveFormats.None && Ui.Read(_encrypt);
        archive.EncryptNames = archive.Format == ArchiveFormats.SevenZip && archive.Encrypt && Ui.Read(_encryptNames);
        archive.PasswordCredentialId = archive.Encrypt ? _passwordId : archive.PasswordCredentialId;
        task.Verify.EveryRuns = Ui.Read(_verifyRuns);
        task.Verify.StoredEveryDays = Ui.Read(_verifyDays);
    }

    private void ShowRows()
    {
        bool mirror = Context.BackupType == BackupTypes.Mirror;
        _mirrorNote.IsVisible = mirror;
        _none.IsEnabled = _zip.IsEnabled = _sevenZip.IsEnabled = !mirror;
        _options.IsVisible = !mirror && Format != ArchiveFormats.None;
        _split.IsEnabled = Format == ArchiveFormats.SevenZip;
        _encryptNames.IsEnabled = Format == ArchiveFormats.SevenZip && Ui.Read(_encrypt);
        _passwordState.Text = _passwordId is null ? Strings.ArchivePasswordNotSet : Strings.ArchivePasswordSet;
    }

    private async Task SetPasswordAsync()
    {
        if (await Dialogs.NewPassword(Context.Window, Strings.ArchivePasswordTitle, Strings.ArchivePasswordChangeNote) is not { } password)
        {
            return;
        }

        var credential = new Credential
        {
            Id = _passwordId ?? TaskDefinition.NewId(),
            Kind = CredentialKinds.ArchivePassword,
            User = Strings.Format(Strings.ArchivePasswordName, string.IsNullOrWhiteSpace(Context.Task.Name) ? Strings.ArchivePasswordNewTask : Context.Task.Name),
        };
        try
        {
            var saved = await Context.Monitor.RequestAsync(PipeProtocol.Methods.CredentialSave, new CredentialInput { Credential = credential, Password = password },
                IpcJsonContext.Default.CredentialInput, IpcJsonContext.Default.CredentialSaveResult);
            _passwordId = saved.Credential.Id;
            ShowRows();
        }
        catch (ServiceUnavailableException ex)
        {
            await Dialogs.Show(Context.Window, ex.Message);
        }
    }
}

internal sealed class RetentionPage : EditorPage
{
    private readonly RadioButton _none;
    private readonly RadioButton _count;
    private readonly RadioButton _age;
    private readonly RadioButton _gfs;
    private readonly NumericUpDown _keepFull;
    private readonly NumericUpDown _maxAge;
    private readonly NumericUpDown _daily;
    private readonly NumericUpDown _weekly;
    private readonly NumericUpDown _monthly;
    private readonly CheckBox _separate;
    private readonly TextBox _template;
    private readonly CheckBox _keepTop;
    private readonly TextBlock _mirrorNote = Ui.Note(Strings.RetentionMirrorNote);
    private readonly StackPanel _rules;

    public RetentionPage(EditorContext context)
        : base(context)
    {
        var retention = context.Task.Retention;
        var layout = context.Task.Layout;
        _none = Ui.Radio("keep", Strings.RetentionNone, retention.Mode == RetentionModes.None);
        _count = Ui.Radio("keep", Strings.RetentionCount, retention.Mode == RetentionModes.Count);
        _age = Ui.Radio("keep", Strings.RetentionAge, retention.Mode == RetentionModes.Age);
        _gfs = Ui.Radio("keep", Strings.RetentionGfs, retention.Mode == RetentionModes.Gfs);
        _keepFull = Ui.Number(retention.KeepFull, 1, 1000);
        _maxAge = Ui.Number(retention.MaxAgeDays > 0 ? retention.MaxAgeDays : 90, 1, 36500);
        _daily = Ui.Number(retention.Gfs.Daily, 0, 1000, 100);
        _weekly = Ui.Number(retention.Gfs.Weekly, 0, 1000, 100);
        _monthly = Ui.Number(retention.Gfs.Monthly, 0, 1000, 100);
        _separate = Ui.Check(Strings.SeparateFolders, layout.SeparateFolders);
        _template = Ui.TextBox(layout.NameTemplate);
        _keepTop = Ui.Check(Strings.KeepTopDir, layout.KeepTopDir);
        context.TypeChanged += (_, _) => ShowRows();
        _rules = Ui.Stack(
            _none,
            Ui.Horizontal(_count, _keepFull, new TextBlock { Text = Strings.RetentionCountSuffix }),
            Ui.Note(Strings.RetentionCountHint),
            Ui.Horizontal(_age, _maxAge, new TextBlock { Text = Strings.RetentionAgeSuffix }),
            Ui.Note(Strings.RetentionAgeHint),
            _gfs,
            Ui.Horizontal(new TextBlock { Text = Strings.GfsDaily }, _daily, new TextBlock { Text = Strings.GfsWeekly }, _weekly,
                new TextBlock { Text = Strings.GfsMonthly }, _monthly),
            Ui.Note(Strings.RetentionGfsHint),
            Ui.Heading(Strings.RetentionRules),
            Ui.Note(Strings.RetentionRulesText),
            Ui.Heading(Strings.EditorLayout),
            _separate,
            Ui.Row(Strings.NameTemplate, _template, Strings.NameTemplateHelp, 150),
            _keepTop);
        View = Ui.Page(Ui.Title(Strings.PageRetention), _mirrorNote, _rules);
        ShowRows();
    }

    public override string Title => Strings.PageRetention;

    public override string Icon => "history";

    public override Control View { get; }

    public override string? Check() =>
        _gfs.IsChecked == true && Ui.Read(_daily) + Ui.Read(_weekly) + Ui.Read(_monthly) == 0 ? Strings.RetentionGfsEmpty
        : Ui.Read(_separate) && string.IsNullOrWhiteSpace(_template.Text) ? Strings.ErrorTemplateRequired
        : null;

    public override void Save(TaskDefinition task)
    {
        var retention = task.Retention;
        retention.Mode = _none.IsChecked == true ? RetentionModes.None : _age.IsChecked == true ? RetentionModes.Age
            : _gfs.IsChecked == true ? RetentionModes.Gfs : RetentionModes.Count;
        retention.KeepFull = Ui.Read(_keepFull);
        retention.MaxAgeDays = Ui.Read(_maxAge);
        retention.Gfs.Daily = Ui.Read(_daily);
        retention.Gfs.Weekly = Ui.Read(_weekly);
        retention.Gfs.Monthly = Ui.Read(_monthly);
        task.Layout.SeparateFolders = Ui.Read(_separate);
        task.Layout.NameTemplate = (_template.Text ?? string.Empty).Trim();
        task.Layout.KeepTopDir = Ui.Read(_keepTop);
    }

    private void ShowRows()
    {
        bool mirror = Context.BackupType == BackupTypes.Mirror;
        _mirrorNote.IsVisible = mirror;
        _rules.IsVisible = !mirror;
    }
}

internal sealed class EventsPage : EditorPage
{
    private readonly List<TaskEvent> _pre;
    private readonly List<TaskEvent> _post;
    private readonly ListBox _preList = Ui.List(120);
    private readonly ListBox _postList = Ui.List(120);
    private readonly CheckBox _abortOnPre;
    private readonly ComboBox _after;

    public EventsPage(EditorContext context)
        : base(context)
    {
        var events = context.Task.Events;
        _pre = events.Pre.ToList();
        _post = events.Post.ToList();
        _abortOnPre = Ui.Check(Strings.EventsAbortOnPre, events.AbortOnPreFailure);
        _after = Ui.Choice(
        [
            (AfterActions.None, Strings.AfterNone), (AfterActions.Shutdown, Strings.AfterShutdown), (AfterActions.Restart, Strings.AfterRestart),
            (AfterActions.Sleep, Strings.AfterSleep),
        ], events.After == AfterActions.Hibernate ? AfterActions.Sleep : events.After);
        View = Ui.Page(
            Ui.Title(Strings.PageEvents),
            Ui.Note(MacStrings.EventsNote),
            Ui.Heading(Strings.EventsPreHeading),
            _preList,
            Buttons(_pre, _preList),
            _abortOnPre,
            Ui.Heading(Strings.EventsPostHeading),
            _postList,
            Buttons(_post, _postList),
            Ui.Note(Strings.EventsPostNote),
            Ui.Heading(Strings.AfterHeading),
            _after,
            Ui.Note(MacStrings.AfterNote));
        Fill(_pre, _preList);
        Fill(_post, _postList);
    }

    public override string Title => Strings.PageEvents;

    public override string Icon => "bolt";

    public override Control View { get; }

    public override void Save(TaskDefinition task)
    {
        task.Events.Pre = _pre;
        task.Events.Post = _post;
        task.Events.AbortOnPreFailure = Ui.Read(_abortOnPre);
        task.Events.After = Ui.Read(_after);
    }

    private static string Describe(TaskEvent e) => e.Kind switch
    {
        TaskEventKinds.ServiceStop => Strings.Format(Strings.EventShowStop, e.Service),
        TaskEventKinds.ServiceStart => Strings.Format(Strings.EventShowStart, e.Service),
        _ => $"{e.Command} {e.Args}".Trim() + " · " + Strings.Format(Strings.EventTimeoutShown, Math.Max(1, e.TimeoutS / 60)),
    };

    private static void Fill(List<TaskEvent> events, ListBox list) => list.ItemsSource = events.Select(Describe).ToList();

    private StackPanel Buttons(List<TaskEvent> events, ListBox list)
    {
        var add = Ui.Outlined(Strings.ButtonAdd, () => { });
        add.OnClick(async () =>
        {
            if (await EditAsync(new TaskEvent(), Strings.EventAddTitle) is { } added)
            {
                events.Add(added);
                Fill(events, list);
            }
        });
        var edit = Ui.Text(Strings.ButtonEdit, () => { });
        edit.OnClick(async () =>
        {
            int index = list.SelectedIndex;
            if (index >= 0 && index < events.Count && await EditAsync(events[index], Strings.EventEditTitle) is { } changed)
            {
                events[index] = changed;
                Fill(events, list);
            }
        });
        var remove = Ui.Text(Strings.ButtonRemove, () =>
        {
            int index = list.SelectedIndex;
            if (index >= 0 && index < events.Count)
            {
                events.RemoveAt(index);
                Fill(events, list);
            }
        });
        var up = Ui.Text(Strings.ButtonUp, () =>
        {
            int index = list.SelectedIndex;
            if (index > 0 && index < events.Count)
            {
                (events[index - 1], events[index]) = (events[index], events[index - 1]);
                Fill(events, list);
                list.SelectedIndex = index - 1;
            }
        });
        return Ui.Horizontal(add, edit, remove, up);
    }

    private async Task<TaskEvent?> EditAsync(TaskEvent original, string title)
    {
        var dialog = new DialogWindow(title, 620);
        var kind = Ui.Choice([(TaskEventKinds.Run, Strings.EventKindRun), (TaskEventKinds.ServiceStop, MacStrings.EventKindStop),
            (TaskEventKinds.ServiceStart, MacStrings.EventKindStart)], original.Kind, 360);
        var command = Ui.TextBox(original.Command, "/Users/Shared/scripts/before.sh");
        var browse = Ui.Text(Strings.ButtonBrowse, () => { });
        browse.OnClick(async () =>
        {
            if (await Context.PickFileAsync(Strings.EventProgram) is { } path)
            {
                command.Text = path;
            }
        });
        var args = Ui.TextBox(original.Args);
        var timeout = Ui.Number(Math.Max(1, original.TimeoutS / 60), 1, 1440);
        var service = Ui.TextBox(original.Service, "com.example.database");
        var error = Ui.Error();
        var runRows = Ui.Stack(
            Ui.Row(Strings.EventProgram, Ui.Horizontal(command, browse), MacStrings.EventProgramHelp, 170),
            Ui.Row(Strings.EventArguments, args, labelWidth: 170),
            Ui.Row(Strings.EventTimeout, timeout, Strings.EventTimeoutHelp, 170));
        var serviceRow = Ui.Row(Strings.EventService, service, MacStrings.EventServiceHelp, 170);
        void Show()
        {
            bool run = Ui.Read(kind) == TaskEventKinds.Run;
            runRows.IsVisible = run;
            serviceRow.IsVisible = !run;
        }

        kind.SelectionChanged += (_, _) => Show();
        dialog.Body.Children.Add(kind);
        dialog.Body.Children.Add(runRows);
        dialog.Body.Children.Add(serviceRow);
        dialog.Body.Children.Add(error);
        Show();
        dialog.AddButton(Strings.ButtonCancel, () => dialog.Close(null));
        dialog.AddButton(Strings.ButtonOk, () =>
        {
            string chosen = Ui.Read(kind);
            if (chosen == TaskEventKinds.Run && string.IsNullOrWhiteSpace(command.Text))
            {
                Ui.Show(error, Strings.ErrorEventProgramRequired);
                return;
            }

            if (chosen != TaskEventKinds.Run && string.IsNullOrWhiteSpace(service.Text))
            {
                Ui.Show(error, Strings.ErrorEventServiceRequired);
                return;
            }

            dialog.Close(new TaskEvent
            {
                Kind = chosen,
                Command = chosen == TaskEventKinds.Run ? command.Text!.Trim() : null,
                Args = chosen == TaskEventKinds.Run ? (args.Text ?? string.Empty).Trim() : string.Empty,
                TimeoutS = Ui.Read(timeout) * 60,
                Service = chosen == TaskEventKinds.Run ? null : service.Text!.Trim(),
                ExtensionData = original.ExtensionData,
            });
        }, main: true);
        return await dialog.ShowDialog<TaskEvent?>(Context.Window);
    }
}

internal sealed class AdvancedPage : EditorPage
{
    private readonly CheckBox _snapshot;
    private readonly ComboBox _onFailure;
    private readonly NumericUpDown _retries;
    private readonly CheckBox _permissions;
    private readonly ComboBox _logLevel;

    public AdvancedPage(EditorContext context)
        : base(context)
    {
        var task = context.Task;
        _snapshot = Ui.Check(MacStrings.SnapshotEnabled, task.Vss.Enabled);
        _onFailure = Ui.Choice([(VssFailureActions.Abort, Strings.VssAbort), (VssFailureActions.Continue, Strings.VssContinue)], task.Vss.OnFailure, 360);
        _retries = Ui.Number(task.Copy.ReadRetries, 0, 5);
        _permissions = Ui.Check(MacStrings.KeepPermissions, task.Copy.PreserveAcl);
        _logLevel = Ui.Choice([(LogLevels.Inherit, Strings.LogLevelInherit), (LogLevels.Summary, Strings.LogLevelSummary), (LogLevels.Files, Strings.LogLevelFiles)],
            task.LogLevel, 320);
        View = Ui.Page(
            Ui.Title(Strings.PageAdvanced),
            Ui.Heading(Strings.EditorVss),
            _snapshot,
            Ui.Note(MacStrings.SnapshotHelp),
            Ui.Row(MacStrings.SnapshotOnFailure, _onFailure),
            Ui.Heading(Strings.EditorReading),
            Ui.Row(Strings.ReadRetries, _retries, Strings.ReadRetriesHelp, 260),
            _permissions,
            Ui.Note(MacStrings.KeepPermissionsHelp),
            Ui.Heading(Strings.EditorLogAndQueue),
            Ui.Row(Strings.LogLevel, _logLevel));
    }

    public override string Title => Strings.PageAdvanced;

    public override string Icon => "tune";

    public override Control View { get; }

    public override void Save(TaskDefinition task)
    {
        task.Vss.Enabled = Ui.Read(_snapshot);
        task.Vss.OnFailure = Ui.Read(_onFailure);
        task.Copy.ReadRetries = Ui.Read(_retries);
        task.Copy.PreserveAcl = Ui.Read(_permissions);
        task.LogLevel = Ui.Read(_logLevel);
    }
}
