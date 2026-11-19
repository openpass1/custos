using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Custos.Core;
using Custos.Core.Agent;
using Custos.Core.Ipc;
using Custos.Core.Json;
using Custos.Core.Model;
using Custos.Core.Monitor;
using Custos.Gui;

namespace Custos.Mac.Gui;

/// <summary>
/// The settings of the service (section 6.10) in pages: the look and the language, the monitoring site, updates,
/// logs, accounts, rights. Only administrators change them; others see them.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private const string ConnectionExample = "http://custos.example.lan#K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ#3F2A-91C0-77DE-0B14";
    private static readonly string[] Languages = ["auto", "ru", "en"];

    private readonly ServiceMonitor _monitor;
    private readonly bool _isAdmin;
    private readonly ComboBox _theme;
    private readonly ComboBox _color;
    private readonly ComboBox _language;
    private readonly NumericUpDown _logDays;
    private readonly NumericUpDown _historyDays;
    private readonly ComboBox _logLevel;
    private readonly CheckBox _checkUpdates;
    private readonly CheckBox _allowAll;
    private readonly TextBox _serverLine = Ui.TextBox(null, ConnectionExample);
    private readonly TextBlock _serverState = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.Medium };
    private readonly TextBlock _serverDetails = Ui.Note(string.Empty);
    private readonly TextBlock _updateState = Ui.Note(string.Empty);
    private readonly TextBlock _passwordState = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ListBox _accounts = Ui.List(220);
    private readonly TextBlock _error = Ui.Error();
    private readonly Button _updateNow;
    private readonly Button _reset;
    private List<Credential> _accountList = [];

    public SettingsWindow(ServiceMonitor monitor)
    {
        _monitor = monitor;
        _isAdmin = monitor.Status.Hello?.IsAdmin == true;
        var settings = monitor.Snapshot?.Settings ?? new AgentSettings();
        Title = Strings.SettingsTitle;
        Width = 900;
        Height = 640;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _theme = Ui.Choice([(Themes.Auto, MacStrings.ThemeAuto), (Themes.Light, Strings.ThemeLight), (Themes.Dark, Strings.ThemeDark)], settings.Theme);
        // The colour belongs to this user, not to the service: it is kept in ui.json and changes as soon as it is picked.
        _color = Ui.Choice(Palettes.Ids.Select(id => (id, Palettes.Title(id))), Palettes.Known(UiState.Load().Color));
        _color.SelectionChanged += (_, _) => PickColor(Ui.Read(_color));
        _language = Ui.Choice([("auto", Strings.LanguageAuto), ("ru", Strings.LanguageRussian), ("en", Strings.LanguageEnglish)],
            Languages.Contains(settings.Language) ? settings.Language : "auto");
        _logDays = Ui.Number(settings.LogRetentionDays, 1, 3650);
        _historyDays = Ui.Number(settings.HistoryRetentionDays, 1, 36500);
        _logLevel = Ui.Choice([(LogLevels.Summary, Strings.LogLevelSummary), (LogLevels.Files, Strings.LogLevelFiles)], settings.DefaultLogLevel, 320);
        _checkUpdates = Ui.Check(Strings.CheckUpdates, settings.CheckUpdates);
        _allowAll = Ui.Check(Strings.AllowAllUsersEdit, settings.AllowAllUsersEdit);

        var paste = Ui.Text(Strings.ServerPasteTip, () => { });
        paste.OnClick(PasteAsync);
        var connect = Ui.Filled(Strings.ServerConnectButton, () => { });
        connect.OnClick(() => ConnectAsync(connect), ShowError);
        _reset = Ui.Text(Strings.ServerResetButton, () => { });
        _reset.OnClick(ResetAsync, ShowError);
        var checkNow = Ui.Outlined(Strings.UpdatesCheckNow, () => { });
        checkNow.OnClick(() => CheckUpdatesAsync(checkNow), ShowError);
        _updateNow = Ui.Filled(Strings.UpdateInstall, () => { });
        _updateNow.OnClick(UpdateAsync, ShowError);
        var setPassword = Ui.Outlined(Strings.ProgramPasswordSetButton, () => { });
        setPassword.OnClick(SetPasswordAsync, ShowError);
        var removePassword = Ui.Text(Strings.ProgramPasswordRemoveButton, () => { });
        removePassword.OnClick(RemovePasswordAsync, ShowError);
        var addAccount = Ui.Outlined(Strings.ButtonAdd, () => { });
        addAccount.OnClick(AddAccountAsync, ShowError);
        var editAccount = Ui.Text(Strings.ButtonEdit, () => { });
        editAccount.OnClick(EditAccountAsync, ShowError);
        var deleteAccount = Ui.Text(Strings.ButtonRemove, () => { });
        deleteAccount.OnClick(DeleteAccountAsync, ShowError);

        string sevenZip = monitor.Snapshot?.SevenZipProblem is { } problem ? Strings.Format(MacStrings.SevenZipProblem, problem)
            : Strings.Format(MacStrings.SevenZipVersion, monitor.Snapshot?.SevenZipVersion ?? "?");
        var pages = new (string Title, string Icon, Control View)[]
        {
            (Strings.SettingsLook, "palette", Ui.Page(
                Ui.Title(Strings.SettingsLook),
                Ui.Row(Strings.ThemeLabel, _theme, MacStrings.ThemeHelp),
                Ui.Row(MacStrings.ColorLabel, _color, MacStrings.ColorHelp),
                Ui.Row(Strings.ServiceLanguage, _language, MacStrings.ServiceLanguageHelp))),
            (Strings.SettingsServer, "cloud", Ui.Page(
                Ui.Title(Strings.SettingsServer),
                _serverState,
                _serverDetails,
                Ui.Heading(Strings.ServerLineLabel),
                Ui.Horizontal(_serverLine, paste),
                Ui.Note(Strings.ServerLineHelp),
                Ui.Horizontal(connect, _reset))),
            (Strings.SettingsUpdates, "update", Ui.Page(
                Ui.Title(Strings.SettingsUpdates),
                _checkUpdates,
                Ui.Note(Strings.Format(MacStrings.CheckUpdatesHelp, CustosInfo.GitHubRepository)),
                _updateState,
                Ui.Horizontal(checkNow, _updateNow),
                Ui.Heading(Strings.Settings7Zip),
                Ui.Note(sevenZip))),
            (Strings.SettingsLogs, "receipt_long", Ui.Page(
                Ui.Title(Strings.SettingsLogs),
                Ui.Row(Strings.LogRetention, _logDays, MacStrings.LogRetentionHelp, 260),
                Ui.Row(Strings.HistoryRetention, _historyDays, Strings.HistoryRetentionHelp, 260),
                Ui.Row(Strings.DefaultLogLevel, _logLevel, Strings.DefaultLogLevelHelp, 260))),
            (MacStrings.SettingsAccounts, "key", Ui.Page(
                Ui.Title(Strings.SettingsAccounts),
                Ui.Note(MacStrings.SettingsAccountsNote),
                _accounts,
                Ui.Horizontal(addAccount, editAccount, deleteAccount))),
            (Strings.SettingsRights, "shield", Ui.Page(
                Ui.Title(Strings.SettingsRights),
                _allowAll,
                Ui.Note(MacStrings.AllowAllUsersEditHelp),
                Ui.Row(Strings.ProgramPasswordLabel, Ui.Horizontal(_passwordState, setPassword, removePassword), MacStrings.ProgramPasswordHelp),
                Ui.Note(_isAdmin ? string.Empty : Strings.SettingsRightsAdminOnly))),
        };

        var view = new ContentControl();
        var nav = new ListBox
        {
            Theme = Ui.Theme("NavList"),
            ItemsSource = pages.Select(p => new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children = { new Icon { Kind = p.Icon, Size = 20 }, new TextBlock { Text = p.Title, VerticalAlignment = VerticalAlignment.Center } },
            }).ToList(),
            Width = 240,
            Margin = new Thickness(12),
        };
        nav.SelectionChanged += (_, _) => view.Content = nav.SelectedIndex >= 0 ? pages[nav.SelectedIndex].View : null;

        var save = Ui.Filled(Strings.ButtonOk, () => { });
        save.OnClick(() => SaveAsync(save), ShowError);
        save.IsEnabled = _isAdmin || monitor.CanEdit;
        var bottom = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(24, 10, 20, 14),
            Children =
            {
                new StackPanel { Children = { Ui.Note(_isAdmin || monitor.CanEdit ? string.Empty : Strings.SettingsReadOnly), _error } },
                Ui.Horizontal(Ui.Text(Strings.ButtonCancel, Close), save),
            },
        };
        Grid.SetColumn(bottom.Children[1], 1);
        var dock = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(nav, Dock.Left);
        dock.Children.Add(bottom);
        dock.Children.Add(nav);
        dock.Children.Add(new Border { Classes = { "pane" }, Margin = new Thickness(12), Child = view });
        Content = dock;
        nav.SelectedIndex = 0;

        foreach (var control in new Control[] { _allowAll, setPassword, removePassword, connect, _reset, _serverLine, paste, _updateNow, checkNow })
        {
            control.IsEnabled = _isAdmin;
        }

        _accounts.DoubleTapped += (_, _) => Safe.Run(EditAccountAsync, ShowError);
        monitor.SnapshotChanged += OnSnapshot;
        Closed += (_, _) => monitor.SnapshotChanged -= OnSnapshot;
        ShowState();
    }

    private void OnSnapshot(object? sender, EventArgs e) => ShowState();

    /// <summary>Puts the colour scheme in place in every window at once and remembers it for this user.</summary>
    private static void PickColor(string id)
    {
        if (Application.Current is { } app)
        {
            Palettes.Apply(app, id);
        }

        var state = UiState.Load();
        state.Color = id == Palettes.Default ? null : id;
        state.Save();
    }

    private void ShowError(Exception ex) => Ui.Show(_error, ex.Message);

    private void ShowState()
    {
        var snapshot = _monitor.Snapshot;
        var server = snapshot?.Server ?? new ServerLinkInfo();
        string state = ServerLink.StateText(server.State);
        if (server.ServerName is { Length: > 0 } name && server.State is ServerLinkStates.Approved or ServerLinkStates.Pending)
        {
            state += " " + Strings.Format(Strings.ServerNamed, name);
        }

        _serverState.Text = server.Detail is { } detail ? state.TrimEnd('.') + ": " + detail : state;
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

        _serverDetails.Text = string.Join("\n", lines);
        _reset.IsEnabled = _isAdmin && server.ServerUrl is not null;

        var update = snapshot?.ProgramUpdate ?? new ProgramUpdateInfo();
        _updateState.Text = update.ManagedBySite ? Strings.UpdatesManagedBySite
            : !update.Checking ? Strings.UpdatesOff
            : update.CheckedAt is null ? Strings.UpdatesNever
            : Strings.Format(Strings.UpdatesLastCheck, update.CheckedAt.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                update.Error ?? (update.Latest is null ? Strings.UpdatesNone
                    : update.Available ? Strings.Format(Strings.UpdatesAvailable, update.Latest) : Strings.Format(Strings.UpdatesLatest, update.Latest)));
        _updateNow.IsVisible = update.Available;
        _updateNow.IsEnabled = _isAdmin && !update.Installing;

        _passwordState.Text = snapshot?.ProgramPasswordSet == true ? Strings.ProgramPasswordIsSet : Strings.ProgramPasswordNotSet;

        int selected = _accounts.SelectedIndex;
        _accountList = (snapshot?.Credentials ?? []).Where(c => c.Kind != CredentialKinds.ArchivePassword && c.Kind != CredentialKinds.SshKey)
            .OrderBy(c => c.Kind, StringComparer.Ordinal).ThenBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        _accounts.ItemsSource = _accountList.Select(c => $"{KindTitle(c.Kind)} — {c.DisplayName}").ToList();
        _accounts.SelectedIndex = Math.Min(selected, _accountList.Count - 1);
    }

    private static string KindTitle(string kind) => kind switch
    {
        CredentialKinds.Windows => MacStrings.AccountKindSmb,
        CredentialKinds.Ftp => Strings.AccountKindFtp,
        CredentialKinds.Sftp => Strings.AccountKindSftp,
        CredentialKinds.WebDav => Strings.AccountKindWebDav,
        CredentialKinds.S3 => Strings.AccountKindS3,
        CredentialKinds.GDrive => Strings.AccountKindGDrive,
        CredentialKinds.YaDisk => Strings.AccountKindYaDisk,
        CredentialKinds.Dropbox => Strings.AccountKindDropbox,
        CredentialKinds.Db => Strings.PageDatabases,
        _ => kind,
    };

    /// <summary>The settings as the service sent them, to change without touching the snapshot.</summary>
    private static AgentSettings Copy(AgentSettings settings) =>
        JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(settings, ConfigJsonContext.Default.AgentSettings), ConfigJsonContext.Default.AgentSettings)!;

    private async Task SaveAsync(Button save)
    {
        Ui.Show(_error, null);
        var settings = Copy(_monitor.Snapshot?.Settings ?? new AgentSettings());
        settings.Theme = Ui.Read(_theme);
        settings.Language = Ui.Read(_language);
        settings.LogRetentionDays = Ui.Read(_logDays);
        settings.HistoryRetentionDays = Ui.Read(_historyDays);
        settings.DefaultLogLevel = Ui.Read(_logLevel);
        settings.CheckUpdates = Ui.Read(_checkUpdates);
        settings.AllowAllUsersEdit = Ui.Read(_allowAll);
        save.IsEnabled = false;
        try
        {
            await _monitor.RequestAsync(PipeProtocol.Methods.SettingsSave, new SettingsSaveRequest { Settings = settings },
                IpcJsonContext.Default.SettingsSaveRequest, IpcJsonContext.Default.OkResult);
            Close();
        }
        finally
        {
            save.IsEnabled = true;
        }
    }

    private async Task PasteAsync()
    {
        string? text = Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
        string? line = text?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(line))
        {
            Ui.Show(_error, Strings.ServerPasteEmpty);
            return;
        }

        _serverLine.Text = line;
    }

    private async Task ConnectAsync(Button connect)
    {
        Ui.Show(_error, null);
        string line = (_serverLine.Text ?? string.Empty).Trim();
        if (line.Length == 0)
        {
            Ui.Show(_error, Strings.ServerLineRequired);
            return;
        }

        if (!RegistrationCode.TrySplitConnection(line, out string url, out string code) || RegistrationCode.TryParse(code, out _) is null)
        {
            Ui.Show(_error, Strings.Format(Strings.ServerLineBad, ConnectionExample));
            return;
        }

        connect.IsEnabled = false;
        try
        {
            await _monitor.RequestAsync(PipeProtocol.Methods.ServerConnect, new ServerConnectRequest { ServerUrl = url, Code = code },
                IpcJsonContext.Default.ServerConnectRequest, IpcJsonContext.Default.OkResult);
            _serverLine.Text = string.Empty;
            await Dialogs.Show(this, Strings.ServerConnectDone);
        }
        finally
        {
            connect.IsEnabled = _isAdmin;
        }
    }

    private async Task ResetAsync()
    {
        string host = _monitor.Snapshot?.Server.ServerHost ?? string.Empty;
        if (await Dialogs.Ask(this, Strings.Format(Strings.ServerResetConfirm, host), Strings.ServerResetButton.TrimEnd('…', '.')))
        {
            await _monitor.CommandAsync(PipeProtocol.Methods.ServerReset);
        }
    }

    private async Task CheckUpdatesAsync(Button button)
    {
        button.IsEnabled = false;
        try
        {
            await _monitor.RequestAsync(PipeProtocol.Methods.ProgramCheck, EmptyRequest.Instance, IpcJsonContext.Default.EmptyRequest,
                IpcJsonContext.Default.ProgramUpdateInfo, TimeSpan.FromMinutes(1));
        }
        finally
        {
            button.IsEnabled = _isAdmin;
        }
    }

    private async Task UpdateAsync()
    {
        var update = _monitor.Snapshot?.ProgramUpdate;
        if (update?.Latest is not { } latest || !await Dialogs.Ask(this, Strings.Format(MacStrings.UpdateAsk, latest), Strings.UpdateInstall))
        {
            return;
        }

        var result = await _monitor.RequestAsync(PipeProtocol.Methods.ProgramUpdate, EmptyRequest.Instance, IpcJsonContext.Default.EmptyRequest,
            IpcJsonContext.Default.OkResult, TimeSpan.FromMinutes(15));
        if (result.Message is { } message)
        {
            await Dialogs.Show(this, message);
        }
    }

    private async Task SetPasswordAsync()
    {
        if (await Dialogs.NewPassword(this, Strings.PasswordSetTitle, Strings.Format(Strings.PasswordSetNote, ProgramPassword.MinLength),
            ProgramPassword.MinLength) is not { } password)
        {
            return;
        }

        await _monitor.RequestAsync(PipeProtocol.Methods.PasswordSet, new PasswordRequest { Password = password },
            IpcJsonContext.Default.PasswordRequest, IpcJsonContext.Default.OkResult);
        await Dialogs.Show(this, Strings.ProgramPasswordSaved);
    }

    private async Task RemovePasswordAsync()
    {
        if (_monitor.Snapshot?.ProgramPasswordSet != true || !await Dialogs.Ask(this, Strings.ProgramPasswordRemoveConfirm, Strings.ProgramPasswordRemoveButton))
        {
            return;
        }

        await _monitor.RequestAsync(PipeProtocol.Methods.PasswordSet, new PasswordRequest { Password = null },
            IpcJsonContext.Default.PasswordRequest, IpcJsonContext.Default.OkResult);
        await Dialogs.Show(this, Strings.ProgramPasswordRemoved);
    }

    private async Task AddAccountAsync()
    {
        var kind = Ui.Choice([(CredentialKinds.Windows, MacStrings.AccountKindSmb), (CredentialKinds.Sftp, Strings.AccountKindSftp),
            (CredentialKinds.Ftp, Strings.AccountKindFtp), (CredentialKinds.WebDav, Strings.AccountKindWebDav), (CredentialKinds.S3, Strings.AccountKindS3)],
            CredentialKinds.Windows, 320);
        var dialog = new DialogWindow(Strings.AccountAddTitle);
        dialog.Body.Children.Add(Ui.Row(Strings.AccountKind, kind, labelWidth: 120));
        dialog.AddButton(Strings.ButtonCancel, () => dialog.Close(null));
        dialog.AddButton(Strings.ButtonOk, () => dialog.Close(Ui.Read(kind)), main: true);
        if (await dialog.ShowDialog<string?>(this) is { } chosen)
        {
            await Dialogs.EditAccount(this, _monitor, chosen, null);
        }
    }

    private async Task EditAccountAsync()
    {
        int index = _accounts.SelectedIndex;
        if (index < 0 || index >= _accountList.Count || CredentialKinds.IsCloud(_accountList[index].Kind) || _accountList[index].Kind == CredentialKinds.Db)
        {
            return;
        }

        await Dialogs.EditAccount(this, _monitor, _accountList[index].Kind, _accountList[index]);
    }

    private async Task DeleteAccountAsync()
    {
        int index = _accounts.SelectedIndex;
        if (index < 0 || index >= _accountList.Count)
        {
            return;
        }

        var account = _accountList[index];
        if (await Dialogs.Ask(this, Strings.Format(Strings.ConfirmDeleteAccount, account.DisplayName), Strings.ButtonRemove))
        {
            await _monitor.RequestAsync(PipeProtocol.Methods.CredentialDelete, new CredentialRequest { CredentialId = account.Id },
                IpcJsonContext.Default.CredentialRequest, IpcJsonContext.Default.OkResult);
        }
    }
}
