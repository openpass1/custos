using Custos.Core;
using Custos.Core.Agent;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>
/// «Все настройки»: the monitoring server, who may change things and what users see, the program password, the logs
/// and the history, the priority and language of the service, updates, 7-Zip, the cloud drives and the stored
/// accounts (SettingsForm of Custos.exe). Everyone may look; only administrators may change (section 4). The theme
/// and the colour are in the menu «Настройки» itself.
/// </summary>
internal sealed class SettingsDialog : FormDialog
{
    private static readonly string[] Languages = ["auto", "ru", "en"];
    private static readonly string[] TrayModeValues = [TrayModes.Full, TrayModes.Status, TrayModes.Hidden];

    private readonly ServiceMonitor _monitor;
    private readonly bool _canEdit;
    private readonly bool _isAdmin;
    private readonly CheckItem _allowRemoteEdit = new(Strings.AllowRemoteEdit, description: Strings.AllowRemoteEditHelp);
    private readonly CheckItem _allowAll = new(Strings.AllowAllUsersEdit, description: Strings.AllowAllUsersEditHelp);
    private readonly ChoiceItem _trayMode = new(L(Strings.TrayModeLabel), [Strings.TrayModeFull, Strings.TrayModeStatus, Strings.TrayModeHidden], 0, 360)
    {
        Help = Strings.TrayModeHelp,
    };

    private readonly NoteItem _passwordState = new(string.Empty);
    private readonly ButtonItem _passwordSet;
    private readonly ButtonItem _passwordRemove;
    private readonly ChoiceItem _logLevel = new(L(Strings.DefaultLogLevel), [Strings.LogLevelSummary, Strings.LogLevelFiles], 0, 300)
    {
        Help = Strings.DefaultLogLevelHelp,
    };

    private readonly NumberItem _retention = new(L(Strings.LogRetention), 30, 1, 3650, 120) { Help = Strings.LogRetentionHelp };
    private readonly NumberItem _historyRetention = new(L(Strings.HistoryRetention), 365, 1, 3650, 120) { Help = Strings.HistoryRetentionHelp };
    private readonly ChoiceItem _priority = new(L(Strings.CopyPriority), [Strings.PriorityLow, Strings.PriorityNormal], 0, 300) { Help = Strings.CopyPriorityHelp };
    private readonly ChoiceItem _language = new(L(Strings.ServiceLanguage), [Strings.LanguageAuto, Strings.LanguageRussian, Strings.LanguageEnglish], 0, 300)
    {
        Help = Strings.ServiceLanguageHelp,
    };

    private readonly CheckItem _checkUpdates = new(Strings.CheckUpdates, description: Strings.Format(Strings.CheckUpdatesHelp, CustosInfo.GitHubRepository));
    private readonly NoteItem _updatesState = new(string.Empty);
    private readonly ButtonItem _updatesCheck;
    private readonly NoteItem _archiver = new(string.Empty);
    private readonly ButtonItem _sevenZipCheck;
    private readonly NoteItem _cloudApps = new(string.Empty);
    private readonly ButtonItem _cloudOwn;
    private readonly CardsItem _accounts = new(Strings.AccountNotUsed);
    private readonly ButtonItem _addAccount;
    private readonly List<ButtonItem> _connectCloud = [];
    private CloudAppsResult? _cloudAppsResult;

    public SettingsDialog(ServiceMonitor monitor)
        : base(Strings.SettingsTitle, 760, sizable: true)
    {
        _monitor = monitor;
        _canEdit = monitor.CanEdit;
        _isAdmin = monitor.Status.Hello?.IsAdmin == true;
        bool connected = monitor.IsConnected;
        var failed = (Action<Exception>)(ex => ShowError(ex.Message));

        _ = new MonitorSettingsSection(this, monitor, ShowError);
        Items.Add(_allowRemoteEdit);

        // Section 4: who may change things, what users see, the program password.
        _passwordSet = new ButtonItem("password", Strings.ProgramPasswordSetButton, SetPasswordAsync) { Failed = failed };
        _passwordRemove = new ButtonItem(null, Strings.ProgramPasswordRemoveButton, RemovePasswordAsync, ButtonLook.Outlined) { Failed = failed };
        Items.Add(new HeadingItem(Strings.SettingsRights));
        Items.Add(_allowAll);
        Items.Add(_trayMode);
        Items.Add(new HeadingItem(L(Strings.ProgramPasswordLabel), Strings.ProgramPasswordHelp));
        Items.Add(_passwordState);
        Items.Add(new RowItem(_passwordSet, _passwordRemove));
        if (!_isAdmin)
        {
            Items.Add(new NoteItem(Strings.SettingsRightsAdminOnly));
        }

        Items.Add(new HeadingItem(Strings.SettingsLogs));
        Items.Add(_logLevel);
        Items.Add(_retention);
        Items.Add(_historyRetention);
        Items.Add(_priority);
        Items.Add(_language);

        // New versions of the program on GitHub, for a computer without a monitoring site.
        _updatesCheck = new ButtonItem("update", Strings.UpdatesCheckNow, CheckUpdatesAsync) { Failed = failed };
        Items.Add(new HeadingItem(Strings.SettingsUpdates));
        Items.Add(_checkUpdates);
        Items.Add(_updatesState);
        Items.Add(_updatesCheck);

        _sevenZipCheck = new ButtonItem("update", Strings.SevenZipCheckButton, CheckSevenZipAsync) { Failed = failed };
        Items.Add(new HeadingItem(Strings.Settings7Zip));
        Items.Add(_archiver);
        Items.Add(_sevenZipCheck);
        Items.Add(new NoteItem(CustosEdition.IsLegacy ? Strings.Settings7ZipNoteLegacy : Strings.Settings7ZipNote));
        _sevenZipCheck.IsEnabled = _isAdmin && connected;

        // Cloud drives (section 6.3): what this version brings, and the administrator's own applications. The legacy
        // edition has none (CustosEdition).
        _cloudOwn = ButtonItem.Of("apps", Strings.SettingsCloudOwn, () => Safe.Run(EditCloudAppsAsync, failed), ButtonLook.Outlined);
        _cloudOwn.IsEnabled = false;
        if (!CustosEdition.IsLegacy)
        {
            Items.Add(new HeadingItem(Strings.SettingsCloud));
            Items.Add(_cloudApps);
            Items.Add(_cloudOwn);
            Items.Add(new NoteItem(_isAdmin ? Strings.SettingsCloudNote : Strings.CloudAdminOnly));
            _cloudApps.Show(_isAdmin ? null : Strings.Format(Strings.SettingsCloudBuiltIn, "—"));
        }

        // The stored accounts: each with what uses it.
        _addAccount = ButtonItem.Of("person_add", Strings.ButtonAdd, () => Safe.Run(AddAccountAsync, failed));
        _addAccount.IsEnabled = _canEdit;
        Items.Add(new HeadingItem(Strings.SettingsAccounts));
        Items.Add(new NoteItem(Strings.SettingsAccountsNote));
        Items.Add(_accounts);
        var accountButtons = new List<FormItem> { _addAccount };
        if (!CustosEdition.IsLegacy)
        {
            foreach (string kind in CloudNames.Kinds)
            {
                string chosen = kind;
                var connect = ButtonItem.Of("cloud", CloudNames.Name(kind), () => Safe.Run(() => ConnectCloudAsync(chosen, null), failed), ButtonLook.Outlined);
                connect.IsEnabled = _isAdmin && connected;
                _connectCloud.Add(connect);
                accountButtons.Add(connect);
            }
        }

        Items.Add(new RowItem([.. accountButtons]));
        if (!_canEdit)
        {
            Items.Add(new NoteItem(Strings.SettingsReadOnly));
        }

        var settings = monitor.Snapshot?.Settings ?? new AgentSettings();
        _logLevel.Load(settings.DefaultLogLevel == LogLevels.Files ? 1 : 0);
        _retention.Value = Math.Clamp(settings.LogRetentionDays, 1, 3650);
        _historyRetention.Value = Math.Clamp(settings.HistoryRetentionDays, 1, 3650);
        _priority.Load(settings.ProcessPriority == "normal" ? 1 : 0);
        _language.Load(Math.Max(0, Array.IndexOf(Languages, settings.Language)));
        _trayMode.Load(Math.Max(0, Array.IndexOf(TrayModeValues, settings.TrayMode)));
        _allowAll.Load(settings.AllowAllUsersEdit);
        _checkUpdates.Load(settings.CheckUpdates);
        _allowRemoteEdit.Load(settings.AllowRemoteEdit);
        foreach (var item in new FormItem[] { _logLevel, _retention, _historyRetention, _priority, _language })
        {
            item.IsEnabled = _canEdit;
        }

        // The rights stay with the administrators even when everybody may change tasks (the service keeps them so).
        foreach (var item in new FormItem[] { _allowRemoteEdit, _allowAll, _trayMode })
        {
            item.IsEnabled = _canEdit && _isAdmin;
        }

        _allowAll.OnChange = () =>
        {
            // Turning it on hands out the rights of an administrator: said plainly, and only on «Да».
            if (_allowAll.On && !settings.AllowAllUsersEdit)
            {
                Safe.Run(async () =>
                {
                    if (!await Dialogs.Ask(this, Strings.AllowAllUsersEditWarning, Strings.Yes, Strings.No))
                    {
                        _allowAll.Load(false);
                    }
                });
            }
        };

        OkButton.IsVisible = _canEdit;
        CancelButton.Content = _canEdit ? Strings.ButtonCancel : Strings.ButtonClose;
        ShowAll();
        _monitor.SnapshotChanged += OnSnapshotChanged;
        Closed += (_, _) => _monitor.SnapshotChanged -= OnSnapshotChanged;
        if (_isAdmin && connected && !CustosEdition.IsLegacy)
        {
            Opened += (_, _) => Safe.Run(LoadCloudAppsAsync, ex => _cloudApps.Show(ex.Message, Tone.Error));
        }
    }

    protected override async Task<bool> OkAsync()
    {
        if (_monitor.Snapshot?.Settings is not { } current)
        {
            ShowError(Strings.ProblemNoConnection);
            return false;
        }

        if (!_retention.IsValid || !_historyRetention.IsValid)
        {
            ShowError(!_retention.IsValid ? $"«{_retention.Label}»: {_retention.Range()}" : $"«{_historyRetention.Label}»: {_historyRetention.Range()}");
            return false;
        }

        // A copy: the snapshot keeps what the service said until it says otherwise.
        var json = IpcJsonContext.Default.AgentSettings;
        var settings = System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(current, json), json)!;
        settings.DefaultLogLevel = _logLevel.Selected == 1 ? LogLevels.Files : LogLevels.Summary;
        settings.LogRetentionDays = _retention.Value;
        settings.HistoryRetentionDays = _historyRetention.Value;
        settings.ProcessPriority = _priority.Selected == 1 ? "normal" : "low";
        settings.Language = Languages[Math.Max(0, _language.Selected)];
        settings.TrayMode = TrayModeValues[Math.Max(0, _trayMode.Selected)];
        settings.AllowAllUsersEdit = _allowAll.On;
        settings.AllowRemoteEdit = _allowRemoteEdit.On;
        settings.CheckUpdates = _checkUpdates.On;
        await _monitor.RequestAsync(PipeProtocol.Methods.SettingsSave, new SettingsSaveRequest { Settings = settings },
            IpcJsonContext.Default.SettingsSaveRequest, IpcJsonContext.Default.OkResult);
        return true;
    }

    private static string L(string label) => label.TrimEnd(':', ' ');

    private static bool UsesCredential(TaskDefinition task, string id) =>
        task.Sources.Any(s => Describe.SameId(s.CredentialId, id))
        || task.Destinations.Any(d => Describe.SameId(d.CredentialId, id))
        || task.DbServers.Any(d => Describe.SameId(d.CredentialId, id))
        || Describe.SameId(task.Archive.PasswordCredentialId, id);

    private static string KindIcon(string kind) => kind switch
    {
        CredentialKinds.Windows => "lan",
        CredentialKinds.Db => "database",
        CredentialKinds.SshKey => "key",
        _ when CredentialKinds.IsCloud(kind) => "cloud",
        _ => "dns",
    };

    private void OnSnapshotChanged(object? sender, EventArgs e) => ShowAll();

    private void ShowAll()
    {
        FillAccounts();
        ShowPassword();
        ShowUpdates();
        ShowArchiver();
    }

    /// <summary>The daily check of GitHub: on or off, and what it last found. A computer connected to a monitoring
    /// site gets its updates from the site, so the check is not offered there.</summary>
    private void ShowUpdates()
    {
        var update = _monitor.Snapshot?.ProgramUpdate ?? new ProgramUpdateInfo();
        bool site = update.ManagedBySite;
        _checkUpdates.IsEnabled = _canEdit && _isAdmin && !site;

        // Off (or a site decides): a check would show nowhere, so there is nothing to press.
        _updatesCheck.IsEnabled = _isAdmin && _monitor.IsConnected && update.Checking;
        if (site)
        {
            _updatesState.Show(Strings.UpdatesManagedBySite, Tone.Plain);
            return;
        }

        string found = update.Error
            ?? (update.Latest is null ? Strings.UpdatesNone
                : update.Available ? Strings.Format(Strings.UpdatesAvailable, update.Latest)
                : Strings.Format(Strings.UpdatesLatest, CustosInfo.Version));
        _updatesState.Show(!update.Checking ? Strings.UpdatesOff
            : update.CheckedAt is { } at ? Strings.Format(Strings.UpdatesLastCheck, Describe.Time(at), found)
            : Strings.UpdatesNever, update.Error is null ? Tone.Plain : Tone.Caution);
    }

    /// <summary>«Проверить сейчас»: the service asks GitHub at once.</summary>
    private async Task CheckUpdatesAsync()
    {
        try
        {
            await _monitor.RequestAsync(PipeProtocol.Methods.ProgramCheck, EmptyRequest.Instance,
                IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.ProgramUpdateInfo, TimeSpan.FromMinutes(1));
        }
        finally
        {
            ShowUpdates();
        }
    }

    /// <summary>The version of 7z.dll the service uses, or what is wrong with it.</summary>
    private void ShowArchiver()
    {
        var snapshot = _monitor.Snapshot;
        _archiver.Show(snapshot?.SevenZipProblem is { } problem ? Strings.Format(Strings.Settings7ZipProblem, problem)
            : Strings.Format(Strings.Settings7ZipVersion, snapshot?.SevenZipVersion ?? "—"), snapshot?.SevenZipProblem is null ? Tone.Plain : Tone.Caution);
    }

    /// <summary>«Проверить обновление 7-Zip»: asks the service, which asks GitHub; updates only after «Да».</summary>
    private async Task CheckSevenZipAsync()
    {
        var json = IpcJsonContext.Default;
        try
        {
            var check = await _monitor.RequestAsync(PipeProtocol.Methods.SevenZipCheck, EmptyRequest.Instance,
                json.EmptyRequest, json.SevenZipCheckResult, TimeSpan.FromMinutes(1));
            if (check.Error is { } error)
            {
                await Dialogs.Show(this, error);
                return;
            }

            if (!check.UpdateAvailable)
            {
                await Dialogs.Show(this, Strings.Format(Strings.SevenZipNothingNew, check.Installed ?? "—", check.Latest));
                return;
            }

            if (!await Dialogs.Ask(this, Strings.Format(CustosEdition.IsLegacy ? Strings.SevenZipAskUpdateLegacy : Strings.SevenZipAskUpdate,
                    check.Installed ?? "—", check.Latest), Strings.Yes, Strings.No))
            {
                return;
            }

            _archiver.Show(Strings.Format(Strings.SevenZipUpdatingTo, check.Latest), Tone.Plain);
            var result = await _monitor.RequestAsync(PipeProtocol.Methods.SevenZipUpdate, EmptyRequest.Instance,
                json.EmptyRequest, json.OkResult, TimeSpan.FromMinutes(15));
            ShowArchiver();
            await Dialogs.Show(this, result.Ok ? result.Message ?? Strings.Format(Strings.Settings7ZipVersion, check.Latest)
                : result.Message ?? Strings.ProblemNoAnswer);
        }
        finally
        {
            ShowArchiver();
        }
    }

    /// <summary>Whether a program password is set, and the buttons for it (administrators only).</summary>
    private void ShowPassword()
    {
        bool set = _monitor.Snapshot?.ProgramPasswordSet == true;
        _passwordState.Show(set ? Strings.ProgramPasswordIsSet : Strings.ProgramPasswordNotSet, Tone.Plain);
        _passwordSet.Text = set ? Strings.ProgramPasswordChangeButton : Strings.ProgramPasswordSetButton;
        _passwordSet.IsEnabled = _isAdmin && _monitor.IsConnected;
        _passwordRemove.IsVisible = set;
        _passwordRemove.IsEnabled = _isAdmin && _monitor.IsConnected;
    }

    private async Task SetPasswordAsync()
    {
        string? password = await Dialogs.NewPassword(this, Strings.PasswordSetTitle,
            Strings.Format(Strings.PasswordSetNote, ProgramPassword.MinLength), ProgramPassword.MinLength);
        if (password is null)
        {
            return;
        }

        await _monitor.RequestAsync(PipeProtocol.Methods.PasswordSet, new PasswordRequest { Password = password },
            IpcJsonContext.Default.PasswordRequest, IpcJsonContext.Default.OkResult);
        await Dialogs.Show(this, Strings.ProgramPasswordSaved);
    }

    private async Task RemovePasswordAsync()
    {
        if (!await Dialogs.Ask(this, Strings.ProgramPasswordRemoveConfirm, Strings.Yes, Strings.No))
        {
            return;
        }

        await _monitor.RequestAsync(PipeProtocol.Methods.PasswordSet, new PasswordRequest { Password = null },
            IpcJsonContext.Default.PasswordRequest, IpcJsonContext.Default.OkResult);
        await Dialogs.Show(this, Strings.ProgramPasswordRemoved);
    }

    private void FillAccounts()
    {
        var snapshot = _monitor.Snapshot;
        string[] kinds = [.. CredentialDialog.Kinds, .. CloudNames.Kinds];
        var shown = (snapshot?.Credentials ?? [])
            .Where(c => Array.IndexOf(kinds, c.Kind) >= 0)
            .OrderBy(c => Array.IndexOf(kinds, c.Kind))
            .ThenBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _accounts.Fill(shown.Select(credential =>
        {
            var users = snapshot!.Tasks.Where(t => UsesCredential(t, credential.Id)).Select(t => t.Name).ToList();
            bool cloud = CredentialKinds.IsCloud(credential.Kind);
            return new CardItem(KindIcon(credential.Kind), credential.DisplayName,
                CredentialDialog.KindText(credential.Kind) + " · " + (users.Count == 0 ? Strings.AccountNotUsed : string.Join(", ", users)))
            {
                Edit = _canEdit && (!cloud || _isAdmin) ? () => Safe.Run(() => EditAccountAsync(credential), ex => ShowError(ex.Message)) : null,
                Delete = _canEdit ? () => Safe.Run(() => DeleteAccountAsync(credential), ex => ShowError(ex.Message)) : null,
            };
        }));
    }

    private async Task AddAccountAsync()
    {
        var dialog = new CredentialDialog(_monitor, null, null);
        await dialog.ShowAsync(this);
    }

    /// <summary>A new password of an account; an account of a cloud drive is connected again in the browser.</summary>
    private async Task EditAccountAsync(Credential account)
    {
        if (CredentialKinds.IsCloud(account.Kind))
        {
            await ConnectCloudAsync(account.Kind, account);
            return;
        }

        var dialog = new CredentialDialog(_monitor, account, account.Kind);
        await dialog.ShowAsync(this);
    }

    /// <summary>Connects a new account of a cloud drive, or an existing one again.</summary>
    private async Task ConnectCloudAsync(string kind, Credential? existing)
    {
        var dialog = new CloudConnectDialog(_monitor, kind, existing);
        await dialog.ShowAsync(this);
    }

    private async Task DeleteAccountAsync(Credential account)
    {
        if (!await Dialogs.Ask(this, Strings.Format(Strings.ConfirmDeleteAccount, account.DisplayName), Strings.ButtonRemove, Strings.ButtonCancel))
        {
            return;
        }

        await _monitor.RequestAsync(PipeProtocol.Methods.CredentialDelete, new CredentialRequest { CredentialId = account.Id },
            IpcJsonContext.Default.CredentialRequest, IpcJsonContext.Default.OkResult);
    }

    /// <summary>Which services this version has an application for; only administrators may ask.</summary>
    private async Task LoadCloudAppsAsync()
    {
        _cloudAppsResult = await _monitor.RequestAsync(PipeProtocol.Methods.CloudAppsGet, EmptyRequest.Instance,
            IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.CloudAppsResult);
        var builtIn = _cloudAppsResult.BuiltIn;
        string names = builtIn.Count == 0 ? Strings.SettingsCloudNone : string.Join(", ", CloudNames.Kinds.Where(builtIn.Contains).Select(CloudNames.Name));
        _cloudApps.Show(Strings.Format(Strings.SettingsCloudBuiltIn, names), Tone.Plain);
        _cloudOwn.IsEnabled = true;
    }

    private async Task EditCloudAppsAsync()
    {
        if (_cloudAppsResult is not { } current)
        {
            return;
        }

        var dialog = new CloudAppsDialog(_monitor, current);
        if (await dialog.ShowAsync(this))
        {
            current.Own = dialog.Entered();
        }
    }
}
