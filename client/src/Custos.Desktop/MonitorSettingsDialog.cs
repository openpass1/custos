using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Custos.Core.Ipc;
using Custos.Core.Monitor;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>The monitoring settings on a desktop that does not expose the full settings window.</summary>
internal sealed class MonitorSettingsDialog : FormDialog
{
    private readonly ServiceMonitor _monitor;

    public MonitorSettingsDialog(ServiceMonitor monitor) : base(Strings.SettingsServer, 700)
    {
        _monitor = monitor;
        OkButton.IsVisible = false;
        CancelButton.Content = Strings.ButtonClose;
        _ = new MonitorSettingsSection(this, monitor, ShowError, standalone: true);
    }

    public async Task ShowOverAsync(Window owner)
    {
        // The password dialog belongs to the active modal window, not the disabled main window.
        var previous = _monitor.AskPassword;
        _monitor.AskPassword = () => Dialogs.ProgramPassword(this, _monitor);
        try
        {
            await ShowAsync(owner);
        }
        finally
        {
            _monitor.AskPassword = previous;
        }
    }

    protected override Task<bool> OkAsync() => Task.FromResult(false);
}

/// <summary>The service's monitoring status and the two local administrator operations.</summary>
internal sealed class MonitorSettingsSection
{
    // Example values, never the connection line received from the user.
    private const string Example = "http://custos.example.lan#K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ#3F2A-91C0-77DE-0B14";

    private readonly FormDialog _owner;
    private readonly ServiceMonitor _monitor;
    private readonly Action<string?> _showError;
    private readonly bool _standalone;
    private readonly NoteItem _state = new(string.Empty);
    private readonly NoteItem _details = new(string.Empty);
    private readonly NoteItem _administrators = new(Strings.ServerAdminOnly);
    private readonly TextItem _line;
    private readonly ButtonItem _connect;
    private readonly ButtonItem _reset;
    private readonly ButtonItem? _paste;
    private bool _busy;

    public MonitorSettingsSection(FormDialog owner, ServiceMonitor monitor, Action<string?> showError, bool standalone = false)
    {
        _owner = owner;
        _monitor = monitor;
        _showError = showError;
        _standalone = standalone;
        _line = new TextItem(Strings.ServerLineLabel.TrimEnd(':', ' '))
        {
            Placeholder = Example,
            Help = Strings.ServerLineHelp,
            Secret = standalone,
            ActionIcon = "content_paste",
            ActionTip = Strings.ServerPasteTip,
            Action = standalone ? null : PasteAsync,
        };
        var failed = (Exception error) => _showError(error.Message);
        _connect = new ButtonItem("link", Strings.ServerConnectButton, () => ChangeAsync(ConnectAsync)) { Failed = failed };
        _reset = new ButtonItem("link_off", Strings.ServerResetButton, () => ChangeAsync(ResetAsync), ButtonLook.Outlined) { Failed = failed };

        if (!standalone)
        {
            owner.Items.Add(new HeadingItem(Strings.SettingsServer));
        }

        owner.Items.Add(_state);
        owner.Items.Add(_line);
        if (standalone)
        {
            // TextItem has one slot for either its paste button or its password-visibility switch.
            _paste = new ButtonItem("content_paste", Strings.ServerPasteTip, PasteAsync, ButtonLook.Text) { Failed = failed };
            owner.Items.Add(new RowItem(_paste, _connect, _reset));
        }
        else
        {
            owner.Items.Add(new RowItem(_connect, _reset));
        }

        owner.Items.Add(_details);
        owner.Items.Add(_administrators);
        _monitor.StatusChanged += OnChanged;
        _monitor.SnapshotChanged += OnChanged;
        owner.Closing += (_, args) => args.Cancel |= _busy;
        owner.Closed += (_, _) =>
        {
            _monitor.StatusChanged -= OnChanged;
            _monitor.SnapshotChanged -= OnChanged;
            ClearLine();
        };
        Refresh();
    }

    private bool CanChange => !_busy && _monitor.IsConnected && _monitor.Status.Hello?.IsAdmin == true;

    private void OnChanged(object? sender, EventArgs args) => Refresh();

    private void Refresh()
    {
        var server = _monitor.Snapshot?.Server;
        bool canChange = CanChange;
        _line.IsEnabled = _connect.IsEnabled = canChange;
        _reset.IsEnabled = canChange && server?.ServerUrl is not null;
        if (_paste is not null)
        {
            _paste.IsEnabled = canChange;
        }

        _administrators.IsVisible = _monitor.IsConnected && _monitor.Status.Hello?.IsAdmin != true;
        if (server is null)
        {
            _state.Show(Strings.ProblemNoConnection, Tone.Caution);
            _details.Show(null);
            return;
        }

        string status = ServerLink.StateText(server.State);
        if (server.ServerName is { Length: > 0 } name && server.State is ServerLinkStates.Approved or ServerLinkStates.Pending)
        {
            status += " " + Strings.Format(Strings.ServerNamed, name);
        }

        bool normal = server.State is ServerLinkStates.Approved or ServerLinkStates.Off or ServerLinkStates.Pending or ServerLinkStates.Connecting;
        if (!_standalone && server.Detail is { } detail)
        {
            status = status.TrimEnd('.') + ": " + detail;
        }

        _state.Show(status, normal ? Tone.Plain : Tone.Caution);
        var lines = new List<string>();
        if (DisplayUrl(server.ServerUrl) is { } url)
        {
            lines.Add(Strings.Format(Strings.ServerAddress, url));
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

        _details.Show(string.Join(Environment.NewLine, lines));
    }

    private async Task PasteAsync()
    {
        if (!CanChange)
        {
            return;
        }

        string? text = _owner.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
        string? line = text?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(line))
        {
            _line.Error = Strings.ServerPasteEmpty;
            return;
        }

        _line.Reveal = false;
        _line.Text = line;
    }

    private async Task ChangeAsync(Func<Task> change)
    {
        if (!CanChange)
        {
            return;
        }

        _busy = true;
        _showError(null);
        Refresh();
        try
        {
            await change();
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private async Task ConnectAsync()
    {
        string line = _line.Text.Trim();
        if (line.Length == 0)
        {
            _line.Error = Strings.ServerLineRequired;
            return;
        }

        if (!RegistrationCode.TrySplitConnection(line, out string url, out string code)
            || !MonitorClient.IsValidServerUrl(url, out string normalized) || RegistrationCode.TryParse(code, out _) is null
            || (_standalone && new Uri(normalized).UserInfo.Length != 0))
        {
            _line.Error = Strings.Format(Strings.ServerLineBad, Example);
            return;
        }

        string? current = _monitor.Snapshot?.Server.ServerUrl;
        if (_standalone && current is not null && !string.Equals(current, normalized, StringComparison.OrdinalIgnoreCase)
            && !await Dialogs.Ask(_owner, Strings.Format(DesktopStrings.MonitorSwitchConfirm, DisplayUrl(current) ?? "—", DisplayUrl(normalized)),
                Strings.ServerConnectButton, Strings.ButtonCancel))
        {
            return;
        }

        if (!await IsCurrentAsync(current))
        {
            return;
        }

        var result = await _monitor.RequestAsync(PipeProtocol.Methods.ServerConnect, new ServerConnectRequest { ServerUrl = normalized, Code = code },
            IpcJsonContext.Default.ServerConnectRequest, IpcJsonContext.Default.OkResult);
        if (result.Ok)
        {
            ClearLine();
            await Dialogs.Show(_owner, Strings.ServerConnectDone);
        }
        else
        {
            _showError(result.Message ?? Strings.ProblemNoAnswer);
        }
    }

    private async Task ResetAsync()
    {
        var server = _monitor.Snapshot?.Server;
        if (server?.ServerUrl is not { } current
            || !await Dialogs.Ask(_owner, Strings.Format(Strings.ServerResetConfirm, server.ServerHost ?? "—"), Strings.Yes, Strings.No)
            || !await IsCurrentAsync(current))
        {
            return;
        }

        var result = await _monitor.RequestAsync(PipeProtocol.Methods.ServerReset, EmptyRequest.Instance,
            IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.OkResult);
        if (!result.Ok)
        {
            _showError(result.Message ?? Strings.ProblemNoAnswer);
        }
    }

    private async Task<bool> IsCurrentAsync(string? previous)
    {
        if (!await _monitor.EnsureUnlockedAsync())
        {
            return false;
        }

        if (string.Equals(_monitor.Snapshot?.Server.ServerUrl, previous, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        _showError(DesktopStrings.MonitorConnectionChanged);
        return false;
    }

    private void ClearLine()
    {
        _line.Reveal = false;
        _line.Text = string.Empty;
    }

    private static string? DisplayUrl(string? value) => MonitorClient.IsValidServerUrl(value, out string normalized)
        ? new Uri(normalized).GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
        : null;
}
