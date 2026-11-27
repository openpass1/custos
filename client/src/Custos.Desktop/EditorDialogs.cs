using System.Globalization;
using Avalonia.Platform.Storage;
using Custos.Core;
using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>Adds or changes one source of a task: a folder, a file or a network folder with its account.</summary>
internal sealed class SourceDialog : FormDialog
{
    private readonly TaskSource _original;
    private readonly RadioGroup _kinds = new();
    private readonly RadioItem _folder;
    private readonly RadioItem _file;
    private readonly RadioItem _network;
    private readonly TextItem _path;
    private readonly CheckItem _recursive = new(Strings.SourceRecursive);
    private readonly AccountPicker _account;

    public SourceDialog(EditorContext context, TaskSource source)
        : base(source.Path.Length == 0 ? Strings.SourceAddTitle : Strings.SourceEditTitle, 680)
    {
        _original = source;
        _folder = new RadioItem(_kinds, Strings.SourceFolder, inline: true);
        _file = new RadioItem(_kinds, Strings.SourceFile, inline: true);
        _network = new RadioItem(_kinds, Strings.SourceNetwork, inline: true);
        _path = new TextItem(EditorPage_L(Strings.SourcePath)) { ActionIcon = "folder_open", ActionTip = Strings.ButtonBrowse, Action = BrowseAsync };
        _account = new AccountPicker(context, () => this, EditorPage_L(Strings.DestinationAccount),
            CustosEdition.IsLinux ? DesktopStrings.LinuxSmbAccountHelp : Strings.SourceAccountHelp);
        if (CustosEdition.IsLinux)
        {
            _account.SetKind(CredentialKinds.Windows, Strings.AccountNone);
        }
        Items.Add(new RowItem(_folder, _file, _network));
        Items.Add(_path);
        Items.Add(_recursive);
        Items.Add(_account.Row);
        Items.Add(new NoteItem(CustosEdition.IsLinux ? DesktopStrings.LinuxSourcePathHelp : Strings.SourceNote));
        Items.Add(new NoteItem(CustosEdition.IsLinux ? DesktopStrings.LinuxPathVariables : Strings.PathVariablesNote));

        (source.Kind switch
        {
            SourceKinds.File => _file,
            SourceKinds.Unc => _network,
            _ => _folder,
        }).Load();
        _path.Load(source.Path);
        _recursive.Load(source.Recursive);
        _account.SelectedId = source.CredentialId;
        UpdateFields();

        foreach (var kind in _kinds.Items)
        {
            kind.OnChange = UpdateFields;
        }

        _path.OnChange = () =>
        {
            if (IsNetworkPath(_path.Text) && !_network.On)
            {
                _network.On = true;
            }
        };
    }

    /// <summary>The source as entered, after OK.</summary>
    public TaskSource Source { get; private set; } = new();

    protected override Task<bool> OkAsync()
    {
        string path = _path.Value;
        string? problem = path.Length == 0 ? Strings.ErrorPathRequired : CheckPath(path);
        if (problem is not null)
        {
            _path.Error = problem;
            return Task.FromResult(false);
        }

        Source = new TaskSource
        {
            Id = _original.Id,
            Kind = _file.On ? SourceKinds.File : _network.On ? SourceKinds.Unc : SourceKinds.Folder,
            Path = CustosEdition.IsLinux && _network.On ? LinuxEditorPaths.NormalizeShare(path) : path,
            Recursive = _file.On || _recursive.On,
            CredentialId = _network.On ? _account.SelectedId : null,
            ExtensionData = _original.ExtensionData,
        };
        return Task.FromResult(true);
    }

    internal static string EditorPage_L(string label) => label.TrimEnd(':', ' ');

    private string? CheckPath(string path)
    {
        if (CustosEdition.IsLinux)
        {
            return _network.On
                ? LinuxEditorPaths.IsShare(path) ? null : Strings.Format(DesktopStrings.LinuxErrorSharePath, path)
                : LinuxEditorPaths.IsLocal(path) ? null : Strings.Format(DesktopStrings.LinuxErrorLocalPath, path);
        }

        return !PathVariables.LooksAbsolute(path) ? Strings.Format(Strings.ErrorPathNotAbsolute, path)
            : _network.On && !IsNetworkPath(path) ? Strings.Format(Strings.ErrorNotUnc, path)
            : !_network.On && IsNetworkPath(path) ? Strings.Format(Strings.ErrorUncNeedsNetwork, path)
            : null;
    }

    internal static bool IsNetworkPath(string path) => CustosEdition.IsLinux ? BackupEngine.IsNetworkPath(path)
        : path.StartsWith(@"\\", StringComparison.Ordinal);

    private void UpdateFields()
    {
        _recursive.IsEnabled = !_file.On;
        _account.Row.IsVisible = _network.On;
    }

    private async Task BrowseAsync()
    {
        string? picked = _file.On
            ? await PickFileAsync(this, Strings.SourceFile, _path.Value)
            : await PickFolderAsync(this, Strings.SourceFolder, _path.Value);
        if (picked is not null)
        {
            _path.Text = picked;
        }
    }
}

/// <summary>
/// Adds or changes one destination of a task (section 6.3): a local folder, a network folder with its account, a USB
/// disk found by its label, a folder on an SFTP, FTP or WebDAV server, in an S3 bucket or on a cloud drive, with
/// «Проверить доступ». A destination of a kind of a later version is shown, not changed.
/// </summary>
internal sealed class DestinationDialog : FormDialog
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
    private readonly ChoiceItem _kind;
    private readonly TextItem _host = new(L(Strings.DestinationHost), width: 300);
    private readonly TextItem _port = new(L(Strings.DestinationPort), width: 70) { MaxLength = 5 };
    private readonly RowItem _hostRow;
    private readonly TextItem _endpoint = new(L(Strings.DestinationEndpoint)) { Help = Strings.DestinationEndpointHelp };
    private readonly TextItem _bucket = new(L(Strings.DestinationBucket)) { Help = Strings.DestinationBucketHelp };
    private readonly TextItem _region = new(L(Strings.DestinationRegion), width: 300) { Help = Strings.DestinationRegionHelp };
    private readonly TextItem _path;
    private readonly ComboItem _usbLabel = new(L(Strings.DestinationUsbLabel), [], width: 300) { Help = Strings.DestinationUsbHelp };
    private readonly AccountPicker _account;
    private readonly SpeedItem _speed = new(L(Strings.DestinationSpeed)) { Help = Strings.DestinationSpeedHelp };
    private readonly RowItem _speedRow;
    private readonly NumberItem _part = new(L(Strings.DestinationPartSize), 0, 0, 1_000_000, 160) { Help = Strings.DestinationPartHelp };
    private readonly RowItem _partRow;
    private readonly NoteItem _cloudWarning = new(Strings.DestinationCloudWarning, Tone.Warning);
    private readonly NumberItem _lockDays = new(L(Strings.DestinationLock), 0, 0, 36500, 160) { Help = Strings.DestinationLockHelp };
    private readonly RowItem _lockRow;
    private readonly ChoiceItem _lockMode = new(L(Strings.DestinationLockMode), [Strings.LockModeGovernance, Strings.LockModeCompliance], 0, 460);
    private readonly NumberItem _wormDays = new(L(Strings.DestinationWorm), 0, 0, 36500, 160) { Help = Strings.DestinationWormHelp };
    private readonly RowItem _wormRow;
    private readonly NumberItem _wormHours = new(L(Strings.DestinationWormLock), DefaultWormLockHours, 0, WormSettings.MaxLockHours, 160);
    private readonly RowItem _wormLockRow;
    private readonly InfoItem _key = new("key", string.Empty);
    private readonly ButtonItem _forgetKey;
    private readonly NoteItem _unsupported = new(Strings.DestinationLater, Tone.Warning);
    private readonly ButtonItem _test;
    private readonly NoteItem _testResult = new(string.Empty) { IsVisible = false };
    private string? _fingerprint;
    private string _fingerprintOf = string.Empty;
    private string _shownKind;
    private bool _filling = true;

    /// <param name="title">The title when the dialog is used elsewhere than in the task editor (restore).</param>
    public DestinationDialog(EditorContext context, TaskDestination destination, bool isNew, string? title = null)
        : base(title ?? (isNew ? Strings.DestinationAddTitle : Strings.DestinationEditTitle), 720, sizable: true)
    {
        _context = context;
        _original = destination;
        _supported = DestinationKinds.IsSupported(destination.Kind);
        if (CustosEdition.IsLegacy && DestinationKinds.IsCloudDrive(destination.Kind))
        {
            _unsupported.Text = Strings.DestinationNotInLegacy;
        }

        var kinds = Kinds.Select(DestinationsPage.KindText).ToList();
        if (!_supported)
        {
            // A kind of a later version, set from the server: shown, not editable here.
            kinds.Add(DestinationsPage.KindText(destination.Kind));
        }

        _kind = new ChoiceItem(L(Strings.DestinationKind), kinds, 0, 300)
        {
            Help = CustosEdition.IsLinux ? DesktopStrings.LinuxDestinationKindHelp : Strings.DestinationKindHelp,
        };
        _hostRow = new RowItem(_host, _port);
        _path = new TextItem(L(Strings.DestinationFolder)) { ActionIcon = "folder_open", ActionTip = Strings.ButtonBrowse, Action = BrowseAsync };
        _account = new AccountPicker(context, () => this, L(Strings.DestinationAccount));
        _speedRow = new RowItem(_speed, new LabelItem(Strings.RemoteSpeedUnit));
        _partRow = new RowItem(_part, new LabelItem(Strings.DestinationPartUnit));
        _lockRow = new RowItem(_lockDays, new LabelItem(Strings.DestinationLockUnit));
        _wormRow = new RowItem(_wormDays, new LabelItem(Strings.DestinationWormUnit));
        _wormLockRow = new RowItem(_wormHours, new LabelItem(Strings.DestinationWormLockUnit));
        _forgetKey = ButtonItem.Of("key_off", Strings.ButtonForgetKey, () =>
        {
            _fingerprint = null;
            ShowKey();
        }, ButtonLook.Text);
        _test = new ButtonItem("network_check", Strings.ButtonTestAccess, TestAccessAsync) { Failed = ex => _testResult.Show(ex.Message, Tone.Error) };

        Items.Add(_kind);
        Items.Add(_hostRow);
        Items.Add(_endpoint);
        Items.Add(_bucket);
        Items.Add(_region);
        Items.Add(_path);
        Items.Add(_usbLabel);
        Items.Add(_account.Row);
        Items.Add(_speedRow);
        Items.Add(_partRow);
        Items.Add(_cloudWarning);
        Items.Add(_lockRow);
        Items.Add(_lockMode);
        Items.Add(_wormRow);
        Items.Add(_wormLockRow);
        Items.Add(_key);
        Items.Add(_forgetKey);
        Items.Add(_unsupported);
        Items.Add(new NoteItem(CustosEdition.IsLinux ? DesktopStrings.LinuxPathVariables : Strings.PathVariablesNote));
        Items.Add(_test);
        Items.Add(_testResult);

        _usbLabel.Options = UsbLabels();
        if (CustosEdition.IsLinux)
        {
            _usbLabel.Help = DesktopStrings.LinuxUsbLabelHelp;
        }
        int index = Array.IndexOf(Kinds, destination.Kind);
        _kind.Load(index >= 0 ? index : Kinds.Length);
        _shownKind = Kind;
        _path.Load(destination.Path);
        _usbLabel.Load(destination.UsbLabel);
        _host.Load(destination.Host);
        _port.Load(destination.Port is { } port ? port.ToString(CultureInfo.InvariantCulture) : string.Empty);
        _endpoint.Load(destination.Endpoint);
        _bucket.Load(destination.Bucket);
        _region.Load(destination.Region);
        _speed.Kbps = destination.SpeedLimitKbps;
        _part.Value = destination.PartMb;
        _lockDays.Value = destination.ObjectLockDays;
        _lockMode.Load(destination.ObjectLockMode == ObjectLockModes.Compliance ? 1 : 0);
        _wormDays.Value = destination.WormDays;

        // A new WORM starts with the delay many NAS have; a lower one than the storage has would make the check fail.
        _wormHours.Value = destination.WormDays > 0 ? destination.WormLockHours : DefaultWormLockHours;
        _account.SelectedId = destination.CredentialId;
        _fingerprint = string.IsNullOrWhiteSpace(destination.ServerFingerprint) ? null : destination.ServerFingerprint;

        // Before the fields are shown: showing them looks whether the server is still the one the key belongs to.
        _fingerprintOf = ServerOf();
        _filling = false;
        UpdateFields();

        _kind.OnChange = UpdateFields;
        _path.OnChange = () =>
        {
            _testResult.Show(null);
            if (SourceDialog.IsNetworkPath(_path.Text) && Kind == DestinationKinds.Local)
            {
                _kind.Selected = Array.IndexOf(Kinds, DestinationKinds.Unc);
            }
        };
        _host.OnChange = ServerChanged;
        _port.OnChange = ServerChanged;
        _endpoint.OnChange = ServerChanged;
        _lockDays.OnChange = UpdateLock;
        _wormDays.OnChange = UpdateLock;
    }

    /// <summary>The destination as entered, after OK.</summary>
    public TaskDestination Destination { get; private set; } = new();

    /// <summary>What is wrong with a destination of a supported kind, in words, or null.</summary>
    public static string? Check(TaskDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        string path = destination.Path;
        switch (destination.Kind)
        {
            case DestinationKinds.Usb:
                return string.IsNullOrWhiteSpace(destination.UsbLabel) ? Strings.ErrorUsbLabelRequired
                    : CustosEdition.IsLinux && !LinuxEditorPaths.IsVolumeFolder(path) ? DesktopStrings.LinuxErrorVolumeFolder
                    : null;
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
            case DestinationKinds.Unc when CustosEdition.IsLinux:
                return LinuxEditorPaths.IsShare(path) ? null : Strings.Format(DesktopStrings.LinuxErrorSharePath, path);
            case DestinationKinds.Local when CustosEdition.IsLinux:
                return string.IsNullOrWhiteSpace(path) ? Strings.ErrorDestinationFolderRequired
                    : LinuxEditorPaths.IsLocal(path) ? null : Strings.Format(DesktopStrings.LinuxErrorLocalPath, path);
            default:
                return string.IsNullOrWhiteSpace(path) ? Strings.ErrorDestinationFolderRequired
                    : !PathVariables.LooksAbsolute(path) ? Strings.Format(Strings.ErrorPathNotAbsolute, path)
                    : destination.Kind == DestinationKinds.Unc && !path.StartsWith(@"\\", StringComparison.Ordinal) ? Strings.Format(Strings.ErrorNotUnc, path)
                    : destination.Kind == DestinationKinds.Local && path.StartsWith(@"\\", StringComparison.Ordinal) ? Strings.Format(Strings.ErrorUncNeedsNetwork, path)
                    : null;
        }
    }

    protected override async Task<bool> OkAsync()
    {
        if (!_supported)
        {
            Destination = _original;
            return true;
        }

        if (Invalid() is { } wrong)
        {
            ShowError(wrong);
            return false;
        }

        var destination = Build();
        if (Check(destination) is { } problem)
        {
            ShowError(problem);
            return false;
        }

        if (destination.Kind == DestinationKinds.Sftp && destination.ServerFingerprint is null
            && !await Dialogs.Ask(this, Strings.DestinationKeyAsk, Strings.Yes, Strings.No))
        {
            return false;
        }

        Destination = destination;
        return true;
    }

    private static string L(string label) => label.TrimEnd(':', ' ');

    private string Kind => _kind.Selected >= 0 && _kind.Selected < Kinds.Length ? Kinds[_kind.Selected] : _original.Kind;

    /// <summary>A number field of the kind shown that holds no number in its range, in words.</summary>
    private string? Invalid()
    {
        foreach (var number in new NumberItem[] { _part, _lockDays, _wormDays, _wormHours })
        {
            if (Visible(number) && !number.IsValid)
            {
                number.Error = number.Range();
                return $"«{number.Label}»: {number.Range()}";
            }
        }

        if (_speedRow.IsVisible && !_speed.IsValid)
        {
            _speed.Error = _speed.Range();
            return $"«{_speed.Label}»: {_speed.Range()}";
        }

        return null;

        bool Visible(NumberItem number) =>
            number == _part ? _partRow.IsVisible
            : number == _lockDays ? _lockRow.IsVisible
            : number == _wormDays ? _wormRow.IsVisible
            : _wormLockRow.IsVisible;
    }

    /// <summary>The destination as the fields now say; what the dialog does not show is kept as it was.</summary>
    internal TaskDestination Build()
    {
        string kind = Kind;
        bool server = kind is DestinationKinds.Sftp or DestinationKinds.Ftp or DestinationKinds.Ftps;
        bool s3 = kind == DestinationKinds.S3;
        bool cloud = DestinationKinds.IsCloudDrive(kind);
        string path = _path.Value;
        if (CustosEdition.IsLinux && kind == DestinationKinds.Unc)
        {
            path = LinuxEditorPaths.NormalizeShare(path);
        }
        int lockDays = s3 ? _lockDays.Value : 0;
        int wormDays = DestinationKinds.IsFileSystem(kind) ? _wormDays.Value : 0;
        string port = _port.Text.Trim();
        return new TaskDestination
        {
            Id = _original.Id,
            Kind = kind,
            Path = kind == DestinationKinds.Usb && !CustosEdition.IsLinux ? DestinationKinds.UsbFolder(path) : path,
            UsbLabel = kind == DestinationKinds.Usb ? _usbLabel.Value : null,
            CredentialId = DestinationKinds.CredentialKind(kind) is null ? null : _account.SelectedId,
            Host = server || kind == DestinationKinds.WebDav ? _host.Value : null,

            // The address of WebDAV holds its port; one that came with the task from the server is kept.
            Port = !server ? (kind == DestinationKinds.WebDav && _original.Kind == DestinationKinds.WebDav ? _original.Port : null)
                : port.Length == 0 ? null
                : int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : 0,
            Endpoint = s3 && _endpoint.Value.Length > 0 ? _endpoint.Value : null,
            Bucket = s3 ? _bucket.Value : null,
            Region = s3 && _region.Value.Length > 0 ? _region.Value : null,
            DisconnectAfter = _original.DisconnectAfter,
            SpeedLimitKbps = DestinationKinds.CredentialKind(kind) is null ? 0 : _speed.Kbps,
            ObjectLockDays = lockDays,
            ObjectLockMode = lockDays > 0 ? LockModes[Math.Max(0, _lockMode.Selected)] : null,
            WormDays = wormDays,
            WormLockHours = wormDays > 0 ? _wormHours.Value : 0,
            ServerFingerprint = DestinationKinds.IsFileSystem(kind) || cloud ? null : _fingerprint,
            PartMb = cloud ? _part.Value : 0,
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

        _host.Label = L(dav ? Strings.DestinationAddress : Strings.DestinationHost);
        _host.Help = dav ? Strings.DestinationAddressHelp : Strings.DestinationHostHelp;
        _hostRow.IsVisible = server || dav;
        _port.IsVisible = server;
        _port.Placeholder = kind == DestinationKinds.Sftp ? "22" : "21";
        _endpoint.IsVisible = _bucket.IsVisible = _region.IsVisible = s3;

        _path.Label = L(usb ? Strings.DestinationFolderOnDisk
            : server ? Strings.DestinationFolderOnServer
            : s3 ? Strings.DestinationFolderInBucket
            : cloud ? Strings.Format(Strings.DestinationFolderInCloud, CloudNames.AppFolder(kind))
            : Strings.DestinationFolder);
        _path.Help = CustosEdition.IsLinux && usb ? DesktopStrings.LinuxUsbFolderHelp
            : files ? null
            : s3 ? Strings.DestinationFolderBucketHelp
            : dav ? Strings.DestinationFolderWebDavHelp
            : cloud ? Strings.DestinationFolderCloudHelp
            : Strings.DestinationFolderServerHelp;
        _usbLabel.IsVisible = usb;

        _account.Row.IsVisible = accountKind is not null;
        if (accountKind is not null)
        {
            _account.SetKind(accountKind, accountKind switch
            {
                CredentialKinds.Windows => CustosEdition.IsLinux ? Strings.AccountNone : Strings.AccountService,
                CredentialKinds.Ftp or CredentialKinds.WebDav => Strings.AccountNone,
                _ => null,
            });
            _account.Choice.Help = accountKind switch
            {
                CredentialKinds.Windows when CustosEdition.IsLinux => DesktopStrings.LinuxSmbAccountHelp,
                CredentialKinds.Sftp => Strings.DestinationAccountHelpSftp,
                CredentialKinds.Ftp => Strings.DestinationAccountHelpFtp,
                CredentialKinds.WebDav => Strings.DestinationAccountHelpWebDav,
                CredentialKinds.S3 => Strings.DestinationAccountHelpS3,
                _ when cloud => Strings.DestinationAccountHelpCloud,
                _ => Strings.DestinationAccountHelp,
            };
        }

        _speedRow.IsVisible = accountKind is not null;
        _lockRow.IsVisible = s3;
        _wormRow.IsVisible = DestinationKinds.IsFileSystem(kind) && _supported;
        _partRow.IsVisible = _cloudWarning.IsVisible = cloud && _supported;
        if (kind == DestinationKinds.YaDisk && _shownKind != DestinationKinds.YaDisk && _original.Kind != DestinationKinds.YaDisk && _part.Value == 0)
        {
            // Yandex Disk without a subscription takes files of up to 1 GB (section 6.3).
            _part.Value = YandexPartMb;
        }

        _shownKind = kind;
        _unsupported.IsVisible = !_supported;
        _kind.IsEnabled = _path.IsEnabled = _test.IsEnabled = _supported;
        _testResult.Show(null);
        UpdateLock();
        ServerChanged();
    }

    private void UpdateLock()
    {
        _lockMode.IsVisible = Kind == DestinationKinds.S3 && _lockDays.IsValid && _lockDays.Value > 0;
        _wormLockRow.IsVisible = DestinationKinds.IsFileSystem(Kind) && _supported && _wormDays.IsValid && _wormDays.Value > 0;
        _testResult.Show(null);
    }

    /// <summary>What the trusted key belongs to: the kind of the destination and its server.</summary>
    private string ServerOf() => Kind == DestinationKinds.S3
        ? Kind + "|" + _endpoint.Value
        : Kind + "|" + _host.Value + "|" + _port.Text.Trim();

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

        _testResult.Show(null);
        ShowKey();
    }

    /// <summary>The key of an SFTP server is always shown; a certificate only once one was confirmed here.</summary>
    private void ShowKey()
    {
        string kind = Kind;
        bool shown = _supported && (kind == DestinationKinds.Sftp || (_fingerprint is not null && !DestinationKinds.IsFileSystem(kind)));
        _key.IsVisible = shown;
        _forgetKey.IsVisible = shown && _fingerprint is not null;
        _key.Title = L(Strings.DestinationServerKey);
        _key.Detail = _fingerprint ?? Strings.DestinationServerKeyNone;
    }

    private async Task BrowseAsync()
    {
        if (await PickFolderAsync(this, Strings.DestinationFolder.TrimEnd(':'), _path.Value) is { } path)
        {
            _path.Text = path;
        }
    }

    /// <summary>Labels of the disks connected now, for the USB label list.</summary>
    private static List<string> UsbLabels()
    {
        if (CustosEdition.IsLinux)
        {
            return [];
        }

        var labels = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType is DriveType.Removable or DriveType.Fixed && !string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    && !labels.Contains(drive.VolumeLabel))
                {
                    labels.Add(drive.VolumeLabel);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The disk went away while we looked.
            }
        }

        return labels;
    }

    private async Task TestAccessAsync()
    {
        if (Invalid() is { } wrong)
        {
            _testResult.Show(wrong, Tone.Error);
            return;
        }

        if (Check(Build()) is { } problem)
        {
            _testResult.Show(problem, Tone.Error);
            return;
        }

        _testResult.Show(Strings.TestRunning, Tone.Plain);
        try
        {
            var result = await Ask();
            if (result.TrustNeeded && result.Fingerprint is { } seen && await ConfirmKeyAsync(result, seen))
            {
                // The administrator trusts what the server showed: once more, now with it.
                _fingerprint = seen;
                _fingerprintOf = ServerOf();
                ShowKey();
                _testResult.Show(Strings.TestRunning, Tone.Plain);
                result = await Ask();
            }

            ShowTest(result);
        }
        catch (ServiceUnavailableException ex)
        {
            _testResult.Show(ex.Message, Tone.Error);
        }

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
        string text = !result.Ok ? Strings.Format(Strings.TestFailed, result.Error)
            : result.FreeBytes is { } free ? Strings.Format(Strings.TestOk, result.Folder, Custos.Core.Text.Sizes.Format(free))
            : DestinationKinds.IsFileSystem(Kind) ? Strings.Format(Strings.TestOk, result.Folder, "?")
            : Strings.Format(Strings.TestOkRemote, result.Folder);
        _testResult.Show(result.Details.Count == 0 ? text : text + Environment.NewLine + string.Join(Environment.NewLine, result.Details),
            result.Ok ? Tone.Ok : Tone.Error);
    }

    /// <summary>Shows what the server showed and asks whether to trust it; a changed key is a warning.</summary>
    private Task<bool> ConfirmKeyAsync(DestinationTestResult result, string seen)
    {
        string server = Kind == DestinationKinds.S3 ? _endpoint.Value : _host.Value;
        string text = result.FingerprintChanged
            ? Strings.Format(Strings.DestinationKeyChanged, result.Error)
            : Strings.Format(Strings.DestinationKeyConfirm, server, seen);
        return Dialogs.Ask(this, text, Strings.Yes, Strings.No);
    }
}

/// <summary>
/// A speed limit typed in MB/s, to a tenth, and kept in KB/s (1 MB = 1024 KB) as the task stores it. A limit that was
/// not changed comes back exactly as it was loaded, so that 500 KB/s does not turn into 512 (SpeedBox of Custos.exe).
/// </summary>
internal sealed class SpeedItem : FieldBase
{
    /// <summary>The highest limit the task takes, in KB/s.</summary>
    public const int MaxKbps = 10_000_000;

    private readonly NumberItem _number;
    private int _kbps;

    public SpeedItem(string? label)
        : base(label, 160)
    {
        _number = new NumberItem(label, 0, 0, Math.Floor(MaxKbps / 102.4m) / 10, 160, decimals: 1);
        _number.PropertyChanged += (_, e) => Changed(e.PropertyName);
    }

    public string Text
    {
        get => _number.Text;
        set
        {
            _number.Text = value;
            Error = _number.Error;
            OnChange?.Invoke();
        }
    }

    public bool IsValid => _number.IsValid;

    public string Range() => _number.Range();

    /// <summary>The limit in KB/s; 0 = none.</summary>
    public int Kbps
    {
        get => _number.Number == Shown(_kbps) ? _kbps : (int)Math.Min(MaxKbps, Math.Round(_number.Number * 1024));
        set
        {
            _kbps = Math.Clamp(value, 0, MaxKbps);
            _number.Number = Shown(_kbps);
        }
    }

    /// <summary>KB/s as MB/s to a tenth; a limit under 0.1 MB/s shows as 0.1, not as «no limit».</summary>
    private decimal Shown(int kbps) => kbps <= 0 ? 0 : Math.Clamp(Math.Round(kbps / 1024m, 1), 0.1m, _number.Max);
}

/// <summary>
/// Adds or changes one event (section 6.9): a program or script with its arguments, or a Windows service to stop or
/// start, each with a time limit.
/// </summary>
internal sealed class EventDialog : FormDialog
{
    private readonly TaskEvent _original;
    private readonly RadioGroup _kinds = new();
    private readonly RadioItem _run;
    private readonly RadioItem _stop;
    private readonly RadioItem _start;
    private readonly TextItem _program;
    private readonly TextItem _arguments = new(L(Strings.EventArguments));
    private readonly ComboItem _service = new(L(Strings.EventService), []);
    private readonly NumberItem _timeout = new(L(Strings.EventTimeout), 60, 1, 10080, 200) { Help = Strings.EventTimeoutHelp };
    private readonly int _shownMinutes;

    public EventDialog(TaskEvent item, bool isNew)
        : base(isNew ? Strings.EventAddTitle : Strings.EventEditTitle, 680)
    {
        _original = item;
        _run = new RadioItem(_kinds, Strings.EventKindRun);
        _stop = new RadioItem(_kinds, CustosEdition.IsLinux ? DesktopStrings.LinuxEventUnitStop : Strings.EventKindStop);
        _start = new RadioItem(_kinds, CustosEdition.IsLinux ? DesktopStrings.LinuxEventUnitStart : Strings.EventKindStart);
        _program = new TextItem(L(Strings.EventProgram)) { ActionIcon = "folder_open", ActionTip = Strings.ButtonBrowse, Action = BrowseAsync };
        Items.Add(_run);
        Items.Add(_stop);
        Items.Add(_start);
        Items.Add(_program);
        Items.Add(_arguments);
        Items.Add(_service);
        Items.Add(new RowItem(_timeout, new LabelItem(Strings.Minutes)));
        Items.Add(new NoteItem(CustosEdition.IsLinux ? DesktopStrings.LinuxEventHelp : Strings.EventProgramHelp));

        _service.Options = DesktopPlatform.Current.ServiceNames();
        (item.Kind switch
        {
            TaskEventKinds.ServiceStop => _stop,
            TaskEventKinds.ServiceStart => _start,
            _ => _run,
        }).Load();
        _program.Load(item.Command);
        _arguments.Load(item.Args);
        _service.Load(item.Service);
        _shownMinutes = (int)Math.Clamp((item.TimeoutS + 59) / 60, _timeout.Min, _timeout.Max);
        _timeout.Value = _shownMinutes;
        UpdateFields();
        foreach (var kind in _kinds.Items)
        {
            kind.OnChange = UpdateFields;
        }
    }

    /// <summary>The event as entered, after OK.</summary>
    public TaskEvent Event { get; private set; } = new();

    protected override Task<bool> OkAsync()
    {
        string kind = _stop.On ? TaskEventKinds.ServiceStop : _start.On ? TaskEventKinds.ServiceStart : TaskEventKinds.Run;
        string program = _program.Value;
        string service = _service.Value;
        string? problem = kind == TaskEventKinds.Run
            ? program.Length == 0 ? Strings.ErrorEventProgramRequired : null
            : service.Length == 0 ? Strings.ErrorEventServiceRequired : null;
        if (problem is null && !_timeout.IsValid)
        {
            _timeout.Error = _timeout.Range();
            problem = $"«{_timeout.Label}»: {_timeout.Range()}";
        }

        if (problem is not null)
        {
            ShowError(problem);
            return Task.FromResult(false);
        }

        int minutes = _timeout.Value;
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
        return Task.FromResult(true);
    }

    /// <summary>«Программы|*.exe;*.bat|Все файлы|*.*» as the file dialog of Avalonia takes it.</summary>
    internal static List<FilePickerFileType> Filter(string filter)
    {
        var parts = filter.Split('|');
        var types = new List<FilePickerFileType>();
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            types.Add(new FilePickerFileType(parts[i]) { Patterns = parts[i + 1].Split(';') });
        }

        return types;
    }

    private static string L(string label) => label.TrimEnd(':', ' ');

    private void UpdateFields()
    {
        bool run = _run.On;
        _program.IsVisible = _arguments.IsVisible = run;
        _service.IsVisible = !run;
    }

    private async Task BrowseAsync()
    {
        var filters = CustosEdition.IsLinux ? null : Filter(Strings.EventProgramFilter);
        if (await PickFileAsync(this, L(Strings.EventProgram), _program.Value, filters) is { } path)
        {
            _program.Text = path;
        }
    }
}

/// <summary>
/// Adds or changes an account: a Windows account for network folders, or the account of a server (SFTP, FTP and FTPS,
/// WebDAV, S3). The secret (a password, the secret key of S3, or the private key of SFTP) goes straight to the service,
/// which keeps it encrypted; it is never shown or sent back.
/// </summary>
internal sealed class CredentialDialog : FormDialog
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
    private readonly ChoiceItem _kind;
    private readonly TextItem _user = new(L(Strings.AccountUser));
    private readonly TextItem _domain = new(L(Strings.AccountDomain));
    private readonly NoteItem _domainNote = new(Strings.AccountDomainNote);
    private readonly RadioGroup _ways = new();
    private readonly RadioItem _bySecret;
    private readonly RadioItem _byKey;
    private readonly RowItem _way;
    private readonly TextItem _password = new(L(Strings.AccountPassword)) { Secret = true };
    private readonly TextItem _key;
    private readonly NoteItem _note = new(string.Empty);

    /// <param name="monitor">The connection to the service.</param>
    /// <param name="existing">The account to change, or null to add one.</param>
    /// <param name="kind">The kind of a new account when the place it is added from needs one kind; null lets the user choose.</param>
    public CredentialDialog(ServiceMonitor monitor, Credential? existing, string? kind = null)
        : base(existing is null ? Strings.AccountAddTitle : Strings.Format(Strings.AccountEditTitle, existing.DisplayName), 640)
    {
        _monitor = monitor;
        _existing = existing;
        _fixedKind = existing?.Kind ?? kind;
        var kinds = Kinds.Select(KindText).ToList();
        if (_fixedKind is not null && Array.IndexOf(Kinds, _fixedKind) < 0)
        {
            kinds.Add(KindText(_fixedKind));
        }

        _kind = new ChoiceItem(L(Strings.AccountKind), kinds, 0, 360);
        _bySecret = new RadioItem(_ways, Strings.AccountBySecret, on: true, inline: true);
        _byKey = new RadioItem(_ways, Strings.AccountByKey, inline: true);
        _way = new RowItem(_bySecret, _byKey);
        _key = new TextItem(L(Strings.AccountPrivateKey))
        {
            Multiline = true,
            Mono = true,
            Lines = 6,
            ActionIcon = "file_open",
            ActionTip = Strings.ButtonKeyFile,
            Action = TakeKeyFileAsync,
        };
        Items.Add(_kind);
        Items.Add(_user);
        Items.Add(_domain);
        Items.Add(_domainNote);
        Items.Add(_way);
        Items.Add(_password);
        Items.Add(_key);
        Items.Add(_note);

        int index = Array.IndexOf(Kinds, _fixedKind ?? CredentialKinds.Windows);
        _kind.Load(index >= 0 ? index : Kinds.Length);
        _kind.IsEnabled = _fixedKind is null;
        if (existing is not null)
        {
            _user.Load(existing.User);
            _domain.Load(existing.Domain);
        }

        UpdateFields();
        _kind.OnChange = UpdateFields;
        _bySecret.OnChange = UpdateFields;
    }

    /// <summary>The account as the service stored it, after OK.</summary>
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
    /// A private key as the service keeps it: lines ended by a line feed alone, nothing around it. Null when the text is
    /// not a private key in the OpenSSH or PEM format (a key of PuTTY, a public key, some other file).
    /// </summary>
    public static string? CleanKey(string? text)
    {
        string key = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        return key.StartsWith("-----BEGIN", StringComparison.Ordinal) && key.Contains("PRIVATE KEY", StringComparison.Ordinal) ? key + "\n" : null;
    }

    protected override async Task<bool> OkAsync()
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
            _user.Error = s3 ? Strings.ErrorAccessKeyRequired : Strings.ErrorUserRequired;
            return false;
        }

        string? secret;
        if (ByKey)
        {
            if (string.IsNullOrWhiteSpace(_key.Text))
            {
                if (_existing is null)
                {
                    _key.Error = Strings.ErrorKeyRequired;
                    return false;
                }

                secret = null;
            }
            else if (CleanKey(_key.Text) is { } key)
            {
                secret = key;
            }
            else
            {
                _key.Error = Strings.ErrorKeyBad;
                return false;
            }
        }
        else
        {
            secret = _password.Text.Length > 0 ? _password.Text : null;
            if (_existing is null && secret is null)
            {
                _password.Error = s3 ? Strings.ErrorSecretKeyRequired : Strings.ErrorPasswordRequired;
                return false;
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
        var result = await _monitor.RequestAsync(PipeProtocol.Methods.CredentialSave, new CredentialInput { Credential = credential, Password = secret },
            IpcJsonContext.Default.CredentialInput, IpcJsonContext.Default.CredentialSaveResult);
        Saved = result.Credential;
        _password.Load(null);
        _key.Load(null);
        return true;
    }

    private static string L(string label) => label.TrimEnd(':', ' ');

    private string Kind => _kind.Selected >= 0 && _kind.Selected < Kinds.Length ? Kinds[_kind.Selected] : _fixedKind ?? CredentialKinds.Windows;

    /// <summary>SFTP with a private key in place of a password.</summary>
    private bool ByKey => Kind == CredentialKinds.Sftp && _byKey.On;

    /// <summary>Shows the fields of the chosen kind, and what its secret is called.</summary>
    private void UpdateFields()
    {
        string kind = Kind;
        bool windows = kind == CredentialKinds.Windows;
        bool s3 = kind == CredentialKinds.S3;
        bool key = ByKey;
        _user.Label = L(s3 ? Strings.AccountAccessKey : Strings.AccountUser);
        _password.Label = L(s3 ? Strings.AccountSecretKey : Strings.AccountPassword);
        _domain.IsVisible = _domainNote.IsVisible = windows;
        _way.IsVisible = kind == CredentialKinds.Sftp;
        _password.IsVisible = !key;
        _key.IsVisible = key;
        string kept = key ? Strings.AccountKeyNote : s3 ? Strings.AccountSecretNote : Strings.AccountPasswordNote;
        _note.Text = _existing is null ? kept
            : key ? Strings.AccountKeyNote + "\n" + Strings.AccountKeepSecretNote
            : s3 ? Strings.AccountKeepSecretNote
            : Strings.AccountKeepPasswordNote;
    }

    /// <summary>Reads a private key from its file into the field. The window runs as the user, so it reads what the user may read.</summary>
    private async Task TakeKeyFileAsync()
    {
        string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        if (await PickFileAsync(this, Strings.ButtonKeyFile, home, EventDialog.Filter(Strings.KeyFileFilter)) is not { } file)
        {
            return;
        }

        string text;
        try
        {
            if (new FileInfo(file).Length > LargestKeyFile)
            {
                _key.Error = Strings.ErrorKeyBad;
                return;
            }

            text = await File.ReadAllTextAsync(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _key.Error = Strings.Format(Strings.ErrorKeyFile, file, ex.Message);
            return;
        }

        if (CleanKey(text) is not { } clean)
        {
            _key.Error = Strings.ErrorKeyBad;
            return;
        }

        _key.Text = clean;
    }
}

/// <summary>
/// Sets the archive password of a task (section 6.5). It is stored by the service like an account password (kind
/// <c>archive_password</c>) and never shown again.
/// </summary>
internal sealed class ArchivePasswordDialog : FormDialog
{
    private readonly ServiceMonitor _monitor;
    private readonly string? _existingId;
    private readonly string _taskName;
    private readonly TextItem _password = new(Strings.AccountPassword.TrimEnd(':', ' ')) { Secret = true };
    private readonly TextItem _repeat = new(Strings.ArchivePasswordRepeat.TrimEnd(':', ' ')) { Secret = true };

    public ArchivePasswordDialog(ServiceMonitor monitor, string? existingId, string taskName)
        : base(Strings.ArchivePasswordTitle, 560)
    {
        _monitor = monitor;
        _existingId = existingId;
        _taskName = taskName;
        Items.Add(_password);
        Items.Add(_repeat);
        Items.Add(new NoteItem(existingId is null ? Strings.ArchivePasswordWarning : Strings.ArchivePasswordChangeNote, Tone.Warning));

        // A password one can see needs no second typing.
        _password.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TextItem.Reveal))
            {
                _repeat.IsVisible = !_password.Reveal;
            }
        };
    }

    /// <summary>The id of the stored password, after OK.</summary>
    public string? SavedId { get; private set; }

    protected override async Task<bool> OkAsync()
    {
        if (_password.Text.Length == 0)
        {
            _password.Error = Strings.ErrorPasswordRequired;
            return false;
        }

        if (!_password.Reveal && !string.Equals(_password.Text, _repeat.Text, StringComparison.Ordinal))
        {
            _repeat.Error = Strings.ArchivePasswordMismatch;
            return false;
        }

        var credential = new Credential
        {
            Id = _existingId ?? TaskDefinition.NewId(),
            Kind = CredentialKinds.ArchivePassword,
            User = Strings.Format(Strings.ArchivePasswordName, _taskName.Length == 0 ? Strings.ArchivePasswordNewTask : _taskName),
        };
        var result = await _monitor.RequestAsync(PipeProtocol.Methods.CredentialSave, new CredentialInput { Credential = credential, Password = _password.Text },
            IpcJsonContext.Default.CredentialInput, IpcJsonContext.Default.CredentialSaveResult);
        SavedId = result.Credential.Id;
        _password.Load(null);
        _repeat.Load(null);
        return true;
    }
}
