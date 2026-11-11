using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Model;

namespace Custos.Gui;

/// <summary>
/// Base of the small dialogs: scaled, centred on the owner, with "OK" and "Cancel" at the bottom right. The layout stays
/// suspended until the derived class calls <see cref="EndBuild"/>, so that all controls are scaled to the screen together.
/// </summary>
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
        OkButton.Click += (_, _) => OnOk();
    }

    protected TableLayoutPanel Body { get; } = new() { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(16, 14, 16, 4), AutoScroll = true };

    protected Button OkButton { get; } = new() { Text = Strings.ButtonOk, AutoSize = true, MinimumSize = new Size(88, 0) };

    protected Button CancelButtonControl { get; } = new() { Text = Strings.ButtonCancel, AutoSize = true, MinimumSize = new Size(88, 0), DialogResult = DialogResult.Cancel };

    /// <summary>Called by "OK": check, save, and set <see cref="Form.DialogResult"/> when done.</summary>
    protected abstract void OnOk();

    /// <summary>The last call of the constructor of a dialog, after all its controls are added.</summary>
    protected void EndBuild() => ResumeLayout(performLayout: true);

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
        : base(source.Path.Length == 0 ? Strings.SourceAddTitle : Strings.SourceEditTitle, new Size(620, 340))
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
        AddNote(Strings.PathVariablesNote);
        EndBuild();

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
            : !PathVariables.LooksAbsolute(path) ? Strings.Format(Strings.ErrorPathNotAbsolute, path)
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
/// Adds or changes one destination of a task: a local folder, a network folder with its account, or a USB disk found by
/// its label (section 6.3), with "Check access". A destination of a kind of a later version is shown, not changed.
/// </summary>
internal sealed class DestinationDialog : DialogForm
{
    private static readonly string[] Kinds = [DestinationKinds.Local, DestinationKinds.Unc, DestinationKinds.Usb];

    private readonly EditorContext _context;
    private readonly TaskDestination _original;
    private readonly bool _supported;
    private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly Label _pathLabel = Caption(Strings.DestinationFolder);
    private readonly TextBox _path = new() { Dock = DockStyle.Fill };
    private readonly Button _browse = new() { Text = Strings.ButtonBrowse, AutoSize = true };
    private readonly Label _labelLabel = Caption(Strings.DestinationUsbLabel);
    private readonly ComboBox _usbLabel = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 220 };
    private readonly PictureBox _usbHelp;
    private readonly Label _accountLabel = Caption(Strings.DestinationAccount);
    private readonly CredentialPicker _account;
    private readonly PictureBox _accountHelp;
    private readonly Label _unsupported = new() { Text = Strings.DestinationLater, AutoSize = true, Dock = DockStyle.Fill, ForeColor = Glyphs.WarningText, Margin = new Padding(0, 4, 0, 6) };
    private readonly Button _test = new() { Text = Strings.ButtonTestAccess, AutoSize = true };
    private readonly Label _testResult = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(8, 6, 0, 0) };

    public DestinationDialog(EditorContext context, TaskDestination destination, bool isNew)
        : base(isNew ? Strings.DestinationAddTitle : Strings.DestinationEditTitle, new Size(660, 360))
    {
        _context = context;
        _original = destination;
        _supported = DestinationKinds.IsSupported(destination.Kind);
        _account = new CredentialPicker(context) { Dock = DockStyle.Fill };
        _usbHelp = Ui.HelpIcon(context.ToolTip, Strings.DestinationUsbHelp);
        _accountHelp = Ui.HelpIcon(context.ToolTip, Strings.DestinationAccountHelp);
        foreach (string kind in Kinds)
        {
            _kind.Items.Add(DestinationsPage.KindText(kind));
        }

        if (!_supported)
        {
            // A kind of a later version, set from the server: shown, not editable here.
            _kind.Items.Add(DestinationsPage.KindText(destination.Kind));
        }

        AddGridRow(Caption(Strings.DestinationKind), _kind, Ui.HelpIcon(context.ToolTip, Strings.DestinationKindHelp));
        AddGridRow(_pathLabel, _path, _browse);
        AddGridRow(_labelLabel, _usbLabel, _usbHelp);
        AddGridRow(_accountLabel, _account, _accountHelp);
        AddWide(_unsupported);
        AddNote(Strings.PathVariablesNote);
        var testRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 6, 0, 0) };
        testRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        testRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        testRow.Controls.Add(_test, 0, 0);
        testRow.Controls.Add(_testResult, 1, 0);
        AddWide(testRow);
        EndBuild();

        FillUsbLabels();
        int index = Array.IndexOf(Kinds, destination.Kind);
        _kind.SelectedIndex = index >= 0 ? index : Kinds.Length;
        _path.Text = destination.Path;
        _usbLabel.Text = destination.UsbLabel ?? string.Empty;
        _account.SelectedId = destination.CredentialId;
        UpdateFields();

        _kind.SelectedIndexChanged += (_, _) => UpdateFields();
        _path.TextChanged += (_, _) =>
        {
            _testResult.Text = string.Empty;
            if (_path.Text.StartsWith(@"\\", StringComparison.Ordinal) && Kind == DestinationKinds.Local)
            {
                _kind.SelectedIndex = Array.IndexOf(Kinds, DestinationKinds.Unc);
            }
        };
        _browse.Click += (_, _) => Browse();
        _test.Click += (_, _) => TestAccess();
    }

    /// <summary>The destination as entered, after "OK".</summary>
    public TaskDestination Destination { get; private set; } = new();

    /// <summary>What is wrong with a destination of a supported kind, in words, or null.</summary>
    public static string? Check(TaskDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        string path = destination.Path;
        return destination.Kind == DestinationKinds.Usb
                ? string.IsNullOrWhiteSpace(destination.UsbLabel) ? Strings.ErrorUsbLabelRequired : null
            : string.IsNullOrWhiteSpace(path) ? Strings.ErrorDestinationFolderRequired
            : !PathVariables.LooksAbsolute(path) ? Strings.Format(Strings.ErrorPathNotAbsolute, path)
            : destination.Kind == DestinationKinds.Unc && !path.StartsWith(@"\\", StringComparison.Ordinal) ? Strings.Format(Strings.ErrorNotUnc, path)
            : destination.Kind == DestinationKinds.Local && path.StartsWith(@"\\", StringComparison.Ordinal) ? Strings.Format(Strings.ErrorUncNeedsNetwork, path)
            : null;
    }

    protected override void OnOk()
    {
        if (!_supported)
        {
            Destination = _original;
            DialogResult = DialogResult.OK;
            return;
        }

        var destination = Build();
        if (Check(destination) is { } problem)
        {
            Ui.ShowWarning(this, problem);
            return;
        }

        Destination = destination;
        DialogResult = DialogResult.OK;
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) };

    private string Kind => _kind.SelectedIndex >= 0 && _kind.SelectedIndex < Kinds.Length ? Kinds[_kind.SelectedIndex] : _original.Kind;

    private void AddGridRow(Control caption, Control control, Control extra)
    {
        control.Margin = new Padding(0, 3, 0, 3);
        extra.Margin = new Padding(6, 3, 0, 3);
        Body.Controls.Add(caption);
        Body.Controls.Add(control);
        Body.Controls.Add(extra);
    }

    /// <summary>The destination from the fields; what the dialog does not show is kept as it was.</summary>
    private TaskDestination Build()
    {
        string kind = Kind;
        string path = _path.Text.Trim().Trim('"');
        return new TaskDestination
        {
            Id = _original.Id,
            Kind = kind,
            Path = kind == DestinationKinds.Usb ? DestinationKinds.UsbFolder(path) : path,
            UsbLabel = kind == DestinationKinds.Usb ? _usbLabel.Text.Trim() : null,
            CredentialId = kind == DestinationKinds.Unc ? _account.SelectedId : null,
            Host = _original.Host,
            Port = _original.Port,
            Endpoint = _original.Endpoint,
            Bucket = _original.Bucket,
            Region = _original.Region,
            DisconnectAfter = _original.DisconnectAfter,
            SpeedLimitKbps = _original.SpeedLimitKbps,
            ExtensionData = _original.ExtensionData,
        };
    }

    private void UpdateFields()
    {
        string kind = Kind;
        bool usb = kind == DestinationKinds.Usb;
        bool unc = kind == DestinationKinds.Unc;
        _pathLabel.Text = usb ? Strings.DestinationFolderOnDisk : Strings.DestinationFolder;
        _browse.Visible = kind == DestinationKinds.Local;
        _labelLabel.Visible = _usbLabel.Visible = _usbHelp.Visible = usb;
        _accountLabel.Visible = _account.Visible = _accountHelp.Visible = unc;
        _unsupported.Visible = !_supported;
        _kind.Enabled = _path.Enabled = _test.Enabled = _supported;
        _testResult.Text = string.Empty;
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog { ShowNewFolderButton = true, UseDescriptionForTitle = true, Description = Strings.DestinationFolder };
        if (Directory.Exists(_path.Text))
        {
            dialog.InitialDirectory = _path.Text;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _path.Text = dialog.SelectedPath;
        }
    }

    /// <summary>Labels of the disks connected now, for the USB label list.</summary>
    private void FillUsbLabels()
    {
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
    }

    private void TestAccess()
    {
        var destination = Build();
        if (Check(destination) is { } problem)
        {
            _testResult.ForeColor = Glyphs.ErrorText;
            _testResult.Text = problem;
            return;
        }

        _test.Enabled = false;
        _testResult.ForeColor = SystemColors.GrayText;
        _testResult.Text = Strings.TestRunning;
        Ui.Run(this, async () =>
        {
            try
            {
                var result = await _context.Monitor.RequestAsync(PipeProtocol.Methods.DestinationTest, new DestinationTestRequest
                {
                    Destination = destination,
                    TaskName = _context.TaskName,
                    BackupType = _context.BackupType,
                    ProfileSid = _context.ProfileSid,
                },
                    IpcJsonContext.Default.DestinationTestRequest, IpcJsonContext.Default.DestinationTestResult, TimeSpan.FromMinutes(2));
                _testResult.ForeColor = result.Ok ? Glyphs.Green : Glyphs.ErrorText;
                _testResult.Text = result.Ok
                    ? Strings.Format(Strings.TestOk, result.Folder, result.FreeBytes is { } free ? Custos.Core.Text.Sizes.Format(free) : "?")
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

/// <summary>
/// Adds or changes one event (section 6.9): a program or script with its arguments, or a Windows service to stop or
/// start, each with a time limit.
/// </summary>
internal sealed class EventDialog : DialogForm
{
    private readonly TaskEvent _original;
    private readonly RadioButton _run = new() { Text = Strings.EventKindRun, AutoSize = true };
    private readonly RadioButton _stop = new() { Text = Strings.EventKindStop, AutoSize = true };
    private readonly RadioButton _start = new() { Text = Strings.EventKindStart, AutoSize = true };
    private readonly Label _programLabel = Caption(Strings.EventProgram);
    private readonly TextBox _program = new() { Dock = DockStyle.Fill };
    private readonly Button _browse = new() { Text = Strings.ButtonBrowse, AutoSize = true };
    private readonly Label _argumentsLabel = Caption(Strings.EventArguments);
    private readonly TextBox _arguments = new() { Dock = DockStyle.Fill };
    private readonly Label _serviceLabel = Caption(Strings.EventService);
    private readonly ComboBox _service = new() { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
    private readonly NumericUpDown _timeout = new() { Minimum = 1, Maximum = 10080, Width = 80 };
    private readonly int _shownMinutes;

    public EventDialog(EditorContext context, TaskEvent item, bool isNew)
        : base(isNew ? Strings.EventAddTitle : Strings.EventEditTitle, new Size(640, 290))
    {
        _original = item;
        var kinds = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        kinds.Controls.AddRange([_run, _stop, _start]);
        AddWide(kinds);
        AddGridRow(_programLabel, _program, _browse);
        AddGridRow(_argumentsLabel, _arguments, new Label { AutoSize = true });
        AddGridRow(_serviceLabel, _service, new Label { AutoSize = true });
        AddGridRow(Caption(Strings.EventTimeout), _timeout, Ui.HelpIcon(context.ToolTip, Strings.EventTimeoutHelp));
        AddNote(Strings.EventProgramHelp);
        EndBuild();

        FillServices();
        _run.Checked = item.Kind is not (TaskEventKinds.ServiceStop or TaskEventKinds.ServiceStart);
        _stop.Checked = item.Kind == TaskEventKinds.ServiceStop;
        _start.Checked = item.Kind == TaskEventKinds.ServiceStart;
        _program.Text = item.Command ?? string.Empty;
        _arguments.Text = item.Args;
        _service.Text = item.Service ?? string.Empty;
        _shownMinutes = (int)Math.Clamp((item.TimeoutS + 59) / 60, _timeout.Minimum, _timeout.Maximum);
        _timeout.Value = _shownMinutes;
        UpdateFields();

        _run.CheckedChanged += (_, _) => UpdateFields();
        _stop.CheckedChanged += (_, _) => UpdateFields();
        _start.CheckedChanged += (_, _) => UpdateFields();
        _browse.Click += (_, _) => Browse();
    }

    /// <summary>The event as entered, after "OK".</summary>
    public TaskEvent Event { get; private set; } = new();

    protected override void OnOk()
    {
        string kind = _stop.Checked ? TaskEventKinds.ServiceStop : _start.Checked ? TaskEventKinds.ServiceStart : TaskEventKinds.Run;
        string program = _program.Text.Trim().Trim('"');
        string service = _service.Text.Trim();
        string? problem = kind == TaskEventKinds.Run
            ? program.Length == 0 ? Strings.ErrorEventProgramRequired : null
            : service.Length == 0 ? Strings.ErrorEventServiceRequired : null;
        if (problem is not null)
        {
            Ui.ShowWarning(this, problem);
            return;
        }

        int minutes = (int)_timeout.Value;
        Event = new TaskEvent
        {
            Kind = kind,
            Command = kind == TaskEventKinds.Run ? program : null,
            Args = kind == TaskEventKinds.Run ? _arguments.Text.Trim() : string.Empty,
            Service = kind == TaskEventKinds.Run ? null : service,
            // Seconds that do not make whole minutes stay as they were unless the minutes were changed.
            TimeoutS = minutes == _shownMinutes ? _original.TimeoutS : minutes * 60,
            ExtensionData = _original.ExtensionData,
        };
        DialogResult = DialogResult.OK;
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) };

    private void AddGridRow(Control caption, Control control, Control extra)
    {
        control.Margin = new Padding(0, 3, 0, 3);
        extra.Margin = new Padding(6, 3, 0, 3);
        Body.Controls.Add(caption);
        Body.Controls.Add(control);
        Body.Controls.Add(extra);
    }

    private void UpdateFields()
    {
        bool run = _run.Checked;
        _programLabel.Visible = _program.Visible = _browse.Visible = run;
        _argumentsLabel.Visible = _arguments.Visible = run;
        _serviceLabel.Visible = _service.Visible = !run;
    }

    /// <summary>The services of this computer by name, for the list.</summary>
    private void FillServices()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            // The services (not the drivers) as the registry lists them: readable by every user.
            using var services = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            var names = new List<string>();
            foreach (string name in services?.GetSubKeyNames() ?? [])
            {
                using var key = services!.OpenSubKey(name);
                if (key?.GetValue("Type") is int type && (type & 0x30) != 0 && key.GetValue("ImagePath") is not null)
                {
                    names.Add(name);
                }
            }

            _service.Items.AddRange([.. names.Order(StringComparer.OrdinalIgnoreCase)]);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // The list is only a help: the name can be typed.
        }
    }

    private void Browse()
    {
        using var dialog = new OpenFileDialog { CheckFileExists = true, Title = Strings.EventProgram, Filter = Strings.EventProgramFilter };
        if (File.Exists(_program.Text))
        {
            dialog.FileName = _program.Text;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _program.Text = dialog.FileName;
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
        EndBuild();
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
/// Sets the archive password of a task (section 6.5). It is stored by the service like an account password (DPAPI,
/// kind <c>archive_password</c>) and never shown again.
/// </summary>
internal sealed class ArchivePasswordDialog : DialogForm
{
    private readonly ServiceMonitor _monitor;
    private readonly string? _existingId;
    private readonly string _taskName;
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TextBox _repeat = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly CheckBox _show = new() { Text = Strings.ShowPassword, AutoSize = true };

    public ArchivePasswordDialog(ServiceMonitor monitor, string? existingId, string taskName)
        : base(Strings.ArchivePasswordTitle, new Size(540, 300))
    {
        _monitor = monitor;
        _existingId = existingId;
        _taskName = taskName;
        AddRow(Strings.AccountPassword, _password);
        AddRow(Strings.ArchivePasswordRepeat, _repeat);
        AddWide(_show);
        AddNote(existingId is null ? Strings.ArchivePasswordWarning : Strings.ArchivePasswordChangeNote);
        EndBuild();
        _show.CheckedChanged += (_, _) =>
        {
            _password.UseSystemPasswordChar = !_show.Checked;
            _repeat.UseSystemPasswordChar = !_show.Checked;
        };
    }

    /// <summary>The id of the stored password, after "OK".</summary>
    public string? SavedId { get; private set; }

    protected override void OnOk()
    {
        if (_password.Text.Length == 0)
        {
            Ui.ShowWarning(this, Strings.ErrorPasswordRequired);
            return;
        }

        if (!string.Equals(_password.Text, _repeat.Text, StringComparison.Ordinal))
        {
            Ui.ShowWarning(this, Strings.ArchivePasswordMismatch);
            return;
        }

        var credential = new Credential
        {
            Id = _existingId ?? TaskDefinition.NewId(),
            Kind = CredentialKinds.ArchivePassword,
            User = Strings.Format(Strings.ArchivePasswordName, _taskName.Length == 0 ? Strings.ArchivePasswordNewTask : _taskName),
        };
        string password = _password.Text;
        SetBusy(true);
        Ui.Run(this, async () =>
        {
            try
            {
                var result = await _monitor.RequestAsync(PipeProtocol.Methods.CredentialSave, new CredentialInput { Credential = credential, Password = password },
                    IpcJsonContext.Default.CredentialInput, IpcJsonContext.Default.CredentialSaveResult);
                SavedId = result.Credential.Id;
                _password.Clear();
                _repeat.Clear();
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
    private readonly Label _archiver = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 2) };
    private readonly Button _sevenZipCheck = new() { Text = Strings.SevenZipCheckButton, AutoSize = true, Margin = new Padding(8, 0, 0, 0) };

    public SettingsForm(ServiceMonitor monitor)
        : base(Strings.SettingsTitle, new Size(640, 620))
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

        var archiverHeading = new Label { Text = Strings.Settings7Zip, AutoSize = true, Margin = new Padding(0, 14, 0, 4) };
        archiverHeading.Font = heading.Font;
        AddWide(archiverHeading);
        var archiverRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        archiverRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        archiverRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        archiverRow.Controls.Add(_archiver, 0, 0);
        archiverRow.Controls.Add(_sevenZipCheck, 1, 0);
        AddWide(archiverRow);
        AddNote(Strings.Settings7ZipNote);
        ShowArchiver();
        bool admin = monitor.Status.Hello?.IsAdmin == true;
        _sevenZipCheck.Enabled = admin && monitor.IsConnected;
        if (!admin)
        {
            _toolTip.SetToolTip(_sevenZipCheck, Strings.SevenZipAdminOnly);
        }

        _sevenZipCheck.Click += (_, _) => Ui.Run(this, CheckSevenZipAsync);

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

        EndBuild();

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

    /// <summary>The version of 7z.dll the service uses, or what is wrong with it.</summary>
    private void ShowArchiver()
    {
        var snapshot = _monitor.Snapshot;
        _archiver.Text = snapshot?.SevenZipProblem is { } problem ? Strings.Format(Strings.Settings7ZipProblem, problem)
            : Strings.Format(Strings.Settings7ZipVersion, snapshot?.SevenZipVersion ?? "—");
        _archiver.ForeColor = snapshot?.SevenZipProblem is null ? SystemColors.ControlText : Glyphs.WarningText;
    }

    /// <summary>«Проверить обновление 7-Zip»: asks the service, which asks GitHub; updates only after "Yes".</summary>
    private async Task CheckSevenZipAsync()
    {
        var json = IpcJsonContext.Default;
        _sevenZipCheck.Enabled = false;
        UseWaitCursor = true;
        try
        {
            var check = await _monitor.RequestAsync(PipeProtocol.Methods.SevenZipCheck, EmptyRequest.Instance,
                json.EmptyRequest, json.SevenZipCheckResult, TimeSpan.FromMinutes(1));
            UseWaitCursor = false;
            if (check.Error is { } error)
            {
                Ui.ShowWarning(this, error);
                return;
            }

            if (!check.UpdateAvailable)
            {
                Ui.ShowInfo(this, Strings.Format(Strings.SevenZipNothingNew, check.Installed ?? "—", check.Latest));
                return;
            }

            if (!Ui.Confirm(this, Strings.Format(Strings.SevenZipAskUpdate, check.Installed ?? "—", check.Latest)))
            {
                return;
            }

            UseWaitCursor = true;
            _archiver.Text = Strings.Format(Strings.SevenZipUpdatingTo, check.Latest);
            var result = await _monitor.RequestAsync(PipeProtocol.Methods.SevenZipUpdate, EmptyRequest.Instance,
                json.EmptyRequest, json.OkResult, TimeSpan.FromMinutes(15));
            UseWaitCursor = false;
            ShowArchiver();
            if (result.Ok)
            {
                Ui.ShowInfo(this, result.Message ?? Strings.Format(Strings.Settings7ZipVersion, check.Latest));
            }
            else
            {
                Ui.ShowWarning(this, result.Message ?? Strings.ProblemNoAnswer);
            }
        }
        finally
        {
            UseWaitCursor = false;
            if (!IsDisposed)
            {
                _sevenZipCheck.Enabled = true;
                ShowArchiver();
            }
        }
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

/// <summary>
/// Adds or changes one database server of a task (section 6.2): its kind, the folder of its programs, where it
/// listens, the account, and which databases to dump. «Find» lists the servers on this computer, «Test connection»
/// lists the databases of the server.
/// </summary>
internal sealed class DbServerDialog : DialogForm
{
    private static readonly string[] Engines = [DbEngines.MySql, DbEngines.MariaDb, DbEngines.PostgreSql];

    private readonly EditorContext _context;
    private readonly TaskDatabaseServer _original;
    private readonly Credential? _credential;
    private readonly ComboBox _engine = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly TextBox _binDir = new() { Dock = DockStyle.Fill };
    private readonly Button _browse = new() { Text = Strings.ButtonBrowse, AutoSize = true, Margin = new Padding(0) };
    private readonly Button _find = new() { Text = Strings.DbFind, AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
    private readonly TextBox _host = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Width = 90 };
    private readonly TextBox _user = new() { Dock = DockStyle.Fill };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly CheckBox _noPassword = new() { Text = Strings.DbNoPassword, AutoSize = true };
    private readonly RadioButton _all = new() { Text = Strings.DbChoiceAll, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
    private readonly RadioButton _only = new() { Text = Strings.DbChoiceOnly, AutoSize = true };
    private readonly CheckedListBox _databases = new() { Dock = DockStyle.Fill, Height = 130, CheckOnClick = true, IntegralHeight = false };
    private readonly Button _test = new() { Text = Strings.ButtonTestConnection, AutoSize = true };
    private readonly Label _testResult = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(8, 6, 0, 0) };
    private string? _dataDir;
    private string _shownEngine;

    public DbServerDialog(EditorContext context, TaskDatabaseServer server, bool isNew)
        : base(isNew ? Strings.DbServerAddTitle : Strings.DbServerEditTitle, new Size(680, 640))
    {
        _context = context;
        _original = server;
        _credential = context.FindCredential(server.CredentialId);
        foreach (string engine in Engines)
        {
            _engine.Items.Add(DbEngines.Title(engine));
        }

        var folderButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        folderButtons.Controls.AddRange([_browse, _find]);
        AddRow(Strings.DbEngine, _engine);
        AddRow(Strings.DbBinDir, _binDir, folderButtons);
        AddWide(Help(Strings.DbBinDirHelp));
        AddRow(Strings.DbHost, _host, Ui.HelpIcon(context.ToolTip, Strings.DbHostHelp));
        AddRow(Strings.DbPort, _port);
        AddRow(Strings.DbUser, _user);
        AddRow(Strings.DbPassword, _password);
        AddWide(_noPassword);
        AddNote(_credential is null ? Strings.DbAccountNote : Strings.DbAccountNote + " " + Strings.DbKeepPasswordNote);
        AddWide(_all);
        AddWide(_only);
        AddWide(_databases);
        AddNote(Strings.DbListNote);
        var testRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 6, 0, 0) };
        testRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        testRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        testRow.Controls.Add(_test, 0, 0);
        testRow.Controls.Add(_testResult, 1, 0);
        AddWide(testRow);
        EndBuild();

        int index = Array.IndexOf(Engines, server.Engine);
        _engine.SelectedIndex = index >= 0 ? index : 0;
        _shownEngine = Engine;
        _binDir.Text = server.BinDir;
        _host.Text = server.Host;
        _port.Value = server.Port ?? DbEngines.DefaultPort(Engine);
        _user.Text = _credential?.User ?? DefaultUser(Engine);
        _noPassword.Checked = _credential is { HasSecret: false };
        _dataDir = server.DataDir;
        _all.Checked = server.AllDatabases;
        _only.Checked = !server.AllDatabases;
        foreach (string name in server.AllDatabases ? server.Exclude : server.Include)
        {
            _databases.Items.Add(name, isChecked: true);
        }

        UpdatePassword();
        _engine.SelectedIndexChanged += (_, _) => EngineChanged();
        _noPassword.CheckedChanged += (_, _) => UpdatePassword();
        _browse.Click += (_, _) => Browse();
        _find.Click += (_, _) => Find();
        _test.Click += (_, _) => TestConnection();
        foreach (var box in new Control[] { _binDir, _host, _user, _password })
        {
            box.TextChanged += (_, _) => _testResult.Text = string.Empty;
        }
    }

    /// <summary>The server as entered, after "OK".</summary>
    public TaskDatabaseServer Server { get; private set; } = new();

    private string Engine => Engines[Math.Max(0, _engine.SelectedIndex)];

    private static string DefaultUser(string engine) => engine == DbEngines.PostgreSql ? "postgres" : "root";

    /// <summary>What is wrong with the fields, in words, or null.</summary>
    private string? Check()
    {
        return string.IsNullOrWhiteSpace(_binDir.Text) ? Strings.ErrorDbBinDirRequired
            : !PathVariables.LooksAbsolute(_binDir.Text.Trim().Trim('"')) ? Strings.Format(Strings.ErrorPathNotAbsolute, _binDir.Text.Trim())
            : string.IsNullOrWhiteSpace(_host.Text) ? Strings.ErrorDbHostRequired
            : string.IsNullOrWhiteSpace(_user.Text) ? Strings.ErrorUserRequired
            : _only.Checked && _databases.CheckedItems.Count == 0 ? Strings.ErrorDbNothingChosen
            : null;
    }

    protected override void OnOk()
    {
        if (Check() is { } problem)
        {
            Ui.ShowWarning(this, problem);
            return;
        }

        string? password = Password;
        if (_credential is null && password is null)
        {
            Ui.ShowWarning(this, Strings.ErrorPasswordRequired);
            return;
        }

        var credential = new Credential
        {
            Id = _credential?.Id ?? TaskDefinition.NewId(),
            Kind = CredentialKinds.Db,
            User = _user.Text.Trim(),
            ExtensionData = _credential?.ExtensionData,
        };
        var server = Build();
        SetBusy(true);
        Ui.Run(this, async () =>
        {
            try
            {
                var result = await _context.Monitor.RequestAsync(PipeProtocol.Methods.CredentialSave, new CredentialInput { Credential = credential, Password = password },
                    IpcJsonContext.Default.CredentialInput, IpcJsonContext.Default.CredentialSaveResult);
                _context.RememberCredential(result.Credential);
                server.CredentialId = result.Credential.Id;
                Server = server;
                _password.Clear();
                DialogResult = DialogResult.OK;
            }
            finally
            {
                SetBusy(false);
            }
        });
    }

    /// <summary>The password to store: "" for none, null to keep the stored one.</summary>
    private string? Password => _noPassword.Checked ? string.Empty : _password.Text.Length > 0 ? _password.Text : null;

    private static Label Help(string text) =>
        new() { Text = text, AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 6) };

    /// <summary>The server from the fields; the account is set on "OK".</summary>
    private TaskDatabaseServer Build()
    {
        var chosen = _databases.CheckedItems.Cast<string>().ToList();
        return new TaskDatabaseServer
        {
            Id = _original.Id,
            Engine = Engine,
            BinDir = _binDir.Text.Trim().Trim('"'),
            Host = _host.Text.Trim(),
            Port = (int)_port.Value,
            CredentialId = _original.CredentialId,
            AllDatabases = _all.Checked,
            Exclude = _all.Checked ? chosen : [],
            Include = _all.Checked ? [] : chosen,
            DataDir = _dataDir,
            ExtensionData = _original.ExtensionData,
        };
    }

    private void UpdatePassword()
    {
        _password.Enabled = !_noPassword.Checked;
        if (_noPassword.Checked)
        {
            _password.Clear();
        }
    }

    /// <summary>The port and user follow the kind of server while they still have the usual values of the old one.</summary>
    private void EngineChanged()
    {
        string old = _shownEngine;
        _shownEngine = Engine;
        if ((int)_port.Value == DbEngines.DefaultPort(old))
        {
            _port.Value = DbEngines.DefaultPort(Engine);
        }

        if (_user.Text.Trim() == DefaultUser(old))
        {
            _user.Text = DefaultUser(Engine);
        }

        _testResult.Text = string.Empty;
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog { ShowNewFolderButton = false, UseDescriptionForTitle = true, Description = Strings.DbBinDir.TrimEnd(':') };
        if (Directory.Exists(_binDir.Text))
        {
            dialog.InitialDirectory = _binDir.Text;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _binDir.Text = dialog.SelectedPath;
        }
    }

    private void Find()
    {
        using var dialog = new DbFindDialog(_context.Monitor);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Selected is not { } found)
        {
            return;
        }

        int index = Array.IndexOf(Engines, found.Engine);
        if (index >= 0)
        {
            _engine.SelectedIndex = index;
        }

        _binDir.Text = found.BinDir;
        _host.Text = found.Host;
        _port.Value = Math.Clamp(found.Port ?? DbEngines.DefaultPort(found.Engine), 1, 65535);
    }

    private void TestConnection()
    {
        if (Check() is { } problem)
        {
            _testResult.ForeColor = Glyphs.ErrorText;
            _testResult.Text = problem;
            return;
        }

        var server = Build();
        var account = new CredentialInput
        {
            Credential = new Credential { Id = _credential?.Id ?? TaskDefinition.NewId(), Kind = CredentialKinds.Db, User = _user.Text.Trim() },
            Password = Password,
        };
        _test.Enabled = false;
        _testResult.ForeColor = SystemColors.GrayText;
        _testResult.Text = Strings.TestRunning;
        Ui.Run(this, async () =>
        {
            try
            {
                var result = await _context.Monitor.RequestAsync(PipeProtocol.Methods.DatabasesTest, new DbTestRequest
                {
                    Server = server,
                    Credential = account,
                    TaskName = _context.TaskName,
                    ProfileSid = _context.ProfileSid,
                },
                    IpcJsonContext.Default.DbTestRequest, IpcJsonContext.Default.DbTestResult, TimeSpan.FromMinutes(2));
                ShowTest(result);
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

    /// <summary>Shows what the test found; the databases of the server join the list (also used by the window tests).</summary>
    internal void ShowTest(DbTestResult result)
    {
        if (!result.Ok)
        {
            _testResult.ForeColor = Glyphs.ErrorText;
            _testResult.Text = Strings.Format(Strings.DbTestFailed, result.Error);
            return;
        }

        _dataDir = result.DataDir;
        var present = _databases.Items.Cast<string>().ToHashSet(StringComparer.Ordinal);
        foreach (string name in result.Databases.Where(n => !present.Contains(n)))
        {
            _databases.Items.Add(name, isChecked: false);
        }

        _testResult.ForeColor = Glyphs.Green;
        _testResult.Text = Strings.Format(Strings.DbTestOk, result.Version, result.Databases.Count)
            + (result.DataDir is { } dataDir ? Environment.NewLine + Strings.Format(Strings.DbTestDataDir, dataDir) : string.Empty);
    }
}

/// <summary>«Find» of <see cref="DbServerDialog"/>: the database servers and folders of their programs on this computer.</summary>
internal sealed class DbFindDialog : DialogForm
{
    private readonly ServiceMonitor _monitor;
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        Height = 220,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
    };

    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 6, 0, 6) };

    public DbFindDialog(ServiceMonitor monitor)
        : base(Strings.DbFindTitle, new Size(720, 380))
    {
        _monitor = monitor;
        _list.Columns.Add(Strings.ColumnServer, 220);
        _list.Columns.Add(Strings.ColumnState, 100);
        _list.Columns.Add(Strings.ColumnPrograms, 360);
        AddNote(Strings.DbFindNote);
        AddWide(_list);
        AddWide(_status);
        EndBuild();
        OkButton.Enabled = false;
        _list.SelectedIndexChanged += (_, _) => OkButton.Enabled = _list.SelectedItems.Count == 1;
        _list.ItemActivate += (_, _) => OnOk();
        Shown += (_, _) => Search();
    }

    /// <summary>The server picked, after "OK".</summary>
    public DbCandidate? Selected { get; private set; }

    protected override void OnOk()
    {
        if (_list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is DbCandidate candidate)
        {
            Selected = candidate;
            DialogResult = DialogResult.OK;
        }
    }

    /// <summary>Shows what the service found (also used by the window tests).</summary>
    internal void ShowFound(DbFindResult result)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var server in result.Servers)
        {
            string title = DbEngines.Title(server.Engine) + (server.Version is { Length: > 0 } version ? " " + version : string.Empty);
            string where = server.Running ? $"{title} — {server.Host}:{server.Port}" : title;
            _list.Items.Add(new ListViewItem([where, server.Running ? Strings.DbRunning : Strings.DbNotRunning, server.BinDir]) { Tag = server });
        }

        _list.EndUpdate();
        _status.Text = result.Servers.Count == 0 ? Strings.DbFindNothing : string.Empty;
        if (_list.Items.Count > 0)
        {
            _list.Items[0].Selected = true;
        }
    }

    private void Search()
    {
        _status.Text = Strings.DbFindRunning;
        UseWaitCursor = true;
        Ui.Run(this, async () =>
        {
            try
            {
                var result = await _monitor.RequestAsync(PipeProtocol.Methods.DatabasesFind, EmptyRequest.Instance,
                    IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.DbFindResult, TimeSpan.FromMinutes(2));
                ShowFound(result);
            }
            catch (ServiceUnavailableException)
            {
                _status.Text = string.Empty;
                throw;
            }
            finally
            {
                UseWaitCursor = false;
            }
        });
    }
}
