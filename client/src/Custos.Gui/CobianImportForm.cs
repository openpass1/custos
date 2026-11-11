using Custos.Core.Cobian;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Text;

namespace Custos.Gui;

/// <summary>
/// Import of Cobian Backup 11 tasks (section 9 of the spec): reads MainList.lst, shows every task with what to check,
/// asks once for the password of each "run as" account, and adds the chosen tasks switched off.
/// </summary>
internal sealed class CobianImportForm : Form
{
    private readonly ServiceMonitor _monitor;
    private readonly TextBox _file = new() { Dock = DockStyle.Fill };
    private readonly Button _browse = new() { Text = Strings.ButtonBrowse, AutoSize = true };
    private readonly Button _read = new() { Text = Strings.ImportRead, AutoSize = true };
    private readonly ListView _tasks = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
    };

    private readonly TextBox _warnings = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly TableLayoutPanel _accounts = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
    private readonly Panel _accountsPanel = new() { Dock = DockStyle.Bottom, Height = 130, AutoScroll = true, Visible = false };
    private readonly Label _summary = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText };
    private readonly Button _import = new() { Text = Strings.ImportButton, AutoSize = true, MinimumSize = new Size(110, 0), Enabled = false };
    private readonly Button _cancel = new() { Text = Strings.ButtonCancel, AutoSize = true, MinimumSize = new Size(88, 0), DialogResult = DialogResult.Cancel };
    private readonly Label _accountsHeading = new() { Text = Strings.ImportAccounts, AutoSize = true, Margin = new Padding(0, 6, 0, 4) };
    private readonly Dictionary<string, AccountRow> _accountRows = new(StringComparer.OrdinalIgnoreCase);
    private CobianImportResult? _result;
    private bool _filling;

    public CobianImportForm(ServiceMonitor monitor)
    {
        _monitor = monitor;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = Strings.ImportTitle;
        Icon = AppIcons.App();
        Size = new Size(920, 680);
        MinimumSize = new Size(700, 500);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        CancelButton = _cancel;

        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new Padding(12, 12, 12, 0) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(new Label { Text = Strings.ImportFile, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) });
        top.Controls.Add(_file);
        top.Controls.Add(_browse);
        top.Controls.Add(_read);
        var note = new Label { Text = Strings.ImportNote, AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 0, 6) };
        top.Controls.Add(note);
        top.SetColumnSpan(note, 4);

        _tasks.Columns.Add(Strings.ColumnTask, 230);
        _tasks.Columns.Add(Strings.ColumnType, 110);
        _tasks.Columns.Add(Strings.ColumnSchedule, 220);
        _tasks.Columns.Add(Strings.ColumnInCobian, 100);
        _tasks.Columns.Add(Strings.ColumnRemarks, 90);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2 };
        split.Panel1.Controls.Add(_tasks);
        var warningsBox = new GroupBox { Text = Strings.ImportRemarks, Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8) };
        warningsBox.Controls.Add(_warnings);
        split.Panel2.Controls.Add(warningsBox);
        var middle = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 4) };
        middle.Controls.Add(split);

        _accounts.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _accounts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        _accounts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _accountsPanel.Controls.Add(_accounts);
        _accountsPanel.Padding = new Padding(12, 0, 12, 0);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, Padding = new Padding(12, 8, 12, 12) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_summary, 0, 0);
        bottom.Controls.Add(_import, 1, 0);
        bottom.Controls.Add(_cancel, 2, 0);

        // Docking goes from the last control added to the first.
        Controls.Add(middle);
        Controls.Add(_accountsPanel);
        Controls.Add(top);
        Controls.Add(bottom);
        ResumeLayout(performLayout: true);

        _file.Text = GuessListFile() ?? string.Empty;
        _browse.Click += (_, _) => Browse();
        _read.Click += (_, _) => ReadList();
        _import.Click += (_, _) => Import();
        _tasks.SelectedIndexChanged += (_, _) => ShowWarnings();
        _tasks.ItemChecked += (_, _) =>
        {
            if (!_filling)
            {
                UpdateAccounts();
            }
        };
        Shown += (_, _) =>
        {
            Ui.Split(split, LogicalToDeviceUnits(120), LogicalToDeviceUnits(80), split.Height - LogicalToDeviceUnits(150));
            if (_file.Text.Length > 0)
            {
                ReadList();
            }
        };
    }

    /// <summary>The tasks read from the list, with their check boxes.</summary>
    internal ListView TaskList => _tasks;

    /// <summary>Accounts whose password is asked for, one row each.</summary>
    internal int AccountCount => _accountRows.Count;

    /// <summary>Reads a list as "Read" does.</summary>
    internal void ReadFile(string path)
    {
        _file.Text = path;
        ReadList();
    }

    /// <summary>A remark of the importer in words.</summary>
    internal static string Describe(ImportWarning warning)
    {
        string argument = warning.Argument ?? string.Empty;
        return warning.Code switch
        {
            ImportWarningCodes.ScheduleUnknown => Strings.Format(Strings.WarnScheduleUnknown, argument),
            ImportWarningCodes.ScheduleNotSupported => Strings.WarnScheduleNotSupported,
            ImportWarningCodes.ScheduleAssumed => Strings.Format(Strings.WarnScheduleAssumed, argument),
            ImportWarningCodes.TimeUnreadable => Strings.WarnTimeUnreadable,
            ImportWarningCodes.WeekdaysAssumed => Strings.Format(Strings.WarnWeekdaysAssumed, argument),
            ImportWarningCodes.BackupTypeAssumed => Strings.WarnBackupTypeAssumed,
            ImportWarningCodes.BackupTypeNotSupported => Strings.Format(Strings.WarnBackupTypeNotSupported, argument),
            ImportWarningCodes.MirrorLater => Strings.WarnMirrorLater,
            ImportWarningCodes.CompressionAssumed => Strings.WarnCompressionAssumed,
            ImportWarningCodes.EncryptionPassword => Strings.WarnEncryptionPassword,
            ImportWarningCodes.SplitIgnored => Strings.WarnSplitIgnored,
            ImportWarningCodes.EventsInComment => Strings.Format(Strings.WarnEventsInComment, argument),
            ImportWarningCodes.SourceUnknown => Strings.Format(Strings.WarnSourceUnknown, argument),
            ImportWarningCodes.FtpNotSupported => Strings.Format(Strings.WarnFtpNotSupported, argument),
            ImportWarningCodes.NoSources => Strings.WarnNoSources,
            ImportWarningCodes.NoDestinations => Strings.WarnNoDestinations,
            ImportWarningCodes.FixedDayAssumed => Strings.Format(Strings.WarnFixedDayAssumed, argument),
            ImportWarningCodes.FiltersImported => Strings.Format(Strings.WarnFiltersImported, argument),
            ImportWarningCodes.RunAsAccount => Strings.Format(Strings.WarnRunAsAccount, argument),
            ImportWarningCodes.RunAsLocalOnly => Strings.Format(Strings.WarnRunAsLocalOnly, argument),
            _ => warning.Code + (argument.Length > 0 ? ": " + argument : string.Empty),
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearAccountRows();
            _accountsHeading.Dispose();
        }

        base.Dispose(disposing);
    }

    private static string? GuessListFile()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            string path = Path.Combine(Environment.GetFolderPath(folder), "Cobian Backup 11", "DB", "MainList.lst");
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private void Browse()
    {
        using var dialog = new OpenFileDialog { Filter = Strings.ImportFilter, CheckFileExists = true, Title = Strings.ImportTitle };
        if (File.Exists(_file.Text))
        {
            dialog.FileName = _file.Text;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _file.Text = dialog.FileName;
            ReadList();
        }
    }

    private void ReadList()
    {
        try
        {
            _result = CobianImporter.Convert(CobianListParser.ReadFile(_file.Text.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or ArgumentException or NotSupportedException)
        {
            _result = null;
            Ui.ShowError(this, Strings.Format(Strings.ImportCannotRead, ex.Message));
        }

        // A new list has new account ids: the rows of the old one go.
        ClearAccountRows();
        _filling = true;
        _tasks.BeginUpdate();
        _tasks.Items.Clear();
        foreach (var imported in _result?.Tasks ?? [])
        {
            var task = imported.Task;
            var item = new ListViewItem(
            [
                task.Name,
                Words.BackupType(task.BackupType),
                TaskDescriber.ScheduleWithCatchUp(task.Schedule),
                imported.EnabledInCobian ? Strings.Yes : Strings.No,
                imported.Warnings.Count == 0 ? "—" : Ui.Number(imported.Warnings.Count),
            ])
            {
                Tag = imported,
                Checked = true,
            };
            _tasks.Items.Add(item);
        }

        _tasks.EndUpdate();
        _filling = false;
        if (_tasks.Items.Count > 0)
        {
            _tasks.Items[0].Selected = true;
        }

        _summary.Text = _result is null ? string.Empty : Strings.Format(Strings.ImportFound, _result.Tasks.Count);
        ShowWarnings();
        UpdateAccounts();
    }

    private void ShowWarnings()
    {
        if (_tasks.SelectedItems.Count == 0 || _tasks.SelectedItems[0].Tag is not ImportedTask imported)
        {
            _warnings.Text = string.Empty;
            return;
        }

        var lines = new List<string> { TaskDescriber.Describe(imported.Task), string.Empty };
        lines.AddRange(imported.Warnings.Count == 0
            ? [Strings.ImportNoRemarks]
            : imported.Warnings.Select(w => "• " + Describe(w)));
        _warnings.Text = string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// One row per account that the chosen tasks use for network paths; its password is asked once. Rows of accounts
    /// that are still needed keep what was typed into them when other tasks are ticked or unticked.
    /// </summary>
    private void UpdateAccounts()
    {
        if (_result is null)
        {
            _accountsPanel.Visible = false;
            _import.Enabled = false;
            return;
        }

        var chosen = Chosen();
        var stored = _monitor.Snapshot?.Credentials ?? [];
        var needed = _result.Accounts.Where(a => chosen.Any(t => Uses(t.Task, a.Credential.Id))).ToList();

        _accounts.SuspendLayout();
        _accounts.Controls.Clear();
        _accounts.RowStyles.Clear();
        foreach (string id in _accountRows.Keys.Where(id => !needed.Any(a => string.Equals(a.Credential.Id, id, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            _accountRows[id].Dispose();
            _accountRows.Remove(id);
        }

        if (needed.Count > 0)
        {
            _accounts.Controls.Add(_accountsHeading);
            _accounts.SetColumnSpan(_accountsHeading, 3);
        }

        foreach (var account in needed)
        {
            if (!_accountRows.TryGetValue(account.Credential.Id, out var row))
            {
                var existing = stored.FirstOrDefault(c => c.Kind == account.Credential.Kind
                    && string.Equals(c.User, account.Credential.User, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(c.Domain ?? ".", account.Credential.Domain ?? ".", StringComparison.OrdinalIgnoreCase));
                row = new AccountRow(account, existing);
                _accountRows[account.Credential.Id] = row;
            }

            row.AddTo(_accounts);
        }

        _accounts.ResumeLayout(performLayout: true);
        _accountsPanel.Visible = needed.Count > 0;
        _import.Enabled = chosen.Count > 0;
    }

    private void ClearAccountRows()
    {
        _accounts.Controls.Clear();
        foreach (var row in _accountRows.Values)
        {
            row.Dispose();
        }

        _accountRows.Clear();
    }

    private List<ImportedTask> Chosen() => [.. _tasks.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => (ImportedTask)i.Tag!)];

    private void Import()
    {
        var chosen = Chosen();
        if (chosen.Count == 0)
        {
            return;
        }

        var request = new TasksImportRequest();
        var replace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _accountRows.Values)
        {
            if (row.UseStored)
            {
                replace[row.Account.Credential.Id] = row.Existing!.Id;
                continue;
            }

            if (row.Password.Length == 0)
            {
                Ui.ShowWarning(this, Strings.Format(Strings.ImportPasswordRequired, row.Account.Credential.DisplayName));
                return;
            }

            request.Credentials.Add(new CredentialInput { Credential = row.Account.Credential, Password = row.Password });
        }

        foreach (var imported in chosen)
        {
            var task = imported.Task.Clone();
            task.Enabled = false;
            foreach (var source in task.Sources.Where(s => s.CredentialId is not null && replace.ContainsKey(s.CredentialId)))
            {
                source.CredentialId = replace[source.CredentialId!];
            }

            foreach (var destination in task.Destinations.Where(d => d.CredentialId is not null && replace.ContainsKey(d.CredentialId)))
            {
                destination.CredentialId = replace[destination.CredentialId!];
            }

            request.Tasks.Add(task);
        }

        _import.Enabled = false;
        _cancel.Enabled = false;
        UseWaitCursor = true;
        Ui.Run(this, async () =>
        {
            try
            {
                await _monitor.RequestAsync(PipeProtocol.Methods.TasksImport, request,
                    IpcJsonContext.Default.TasksImportRequest, IpcJsonContext.Default.OkResult);
                Ui.ShowInfo(this, Strings.Format(Strings.ImportDone, request.Tasks.Count));
                DialogResult = DialogResult.OK;
            }
            finally
            {
                _import.Enabled = true;
                _cancel.Enabled = true;
                UseWaitCursor = false;
            }
        });
    }

    private static bool Uses(TaskDefinition task, string credentialId) =>
        task.Sources.Any(s => string.Equals(s.CredentialId, credentialId, StringComparison.OrdinalIgnoreCase))
        || task.Destinations.Any(d => string.Equals(d.CredentialId, credentialId, StringComparison.OrdinalIgnoreCase));

    /// <summary>An account to import: its name, the tasks using it, and its password or "use the stored one".</summary>
    private sealed class AccountRow : IDisposable
    {
        private readonly Label _name;
        private readonly TextBox _password = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
        private readonly CheckBox _useStored = new() { Text = Strings.ImportUseStored, AutoSize = true };
        private readonly FlowLayoutPanel? _both;
        private readonly Label _tasks;

        public AccountRow(ImportedAccount account, Credential? existing)
        {
            Account = account;
            Existing = existing;
            _name = new Label { Text = account.Credential.DisplayName, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) };
            _tasks = new Label
            {
                Text = Strings.Format(Strings.ImportAccountTasks, string.Join(", ", account.TaskNames)),
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(12, 6, 0, 6),
            };
            if (existing is not null)
            {
                // The same account is stored already: its password is used unless the box is unticked.
                _useStored.Checked = true;
                _password.Enabled = false;
                _password.Dock = DockStyle.None;
                _password.Width = 140;
                _useStored.CheckedChanged += (_, _) => _password.Enabled = !_useStored.Checked;
                _both = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
                _both.Controls.AddRange([_password, _useStored]);
            }
        }

        public ImportedAccount Account { get; }

        public Credential? Existing { get; }

        public bool UseStored => Existing is not null && _useStored.Checked;

        public string Password => _password.Text;

        public void AddTo(TableLayoutPanel table)
        {
            table.Controls.Add(_name);
            table.Controls.Add(_both is null ? _password : _both);
            table.Controls.Add(_tasks);
        }

        public void Dispose()
        {
            _password.Clear();
            _name.Dispose();
            _password.Dispose();
            _useStored.Dispose();
            _both?.Dispose();
            _tasks.Dispose();
        }
    }
}
