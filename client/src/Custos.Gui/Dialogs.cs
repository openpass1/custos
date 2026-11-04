using Custos.Core.Ipc;
using Custos.Core.Model;

namespace Custos.Gui;

/// <summary>Base of the small dialogs: scaled, centred on the owner, with "OK" and "Cancel" at the bottom right.</summary>
internal abstract class DialogForm : Form
{
    protected DialogForm(string title, Size size)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = title;
        Icon = AppIcons.App();
        ClientSize = size;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;

        Body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        Body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 6, 12, 12),
        };
        buttons.Controls.Add(CancelButtonControl);
        buttons.Controls.Add(OkButton);
        AcceptButton = OkButton;
        CancelButton = CancelButtonControl;

        Controls.Add(Body);
        Controls.Add(buttons);
        ResumeLayout(performLayout: false);
        OkButton.Click += (_, _) => OnOk();
    }

    protected TableLayoutPanel Body { get; } = new() { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(16, 14, 16, 4), AutoScroll = true };

    protected Button OkButton { get; } = new() { Text = Strings.ButtonOk, AutoSize = true, MinimumSize = new Size(88, 0) };

    protected Button CancelButtonControl { get; } = new() { Text = Strings.ButtonCancel, AutoSize = true, MinimumSize = new Size(88, 0), DialogResult = DialogResult.Cancel };

    /// <summary>Called by "OK": check, save, and set <see cref="Form.DialogResult"/> when done.</summary>
    protected abstract void OnOk();

    protected void AddRow(string caption, Control control, Control? extra = null)
    {
        Body.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) });
        control.Margin = new Padding(0, 3, 0, 3);
        Body.Controls.Add(control);
        if (extra is null)
        {
            Body.Controls.Add(new Label { AutoSize = true });
        }
        else
        {
            extra.Margin = new Padding(6, 3, 0, 3);
            Body.Controls.Add(extra);
        }
    }

    protected void AddWide(Control control)
    {
        Body.Controls.Add(control);
        Body.SetColumnSpan(control, 3);
    }

    protected Label AddNote(string text)
    {
        var note = new Label { Text = text, AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 0, 6) };
        AddWide(note);
        return note;
    }

    /// <summary>Disables the buttons while a request to the service runs.</summary>
    protected void SetBusy(bool busy)
    {
        OkButton.Enabled = !busy;
        CancelButtonControl.Enabled = !busy;
        UseWaitCursor = busy;
    }
}

/// <summary>Adds or changes one source of a task: a folder, a file or a network folder with its account.</summary>
internal sealed class SourceDialog : DialogForm
{
    private readonly TaskSource _original;
    private readonly RadioButton _folder = new() { Text = Strings.SourceFolder, AutoSize = true };
    private readonly RadioButton _file = new() { Text = Strings.SourceFile, AutoSize = true };
    private readonly RadioButton _network = new() { Text = Strings.SourceNetwork, AutoSize = true };
    private readonly TextBox _path = new() { Dock = DockStyle.Fill };
    private readonly Button _browse = new() { Text = Strings.ButtonBrowse, AutoSize = true };
    private readonly CheckBox _recursive = new() { Text = Strings.SourceRecursive, AutoSize = true };
    private readonly Label _accountLabel = new() { Text = Strings.DestinationAccount, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) };
    private readonly CredentialPicker _account;
    private readonly PictureBox _accountHelp;

    public SourceDialog(EditorContext context, TaskSource source)
        : base(source.Path.Length == 0 ? Strings.SourceAddTitle : Strings.SourceEditTitle, new Size(620, 300))
    {
        _original = source;
        _account = new CredentialPicker(context) { Dock = DockStyle.Fill };
        var kinds = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        kinds.Controls.AddRange([_folder, _file, _network]);
        AddWide(kinds);
        AddRow(Strings.SourcePath, _path, _browse);
        AddWide(_recursive);
        Body.Controls.Add(_accountLabel);
        _account.Margin = new Padding(0, 3, 0, 3);
        Body.Controls.Add(_account);
        _accountHelp = Ui.HelpIcon(context.ToolTip, Strings.SourceAccountHelp);
        Body.Controls.Add(_accountHelp);
        AddNote(Strings.SourceNote);

        switch (source.Kind)
        {
            case SourceKinds.File:
                _file.Checked = true;
                break;
            case SourceKinds.Unc:
                _network.Checked = true;
                break;
            default:
                _folder.Checked = true;
                break;
        }

        _path.Text = source.Path;
        _recursive.Checked = source.Recursive;
        _account.SelectedId = source.CredentialId;
        UpdateFields();

        _folder.CheckedChanged += (_, _) => UpdateFields();
        _file.CheckedChanged += (_, _) => UpdateFields();
        _network.CheckedChanged += (_, _) => UpdateFields();
        _path.TextChanged += (_, _) =>
        {
            if (_path.Text.StartsWith(@"\\", StringComparison.Ordinal) && !_network.Checked)
            {
                _network.Checked = true;
            }
        };
        _browse.Click += (_, _) => Browse();
    }

    /// <summary>The source as entered, after "OK".</summary>
    public TaskSource Source { get; private set; } = new();

    protected override void OnOk()
    {
        string path = _path.Text.Trim().Trim('"');
        string? problem = path.Length == 0 ? Strings.ErrorPathRequired
            : !Path.IsPathFullyQualified(path) ? Strings.Format(Strings.ErrorPathNotAbsolute, path)
            : _network.Checked && !path.StartsWith(@"\\", StringComparison.Ordinal) ? Strings.Format(Strings.ErrorNotUnc, path)
            : !_network.Checked && path.StartsWith(@"\\", StringComparison.Ordinal) ? Strings.Format(Strings.ErrorUncNeedsNetwork, path)
            : null;
        if (problem is not null)
        {
            Ui.ShowWarning(this, problem);
            return;
        }

        Source = new TaskSource
        {
            Id = _original.Id,
            Kind = _file.Checked ? SourceKinds.File : _network.Checked ? SourceKinds.Unc : SourceKinds.Folder,
            Path = path,
            Recursive = _file.Checked || _recursive.Checked,
            CredentialId = _network.Checked ? _account.SelectedId : null,
            ExtensionData = _original.ExtensionData,
        };
        DialogResult = DialogResult.OK;
    }

    private void UpdateFields()
    {
        _recursive.Enabled = !_file.Checked;
        _accountLabel.Visible = _network.Checked;
        _account.Visible = _network.Checked;
        _accountHelp.Visible = _network.Checked;
    }

    private void Browse()
    {
        if (_file.Checked)
        {
            using var dialog = new OpenFileDialog { CheckFileExists = true, Title = Strings.SourceFile };
            if (File.Exists(_path.Text))
            {
                dialog.FileName = _path.Text;
            }

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _path.Text = dialog.FileName;
            }

            return;
        }

        using var folders = new FolderBrowserDialog { ShowNewFolderButton = false, UseDescriptionForTitle = true, Description = Strings.SourceFolder };
        if (Directory.Exists(_path.Text))
        {
            folders.InitialDirectory = _path.Text;
        }

        if (folders.ShowDialog(this) == DialogResult.OK)
        {
            _path.Text = folders.SelectedPath;
        }
    }
}

/// <summary>
/// Adds a Windows account for network folders, or changes its password. The password goes straight to the service,
/// which keeps it encrypted with DPAPI; it is never shown or sent back.
/// </summary>
internal sealed class CredentialDialog : DialogForm
{
    private readonly ServiceMonitor _monitor;
    private readonly Credential? _existing;
    private readonly TextBox _user = new() { Dock = DockStyle.Fill };
    private readonly TextBox _domain = new() { Dock = DockStyle.Fill };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly CheckBox _show = new() { Text = Strings.ShowPassword, AutoSize = true };

    public CredentialDialog(ServiceMonitor monitor, Credential? existing)
        : base(existing is null ? Strings.AccountAddTitle : Strings.Format(Strings.AccountEditTitle, existing.DisplayName), new Size(520, 300))
    {
        _monitor = monitor;
        _existing = existing;
        AddRow(Strings.AccountUser, _user);
        AddRow(Strings.AccountDomain, _domain);
        AddNote(Strings.AccountDomainNote);
        AddRow(Strings.AccountPassword, _password);
        AddWide(_show);
        AddNote(existing is null ? Strings.AccountPasswordNote : Strings.AccountKeepPasswordNote);
        if (existing is not null)
        {
            _user.Text = existing.User;
            _domain.Text = existing.Domain ?? string.Empty;
        }

        _show.CheckedChanged += (_, _) => _password.UseSystemPasswordChar = !_show.Checked;
    }

    /// <summary>The account as the service stored it, after "OK".</summary>
    public Credential? Saved { get; private set; }

    protected override void OnOk()
    {
        string user = _user.Text.Trim();
        string domain = _domain.Text.Trim();
        if (domain.Length == 0 && user.IndexOf('\\', StringComparison.Ordinal) is int slash and > 0)
        {
            domain = user[..slash];
            user = user[(slash + 1)..];
        }

        if (user.Length == 0)
        {
            Ui.ShowWarning(this, Strings.ErrorUserRequired);
            return;
        }

        string? password = _password.Text.Length > 0 ? _password.Text : null;
        if (_existing is null && password is null)
        {
            Ui.ShowWarning(this, Strings.ErrorPasswordRequired);
            return;
        }

        var credential = new Credential
        {
            Id = _existing?.Id ?? TaskDefinition.NewId(),
            Kind = CredentialKinds.Windows,
            User = user,
            Domain = domain.Length == 0 ? null : domain,
            ExtensionData = _existing?.ExtensionData,
        };
        SetBusy(true);
        Ui.Run(this, async () =>
        {
            try
            {
                var result = await _monitor.RequestAsync(PipeProtocol.Methods.CredentialSave, new CredentialInput { Credential = credential, Password = password },
                    IpcJsonContext.Default.CredentialInput, IpcJsonContext.Default.CredentialSaveResult);
                Saved = result.Credential;
                _password.Clear();
                DialogResult = DialogResult.OK;
            }
            finally
            {
                SetBusy(false);
            }
        });
    }
}

/// <summary>
/// Settings of the program: log detail and retention, copy priority, language of the service's messages, and the
/// stored accounts. Everyone may look; only administrators may change (section 4).
/// </summary>
internal sealed class SettingsForm : DialogForm
{
    private static readonly string[] Languages = ["auto", "ru", "en"];

    private readonly ServiceMonitor _monitor;
    private readonly ComboBox _logLevel = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly NumericUpDown _retention = new() { Minimum = 1, Maximum = 3650, Width = 80 };
    private readonly ComboBox _priority = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly ListView _accounts = new()
    {
        Dock = DockStyle.Fill,
        Height = 150,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
    };

    private readonly Button _addAccount = new() { Text = Strings.ButtonAdd, AutoSize = true, MinimumSize = new Size(110, 0) };
    private readonly Button _editAccount = new() { Text = Strings.ButtonChangePassword, AutoSize = true, MinimumSize = new Size(110, 0) };
    private readonly Button _deleteAccount = new() { Text = Strings.ButtonRemove, AutoSize = true, MinimumSize = new Size(110, 0) };
    private readonly ToolTip _toolTip = new();
    private readonly bool _canEdit;

    public SettingsForm(ServiceMonitor monitor)
        : base(Strings.SettingsTitle, new Size(640, 520))
    {
        _monitor = monitor;
        _canEdit = monitor.CanEdit;
        _logLevel.Items.AddRange([Strings.LogLevelSummary, Strings.LogLevelFiles]);
        _priority.Items.AddRange([Strings.PriorityLow, Strings.PriorityNormal]);
        _language.Items.AddRange([Strings.LanguageAuto, Strings.LanguageRussian, Strings.LanguageEnglish]);
        _accounts.Columns.Add(Strings.ColumnAccount, 220);
        _accounts.Columns.Add(Strings.ColumnUsedBy, 330);

        var heading = new Label { Text = Strings.SettingsLogs, AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        heading.Font = new Font(heading.Font, FontStyle.Bold);
        AddWide(heading);
        AddRow(Strings.DefaultLogLevel, _logLevel, Ui.HelpIcon(_toolTip, Strings.DefaultLogLevelHelp));
        AddRow(Strings.LogRetention, _retention);
        AddRow(Strings.CopyPriority, _priority, Ui.HelpIcon(_toolTip, Strings.CopyPriorityHelp));
        AddRow(Strings.ServiceLanguage, _language, Ui.HelpIcon(_toolTip, Strings.ServiceLanguageHelp));

        var accountsHeading = new Label { Text = Strings.SettingsAccounts, AutoSize = true, Margin = new Padding(0, 14, 0, 4) };
        accountsHeading.Font = heading.Font;
        AddWide(accountsHeading);
        AddNote(Strings.SettingsAccountsNote);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(8, 0, 0, 0) };
        buttons.Controls.AddRange([_addAccount, _editAccount, _deleteAccount]);
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(_accounts, 0, 0);
        row.Controls.Add(buttons, 1, 0);
        AddWide(row);
        if (!_canEdit)
        {
            AddNote(Strings.SettingsReadOnly);
        }

        var settings = monitor.Snapshot?.Settings ?? new AgentSettings();
        _logLevel.SelectedIndex = settings.DefaultLogLevel == LogLevels.Files ? 1 : 0;
        _retention.Value = Math.Clamp(settings.LogRetentionDays, 1, 3650);
        _priority.SelectedIndex = settings.ProcessPriority == "normal" ? 1 : 0;
        int language = Array.IndexOf(Languages, settings.Language);
        _language.SelectedIndex = language < 0 ? 0 : language;

        foreach (var control in new Control[] { _logLevel, _retention, _priority, _language, _addAccount })
        {
            control.Enabled = _canEdit;
        }

        OkButton.Visible = _canEdit;
        CancelButtonControl.Text = _canEdit ? Strings.ButtonCancel : Strings.ButtonClose;

        _accounts.SelectedIndexChanged += (_, _) => UpdateButtons();
        _addAccount.Click += (_, _) => AddAccount();
        _editAccount.Click += (_, _) => EditAccount();
        _deleteAccount.Click += (_, _) => DeleteAccount();
        _monitor.SnapshotChanged += OnSnapshotChanged;
        heading.Disposed += (_, _) => heading.Font.Dispose();
        FillAccounts();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _monitor.SnapshotChanged -= OnSnapshotChanged;
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnOk()
    {
        if (_monitor.Snapshot?.Settings is not { } current)
        {
            Ui.ShowError(this, Strings.ProblemNoConnection);
            return;
        }

        // A copy: the snapshot keeps what the service said until it says otherwise.
        var json = IpcJsonContext.Default.AgentSettings;
        var settings = System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(current, json), json)!;

        settings.DefaultLogLevel = _logLevel.SelectedIndex == 1 ? LogLevels.Files : LogLevels.Summary;
        settings.LogRetentionDays = (int)_retention.Value;
        settings.ProcessPriority = _priority.SelectedIndex == 1 ? "normal" : "low";
        settings.Language = Languages[Math.Max(0, _language.SelectedIndex)];
        SetBusy(true);
        Ui.Run(this, async () =>
        {
            try
            {
                await _monitor.RequestAsync(PipeProtocol.Methods.SettingsSave, new SettingsSaveRequest { Settings = settings },
                    IpcJsonContext.Default.SettingsSaveRequest, IpcJsonContext.Default.OkResult);
                DialogResult = DialogResult.OK;
            }
            finally
            {
                SetBusy(false);
            }
        });
    }

    private void OnSnapshotChanged(object? sender, EventArgs e) => FillAccounts();

    private void FillAccounts()
    {
        var snapshot = _monitor.Snapshot;
        string? selected = _accounts.SelectedItems.Count > 0 ? (string)_accounts.SelectedItems[0].Tag! : null;
        _accounts.BeginUpdate();
        _accounts.Items.Clear();
        foreach (var credential in (snapshot?.Credentials ?? []).Where(c => c.Kind == CredentialKinds.Windows).OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var users = snapshot!.Tasks.Where(t => UsesCredential(t, credential.Id)).Select(t => t.Name).ToList();
            var item = new ListViewItem([credential.DisplayName, users.Count == 0 ? Strings.AccountNotUsed : string.Join(", ", users)]) { Tag = credential.Id };
            _accounts.Items.Add(item);
            item.Selected = string.Equals(credential.Id, selected, StringComparison.OrdinalIgnoreCase);
        }

        _accounts.EndUpdate();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool any = _accounts.SelectedItems.Count > 0;
        _editAccount.Enabled = _canEdit && any;
        _deleteAccount.Enabled = _canEdit && any;
    }

    private Credential? SelectedAccount() => _accounts.SelectedItems.Count > 0 && _monitor.Snapshot is { } snapshot
        ? snapshot.Credentials.FirstOrDefault(c => string.Equals(c.Id, (string)_accounts.SelectedItems[0].Tag!, StringComparison.OrdinalIgnoreCase))
        : null;

    private void AddAccount()
    {
        using var dialog = new CredentialDialog(_monitor, null);
        dialog.ShowDialog(this);
    }

    private void EditAccount()
    {
        if (SelectedAccount() is not { } account)
        {
            return;
        }

        using var dialog = new CredentialDialog(_monitor, account);
        dialog.ShowDialog(this);
    }

    private void DeleteAccount()
    {
        if (SelectedAccount() is not { } account || !Ui.Confirm(this, Strings.Format(Strings.ConfirmDeleteAccount, account.DisplayName)))
        {
            return;
        }

        Ui.Run(this, () => _monitor.RequestAsync(PipeProtocol.Methods.CredentialDelete, new CredentialRequest { CredentialId = account.Id },
            IpcJsonContext.Default.CredentialRequest, IpcJsonContext.Default.OkResult));
    }

    private static bool UsesCredential(TaskDefinition task, string id) =>
        task.Sources.Any(s => string.Equals(s.CredentialId, id, StringComparison.OrdinalIgnoreCase))
        || task.Destinations.Any(d => string.Equals(d.CredentialId, id, StringComparison.OrdinalIgnoreCase));
}
