using System.Globalization;
using Avalonia.Input.Platform;
using Custos.Core;
using Custos.Core.Ipc;
using Custos.Core.Monitor;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>The service's monitoring status and the two local administrator operations.</summary>
internal sealed class MonitorSettingsSection
{
    // Example values, never the connection line received from the user.
    private const string Example = "http://custos.example.lan#K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ#3F2A-91C0-77DE-0B14";

    private readonly FormDialog _owner;
    private readonly ServiceMonitor _monitor;
    private readonly Action<string?> _showError;
    private readonly bool _linux = CustosEdition.IsLinux;
    private readonly NoteItem _state = new(string.Empty);
    private readonly NoteItem _details = new(string.Empty);
    private readonly NoteItem _administrators = new(Strings.ServerAdminOnly);
    private readonly TextItem _line;
    private readonly ButtonItem _connect;
    private readonly ButtonItem _reset;
    private readonly ButtonItem _changeServer;
    private readonly ButtonItem _cancelChange;
    private readonly RowItem _editorActions;
    private bool _busy;
    private bool _changingServer;

    public MonitorSettingsSection(FormDialog owner, ServiceMonitor monitor, Action<string?> showError)
    {
        _owner = owner;
        _monitor = monitor;
        _showError = showError;
        _line = new TextItem(Strings.ServerLineLabel.TrimEnd(':', ' '))
        {
            Placeholder = Example,
            Help = Strings.ServerLineHelp,
            ActionIcon = "content_paste",
            ActionTip = Strings.ServerPasteTip,
            Action = PasteAsync,
        };
        var failed = (Exception error) => _showError(error.Message);
        _connect = new ButtonItem("link", Strings.ServerConnectButton, () => ChangeAsync(ConnectAsync)) { Failed = failed };
        _reset = new ButtonItem("link_off", Strings.ServerResetButton, () => ChangeAsync(ResetAsync), ButtonLook.Outlined) { Failed = failed };
        _changeServer = ButtonItem.Of("link", DesktopStrings.MonitorChangeServer, StartChangingServer, ButtonLook.Outlined);
        _cancelChange = ButtonItem.Of(null, Strings.ButtonCancel, EndChangingServer, ButtonLook.Text);
        _editorActions = new RowItem(_connect, _cancelChange);

        owner.Items.Add(new HeadingItem(Strings.SettingsServer));
        owner.Items.Add(_state);
        owner.Items.Add(_details);
        owner.Items.Add(_changeServer);
        owner.Items.Add(_line);
        owner.Items.Add(_editorActions);
        owner.Items.Add(_reset);
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

    private bool CanChange => !_busy && _monitor.IsConnected && _monitor.Snapshot is not null
        && _monitor.Status.Hello?.IsAdmin == true;

    private void OnChanged(object? sender, EventArgs args) => Refresh();

    private void Refresh()
    {
        var server = _monitor.IsConnected ? _monitor.Snapshot?.Server : null;
        bool configured = server?.ServerUrl is { Length: > 0 };
        bool showEditor = !_linux || !configured || _changingServer;
        bool canChange = CanChange;
        _line.IsVisible = _editorActions.IsVisible = showEditor;
        _line.ReadOnly = !canChange;
        _line.IsActionEnabled = _connect.IsEnabled = canChange;
        _changeServer.IsVisible = _linux && configured && !_changingServer;
        _changeServer.IsEnabled = canChange;
        _reset.IsVisible = configured && showEditor;
        _reset.IsEnabled = canChange;
        _cancelChange.IsVisible = configured && _changingServer;
        _cancelChange.IsEnabled = !_busy;

        _administrators.IsVisible = _monitor.IsConnected && _monitor.Status.Hello?.IsAdmin == false;
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
        if (!_linux && server.Detail is { } detail)
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

    private void StartChangingServer()
    {
        if (CanChange)
        {
            _changingServer = true;
            Refresh();
        }
    }

    private void EndChangingServer()
    {
        _changingServer = false;
        ClearLine();
        Refresh();
    }

    private async Task PasteAsync()
    {
        if (!CanChange)
        {
            return;
        }

        string? text = _owner.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
        if (!CanChange || !_owner.IsVisible || !_line.IsVisible)
        {
            return;
        }

        string? line = text?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(line))
        {
            _line.Error = Strings.ServerPasteEmpty;
            return;
        }

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
            || (_linux && new Uri(normalized).UserInfo.Length != 0))
        {
            _line.Error = Strings.Format(Strings.ServerLineBad, Example);
            return;
        }

        string? current = _monitor.Snapshot?.Server.ServerUrl;
        if (_linux && current is not null && !MonitorClient.HaveSameServerUrl(current, normalized)
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
            EndChangingServer();
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
        if (result.Ok)
        {
            EndChangingServer();
        }
        else
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

        if (MonitorClient.HaveSameServerUrl(_monitor.Snapshot?.Server.ServerUrl, previous))
        {
            return true;
        }

        _showError(DesktopStrings.MonitorConnectionChanged);
        return false;
    }

    private void ClearLine()
    {
        _line.Text = string.Empty;
        _line.Error = null;
    }

    private static string? DisplayUrl(string? value) => MonitorClient.IsValidServerUrl(value, out string normalized)
        ? new Uri(normalized).GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
        : null;
}
