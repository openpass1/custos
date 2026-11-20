using System.Collections.ObjectModel;
using System.Globalization;
using Custos.Core.Model;
using Custos.Core.Scheduling;
using Custos.Core.Text;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>
/// One page of the task editor. The pages are the ones of Custos.exe (Custos.Gui/EditorPages.cs) with the same rules,
/// made of form pieces: each page loads the task into its pieces, writes them back into a copy on «Сохранить», and
/// says what is wrong in words.
/// </summary>
internal abstract class EditorPage : Observable
{
    protected EditorPage(EditorContext context, string title, string icon, string? subtitle = null)
    {
        Context = context;
        Title = title;
        Icon = icon;
        Subtitle = subtitle;
    }

    public string Title { get; }

    public string Icon { get; }

    public string? Subtitle { get; }

    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

    public ObservableCollection<FormItem> Items { get; } = [];

    protected EditorContext Context { get; }

    public abstract void LoadFrom(TaskDefinition task);

    /// <summary>Writes the page into the task; what is wrong, in words, or null.</summary>
    public abstract string? SaveTo(TaskDefinition task);

    /// <summary>Called each time the page is shown.</summary>
    public virtual void Shown()
    {
    }

    /// <summary>A caption without the colon of the old forms.</summary>
    protected static string L(string label) => label.TrimEnd(':', ' ');

    protected static string Lines(string text) => text.Replace("\\n", Environment.NewLine, StringComparison.Ordinal);

    protected T Add<T>(T item)
        where T : FormItem
    {
        Items.Add(item);
        return item;
    }

    protected HeadingItem Heading(string text, string? help = null) => Add(new HeadingItem(text, help is null ? null : Lines(help)));

    protected NoteItem Note(string text, Tone tone = Tone.Plain, double indent = 0, bool visible = true) =>
        Add(new NoteItem(Lines(text), tone) { Indent = indent, IsVisible = visible });

    /// <summary>The first number field that holds no number in its range, with the reason.</summary>
    protected static string? Invalid(params NumberItem[] numbers)
    {
        foreach (var number in numbers)
        {
            if (number.IsEnabled && !number.IsValid)
            {
                string range = number.Range();
                number.Error = range;
                return string.IsNullOrEmpty(number.Label) ? range : $"«{number.Label}»: {range}";
            }
        }

        return null;
    }
}

/// <summary>«Основное»: name, on/off, backup type (for a mirror exact or safe), forced full copies, comment.</summary>
internal sealed class BasicPage : EditorPage
{
    private readonly TextItem _name = new(L(Strings.EditorName), width: 520) { MaxLength = 200 };
    private readonly SwitchItem _enabled = new(Strings.EditorEnabled);
    private readonly RadioGroup _types = new();
    private readonly RadioItem _full;
    private readonly RadioItem _incremental;
    private readonly RadioItem _differential;
    private readonly RadioItem _mirror;
    private readonly NoteItem _mirrorLocked;
    private readonly RadioGroup _mirrorModes = new();
    private readonly RadioItem _mirrorExact;
    private readonly RadioItem _mirrorSafe;
    private readonly NumberItem _safeDays = new(null, 30, 1, 3650, 80) { Compact = true };
    private readonly RowItem _mirrorSafeRow;
    private readonly NoteItem _mirrorNote;
    private readonly HeadingItem _forceHeading;
    private readonly CheckItem _forceEvery = new(Strings.ForceEvery) { Inline = true };
    private readonly NumberItem _forceEveryRuns = new(null, 7, 2, 1000, 80) { Compact = true };
    private readonly CheckItem _forceWeekday = new(Strings.ForceWeekday) { Inline = true };
    private readonly ChoiceItem _weekday;
    private readonly CheckItem _forceMonthDay = new(Strings.ForceMonthDay) { Inline = true };
    private readonly ChoiceItem _monthDay;
    private readonly RowItem[] _forceRows;
    private readonly NoteItem _forceNote;
    private readonly TextItem _comment = new(null, width: 520) { Multiline = true, Lines = 3 };

    public BasicPage(EditorContext context)
        : base(context, Strings.PageBasic, "description")
    {
        var dayNames = CultureInfo.CurrentCulture.DateTimeFormat.DayNames;
        _weekday = new ChoiceItem(null, [.. Enumerable.Range(1, 7).Select(d => dayNames[d % 7])], 6, 200) { Compact = true };

        // 1–30 and the last day: a month without the chosen day takes its last day (ScheduleCalculator.IsMonthDay).
        _monthDay = new ChoiceItem(null, [.. Enumerable.Range(1, MonthDays.LastNumber).Select(d => d.ToString(CultureInfo.CurrentCulture)), Strings.LastDayOfMonth], 0, 200)
        {
            Compact = true,
        };

        Add(_name);
        Add(_enabled);
        Heading(Strings.EditorBackupType);
        _full = Add(new RadioItem(_types, Strings.TypeFullTitle, Strings.TypeFullHint));
        _incremental = Add(new RadioItem(_types, Strings.TypeIncrementalTitle, Strings.TypeIncrementalHint));
        _differential = Add(new RadioItem(_types, Strings.TypeDifferentialTitle, Strings.TypeDifferentialHint));
        _mirror = Add(new RadioItem(_types, Strings.TypeMirrorTitle, Strings.TypeMirrorHint));
        _mirrorLocked = Note(string.Empty, Tone.Caution, 52, visible: false);
        _mirrorExact = Add(new RadioItem(_mirrorModes, Strings.MirrorExact) { Indent = 30 });
        _mirrorSafe = new RadioItem(_mirrorModes, Strings.MirrorSafe, inline: true);
        _mirrorSafeRow = Add(new RowItem(_mirrorSafe, _safeDays, new LabelItem(Strings.MirrorSafeDays)) { Indent = 30 });
        _mirrorNote = Note(Strings.MirrorNote, indent: 30);

        _forceHeading = Heading(Strings.EditorForceFull);
        _forceRows =
        [
            Add(new RowItem(_forceEvery, _forceEveryRuns, new LabelItem(Strings.ForceEveryRunsSuffix))),
            Add(new RowItem(_forceWeekday, _weekday)),
            Add(new RowItem(_forceMonthDay, _monthDay)),
        ];
        _forceNote = Note(Strings.ForceFullNote);

        Heading(Strings.EditorComment);
        Add(_comment);

        foreach (var type in _types.Items)
        {
            type.OnChange = () =>
            {
                if (type.On)
                {
                    UpdateType();
                }
            };
        }

        _mirrorSafe.OnChange = () => _safeDays.IsEnabled = _mirrorSafe.On && _mirror.On;
        _name.OnChange = () => Context.SetShape(taskName: _name.Value);
        Context.ShapeChanged += (_, _) => ShowMirrorLocked();
        Context.RemoteChanged += (_, _) => ShowMirrorLocked();
        _forceEvery.OnChange = () =>
        {
            _forceEveryRuns.IsEnabled = _forceEvery.On;
            ShowForcedFull();
        };
        _forceWeekday.OnChange = () =>
        {
            _weekday.IsEnabled = _forceWeekday.On;
            ShowForcedFull();
        };
        _forceMonthDay.OnChange = () =>
        {
            _monthDay.IsEnabled = _forceMonthDay.On;
            ShowForcedFull();
        };
    }

    public override void LoadFrom(TaskDefinition task)
    {
        _name.Load(task.Name);
        _enabled.On = task.Enabled;
        (task.MirrorMode == MirrorModes.Safe ? _mirrorSafe : _mirrorExact).Load();
        _safeDays.Value = task.Retention.SafeMirrorDays > 0 ? task.Retention.SafeMirrorDays : 30;
        (task.BackupType switch
        {
            BackupTypes.Full => _full,
            BackupTypes.Differential => _differential,
            BackupTypes.Mirror => _mirror,
            _ => _incremental,
        }).Load();

        var force = task.ForceFull;
        _forceEvery.Load(force.EveryRuns > 0);
        _forceEveryRuns.Value = force.EveryRuns > 0 ? force.EveryRuns : 7;
        _forceWeekday.Load(force.Weekday is >= 1 and <= 7);
        _weekday.Load((force.Weekday is >= 1 and <= 7 ? force.Weekday.Value : 7) - 1);
        _forceMonthDay.Load(force.MonthDay is (>= 1 and <= 31) or (-1));
        _monthDay.Load(force.MonthDay switch
        {
            -1 or 31 => MonthDays.LastNumber,
            >= 1 and <= MonthDays.LastNumber => force.MonthDay.Value - 1,
            _ => 0,
        });
        _comment.Load(task.Comment);
        _forceEveryRuns.IsEnabled = _forceEvery.On;
        _weekday.IsEnabled = _forceWeekday.On;
        _monthDay.IsEnabled = _forceMonthDay.On;
        Context.SetShape(taskName: task.Name.Trim());
        ShowForcedFull();
        UpdateType();
    }

    public override string? SaveTo(TaskDefinition task)
    {
        string name = _name.Value;
        if (name.Length == 0)
        {
            _name.Error = Strings.ErrorNameRequired;
            return Strings.ErrorNameRequired;
        }

        // A mirror replaces files one by one: not where they are protected, not on a cloud drive, not from a Linux
        // server (sections 6.3, remote pull 4.1).
        if (_mirror.On && MirrorProblem() is { } problem)
        {
            return problem;
        }

        if (Invalid(_safeDays, _forceEveryRuns) is { } wrong)
        {
            return wrong;
        }

        task.Name = name;
        task.Enabled = _enabled.On;
        task.BackupType = Type;
        task.MirrorMode = task.BackupType == BackupTypes.Mirror ? _mirrorSafe.On ? MirrorModes.Safe : MirrorModes.Exact : null;
        if (task.BackupType == BackupTypes.Mirror && _mirrorSafe.On)
        {
            task.Retention.SafeMirrorDays = _safeDays.Value;
        }

        task.ForceFull.EveryRuns = _forceEvery.On ? _forceEveryRuns.Value : 0;
        task.ForceFull.Weekday = _forceWeekday.On ? _weekday.Selected + 1 : null;
        task.ForceFull.MonthDay = _forceMonthDay.On ? (_monthDay.Selected == MonthDays.LastNumber ? -1 : _monthDay.Selected + 1) : null;
        task.Comment = _comment.Text.Trim();
        return null;
    }

    private string Type => _full.On ? BackupTypes.Full : _differential.On ? BackupTypes.Differential : _mirror.On ? BackupTypes.Mirror : BackupTypes.Incremental;

    /// <summary>The retention page warns when nothing forces a full backup.</summary>
    private void ShowForcedFull() => Context.SetShape(forcedFull: _forceEvery.On || _forceWeekday.On || _forceMonthDay.On);

    private void UpdateType()
    {
        bool mirror = _mirror.On;
        bool forced = !_full.On && !mirror;
        _forceHeading.IsVisible = _forceNote.IsVisible = !mirror;
        foreach (var row in _forceRows)
        {
            row.IsVisible = !mirror;
            row.IsEnabled = forced;
        }

        _mirrorExact.IsVisible = _mirrorSafeRow.IsVisible = _mirrorNote.IsVisible = mirror;
        _safeDays.IsEnabled = mirror && _mirrorSafe.On;
        Context.SetShape(taskName: _name.Value, backupType: Type);
        ShowMirrorLocked();
    }

    /// <summary>Why the task cannot be a mirror, as saving says it; null if it can.</summary>
    private string? MirrorProblem() =>
        Context.HasRemoteSource ? Custos.Core.Messages.RemoteNoMirror
        : Context.Protecting is { } protecting ? Custos.Core.Backup.BackupEngine.ProtectionNeedsSets(protecting, TaskDescriber.Destination(protecting))
        : Context.CloudDrive is { } cloud ? Custos.Core.Messages.Format(Custos.Core.Messages.CloudNoMirror, TaskDescriber.Destination(cloud))
        : null;

    /// <summary>Where a mirror cannot be made, the choice is off with the reason, unless the task is a mirror already;
    /// then it stays on, so that it can be seen, and saving says what to change.</summary>
    private void ShowMirrorLocked()
    {
        string? reason = Context.HasRemoteSource ? Strings.MirrorRemoteNote
            : Context.Protecting is { } protecting ? Strings.Format(Strings.MirrorProtectedNote, TaskDescriber.Destination(protecting))
            : Context.CloudDrive is { } cloud ? Strings.Format(Strings.MirrorCloudNote, TaskDescriber.Destination(cloud))
            : null;
        _mirror.IsEnabled = reason is null || _mirror.On;
        _mirrorLocked.Show(reason);
    }
}

/// <summary>The days of a month a schedule offers: 1–30 and «the last day» (-1); a 31 means the last day too.</summary>
internal static class MonthDays
{
    public const int LastNumber = 30;
}

/// <summary>
/// «Источники»: folders, files and network folders with their accounts, or one Linux server, and under them the
/// filters of the task (section 6.2).
/// </summary>
internal sealed class SourcesPage : EditorPage
{
    private readonly CardsItem _cards = new(Strings.ErrorNoSources);
    private readonly ButtonItem _add;
    private readonly ButtonItem _addLinux;
    private readonly NoteItem _remoteNote;
    private readonly SwitchItem _keepTopDir = new(Strings.KeepTopDir, Lines(Strings.KeepTopDirHelp));
    private readonly TextItem _exclude = new(L(Strings.FilterExclude), width: 500) { Hint = Strings.FilterExcludeHelp };
    private readonly TextItem _excludeDirs;
    private readonly CheckItem _skipHidden = new(Strings.SkipHidden);
    private readonly CheckItem _skipSystem = new(Strings.SkipSystem);
    private readonly CheckItem _skipTemp = new(Strings.SkipTemp, description: Strings.SkipTempHelp);
    private readonly TextItem _include = new(L(Strings.FilterInclude), width: 500) { Hint = Strings.FilterIncludeHelp };
    private readonly FormItem[] _fileOptions;
    private readonly List<TaskSource> _sources = [];

    public SourcesPage(EditorContext context)
        : base(context, Strings.PageSources, "folder", Strings.SourcesNote)
    {
        _excludeDirs = new TextItem(L(Strings.FilterExcludeDirs), width: 500)
        {
            Multiline = true,
            Lines = 3,
            Hint = Strings.FilterExcludeDirsHelp,
            ActionIcon = "folder_open",
            ActionTip = Strings.FilterExcludeDirsBrowse,
            Action = BrowseExcludedFolderAsync,
        };
        _add = ButtonItem.Of("add", Strings.SourceAddTitle, () => Safe.Run(AddSourceAsync));
        _addLinux = ButtonItem.Of("dns", Strings.SourceAddLinux, () => Safe.Run(AddLinuxServerAsync));
        Add(_cards);
        Add(new RowItem(_add, _addLinux));
        _remoteNote = Note(Strings.SourcesRemoteNote, visible: false);
        Add(_keepTopDir);
        Heading(Strings.FiltersHeading);
        Note(Strings.FiltersNote);
        Add(_exclude);
        Add(_excludeDirs);
        Add(_skipHidden);
        Add(_skipSystem);
        Add(_skipTemp);
        Add(_include);
        _fileOptions = [_keepTopDir, _exclude, _excludeDirs, _skipHidden, _skipSystem, _skipTemp, _include];
        Context.CredentialsChanged += (_, _) => Fill();
    }

    public override void LoadFrom(TaskDefinition task)
    {
        _sources.Clear();
        _sources.AddRange(task.Sources);
        _keepTopDir.On = task.Layout.KeepTopDir;
        var filters = task.Filters;
        _exclude.Load(string.Join("; ", filters.Exclude));
        _excludeDirs.Load(string.Join(Environment.NewLine, filters.ExcludeDirs));
        _skipHidden.Load(filters.SkipHidden);
        _skipSystem.Load(filters.SkipSystem);
        _skipTemp.Load(filters.SkipTemp);
        _include.Load(string.Join("; ", filters.Include));
        Fill();
    }

    public override string? SaveTo(TaskDefinition task)
    {
        // A task may dump only databases (section 6.2).
        if (task.Enabled && _sources.Count == 0 && Context.DbServerCount == 0)
        {
            return Strings.ErrorNoSources;
        }

        task.Sources = [.. _sources];
        task.Layout.KeepTopDir = _keepTopDir.On;
        var filters = task.Filters;
        filters.Exclude = Masks(_exclude.Text);
        filters.ExcludeDirs = Masks(_excludeDirs.Text);
        filters.SkipHidden = _skipHidden.On;
        filters.SkipSystem = _skipSystem.On;
        filters.SkipTemp = _skipTemp.On;
        filters.Include = Masks(_include.Text);
        return null;
    }

    /// <summary>Masks separated by semicolons or line breaks, without repeats.</summary>
    internal static List<string> Masks(string text) =>
        [.. text.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Adds a line to «Пропускать папки» unless it is there already.</summary>
    internal void AddExcludedFolder(string path)
    {
        var lines = Masks(_excludeDirs.Text);
        if (!lines.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add(path);
            _excludeDirs.Text = string.Join(Environment.NewLine, lines);
        }
    }

    private void Fill()
    {
        _cards.Fill(_sources.Select((source, i) => Card(source, i)));
        UpdateButtons();
    }

    private CardItem Card(TaskSource source, int index)
    {
        Action edit = () => Safe.Run(() => EditSourceAsync(index));
        Action delete = () =>
        {
            _sources.RemoveAt(index);
            Fill();
        };
        if (source.Kind == SourceKinds.Ssh)
        {
            string key = Context.FindCredential(source.CredentialId)?.DisplayName ?? Strings.AccountUnknown;
            return new CardItem("dns", DesktopStrings.LinuxServer + " " + source.Host, source.Path + " · " + key) { Edit = edit, Delete = delete };
        }

        bool network = source.Kind == SourceKinds.Unc || source.Path.StartsWith(@"\\", StringComparison.Ordinal);
        string subtitle = source.Kind == SourceKinds.File ? Strings.SourceFile
            : source.Recursive ? Strings.SourceRecursive
            : Strings.Format(Strings.CardNotRecursive, network ? Strings.SourceNetwork : Strings.SourceFolder);
        string? account = source.CredentialId is { } id ? Context.FindCredential(id)?.DisplayName ?? Strings.AccountUnknown
            : source.Kind == SourceKinds.Unc ? Strings.AccountService
            : null;
        if (account is not null)
        {
            subtitle += " · " + account;
        }

        return new CardItem(source.Kind == SourceKinds.File ? "description" : network ? "lan" : "folder", source.Path, subtitle)
        {
            Edit = edit,
            Delete = delete,
            Warning = account == Strings.AccountUnknown,
        };
    }

    private void UpdateButtons()
    {
        // A Linux server is the only source of its task (CUSTOS-remote-pull.md 4.1); the filters are custos-helper's.
        bool remote = _sources.Any(s => s.Kind == SourceKinds.Ssh);
        _add.IsEnabled = !remote;
        _addLinux.IsEnabled = _sources.Count == 0;
        _remoteNote.IsVisible = remote;
        foreach (var item in _fileOptions)
        {
            item.IsEnabled = !remote;
        }

        Context.HasRemoteSource = remote;
        Context.Reads = (_sources.Count > 0,
            _sources.Any(s => s.Kind is SourceKinds.Folder or SourceKinds.File && !s.Path.StartsWith(@"\\", StringComparison.Ordinal)));
    }

    private async Task AddLinuxServerAsync()
    {
        var dialog = new RemoteSourceDialog(Context, new TaskSource());
        if (await dialog.ShowAsync(Context.Owner))
        {
            _sources.Add(dialog.Source);
            Fill();
        }
    }

    private async Task AddSourceAsync()
    {
        var dialog = new SourceDialog(Context, new TaskSource());
        if (await dialog.ShowAsync(Context.Owner))
        {
            _sources.Add(dialog.Source);
            Fill();
        }
    }

    private async Task EditSourceAsync(int index)
    {
        if (index >= _sources.Count)
        {
            return;
        }

        if (_sources[index].Kind == SourceKinds.Ssh)
        {
            var remote = new RemoteSourceDialog(Context, _sources[index]);
            if (await remote.ShowAsync(Context.Owner))
            {
                _sources[index] = remote.Source;
                Fill();
            }

            return;
        }

        var dialog = new SourceDialog(Context, _sources[index]);
        if (await dialog.ShowAsync(Context.Owner))
        {
            _sources[index] = dialog.Source;
            Fill();
        }
    }

    /// <summary>Adds a folder picked like a source folder as a line of «Пропускать папки»; it starts in the first source.</summary>
    private async Task BrowseExcludedFolderAsync()
    {
        string? start = _sources.Where(s => s.Kind != SourceKinds.File).Select(s => s.Path).FirstOrDefault(Directory.Exists);
        if (await FormDialog.PickFolderAsync(Context.Owner, Strings.FilterExcludeDirsBrowse, start) is { } path)
        {
            AddExcludedFolder(path);
        }
    }
}

/// <summary>«Базы данных» (section 6.2): MySQL, MariaDB and PostgreSQL servers on this computer, each dumped every run.</summary>
internal sealed class DatabasesPage : EditorPage
{
    private readonly CardsItem _cards = new();
    private readonly ButtonItem _add;
    private readonly NoteItem _remoteNote;
    private readonly List<TaskDatabaseServer> _servers = [];

    public DatabasesPage(EditorContext context)
        : base(context, Strings.PageDatabases, "database", Strings.DatabasesNote)
    {
        _remoteNote = Note(Strings.DatabasesRemoteNote, Tone.Warning, visible: false);
        Add(_cards);
        _add = Add(ButtonItem.Of("add", Strings.DbServerAddTitle, () => Safe.Run(AddServerAsync)));
        Context.RemoteChanged += (_, _) => UpdateButtons();
    }

    public override void LoadFrom(TaskDefinition task)
    {
        _servers.Clear();
        _servers.AddRange(task.DbServers);
        Fill();
    }

    public override string? SaveTo(TaskDefinition task)
    {
        task.DbServers = [.. _servers];
        return null;
    }

    /// <summary>«все», «все, кроме: a, b», «только: c».</summary>
    internal static string DescribeDatabases(TaskDatabaseServer server) =>
        server.AllDatabases
            ? server.Exclude.Count == 0 ? Strings.DbAll : Strings.Format(Strings.DbAllExcept, string.Join(", ", server.Exclude))
            : Strings.Format(Strings.DbOnly, string.Join(", ", server.Include));

    /// <summary>«MySQL 127.0.0.1:3306».</summary>
    internal static string DescribeServer(TaskDatabaseServer server) =>
        string.Concat(DbEngines.Title(server.Engine), " ", server.Host, ":",
            (server.Port ?? DbEngines.DefaultPort(server.Engine)).ToString(CultureInfo.InvariantCulture));

    private void Fill()
    {
        _cards.Fill(_servers.Select((server, i) => new CardItem("database", DescribeServer(server),
            Strings.ColumnDatabases.TrimEnd(':') + ": " + DescribeDatabases(server) + Environment.NewLine + server.BinDir)
        {
            Edit = () => Safe.Run(() => EditServerAsync(i)),
            Delete = () =>
            {
                _servers.RemoveAt(i);
                Fill();
            },
        }));
        Context.DbServerCount = _servers.Count;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _add.IsEnabled = !Context.HasRemoteSource;
        _remoteNote.IsVisible = Context.HasRemoteSource;
    }

    private async Task AddServerAsync()
    {
        var dialog = new DbServerDialog(Context, new TaskDatabaseServer(), isNew: true);
        if (await dialog.ShowAsync(Context.Owner))
        {
            _servers.Add(dialog.Server);
            Fill();
        }
    }

    private async Task EditServerAsync(int index)
    {
        if (index >= _servers.Count)
        {
            return;
        }

        var dialog = new DbServerDialog(Context, _servers[index], isNew: false);
        if (await dialog.ShowAsync(Context.Owner))
        {
            _servers[index] = dialog.Server;
            Fill();
        }
    }
}

/// <summary>
/// «Назначения»: where the copies go (section 6.3), each added and changed in its own dialog, and how the copies are laid
/// out (section 6.4): a folder per copy, named by a template, with an example of the name it gives.
/// </summary>
internal sealed class DestinationsPage : EditorPage
{
    /// <summary>Name templates offered in the list; any other text can be typed.</summary>
    internal static readonly string[] TemplatePresets =
    [
        LayoutOptions.DefaultNameTemplate,
        "{task} {yyyy}-{MM}-{dd} {HH}-{mm}",
        "{task} {yyyy}-{MM}-{dd} ({type})",
        "{yyyy}-{MM}-{dd} {HH}-{mm} {task} ({type})",
        "{computer} {task} {yyyy}-{MM}-{dd} {HH}-{mm} ({type})",
    ];

    private readonly CardsItem _cards = new(Strings.ErrorNoDestinations);
    private readonly NoteItem _cloudNote;
    private readonly NoteItem _mirrorNote;
    private readonly SwitchItem _separate = new(Strings.SeparateFolders);
    private readonly NoteItem _separateForced;
    private readonly ComboItem _template = new(L(Strings.NameTemplate), TemplatePresets, width: 560) { Help = Strings.NameTemplateHelp };
    private readonly List<TaskDestination> _destinations = [];

    public DestinationsPage(EditorContext context)
        : base(context, Strings.PageDestinations, "hard_drive", Strings.DestinationsNote)
    {
        Add(_cards);
        Add(ButtonItem.Of("add", Strings.DestinationAddTitle, () => Safe.Run(AddDestinationAsync)));
        _cloudNote = Note(string.Empty, Tone.Warning, visible: false);
        Heading(Strings.EditorLayout, Strings.SeparateFoldersHelp);
        _mirrorNote = Note(Strings.LayoutMirrorNote, Tone.Caution, visible: false);
        Add(_separate);
        _separateForced = Note(string.Empty, visible: false);
        Add(_template);
        _separate.OnChange = UpdateLayout;
        _template.OnChange = ShowExample;
        Context.ShapeChanged += (_, _) => UpdateLayout();
        Context.RemoteChanged += (_, _) => UpdateLayout();
        Context.CredentialsChanged += (_, _) => Fill();
    }

    public override void LoadFrom(TaskDefinition task)
    {
        _destinations.Clear();
        _destinations.AddRange(task.Destinations);
        _separate.On = task.Layout.SeparateFolders;
        _template.Load(task.Layout.NameTemplate);
        Fill();
        UpdateLayout();
    }

    public override string? SaveTo(TaskDefinition task)
    {
        if (task.Enabled && _destinations.Count == 0)
        {
            return Strings.ErrorNoDestinations;
        }

        // The dialog checks what it saves; this catches tasks that came in from elsewhere (the server, a file).
        foreach (var destination in _destinations)
        {
            if (!DestinationKinds.IsSupported(destination.Kind))
            {
                continue;
            }

            if (destination.Kind == DestinationKinds.Usb)
            {
                // The disk is found by its label; a drive letter in the folder would only mislead.
                destination.Path = DestinationKinds.UsbFolder(destination.Path);
            }

            if (DestinationDialog.Check(destination) is { } problem)
            {
                return TaskDescriber.Destination(destination) + ": " + problem;
            }
        }

        bool named = UsesTemplate;
        if (named && string.IsNullOrWhiteSpace(_template.Text))
        {
            _template.Error = Strings.ErrorTemplateRequired;
            return Strings.ErrorTemplateRequired;
        }

        // A locked file cannot be replaced, and a mirror and a shared folder replace files (section 6.3).
        bool replaces = Context.IsMirror || (!_separate.On && Context.ArchiveFormat == ArchiveFormats.None);
        if (replaces && _destinations.FirstOrDefault(d => d.ProtectsFiles) is { } locked)
        {
            return Custos.Core.Backup.BackupEngine.ProtectionNeedsSets(locked, TaskDescriber.Destination(locked));
        }

        // A mirror needs to look at and replace single files: not on a cloud drive (section 6.3).
        if (Context.IsMirror && _destinations.FirstOrDefault(d => DestinationKinds.IsCloudDrive(d.Kind)) is { } cloud)
        {
            return Custos.Core.Messages.Format(Custos.Core.Messages.CloudNoMirror, TaskDescriber.Destination(cloud));
        }

        task.Destinations = [.. _destinations];
        task.Layout.SeparateFolders = _separate.On;
        if (named)
        {
            string template = _template.Value;
            task.Layout.NameTemplate = template.Length == 0 ? LayoutOptions.DefaultNameTemplate : template;
        }

        return null;
    }

    /// <summary>The name template is used: a folder per copy, or archives (always named by date); never a mirror.</summary>
    private bool UsesTemplate => !Context.IsMirror && (_separate.On || Context.ArchiveFormat != ArchiveFormats.None);

    /// <summary>The name of a destination kind; a cloud drive is marked as missing in the legacy edition.</summary>
    internal static string KindText(string kind) =>
        Core.CustosEdition.IsLegacy && DestinationKinds.IsCloudDrive(kind) ? Strings.Format(Strings.KindNotInLegacy, KindName(kind)) : KindName(kind);

    internal static string KindName(string kind) => kind switch
    {
        DestinationKinds.Local => Strings.KindLocal,
        DestinationKinds.Unc => Strings.KindUnc,
        DestinationKinds.Usb => Strings.KindUsb,
        DestinationKinds.Sftp => Strings.KindSftp,
        DestinationKinds.Ftp => Strings.KindFtp,
        DestinationKinds.Ftps => Strings.KindFtps,
        DestinationKinds.WebDav => Strings.KindWebDav,
        DestinationKinds.S3 => Strings.KindS3,
        DestinationKinds.GDrive => Strings.KindGDrive,
        DestinationKinds.YaDisk => Strings.KindYaDisk,
        DestinationKinds.Dropbox => Strings.KindDropbox,
        _ => Strings.Format(Strings.KindLater, kind.ToUpperInvariant()),
    };

    /// <summary>The card of a destination: the place, its kind and account, WORM, Object Lock and speed as tags.</summary>
    internal static CardItem Card(TaskDestination destination, EditorContext context, Action? edit, Action? delete)
    {
        var tags = new List<CardTag>();
        if (WormSettings.Of(destination) is { } worm)
        {
            tags.Add(new CardTag("lock_clock", Strings.Format(DesktopStrings.WormTag, worm.Days), Accent: true));
        }

        if (destination.Kind == DestinationKinds.S3 && destination.ObjectLockDays > 0)
        {
            tags.Add(new CardTag("lock_clock", Strings.Format(DesktopStrings.ObjectLockTag, destination.ObjectLockDays), Accent: true));
        }

        if (destination.SpeedLimitKbps > 0)
        {
            tags.Add(new CardTag("speed", Sizes.FormatSpeed(destination.SpeedLimitKbps * 1024L)));
        }

        string? account = destination.CredentialId is { } id ? context.FindCredential(id)?.DisplayName ?? Strings.AccountUnknown
            : destination.Kind == DestinationKinds.Unc ? Strings.AccountService
            : null;
        string icon = destination.Kind switch
        {
            DestinationKinds.Local => "folder",
            DestinationKinds.Unc => "lan",
            DestinationKinds.Usb => "usb",
            _ when DestinationKinds.IsCloudDrive(destination.Kind) => "cloud_upload",
            _ => "dns",
        };
        string subtitle = KindText(destination.Kind);
        if (account is not null)
        {
            subtitle += " · " + account;
        }

        return new CardItem(icon, TaskDescriber.Destination(destination), subtitle, tags)
        {
            Edit = edit,
            Delete = delete,
            Warning = account == Strings.AccountUnknown || !DestinationKinds.IsSupported(destination.Kind),
        };
    }

    private void Fill()
    {
        _cards.Fill(_destinations.Select((destination, i) => Card(destination, Context,
            () => Safe.Run(() => EditDestinationAsync(i)),
            () =>
            {
                _destinations.RemoveAt(i);
                Fill();
            })));

        // The archive page offers 7z only while every destination lets a writer go back in the file.
        Context.StreamOnlyDestination = _destinations.FirstOrDefault(d => !DestinationKinds.CanSeek(d.Kind)) is { } stream
            ? TaskDescriber.Destination(stream)
            : null;
        Context.WormDays = _destinations.Select(d => WormSettings.Of(d)?.Days ?? 0).DefaultIfEmpty(0).Max();
        Context.Protecting = _destinations.FirstOrDefault(d => d.ProtectsFiles);
        Context.CloudDrive = _destinations.FirstOrDefault(d => DestinationKinds.IsCloudDrive(d.Kind));
        Context.KeepsNtfs = _destinations.Count == 0 || _destinations.Any(d => DestinationKinds.IsFileSystem(d.Kind));
        UpdateLayout();
    }

    private void UpdateLayout()
    {
        bool mirror = Context.IsMirror;
        _mirrorNote.IsVisible = mirror;
        ShowCloudNote();

        // A destination that protects its files takes only sets that are never overwritten (section 6.3). A Linux
        // server always makes a set of its own (CUSTOS-remote-pull.md 4.1), whatever the switch says.
        var protects = mirror ? null : _destinations.FirstOrDefault(d => d.ProtectsFiles);
        string? forced = mirror ? null
            : Context.HasRemoteSource ? Strings.SeparateRemoteNote
            : protects is not null ? Strings.Format(Strings.SeparateForcedNote, TaskDescriber.Destination(protects))
            : null;
        if (forced is not null && !_separate.On)
        {
            _separate.On = true;
        }

        _separate.IsEnabled = !mirror && forced is null;
        _separateForced.Show(forced);
        _template.IsEnabled = UsesTemplate;
        ShowExample();
        Context.SeparateFolders = _separate.On;
    }

    /// <summary>The name the template gives now, so that a typing slip shows before the first run.</summary>
    private void ShowExample()
    {
        if (!UsesTemplate || string.IsNullOrWhiteSpace(_template.Text))
        {
            _template.Hint = null;
            return;
        }

        string name = Context.TaskName.Length > 0 ? Context.TaskName : Strings.NewTaskName;
        string type = Context.BackupType == BackupTypes.Mirror ? BackupTypes.Full : Context.BackupType;
        string example = Custos.Core.Backup.SetNaming.Expand(_template.Value, name, DesktopClock.Now.LocalDateTime, Environment.MachineName, type);
        example += Context.ArchiveFormat switch
        {
            ArchiveFormats.Zip => ".zip",
            ArchiveFormats.SevenZip => ".7z",
            _ => string.Empty,
        };
        _template.Hint = Strings.Format(Strings.NameTemplateExample, example);
    }

    /// <summary>A copy without compression to a cloud drive is allowed, but slow with many small files (section 6.3).</summary>
    private void ShowCloudNote()
    {
        var cloud = _destinations.FirstOrDefault(d => DestinationKinds.IsCloudDrive(d.Kind));
        bool plain = cloud is not null && Context.ArchiveFormat == ArchiveFormats.None;
        _cloudNote.Show(!plain ? null
            : Context.IsMirror ? Custos.Core.Messages.Format(Custos.Core.Messages.CloudNoMirror, TaskDescriber.Destination(cloud!))
            : Strings.Format(Strings.DestinationCloudPlainNote, KindText(cloud!.Kind)));
    }

    private async Task AddDestinationAsync()
    {
        var dialog = new DestinationDialog(Context, new TaskDestination { Kind = DestinationKinds.Local }, isNew: true);
        if (await dialog.ShowAsync(Context.Owner))
        {
            _destinations.Add(dialog.Destination);
            if (DestinationKinds.IsCloudDrive(dialog.Destination.Kind))
            {
                Context.WantZip();
            }

            Fill();
        }
    }

    private async Task EditDestinationAsync(int index)
    {
        if (index >= _destinations.Count)
        {
            return;
        }

        var dialog = new DestinationDialog(Context, _destinations[index], isNew: false);
        if (await dialog.ShowAsync(Context.Owner))
        {
            _destinations[index] = dialog.Destination;
            Fill();
        }
    }
}

/// <summary>«Расписание»: daily, weekly, monthly, by interval or by hand, with catch-up runs (section 6.7).</summary>
internal sealed class SchedulePage : EditorPage
{
    private static readonly string[] Types = [ScheduleTypes.Daily, ScheduleTypes.Weekly, ScheduleTypes.Monthly, ScheduleTypes.Interval, ScheduleTypes.Manual];

    private readonly SegmentsItem _type;
    private readonly ChipsItem _days;
    private readonly ChipsItem _monthDays;
    private readonly NoteItem _monthNote;
    private readonly TextItem _times = new(L(Strings.ScheduleTimes), width: 240) { Hint = Strings.ScheduleTimesHelp };
    private readonly NumberItem _interval = new(null, 60, 1, 1440, 90) { Compact = true };
    private readonly RowItem _intervalRow;
    private readonly SwitchItem _catchUp = new(Strings.ScheduleCatchUp, Strings.ScheduleCatchUpHelp, card: true);
    private readonly InfoItem _preview = new("event_available", string.Empty);
    private bool _loading;

    // The interval as loaded: kept as it was when the schedule is not «by timer».
    private int _loadedInterval;

    public SchedulePage(EditorContext context)
        : base(context, Strings.PageSchedule, "schedule", Strings.ScheduleTimeZoneNote)
    {
        _type = new SegmentsItem(L(Strings.ScheduleType),
            [Strings.ScheduleDaily, Strings.ScheduleWeekly, Strings.ScheduleMonthly, Strings.ScheduleByInterval.Split(',')[0], Strings.ScheduleManual], 0);
        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        _days = new ChipsItem(L(Strings.ScheduleWeekdays), Enumerable.Range(1, 7).Select(d => names[d % 7]));
        _monthDays = new ChipsItem(L(Strings.ScheduleMonthDays),
            [.. Enumerable.Range(1, MonthDays.LastNumber).Select(d => d.ToString(CultureInfo.CurrentCulture)), Strings.LastDayOfMonth]);
        Add(_type);
        Add(_days);
        Add(_monthDays);
        _monthNote = Note(DesktopStrings.MonthDaysNote);
        Add(_times);
        _intervalRow = Add(new RowItem(new LabelItem(L(Strings.ScheduleInterval)), _interval, new LabelItem(DesktopStrings.EveryMinutes)));
        Add(_catchUp);
        Heading(Strings.SchedulePreview);
        Add(_preview);

        _type.OnChange = () =>
        {
            UpdateFields();
            ShowPreview();
        };
        _times.OnChange = ShowPreview;
        _days.OnChange = ShowPreview;
        _monthDays.OnChange = ShowPreview;
        _interval.OnChange = ShowPreview;
        _catchUp.OnChange = ShowPreview;
    }

    public override void LoadFrom(TaskDefinition task)
    {
        _loading = true;
        var schedule = task.Schedule;
        int type = Array.IndexOf(Types, schedule.Type);
        _type.Load(type < 0 ? 0 : type);
        _times.Load(string.Join(", ", schedule.Times));
        for (int i = 0; i < 7; i++)
        {
            _days.Chips[i].Load(schedule.DaysOfWeek.Count == 0 || schedule.DaysOfWeek.Contains(i + 1));
        }

        for (int i = 0; i < MonthDays.LastNumber; i++)
        {
            _monthDays.Chips[i].Load(schedule.DaysOfMonth.Contains(i + 1));
        }

        _monthDays.Chips[MonthDays.LastNumber].Load(schedule.DaysOfMonth.Any(d => d is -1 or 31));
        _loadedInterval = schedule.IntervalMinutes;
        _interval.Value = schedule.IntervalMinutes > 0 ? schedule.IntervalMinutes : 60;
        _catchUp.On = schedule.CatchUp;
        _loading = false;
        UpdateFields();
        ShowPreview();
    }

    public override string? SaveTo(TaskDefinition task)
    {
        var (schedule, problem) = Read();
        if (problem is not null)
        {
            return problem;
        }

        // Fields this page does not show are kept.
        schedule!.ExtensionData = task.Schedule.ExtensionData;
        task.Schedule = schedule;
        return null;
    }

    private string SelectedType => Types[Math.Clamp(_type.Selected, 0, Types.Length - 1)];

    private void UpdateFields()
    {
        string type = SelectedType;
        _times.IsVisible = type is ScheduleTypes.Daily or ScheduleTypes.Weekly or ScheduleTypes.Monthly;
        _days.IsVisible = type is ScheduleTypes.Daily or ScheduleTypes.Weekly or ScheduleTypes.Interval;
        _monthDays.IsVisible = _monthNote.IsVisible = type == ScheduleTypes.Monthly;
        _intervalRow.IsVisible = type == ScheduleTypes.Interval;
        _catchUp.IsVisible = type != ScheduleTypes.Manual;
    }

    /// <summary>The schedule as typed; with the first problem found, in words.</summary>
    private (ScheduleOptions? Schedule, string? Problem) Read()
    {
        if (SelectedType == ScheduleTypes.Interval && Invalid(_interval) is { } wrong)
        {
            return (null, wrong);
        }

        var schedule = new ScheduleOptions
        {
            Type = SelectedType,
            CatchUp = _catchUp.On,
            IntervalMinutes = SelectedType == ScheduleTypes.Interval ? _interval.Value : _loadedInterval,
        };

        schedule.Times = [];
        foreach (string part in _times.Text.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ScheduleCalculator.ParseTime(part) is not { } time)
            {
                return (null, Strings.Format(Strings.ErrorBadTime, part));
            }

            string text = time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
            if (!schedule.Times.Contains(text))
            {
                schedule.Times.Add(text);
            }
        }

        schedule.Times.Sort(StringComparer.Ordinal);
        schedule.DaysOfWeek = [.. Enumerable.Range(1, 7).Where(d => _days.Chips[d - 1].On)];
        schedule.DaysOfMonth = [.. Enumerable.Range(1, MonthDays.LastNumber).Where(d => _monthDays.Chips[d - 1].On)];
        if (_monthDays.Chips[MonthDays.LastNumber].On)
        {
            schedule.DaysOfMonth.Add(-1);
        }

        switch (schedule.Type)
        {
            case ScheduleTypes.Daily or ScheduleTypes.Weekly or ScheduleTypes.Monthly when schedule.Times.Count == 0:
                return (null, Strings.ErrorNoTimes);
            case ScheduleTypes.Weekly or ScheduleTypes.Daily when schedule.DaysOfWeek.Count == 0:
                return (null, Strings.ErrorNoWeekdays);
            case ScheduleTypes.Monthly when schedule.DaysOfMonth.Count == 0:
                return (null, Strings.ErrorNoMonthDays);
        }

        if (schedule.Type is ScheduleTypes.Daily or ScheduleTypes.Interval && schedule.DaysOfWeek.Count == 0)
        {
            schedule.DaysOfWeek = [1, 2, 3, 4, 5, 6, 7];
        }

        if (schedule.Times.Count == 0)
        {
            schedule.Times = ["00:00"];
        }

        return (schedule, null);
    }

    private void ShowPreview()
    {
        if (_loading)
        {
            return;
        }

        var (schedule, problem) = Read();
        if (schedule is null)
        {
            _preview.Title = problem ?? string.Empty;
            _preview.Detail = null;
            return;
        }

        _preview.Title = TaskDescriber.ScheduleWithCatchUp(schedule);
        var next = ScheduleCalculator.Upcoming(schedule, DesktopClock.Now, TimeZoneInfo.Local, 3);
        _preview.Detail = next.Count == 0 ? null : Strings.Format(Strings.ScheduleUpcoming, string.Join(", ", next.Select(t => Describe.Time(t))));
    }
}

/// <summary>«Сжатие»: format, compression level, volumes, encryption and verification (section 6.5).</summary>
internal sealed class ArchivePage : EditorPage
{
    // Volume sizes in MB as 7-Zip offers them: CD, the FAT32 limit, DVD.
    private static readonly int[] VolumePresets = [700, 4095, 4480];

    private readonly NoteItem _mirrorNote;
    private readonly RadioGroup _formats = new();
    private readonly RadioItem _none;
    private readonly RadioItem _zip;
    private readonly RadioItem _sevenZip;
    private readonly NoteItem _sevenZipProblem;
    private readonly NoteItem _sevenZipStream;
    private readonly ChoiceItem _level;
    private readonly CheckItem _split = new(Strings.ArchiveSplit.TrimEnd(), on: false, description: Strings.ArchiveSplitHelp) { Inline = true };
    private readonly ChoiceItem _volume = new(null, [Strings.ArchiveVolume700, Strings.ArchiveVolumeFat32, Strings.ArchiveVolumeDvd, Strings.ArchiveVolumeCustom], 0, 240)
    {
        Compact = true,
    };

    private readonly NumberItem _volumeMb = new(null, 1024, 1, 1_000_000, 110) { Compact = true };
    private readonly LabelItem _volumeUnit = new(Strings.ArchiveMb);
    private readonly RowItem _volumes;
    private readonly NoteItem _splitNote;
    private readonly SwitchItem _encrypt = new(Strings.ArchiveEncrypt);
    private readonly ButtonItem _setPassword;
    private readonly CheckItem _encryptNames = new(Strings.ArchiveEncryptNames, description: Strings.ArchiveEncryptNamesHelp) { Indent = 30 };
    private readonly NumberItem _verifyRuns = new(L(Strings.VerifyEveryRuns), 0, 0, 1000, 80) { Hint = Strings.VerifyEveryRunsHelp };
    private readonly NumberItem _verifyDays = new(L(Strings.VerifyStoredDays), 0, 0, 3650, 80) { Hint = Strings.VerifyStoredDaysHelp };
    private string? _passwordId;
    private bool _passwordSaved;
    private string _taskName = string.Empty;

    public ArchivePage(EditorContext context)
        : base(context, Strings.PageArchive, "folder_zip", Strings.ArchiveOpenAnywhere)
    {
        _level = new ChoiceItem(L(Strings.ArchiveLevel), [.. Enumerable.Range(0, 10).Select(level => level switch
        {
            0 => Strings.ArchiveLevel0,
            1 => Strings.ArchiveLevel1,
            5 => Strings.ArchiveLevel5,
            9 => Strings.ArchiveLevel9,
            _ => level.ToString(CultureInfo.CurrentCulture),
        })], 5, 260) { Help = Lines(Strings.ArchiveLevelHelp) };
        _setPassword = ButtonItem.Of("key", Strings.ArchiveSetPassword.TrimEnd('…', '.') + "…", () => Safe.Run(SetPasswordAsync), ButtonLook.Outlined);

        Heading(Strings.ArchiveFormat);
        _mirrorNote = Note(Strings.ArchiveMirrorNote, Tone.Caution, visible: false);
        _none = new RadioItem(_formats, Strings.ArchiveNone);
        _zip = new RadioItem(_formats, Strings.ArchiveZip, Strings.ArchiveZipHelp);
        _sevenZip = new RadioItem(_formats, Strings.ArchiveSevenZip, Strings.ArchiveSevenZipHelp);
        Add(new RowItem(_none, _zip, _sevenZip));
        _sevenZipProblem = Note(string.Empty, Tone.Caution, 52, visible: false);
        _sevenZipStream = Note(string.Empty, Tone.Caution, 52, visible: false);
        Add(_level);
        _volumes = Add(new RowItem(_split, _volume, _volumeMb, _volumeUnit));
        _splitNote = new NoteItem(Strings.ArchiveSplitHelp);

        Heading(Strings.ArchiveEncryption);
        Add(new RowItem(_encrypt, _setPassword));
        Add(_encryptNames);
        Note(Strings.ArchivePasswordWarning, Tone.Warning);

        Heading(Strings.ArchiveVerify);
        Add(_verifyRuns);
        Add(_verifyDays);

        foreach (var format in _formats.Items)
        {
            format.OnChange = UpdateControls;
        }

        _split.OnChange = UpdateControls;
        _encrypt.OnChange = UpdateControls;
        _volume.OnChange = UpdateControls;
        Context.ShapeChanged += (_, _) => UpdateControls();

        // A cloud drive added to a task that keeps plain files: ZIP is the default there (section 6.3).
        Context.ZipWanted += (_, _) =>
        {
            if (_none.On && !Context.IsMirror)
            {
                _zip.On = true;
            }
        };
    }

    public override void Shown()
    {
        string? problem = Context.Monitor.Snapshot?.SevenZipProblem;
        _sevenZipProblem.Show(problem is null ? null : Strings.Format(Strings.ArchiveSevenZipProblem, problem));
    }

    public override void LoadFrom(TaskDefinition task)
    {
        var archive = task.Archive;
        _taskName = task.Name;
        (archive.Format switch
        {
            ArchiveFormats.Zip => _zip,
            ArchiveFormats.SevenZip => _sevenZip,
            _ => _none,
        }).Load();
        _level.Load(Math.Clamp(archive.Level, 0, 9));
        _split.Load(archive.SplitMb > 0);
        int preset = Array.IndexOf(VolumePresets, archive.SplitMb);
        _volume.Load(archive.SplitMb <= 0 ? 0 : preset >= 0 ? preset : VolumePresets.Length);
        _volumeMb.Value = archive.SplitMb > 0 ? archive.SplitMb : 1024;
        _encrypt.On = archive.Encrypt;
        _encryptNames.Load(archive.EncryptNames);
        _passwordId = archive.PasswordCredentialId;
        _verifyRuns.Value = task.Verify.EveryRuns;
        _verifyDays.Value = task.Verify.StoredEveryDays;
        ShowPasswordState();
        UpdateControls();
        Shown();
    }

    public override string? SaveTo(TaskDefinition task)
    {
        if (Invalid(_volumeMb, _verifyRuns, _verifyDays) is { } wrong)
        {
            return wrong;
        }

        var archive = task.Archive;
        archive.Format = Context.IsMirror ? ArchiveFormats.None : SelectedFormat;
        archive.Level = Math.Max(0, _level.Selected);
        archive.SplitMb = _sevenZip.On && _split.On
            ? _volume.Selected < VolumePresets.Length ? VolumePresets[Math.Max(0, _volume.Selected)] : _volumeMb.Value
            : 0;
        bool packed = archive.Format != ArchiveFormats.None;
        archive.Encrypt = packed && _encrypt.On;
        archive.EncryptNames = archive.Encrypt && _sevenZip.On && _encryptNames.On;
        archive.PasswordCredentialId = archive.Encrypt ? _passwordId : archive.PasswordCredentialId;
        task.Verify.EveryRuns = _verifyRuns.Value;
        task.Verify.StoredEveryDays = _verifyDays.Value;

        if (archive.Format == ArchiveFormats.SevenZip && task.Destinations.FirstOrDefault(d => !DestinationKinds.CanSeek(d.Kind)) is { } stream)
        {
            return Strings.Format(Strings.ArchiveSevenZipNeedsSeek, TaskDescriber.Destination(stream));
        }

        if (archive.Encrypt && !HasPassword())
        {
            return Strings.ArchivePasswordRequired;
        }

        return null;
    }

    private string SelectedFormat => _zip.On ? ArchiveFormats.Zip : _sevenZip.On ? ArchiveFormats.SevenZip : ArchiveFormats.None;

    private void UpdateControls()
    {
        // A mirror keeps plain files: the format is «none» whatever is chosen.
        bool mirror = Context.IsMirror;
        _mirrorNote.IsVisible = mirror;
        _none.IsEnabled = _zip.IsEnabled = !mirror;

        // 7z is not offered while a destination takes files from start to end only (section 6.5). A task that has
        // both keeps its choice visible, with the reason why it cannot be saved like that.
        string? stream = Context.StreamOnlyDestination;
        _sevenZip.IsEnabled = !mirror && (stream is null || _sevenZip.On);
        _sevenZipStream.Show(stream is null || mirror ? null : Strings.Format(Strings.ArchiveSevenZipNeedsSeek, stream));
        Context.SetShape(archiveFormat: mirror ? ArchiveFormats.None : SelectedFormat);
        bool packed = !_none.On && !mirror;
        _level.IsEnabled = packed;
        _volumes.IsVisible = _splitNote.IsVisible = _sevenZip.On;
        _split.IsEnabled = _sevenZip.On && packed;
        _volume.IsEnabled = _split.IsEnabled && _split.On;
        bool custom = _volume.Selected == VolumePresets.Length;
        _volumeMb.IsVisible = _volumeUnit.IsVisible = custom;
        _volumeMb.IsEnabled = _volume.IsEnabled && custom;
        _encrypt.IsEnabled = packed;
        _setPassword.IsEnabled = packed && _encrypt.On;
        _encryptNames.IsEnabled = _sevenZip.On && _encrypt.On && packed;
    }

    /// <summary>A password is stored for the task, or was just set in this editor.</summary>
    private bool HasPassword() => _passwordId is { } id && (_passwordSaved
        || Context.Monitor.Snapshot?.Credentials.FirstOrDefault(c => Describe.SameId(c.Id, id)) is { HasSecret: true });

    private void ShowPasswordState() => _encrypt.Description = HasPassword() ? Strings.ArchivePasswordSet : Strings.ArchivePasswordNotSet;

    private async Task SetPasswordAsync()
    {
        var dialog = new ArchivePasswordDialog(Context.Monitor, _passwordId, Context.TaskName.Length > 0 ? Context.TaskName : _taskName);
        if (await dialog.ShowAsync(Context.Owner) && dialog.SavedId is { } id)
        {
            _passwordId = id;
            _passwordSaved = true;
            ShowPasswordState();
        }
    }
}

/// <summary>«Хранение»: by count of full copies with their chains, by age, or grandfather-father-son (section 6.6).</summary>
internal sealed class RetentionPage : EditorPage
{
    private readonly NoteItem _mirrorNote;
    private readonly NoteItem _sharedNote;
    private readonly RadioGroup _modes = new();
    private readonly RadioItem _keepAll;
    private readonly RadioItem _byCount;
    private readonly NumberItem _keepFull = new(null, 12, 1, 1000, 80) { Compact = true };
    private readonly RadioItem _byAge;
    private readonly NumberItem _maxAge = new(null, 30, 1, 3650, 80) { Compact = true };
    private readonly RadioItem _gfs;
    private readonly NumberItem _daily = new(null, 7, 0, 1000, 72) { Compact = true };
    private readonly NumberItem _weekly = new(null, 4, 0, 1000, 72) { Compact = true };
    private readonly NumberItem _monthly = new(null, 12, 0, 1000, 72) { Compact = true };
    private readonly RowItem _gfsCounts;
    private readonly NoteItem _noFullWarning;
    private readonly NoteItem _wormNote;

    public RetentionPage(EditorContext context)
        : base(context, Strings.PageRetention, "layers")
    {
        _mirrorNote = Note(Strings.RetentionMirrorNote, Tone.Caution, visible: false);
        _sharedNote = Note(Strings.Format(Strings.RetentionSharedNote, Strings.SeparateFolders, Strings.PageDestinations, Strings.PageArchive),
            Tone.Caution, visible: false);
        _keepAll = Add(new RadioItem(_modes, Strings.RetentionNone));
        _byCount = new RadioItem(_modes, Strings.RetentionCount, inline: true);
        Add(new RowItem(_byCount, _keepFull, new LabelItem(Strings.RetentionCountSuffix) { Help = Strings.RetentionCountHint }));
        _byAge = new RadioItem(_modes, Strings.RetentionAge, inline: true);
        Add(new RowItem(_byAge, _maxAge, new LabelItem(Strings.RetentionAgeSuffix) { Help = Strings.RetentionAgeHint }));
        _gfs = Add(new RadioItem(_modes, Strings.RetentionGfs, Strings.RetentionGfsHint));
        _gfsCounts = Add(new RowItem(
            new LabelItem(L(Strings.GfsDaily)), _daily,
            new LabelItem(L(Strings.GfsWeekly)) { Indent = 8 }, _weekly,
            new LabelItem(L(Strings.GfsMonthly)) { Indent = 8 }, _monthly)
        {
            Indent = 30,
        });
        _noFullWarning = Note(Strings.RetentionNoFullWarning, Tone.Warning, visible: false);
        _wormNote = Note(string.Empty, Tone.Warning, visible: false);
        Heading(Strings.RetentionRules);
        Note(Strings.RetentionRulesText);

        foreach (var mode in _modes.Items)
        {
            mode.OnChange = UpdateEnabled;
        }

        Context.ShapeChanged += (_, _) => UpdateEnabled();
        Context.RemoteChanged += (_, _) => UpdateEnabled();
    }

    public override void LoadFrom(TaskDefinition task)
    {
        var retention = task.Retention;
        _keepFull.Value = retention.KeepFull;
        _maxAge.Value = retention.MaxAgeDays > 0 ? retention.MaxAgeDays : 30;
        _daily.Value = retention.Gfs.Daily;
        _weekly.Value = retention.Gfs.Weekly;
        _monthly.Value = retention.Gfs.Monthly;
        (retention.Mode switch
        {
            RetentionModes.Count => _byCount,
            RetentionModes.Age => _byAge,
            RetentionModes.Gfs => _gfs,
            _ => _keepAll,
        }).Load();
        UpdateEnabled();
    }

    public override string? SaveTo(TaskDefinition task)
    {
        if (Invalid(_keepFull, _maxAge, _daily, _weekly, _monthly) is { } wrong)
        {
            return wrong;
        }

        if (_gfs.On && _daily.Value + _weekly.Value + _monthly.Value == 0)
        {
            return Strings.RetentionGfsEmpty;
        }

        var retention = task.Retention;
        retention.Mode = _byCount.On ? RetentionModes.Count : _byAge.On ? RetentionModes.Age : _gfs.On ? RetentionModes.Gfs : RetentionModes.None;
        retention.KeepFull = _keepFull.Value;
        if (_byAge.On)
        {
            // Unset (0) shows as 30 days; it is written only when this mode is chosen.
            retention.MaxAgeDays = _maxAge.Value;
        }

        retention.Gfs.Daily = _daily.Value;
        retention.Gfs.Weekly = _weekly.Value;
        retention.Gfs.Monthly = _monthly.Value;

        // A rule that deletes backups before the WORM of a destination lets them go deletes nothing (section 6.6). The
        // pages before this one have filled in the type, the destinations and the schedule.
        return Custos.Core.Backup.WormRetention.Problem(task);
    }

    private void UpdateEnabled()
    {
        // A mirror and a shared folder are one copy updated in place: retention has nothing to delete there (section
        // 6.6), so its rules are off, kept as they are for when that changes. A Linux server always makes sets.
        bool mirror = Context.IsMirror;
        bool shared = !mirror && !Context.SeparateFolders && Context.ArchiveFormat == ArchiveFormats.None && !Context.HasRemoteSource;
        bool off = mirror || shared;
        _mirrorNote.IsVisible = mirror;
        _sharedNote.IsVisible = shared;
        _keepAll.IsEnabled = _byCount.IsEnabled = _byAge.IsEnabled = _gfs.IsEnabled = !off;
        _keepFull.IsEnabled = _byCount.On && !off;
        _maxAge.IsEnabled = _byAge.On && !off;
        _gfsCounts.IsEnabled = _gfs.On && !off;

        // Without a forced full backup one chain grows from the first full backup, and «by count» (with incremental or
        // differential backups) or «by age» (incremental: each needs the ones before it) can delete none of it.
        bool chain = Context.BackupType is BackupTypes.Incremental or BackupTypes.Differential && !Context.HasForcedFull;
        _noFullWarning.IsVisible = !off && chain && (_byCount.On || (_byAge.On && Context.BackupType == BackupTypes.Incremental));

        // WORM keeps a backup for its days whatever the rule says (section 6.6).
        _wormNote.Show(Context.WormDays > 0 && !off && !_keepAll.On ? Strings.Format(Strings.RetentionWormNote, Context.WormDays) : null);
    }
}

/// <summary>
/// «События»: programs, scripts and services before and after the backup, whether a failed event before stops the
/// task, and what to do with the computer when the run is over (section 6.9).
/// </summary>
internal sealed class EventsPage : EditorPage
{
    private static readonly string[] AfterValues = [AfterActions.None, AfterActions.Shutdown, AfterActions.Restart, AfterActions.Sleep, AfterActions.Hibernate];

    private readonly EventList _pre;
    private readonly EventList _post;
    private readonly CheckItem _abortOnPre = new(Strings.EventsAbortOnPre);
    private readonly ChoiceItem _after = new(null, [Strings.AfterNone, Strings.AfterShutdown, Strings.AfterRestart, Strings.AfterSleep, Strings.AfterHibernate], 0, 360)
    {
        Hint = Strings.AfterNote,
    };

    private string _unknownAfter = AfterActions.None;

    public EventsPage(EditorContext context)
        : base(context, Strings.PageEvents, "bolt", Strings.EventsNote)
    {
        Heading(Strings.EventsPreHeading);
        _pre = new EventList(context, Items);
        Add(_abortOnPre);
        Heading(Strings.EventsPostHeading);
        _post = new EventList(context, Items);
        Note(Strings.EventsPostNote);
        Heading(Strings.AfterHeading);
        Add(_after);

        // Without events before the backup there is nothing whose failure could stop it.
        _pre.Changed += () => _abortOnPre.IsEnabled = _pre.Count > 0;
    }

    public override void LoadFrom(TaskDefinition task)
    {
        var events = task.Events;
        _pre.Load(events.Pre);
        _post.Load(events.Post);
        _abortOnPre.Load(events.AbortOnPreFailure);
        _abortOnPre.IsEnabled = _pre.Count > 0;
        _unknownAfter = events.After;
        _after.Load(Math.Max(0, Array.IndexOf(AfterValues, events.After)));
    }

    public override string? SaveTo(TaskDefinition task)
    {
        var events = task.Events;
        events.Pre = _pre.Events;
        events.Post = _post.Events;
        events.AbortOnPreFailure = _abortOnPre.On;
        string after = AfterValues[Math.Max(0, _after.Selected)];

        // A value of a later version stays unless something else was picked.
        events.After = after == AfterActions.None && Array.IndexOf(AfterValues, _unknownAfter) < 0 ? _unknownAfter : after;
        return null;
    }

    /// <summary>One list of events: cards with «up», «down», «change» and «remove», and «Добавить событие».</summary>
    private sealed class EventList
    {
        private readonly EditorContext _context;
        private readonly CardsItem _cards = new();
        private readonly List<TaskEvent> _events = [];

        public EventList(EditorContext context, ICollection<FormItem> items)
        {
            _context = context;
            items.Add(_cards);
            items.Add(ButtonItem.Of("add", Strings.EventAddTitle, () => Safe.Run(AddAsync)));
        }

        public event Action? Changed;

        public List<TaskEvent> Events => [.. _events];

        public int Count => _events.Count;

        public void Load(List<TaskEvent> events)
        {
            _events.Clear();
            _events.AddRange(events);
            Fill();
        }

        private void Fill()
        {
            _cards.Fill(_events.Select((item, i) => new CardItem(
                item.Kind switch
                {
                    TaskEventKinds.ServiceStop => "stop_circle",
                    TaskEventKinds.ServiceStart => "play_circle",
                    _ => "terminal",
                },
                Describe.Event(item),
                Strings.Format(Strings.EventTimeoutShown, Math.Max(1, (item.TimeoutS + 59) / 60)))
            {
                Edit = () => Safe.Run(() => EditAsync(i)),
                Delete = () =>
                {
                    _events.RemoveAt(i);
                    Fill();
                },
                Up = () => Shift(i, -1),
                Down = () => Shift(i, 1),
                IsFirst = i == 0,
                IsLast = i == _events.Count - 1,
            }));
            Changed?.Invoke();
        }

        private async Task AddAsync()
        {
            var dialog = new EventDialog(new TaskEvent(), isNew: true);
            if (await dialog.ShowAsync(_context.Owner))
            {
                _events.Add(dialog.Event);
                Fill();
            }
        }

        private async Task EditAsync(int index)
        {
            if (index >= _events.Count)
            {
                return;
            }

            var dialog = new EventDialog(_events[index], isNew: false);
            if (await dialog.ShowAsync(_context.Owner))
            {
                _events[index] = dialog.Event;
                Fill();
            }
        }

        private void Shift(int index, int step)
        {
            int other = index + step;
            if (index < 0 || other < 0 || other >= _events.Count)
            {
                return;
            }

            (_events[index], _events[other]) = (_events[other], _events[index]);
            Fill();
        }
    }
}

/// <summary>«Дополнительно»: shadow copies (VSS), read retries, permissions, log detail and priority in the queue.</summary>
internal sealed class AdvancedPage : EditorPage
{
    private static readonly string[] Levels = [LogLevels.Inherit, LogLevels.Summary, LogLevels.Files];

    private readonly SwitchItem _vss = new(Strings.VssEnabled, Strings.VssHelp);
    private readonly NoteItem _vssNote;
    private readonly ChoiceItem _vssFailure = new(L(Strings.VssOnFailure), [Strings.VssAbort, Strings.VssContinue], 0, 420);
    private readonly NumberItem _retries = new(L(Strings.ReadRetries), 1, 0, 5, 200) { Hint = Strings.ReadRetriesHelp };
    private readonly NoteItem _retriesNote;
    private readonly CheckItem _acl = new(Strings.PreserveAcl, description: Strings.PreserveAclHelp);
    private readonly NoteItem _aclNote;
    private readonly ChoiceItem _logLevel = new(L(Strings.LogLevel), [Strings.LogLevelInherit, Strings.LogLevelSummary, Strings.LogLevelFiles], 0, 420);
    private readonly NumberItem _priority = new(L(Strings.Priority), 0, -100, 100, 200) { Hint = Strings.PriorityHelp };

    public AdvancedPage(EditorContext context)
        : base(context, Strings.PageAdvanced, "tune")
    {
        Heading(Strings.EditorVss);
        Add(_vss);
        _vssNote = Note(string.Empty, Tone.Caution, visible: false);
        Add(_vssFailure);
        Heading(Strings.EditorReading);
        Add(_retries);
        _retriesNote = Note(Strings.AdvancedRetriesRemote, Tone.Caution, visible: false);
        Add(_acl);
        _aclNote = Note(string.Empty, Tone.Caution, 52, visible: false);
        Heading(Strings.EditorLogAndQueue);
        Add(_logLevel);
        Add(_priority);

        _vss.OnChange = UpdateEnabled;
        Context.ShapeChanged += (_, _) => UpdateEnabled();
        Context.RemoteChanged += (_, _) => UpdateEnabled();
    }

    public override void Shown() => UpdateEnabled();

    public override void LoadFrom(TaskDefinition task)
    {
        _vss.On = task.Vss.Enabled;
        _vssFailure.Load(task.Vss.OnFailure == VssFailureActions.Continue ? 1 : 0);
        _retries.Value = task.Copy.ReadRetries;
        _acl.Load(task.Copy.PreserveAcl);
        int level = Array.IndexOf(Levels, task.LogLevel);
        _logLevel.Load(level < 0 ? 0 : level);
        _priority.Value = task.Priority;
        UpdateEnabled();
    }

    public override string? SaveTo(TaskDefinition task)
    {
        if (Invalid(_retries, _priority) is { } wrong)
        {
            return wrong;
        }

        task.Vss.Enabled = _vss.On;
        task.Vss.OnFailure = _vssFailure.Selected == 1 ? VssFailureActions.Continue : VssFailureActions.Abort;
        task.Copy.ReadRetries = _retries.Value;
        task.Copy.PreserveAcl = _acl.On;
        task.LogLevel = Levels[Math.Max(0, _logLevel.Selected)];
        task.Priority = _priority.Value;
        return null;
    }

    /// <summary>
    /// What does nothing for this task is off, with the reason; the values stay as they are for when that changes. A
    /// shadow copy is made of local disks only (section 6.4); a Linux server is read by custos-helper; NTFS permissions
    /// go only into plain copies on a folder, a network folder or a USB disk (section 6.8).
    /// </summary>
    private void UpdateEnabled()
    {
        bool remote = Context.HasRemoteSource;
        var reads = Context.Reads;
        string? vss = remote ? Strings.AdvancedVssRemote
            : (reads.Any || Context.DbServerCount > 0) && !reads.Local ? Strings.AdvancedVssNoLocal
            : null;
        _vss.IsEnabled = vss is null;
        _vssNote.Show(vss);
        _vssFailure.IsEnabled = _vss.On && vss is null;

        _retries.IsEnabled = !remote;
        _retriesNote.IsVisible = remote;

        string? acl = remote ? Strings.AdvancedAclRemote
            : Context.ArchiveFormat != ArchiveFormats.None ? Strings.AdvancedAclArchive
            : Context.KeepsNtfs ? null
            : Strings.AdvancedAclNoNtfs;
        _acl.IsEnabled = acl is null;
        _aclNote.Show(acl);
    }
}
