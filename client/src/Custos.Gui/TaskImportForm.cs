using Custos.Core;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Text;
using Custos.Core.Transfer;

namespace Custos.Gui;

/// <summary>
/// Import of a file of exported tasks (section 9a of the spec), for example after reinstalling Windows: reads the
/// file, asks for the export password at once when the file has passwords and keys, shows every task with what it still
/// needs, asks once for each password that is not in the file, and adds the chosen tasks with their ids. The rows for
/// those passwords appear only once the file is opened or the person chose to go without its passwords: shown next to
/// a closed file, they were taken for the place of the export password.
/// </summary>
internal sealed class TaskImportForm : Form
{
    private readonly ServiceMonitor _monitor;
    private readonly TextBox _file = new() { Dock = DockStyle.Fill };
    private readonly Button _browse = new() { Text = Strings.ButtonBrowse, AutoSize = true };
    private readonly Button _read = new() { Text = Strings.ImportRead, AutoSize = true };
    private readonly Label _from = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2) };
    private readonly Label _secretsState = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) };
    private readonly Button _enterPassword = new() { Text = Strings.ImportFileEnterPassword, AutoSize = true };
    private readonly FlowLayoutPanel _secretsRow = new() { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0), Visible = false };
    private readonly ListView _tasks = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
    };

    private readonly TextBox _remarks = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly TableLayoutPanel _accounts = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
    private readonly Panel _accountsPanel = new() { Dock = DockStyle.Bottom, Height = 140, AutoScroll = true, Visible = false };
    private readonly Label _accountsHeading = new() { Text = Strings.ImportFileAccounts, AutoSize = true, Margin = new Padding(0, 6, 0, 4) };
    private readonly Label _summary = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText };
    private readonly Button _import = new() { Text = Strings.ImportButton, AutoSize = true, MinimumSize = new Size(110, 0), Enabled = false };
    private readonly Button _cancel = new() { Text = Strings.ButtonCancel, AutoSize = true, MinimumSize = new Size(88, 0), DialogResult = DialogResult.Cancel };
    private readonly Dictionary<string, AccountRow> _accountRows = new(StringComparer.OrdinalIgnoreCase);
    private ExportFile? _exported;
    private ExportSecrets? _secrets;
    private TaskImport? _plan;
    private bool _filling;

    /// <summary>The person chose to import without the passwords of the file.</summary>
    private bool _declined;

    public TaskImportForm(ServiceMonitor monitor)
    {
        _monitor = monitor;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = Strings.ImportFileTitle;
        Icon = AppIcons.App();
        Size = new Size(940, 700);
        MinimumSize = new Size(700, 520);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        CancelButton = _cancel;

        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new Padding(12, 12, 12, 0) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(new Label { Text = Strings.ImportFileLabel, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) });
        top.Controls.Add(_file);
        top.Controls.Add(_browse);
        top.Controls.Add(_read);
        var note = new Label { Text = Strings.ImportFileNote, AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 0, 6) };
        top.Controls.Add(note);
        top.SetColumnSpan(note, 4);
        top.Controls.Add(_from);
        top.SetColumnSpan(_from, 4);
        _secretsRow.Controls.AddRange([_secretsState, _enterPassword]);
        top.Controls.Add(_secretsRow);
        top.SetColumnSpan(_secretsRow, 4);

        _tasks.Columns.Add(Strings.ColumnTask, 230);
        _tasks.Columns.Add(Strings.ColumnType, 110);
        _tasks.Columns.Add(Strings.ColumnSchedule, 200);
        _tasks.Columns.Add(Strings.ColumnEnabled, 80);
        _tasks.Columns.Add(Strings.ColumnHere, 150);
        _tasks.Columns.Add(Strings.ColumnRemarks, 90);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2 };
        split.Panel1.Controls.Add(_tasks);
        var remarksBox = new GroupBox { Text = Strings.ImportRemarks, Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8) };
        remarksBox.Controls.Add(_remarks);
        split.Panel2.Controls.Add(remarksBox);
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

        _browse.Click += (_, _) => Browse();
        _read.Click += (_, _) => ReadAndAsk();
        _enterPassword.Click += (_, _) => AskExportPassword();
        _import.Click += (_, _) => Import();
        _tasks.SelectedIndexChanged += (_, _) => ShowRemarks();
        _tasks.ItemChecked += (_, _) =>
        {
            if (!_filling)
            {
                UpdateAccounts();
            }
        };
        Shown += (_, _) => Ui.Split(split, LogicalToDeviceUnits(120), LogicalToDeviceUnits(80), split.Height - LogicalToDeviceUnits(150));
    }

    /// <summary>The tasks of the file with their check boxes (window tests).</summary>
    internal ListView TaskList => _tasks;

    /// <summary>Accounts whose password is asked for, one row each (window tests).</summary>
    internal int AccountCount => _accountRows.Count;

    /// <summary>Whether the row about the passwords of the file is shown (window tests).</summary>
    internal bool AsksExportPassword => _secretsRow.Visible;

    /// <summary>Whether the secrets of the file are open (window tests).</summary>
    internal bool SecretsOpen => _secrets is not null;

    /// <summary>The remarks of the selected task (window tests).</summary>
    internal string RemarksText => _remarks.Text;

    /// <summary>Reads a file as «Read» does, without asking for the export password (window tests).</summary>
    internal void ReadFile(string path)
    {
        _file.Text = path;
        ReadFile();
    }

    /// <summary>Opens the secrets as the export password dialog does (window tests).</summary>
    internal void OpenSecrets(string password)
    {
        if (_exported?.Secrets is not null && ExportPasswordDialog.TryOpen(_exported, password).Secrets is { } secrets)
        {
            Opened(secrets);
        }
    }

    /// <summary>Goes on without the passwords of the file, as «Without passwords from the file» does (window tests).</summary>
    internal void DeclineSecrets() => Declined();

    /// <summary>A remark in words.</summary>
    internal static string Describe(ImportRemark remark)
    {
        string argument = remark.Argument ?? string.Empty;
        return remark.Code switch
        {
            ImportRemarkCodes.Replaces => Strings.Format(Strings.RemarkReplaces, argument),
            ImportRemarkCodes.PasswordMissing => Strings.Format(Strings.RemarkPasswordMissing, argument),
            ImportRemarkCodes.ArchivePasswordMissing => Strings.RemarkArchivePasswordMissing,
            ImportRemarkCodes.SshKeyMissing => Strings.Format(Strings.RemarkSshKeyMissing, argument),
            ImportRemarkCodes.CloudMissing => Strings.Format(Strings.RemarkCloudMissing, argument),
            ImportRemarkCodes.SecretsLocked => Strings.RemarkSecretsLocked,
            _ => remark.Code + (argument.Length > 0 ? ": " + argument : string.Empty),
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearAccountRows();
            _accountsHeading.Dispose();
            _secrets = null;
        }

        base.Dispose(disposing);
    }

    private void Browse()
    {
        using var dialog = new OpenFileDialog { Filter = Strings.ExportFilter, CheckFileExists = true, Title = Strings.ImportFileTitle };
        if (File.Exists(_file.Text))
        {
            dialog.FileName = _file.Text;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _file.Text = dialog.FileName;
            ReadAndAsk();
        }
    }

    /// <summary>Reads the file and, when it has passwords and keys, asks for the export password straight away.</summary>
    private void ReadAndAsk()
    {
        ReadFile();
        AskExportPassword();
    }

    private void ReadFile()
    {
        _exported = null;
        _secrets = null;
        _declined = false;
        try
        {
            _exported = TaskExport.Read(File.ReadAllBytes(_file.Text.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            Ui.ShowError(this, Strings.Format(Strings.ImportFileCannotRead, ex.Message));
        }
        catch (ExportException ex)
        {
            Ui.ShowError(this, ex.Message);
        }

        _secretsRow.Visible = _exported?.Secrets is not null;
        ShowSecretsState();
        _from.Text = _exported is null ? string.Empty
            : Strings.Format(Strings.ImportFileFrom, _exported.Computer ?? "?", Ui.Time(_exported.ExportedAt), _exported.ProgramVersion ?? "?")
                + (_exported.Secrets is null ? " " + Strings.ImportFileNoSecrets : string.Empty);

        // A new file has its own accounts: the rows of the old one go.
        ClearAccountRows();
        Plan();
    }

    /// <summary>Asks for the export password of a file with passwords and keys that are not open yet.</summary>
    private void AskExportPassword()
    {
        if (_exported?.Secrets is null || _secrets is not null)
        {
            return;
        }

        var file = _exported;
        using var dialog = new ExportPasswordDialog(file);
        bool opened = dialog.ShowDialog(this) == DialogResult.OK && dialog.Secrets is not null;
        if (!ReferenceEquals(file, _exported))
        {
            return;
        }

        if (opened)
        {
            Opened(dialog.Secrets!);
        }
        else
        {
            Declined();
        }
    }

    private void Opened(ExportSecrets secrets)
    {
        _secrets = secrets;
        ShowSecretsState();

        // Accounts whose secret is in the file now need no row.
        ClearAccountRows();
        Plan();
    }

    private void Declined()
    {
        _declined = true;
        ShowSecretsState();
        UpdateAccounts();
    }

    private void ShowSecretsState()
    {
        bool open = _secrets is not null;
        _secretsState.ForeColor = open ? Glyphs.Green : Glyphs.WarningText;
        _secretsState.Text = open ? Strings.ImportFileOpened : Strings.ImportFileHasSecrets;
        _enterPassword.Visible = !open;
    }

    /// <summary>Fills the list from the file and what is on this computer, keeping the ticks of a list already shown.</summary>
    private void Plan()
    {
        var previous = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var shown in _tasks.Items.Cast<ListViewItem>().Where(i => i.Tag is ImportItem))
        {
            previous[((ImportItem)shown.Tag!).Task.Id] = shown.Checked;
        }

        var snapshot = _monitor.Snapshot;
        _plan = _exported is null ? null : new TaskImport(_exported, _secrets, snapshot?.Tasks ?? [], snapshot?.Credentials ?? []);
        string? selected = _tasks.SelectedItems.Count > 0 ? ((ImportItem)_tasks.SelectedItems[0].Tag!).Task.Id : null;
        _filling = true;
        _tasks.BeginUpdate();
        _tasks.Items.Clear();
        foreach (var item in _plan?.Items ?? [])
        {
            var task = item.Task;
            _tasks.Items.Add(new ListViewItem(
            [
                task.Name,
                Words.BackupType(task.BackupType),
                TaskDescriber.ScheduleWithCatchUp(task.Schedule),
                task.Enabled ? Strings.Yes : Strings.No,
                item.Here is null ? "—" : Strings.ImportHereReplaced,
                item.Remarks.Count == 0 ? "—" : Ui.Number(item.Remarks.Count),
            ])
            {
                Tag = item,

                // A task that is here already is replaced only when the person ticks it.
                Checked = previous.TryGetValue(task.Id, out bool ticked) ? ticked : item.Here is null,
            });
        }

        _tasks.EndUpdate();
        _filling = false;
        var again = _tasks.Items.Cast<ListViewItem>().FirstOrDefault(i => string.Equals(((ImportItem)i.Tag!).Task.Id, selected, StringComparison.OrdinalIgnoreCase))
            ?? (_tasks.Items.Count > 0 ? _tasks.Items[0] : null);
        if (again is not null)
        {
            again.Selected = true;
        }

        _summary.Text = _plan is null ? string.Empty : Strings.Format(Strings.ImportFileFound, _plan.Items.Count);
        ShowRemarks();
        UpdateAccounts();
    }

    private void ShowRemarks()
    {
        if (_tasks.SelectedItems.Count == 0 || _tasks.SelectedItems[0].Tag is not ImportItem item)
        {
            _remarks.Text = string.Empty;
            return;
        }

        var lines = new List<string> { TaskDescriber.Describe(item.Task), string.Empty };
        lines.AddRange(item.Remarks.Count == 0
            ? [Strings.ImportNoRemarks]
            : item.Remarks.Select(r => "• " + Describe(r)));
        _remarks.Text = string.Join(Environment.NewLine, lines);
    }

    /// <summary>One row per password the chosen tasks need that is neither here nor in the file. Rows still needed keep
    /// what was typed into them when tasks are ticked or unticked.</summary>
    private void UpdateAccounts()
    {
        if (_plan is null)
        {
            ClearAccountRows();
            _accountsPanel.Visible = false;
            _import.Enabled = false;
            return;
        }

        // Next to a file that is not opened, these rows were taken for the place of the export password: they appear
        // only once the person chose to go without the passwords of the file.
        var chosen = Chosen();
        var needed = _plan.SecretsLocked && !_declined ? [] : _plan.AccountsToAsk(chosen);
        _accountsHeading.Text = _plan.SecretsLocked ? Strings.ImportFileAccountsLocked : Strings.ImportFileAccounts;
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
                row = new AccountRow(account);
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

    private List<ImportItem> Chosen() => [.. _tasks.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => i.Tag).OfType<ImportItem>()];

    private void Import()
    {
        var chosen = Chosen();
        if (_plan is null || chosen.Count == 0)
        {
            return;
        }

        if (_plan.SecretsLocked && !_declined)
        {
            // The file is not opened and the person has not said to go without it: ask first, then they look again.
            AskExportPassword();
            return;
        }

        var typed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var useStored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _accountRows.Values)
        {
            if (row.UseStored)
            {
                useStored[row.Account.Credential.Id] = row.Account.Similar!.Id;
            }
            else
            {
                typed[row.Account.Credential.Id] = row.Password;
            }
        }

        var build = _plan.Build(chosen, typed, useStored);
        _import.Enabled = false;
        _cancel.Enabled = false;
        UseWaitCursor = true;
        Ui.Run(this, async () =>
        {
            try
            {
                await _monitor.RequestAsync(PipeProtocol.Methods.TasksImport, build.Request,
                    IpcJsonContext.Default.TasksImportRequest, IpcJsonContext.Default.OkResult);
                string text = Strings.Format(Strings.ImportFileDone, build.Request.Tasks.Count);
                if (build.SwitchedOff.Count > 0)
                {
                    text += Environment.NewLine + Environment.NewLine + Strings.Format(Strings.ImportFileSwitchedOff, string.Join(", ", build.SwitchedOff));
                }

                Ui.ShowInfo(this, text);
                DialogResult = DialogResult.OK;
            }
            finally
            {
                foreach (var credential in build.Request.Credentials)
                {
                    credential.Password = null;
                }

                if (!IsDisposed)
                {
                    _import.Enabled = true;
                    _cancel.Enabled = true;
                    UseWaitCursor = false;
                }
            }
        });
    }

    /// <summary>A password to type: the account, the tasks using it, and its password or "use the stored one".</summary>
    private sealed class AccountRow : IDisposable
    {
        private readonly Label _name;
        private readonly TextBox _password = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
        private readonly CheckBox _useStored = new() { Text = Strings.ImportUseStored, AutoSize = true };
        private readonly FlowLayoutPanel? _both;
        private readonly Label _tasks;

        public AccountRow(ImportAccount account)
        {
            Account = account;
            _name = new Label { Text = account.Credential.DisplayName, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) };
            _tasks = new Label
            {
                Text = Strings.Format(Strings.ImportAccountTasks, string.Join(", ", account.TaskNames)),
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(12, 6, 0, 6),
            };
            if (account.Similar is not null)
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

        public ImportAccount Account { get; }

        public bool UseStored => Account.Similar is not null && _useStored.Checked;

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

/// <summary>
/// Asks for the export password of a file and opens its passwords and keys with it. A wrong password is said in the
/// dialog, which stays open; «Without passwords from the file» closes it with the file not opened.
/// </summary>
internal sealed class ExportPasswordDialog : DialogForm
{
    private readonly ExportFile _file;
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly CheckBox _show = new() { Text = Strings.ShowPassword, AutoSize = true };
    private readonly Label _error = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Glyphs.ErrorText, Margin = new Padding(0, 2, 0, 4) };

    public ExportPasswordDialog(ExportFile file)
        : base(Strings.ExportPasswordTitle, new Size(580, 270))
    {
        _file = file;
        OkButton.Text = Strings.ImportFileOpen;
        CancelButtonControl.Text = Strings.ExportPasswordSkip;
        AddWide(new Label { Text = Strings.ExportPasswordAsk, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) });
        AddRow(Strings.ExportPassword, _password);
        AddWide(_show);
        AddWide(_error);
        AddNote(Strings.ExportPasswordSkipNote);
        EndBuild();
        _show.CheckedChanged += (_, _) => _password.UseSystemPasswordChar = !_show.Checked;
        Shown += (_, _) => _password.Focus();
    }

    /// <summary>The opened passwords and keys, after «Open».</summary>
    public ExportSecrets? Secrets { get; private set; }

    /// <summary>What went wrong with the last password (window tests).</summary>
    internal string ErrorText => _error.Text;

    /// <summary>Opens the file as «Open» does, and waits for it (window tests).</summary>
    internal void Open(string password) => Done(TryOpen(_file, password));

    internal static (ExportSecrets? Secrets, string? Error) TryOpen(ExportFile file, string password)
    {
        try
        {
            return (TaskExport.Open(file, password), null);
        }
        catch (ExportException ex)
        {
            return (null, ex.Message);
        }
    }

    protected override void OnOk()
    {
        string password = _password.Text;
        if (password.Length == 0)
        {
            ShowError(Strings.ExportPasswordEmpty);
            return;
        }

        _error.Text = string.Empty;
        SetBusy(true);
        Ui.Run(this, async () =>
        {
            try
            {
                // PBKDF2 takes a moment: not on the thread of the window.
                Done(await Task.Run(() => TryOpen(_file, password)));
            }
            finally
            {
                SetBusy(false);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _password.Clear();
        }

        base.Dispose(disposing);
    }

    private void Done((ExportSecrets? Secrets, string? Error) opened)
    {
        if (opened.Secrets is null)
        {
            ShowError(opened.Error ?? string.Empty);
            return;
        }

        Secrets = opened.Secrets;
        _password.Clear();
        DialogResult = DialogResult.OK;
    }

    private void ShowError(string text)
    {
        _error.Text = text;
        _password.Focus();
        _password.SelectAll();
    }
}
