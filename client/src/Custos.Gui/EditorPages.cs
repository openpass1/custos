using System.ComponentModel;
using System.Globalization;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Scheduling;
using Custos.Core.Text;

namespace Custos.Gui;

/// <summary>"Basic": name, on/off, backup type with the explanations of section 6.1 (for a mirror exact or safe),
/// forced full copies, comment.</summary>
internal sealed class BasicPage : EditorPage
{
    private readonly TextBox _name = new() { Dock = DockStyle.Fill, MaxLength = 200 };
    private readonly CheckBox _enabled = new() { Text = Strings.EditorEnabled, AutoSize = true };
    private readonly RadioButton _full = Radio(Strings.TypeFullTitle);
    private readonly RadioButton _incremental = Radio(Strings.TypeIncrementalTitle);
    private readonly RadioButton _differential = Radio(Strings.TypeDifferentialTitle);
    private readonly RadioButton _mirror = Radio(Strings.TypeMirrorTitle);
    private readonly CheckBox _forceEvery = new() { Text = Strings.ForceEvery, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
    private readonly NumericUpDown _forceEveryRuns = new() { Minimum = 2, Maximum = 1000, Value = 7, Width = 60 };
    private readonly CheckBox _forceWeekday = new() { Text = Strings.ForceWeekday, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
    private readonly ComboBox _weekday = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly CheckBox _forceMonthDay = new() { Text = Strings.ForceMonthDay, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
    private readonly ComboBox _monthDay = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly Panel _forceGroup = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly TextBox _comment = new() { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Height = 64, Dock = DockStyle.Fill };
    private readonly RadioButton _mirrorExact = new() { Text = Strings.MirrorExact, AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
    private readonly RadioButton _mirrorSafe = new() { Text = Strings.MirrorSafe, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly NumericUpDown _safeDays = new() { Minimum = 1, Maximum = 3650, Value = 30, Width = 70 };
    private readonly FlowLayoutPanel _mirrorOptions = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(18, 0, 0, 0) };

    public BasicPage(EditorContext context)
        : base(context, Strings.PageBasic, Glyph.Info)
    {
        var dayNames = CultureInfo.CurrentCulture.DateTimeFormat.DayNames;
        for (int day = 1; day <= 7; day++)
        {
            _weekday.Items.Add(dayNames[day % 7]);
        }

        for (int day = 1; day <= 31; day++)
        {
            _monthDay.Items.Add(day.ToString(CultureInfo.CurrentCulture));
        }

        _monthDay.Items.Add(Strings.LastDayOfMonth);
        _weekday.SelectedIndex = 6;
        _monthDay.SelectedIndex = 0;

        AddRow(Strings.EditorName, _name);
        AddWide(_enabled);
        AddHeading(Strings.EditorBackupType);
        AddWide(_full);
        AddNote(Strings.TypeFullHint, 18);
        AddWide(_incremental);
        AddNote(Strings.TypeIncrementalHint, 18);
        AddWide(_differential);
        AddNote(Strings.TypeDifferentialHint, 18);
        AddWide(_mirror);
        AddNote(Strings.TypeMirrorHint, 18);
        _mirrorOptions.Controls.AddRange([_mirrorExact, Inline(_mirrorSafe, _safeDays, new Label { Text = Strings.MirrorSafeDays, AutoSize = true, Margin = new Padding(4, 8, 0, 0) })]);
        AddWide(_mirrorOptions);
        AddNote(Strings.MirrorNote, 18);

        AddHeading(Strings.EditorForceFull);
        var force = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
        force.Controls.Add(Inline(_forceEvery, _forceEveryRuns, new Label { Text = Strings.ForceEveryRunsSuffix, AutoSize = true, Margin = new Padding(4, 6, 0, 0) }));
        force.Controls.Add(Inline(_forceWeekday, _weekday));
        force.Controls.Add(Inline(_forceMonthDay, _monthDay));
        _forceGroup.Controls.Add(force);
        AddWide(_forceGroup);
        AddNote(Strings.ForceFullNote);

        AddHeading(Strings.EditorComment);
        AddWide(_comment);

        foreach (var type in new[] { _full, _incremental, _differential, _mirror })
        {
            type.CheckedChanged += (_, _) => UpdateType();
        }

        // The two sit in different panels, so they are kept exclusive by hand.
        _mirrorExact.CheckedChanged += (_, _) => _mirrorSafe.Checked = !_mirrorExact.Checked;
        _mirrorSafe.CheckedChanged += (_, _) =>
        {
            _mirrorExact.Checked = !_mirrorSafe.Checked;
            _safeDays.Enabled = _mirrorSafe.Checked && _mirror.Checked;
        };
        _name.TextChanged += (_, _) => Context.SetShape(taskName: _name.Text.Trim());
        _forceEvery.CheckedChanged += (_, _) => _forceEveryRuns.Enabled = _forceEvery.Checked;
        _forceWeekday.CheckedChanged += (_, _) => _weekday.Enabled = _forceWeekday.Checked;
        _forceMonthDay.CheckedChanged += (_, _) => _monthDay.Enabled = _forceMonthDay.Checked;
    }

    public override void LoadFrom(TaskDefinition task)
    {
        _name.Text = task.Name;
        _enabled.Checked = task.Enabled;
        _mirrorSafe.Checked = task.MirrorMode == MirrorModes.Safe;
        _mirrorExact.Checked = !_mirrorSafe.Checked;
        _safeDays.Value = Math.Clamp(task.Retention.SafeMirrorDays > 0 ? task.Retention.SafeMirrorDays : 30, 1, 3650);
        switch (task.BackupType)
        {
            case BackupTypes.Full:
                _full.Checked = true;
                break;
            case BackupTypes.Differential:
                _differential.Checked = true;
                break;
            case BackupTypes.Mirror:
                _mirror.Checked = true;
                break;
            default:
                _incremental.Checked = true;
                break;
        }

        var force = task.ForceFull;
        _forceEvery.Checked = force.EveryRuns > 0;
        _forceEveryRuns.Value = Math.Clamp(force.EveryRuns > 0 ? force.EveryRuns : 7, (int)_forceEveryRuns.Minimum, (int)_forceEveryRuns.Maximum);
        _forceWeekday.Checked = force.Weekday is >= 1 and <= 7;
        _weekday.SelectedIndex = (force.Weekday is >= 1 and <= 7 ? force.Weekday.Value : 7) - 1;
        _forceMonthDay.Checked = force.MonthDay is (>= 1 and <= 31) or (-1);
        _monthDay.SelectedIndex = force.MonthDay switch
        {
            -1 => 31,
            >= 1 and <= 31 => force.MonthDay.Value - 1,
            _ => 0,
        };
        _comment.Text = task.Comment;
        _forceEveryRuns.Enabled = _forceEvery.Checked;
        _weekday.Enabled = _forceWeekday.Checked;
        _monthDay.Enabled = _forceMonthDay.Checked;
        UpdateType();
    }

    public override string? SaveTo(TaskDefinition task)
    {
        string name = _name.Text.Trim();
        if (name.Length == 0)
        {
            return Strings.ErrorNameRequired;
        }

        task.Name = name;
        task.Enabled = _enabled.Checked;
        task.BackupType = _full.Checked ? BackupTypes.Full
            : _differential.Checked ? BackupTypes.Differential
            : _mirror.Checked ? BackupTypes.Mirror
            : BackupTypes.Incremental;
        task.MirrorMode = task.BackupType == BackupTypes.Mirror ? _mirrorSafe.Checked ? MirrorModes.Safe : MirrorModes.Exact : null;
        if (task.BackupType == BackupTypes.Mirror && _mirrorSafe.Checked)
        {
            task.Retention.SafeMirrorDays = (int)_safeDays.Value;
        }

        task.ForceFull.EveryRuns = _forceEvery.Checked ? (int)_forceEveryRuns.Value : 0;
        task.ForceFull.Weekday = _forceWeekday.Checked ? _weekday.SelectedIndex + 1 : null;
        task.ForceFull.MonthDay = _forceMonthDay.Checked ? (_monthDay.SelectedIndex == 31 ? -1 : _monthDay.SelectedIndex + 1) : null;
        task.Comment = _comment.Text.Trim();
        return null;
    }

    private static RadioButton Radio(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };

    private static FlowLayoutPanel Inline(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        panel.Controls.AddRange(controls);
        return panel;
    }

    private void UpdateType()
    {
        _forceGroup.Enabled = !_full.Checked && !_mirror.Checked;
        _mirrorOptions.Enabled = _mirror.Checked;
        _safeDays.Enabled = _mirror.Checked && _mirrorSafe.Checked;
        string type = _full.Checked ? BackupTypes.Full : _differential.Checked ? BackupTypes.Differential : _mirror.Checked ? BackupTypes.Mirror : BackupTypes.Incremental;
        Context.SetShape(taskName: _name.Text.Trim(), backupType: type);
    }
}

/// <summary>
/// "Sources": folders, files and network folders with their accounts, and under them the filters of the task: masks of
/// files to skip, folders to skip (typed or picked with "Browse…"), hidden, system and temporary files, and "only
/// files" (section 6.2).
/// </summary>
internal sealed class SourcesPage : EditorPage
{
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        Height = 160,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
    };

    private readonly Button _add = new() { Text = Strings.ButtonAdd, AutoSize = true, MinimumSize = new Size(100, 0) };
    private readonly Button _addLinux = new() { Text = Strings.SourceAddLinux, AutoSize = true, MinimumSize = new Size(100, 0) };
    private readonly Button _edit = new() { Text = Strings.ButtonEdit, AutoSize = true, MinimumSize = new Size(100, 0) };
    private readonly Button _remove = new() { Text = Strings.ButtonRemove, AutoSize = true, MinimumSize = new Size(100, 0) };
    private readonly Label _remoteNote;
    private readonly Control[] _fileOptions;
    private readonly CheckBox _keepTopDir = new() { Text = Strings.KeepTopDir, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
    private readonly TextBox _exclude = new() { Dock = DockStyle.Fill };

    // One folder per line; Enter starts a new line instead of pressing OK.
    private readonly TextBox _excludeDirs = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        AcceptsReturn = true,
        ScrollBars = ScrollBars.Vertical,
        Height = 76,
        Margin = new Padding(0),
    };

    private readonly Button _browseDirs = new() { Text = Strings.ButtonBrowse, AutoSize = true, MinimumSize = new Size(100, 0), Margin = new Padding(8, 0, 0, 0) };
    private readonly CheckBox _skipHidden = new() { Text = Strings.SkipHidden, AutoSize = true };
    private readonly CheckBox _skipSystem = new() { Text = Strings.SkipSystem, AutoSize = true };
    private readonly CheckBox _skipTemp = new() { Text = Strings.SkipTemp, AutoSize = true };
    private readonly TextBox _include = new() { Dock = DockStyle.Fill };
    private readonly List<TaskSource> _sources = [];

    public SourcesPage(EditorContext context)
        : base(context, Strings.PageSources, Glyph.Folder)
    {
        _list.Columns.Add(Strings.ColumnPath, 330);
        _list.Columns.Add(Strings.ColumnSubfolders, 90);
        _list.Columns.Add(Strings.ColumnAccount, 150);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(8, 0, 0, 0) };
        buttons.Controls.AddRange([_add, _addLinux, _edit, _remove]);
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(_list, 0, 0);
        row.Controls.Add(buttons, 1, 0);

        AddNote(Strings.SourcesNote);
        AddWide(row);
        _remoteNote = AddNote(Strings.SourcesRemoteNote);
        _remoteNote.Visible = false;
        AddWide(_keepTopDir, Strings.KeepTopDirHelp);

        var dirs = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        dirs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        dirs.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        dirs.Controls.Add(_excludeDirs, 0, 0);
        dirs.Controls.Add(_browseDirs, 1, 0);

        AddHeading(Strings.FiltersHeading);
        AddNote(Strings.FiltersNote);
        AddRow(Strings.FilterExclude, _exclude, Strings.FilterExcludeHelp);
        AddRow(Strings.FilterExcludeDirs, dirs, Strings.FilterExcludeDirsHelp, alignTop: true);
        AddWide(_skipHidden);
        AddWide(_skipSystem);
        AddWide(_skipTemp, Strings.SkipTempHelp);
        AddRow(Strings.FilterInclude, _include, Strings.FilterIncludeHelp);
        _fileOptions = [_keepTopDir, _exclude, _excludeDirs, _browseDirs, _skipHidden, _skipSystem, _skipTemp, _include];

        _browseDirs.Click += (_, _) => BrowseExcludedFolder();
        _add.Click += (_, _) => AddSource();
        _addLinux.Click += (_, _) => AddLinuxServer();
        _edit.Click += (_, _) => EditSource();
        _remove.Click += (_, _) => RemoveSource();
        _list.ItemActivate += (_, _) => EditSource();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        Context.CredentialsChanged += (_, _) => Fill();
    }

    public override void LoadFrom(TaskDefinition task)
    {
        _sources.Clear();
        _sources.AddRange(task.Sources);
        _keepTopDir.Checked = task.Layout.KeepTopDir;
        var filters = task.Filters;
        _exclude.Text = string.Join("; ", filters.Exclude);
        _excludeDirs.Text = string.Join(Environment.NewLine, filters.ExcludeDirs);
        _skipHidden.Checked = filters.SkipHidden;
        _skipSystem.Checked = filters.SkipSystem;
        _skipTemp.Checked = filters.SkipTemp;
        _include.Text = string.Join("; ", filters.Include);
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
        task.Layout.KeepTopDir = _keepTopDir.Checked;
        var filters = task.Filters;
        filters.Exclude = Masks(_exclude.Text);
        filters.ExcludeDirs = Masks(_excludeDirs.Text);
        filters.SkipHidden = _skipHidden.Checked;
        filters.SkipSystem = _skipSystem.Checked;
        filters.SkipTemp = _skipTemp.Checked;
        filters.Include = Masks(_include.Text);
        return null;
    }

    /// <summary>Masks separated by semicolons or line breaks, without repeats.</summary>
    internal static List<string> Masks(string text) =>
        [.. text.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    private void Fill()
    {
        int selected = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
        var accounts = Context.Credentials;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var source in _sources)
        {
            if (source.Kind == SourceKinds.Ssh)
            {
                string key = Context.FindCredential(source.CredentialId)?.DisplayName ?? Strings.AccountUnknown;
                _list.Items.Add(new ListViewItem([source.Path, "—", key]));
                continue;
            }

            string account = source.CredentialId is { } id
                ? accounts.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? Strings.AccountUnknown
                : source.Kind == SourceKinds.Unc ? Strings.AccountService : string.Empty;
            _list.Items.Add(new ListViewItem([source.Path, source.Kind == SourceKinds.File ? "—" : source.Recursive ? Strings.Yes : Strings.No, account]));
        }

        _list.EndUpdate();
        if (selected >= 0 && selected < _list.Items.Count)
        {
            _list.Items[selected].Selected = true;
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool any = _list.SelectedIndices.Count > 0;
        _edit.Enabled = any;
        _remove.Enabled = any;

        // A Linux server is the only source of its task (CUSTOS-remote-pull.md 4.1); the filters are custos-helper's.
        bool remote = _sources.Any(s => s.Kind == SourceKinds.Ssh);
        _add.Enabled = !remote;
        _addLinux.Enabled = _sources.Count == 0;
        _remoteNote.Visible = remote;
        foreach (var control in _fileOptions)
        {
            control.Enabled = !remote;
        }

        Context.HasRemoteSource = remote;
    }

    private void AddLinuxServer()
    {
        using var dialog = new RemoteSourceDialog(Context, new TaskSource());
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
        {
            _sources.Add(dialog.Source);
            Fill();
            _list.Items[^1].Selected = true;
        }
    }

    private void AddSource()
    {
        using var dialog = new SourceDialog(Context, new TaskSource());
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
        {
            _sources.Add(dialog.Source);
            Fill();
            _list.Items[^1].Selected = true;
        }
    }

    private void EditSource()
    {
        if (_list.SelectedIndices.Count == 0)
        {
            return;
        }

        int index = _list.SelectedIndices[0];
        if (_sources[index].Kind == SourceKinds.Ssh)
        {
            using var remote = new RemoteSourceDialog(Context, _sources[index]);
            if (remote.ShowDialog(FindForm()) == DialogResult.OK)
            {
                _sources[index] = remote.Source;
                Fill();
            }

            return;
        }

        using var dialog = new SourceDialog(Context, _sources[index]);
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
        {
            _sources[index] = dialog.Source;
            Fill();
        }
    }

    private void RemoveSource()
    {
        if (_list.SelectedIndices.Count == 0)
        {
            return;
        }

        _sources.RemoveAt(_list.SelectedIndices[0]);
        Fill();
    }

    /// <summary>Adds a folder picked like a source folder as a line of "Skip folders"; it starts in the first source.</summary>
    private void BrowseExcludedFolder()
    {
        using var dialog = new FolderBrowserDialog { ShowNewFolderButton = false, UseDescriptionForTitle = true, Description = Strings.FilterExcludeDirsBrowse };
        string? start = _sources.Where(s => s.Kind != SourceKinds.File).Select(s => s.Path).FirstOrDefault(Directory.Exists);
        if (start is not null)
        {
            dialog.InitialDirectory = start;
        }

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        AddExcludedFolder(dialog.SelectedPath);
    }

    /// <summary>Adds a line to "Skip folders" unless it is there already.</summary>
    internal void AddExcludedFolder(string path)
    {
        var lines = Masks(_excludeDirs.Text);
        if (lines.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        lines.Add(path);
        _excludeDirs.Text = string.Join(Environment.NewLine, lines);
        _excludeDirs.SelectionStart = _excludeDirs.TextLength;
        _excludeDirs.ScrollToCaret();
    }
}

/// <summary>
/// "Databases" (section 6.2): the MySQL, MariaDB and PostgreSQL servers on this computer whose databases each run dumps
/// into the folder «db» of the copy, each added and changed in <see cref="DbServerDialog"/>.
/// </summary>
internal sealed class DatabasesPage : EditorPage
{
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        Height = 160,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
    };

    private readonly Button _add = new() { Text = Strings.ButtonAdd, AutoSize = true, MinimumSize = new Size(100, 0) };
    private readonly Button _edit = new() { Text = Strings.ButtonEdit, AutoSize = true, MinimumSize = new Size(100, 0) };
    private readonly Button _remove = new() { Text = Strings.ButtonRemove, AutoSize = true, MinimumSize = new Size(100, 0) };
    private readonly List<TaskDatabaseServer> _servers = [];
    private readonly Label _remoteNote;

    public DatabasesPage(EditorContext context)
        : base(context, Strings.PageDatabases, Glyph.Database)
    {
        _list.Columns.Add(Strings.ColumnServer, 200);
        _list.Columns.Add(Strings.ColumnDatabases, 160);
        _list.Columns.Add(Strings.ColumnPrograms, 220);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(8, 0, 0, 0) };
        buttons.Controls.AddRange([_add, _edit, _remove]);
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(_list, 0, 0);
        row.Controls.Add(buttons, 1, 0);

        AddNote(Strings.DatabasesNote);
        _remoteNote = AddNote(Strings.DatabasesRemoteNote);
        _remoteNote.ForeColor = Glyphs.WarningText;
        _remoteNote.Visible = false;
        AddWide(row);

        _add.Click += (_, _) => AddServer();
        Context.RemoteChanged += (_, _) => UpdateButtons();
        _edit.Click += (_, _) => EditServer();
        _remove.Click += (_, _) => RemoveServer();
        _list.ItemActivate += (_, _) => EditServer();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
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

    /// <summary>"all", "all but: a, b", "only: c".</summary>
    internal static string DescribeDatabases(TaskDatabaseServer server) =>
        server.AllDatabases
            ? server.Exclude.Count == 0 ? Strings.DbAll : Strings.Format(Strings.DbAllExcept, string.Join(", ", server.Exclude))
            : Strings.Format(Strings.DbOnly, string.Join(", ", server.Include));

    /// <summary>"MySQL 127.0.0.1:3306".</summary>
    internal static string DescribeServer(TaskDatabaseServer server) =>
        string.Concat(DbEngines.Title(server.Engine), " ", server.Host, ":",
            (server.Port ?? DbEngines.DefaultPort(server.Engine)).ToString(CultureInfo.InvariantCulture));

    private void Fill()
    {
        int selected = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var server in _servers)
        {
            _list.Items.Add(new ListViewItem([DescribeServer(server), DescribeDatabases(server), server.BinDir]));
        }

        _list.EndUpdate();
        if (selected >= 0 && selected < _list.Items.Count)
        {
            _list.Items[selected].Selected = true;
        }

        Context.DbServerCount = _servers.Count;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool any = _list.SelectedIndices.Count > 0;
        _edit.Enabled = any;
        _remove.Enabled = any;
        _add.Enabled = !Context.HasRemoteSource;
        _remoteNote.Visible = Context.HasRemoteSource;
    }

    private void AddServer()
    {
        using var dialog = new DbServerDialog(Context, new TaskDatabaseServer(), isNew: true);
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
        {
            _servers.Add(dialog.Server);
            Fill();
            _list.Items[^1].Selected = true;
        }
    }

    private void EditServer()
    {
        if (_list.SelectedIndices.Count == 0)
        {
            return;
        }

        int index = _list.SelectedIndices[0];
        using var dialog = new DbServerDialog(Context, _servers[index], isNew: false);
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
        {
            _servers[index] = dialog.Server;
            Fill();
        }
    }

    private void RemoveServer()
    {
        if (_list.SelectedIndices.Count == 0)
        {
            return;
        }

        _servers.RemoveAt(_list.SelectedIndices[0]);
        Fill();
    }
}

/// <summary>
/// "Destinations": local folders, network folders with an account and USB disks found by their label (section 6.3),
/// added and changed in a dialog of their own as the sources are, and how the copies are laid out (section 6.4): a
/// folder per copy, named by a template picked from a list or typed, with an example of the name it gives.
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

    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        Height = 130,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
    };

    private readonly Button _add = new() { Text = Strings.ButtonAdd, AutoSize = true, MinimumSize = new Size(100, 0) };
    private readonly Button _edit = new() { Text = Strings.ButtonEdit, AutoSize = true, MinimumSize = new Size(100, 0) };
    private readonly Button _remove = new() { Text = Strings.ButtonRemove, AutoSize = true, MinimumSize = new Size(100, 0) };
    private readonly Label _mirrorNote = new() { Text = Strings.LayoutMirrorNote, AutoSize = true, Dock = DockStyle.Fill, ForeColor = Glyphs.WarningText, Margin = new Padding(0, 0, 0, 6), Visible = false };
    private readonly CheckBox _separate = new() { Text = Strings.SeparateFolders, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
    private readonly ComboBox _template = new() { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
    private readonly Label _example = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 6) };
    private readonly List<TaskDestination> _destinations = [];

    public DestinationsPage(EditorContext context)
        : base(context, Strings.PageDestinations, Glyph.Drive)
    {
        _list.Columns.Add(Strings.ColumnDestination, 330);
        _list.Columns.Add(Strings.ColumnType, 120);
        _list.Columns.Add(Strings.ColumnAccount, 150);
        _template.Items.AddRange(TemplatePresets);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(8, 0, 0, 0) };
        buttons.Controls.AddRange([_add, _edit, _remove]);
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(_list, 0, 0);
        row.Controls.Add(buttons, 1, 0);

        AddNote(Strings.DestinationsNote);
        AddWide(row);
        AddHeading(Strings.EditorLayout);
        AddWide(_mirrorNote);
        AddWide(_separate, Strings.SeparateFoldersHelp);
        AddRow(Strings.NameTemplate, _template, Strings.NameTemplateHelp);
        AddWide(_example);

        _add.Click += (_, _) => AddDestination();
        _edit.Click += (_, _) => EditDestination();
        _remove.Click += (_, _) => RemoveDestination();
        _list.ItemActivate += (_, _) => EditDestination();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _separate.CheckedChanged += (_, _) => UpdateLayout();
        _template.TextChanged += (_, _) => ShowExample();
        Context.ShapeChanged += (_, _) => UpdateLayout();
        Context.CredentialsChanged += (_, _) => Fill();
    }

    public override void LoadFrom(TaskDefinition task)
    {
        _destinations.Clear();
        _destinations.AddRange(task.Destinations);
        _separate.Checked = task.Layout.SeparateFolders;
        _template.Text = task.Layout.NameTemplate;
        Fill();
        if (_list.Items.Count > 0)
        {
            _list.Items[0].Selected = true;
        }

        UpdateLayout();
    }

    public override string? SaveTo(TaskDefinition task)
    {
        if (task.Enabled && _destinations.Count == 0)
        {
            return Strings.ErrorNoDestinations;
        }

        // The dialog checks what it saves; this catches tasks that came in from elsewhere (the server, a file).
        for (int i = 0; i < _destinations.Count; i++)
        {
            var destination = _destinations[i];
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
                // The destination with the problem is shown, so that it is clear which one is meant.
                _list.Items[i].Selected = true;
                return problem;
            }
        }

        bool named = UsesTemplate;
        if (named && string.IsNullOrWhiteSpace(_template.Text))
        {
            return Strings.ErrorTemplateRequired;
        }

        task.Destinations = [.. _destinations];
        task.Layout.SeparateFolders = _separate.Checked;
        if (named)
        {
            // An emptied field means the usual name, and says so when the task is opened again.
            string template = _template.Text.Trim();
            task.Layout.NameTemplate = template.Length == 0 ? LayoutOptions.DefaultNameTemplate : template;
        }

        return null;
    }

    /// <summary>The name template is used: a folder per copy, or archives (always named by date); never a mirror.</summary>
    private bool UsesTemplate => !Context.IsMirror && (_separate.Checked || Context.ArchiveFormat != ArchiveFormats.None);

    internal static string KindText(string kind) => kind switch
    {
        DestinationKinds.Local => Strings.KindLocal,
        DestinationKinds.Unc => Strings.KindUnc,
        DestinationKinds.Usb => Strings.KindUsb,
        _ => Strings.Format(Strings.KindLater, kind.ToUpperInvariant()),
    };

    private void Fill()
    {
        int selected = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
        var accounts = Context.Credentials;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var destination in _destinations)
        {
            string account = destination.CredentialId is { } id
                ? accounts.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? Strings.AccountUnknown
                : destination.Kind == DestinationKinds.Unc ? Strings.AccountService : string.Empty;
            _list.Items.Add(new ListViewItem([TaskDescriber.Destination(destination), KindText(destination.Kind), account]));
        }

        _list.EndUpdate();
        if (selected >= 0 && selected < _list.Items.Count)
        {
            _list.Items[selected].Selected = true;
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool any = _list.SelectedIndices.Count > 0;
        _edit.Enabled = any;
        _remove.Enabled = any;
    }

    private void UpdateLayout()
    {
        bool mirror = Context.IsMirror;
        _mirrorNote.Visible = mirror;
        _separate.Enabled = !mirror;
        _template.Enabled = UsesTemplate;
        ShowExample();
    }

    /// <summary>The name the template gives now, so that a typing slip shows before the first run.</summary>
    private void ShowExample()
    {
        if (!UsesTemplate || string.IsNullOrWhiteSpace(_template.Text))
        {
            _example.Text = string.Empty;
            return;
        }

        string name = Context.TaskName.Length > 0 ? Context.TaskName : Strings.NewTaskName;
        string type = Context.BackupType == BackupTypes.Mirror ? BackupTypes.Full : Context.BackupType;
        string example = Custos.Core.Backup.SetNaming.Expand(_template.Text.Trim(), name, DateTime.Now, Environment.MachineName, type);
        example += Context.ArchiveFormat switch
        {
            ArchiveFormats.Zip => ".zip",
            ArchiveFormats.SevenZip => ".7z",
            _ => string.Empty,
        };
        _example.Text = Strings.Format(Strings.NameTemplateExample, example);
    }

    private void AddDestination()
    {
        using var dialog = new DestinationDialog(Context, new TaskDestination { Kind = DestinationKinds.Local }, isNew: true);
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
        {
            _destinations.Add(dialog.Destination);
            Fill();
            _list.Items[^1].Selected = true;
        }
    }

    private void EditDestination()
    {
        if (_list.SelectedIndices.Count == 0)
        {
            return;
        }

        int index = _list.SelectedIndices[0];
        using var dialog = new DestinationDialog(Context, _destinations[index], isNew: false);
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
        {
            _destinations[index] = dialog.Destination;
            Fill();
        }
    }

    private void RemoveDestination()
    {
        if (_list.SelectedIndices.Count == 0)
        {
            return;
        }

        _destinations.RemoveAt(_list.SelectedIndices[0]);
        Fill();
    }
}

/// <summary>"Schedule": daily, weekly, monthly, by interval or manual, with catch-up runs (section 6.7).</summary>
internal sealed class SchedulePage : EditorPage
{
    private static readonly string[] Types = [ScheduleTypes.Daily, ScheduleTypes.Weekly, ScheduleTypes.Monthly, ScheduleTypes.Interval, ScheduleTypes.Manual];

    private readonly ComboBox _type = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly Label _timesLabel = Caption(Strings.ScheduleTimes);
    private readonly TextBox _times = new() { Width = 220 };
    private readonly Label _daysLabel = Caption(Strings.ScheduleWeekdays);
    private readonly FlowLayoutPanel _days = new() { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
    private readonly CheckBox[] _dayBoxes = new CheckBox[7];
    private readonly Label _monthDaysLabel = Caption(Strings.ScheduleMonthDays);
    private readonly TextBox _monthDays = new() { Width = 220 };
    private readonly CheckBox _lastDay = new() { Text = Strings.LastDayOfMonth, AutoSize = true };
    private readonly Label _intervalLabel = Caption(Strings.ScheduleInterval);
    private readonly NumericUpDown _interval = new() { Minimum = 1, Maximum = 1440, Value = 60, Width = 80 };
    private readonly CheckBox _catchUp = new() { Text = Strings.ScheduleCatchUp, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
    private readonly Label _preview = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
    private readonly Label _upcoming = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText };
    private readonly TableLayoutPanel _grid = new() { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
    private bool _loading;

    // The interval as loaded: kept as it was when the schedule is not "by timer".
    private int _loadedInterval;

    public SchedulePage(EditorContext context)
        : base(context, Strings.PageSchedule, Glyph.Clock)
    {
        _type.Items.AddRange([Strings.ScheduleDaily, Strings.ScheduleWeekly, Strings.ScheduleMonthly, Strings.ScheduleByInterval, Strings.ScheduleManual]);
        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        for (int i = 0; i < 7; i++)
        {
            _dayBoxes[i] = new CheckBox { Text = names[(i + 1) % 7], AutoSize = true, Margin = new Padding(0, 3, 10, 3) };
            _days.Controls.Add(_dayBoxes[i]);
            _dayBoxes[i].CheckedChanged += (_, _) => ShowPreview();
        }

        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        AddGridRow(Caption(Strings.ScheduleType), _type);
        AddGridRow(_timesLabel, Inline(_times, Ui.HelpIcon(ToolTip, Strings.ScheduleTimesHelp)));
        AddGridRow(_daysLabel, _days);
        AddGridRow(_monthDaysLabel, Inline(_monthDays, _lastDay, Ui.HelpIcon(ToolTip, Strings.ScheduleMonthDaysHelp)));
        AddGridRow(_intervalLabel, Inline(_interval, new Label { Text = Strings.Minutes, AutoSize = true, Margin = new Padding(4, 6, 0, 0) }));

        AddWide(_grid);
        AddWide(_catchUp, Strings.ScheduleCatchUpHelp);
        AddHeading(Strings.SchedulePreview);
        AddWide(_preview);
        AddWide(_upcoming);
        AddNote(Strings.ScheduleTimeZoneNote);

        _type.SelectedIndexChanged += (_, _) =>
        {
            UpdateFields();
            ShowPreview();
        };
        _times.TextChanged += (_, _) => ShowPreview();
        _monthDays.TextChanged += (_, _) => ShowPreview();
        _lastDay.CheckedChanged += (_, _) => ShowPreview();
        _interval.ValueChanged += (_, _) => ShowPreview();
        _catchUp.CheckedChanged += (_, _) => ShowPreview();
    }

    public override void LoadFrom(TaskDefinition task)
    {
        _loading = true;
        var schedule = task.Schedule;
        int type = Array.IndexOf(Types, schedule.Type);
        _type.SelectedIndex = type < 0 ? 0 : type;
        _times.Text = string.Join(", ", schedule.Times);
        for (int i = 0; i < 7; i++)
        {
            _dayBoxes[i].Checked = schedule.DaysOfWeek.Count == 0 || schedule.DaysOfWeek.Contains(i + 1);
        }

        _monthDays.Text = string.Join(", ", schedule.DaysOfMonth.Where(d => d is >= 1 and <= 31));
        _lastDay.Checked = schedule.DaysOfMonth.Contains(-1);
        _loadedInterval = schedule.IntervalMinutes;
        _interval.Value = Math.Clamp(schedule.IntervalMinutes > 0 ? schedule.IntervalMinutes : 60, 1, 1440);
        _catchUp.Checked = schedule.CatchUp;
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

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) };

    private static FlowLayoutPanel Inline(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        panel.Controls.AddRange(controls);
        return panel;
    }

    private void AddGridRow(Control caption, Control control)
    {
        control.Margin = new Padding(0, 3, 0, 3);
        _grid.Controls.Add(caption);
        _grid.Controls.Add(control);
    }

    private string SelectedType => _type.SelectedIndex >= 0 ? Types[_type.SelectedIndex] : ScheduleTypes.Daily;

    private void UpdateFields()
    {
        string type = SelectedType;
        bool times = type is ScheduleTypes.Daily or ScheduleTypes.Weekly or ScheduleTypes.Monthly;
        bool weekdays = type is ScheduleTypes.Daily or ScheduleTypes.Weekly or ScheduleTypes.Interval;
        _timesLabel.Visible = times;
        _times.Parent!.Visible = times;
        _daysLabel.Visible = weekdays;
        _days.Visible = weekdays;
        _monthDaysLabel.Visible = type == ScheduleTypes.Monthly;
        _monthDays.Parent!.Visible = type == ScheduleTypes.Monthly;
        _intervalLabel.Visible = type == ScheduleTypes.Interval;
        _interval.Parent!.Visible = type == ScheduleTypes.Interval;
        _catchUp.Visible = type != ScheduleTypes.Manual;
    }

    /// <summary>The schedule as typed; with the first problem found, in words.</summary>
    private (ScheduleOptions? Schedule, string? Problem) Read()
    {
        var schedule = new ScheduleOptions
        {
            Type = SelectedType,
            CatchUp = _catchUp.Checked,
            IntervalMinutes = SelectedType == ScheduleTypes.Interval ? (int)_interval.Value : _loadedInterval,
        };

        schedule.Times = [];
        foreach (string part in Split(_times.Text))
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
        schedule.DaysOfWeek = [.. Enumerable.Range(1, 7).Where(d => _dayBoxes[d - 1].Checked)];
        schedule.DaysOfMonth = [];
        foreach (string part in Split(_monthDays.Text))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.CurrentCulture, out int day) || day is < 1 or > 31)
            {
                return (null, Strings.Format(Strings.ErrorBadMonthDay, part));
            }

            if (!schedule.DaysOfMonth.Contains(day))
            {
                schedule.DaysOfMonth.Add(day);
            }
        }

        schedule.DaysOfMonth.Sort();
        if (_lastDay.Checked)
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

    private static string[] Split(string text) =>
        text.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void ShowPreview()
    {
        if (_loading)
        {
            return;
        }

        var (schedule, problem) = Read();
        if (schedule is null)
        {
            _preview.ForeColor = Glyphs.WarningText;
            _preview.Text = problem;
            _upcoming.Text = string.Empty;
            return;
        }

        _preview.ForeColor = SystemColors.ControlText;
        _preview.Text = TaskDescriber.ScheduleWithCatchUp(schedule);
        var next = ScheduleCalculator.Upcoming(schedule, DateTimeOffset.UtcNow, TimeZoneInfo.Local, 3);
        _upcoming.Text = next.Count == 0 ? string.Empty : Strings.Format(Strings.ScheduleUpcoming, string.Join(", ", next.Select(t => Ui.Time(t))));
    }
}

/// <summary>"Archive": format, compression level, volumes, encryption and verification (section 6.5).</summary>
internal sealed class ArchivePage : EditorPage
{
    // Volume sizes in MB as 7-Zip offers them: CD, the FAT32 limit, DVD.
    private static readonly int[] VolumePresets = [700, 4095, 4480];

    private readonly RadioButton _none = new() { Text = Strings.ArchiveNone, AutoSize = true };
    private readonly RadioButton _zip = new() { Text = Strings.ArchiveZip, AutoSize = true };
    private readonly RadioButton _sevenZip = new() { Text = Strings.ArchiveSevenZip, AutoSize = true };
    private readonly Label _sevenZipProblem = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Glyphs.WarningText, Margin = new Padding(18, 0, 0, 6), Visible = false };
    private readonly Label _mirrorNote = new() { Text = Strings.ArchiveMirrorNote, AutoSize = true, Dock = DockStyle.Fill, ForeColor = Glyphs.WarningText, Margin = new Padding(0, 0, 0, 6), Visible = false };
    private readonly ComboBox _level = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly CheckBox _split = new() { Text = Strings.ArchiveSplit, AutoSize = true };
    private readonly ComboBox _volume = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly NumericUpDown _volumeMb = new() { Minimum = 1, Maximum = 1_000_000, Value = 1024, Width = 90 };
    private readonly CheckBox _encrypt = new() { Text = Strings.ArchiveEncrypt, AutoSize = true };
    private readonly Button _setPassword = new() { Text = Strings.ArchiveSetPassword, AutoSize = true };
    private readonly Label _passwordState = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 6, 0, 6) };
    private readonly CheckBox _encryptNames = new() { Text = Strings.ArchiveEncryptNames, AutoSize = true };
    private readonly NumericUpDown _verifyRuns = new() { Minimum = 0, Maximum = 1000, Width = 70 };
    private readonly NumericUpDown _verifyDays = new() { Minimum = 0, Maximum = 3650, Width = 70 };
    private string? _passwordId;
    private bool _passwordSaved;
    private string _taskName = string.Empty;

    public ArchivePage(EditorContext context)
        : base(context, Strings.PageArchive, Glyph.Box)
    {
        for (int level = 0; level <= 9; level++)
        {
            _level.Items.Add(level switch
            {
                0 => Strings.ArchiveLevel0,
                1 => Strings.ArchiveLevel1,
                5 => Strings.ArchiveLevel5,
                9 => Strings.ArchiveLevel9,
                _ => level.ToString(System.Globalization.CultureInfo.CurrentCulture),
            });
        }

        _volume.Items.AddRange([Strings.ArchiveVolume700, Strings.ArchiveVolumeFat32, Strings.ArchiveVolumeDvd, Strings.ArchiveVolumeCustom]);

        AddHeading(Strings.ArchiveFormat);
        AddWide(_mirrorNote);
        AddWide(_none);
        AddWide(_zip, Strings.ArchiveZipHelp);
        AddWide(_sevenZip, Strings.ArchiveSevenZipHelp);
        AddWide(_sevenZipProblem);
        AddNote(Strings.ArchiveOpenAnywhere);
        AddRow(Strings.ArchiveLevel, _level, Strings.ArchiveLevelHelp);

        var volumes = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        volumes.Controls.AddRange([_split, _volume, _volumeMb, new Label { Text = Strings.ArchiveMb, AutoSize = true, Margin = new Padding(4, 8, 0, 0) }]);
        AddWide(volumes, Strings.ArchiveSplitHelp);

        AddHeading(Strings.ArchiveEncryption);
        var password = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        password.Controls.AddRange([_encrypt, _setPassword, _passwordState]);
        AddWide(password);
        AddWide(_encryptNames, Strings.ArchiveEncryptNamesHelp, 18);
        AddNote(Strings.ArchivePasswordWarning, 18);

        AddHeading(Strings.ArchiveVerify);
        AddRow(Strings.VerifyEveryRuns, _verifyRuns, Strings.VerifyEveryRunsHelp);
        AddRow(Strings.VerifyStoredDays, _verifyDays, Strings.VerifyStoredDaysHelp);

        foreach (var control in new Control[] { _none, _zip, _sevenZip, _split, _encrypt })
        {
            switch (control)
            {
                case RadioButton radio:
                    radio.CheckedChanged += (_, _) => UpdateControls();
                    break;
                case CheckBox box:
                    box.CheckedChanged += (_, _) => UpdateControls();
                    break;
            }
        }

        _volume.SelectedIndexChanged += (_, _) => UpdateControls();
        _setPassword.Click += (_, _) => SetPassword();
        Context.ShapeChanged += (_, _) => UpdateControls();
    }

    public override void Shown()
    {
        string? problem = Context.Monitor.Snapshot?.SevenZipProblem;
        _sevenZipProblem.Text = problem is null ? string.Empty : Strings.Format(Strings.ArchiveSevenZipProblem, problem);
        _sevenZipProblem.Visible = problem is not null;
    }

    public override void LoadFrom(TaskDefinition task)
    {
        var archive = task.Archive;
        _taskName = task.Name;
        _none.Checked = archive.Format is not (ArchiveFormats.Zip or ArchiveFormats.SevenZip);
        _zip.Checked = archive.Format == ArchiveFormats.Zip;
        _sevenZip.Checked = archive.Format == ArchiveFormats.SevenZip;
        _level.SelectedIndex = Math.Clamp(archive.Level, 0, 9);
        _split.Checked = archive.SplitMb > 0;
        int preset = Array.IndexOf(VolumePresets, archive.SplitMb);
        _volume.SelectedIndex = archive.SplitMb <= 0 ? 0 : preset >= 0 ? preset : VolumePresets.Length;
        _volumeMb.Value = archive.SplitMb > 0 ? Math.Clamp(archive.SplitMb, 1, 1_000_000) : 1024;
        _encrypt.Checked = archive.Encrypt;
        _encryptNames.Checked = archive.EncryptNames;
        _passwordId = archive.PasswordCredentialId;
        _verifyRuns.Value = Math.Clamp(task.Verify.EveryRuns, 0, 1000);
        _verifyDays.Value = Math.Clamp(task.Verify.StoredEveryDays, 0, 3650);
        ShowPasswordState();
        UpdateControls();
    }

    public override string? SaveTo(TaskDefinition task)
    {
        var archive = task.Archive;
        archive.Format = Context.IsMirror ? ArchiveFormats.None : SelectedFormat;
        archive.Level = Math.Max(0, _level.SelectedIndex);
        archive.SplitMb = _sevenZip.Checked && _split.Checked
            ? _volume.SelectedIndex < VolumePresets.Length ? VolumePresets[Math.Max(0, _volume.SelectedIndex)] : (int)_volumeMb.Value
            : 0;
        bool packed = archive.Format != ArchiveFormats.None;
        archive.Encrypt = packed && _encrypt.Checked;
        archive.EncryptNames = archive.Encrypt && _sevenZip.Checked && _encryptNames.Checked;
        archive.PasswordCredentialId = archive.Encrypt ? _passwordId : archive.PasswordCredentialId;
        task.Verify.EveryRuns = (int)_verifyRuns.Value;
        task.Verify.StoredEveryDays = (int)_verifyDays.Value;

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

    private string SelectedFormat => _zip.Checked ? ArchiveFormats.Zip : _sevenZip.Checked ? ArchiveFormats.SevenZip : ArchiveFormats.None;

    private void UpdateControls()
    {
        // A mirror keeps plain files: the format is "none" whatever is ticked.
        bool mirror = Context.IsMirror;
        _mirrorNote.Visible = mirror;
        _none.Enabled = _zip.Enabled = _sevenZip.Enabled = !mirror;
        Context.SetShape(archiveFormat: mirror ? ArchiveFormats.None : SelectedFormat);
        bool packed = !_none.Checked && !mirror;
        _level.Enabled = packed;
        _split.Enabled = _sevenZip.Checked && packed;
        _volume.Enabled = _split.Enabled && _split.Checked;
        _volumeMb.Enabled = _volume.Enabled && _volume.SelectedIndex == VolumePresets.Length;
        _encrypt.Enabled = packed;
        _setPassword.Enabled = packed && _encrypt.Checked;
        _passwordState.Visible = packed && _encrypt.Checked;
        _encryptNames.Enabled = _sevenZip.Checked && _encrypt.Checked && packed;
    }

    /// <summary>A password is stored for the task, or was just set in this editor.</summary>
    private bool HasPassword() => _passwordId is { } id && (_passwordSaved
        || Context.Monitor.Snapshot?.Credentials.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) is { HasSecret: true });

    private void ShowPasswordState()
    {
        bool set = HasPassword();
        _passwordState.Text = set ? Strings.ArchivePasswordSet : Strings.ArchivePasswordNotSet;
        _passwordState.ForeColor = set ? SystemColors.GrayText : Glyphs.WarningText;
    }

    private void SetPassword()
    {
        using var dialog = new ArchivePasswordDialog(Context.Monitor, _passwordId, _taskName);
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK && dialog.SavedId is { } id)
        {
            _passwordId = id;
            _passwordSaved = true;
            ShowPasswordState();
        }
    }
}

/// <summary>"Retention": by count of full copies with their chains, by age, or grandfather-father-son (section 6.6).</summary>
internal sealed class RetentionPage : EditorPage
{
    private readonly RadioButton _keepAll = new() { Text = Strings.RetentionNone, AutoSize = true };
    private readonly RadioButton _byCount = new() { Text = Strings.RetentionCount, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly NumericUpDown _keepFull = new() { Minimum = 1, Maximum = 1000, Value = 12, Width = 70 };
    private readonly RadioButton _byAge = new() { Text = Strings.RetentionAge, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly NumericUpDown _maxAge = new() { Minimum = 1, Maximum = 3650, Value = 30, Width = 70 };
    private readonly RadioButton _gfs = new() { Text = Strings.RetentionGfs, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly NumericUpDown _daily = new() { Minimum = 0, Maximum = 1000, Value = 7, Width = 60 };
    private readonly NumericUpDown _weekly = new() { Minimum = 0, Maximum = 1000, Value = 4, Width = 60 };
    private readonly NumericUpDown _monthly = new() { Minimum = 0, Maximum = 1000, Value = 12, Width = 60 };
    private readonly FlowLayoutPanel _gfsCounts = new() { AutoSize = true, WrapContents = true, Margin = new Padding(18, 0, 0, 0) };
    private readonly Label _mirrorNote = new() { Text = Strings.RetentionMirrorNote, AutoSize = true, Dock = DockStyle.Fill, ForeColor = Glyphs.WarningText, Margin = new Padding(0, 0, 0, 6), Visible = false };

    public RetentionPage(EditorContext context)
        : base(context, Strings.PageRetention, Glyph.Layers)
    {
        AddWide(_mirrorNote);
        AddWide(_keepAll);
        AddWide(Line(_byCount, _keepFull, Strings.RetentionCountSuffix));
        AddNote(Strings.RetentionCountHint, 18);
        AddWide(Line(_byAge, _maxAge, Strings.RetentionAgeSuffix));
        AddNote(Strings.RetentionAgeHint, 18);
        AddWide(_gfs);
        _gfsCounts.Controls.AddRange([Caption(Strings.GfsDaily), _daily, Caption(Strings.GfsWeekly), _weekly, Caption(Strings.GfsMonthly), _monthly]);
        AddWide(_gfsCounts);
        AddNote(Strings.RetentionGfsHint, 18);
        AddHeading(Strings.RetentionRules);
        AddNote(Strings.RetentionRulesText);

        // The radio buttons sit in different panels, so they are kept exclusive by hand.
        RadioButton[] modes = [_keepAll, _byCount, _byAge, _gfs];
        foreach (var mode in modes)
        {
            mode.CheckedChanged += (_, _) =>
            {
                if (mode.Checked)
                {
                    foreach (var other in modes.Where(m => m != mode))
                    {
                        other.Checked = false;
                    }
                }

                UpdateEnabled();
            };
        }

        Context.ShapeChanged += (_, _) => UpdateEnabled();
    }

    public override void LoadFrom(TaskDefinition task)
    {
        var retention = task.Retention;
        _keepFull.Value = Math.Clamp(retention.KeepFull, 1, 1000);
        _maxAge.Value = retention.MaxAgeDays > 0 ? Math.Clamp(retention.MaxAgeDays, 1, 3650) : 30;
        _daily.Value = Math.Clamp(retention.Gfs.Daily, 0, 1000);
        _weekly.Value = Math.Clamp(retention.Gfs.Weekly, 0, 1000);
        _monthly.Value = Math.Clamp(retention.Gfs.Monthly, 0, 1000);
        _keepAll.Checked = retention.Mode is not (RetentionModes.Count or RetentionModes.Age or RetentionModes.Gfs);
        _byCount.Checked = retention.Mode == RetentionModes.Count;
        _byAge.Checked = retention.Mode == RetentionModes.Age;
        _gfs.Checked = retention.Mode == RetentionModes.Gfs;
        UpdateEnabled();
    }

    public override string? SaveTo(TaskDefinition task)
    {
        if (_gfs.Checked && _daily.Value + _weekly.Value + _monthly.Value == 0)
        {
            return Strings.RetentionGfsEmpty;
        }

        var retention = task.Retention;
        retention.Mode = _byCount.Checked ? RetentionModes.Count : _byAge.Checked ? RetentionModes.Age : _gfs.Checked ? RetentionModes.Gfs : RetentionModes.None;
        retention.KeepFull = (int)_keepFull.Value;
        if (_byAge.Checked)
        {
            // Unset (0) shows as 30 days; it is written only when this mode is chosen.
            retention.MaxAgeDays = (int)_maxAge.Value;
        }

        retention.Gfs.Daily = (int)_daily.Value;
        retention.Gfs.Weekly = (int)_weekly.Value;
        retention.Gfs.Monthly = (int)_monthly.Value;
        return null;
    }

    private static FlowLayoutPanel Line(RadioButton radio, NumericUpDown number, string suffix)
    {
        var line = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        line.Controls.AddRange([radio, number, new Label { Text = suffix, AutoSize = true, Margin = new Padding(4, 8, 0, 0) }]);
        return line;
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 6, 4, 0) };

    private void UpdateEnabled()
    {
        bool mirror = Context.IsMirror;
        _mirrorNote.Visible = mirror;
        _keepAll.Enabled = _byCount.Enabled = _byAge.Enabled = _gfs.Enabled = !mirror;
        _keepFull.Enabled = _byCount.Checked && !mirror;
        _maxAge.Enabled = _byAge.Checked && !mirror;
        _gfsCounts.Enabled = _gfs.Checked && !mirror;
    }
}

/// <summary>
/// "Events": programs, scripts and services before and after the backup, whether a failed event before stops the
/// task, and what to do with the computer when the run is over (section 6.9).
/// </summary>
internal sealed class EventsPage : EditorPage
{
    private static readonly string[] AfterValues = [AfterActions.None, AfterActions.Shutdown, AfterActions.Restart, AfterActions.Sleep, AfterActions.Hibernate];

    private readonly EventList _pre;
    private readonly EventList _post;
    private readonly CheckBox _abortOnPre = new() { Text = Strings.EventsAbortOnPre, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
    private readonly ComboBox _after = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private string _unknownAfter = AfterActions.None;

    public EventsPage(EditorContext context)
        : base(context, Strings.PageEvents, Glyph.Bolt)
    {
        _pre = new EventList(context);
        _post = new EventList(context);
        _after.Items.AddRange([Strings.AfterNone, Strings.AfterShutdown, Strings.AfterRestart, Strings.AfterSleep, Strings.AfterHibernate]);

        AddNote(Strings.EventsNote);
        AddHeading(Strings.EventsPreHeading);
        AddWide(_pre);
        AddWide(_abortOnPre);
        AddHeading(Strings.EventsPostHeading);
        AddWide(_post);
        AddNote(Strings.EventsPostNote);
        AddHeading(Strings.AfterHeading);
        AddWide(_after);
        AddNote(Strings.AfterNote);
    }

    public override void LoadFrom(TaskDefinition task)
    {
        var events = task.Events;
        _pre.Load(events.Pre);
        _post.Load(events.Post);
        _abortOnPre.Checked = events.AbortOnPreFailure;
        int index = Array.IndexOf(AfterValues, events.After);
        _unknownAfter = events.After;
        _after.SelectedIndex = Math.Max(0, index);
    }

    public override string? SaveTo(TaskDefinition task)
    {
        var events = task.Events;
        events.Pre = _pre.Items;
        events.Post = _post.Items;
        events.AbortOnPreFailure = _abortOnPre.Checked;
        string after = AfterValues[Math.Max(0, _after.SelectedIndex)];
        // A value of a later version stays unless something else was picked.
        events.After = after == AfterActions.None && Array.IndexOf(AfterValues, _unknownAfter) < 0 ? _unknownAfter : after;
        return null;
    }

    /// <summary>One list of events with Add, Edit, Remove, Up and Down.</summary>
    private sealed class EventList : TableLayoutPanel
    {
        private readonly EditorContext _context;
        private readonly ListView _list = new()
        {
            Dock = DockStyle.Fill,
            Height = 110,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
        };

        private readonly Button _add = new() { Text = Strings.ButtonAdd, AutoSize = true, MinimumSize = new Size(100, 0) };
        private readonly Button _edit = new() { Text = Strings.ButtonEdit, AutoSize = true, MinimumSize = new Size(100, 0) };
        private readonly Button _remove = new() { Text = Strings.ButtonRemove, AutoSize = true, MinimumSize = new Size(100, 0) };
        private readonly Button _up = new() { Text = Strings.ButtonUp, AutoSize = true, MinimumSize = new Size(100, 0) };
        private readonly Button _down = new() { Text = Strings.ButtonDown, AutoSize = true, MinimumSize = new Size(100, 0) };
        private readonly List<TaskEvent> _events = [];

        public EventList(EditorContext context)
        {
            _context = context;
            Dock = DockStyle.Fill;
            AutoSize = true;
            ColumnCount = 2;
            Margin = new Padding(0);
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _list.Columns.Add(Strings.ColumnEvent, 420);
            _list.Columns.Add(Strings.ColumnTimeout, 110);
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(8, 0, 0, 0) };
            buttons.Controls.AddRange([_add, _edit, _remove, _up, _down]);
            Controls.Add(_list, 0, 0);
            Controls.Add(buttons, 1, 0);

            _add.Click += (_, _) => AddEvent();
            _edit.Click += (_, _) => EditEvent();
            _list.ItemActivate += (_, _) => EditEvent();
            _remove.Click += (_, _) => RemoveEvent();
            _up.Click += (_, _) => Shift(-1);
            _down.Click += (_, _) => Shift(1);
            _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        }

        public List<TaskEvent> Items => [.. _events];

        public void Load(List<TaskEvent> events)
        {
            _events.Clear();
            _events.AddRange(events);
            Fill();
        }

        /// <summary>The event in words, for the list.</summary>
        internal static string Describe(TaskEvent item) => item.Kind switch
        {
            TaskEventKinds.ServiceStop => Strings.Format(Strings.EventShowStop, item.Service),
            TaskEventKinds.ServiceStart => Strings.Format(Strings.EventShowStart, item.Service),
            _ => string.IsNullOrWhiteSpace(item.Args) ? item.Command ?? string.Empty : $"{item.Command} {item.Args}",
        };

        private int Selected => _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;

        private void Fill(int select = -1)
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var item in _events)
            {
                int minutes = Math.Max(1, (item.TimeoutS + 59) / 60);
                _list.Items.Add(new ListViewItem([Describe(item), Strings.Format(Strings.EventTimeoutShown, minutes)]));
            }

            _list.EndUpdate();
            if (select >= 0 && select < _list.Items.Count)
            {
                _list.Items[select].Selected = true;
            }

            UpdateButtons();
        }

        private void UpdateButtons()
        {
            int index = Selected;
            _edit.Enabled = _remove.Enabled = index >= 0;
            _up.Enabled = index > 0;
            _down.Enabled = index >= 0 && index < _events.Count - 1;
        }

        private void AddEvent()
        {
            using var dialog = new EventDialog(_context, new TaskEvent(), isNew: true);
            if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            {
                _events.Add(dialog.Event);
                Fill(_events.Count - 1);
            }
        }

        private void EditEvent()
        {
            int index = Selected;
            if (index < 0)
            {
                return;
            }

            using var dialog = new EventDialog(_context, _events[index], isNew: false);
            if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            {
                _events[index] = dialog.Event;
                Fill(index);
            }
        }

        private void RemoveEvent()
        {
            int index = Selected;
            if (index >= 0)
            {
                _events.RemoveAt(index);
                Fill(Math.Min(index, _events.Count - 1));
            }
        }

        private void Shift(int step)
        {
            int index = Selected;
            int other = index + step;
            if (index < 0 || other < 0 || other >= _events.Count)
            {
                return;
            }

            (_events[index], _events[other]) = (_events[other], _events[index]);
            Fill(other);
        }
    }
}

/// <summary>"Advanced": shadow copies (VSS), read retries, log detail and priority in the queue.</summary>
internal sealed class AdvancedPage : EditorPage
{
    private static readonly string[] Levels = [LogLevels.Inherit, LogLevels.Summary, LogLevels.Files];

    private readonly CheckBox _vss = new() { Text = Strings.VssEnabled, AutoSize = true };
    private readonly RadioButton _vssAbort = new() { Text = Strings.VssAbort, AutoSize = true };
    private readonly RadioButton _vssContinue = new() { Text = Strings.VssContinue, AutoSize = true };
    private readonly FlowLayoutPanel _vssFailure = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(18, 0, 0, 0) };
    private readonly NumericUpDown _retries = new() { Minimum = 0, Maximum = 5, Value = 1, Width = 60 };
    private readonly CheckBox _acl = new() { Text = Strings.PreserveAcl, AutoSize = true };
    private readonly ComboBox _logLevel = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly NumericUpDown _priority = new() { Minimum = -100, Maximum = 100, Value = 0, Width = 70 };

    public AdvancedPage(EditorContext context)
        : base(context, Strings.PageAdvanced, Glyph.Sliders)
    {
        _logLevel.Items.AddRange([Strings.LogLevelInherit, Strings.LogLevelSummary, Strings.LogLevelFiles]);
        _vssFailure.Controls.AddRange([new Label { Text = Strings.VssOnFailure, AutoSize = true, Margin = new Padding(0, 4, 0, 2) }, _vssAbort, _vssContinue]);

        AddHeading(Strings.EditorVss);
        AddWide(_vss, Strings.VssHelp);
        AddWide(_vssFailure);
        AddHeading(Strings.EditorReading);
        AddRow(Strings.ReadRetries, _retries, Strings.ReadRetriesHelp);
        AddWide(_acl, Strings.PreserveAclHelp);
        AddHeading(Strings.EditorLogAndQueue);
        AddRow(Strings.LogLevel, _logLevel);
        AddRow(Strings.Priority, _priority, Strings.PriorityHelp);

        _vss.CheckedChanged += (_, _) => _vssFailure.Enabled = _vss.Checked;
    }

    public override void LoadFrom(TaskDefinition task)
    {
        _vss.Checked = task.Vss.Enabled;
        _vssAbort.Checked = task.Vss.OnFailure != VssFailureActions.Continue;
        _vssContinue.Checked = task.Vss.OnFailure == VssFailureActions.Continue;
        _vssFailure.Enabled = _vss.Checked;
        _retries.Value = Math.Clamp(task.Copy.ReadRetries, 0, 5);
        _acl.Checked = task.Copy.PreserveAcl;
        int level = Array.IndexOf(Levels, task.LogLevel);
        _logLevel.SelectedIndex = level < 0 ? 0 : level;
        _priority.Value = Math.Clamp(task.Priority, -100, 100);
    }

    public override string? SaveTo(TaskDefinition task)
    {
        task.Vss.Enabled = _vss.Checked;
        task.Vss.OnFailure = _vssContinue.Checked ? VssFailureActions.Continue : VssFailureActions.Abort;
        task.Copy.ReadRetries = (int)_retries.Value;
        task.Copy.PreserveAcl = _acl.Checked;
        task.LogLevel = Levels[Math.Max(0, _logLevel.SelectedIndex)];
        task.Priority = (int)_priority.Value;
        return null;
    }
}

/// <summary>Choice of a stored Windows account, or the service's own one, with "Add…".</summary>
internal sealed class CredentialPicker : UserControl
{
    private readonly EditorContext _context;
    private readonly ComboBox _combo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Button _add = new() { Text = Strings.ButtonAddAccount, AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
    private string? _selectedId;
    private bool _filling;

    public CredentialPicker(EditorContext context)
    {
        _context = context;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.Controls.Add(_combo, 0, 0);
        layout.Controls.Add(_add, 1, 0);
        Controls.Add(layout);
        _combo.SelectedIndexChanged += (_, _) =>
        {
            if (!_filling && _combo.SelectedItem is Item item)
            {
                _selectedId = item.Id;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        _add.Click += (_, _) =>
        {
            if (_context.AddCredential(FindForm()!) is { } added)
            {
                SelectedId = added.Id;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        _context.CredentialsChanged += (_, _) => Fill();
        Fill();
    }

    public event EventHandler? SelectionChanged;

    /// <summary>The account id; null = the account of the service (SYSTEM).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? SelectedId
    {
        get => _selectedId;
        set
        {
            _selectedId = value;
            Fill();
        }
    }

    private void Fill()
    {
        _filling = true;
        try
        {
            _combo.Items.Clear();
            _combo.Items.Add(new Item(null, Strings.AccountService));
            foreach (var credential in _context.Credentials)
            {
                _combo.Items.Add(new Item(credential.Id, credential.DisplayName));
            }

            if (_selectedId is not null && !_context.Credentials.Any(c => string.Equals(c.Id, _selectedId, StringComparison.OrdinalIgnoreCase)))
            {
                _combo.Items.Add(new Item(_selectedId, Strings.AccountUnknown));
            }

            _combo.SelectedItem = _combo.Items.Cast<Item>().FirstOrDefault(i => string.Equals(i.Id, _selectedId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _filling = false;
        }
    }

    private sealed record Item(string? Id, string Text)
    {
        public override string ToString() => Text;
    }
}
