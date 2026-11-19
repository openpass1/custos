using System.ComponentModel;
using System.Globalization;
using Custos.Core;
using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Monitor;
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

    /// <summary>
    /// The hints of the «?» icons of this dialog. A tooltip of the editor would not do: it belongs to the editor
    /// window, which is inactive while the dialog is open, and Windows shows no hints of an inactive window.
    /// </summary>
    protected ToolTip Tips { get; } = new() { AutoPopDelay = 20000 };

    protected Button CancelButtonControl { get; } = new() { Text = Strings.ButtonCancel, AutoSize = true, MinimumSize = new Size(88, 0), DialogResult = DialogResult.Cancel };

    /// <summary>The caption of a row, for dialogs that show and hide their rows.</summary>
    protected static Label Caption(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) };

    /// <summary>Called by "OK": check, save, and set <see cref="Form.DialogResult"/> when done.</summary>
    protected abstract void OnOk();

    /// <summary>The last call of the constructor of a dialog, after all its controls are added.</summary>
    protected void EndBuild() => ResumeLayout(performLayout: true);

    /// <summary>
    /// Lets the dialog be resized and maximized. The rows then sit in a panel that scrolls, at their own height: a
    /// table that fills the window itself gives the height it does not need to its last row, so a maximized window
    /// showed a long empty space under the last line.
    /// </summary>
    protected void MakeSizable(Size minimum)
    {
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimumSize = minimum;
        int index = Controls.GetChildIndex(Body);
        Controls.Remove(Body);
        var host = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        Body.AutoScroll = false;
        Body.AutoSize = true;
        Body.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Body.Dock = DockStyle.Top;
        host.Controls.Add(Body);
        Controls.Add(host);
        Controls.SetChildIndex(host, index);
    }

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Tips.Dispose();
        }

        base.Dispose(disposing);
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
        _accountHelp = Ui.HelpIcon(Tips, Strings.SourceAccountHelp);
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
/// Adds or changes one destination of a task (section 6.3): a local folder, a network folder with its account, a USB
/// disk found by its label, or a folder on an SFTP, FTP or WebDAV server or in an S3 bucket, with "Check access".
/// A destination of a kind of a later version is shown, not changed.
/// </summary>
internal sealed class DestinationDialog : DialogForm
{
    // The legacy edition has no cloud drives: they are not offered (CustosEdition).
    private static readonly string[] Kinds =
    [
        .. new[]
        {
            DestinationKinds.Local, DestinationKinds.Unc, DestinationKinds.Usb,
            DestinationKinds.Sftp, DestinationKinds.Ftp, DestinationKinds.Ftps, DestinationKinds.WebDav, DestinationKinds.S3,
            DestinationKinds.GDrive, DestinationKinds.YaDisk, DestinationKinds.Dropbox,
        }.Where(DestinationKinds.IsSupported),
    ];

    /// <summary>The part size a new Yandex Disk destination starts with: the Disk without a subscription takes files
    /// of up to 1 GB.</summary>
    internal const int YandexPartMb = 900;

    /// <summary>The delay before the lock a new WORM destination starts with, in hours.</summary>
    internal const int DefaultWormLockHours = 3;

    private static readonly string[] LockModes = [ObjectLockModes.Governance, ObjectLockModes.Compliance];

    private readonly EditorContext _context;
    private readonly TaskDestination _original;
    private readonly bool _supported;
    private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly Label _hostLabel = Caption(Strings.DestinationHost);
    private readonly TextBox _host = new() { Dock = DockStyle.Fill };
    private readonly Label _portLabel = new() { Text = Strings.DestinationPort, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 0, 4, 0) };
    private readonly TextBox _port = new() { Width = 64, MaxLength = 5, Margin = new Padding(0) };
    private readonly TableLayoutPanel _hostRow = new() { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
    private readonly PictureBox _hostHelp;
    private readonly Label _endpointLabel = Caption(Strings.DestinationEndpoint);
    private readonly TextBox _endpoint = new() { Dock = DockStyle.Fill };
    private readonly PictureBox _endpointHelp;
    private readonly Label _bucketLabel = Caption(Strings.DestinationBucket);
    private readonly TextBox _bucket = new() { Dock = DockStyle.Fill };
    private readonly PictureBox _bucketHelp;
    private readonly Label _regionLabel = Caption(Strings.DestinationRegion);
    private readonly TextBox _region = new() { Width = 260 };
    private readonly PictureBox _regionHelp;
    private readonly Label _pathLabel = Caption(Strings.DestinationFolder);
    private readonly TextBox _path = new() { Dock = DockStyle.Fill };
    private readonly Button _browse = new() { Text = Strings.ButtonBrowse, AutoSize = true, Margin = new Padding(0) };
    private readonly PictureBox _pathHelp;
    private readonly Label _labelLabel = Caption(Strings.DestinationUsbLabel);
    private readonly ComboBox _usbLabel = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 260 };
    private readonly PictureBox _usbHelp;
    private readonly Label _accountLabel = Caption(Strings.DestinationAccount);
    private readonly CredentialPicker _account;
    private readonly PictureBox _accountHelp;
    private readonly Label _speedLabel = Caption(Strings.DestinationSpeed);
    private readonly SpeedBox _speed = new() { Width = 90, Margin = new Padding(0) };
    private readonly FlowLayoutPanel _speedRow = new() { AutoSize = true, WrapContents = false };
    private readonly PictureBox _speedHelp;
    private readonly Label _partLabel = Caption(Strings.DestinationPartSize);
    private readonly NumericUpDown _part = new() { Minimum = 0, Maximum = 1_000_000, Increment = 100, Width = 90, Margin = new Padding(0) };
    private readonly FlowLayoutPanel _partRow = new() { AutoSize = true, WrapContents = false };
    private readonly PictureBox _partHelp;
    private readonly Label _cloudWarning = new() { Text = Strings.DestinationCloudWarning, AutoSize = true, Dock = DockStyle.Fill, ForeColor = Glyphs.WarningText, Margin = new Padding(0, 4, 0, 6) };
    private readonly Label _lockLabel = Caption(Strings.DestinationLock);
    private readonly NumericUpDown _lockDays = new() { Minimum = 0, Maximum = 36500, Width = 90, Margin = new Padding(0) };
    private readonly FlowLayoutPanel _lockRow = new() { AutoSize = true, WrapContents = false };
    private readonly PictureBox _lockHelp;
    private readonly Label _lockModeLabel = Caption(Strings.DestinationLockMode);
    private readonly ComboBox _lockMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
    private readonly Label _lockModeGap = new() { AutoSize = true };
    private readonly Label _wormLabel = Caption(Strings.DestinationWorm);
    private readonly NumericUpDown _wormDays = new() { Minimum = 0, Maximum = 36500, Width = 90, Margin = new Padding(0) };
    private readonly FlowLayoutPanel _wormRow = new() { AutoSize = true, WrapContents = false };
    private readonly PictureBox _wormHelp;
    private readonly Label _wormLockLabel = Caption(Strings.DestinationWormLock);
    private readonly NumericUpDown _wormHours = new() { Minimum = 0, Maximum = WormSettings.MaxLockHours, Width = 90, Margin = new Padding(0) };
    private readonly FlowLayoutPanel _wormLockRow = new() { AutoSize = true, WrapContents = false };
    private readonly Label _wormLockGap = new() { AutoSize = true };
    private readonly Label _keyLabel = Caption(Strings.DestinationServerKey);
    private readonly Label _key = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 6) };
    private readonly Button _forgetKey = new() { Text = Strings.ButtonForgetKey, AutoSize = true };
    private readonly Label _unsupported = new() { Text = Strings.DestinationLater, AutoSize = true, Dock = DockStyle.Fill, ForeColor = Glyphs.WarningText, Margin = new Padding(0, 4, 0, 6) };
    private readonly Button _test = new() { Text = Strings.ButtonTestAccess, AutoSize = true };
    private readonly Label _testResult = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(8, 6, 0, 0) };
    private string? _fingerprint;
    private string _fingerprintOf = string.Empty;
    private bool _filling = true;

    /// <param name="title">The title when the dialog is used elsewhere than in the task editor (restore).</param>
    public DestinationDialog(EditorContext context, TaskDestination destination, bool isNew, string? title = null)
        : base(title ?? (isNew ? Strings.DestinationAddTitle : Strings.DestinationEditTitle), new Size(700, 500))
    {
        _context = context;
        _original = destination;
        _supported = DestinationKinds.IsSupported(destination.Kind);
        if (CustosEdition.IsLegacy && DestinationKinds.IsCloudDrive(destination.Kind))
        {
            _unsupported.Text = Strings.DestinationNotInLegacy;
        }
        _account = new CredentialPicker(context) { Dock = DockStyle.Fill };
        _hostHelp = Ui.HelpIcon(Tips, Strings.DestinationHostHelp);
        _endpointHelp = Ui.HelpIcon(Tips, Strings.DestinationEndpointHelp);
        _bucketHelp = Ui.HelpIcon(Tips, Strings.DestinationBucketHelp);
        _regionHelp = Ui.HelpIcon(Tips, Strings.DestinationRegionHelp);
        _pathHelp = Ui.HelpIcon(Tips, Strings.DestinationFolderServerHelp);
        _usbHelp = Ui.HelpIcon(Tips, Strings.DestinationUsbHelp);
        _accountHelp = Ui.HelpIcon(Tips, Strings.DestinationAccountHelp);
        _speedHelp = Ui.HelpIcon(Tips, Strings.DestinationSpeedHelp);
        _partHelp = Ui.HelpIcon(Tips, Strings.DestinationPartHelp);
        _lockHelp = Ui.HelpIcon(Tips, Strings.DestinationLockHelp);
        _wormHelp = Ui.HelpIcon(Tips, Strings.DestinationWormHelp);
        foreach (string kind in Kinds)
        {
            _kind.Items.Add(DestinationsPage.KindText(kind));
        }

        if (!_supported)
        {
            // A kind of a later version, set from the server: shown, not editable here.
            _kind.Items.Add(DestinationsPage.KindText(destination.Kind));
        }

        _lockMode.Items.AddRange([Strings.LockModeGovernance, Strings.LockModeCompliance]);
        _hostRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _hostRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _hostRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _host.Margin = new Padding(0);
        _hostRow.Controls.Add(_host, 0, 0);
        _hostRow.Controls.Add(_portLabel, 1, 0);
        _hostRow.Controls.Add(_port, 2, 0);
        _speedRow.Controls.Add(_speed);
        _speedRow.Controls.Add(new Label { Text = Strings.RemoteSpeedUnit, AutoSize = true, Margin = new Padding(6, 3, 0, 0) });
        _partRow.Controls.Add(_part);
        _partRow.Controls.Add(new Label { Text = Strings.DestinationPartUnit, AutoSize = true, Margin = new Padding(6, 3, 0, 0) });
        _lockRow.Controls.Add(_lockDays);
        _lockRow.Controls.Add(new Label { Text = Strings.DestinationLockUnit, AutoSize = true, Margin = new Padding(6, 3, 0, 0) });
        _wormRow.Controls.Add(_wormDays);
        _wormRow.Controls.Add(new Label { Text = Strings.DestinationWormUnit, AutoSize = true, Margin = new Padding(6, 3, 0, 0) });
        _wormLockRow.Controls.Add(_wormHours);
        _wormLockRow.Controls.Add(new Label { Text = Strings.DestinationWormLockUnit, AutoSize = true, Margin = new Padding(6, 3, 0, 0) });
        var pathExtra = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        pathExtra.Controls.Add(_browse);
        pathExtra.Controls.Add(_pathHelp);

        AddGridRow(Caption(Strings.DestinationKind), _kind, Ui.HelpIcon(Tips, Strings.DestinationKindHelp));
        AddGridRow(_hostLabel, _hostRow, _hostHelp);
        AddGridRow(_endpointLabel, _endpoint, _endpointHelp);
        AddGridRow(_bucketLabel, _bucket, _bucketHelp);
        AddGridRow(_regionLabel, _region, _regionHelp);
        AddGridRow(_pathLabel, _path, pathExtra);
        AddGridRow(_labelLabel, _usbLabel, _usbHelp);
        AddGridRow(_accountLabel, _account, _accountHelp);
        AddGridRow(_speedLabel, _speedRow, _speedHelp);
        AddGridRow(_partLabel, _partRow, _partHelp);
        AddGridRow(_lockLabel, _lockRow, _lockHelp);
        AddGridRow(_lockModeLabel, _lockMode, _lockModeGap);
        AddGridRow(_wormLabel, _wormRow, _wormHelp);
        AddGridRow(_wormLockLabel, _wormLockRow, _wormLockGap);
        AddGridRow(_keyLabel, _key, _forgetKey);
        AddWide(_unsupported);
        AddWide(_cloudWarning);
        AddNote(Strings.PathVariablesNote);
        var testRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 6, 0, 0) };
        testRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        testRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        testRow.Controls.Add(_test, 0, 0);
        testRow.Controls.Add(_testResult, 1, 0);
        AddWide(testRow);
        MakeSizable(new Size(620, 420));
        EndBuild();

        FillUsbLabels();
        int index = Array.IndexOf(Kinds, destination.Kind);
        _kind.SelectedIndex = index >= 0 ? index : Kinds.Length;
        _path.Text = destination.Path;
        _usbLabel.Text = destination.UsbLabel ?? string.Empty;
        _host.Text = destination.Host ?? string.Empty;
        _port.Text = destination.Port is { } port ? port.ToString(CultureInfo.InvariantCulture) : string.Empty;
        _endpoint.Text = destination.Endpoint ?? string.Empty;
        _bucket.Text = destination.Bucket ?? string.Empty;
        _region.Text = destination.Region ?? string.Empty;
        _speed.Kbps = destination.SpeedLimitKbps;
        _part.Value = Math.Clamp(destination.PartMb, 0, (int)_part.Maximum);
        _lockDays.Value = Math.Clamp(destination.ObjectLockDays, 0, (int)_lockDays.Maximum);
        _lockMode.SelectedIndex = destination.ObjectLockMode == ObjectLockModes.Compliance ? 1 : 0;
        _wormDays.Value = Math.Clamp(destination.WormDays, 0, (int)_wormDays.Maximum);

        // A new WORM starts with the delay many NAS have; a lower one than the storage has would make the check fail.
        _wormHours.Value = destination.WormDays > 0 ? Math.Clamp(destination.WormLockHours, 0, WormSettings.MaxLockHours) : DefaultWormLockHours;
        _account.SelectedId = destination.CredentialId;
        _fingerprint = string.IsNullOrWhiteSpace(destination.ServerFingerprint) ? null : destination.ServerFingerprint;

        // Before the fields are shown: showing them looks whether the server is still the one the key belongs to.
        _fingerprintOf = ServerOf();
        _filling = false;
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
        _host.TextChanged += (_, _) => ServerChanged();
        _port.TextChanged += (_, _) => ServerChanged();
        _endpoint.TextChanged += (_, _) => ServerChanged();
        _lockDays.ValueChanged += (_, _) => UpdateLock();
        _wormDays.ValueChanged += (_, _) => UpdateLock();
        _forgetKey.Click += (_, _) =>
        {
            _fingerprint = null;
            ShowKey();
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
        switch (destination.Kind)
        {
            case DestinationKinds.Usb:
                return string.IsNullOrWhiteSpace(destination.UsbLabel) ? Strings.ErrorUsbLabelRequired : null;
            case DestinationKinds.Sftp or DestinationKinds.Ftp or DestinationKinds.Ftps:
                return string.IsNullOrWhiteSpace(destination.Host) ? Strings.ErrorHostRequired
                    : destination.Port is < 1 or > 65535 ? Strings.ErrorPortBad
                    : destination.Kind == DestinationKinds.Sftp && string.IsNullOrEmpty(destination.CredentialId) ? Strings.ErrorAccountRequired
                    : null;
            case DestinationKinds.WebDav:
                return Custos.Core.Destinations.DestinationStores.Problem(destination) is null ? null : Strings.ErrorAddressRequired;
            case DestinationKinds.S3:
                return string.IsNullOrWhiteSpace(destination.Bucket) ? Strings.ErrorBucketRequired
                    : string.IsNullOrEmpty(destination.CredentialId) ? Strings.ErrorAccountRequired
                    : null;
            case DestinationKinds.GDrive or DestinationKinds.YaDisk or DestinationKinds.Dropbox:
                // The folder may be empty: the folder of CUSTOS in the account itself.
                return string.IsNullOrEmpty(destination.CredentialId) ? Strings.ErrorCloudAccountRequired : null;
            default:
                return string.IsNullOrWhiteSpace(path) ? Strings.ErrorDestinationFolderRequired
                    : !PathVariables.LooksAbsolute(path) ? Strings.Format(Strings.ErrorPathNotAbsolute, path)
                    : destination.Kind == DestinationKinds.Unc && !path.StartsWith(@"\\", StringComparison.Ordinal) ? Strings.Format(Strings.ErrorNotUnc, path)
                    : destination.Kind == DestinationKinds.Local && path.StartsWith(@"\\", StringComparison.Ordinal) ? Strings.Format(Strings.ErrorUncNeedsNetwork, path)
                    : null;
        }
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

        if (destination.Kind == DestinationKinds.Sftp && destination.ServerFingerprint is null && !Ui.Confirm(this, Strings.DestinationKeyAsk))
        {
            return;
        }

        Destination = destination;
        DialogResult = DialogResult.OK;
    }

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
    /// <summary>The destination as the fields now say.</summary>
    internal TaskDestination Build()
    {
        string kind = Kind;
        bool server = kind is DestinationKinds.Sftp or DestinationKinds.Ftp or DestinationKinds.Ftps;
        bool s3 = kind == DestinationKinds.S3;
        bool cloud = DestinationKinds.IsCloudDrive(kind);
        string path = _path.Text.Trim().Trim('"');
        int lockDays = s3 ? (int)_lockDays.Value : 0;
        int wormDays = DestinationKinds.IsFileSystem(kind) ? (int)_wormDays.Value : 0;
        return new TaskDestination
        {
            Id = _original.Id,
            Kind = kind,
            Path = kind == DestinationKinds.Usb ? DestinationKinds.UsbFolder(path) : path,
            UsbLabel = kind == DestinationKinds.Usb ? _usbLabel.Text.Trim() : null,
            CredentialId = DestinationKinds.CredentialKind(kind) is null ? null : _account.SelectedId,
            Host = server || kind == DestinationKinds.WebDav ? _host.Text.Trim() : null,
            // The address of WebDAV holds its port; one that came with the task from the server is kept.
            Port = !server ? (kind == DestinationKinds.WebDav && _original.Kind == DestinationKinds.WebDav ? _original.Port : null)
                : _port.Text.Trim().Length == 0 ? null
                : int.TryParse(_port.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) ? port : 0,
            Endpoint = s3 && _endpoint.Text.Trim().Length > 0 ? _endpoint.Text.Trim() : null,
            Bucket = s3 ? _bucket.Text.Trim() : null,
            Region = s3 && _region.Text.Trim().Length > 0 ? _region.Text.Trim() : null,
            DisconnectAfter = _original.DisconnectAfter,
            SpeedLimitKbps = DestinationKinds.CredentialKind(kind) is null ? 0 : _speed.Kbps,
            ObjectLockDays = lockDays,
            ObjectLockMode = lockDays > 0 ? LockModes[Math.Max(0, _lockMode.SelectedIndex)] : null,
            WormDays = wormDays,
            WormLockHours = wormDays > 0 ? (int)_wormHours.Value : 0,
            ServerFingerprint = DestinationKinds.IsFileSystem(kind) || cloud ? null : _fingerprint,
            PartMb = cloud ? (int)_part.Value : 0,
            ExtensionData = _original.ExtensionData,
        };
    }

    private void UpdateFields()
    {
        if (_filling)
        {
            return;
        }

        string kind = Kind;
        bool usb = kind == DestinationKinds.Usb;
        bool server = kind is DestinationKinds.Sftp or DestinationKinds.Ftp or DestinationKinds.Ftps;
        bool dav = kind == DestinationKinds.WebDav;
        bool s3 = kind == DestinationKinds.S3;
        bool cloud = DestinationKinds.IsCloudDrive(kind);
        bool files = DestinationKinds.IsFileSystem(kind) || !_supported;
        string? accountKind = _supported ? DestinationKinds.CredentialKind(kind) : null;

        _hostLabel.Text = dav ? Strings.DestinationAddress : Strings.DestinationHost;
        _hostLabel.Visible = _hostRow.Visible = _hostHelp.Visible = server || dav;
        _portLabel.Visible = _port.Visible = server;
        _port.PlaceholderText = kind == DestinationKinds.Sftp ? "22" : "21";
        Tips.SetToolTip(_hostHelp, Custos.Core.Text.LineWrap.Wrap(dav ? Strings.DestinationAddressHelp : Strings.DestinationHostHelp));
        _endpointLabel.Visible = _endpoint.Visible = _endpointHelp.Visible = s3;
        _bucketLabel.Visible = _bucket.Visible = _bucketHelp.Visible = s3;
        _regionLabel.Visible = _region.Visible = _regionHelp.Visible = s3;

        _pathLabel.Text = usb ? Strings.DestinationFolderOnDisk
            : server ? Strings.DestinationFolderOnServer
            : s3 ? Strings.DestinationFolderInBucket
            : cloud ? Strings.Format(Strings.DestinationFolderInCloud, CloudNames.AppFolder(kind))
            : Strings.DestinationFolder;
        _browse.Visible = kind == DestinationKinds.Local;
        _pathHelp.Visible = !files;
        Tips.SetToolTip(_pathHelp, Custos.Core.Text.LineWrap.Wrap(
            s3 ? Strings.DestinationFolderBucketHelp : dav ? Strings.DestinationFolderWebDavHelp
            : cloud ? Strings.DestinationFolderCloudHelp : Strings.DestinationFolderServerHelp));
        _labelLabel.Visible = _usbLabel.Visible = _usbHelp.Visible = usb;

        _accountLabel.Visible = _account.Visible = _accountHelp.Visible = accountKind is not null;
        if (accountKind is not null)
        {
            _account.SetKind(accountKind, accountKind switch
            {
                CredentialKinds.Windows => Strings.AccountService,
                CredentialKinds.Ftp or CredentialKinds.WebDav => Strings.AccountNone,
                _ => null,
            });
            Tips.SetToolTip(_accountHelp, Custos.Core.Text.LineWrap.Wrap(accountKind switch
            {
                CredentialKinds.Sftp => Strings.DestinationAccountHelpSftp,
                CredentialKinds.Ftp => Strings.DestinationAccountHelpFtp,
                CredentialKinds.WebDav => Strings.DestinationAccountHelpWebDav,
                CredentialKinds.S3 => Strings.DestinationAccountHelpS3,
                _ when cloud => Strings.DestinationAccountHelpCloud,
                _ => Strings.DestinationAccountHelp,
            }));
        }

        _speedLabel.Visible = _speedRow.Visible = _speedHelp.Visible = accountKind is not null;
        _lockLabel.Visible = _lockRow.Visible = _lockHelp.Visible = s3;
        _wormLabel.Visible = _wormRow.Visible = _wormHelp.Visible = DestinationKinds.IsFileSystem(kind) && _supported;
        _partLabel.Visible = _partRow.Visible = _partHelp.Visible = cloud && _supported;
        _cloudWarning.Visible = cloud && _supported;
        if (kind == DestinationKinds.YaDisk && _original.Kind != DestinationKinds.YaDisk && _part.Value == 0)
        {
            // Yandex Disk without a subscription takes files of up to 1 GB (section 6.3).
            _part.Value = YandexPartMb;
        }

        _unsupported.Visible = !_supported;
        _kind.Enabled = _path.Enabled = _test.Enabled = _supported;
        _testResult.Text = string.Empty;
        UpdateLock();
        ServerChanged();
    }

    private void UpdateLock()
    {
        bool shown = Kind == DestinationKinds.S3 && _lockDays.Value > 0;
        _lockModeLabel.Visible = _lockMode.Visible = _lockModeGap.Visible = shown;
        bool worm = DestinationKinds.IsFileSystem(Kind) && _supported && _wormDays.Value > 0;
        _wormLockLabel.Visible = _wormLockRow.Visible = _wormLockGap.Visible = worm;
        _testResult.Text = string.Empty;
    }

    /// <summary>What the trusted key belongs to: the kind of the destination and its server.</summary>
    private string ServerOf() => Kind == DestinationKinds.S3
        ? Kind + "|" + _endpoint.Text.Trim()
        : Kind + "|" + _host.Text.Trim() + "|" + _port.Text.Trim();

    /// <summary>A key or a certificate is trusted for one server: another address has to be confirmed anew.</summary>
    private void ServerChanged()
    {
        if (_filling)
        {
            return;
        }

        string now = ServerOf();
        if (!string.Equals(now, _fingerprintOf, StringComparison.OrdinalIgnoreCase))
        {
            _fingerprintOf = now;
            _fingerprint = null;
        }

        _testResult.Text = string.Empty;
        ShowKey();
    }

    /// <summary>The key of an SFTP server is always shown; a certificate only once one was confirmed here (the
    /// certificates Windows trusts need no confirmation).</summary>
    private void ShowKey()
    {
        string kind = Kind;
        bool shown = _supported && (kind == DestinationKinds.Sftp || (_fingerprint is not null && !DestinationKinds.IsFileSystem(kind)));
        _keyLabel.Visible = _key.Visible = shown;
        _forgetKey.Visible = shown && _fingerprint is not null;
        _key.Text = _fingerprint ?? Strings.DestinationServerKeyNone;
        _key.ForeColor = _fingerprint is null ? Glyphs.WarningText : SystemColors.ControlText;
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
        if (Check(Build()) is { } problem)
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
                var result = await Ask();
                if (result.TrustNeeded && result.Fingerprint is { } seen && ConfirmKey(result, seen))
                {
                    // The administrator trusts what the server showed: once more, now with it.
                    _fingerprint = seen;
                    _fingerprintOf = ServerOf();
                    ShowKey();
                    _testResult.ForeColor = SystemColors.GrayText;
                    _testResult.Text = Strings.TestRunning;
                    result = await Ask();
                }

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

        Task<DestinationTestResult> Ask() => _context.Monitor.RequestAsync(PipeProtocol.Methods.DestinationTest, new DestinationTestRequest
        {
            Destination = Build(),
            TaskName = _context.TaskName,
            BackupType = _context.BackupType,
            ProfileSid = _context.ProfileSid,
        },
            IpcJsonContext.Default.DestinationTestRequest, IpcJsonContext.Default.DestinationTestResult, TimeSpan.FromMinutes(3));
    }

    private void ShowTest(DestinationTestResult result)
    {
        _testResult.ForeColor = result.Ok ? Glyphs.Green : Glyphs.ErrorText;
        string text = !result.Ok ? Strings.Format(Strings.TestFailed, result.Error)
            : result.FreeBytes is { } free ? Strings.Format(Strings.TestOk, result.Folder, Custos.Core.Text.Sizes.Format(free))
            : DestinationKinds.IsFileSystem(Kind) ? Strings.Format(Strings.TestOk, result.Folder, "?")
            : Strings.Format(Strings.TestOkRemote, result.Folder);
        _testResult.Text = result.Details.Count == 0 ? text : text + Environment.NewLine + string.Join(Environment.NewLine, result.Details);
    }

    /// <summary>Shows what the server showed and asks whether to trust it; a changed key is a warning.</summary>
    private bool ConfirmKey(DestinationTestResult result, string seen)
    {
        string server = Kind == DestinationKinds.S3 ? _endpoint.Text.Trim() : _host.Text.Trim();
        string text = result.FingerprintChanged
            ? Strings.Format(Strings.DestinationKeyChanged, result.Error)
            : Strings.Format(Strings.DestinationKeyConfirm, server, seen);
        return MessageBox.Show(this, text, Strings.AppTitle, MessageBoxButtons.YesNo,
            result.FingerprintChanged ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
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
        AddGridRow(Caption(Strings.EventTimeout), _timeout, Ui.HelpIcon(Tips, Strings.EventTimeoutHelp));
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
/// Adds or changes an account: a Windows account for network folders, or the account of a server (SFTP, FTP and
/// FTPS, WebDAV, S3). The secret (a password, the secret key of S3, or the private key of SFTP) goes straight to the
/// service, which keeps it encrypted with DPAPI; it is never shown or sent back.
/// </summary>
internal sealed class CredentialDialog : DialogForm
{
    /// <summary>The kinds of accounts that are added by hand, in the order of the list.</summary>
    internal static readonly string[] Kinds =
    [
        CredentialKinds.Windows, CredentialKinds.Sftp, CredentialKinds.Ftp, CredentialKinds.WebDav, CredentialKinds.S3,
    ];

    /// <summary>A private key file is a few kilobytes; anything much larger is some other file.</summary>
    private const int LargestKeyFile = 64 * 1024;

    private readonly ServiceMonitor _monitor;
    private readonly Credential? _existing;
    private readonly string? _fixedKind;
    private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly Label _userLabel = Caption(Strings.AccountUser);
    private readonly TextBox _user = new() { Dock = DockStyle.Fill };
    private readonly TextBox _domain = new() { Dock = DockStyle.Fill };
    private readonly RadioButton _bySecret = new() { Text = Strings.AccountBySecret, AutoSize = true, Checked = true };
    private readonly RadioButton _byKey = new() { Text = Strings.AccountByKey, AutoSize = true };
    private readonly FlowLayoutPanel _way = new() { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 2) };
    private readonly Label _passwordLabel = Caption(Strings.AccountPassword);
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly CheckBox _show = new() { Text = Strings.ShowPassword, AutoSize = true };
    private readonly TextBox _key = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        AcceptsReturn = true,
        WordWrap = false,
        ScrollBars = ScrollBars.Both,
        Height = 130,
    };

    private readonly Button _keyFile = new() { Text = Strings.ButtonKeyFile, AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Left };
    private readonly Control[] _domainRow;
    private readonly Control[] _passwordRow;
    private readonly Control[] _keyRow;
    private readonly Label _domainNote;
    private readonly Label _note;

    /// <param name="monitor">The connection to the service.</param>
    /// <param name="existing">The account to change, or null to add one.</param>
    /// <param name="kind">The kind of a new account when the place it is added from needs one kind; null lets the user choose.</param>
    public CredentialDialog(ServiceMonitor monitor, Credential? existing, string? kind = null)
        : base(existing is null ? Strings.AccountAddTitle : Strings.Format(Strings.AccountEditTitle, existing.DisplayName), new Size(580, 470))
    {
        _monitor = monitor;
        _existing = existing;
        _fixedKind = existing?.Kind ?? kind;
        foreach (string item in Kinds)
        {
            _kind.Items.Add(KindText(item));
        }

        if (_fixedKind is not null && Array.IndexOf(Kinds, _fixedKind) < 0)
        {
            _kind.Items.Add(KindText(_fixedKind));
        }

        _way.Controls.Add(_bySecret);
        _way.Controls.Add(_byKey);
        AddGridRow(Caption(Strings.AccountKind), _kind);
        AddGridRow(_userLabel, _user);
        _domainRow = AddGridRow(Caption(Strings.AccountDomain), _domain);
        _domainNote = AddNote(Strings.AccountDomainNote);
        AddWide(_way);
        _passwordRow = AddGridRow(_passwordLabel, _password);
        AddWide(_show);
        var keyLabel = Caption(Strings.AccountPrivateKey);
        keyLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _keyRow = AddGridRow(keyLabel, _key, _keyFile);
        _note = AddNote(string.Empty);
        EndBuild();

        int index = Array.IndexOf(Kinds, _fixedKind ?? CredentialKinds.Windows);
        _kind.SelectedIndex = index >= 0 ? index : Kinds.Length;
        _kind.Enabled = _fixedKind is null;
        if (existing is not null)
        {
            _user.Text = existing.User;
            _domain.Text = existing.Domain ?? string.Empty;
        }

        UpdateFields();
        _kind.SelectedIndexChanged += (_, _) => UpdateFields();
        _bySecret.CheckedChanged += (_, _) => UpdateFields();
        _show.CheckedChanged += (_, _) => _password.UseSystemPasswordChar = !_show.Checked;
        _keyFile.Click += (_, _) => TakeKeyFile();
    }

    /// <summary>The account as the service stored it, after "OK".</summary>
    public Credential? Saved { get; private set; }

    /// <summary>The name of a kind of accounts, as the lists show it.</summary>
    public static string KindText(string kind) => kind switch
    {
        CredentialKinds.Windows => Strings.AccountKindWindows,
        CredentialKinds.Sftp => Strings.AccountKindSftp,
        CredentialKinds.Ftp => Strings.AccountKindFtp,
        CredentialKinds.WebDav => Strings.AccountKindWebDav,
        CredentialKinds.S3 => Strings.AccountKindS3,
        CredentialKinds.GDrive => Strings.AccountKindGDrive,
        CredentialKinds.YaDisk => Strings.AccountKindYaDisk,
        CredentialKinds.Dropbox => Strings.AccountKindDropbox,
        _ => kind,
    };

    /// <summary>
    /// A private key as the service keeps it: lines ended by a line feed alone, nothing around it. Null when the text
    /// is not a private key in the OpenSSH or PEM format (a key of PuTTY, a public key, some other file).
    /// </summary>
    public static string? CleanKey(string? text)
    {
        string key = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        return key.StartsWith("-----BEGIN", StringComparison.Ordinal) && key.Contains("PRIVATE KEY", StringComparison.Ordinal) ? key + "\n" : null;
    }

    protected override void OnOk()
    {
        string kind = Kind;
        bool windows = kind == CredentialKinds.Windows;
        bool s3 = kind == CredentialKinds.S3;
        string user = _user.Text.Trim();
        string domain = windows ? _domain.Text.Trim() : string.Empty;
        if (windows && domain.Length == 0 && user.IndexOf('\\', StringComparison.Ordinal) is int slash and > 0)
        {
            domain = user[..slash];
            user = user[(slash + 1)..];
        }

        if (user.Length == 0)
        {
            Ui.ShowWarning(this, s3 ? Strings.ErrorAccessKeyRequired : Strings.ErrorUserRequired);
            return;
        }

        string? secret;
        if (ByKey)
        {
            if (string.IsNullOrWhiteSpace(_key.Text))
            {
                if (_existing is null)
                {
                    Ui.ShowWarning(this, Strings.ErrorKeyRequired);
                    return;
                }

                secret = null;
            }
            else if (CleanKey(_key.Text) is { } key)
            {
                secret = key;
            }
            else
            {
                Ui.ShowWarning(this, Strings.ErrorKeyBad);
                return;
            }
        }
        else
        {
            secret = _password.Text.Length > 0 ? _password.Text : null;
            if (_existing is null && secret is null)
            {
                Ui.ShowWarning(this, s3 ? Strings.ErrorSecretKeyRequired : Strings.ErrorPasswordRequired);
                return;
            }
        }

        var credential = new Credential
        {
            Id = _existing?.Id ?? TaskDefinition.NewId(),
            Kind = kind,
            User = user,
            Domain = domain.Length == 0 ? null : domain,
            ExtensionData = _existing?.ExtensionData,
        };
        SetBusy(true);
        Ui.Run(this, async () =>
        {
            try
            {
                var result = await _monitor.RequestAsync(PipeProtocol.Methods.CredentialSave, new CredentialInput { Credential = credential, Password = secret },
                    IpcJsonContext.Default.CredentialInput, IpcJsonContext.Default.CredentialSaveResult);
                Saved = result.Credential;
                _password.Clear();
                _key.Clear();
                DialogResult = DialogResult.OK;
            }
            finally
            {
                SetBusy(false);
            }
        });
    }

    private string Kind => _kind.SelectedIndex >= 0 && _kind.SelectedIndex < Kinds.Length ? Kinds[_kind.SelectedIndex] : _fixedKind ?? CredentialKinds.Windows;

    /// <summary>SFTP with a private key in place of a password.</summary>
    private bool ByKey => Kind == CredentialKinds.Sftp && _byKey.Checked;

    private static void SetVisible(Control[] row, bool visible)
    {
        foreach (var control in row)
        {
            control.Visible = visible;
        }
    }

    private Control[] AddGridRow(Control caption, Control control, Control? extra = null)
    {
        extra ??= new Label { AutoSize = true };
        control.Margin = new Padding(0, 3, 0, 3);
        extra.Margin = new Padding(6, 3, 0, 3);
        Body.Controls.Add(caption);
        Body.Controls.Add(control);
        Body.Controls.Add(extra);
        return [caption, control, extra];
    }

    /// <summary>Shows the fields of the chosen kind, and what its secret is called.</summary>
    private void UpdateFields()
    {
        string kind = Kind;
        bool windows = kind == CredentialKinds.Windows;
        bool s3 = kind == CredentialKinds.S3;
        bool key = ByKey;
        Body.SuspendLayout();
        _userLabel.Text = s3 ? Strings.AccountAccessKey : Strings.AccountUser;
        _passwordLabel.Text = s3 ? Strings.AccountSecretKey : Strings.AccountPassword;
        SetVisible(_domainRow, windows);
        _domainNote.Visible = windows;
        _way.Visible = kind == CredentialKinds.Sftp;
        SetVisible(_passwordRow, !key);
        _show.Visible = !key;
        SetVisible(_keyRow, key);
        string kept = key ? Strings.AccountKeyNote : s3 ? Strings.AccountSecretNote : Strings.AccountPasswordNote;
        _note.Text = _existing is null ? kept
            : key ? Strings.AccountKeyNote + "\n" + Strings.AccountKeepSecretNote
            : s3 ? Strings.AccountKeepSecretNote
            : Strings.AccountKeepPasswordNote;
        Body.ResumeLayout(performLayout: true);
    }

    /// <summary>Reads a private key from its file into the field. The window runs as the user, so it reads what the user may read.</summary>
    private void TakeKeyFile()
    {
        using var dialog = new OpenFileDialog { Filter = Strings.KeyFileFilter, CheckFileExists = true };
        string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        if (Directory.Exists(home))
        {
            dialog.InitialDirectory = home;
        }

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        string text;
        try
        {
            if (new FileInfo(dialog.FileName).Length > LargestKeyFile)
            {
                Ui.ShowWarning(this, Strings.ErrorKeyBad);
                return;
            }

            text = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Ui.ShowWarning(this, Strings.Format(Strings.ErrorKeyFile, dialog.FileName, ex.Message));
            return;
        }

        if (CleanKey(text) is not { } key)
        {
            Ui.ShowWarning(this, Strings.ErrorKeyBad);
            return;
        }

        // The edit control of Windows breaks lines only at CR LF.
        _key.Text = key.Replace("\n", "\r\n", StringComparison.Ordinal);
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
    private readonly TextBox _repeat = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, Margin = new Padding(0, 3, 0, 3) };
    private readonly Label _repeatCaption = Caption(Strings.ArchivePasswordRepeat);
    private readonly CheckBox _show = new() { Text = Strings.ShowPassword, AutoSize = true };

    public ArchivePasswordDialog(ServiceMonitor monitor, string? existingId, string taskName)
        : base(Strings.ArchivePasswordTitle, new Size(540, 300))
    {
        _monitor = monitor;
        _existingId = existingId;
        _taskName = taskName;
        AddRow(Strings.AccountPassword, _password);
        Body.Controls.Add(_repeatCaption);
        Body.Controls.Add(_repeat);
        Body.Controls.Add(new Label { AutoSize = true });
        AddWide(_show);
        AddNote(existingId is null ? Strings.ArchivePasswordWarning : Strings.ArchivePasswordChangeNote);
        EndBuild();

        // A password one can see needs no second typing.
        _show.CheckedChanged += (_, _) =>
        {
            _password.UseSystemPasswordChar = !_show.Checked;
            _repeat.UseSystemPasswordChar = !_show.Checked;
            _repeatCaption.Visible = !_show.Checked;
            _repeat.Visible = !_show.Checked;
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

        if (!_show.Checked && !string.Equals(_password.Text, _repeat.Text, StringComparison.Ordinal))
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
    private static readonly string[] TrayModeValues = [TrayModes.Full, TrayModes.Status, TrayModes.Hidden];
    private static readonly string[] ThemeValues = [Themes.Auto, Themes.Light, Themes.Dark];

    private readonly ServiceMonitor _monitor;
    private readonly ComboBox _logLevel = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly NumericUpDown _retention = new() { Minimum = 1, Maximum = 3650, Width = 80 };
    private readonly NumericUpDown _historyRetention = new() { Minimum = 1, Maximum = 3650, Width = 80 };
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
    private readonly Button _connectCloud = new() { Text = Strings.ButtonConnectCloud, AutoSize = true, MinimumSize = new Size(110, 0) };
    private readonly ContextMenuStrip _cloudMenu = new();
    private readonly Label _cloudApps = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 2) };
    private readonly Button _cloudOwn = new() { Text = Strings.SettingsCloudOwn, AutoSize = true, Margin = new Padding(8, 0, 0, 0), Enabled = false };
    private CloudAppsResult? _cloudAppsResult;
    private readonly ToolTip _toolTip = new();
    private readonly bool _canEdit;
    private readonly Label _archiver = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 2) };
    private readonly Button _sevenZipCheck = new() { Text = Strings.SevenZipCheckButton, AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
    /// <summary>What a connection line looks like, with made-up values.</summary>
    internal const string ConnectionExample = "http://custos.example.lan#K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ#3F2A-91C0-77DE-0B14";

    private readonly Label _serverState = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 6) };
    private readonly TextBox _serverLine = new() { Dock = DockStyle.Fill, PlaceholderText = ConnectionExample };
    private readonly Button _serverPaste = new() { Size = new Size(30, 25), Margin = new Padding(0), AccessibleName = Strings.ServerPasteTip };
    private readonly Button _serverConnect = new() { Text = Strings.ServerConnectButton, AutoSize = true, MinimumSize = new Size(110, 0) };
    private readonly Button _serverReset = new() { Text = Strings.ServerResetButton, AutoSize = true, Margin = new Padding(8, 3, 0, 3) };
    private readonly Label _serverKeys = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 0, 2) };
    private readonly CheckBox _allowRemoteEdit = new() { Text = Strings.AllowRemoteEdit, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly CheckBox _allowAll = new() { Text = Strings.AllowAllUsersEdit, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly ComboBox _trayMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly Label _passwordState = new() { AutoSize = true, Margin = new Padding(0, 6, 8, 0) };
    private readonly Button _passwordSet = new() { AutoSize = true, MinimumSize = new Size(100, 0), Margin = new Padding(0, 0, 6, 0) };
    private readonly Button _passwordRemove = new() { Text = Strings.ProgramPasswordRemoveButton, AutoSize = true, MinimumSize = new Size(100, 0), Margin = new Padding(0) };
    private readonly ComboBox _theme = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly CheckBox _checkUpdates = new() { Text = Strings.CheckUpdates, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly Label _updatesState = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 2) };
    private readonly Button _updatesCheck = new() { Text = Strings.UpdatesCheckNow, AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
    private readonly bool _isAdmin;

    public SettingsForm(ServiceMonitor monitor)
        : base(Strings.SettingsTitle, new Size(680, 760))
    {
        _monitor = monitor;
        _canEdit = monitor.CanEdit;
        _logLevel.Items.AddRange([Strings.LogLevelSummary, Strings.LogLevelFiles]);
        _priority.Items.AddRange([Strings.PriorityLow, Strings.PriorityNormal]);
        _language.Items.AddRange([Strings.LanguageAuto, Strings.LanguageRussian, Strings.LanguageEnglish]);
        _trayMode.Items.AddRange([Strings.TrayModeFull, Strings.TrayModeStatus, Strings.TrayModeHidden]);
        _theme.Items.AddRange([Strings.ThemeAuto, Strings.ThemeLight, Strings.ThemeDark]);
        _accounts.Columns.Add(Strings.ColumnAccount, 200);
        _accounts.Columns.Add(Strings.ColumnAccountKind, 150);
        _accounts.Columns.Add(Strings.ColumnUsedBy, 220);

        _isAdmin = monitor.Status.Hello?.IsAdmin == true;
        var serverHeading = new Label { Text = Strings.SettingsServer, AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        var heading = new Label { Text = Strings.SettingsLogs, AutoSize = true, Margin = new Padding(0, 14, 0, 4) };
        heading.Font = new Font(heading.Font, FontStyle.Bold);
        serverHeading.Font = heading.Font;
        AddWide(serverHeading);
        AddWide(_serverState);
        // The line is long and comes from the site: one press of the button puts it in.
        _serverPaste.Image = Glyphs.Draw(Glyph.Paste, 16);
        _toolTip.SetToolTip(_serverPaste, Strings.ServerPasteTip);
        var lineHelp = Ui.HelpIcon(_toolTip, Strings.ServerLineHelp);
        lineHelp.Margin = new Padding(4, 3, 0, 0);
        var lineTools = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        lineTools.Controls.AddRange([_serverPaste, lineHelp]);
        AddRow(Strings.ServerLineLabel, _serverLine, lineTools);
        lineTools.Margin = new Padding(6, 2, 0, 2);
        var serverButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 0) };
        serverButtons.Controls.AddRange([_serverConnect, _serverReset]);
        AddWide(serverButtons);
        AddWide(_serverKeys);
        var remoteEdit = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        var remoteEditHelp = Ui.HelpIcon(_toolTip, Strings.AllowRemoteEditHelp);
        remoteEditHelp.Margin = new Padding(6, 8, 0, 0);
        remoteEdit.Controls.AddRange([_allowRemoteEdit, remoteEditHelp]);
        AddWide(remoteEdit);
        if (!_isAdmin)
        {
            AddNote(Strings.ServerAdminOnly);
        }

        // Section 4: who may change things, what users see, the program password.
        var rightsHeading = new Label { Text = Strings.SettingsRights, AutoSize = true, Margin = new Padding(0, 14, 0, 4) };
        rightsHeading.Font = heading.Font;
        AddWide(rightsHeading);
        var allowAll = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        var allowAllHelp = Ui.HelpIcon(_toolTip, Strings.AllowAllUsersEditHelp);
        allowAllHelp.Margin = new Padding(6, 8, 0, 0);
        allowAll.Controls.AddRange([_allowAll, allowAllHelp]);
        AddWide(allowAll);
        AddRow(Strings.TrayModeLabel, _trayMode, Ui.HelpIcon(_toolTip, Strings.TrayModeHelp));
        var password = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        password.Controls.AddRange([_passwordState, _passwordSet, _passwordRemove]);
        AddRow(Strings.ProgramPasswordLabel, password, Ui.HelpIcon(_toolTip, Strings.ProgramPasswordHelp));
        if (!_isAdmin)
        {
            AddNote(Strings.SettingsRightsAdminOnly);
        }

        AddWide(heading);
        AddRow(Strings.DefaultLogLevel, _logLevel, Ui.HelpIcon(_toolTip, Strings.DefaultLogLevelHelp));
        AddRow(Strings.LogRetention, _retention, Ui.HelpIcon(_toolTip, Strings.LogRetentionHelp));
        AddRow(Strings.HistoryRetention, _historyRetention, Ui.HelpIcon(_toolTip, Strings.HistoryRetentionHelp));
        AddRow(Strings.CopyPriority, _priority, Ui.HelpIcon(_toolTip, Strings.CopyPriorityHelp));
        AddRow(Strings.ServiceLanguage, _language, Ui.HelpIcon(_toolTip, Strings.ServiceLanguageHelp));

        // The legacy edition has no dark theme (Theme), so it has nothing to choose here.
        if (!CustosEdition.IsLegacy)
        {
            var lookHeading = new Label { Text = Strings.SettingsLook, AutoSize = true, Margin = new Padding(0, 14, 0, 4) };
            lookHeading.Font = heading.Font;
            AddWide(lookHeading);
            AddRow(Strings.ThemeLabel, _theme, Ui.HelpIcon(_toolTip, Strings.ThemeHelp));
        }

        // New versions of the program on GitHub, for a computer without a monitoring site.
        var updatesHeading = new Label { Text = Strings.SettingsUpdates, AutoSize = true, Margin = new Padding(0, 14, 0, 4) };
        updatesHeading.Font = heading.Font;
        AddWide(updatesHeading);
        var checkUpdates = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        var checkUpdatesHelp = Ui.HelpIcon(_toolTip, Strings.Format(Strings.CheckUpdatesHelp, CustosInfo.GitHubRepository));
        checkUpdatesHelp.Margin = new Padding(6, 8, 0, 0);
        checkUpdates.Controls.AddRange([_checkUpdates, checkUpdatesHelp]);
        AddWide(checkUpdates);
        var updatesRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        updatesRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        updatesRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        updatesRow.Controls.Add(_updatesState, 0, 0);
        updatesRow.Controls.Add(_updatesCheck, 1, 0);
        AddWide(updatesRow);
        _updatesCheck.Click += (_, _) => Ui.Run(this, CheckUpdatesAsync);

        var archiverHeading = new Label { Text = Strings.Settings7Zip, AutoSize = true, Margin = new Padding(0, 14, 0, 4) };
        archiverHeading.Font = heading.Font;
        AddWide(archiverHeading);
        var archiverRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        archiverRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        archiverRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        archiverRow.Controls.Add(_archiver, 0, 0);
        archiverRow.Controls.Add(_sevenZipCheck, 1, 0);
        AddWide(archiverRow);
        AddNote(CustosEdition.IsLegacy ? Strings.Settings7ZipNoteLegacy : Strings.Settings7ZipNote);
        ShowArchiver();
        bool admin = monitor.Status.Hello?.IsAdmin == true;
        _sevenZipCheck.Enabled = admin && monitor.IsConnected;
        if (!admin)
        {
            _toolTip.SetToolTip(_sevenZipCheck, Strings.SevenZipAdminOnly);
        }

        _sevenZipCheck.Click += (_, _) => Ui.Run(this, CheckSevenZipAsync);

        // Cloud drives (section 6.3): what this version brings, and the administrator's own applications. The legacy
        // edition has none (CustosEdition).
        if (!CustosEdition.IsLegacy)
        {
            var cloudHeading = new Label { Text = Strings.SettingsCloud, AutoSize = true, Margin = new Padding(0, 14, 0, 4) };
            cloudHeading.Font = heading.Font;
            AddWide(cloudHeading);
            var cloudRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
            cloudRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            cloudRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            cloudRow.Controls.Add(_cloudApps, 0, 0);
            cloudRow.Controls.Add(_cloudOwn, 1, 0);
            AddWide(cloudRow);
            AddNote(admin ? Strings.SettingsCloudNote : Strings.CloudAdminOnly);
            _cloudApps.Text = admin ? string.Empty : Strings.Format(Strings.SettingsCloudBuiltIn, "—");
            foreach (string kind in CloudNames.Kinds)
            {
                string chosen = kind;
                _cloudMenu.Items.Add(CloudNames.Name(kind), null, (_, _) => ConnectCloud(chosen, null));
            }
        }

        var accountsHeading = new Label { Text = Strings.SettingsAccounts, AutoSize = true, Margin = new Padding(0, 14, 0, 4) };
        accountsHeading.Font = heading.Font;
        AddWide(accountsHeading);
        AddNote(Strings.SettingsAccountsNote);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(8, 0, 0, 0) };
        buttons.Controls.AddRange(CustosEdition.IsLegacy ? [_addAccount, _editAccount, _deleteAccount] : [_addAccount, _connectCloud, _editAccount, _deleteAccount]);
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
        _historyRetention.Value = Math.Clamp(settings.HistoryRetentionDays, 1, 3650);
        _priority.SelectedIndex = settings.ProcessPriority == "normal" ? 1 : 0;
        int language = Array.IndexOf(Languages, settings.Language);
        _language.SelectedIndex = language < 0 ? 0 : language;

        int theme = Array.IndexOf(ThemeValues, settings.Theme);
        _theme.SelectedIndex = theme < 0 ? 0 : theme;
        int trayMode = Array.IndexOf(TrayModeValues, settings.TrayMode);
        _trayMode.SelectedIndex = trayMode < 0 ? 0 : trayMode;
        _allowAll.Checked = settings.AllowAllUsersEdit;
        _checkUpdates.Checked = settings.CheckUpdates;

        _allowRemoteEdit.Checked = settings.AllowRemoteEdit;
        foreach (var control in new Control[] { _logLevel, _retention, _historyRetention, _priority, _language, _theme, _addAccount })
        {
            control.Enabled = _canEdit;
        }

        // The rights stay with the administrators even when everybody may change tasks (the service keeps them so).
        foreach (var control in new Control[] { _allowRemoteEdit, _allowAll, _trayMode })
        {
            control.Enabled = _canEdit && _isAdmin;
        }

        _allowAll.CheckedChanged += (_, _) =>
        {
            // Turning it on hands out the rights of an administrator: said plainly, and only on "Yes".
            if (_allowAll.Checked && !settings.AllowAllUsersEdit && !Ui.Confirm(this, Strings.AllowAllUsersEditWarning))
            {
                _allowAll.Checked = false;
            }
        };
        ShowUpdates();
        _passwordSet.Click += (_, _) => SetPassword();
        _passwordRemove.Click += (_, _) => Ui.Run(this, RemovePasswordAsync);
        ShowPassword();

        foreach (var control in new Control[] { _serverLine, _serverPaste, _serverConnect })
        {
            control.Enabled = _isAdmin && monitor.IsConnected;
        }

        _serverPaste.Click += (_, _) => PasteServerLine();
        _serverConnect.Click += (_, _) => Ui.Run(this, ConnectServerAsync);
        _serverReset.Click += (_, _) => Ui.Run(this, ResetServerAsync);
        ShowServer();

        OkButton.Visible = _canEdit;
        CancelButtonControl.Text = _canEdit ? Strings.ButtonCancel : Strings.ButtonClose;

        _accounts.SelectedIndexChanged += (_, _) => UpdateButtons();
        _addAccount.Click += (_, _) => AddAccount();
        _connectCloud.Enabled = _isAdmin && monitor.IsConnected;
        if (!_isAdmin)
        {
            _toolTip.SetToolTip(_connectCloud, Strings.CloudAdminOnly);
        }

        _connectCloud.Click += (_, _) => _cloudMenu.Show(_connectCloud, new Point(0, _connectCloud.Height));
        _cloudOwn.Click += (_, _) => EditCloudApps();
        if (_isAdmin && monitor.IsConnected)
        {
            Ui.Run(this, LoadCloudAppsAsync);
        }

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
            _cloudMenu.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>The daily check of GitHub: on or off, and what it last found. A computer connected to a monitoring
    /// site gets its updates from the site, so the check is not offered there.</summary>
    private void ShowUpdates()
    {
        var update = _monitor.Snapshot?.ProgramUpdate ?? new ProgramUpdateInfo();
        bool site = update.ManagedBySite;
        _checkUpdates.Enabled = _canEdit && _isAdmin && !site;
        // Off (or a site decides): a check would show nowhere, so there is nothing to press.
        _updatesCheck.Enabled = _isAdmin && _monitor.IsConnected && update.Checking;
        _toolTip.SetToolTip(_updatesCheck, _isAdmin ? null : Strings.UpdateAdminOnly);
        _updatesState.ForeColor = update.Error is null || site ? SystemColors.ControlText : Glyphs.WarningText;
        if (site)
        {
            _updatesState.Text = Strings.UpdatesManagedBySite;
            return;
        }

        string found = update.Error
            ?? (update.Latest is null ? Strings.UpdatesNone
                : update.Available ? Strings.Format(Strings.UpdatesAvailable, update.Latest)
                : Strings.Format(Strings.UpdatesLatest, CustosInfo.Version));
        _updatesState.Text = !update.Checking ? Strings.UpdatesOff
            : update.CheckedAt is { } at ? Strings.Format(Strings.UpdatesLastCheck, Ui.Time(at), found)
            : Strings.UpdatesNever;
    }

    /// <summary>«Проверить сейчас»: the service asks GitHub at once.</summary>
    private async Task CheckUpdatesAsync()
    {
        _updatesCheck.Enabled = false;
        UseWaitCursor = true;
        try
        {
            await _monitor.RequestAsync(PipeProtocol.Methods.ProgramCheck, EmptyRequest.Instance,
                IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.ProgramUpdateInfo, TimeSpan.FromMinutes(1));
        }
        finally
        {
            UseWaitCursor = false;
            if (!IsDisposed)
            {
                ShowUpdates();
            }
        }
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

            if (!Ui.Confirm(this, Strings.Format(CustosEdition.IsLegacy ? Strings.SevenZipAskUpdateLegacy : Strings.SevenZipAskUpdate, check.Installed ?? "—", check.Latest)))
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
        settings.HistoryRetentionDays = (int)_historyRetention.Value;
        settings.ProcessPriority = _priority.SelectedIndex == 1 ? "normal" : "low";
        settings.Language = Languages[Math.Max(0, _language.SelectedIndex)];
        settings.Theme = ThemeValues[Math.Max(0, _theme.SelectedIndex)];
        settings.TrayMode = TrayModeValues[Math.Max(0, _trayMode.SelectedIndex)];
        settings.AllowAllUsersEdit = _allowAll.Checked;
        settings.AllowRemoteEdit = _allowRemoteEdit.Checked;
        settings.CheckUpdates = _checkUpdates.Checked;
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

    private void OnSnapshotChanged(object? sender, EventArgs e)
    {
        FillAccounts();
        ShowPassword();
        ShowServer();
        ShowUpdates();
    }

    /// <summary>Whether a program password is set, and the buttons for it (administrators only).</summary>
    private void ShowPassword()
    {
        bool set = _monitor.Snapshot?.ProgramPasswordSet == true;
        _passwordState.Text = set ? Strings.ProgramPasswordIsSet : Strings.ProgramPasswordNotSet;
        _passwordSet.Text = set ? Strings.ProgramPasswordChangeButton : Strings.ProgramPasswordSetButton;
        _passwordSet.Enabled = _isAdmin && _monitor.IsConnected;
        _passwordRemove.Visible = set;
        _passwordRemove.Enabled = _isAdmin && _monitor.IsConnected;
    }

    private void SetPassword()
    {
        using var form = new ProgramPasswordForm(_monitor);
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            Ui.ShowInfo(this, Strings.ProgramPasswordSaved);
        }
    }

    private async Task RemovePasswordAsync()
    {
        if (!Ui.Confirm(this, Strings.ProgramPasswordRemoveConfirm))
        {
            return;
        }

        await _monitor.RequestAsync(PipeProtocol.Methods.PasswordSet, new PasswordRequest { Password = null },
            IpcJsonContext.Default.PasswordRequest, IpcJsonContext.Default.OkResult);
        Ui.ShowInfo(this, Strings.ProgramPasswordRemoved);
    }

    /// <summary>The state of the connection to the monitoring server, the fingerprints and the reports waiting.</summary>
    private void ShowServer()
    {
        var server = _monitor.Snapshot?.Server ?? new ServerLinkInfo();
        string state = ServerLink.StateText(server.State);
        if (server.ServerName is { Length: > 0 } name && server.State is ServerLinkStates.Approved or ServerLinkStates.Pending)
        {
            state += " " + Strings.Format(Strings.ServerNamed, name);
        }

        _serverState.Text = server.Detail is { } detail ? state.TrimEnd('.') + ": " + detail : state;
        _serverState.ForeColor = server.State is ServerLinkStates.Approved or ServerLinkStates.Off or ServerLinkStates.Pending or ServerLinkStates.Connecting
            ? SystemColors.ControlText : Glyphs.WarningText;
        // The line itself is not shown again (it holds the code): the address it gave is.
        var lines = new List<string>();
        if (server.ServerUrl is { } address)
        {
            lines.Add(Strings.Format(Strings.ServerAddress, address));
        }

        lines.Add(Strings.Format(Strings.ServerFingerprints, server.ServerFingerprint ?? "—", server.AgentFingerprint ?? "—"));
        if (server.LastContact is { } contact)
        {
            lines.Add(Strings.Format(Strings.ServerLastContact, contact.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
        }

        if (server.Waiting > 0)
        {
            lines.Add(Strings.Format(Strings.ServerWaiting, server.Waiting));
        }

        _serverKeys.Text = string.Join(Environment.NewLine, lines);
        _serverReset.Enabled = _isAdmin && _monitor.IsConnected && server.ServerUrl is not null;
    }

    /// <summary>Reads the text of the clipboard; the tests put their own text here.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<string?> ClipboardText { get; set; } = Ui.ClipboardText;

    /// <summary>
    /// The button next to the field: the line copied on the site replaces what the field holds. Of several lines the
    /// first one with text is taken, so a line copied together with its line break fits too.
    /// </summary>
    internal void PasteServerLine()
    {
        string? line = ClipboardText()?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(line))
        {
            Ui.ShowWarning(this, Strings.ServerPasteEmpty);
            return;
        }

        _serverLine.Text = line;
        _serverLine.SelectionStart = line.Length;
        _serverLine.Focus();
    }

    /// <summary>
    /// «Подключить»: the connection line of the site (A.3), the address and the registration code in one, goes to the
    /// service, which registers.
    /// </summary>
    private async Task ConnectServerAsync()
    {
        string line = _serverLine.Text.Trim();
        if (line.Length == 0)
        {
            Ui.ShowWarning(this, Strings.ServerLineRequired);
            _serverLine.Focus();
            return;
        }

        if (!RegistrationCode.TrySplitConnection(line, out string url, out string code) || RegistrationCode.TryParse(code, out _) is null)
        {
            Ui.ShowWarning(this, Strings.Format(Strings.ServerLineBad, ConnectionExample));
            _serverLine.Focus();
            return;
        }

        _serverConnect.Enabled = false;
        try
        {
            await _monitor.RequestAsync(PipeProtocol.Methods.ServerConnect, new ServerConnectRequest { ServerUrl = url, Code = code },
                IpcJsonContext.Default.ServerConnectRequest, IpcJsonContext.Default.OkResult);
            _serverLine.Text = string.Empty;
            Ui.ShowInfo(this, Strings.ServerConnectDone);
        }
        finally
        {
            if (!IsDisposed)
            {
                _serverConnect.Enabled = true;
            }
        }
    }

    /// <summary>«Сбросить привязку к серверу» (A.3): local, administrators only.</summary>
    private async Task ResetServerAsync()
    {
        string server = _monitor.Snapshot?.Server.ServerHost ?? "?";
        if (!Ui.Confirm(this, Strings.Format(Strings.ServerResetConfirm, server)))
        {
            return;
        }

        await _monitor.RequestAsync(PipeProtocol.Methods.ServerReset, EmptyRequest.Instance,
            IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.OkResult);
    }

    private void FillAccounts()
    {
        var snapshot = _monitor.Snapshot;
        string? selected = _accounts.SelectedItems.Count > 0 ? (string)_accounts.SelectedItems[0].Tag! : null;
        _accounts.BeginUpdate();
        _accounts.Items.Clear();
        string[] kinds = [.. CredentialDialog.Kinds, .. CloudNames.Kinds];
        var shown = (snapshot?.Credentials ?? [])
            .Where(c => Array.IndexOf(kinds, c.Kind) >= 0)
            .OrderBy(c => Array.IndexOf(kinds, c.Kind))
            .ThenBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase);
        foreach (var credential in shown)
        {
            var users = snapshot!.Tasks.Where(t => UsesCredential(t, credential.Id)).Select(t => t.Name).ToList();
            var item = new ListViewItem([credential.DisplayName, CredentialDialog.KindText(credential.Kind), users.Count == 0 ? Strings.AccountNotUsed : string.Join(", ", users)]) { Tag = credential.Id };
            _accounts.Items.Add(item);
            item.Selected = string.Equals(credential.Id, selected, StringComparison.OrdinalIgnoreCase);
        }

        _accounts.EndUpdate();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool any = _accounts.SelectedItems.Count > 0;
        bool cloud = CredentialKinds.IsCloud(SelectedAccount()?.Kind);

        // An account of a cloud drive has no password here: it is connected again in the browser.
        _editAccount.Text = cloud ? Strings.ButtonReconnect : Strings.ButtonChangePassword;
        _editAccount.Enabled = _canEdit && any && (!cloud || _isAdmin);
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

        if (CredentialKinds.IsCloud(account.Kind))
        {
            ConnectCloud(account.Kind, account);
            return;
        }

        using var dialog = new CredentialDialog(_monitor, account);
        dialog.ShowDialog(this);
    }

    /// <summary>Connects a new account of a cloud drive, or an existing one again.</summary>
    private void ConnectCloud(string kind, Credential? existing)
    {
        using var dialog = new CloudConnectDialog(_monitor, kind, existing);
        dialog.ShowDialog(this);
    }

    /// <summary>Which services this version has an application for; only administrators may ask.</summary>
    private async Task LoadCloudAppsAsync()
    {
        try
        {
            _cloudAppsResult = await _monitor.RequestAsync(PipeProtocol.Methods.CloudAppsGet, EmptyRequest.Instance,
                IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.CloudAppsResult);
        }
        catch (ServiceUnavailableException ex)
        {
            if (!IsDisposed)
            {
                _cloudApps.Text = ex.Message;
            }

            return;
        }

        if (IsDisposed)
        {
            return;
        }

        ShowCloudApps();
        _cloudOwn.Enabled = true;
    }

    private void ShowCloudApps()
    {
        var builtIn = _cloudAppsResult?.BuiltIn ?? [];
        string names = builtIn.Count == 0 ? Strings.SettingsCloudNone : string.Join(", ", CloudNames.Kinds.Where(builtIn.Contains).Select(CloudNames.Name));
        _cloudApps.Text = Strings.Format(Strings.SettingsCloudBuiltIn, names);
    }

    private void EditCloudApps()
    {
        if (_cloudAppsResult is not { } current)
        {
            return;
        }

        using var dialog = new CloudAppsDialog(_monitor, current);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            current.Own = dialog.Entered();
        }
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
    private readonly RadioButton _all = new() { Text = Strings.DbChoiceAll, AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
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
        AddRow(Strings.DbHost, _host, Ui.HelpIcon(Tips, Strings.DbHostHelp));
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

        // A tick always means "dumped": with «All databases» the stored exclusions are the unticked ones.
        foreach (string name in server.AllDatabases ? server.Exclude : server.Include)
        {
            _databases.Items.Add(name, isChecked: !server.AllDatabases);
        }

        UpdatePassword();
        _all.CheckedChanged += (_, _) => ChoiceChanged();
        _databases.ItemCheck += (_, e) => DatabaseTicked(e);
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
    internal TaskDatabaseServer Build()
    {
        var chosen = _databases.CheckedItems.Cast<string>().ToList();
        var left = _databases.Items.Cast<string>().Where(n => !chosen.Contains(n, StringComparer.Ordinal)).ToList();
        return new TaskDatabaseServer
        {
            Id = _original.Id,
            Engine = Engine,
            BinDir = _binDir.Text.Trim().Trim('"'),
            Host = _host.Text.Trim(),
            Port = (int)_port.Value,
            CredentialId = _original.CredentialId,
            AllDatabases = _all.Checked,
            Exclude = _all.Checked ? left : [],
            Include = _all.Checked ? [] : chosen,
            DataDir = _dataDir,
            ExtensionData = _original.ExtensionData,
        };
    }

    /// <summary>Unticking a database under «All databases» means a choice of databases: «Selected databases».</summary>
    private void DatabaseTicked(ItemCheckEventArgs e)
    {
        if (_all.Checked && e.NewValue == CheckState.Unchecked)
        {
            _only.Checked = true;
        }
    }

    /// <summary>«All databases» starts with every database ticked; «Selected databases» keeps the ticks as they are.</summary>
    private void ChoiceChanged()
    {
        if (!_all.Checked)
        {
            return;
        }

        for (int i = 0; i < _databases.Items.Count; i++)
        {
            _databases.SetItemChecked(i, true);
        }
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
            _databases.Items.Add(name, isChecked: _all.Checked);
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
