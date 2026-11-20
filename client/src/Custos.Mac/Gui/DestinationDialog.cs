using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Custos.Core.Destinations;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Text;
using Custos.Gui;

namespace Custos.Mac.Gui;

/// <summary>
/// Adds or changes one destination (section 6.3): a folder of this Mac or of an external disk, a network folder, a
/// server (SFTP, FTP, FTPS, WebDAV) or an S3 storage. A cloud drive is connected in CUSTOS for Windows or on the
/// site and is kept here as it is.
/// </summary>
internal sealed class DestinationDialog
{
    private const int DefaultWormLockHours = 3;

    private static readonly string[] Kinds =
    [
        DestinationKinds.Local, DestinationKinds.Unc, DestinationKinds.Usb, DestinationKinds.Sftp, DestinationKinds.Ftp, DestinationKinds.Ftps,
        DestinationKinds.WebDav, DestinationKinds.S3,
    ];

    private readonly EditorContext _context;
    private readonly TaskDestination _original;
    private readonly DialogWindow _dialog;
    private readonly ComboBox _kind;
    private readonly TextBox _path;
    private readonly TextBox _usbLabel;
    private readonly TextBox _host;
    private readonly TextBox _address;
    private readonly TextBox _port;
    private readonly TextBox _endpoint;
    private readonly TextBox _bucket;
    private readonly TextBox _region;
    private readonly NumericUpDown _speed;
    private readonly NumericUpDown _lockDays;
    private readonly ComboBox _lockMode;
    private readonly NumericUpDown _wormDays;
    private readonly NumericUpDown _wormHours;
    private readonly ContentControl _accountHolder = new();
    private readonly TextBlock _testResult = new() { TextWrapping = TextWrapping.Wrap, Classes = { "small" } };
    private readonly TextBlock _error = Ui.Error();
    private readonly Dictionary<string, Control> _rows = [];
    private readonly TextBlock _pathLabel = Ui.Label(Strings.DestinationFolder);
    private readonly Control _pathHelp = Ui.Help(string.Empty);
    private AccountPicker? _account;
    private string? _accountKind;
    private string? _fingerprint;

    private DestinationDialog(EditorContext context, TaskDestination original)
    {
        _context = context;
        _original = original;
        _fingerprint = string.IsNullOrWhiteSpace(original.ServerFingerprint) ? null : original.ServerFingerprint;
        _dialog = new DialogWindow(string.IsNullOrEmpty(original.Path) && original.Host is null && original.Bucket is null
            ? Strings.DestinationAddTitle : Strings.DestinationEditTitle, 720);
        _kind = Ui.Choice(Kinds.Select(k => (k, KindTitle(k))), Kinds.Contains(original.Kind) ? original.Kind : DestinationKinds.Local, 320);
        _path = Ui.TextBox(original.Kind == DestinationKinds.Usb ? original.Path : original.Path);
        _usbLabel = Ui.TextBox(original.UsbLabel, "BACKUP");
        _host = Ui.TextBox(original.Host, "backup.example.org");
        _address = Ui.TextBox(original.Kind == DestinationKinds.WebDav ? original.Host : null, "https://cloud.example.org/remote.php/dav/files/backup");
        _port = Ui.TextBox(original.Port?.ToString(CultureInfo.InvariantCulture), null, 90);
        _endpoint = Ui.TextBox(original.Endpoint, "https://s3.example.org");
        _bucket = Ui.TextBox(original.Bucket);
        _region = Ui.TextBox(original.Region, "eu-central-1", 200);
        _speed = new NumericUpDown { Minimum = 0, Maximum = 9765, Increment = 1, FormatString = "0.#", Width = 130, HorizontalAlignment = HorizontalAlignment.Left,
            Value = original.SpeedLimitKbps <= 0 ? 0 : Math.Max(0.1m, Math.Round(original.SpeedLimitKbps / 1024m, 1)) };
        _lockDays = Ui.Number(original.ObjectLockDays, 0, 36500);
        _lockMode = Ui.Choice([(ObjectLockModes.Governance, Strings.LockModeGovernance), (ObjectLockModes.Compliance, Strings.LockModeCompliance)],
            original.ObjectLockMode ?? ObjectLockModes.Governance, 420);
        _wormDays = Ui.Number(original.WormDays, 0, 36500);
        _wormHours = Ui.Number(original.WormDays > 0 ? original.WormLockHours : DefaultWormLockHours, 0, WormSettings.MaxLockHours);

        var browse = Ui.Text(Strings.ButtonBrowse, () => { });
        browse.OnClick(async () =>
        {
            if (await _context.PickFolderAsync(Strings.DestinationFolder) is { } folder)
            {
                _path.Text = Kind == DestinationKinds.Usb ? UsbRelative(folder) : folder;
            }
        });
        var test = Ui.Tonal("network_check", Strings.ButtonTestAccess, () => { });
        test.OnClick(() => TestAsync(test));

        // The path fills the line, «Обзор…» and the (?) mark of the kind after it, as the rows of Ui.Row.
        var pathRow = new Grid { ColumnDefinitions = new ColumnDefinitions("190,*"), Margin = new Avalonia.Thickness(0, 2) };
        _pathLabel.VerticalAlignment = VerticalAlignment.Top;
        _pathLabel.Margin = new Avalonia.Thickness(0, 5, Ui.Gap, 0);
        _pathLabel.TextWrapping = TextWrapping.Wrap;
        var pathRight = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Children = { _path, browse, _pathHelp } };
        browse.Margin = new Avalonia.Thickness(4, 0, 0, 0);
        Grid.SetColumn(browse, 1);
        Grid.SetColumn(_pathHelp, 2);
        Grid.SetColumn(pathRight, 1);
        pathRow.Children.Add(_pathLabel);
        pathRow.Children.Add(pathRight);

        _rows["browse"] = browse;
        _rows["usb"] = Ui.Row(Strings.DestinationUsbLabel, _usbLabel, MacStrings.DestinationUsbHelp);
        _rows["host"] = Ui.Row(Strings.DestinationHost, Ui.Horizontal(_host, new TextBlock { Text = Strings.DestinationPort }, _port), Strings.DestinationHostHelp);
        _rows["address"] = Ui.Row(Strings.DestinationAddress, _address, Strings.DestinationAddressHelp);
        _rows["endpoint"] = Ui.Row(Strings.DestinationEndpoint, _endpoint, Strings.DestinationEndpointHelp);
        _rows["bucket"] = Ui.Row(Strings.DestinationBucket, _bucket, Strings.DestinationBucketHelp);
        _rows["region"] = Ui.Row(Strings.DestinationRegion, _region, Strings.DestinationRegionHelp);
        _rows["path"] = pathRow;
        _rows["account"] = Ui.Row(Strings.DestinationAccount, _accountHolder);
        _rows["speed"] = Ui.Row(Strings.DestinationSpeed, Ui.Horizontal(_speed, new TextBlock { Text = MacStrings.SpeedUnit }), Strings.DestinationSpeedHelp);
        _rows["lock"] = Ui.Row(Strings.DestinationLock, Ui.Horizontal(_lockDays, new TextBlock { Text = Strings.DestinationLockUnit }), Strings.DestinationLockHelp);
        _rows["lockmode"] = Ui.Row(Strings.DestinationLockMode, _lockMode);
        _rows["worm"] = Ui.Row(Strings.DestinationWorm, Ui.Horizontal(_wormDays, new TextBlock { Text = Strings.DestinationWormUnit }), Strings.DestinationWormHelp);
        _rows["wormlock"] = Ui.Row(Strings.DestinationWormLock, Ui.Horizontal(_wormHours, new TextBlock { Text = Strings.DestinationWormLockUnit }));

        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(Ui.Row(Strings.DestinationKind, _kind));
        foreach (string key in new[] { "host", "address", "endpoint", "bucket", "region", "usb", "path", "account", "speed", "lock", "lockmode", "worm", "wormlock" })
        {
            body.Children.Add(_rows[key]);
        }

        body.Children.Add(Ui.Horizontal(test));
        body.Children.Add(_testResult);
        body.Children.Add(_error);
        _dialog.Body.Children.Add(new ScrollViewer { Content = body, MaxHeight = 620 });
        _dialog.AddButton(Strings.ButtonCancel, () => _dialog.Close(null));
        var ok = _dialog.AddButton(Strings.ButtonOk, () => { }, main: true);
        ok.OnClick(OkAsync);
        _kind.SelectionChanged += (_, _) => ShowRows();
        _lockDays.ValueChanged += (_, _) => ShowRows();
        _wormDays.ValueChanged += (_, _) => ShowRows();
        ShowRows();
    }

    private string Kind => Ui.Read(_kind);

    public static string KindTitle(string kind) => kind switch
    {
        DestinationKinds.Local => MacStrings.KindLocal,
        DestinationKinds.Unc => MacStrings.KindUnc,
        DestinationKinds.Usb => MacStrings.KindUsb,
        DestinationKinds.Sftp => Strings.KindSftp,
        DestinationKinds.Ftp => Strings.KindFtp,
        DestinationKinds.Ftps => Strings.KindFtps,
        DestinationKinds.WebDav => Strings.KindWebDav,
        DestinationKinds.S3 => Strings.KindS3,
        DestinationKinds.GDrive => Strings.KindGDrive,
        DestinationKinds.YaDisk => Strings.KindYaDisk,
        DestinationKinds.Dropbox => Strings.KindDropbox,
        _ => kind,
    };

    /// <summary>The changed destination, or null when the dialog was cancelled.</summary>
    public static async Task<TaskDestination?> Show(EditorContext context, TaskDestination destination)
    {
        if (DestinationKinds.IsCloudDrive(destination.Kind))
        {
            await Dialogs.Show(context.Window, Strings.Format(MacStrings.CloudKept, KindTitle(destination.Kind)));
            return null;
        }

        var dialog = new DestinationDialog(context, destination);
        return await dialog._dialog.ShowDialog<TaskDestination?>(context.Window);
    }

    /// <summary>What is wrong with the destination, in words, or null.</summary>
    private static string? Check(TaskDestination destination) => destination.Kind switch
    {
        DestinationKinds.Usb => string.IsNullOrWhiteSpace(destination.UsbLabel) ? Strings.ErrorUsbLabelRequired : null,
        DestinationKinds.Sftp or DestinationKinds.Ftp or DestinationKinds.Ftps =>
            string.IsNullOrWhiteSpace(destination.Host) ? Strings.ErrorHostRequired
            : destination.Port is < 1 or > 65535 ? Strings.ErrorPortBad
            : destination.Kind == DestinationKinds.Sftp && string.IsNullOrEmpty(destination.CredentialId) ? Strings.ErrorAccountRequired
            : null,
        DestinationKinds.WebDav => DestinationStores.Problem(destination) is null ? null : Strings.ErrorAddressRequired,
        DestinationKinds.S3 => string.IsNullOrWhiteSpace(destination.Bucket) ? Strings.ErrorBucketRequired
            : string.IsNullOrEmpty(destination.CredentialId) ? Strings.ErrorAccountRequired
            : null,
        DestinationKinds.Unc => SourcesPage.PathProblem(destination.Path, network: true),
        _ => string.IsNullOrWhiteSpace(destination.Path) ? Strings.ErrorDestinationFolderRequired : SourcesPage.PathProblem(destination.Path, network: false),
    };

    /// <summary>A folder picked on an external disk: the part after /Volumes/NAME, or the whole path.</summary>
    private string UsbRelative(string folder)
    {
        string[] parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0] == "Volumes")
        {
            _usbLabel.Text = parts[1];
            return string.Join('/', parts.Skip(2));
        }

        return folder;
    }

    private TaskDestination Build()
    {
        string kind = Kind;
        bool server = kind is DestinationKinds.Sftp or DestinationKinds.Ftp or DestinationKinds.Ftps;
        bool s3 = kind == DestinationKinds.S3;
        string path = (_path.Text ?? string.Empty).Trim().Trim('"');
        if (kind == DestinationKinds.Unc)
        {
            path = path.Replace('\\', '/');
        }

        int lockDays = s3 ? Ui.Read(_lockDays) : 0;
        int wormDays = DestinationKinds.IsFileSystem(kind) ? Ui.Read(_wormDays) : 0;
        string portText = (_port.Text ?? string.Empty).Trim();
        return new TaskDestination
        {
            Id = _original.Id,
            Kind = kind,
            Path = kind == DestinationKinds.Usb ? DestinationKinds.UsbFolder(path) : path,
            UsbLabel = kind == DestinationKinds.Usb ? (_usbLabel.Text ?? string.Empty).Trim() : null,
            CredentialId = DestinationKinds.CredentialKind(kind) is null ? null : _account?.SelectedId,
            Host = kind == DestinationKinds.WebDav ? (_address.Text ?? string.Empty).Trim() : server ? (_host.Text ?? string.Empty).Trim() : null,
            Port = !server ? (kind == DestinationKinds.WebDav && _original.Kind == DestinationKinds.WebDav ? _original.Port : null)
                : portText.Length == 0 ? null
                : int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port) ? port : 0,
            Endpoint = s3 && !string.IsNullOrWhiteSpace(_endpoint.Text) ? _endpoint.Text.Trim() : null,
            Bucket = s3 ? (_bucket.Text ?? string.Empty).Trim() : null,
            Region = s3 && !string.IsNullOrWhiteSpace(_region.Text) ? _region.Text.Trim() : null,
            DisconnectAfter = _original.DisconnectAfter,
            SpeedLimitKbps = DestinationKinds.CredentialKind(kind) is null || kind == DestinationKinds.Unc ? 0 : (int)Math.Round((_speed.Value ?? 0) * 1024),
            ObjectLockDays = lockDays,
            ObjectLockMode = lockDays > 0 ? Ui.Read(_lockMode) : null,
            WormDays = wormDays,
            WormLockHours = wormDays > 0 ? Ui.Read(_wormHours) : 0,
            ServerFingerprint = DestinationKinds.IsFileSystem(kind) ? null : _fingerprint,
            PartMb = _original.PartMb,
            ExtensionData = _original.ExtensionData,
        };
    }

    private void ShowRows()
    {
        string kind = Kind;
        bool server = kind is DestinationKinds.Sftp or DestinationKinds.Ftp or DestinationKinds.Ftps;
        bool fileSystem = DestinationKinds.IsFileSystem(kind);
        _rows["host"].IsVisible = server;
        _rows["address"].IsVisible = kind == DestinationKinds.WebDav;
        _rows["endpoint"].IsVisible = _rows["bucket"].IsVisible = _rows["region"].IsVisible = kind == DestinationKinds.S3;
        _rows["usb"].IsVisible = kind == DestinationKinds.Usb;
        _rows["browse"].IsVisible = kind is DestinationKinds.Local or DestinationKinds.Usb;
        _rows["speed"].IsVisible = !fileSystem;
        _rows["lock"].IsVisible = kind == DestinationKinds.S3;
        _rows["lockmode"].IsVisible = kind == DestinationKinds.S3 && Ui.Read(_lockDays) > 0;
        _rows["worm"].IsVisible = _rows["wormlock"].IsVisible = fileSystem;
        _wormHours.IsEnabled = Ui.Read(_wormDays) > 0;
        (string pathLabel, string pathHelp) = kind switch
        {
            DestinationKinds.Unc => (Strings.SourcePath, MacStrings.NetworkPathNote),
            DestinationKinds.Usb => (Strings.DestinationFolderOnDisk, MacStrings.DestinationUsbFolderHelp),
            DestinationKinds.Sftp or DestinationKinds.Ftp or DestinationKinds.Ftps => (Strings.DestinationFolderOnServer, Strings.DestinationFolderServerHelp),
            DestinationKinds.WebDav => (Strings.DestinationFolder, Strings.DestinationFolderWebDavHelp),
            DestinationKinds.S3 => (Strings.DestinationFolderInBucket, Strings.DestinationFolderBucketHelp),
            _ => (Strings.DestinationFolder, MacStrings.DestinationLocalHelp),
        };
        _pathLabel.Text = pathLabel.TrimEnd(':', ' ');
        ToolTip.SetTip(_pathHelp, pathHelp);
        _pathHelp.IsVisible = pathHelp.Length > 0;

        string? accountKind = DestinationKinds.CredentialKind(kind);
        _rows["account"].IsVisible = accountKind is not null;
        if (accountKind != _accountKind)
        {
            _accountKind = accountKind;
            _account = accountKind is null ? null
                : new AccountPicker(_context, accountKind, _original.Kind == kind ? _original.CredentialId : null,
                    allowNone: kind is DestinationKinds.Unc or DestinationKinds.Ftp or DestinationKinds.Ftps or DestinationKinds.WebDav);
            _accountHolder.Content = _account?.View;
        }
    }

    private async Task TestAsync(Button test)
    {
        var destination = Build();
        if (Check(destination) is { } problem)
        {
            ShowTest(false, problem);
            return;
        }

        test.IsEnabled = false;
        ShowTest(null, Strings.TestRunning);
        try
        {
            var result = await AskAsync(destination);
            if (result.TrustNeeded && result.Fingerprint is { } seen)
            {
                string server = Kind == DestinationKinds.S3 ? (_endpoint.Text ?? string.Empty).Trim() : (_host.Text ?? string.Empty).Trim();
                string text = result.FingerprintChanged ? Strings.Format(Strings.DestinationKeyChanged, result.Error)
                    : Strings.Format(Strings.DestinationKeyConfirm, server, seen);
                if (await Dialogs.Ask(_dialog, text, MacStrings.TrustKey))
                {
                    _fingerprint = seen;
                    ShowTest(null, Strings.TestRunning);
                    result = await AskAsync(Build());
                }
            }

            string message = !result.Ok ? Strings.Format(Strings.TestFailed, result.Error)
                : result.FreeBytes is { } free ? Strings.Format(Strings.TestOk, result.Folder, Sizes.Format(free))
                : DestinationKinds.IsFileSystem(Kind) ? Strings.Format(Strings.TestOk, result.Folder, "?")
                : Strings.Format(Strings.TestOkRemote, result.Folder);
            ShowTest(result.Ok, result.Details.Count == 0 ? message : message + "\n" + string.Join('\n', result.Details));
        }
        catch (ServiceUnavailableException ex)
        {
            ShowTest(false, ex.Message);
        }
        finally
        {
            test.IsEnabled = true;
        }
    }

    private Task<DestinationTestResult> AskAsync(TaskDestination destination) => _context.Monitor.RequestAsync(PipeProtocol.Methods.DestinationTest,
        new DestinationTestRequest { Destination = destination, TaskName = _context.Task.Name, BackupType = _context.BackupType, ProfileSid = _context.Task.ProfileSid },
        IpcJsonContext.Default.DestinationTestRequest, IpcJsonContext.Default.DestinationTestResult, TimeSpan.FromMinutes(3));

    private void ShowTest(bool? ok, string text)
    {
        _testResult.Text = text;
        _testResult.Foreground = ok switch
        {
            true => Ui.Brush("Ok"),
            false => Ui.Brush("Error"),
            null => Ui.Brush("OnSurfaceVariant"),
        };
    }

    private async Task OkAsync()
    {
        var destination = Build();
        if (Check(destination) is { } problem)
        {
            Ui.Show(_error, problem);
            return;
        }

        if (destination.Kind == DestinationKinds.Sftp && destination.ServerFingerprint is null
            && !await Dialogs.Ask(_dialog, Strings.DestinationKeyAsk, Strings.ButtonOk))
        {
            return;
        }

        _dialog.Close(destination);
    }
}
