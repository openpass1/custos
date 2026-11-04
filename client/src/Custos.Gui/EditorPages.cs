using System.ComponentModel;
using System.Globalization;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Scheduling;
using Custos.Core.Text;

namespace Custos.Gui;

/// <summary>"Basic": name, on/off, backup type with the explanations of section 6.1, forced full copies, comment.</summary>
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
    private string? _mirrorMode;

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
        _mirror.Enabled = false;

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
        AddNote(Strings.TypeMirrorLater, 18);

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

        _full.CheckedChanged += (_, _) => UpdateForceFull();
        _mirror.CheckedChanged += (_, _) => UpdateForceFull();
        _forceEvery.CheckedChanged += (_, _) => _forceEveryRuns.Enabled = _forceEvery.Checked;
        _forceWeekday.CheckedChanged += (_, _) => _weekday.Enabled = _forceWeekday.Checked;
        _forceMonthDay.CheckedChanged += (_, _) => _monthDay.Enabled = _forceMonthDay.Checked;
    }

    public override void LoadFrom(TaskDefinition task)
    {
        _name.Text = task.Name;
        _enabled.Checked = task.Enabled;
        _mirrorMode = task.MirrorMode;
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
        UpdateForceFull();
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
        task.MirrorMode = task.BackupType == BackupTypes.Mirror ? _mirrorMode : null;
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

    private void UpdateForceFull() => _forceGroup.Enabled = !_full.Checked && !_mirror.Checked;
}

/// <summary>"Sources": folders, files and network folders with their accounts (section 6.2).</summary>
internal sealed class SourcesPage : EditorPage
{
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        Height = 200,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
    };

    private readonly Button _add = new() { Text = Strings.ButtonAdd, AutoSize = true, MinimumSize = new Size(100, 0) };
    private readonly Button _edit = new() { Text = Strings.ButtonEdit, AutoSize = true, MinimumSize = new Size(100, 0) };
    private readonly Button _remove = new() { Text = Strings.ButtonRemove, AutoSize = true, MinimumSize = new Size(100, 0) };
    private readonly CheckBox _keepTopDir = new() { Text = Strings.KeepTopDir, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
    private readonly List<TaskSource> _sources = [];

    public SourcesPage(EditorContext context)
        : base(context, Strings.PageSources, Glyph.Folder)
    {
        _list.Columns.Add(Strings.ColumnPath, 330);
        _list.Columns.Add(Strings.ColumnSubfolders, 90);
        _list.Columns.Add(Strings.ColumnAccount, 150);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(8, 0, 0, 0) };
        buttons.Controls.AddRange([_add, _edit, _remove]);
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(_list, 0, 0);
        row.Controls.Add(buttons, 1, 0);

        AddNote(Strings.SourcesNote);
        AddWide(row);
        AddWide(_keepTopDir, Strings.KeepTopDirHelp);

        _add.Click += (_, _) => AddSource();
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
        Fill();
    }

    public override string? SaveTo(TaskDefinition task)
    {
        if (task.Enabled && _sources.Count == 0)
        {
            return Strings.ErrorNoSources;
        }

        task.Sources = [.. _sources];
        task.Layout.KeepTopDir = _keepTopDir.Checked;
        return null;
    }

    private void Fill()
    {
        int selected = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
        var accounts = Context.Credentials;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var source in _sources)
        {
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
}

/// <summary>
/// "Destinations": local folders, network folders with an account and USB disks found by their label (section 6.3),
/// "Check access", and how the copies are laid out (section 6.4).
/// </summary>
internal sealed class DestinationsPage : EditorPage
{
    private static readonly string[] Kinds = [DestinationKinds.Local, DestinationKinds.Unc, DestinationKinds.Usb];

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
    private readonly Button _remove = new() { Text = Strings.ButtonRemove, AutoSize = true, MinimumSize = new Size(100, 0) };
    private readonly GroupBox _properties = new() { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10, 6, 10, 8), Text = Strings.DestinationProperties };
    private readonly TableLayoutPanel _grid = new() { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
    private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly Label _pathLabel = Caption(Strings.DestinationFolder);
    private readonly TextBox _path = new() { Dock = DockStyle.Fill };
    private readonly Button _browse = new() { Text = Strings.ButtonBrowse, AutoSize = true };
    private readonly Label _labelLabel = Caption(Strings.DestinationUsbLabel);
    private readonly ComboBox _usbLabel = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 220 };
    private readonly Label _accountLabel = Caption(Strings.DestinationAccount);
    private readonly CredentialPicker _account;
    private readonly PictureBox _usbHelp;
    private readonly PictureBox _accountHelp;
    private readonly Label _unsupported = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Glyphs.WarningText };
    private readonly Button _test = new() { Text = Strings.ButtonTestAccess, AutoSize = true };
    private readonly Label _testResult = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
    private readonly CheckBox _separate = new() { Text = Strings.SeparateFolders, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
    private readonly TextBox _template = new() { Dock = DockStyle.Fill };
    private readonly List<TaskDestination> _destinations = [];
    private TaskDestination? _current;
    private bool _loading;

    public DestinationsPage(EditorContext context)
        : base(context, Strings.PageDestinations, Glyph.Drive)
    {
        _account = new CredentialPicker(context) { Dock = DockStyle.Fill };
        _usbHelp = Ui.HelpIcon(ToolTip, Strings.DestinationUsbHelp);
        _accountHelp = Ui.HelpIcon(ToolTip, Strings.DestinationAccountHelp);
        _list.Columns.Add(Strings.ColumnDestination, 330);
        _list.Columns.Add(Strings.ColumnType, 120);
        _list.Columns.Add(Strings.ColumnAccount, 150);
        foreach (string kind in Kinds)
        {
            _kind.Items.Add(KindText(kind));
        }

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(8, 0, 0, 0) };
        buttons.Controls.AddRange([_add, _remove]);
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(_list, 0, 0);
        row.Controls.Add(buttons, 1, 0);

        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddGridRow(Caption(Strings.DestinationKind), _kind, Ui.HelpIcon(ToolTip, Strings.DestinationKindHelp));
        AddGridRow(_pathLabel, _path, _browse);
        AddGridRow(_labelLabel, _usbLabel, _usbHelp);
        AddGridRow(_accountLabel, _account, _accountHelp);
        _grid.Controls.Add(_unsupported);
        _grid.SetColumnSpan(_unsupported, 3);
        var testRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 6, 0, 0) };
        testRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        testRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        testRow.Controls.Add(_test, 0, 0);
        testRow.Controls.Add(_testResult, 1, 0);
        _testResult.Margin = new Padding(8, 6, 0, 0);
        _grid.Controls.Add(testRow);
        _grid.SetColumnSpan(testRow, 3);
        _properties.Controls.Add(_grid);

        AddNote(Strings.DestinationsNote);
        AddWide(row);
        AddWide(_properties);
        AddHeading(Strings.EditorLayout);
        AddWide(_separate, Strings.SeparateFoldersHelp);
        AddRow(Strings.NameTemplate, _template, Strings.NameTemplateHelp);

        _add.Click += (_, _) => AddDestination();
        _remove.Click += (_, _) => RemoveDestination();
        _list.SelectedIndexChanged += (_, _) => ShowSelected();
        _kind.SelectedIndexChanged += (_, _) => OnKindChanged();
        _path.TextChanged += (_, _) => Edit(d => d.Path = _path.Text.Trim());
        _usbLabel.TextChanged += (_, _) => Edit(d => d.UsbLabel = _usbLabel.Text.Trim());
        _account.SelectionChanged += (_, _) => Edit(d => d.CredentialId = _account.SelectedId);
        _browse.Click += (_, _) => Browse();
        _test.Click += (_, _) => TestAccess();
        _separate.CheckedChanged += (_, _) => _template.Enabled = _separate.Checked;
        Context.CredentialsChanged += (_, _) => Fill();
    }

    public override void LoadFrom(TaskDefinition task)
    {
        _destinations.Clear();
        _destinations.AddRange(task.Destinations);
        _separate.Checked = task.Layout.SeparateFolders;
        _template.Text = task.Layout.NameTemplate;
        _template.Enabled = _separate.Checked;
        Fill();
        if (_list.Items.Count > 0)
        {
            _list.Items[0].Selected = true;
        }

        ShowSelected();
    }

    public override void Shown() => FillUsbLabels();

    public override string? SaveTo(TaskDefinition task)
    {
        if (task.Enabled && _destinations.Count == 0)
        {
            return Strings.ErrorNoDestinations;
        }

        foreach (var destination in _destinations.Where(d => DestinationKinds.IsSupported(d.Kind)))
        {
            string? problem = null;
            if (destination.Kind == DestinationKinds.Usb)
            {
                // The disk is found by its label; a drive letter in the folder would only mislead.
                destination.Path = DestinationKinds.UsbFolder(destination.Path);
                problem = string.IsNullOrWhiteSpace(destination.UsbLabel) ? Strings.ErrorUsbLabelRequired : null;
            }
            else if (string.IsNullOrWhiteSpace(destination.Path))
            {
                problem = Strings.ErrorDestinationFolderRequired;
            }
            else if (!Path.IsPathFullyQualified(destination.Path))
            {
                problem = Strings.Format(Strings.ErrorPathNotAbsolute, destination.Path);
            }
            else if (destination.Kind == DestinationKinds.Unc && !destination.Path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                problem = Strings.Format(Strings.ErrorNotUnc, destination.Path);
            }

            if (problem is not null)
            {
                // The destination with the problem is shown, so that it is clear which one is meant.
                _list.Items[_destinations.IndexOf(destination)].Selected = true;
                return problem;
            }
        }

        if (_separate.Checked && string.IsNullOrWhiteSpace(_template.Text))
        {
            return Strings.ErrorTemplateRequired;
        }

        task.Destinations = [.. _destinations];
        task.Layout.SeparateFolders = _separate.Checked;
        if (_separate.Checked)
        {
            task.Layout.NameTemplate = _template.Text.Trim();
        }

        return null;
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) };

    private static string KindText(string kind) => kind switch
    {
        DestinationKinds.Local => Strings.KindLocal,
        DestinationKinds.Unc => Strings.KindUnc,
        DestinationKinds.Usb => Strings.KindUsb,
        _ => Strings.Format(Strings.KindLater, kind.ToUpperInvariant()),
    };

    private void AddGridRow(Control caption, Control control, Control extra)
    {
        control.Margin = new Padding(0, 3, 0, 3);
        extra.Margin = new Padding(6, 3, 0, 3);
        _grid.Controls.Add(caption);
        _grid.Controls.Add(control);
        _grid.Controls.Add(extra);
    }

    private void Fill()
    {
        int selected = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
        var accounts = Context.Credentials;
        _loading = true;
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

        _loading = false;
    }

    private void RefreshRow()
    {
        if (_current is null || _list.SelectedIndices.Count == 0)
        {
            return;
        }

        var item = _list.Items[_list.SelectedIndices[0]];
        item.SubItems[0].Text = TaskDescriber.Destination(_current);
        item.SubItems[1].Text = KindText(_current.Kind);
        item.SubItems[2].Text = _current.CredentialId is { } id
            ? Context.Credentials.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? Strings.AccountUnknown
            : _current.Kind == DestinationKinds.Unc ? Strings.AccountService : string.Empty;
    }

    private void ShowSelected()
    {
        if (_loading)
        {
            return;
        }

        _current = _list.SelectedIndices.Count > 0 ? _destinations[_list.SelectedIndices[0]] : null;
        _remove.Enabled = _current is not null;
        _properties.Enabled = _current is not null;
        _testResult.Text = string.Empty;
        _loading = true;
        try
        {
            if (_current is null)
            {
                _kind.SelectedIndex = -1;
                _path.Text = string.Empty;
                _usbLabel.Text = string.Empty;
                _account.SelectedId = null;
                UpdateFields(DestinationKinds.Local, supported: true);
                return;
            }

            int index = Array.IndexOf(Kinds, _current.Kind);
            if (index < 0)
            {
                // A kind of a later version, set from the server: shown, not editable here.
                while (_kind.Items.Count > Kinds.Length)
                {
                    _kind.Items.RemoveAt(Kinds.Length);
                }

                _kind.Items.Add(KindText(_current.Kind));
                index = Kinds.Length;
            }

            _kind.SelectedIndex = index;
            _path.Text = _current.Path;
            _usbLabel.Text = _current.UsbLabel ?? string.Empty;
            _account.SelectedId = _current.CredentialId;
            UpdateFields(_current.Kind, DestinationKinds.IsSupported(_current.Kind));
        }
        finally
        {
            _loading = false;
        }
    }

    private void UpdateFields(string kind, bool supported)
    {
        bool usb = kind == DestinationKinds.Usb;
        bool unc = kind == DestinationKinds.Unc;
        _pathLabel.Text = usb ? Strings.DestinationFolderOnDisk : Strings.DestinationFolder;
        _browse.Visible = kind == DestinationKinds.Local;
        _labelLabel.Visible = usb;
        _usbLabel.Visible = usb;
        _usbHelp.Visible = usb;
        _accountLabel.Visible = unc;
        _account.Visible = unc;
        _accountHelp.Visible = unc;
        _unsupported.Visible = !supported;
        _unsupported.Text = supported ? string.Empty : Strings.DestinationLater;
        _path.Enabled = supported;
        _kind.Enabled = supported;
        _test.Enabled = supported;
    }

    private void OnKindChanged()
    {
        if (_loading || _current is null || _kind.SelectedIndex < 0 || _kind.SelectedIndex >= Kinds.Length)
        {
            return;
        }

        string kind = Kinds[_kind.SelectedIndex];
        Edit(d =>
        {
            d.Kind = kind;
            if (kind != DestinationKinds.Unc)
            {
                d.CredentialId = null;
            }

            if (kind != DestinationKinds.Usb)
            {
                d.UsbLabel = null;
            }
        });
        UpdateFields(kind, supported: true);
    }

    private void Edit(Action<TaskDestination> change)
    {
        if (_loading || _current is null)
        {
            return;
        }

        change(_current);
        _testResult.Text = string.Empty;
        RefreshRow();
    }

    private void AddDestination()
    {
        _destinations.Add(new TaskDestination { Kind = DestinationKinds.Local });
        Fill();
        _list.Items[^1].Selected = true;
        _path.Focus();
    }

    private void RemoveDestination()
    {
        if (_list.SelectedIndices.Count == 0)
        {
            return;
        }

        _destinations.RemoveAt(_list.SelectedIndices[0]);
        Fill();
        ShowSelected();
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog { ShowNewFolderButton = true, UseDescriptionForTitle = true, Description = Strings.DestinationFolder };
        if (Directory.Exists(_path.Text))
        {
            dialog.InitialDirectory = _path.Text;
        }

        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
        {
            _path.Text = dialog.SelectedPath;
        }
    }

    /// <summary>Labels of the disks connected now, for the USB label list.</summary>
    private void FillUsbLabels()
    {
        string text = _usbLabel.Text;
        _usbLabel.Items.Clear();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType is DriveType.Removable or DriveType.Fixed && !string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    && !_usbLabel.Items.Contains(drive.VolumeLabel))
                {
                    _usbLabel.Items.Add(drive.VolumeLabel);
                }
            }
            catch (IOException)
            {
                // The disk went away while we looked.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        _usbLabel.Text = text;
    }

    private void TestAccess()
    {
        if (_current is null)
        {
            return;
        }

        var destination = new TaskDestination
        {
            Id = _current.Id,
            Kind = _current.Kind,
            Path = _current.Path,
            UsbLabel = _current.UsbLabel,
            CredentialId = _current.CredentialId,
        };
        _test.Enabled = false;
        _testResult.ForeColor = SystemColors.GrayText;
        _testResult.Text = Strings.TestRunning;
        Ui.Run(FindForm(), async () =>
        {
            try
            {
                var result = await Context.Monitor.RequestAsync(PipeProtocol.Methods.DestinationTest, new DestinationTestRequest { Destination = destination },
                    IpcJsonContext.Default.DestinationTestRequest, IpcJsonContext.Default.DestinationTestResult, TimeSpan.FromMinutes(2));
                _testResult.ForeColor = result.Ok ? Glyphs.Green : Glyphs.ErrorText;
                _testResult.Text = result.Ok
                    ? Strings.Format(Strings.TestOk, result.Folder, result.FreeBytes is { } free ? Sizes.Format(free) : "?")
                    : Strings.Format(Strings.TestFailed, result.Error);
            }
            catch (ServiceUnavailableException ex)
            {
                _testResult.ForeColor = Glyphs.ErrorText;
                _testResult.Text = ex.Message;
            }
            finally
            {
                _test.Enabled = true;
            }
        });
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

/// <summary>"Archive": compression and encryption come in version 2; until then copies are plain files.</summary>
internal sealed class ArchivePage : EditorPage
{
    private readonly RadioButton _none = new() { Text = Strings.ArchiveNone, AutoSize = true };
    private readonly RadioButton _zip = new() { Text = Strings.ArchiveZip, AutoSize = true, Enabled = false };
    private readonly RadioButton _sevenZip = new() { Text = Strings.ArchiveSevenZip, AutoSize = true, Enabled = false };
    private readonly Label _imported = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Glyphs.WarningText, Margin = new Padding(0, 6, 0, 6) };

    public ArchivePage(EditorContext context)
        : base(context, Strings.PageArchive, Glyph.Box)
    {
        AddNote(Strings.ArchiveLater);
        AddWide(_none);
        AddWide(_zip);
        AddWide(_sevenZip);
        AddWide(_imported);
    }

    public override bool Available => false;

    public override void LoadFrom(TaskDefinition task)
    {
        _none.Checked = task.Archive.Format == ArchiveFormats.None;
        _zip.Checked = task.Archive.Format == ArchiveFormats.Zip;
        _sevenZip.Checked = task.Archive.Format == ArchiveFormats.SevenZip;
        _imported.Visible = task.Archive.Format != ArchiveFormats.None;
        _imported.Text = Strings.ArchiveSetButUnsupported;
    }

    public override string? SaveTo(TaskDefinition task)
    {
        if (_none.Checked)
        {
            task.Archive.Format = ArchiveFormats.None;
            task.Archive.Encrypt = false;
            task.Archive.EncryptNames = false;
        }

        return null;
    }
}

/// <summary>"Retention": how many full copies to keep with their chains (section 6.6).</summary>
internal sealed class RetentionPage : EditorPage
{
    private readonly RadioButton _keepAll = new() { Text = Strings.RetentionNone, AutoSize = true };
    private readonly RadioButton _byCount = new() { Text = Strings.RetentionCount, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly NumericUpDown _keepFull = new() { Minimum = 1, Maximum = 1000, Value = 12, Width = 70 };
    private readonly RadioButton _byAge = new() { Text = Strings.RetentionAge, AutoSize = true, Enabled = false };
    private readonly RadioButton _gfs = new() { Text = Strings.RetentionGfs, AutoSize = true, Enabled = false };
    private string _otherMode = RetentionModes.Count;

    public RetentionPage(EditorContext context)
        : base(context, Strings.PageRetention, Glyph.Layers)
    {
        AddWide(_keepAll);
        var count = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        count.Controls.AddRange([_byCount, _keepFull, new Label { Text = Strings.RetentionCountSuffix, AutoSize = true, Margin = new Padding(4, 8, 0, 0) }]);
        AddWide(count);
        AddNote(Strings.RetentionCountHint, 18);
        AddWide(_byAge);
        AddWide(_gfs);
        AddNote(Strings.RetentionLater, 18);
        AddHeading(Strings.RetentionRules);
        AddNote(Strings.RetentionRulesText);

        _byCount.CheckedChanged += (_, _) => _keepFull.Enabled = _byCount.Checked;
        _keepAll.CheckedChanged += (_, _) =>
        {
            // The radio buttons sit in different panels, so they are kept exclusive by hand.
            if (_keepAll.Checked)
            {
                _byCount.Checked = false;
            }
        };
        _byCount.CheckedChanged += (_, _) =>
        {
            if (_byCount.Checked)
            {
                _keepAll.Checked = false;
                _byAge.Checked = false;
                _gfs.Checked = false;
            }
        };
    }

    public override void LoadFrom(TaskDefinition task)
    {
        var retention = task.Retention;
        _keepFull.Value = Math.Clamp(retention.KeepFull, 1, 1000);
        _otherMode = retention.Mode;
        _keepAll.Checked = retention.Mode == RetentionModes.None;
        _byCount.Checked = retention.Mode == RetentionModes.Count;
        _byAge.Checked = retention.Mode == RetentionModes.Age;
        _gfs.Checked = retention.Mode == RetentionModes.Gfs;
        _keepFull.Enabled = _byCount.Checked;
    }

    public override string? SaveTo(TaskDefinition task)
    {
        task.Retention.Mode = _keepAll.Checked ? RetentionModes.None : _byCount.Checked ? RetentionModes.Count : _otherMode;
        task.Retention.KeepFull = (int)_keepFull.Value;
        return null;
    }
}

/// <summary>"Filters": file masks to include and exclude, folders to skip, hidden, system and temporary files.</summary>
internal sealed class FiltersPage : EditorPage
{
    private readonly TextBox _include = new() { Dock = DockStyle.Fill };
    private readonly TextBox _exclude = new() { Dock = DockStyle.Fill };
    private readonly TextBox _excludeDirs = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _skipHidden = new() { Text = Strings.SkipHidden, AutoSize = true };
    private readonly CheckBox _skipSystem = new() { Text = Strings.SkipSystem, AutoSize = true };
    private readonly CheckBox _skipTemp = new() { Text = Strings.SkipTemp, AutoSize = true };

    public FiltersPage(EditorContext context)
        : base(context, Strings.PageFilters, Glyph.Funnel)
    {
        AddNote(Strings.FiltersNote);
        AddRow(Strings.FilterInclude, _include, Strings.FilterIncludeHelp);
        AddRow(Strings.FilterExclude, _exclude, Strings.FilterExcludeHelp);
        AddRow(Strings.FilterExcludeDirs, _excludeDirs, Strings.FilterExcludeDirsHelp);
        AddWide(_skipHidden);
        AddWide(_skipSystem);
        AddWide(_skipTemp, Strings.SkipTempHelp);
    }

    public override void LoadFrom(TaskDefinition task)
    {
        _include.Text = string.Join("; ", task.Filters.Include);
        _exclude.Text = string.Join("; ", task.Filters.Exclude);
        _excludeDirs.Text = string.Join("; ", task.Filters.ExcludeDirs);
        _skipHidden.Checked = task.Filters.SkipHidden;
        _skipSystem.Checked = task.Filters.SkipSystem;
        _skipTemp.Checked = task.Filters.SkipTemp;
    }

    public override string? SaveTo(TaskDefinition task)
    {
        task.Filters.Include = Masks(_include.Text);
        task.Filters.Exclude = Masks(_exclude.Text);
        task.Filters.ExcludeDirs = Masks(_excludeDirs.Text);
        task.Filters.SkipHidden = _skipHidden.Checked;
        task.Filters.SkipSystem = _skipSystem.Checked;
        task.Filters.SkipTemp = _skipTemp.Checked;
        return null;
    }

    private static List<string> Masks(string text) =>
        [.. text.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];
}

/// <summary>"Events": scripts and services before and after, shutdown after the run; they come in version 2.</summary>
internal sealed class EventsPage : EditorPage
{
    private readonly Label _present = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Glyphs.WarningText };

    public EventsPage(EditorContext context)
        : base(context, Strings.PageEvents, Glyph.Bolt)
    {
        AddNote(Strings.EventsLater);
        AddWide(_present);
    }

    public override bool Available => false;

    public override void LoadFrom(TaskDefinition task)
    {
        int count = task.Events.Pre.Count + task.Events.Post.Count;
        _present.Visible = count > 0 || task.Events.After != AfterActions.None;
        _present.Text = Strings.EventsSetButUnsupported;
    }

    public override string? SaveTo(TaskDefinition task) => null;
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
        int level = Array.IndexOf(Levels, task.LogLevel);
        _logLevel.SelectedIndex = level < 0 ? 0 : level;
        _priority.Value = Math.Clamp(task.Priority, -100, 100);
    }

    public override string? SaveTo(TaskDefinition task)
    {
        task.Vss.Enabled = _vss.Checked;
        task.Vss.OnFailure = _vssContinue.Checked ? VssFailureActions.Continue : VssFailureActions.Abort;
        task.Copy.ReadRetries = (int)_retries.Value;
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
