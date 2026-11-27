using Custos.Core;
using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Remote;
using Custos.Core.Text;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>
/// The Linux server as source (CUSTOS-remote-pull.md 4.2): where the server is, from where the key may log on, what
/// custos-helper gives out, the key and the block to paste into a root session there, the check of the connection
/// that pins the key of the server, and the databases to dump, ticked from what the check found. The other pages of
/// the editor do the rest (RemoteSourceDialog of Custos.exe).
/// </summary>
internal sealed class RemoteSourceDialog : FormDialog
{
    private static readonly string[] Presets = [RemotePresets.IspmanagerSites, RemotePresets.IspmanagerFull, RemotePresets.Linux, RemotePresets.Custom];
    private static readonly string[] Engines = [DbEngines.MariaDb, DbEngines.MySql, DbEngines.PostgreSql];

    private readonly EditorContext _context;
    private readonly TaskSource _original;
    private readonly TextItem _host = new(L(Strings.RemoteHost), width: 300) { Help = Strings.RemoteHostHelp };
    private readonly NumberItem _port = new(L(Strings.RemotePort), 22, 1, 65535, 70);
    private readonly TextItem _user = new(L(Strings.RemoteUser), "custos", 160);
    private readonly TextItem _officeIp = new(L(Strings.RemoteOfficeIp), width: 300) { Help = Strings.RemoteOfficeIpHelp };
    private readonly NoteItem _officeWarning = new(Strings.RemoteOfficeEmpty, Tone.Caution);
    private readonly ChoiceItem _preset = new(L(Strings.RemotePreset), [.. Presets.Select(Describe.PresetTitle)], 0, 300);
    private readonly TextItem _roots = new(L(Strings.RemoteRoots)) { Multiline = true, Lines = 4, Help = Strings.RemoteRootsHelp };
    private readonly NoteItem _etcWarning = new(Strings.RemoteEtcWarning, Tone.Caution);
    private readonly TextItem _excludes = new(L(Strings.RemoteExcludes)) { Multiline = true, Lines = 3, Help = Strings.RemoteExcludesHelp };
    private readonly CheckItem[] _engines = [.. Engines.Select(e => new CheckItem(DbEngines.Title(e)) { Inline = true, Tag = e })];
    private readonly RadioGroup _dbChoice = new();
    private readonly RadioItem _dbAll;
    private readonly RadioItem _dbOnly;
    private readonly CheckListItem _dbList = new(140);
    private readonly NoteItem _dbListEmpty = new(Strings.RemoteDbListEmpty);
    private readonly NoteItem _key = new(string.Empty);
    private readonly ButtonItem _createKey;
    private readonly TextItem _block = new(null) { Multiline = true, Lines = 6, Mono = true, ReadOnly = true };
    private readonly ButtonItem _copy;
    private readonly ButtonItem _saveBlock;
    private readonly NoteItem _hostKey = new(string.Empty);
    private readonly ButtonItem _test;
    private readonly NoteItem _testResult = new(string.Empty) { IsVisible = false };
    private readonly SpeedItem _speed = new(L(Strings.RemoteSpeed)) { Help = Strings.RemoteSpeedHelp };
    private string? _credentialId;
    private string? _publicKey;
    private string? _pinned;
    private bool _filling;
    private bool _listing;

    public RemoteSourceDialog(EditorContext context, TaskSource source)
        : base(source.Kind == SourceKinds.Ssh ? Strings.RemoteEditTitle : Strings.RemoteAddTitle, 780, sizable: true)
    {
        _context = context;
        _original = source;
        _dbAll = new RadioItem(_dbChoice, Strings.DbChoiceAll, inline: true, on: true);
        _dbOnly = new RadioItem(_dbChoice, Strings.DbChoiceOnly, inline: true);
        _createKey = new ButtonItem("key", Strings.RemoteCreateKey, CreateKeyAsync) { Failed = ex => ShowError(ex.Message) };
        _copy = new ButtonItem("content_copy", Strings.ButtonCopy, CopyAsync, ButtonLook.Outlined);
        _saveBlock = new ButtonItem("save", Strings.ButtonSaveAs, SaveBlockAsync, ButtonLook.Outlined) { Failed = ex => ShowError(ex.Message) };
        _test = new ButtonItem("network_check", Strings.ButtonTestConnection, TestAsync) { Failed = ex => _testResult.Show(ex.Message, Tone.Error) };

        Items.Add(new RowItem(_host, _port));
        Items.Add(_user);
        Items.Add(_officeIp);
        Items.Add(_officeWarning);

        Items.Add(new HeadingItem(Strings.RemoteWhatHeading));
        Items.Add(_preset);
        Items.Add(_roots);
        Items.Add(_etcWarning);
        Items.Add(_excludes);
        Items.Add(new RowItem([new LabelItem(L(Strings.RemoteEngines)) { Width = 192 }, .. _engines]));

        // The databases to dump: the list fills in on «Проверить подключение» below.
        Items.Add(new HeadingItem(L(Strings.RemoteDbChoice), Strings.RemoteDbChoiceHelp));
        Items.Add(new RowItem(_dbAll, _dbOnly));
        Items.Add(_dbList);
        Items.Add(_dbListEmpty);

        Items.Add(new HeadingItem(Strings.RemoteKeyHeading));
        Items.Add(_key);
        Items.Add(_createKey);
        Items.Add(new NoteItem(Strings.RemoteBlockNote));
        Items.Add(_block);
        Items.Add(new RowItem(_copy, _saveBlock));

        Items.Add(new HeadingItem(Strings.RemoteCheckHeading));
        Items.Add(_hostKey);
        Items.Add(_test);
        Items.Add(_testResult);

        Items.Add(new HeadingItem(Strings.RemoteMoreHeading));
        Items.Add(new RowItem(_speed, new LabelItem(Strings.RemoteSpeedUnit)));
        Items.Add(new NoteItem(Strings.RemoteNextPages));

        Fill(source);
        _host.OnChange = UpdateBlock;
        _officeIp.OnChange = UpdateBlock;
        _user.OnChange = UpdateBlock;
        _preset.OnChange = PresetChosen;
        _roots.OnChange = ListsEdited;
        _excludes.OnChange = ListsEdited;
        foreach (var engine in _engines)
        {
            engine.OnChange = () =>
            {
                ListsEdited();
                EnginesChanged();
            };
        }

        _dbAll.OnChange = DbChoiceChanged;
    }

    /// <summary>The source as set here, after OK.</summary>
    public TaskSource Source { get; private set; } = new();

    /// <summary>The block shown for pasting (also read by the checks).</summary>
    internal string BlockText => _block.Text;

    /// <summary>Whether «Создать новый ключ» is offered.</summary>
    internal bool OffersNewKey => _createKey.IsVisible;

    protected override async Task<bool> OkAsync()
    {
        if (Problem() is { } problem)
        {
            ShowError(problem);
            return false;
        }

        if (_credentialId is null)
        {
            ShowError(Messages.ConfigSshKeyRequired);
            return false;
        }

        if (_dbOnly.On && _dbOnly.IsEnabled && !_dbList.Items.Any(i => i.On))
        {
            ShowError(Strings.ErrorDbNothingChosen);
            return false;
        }

        if (_pinned is null && !await Dialogs.Ask(this, Strings.RemoteSaveUnconfirmed, Strings.Yes, Strings.No))
        {
            return false;
        }

        Source = Build();
        return true;
    }

    /// <summary>Shows what a test found (also used by the pictures). True when the key of the server must be confirmed first.</summary>
    internal bool ShowTest(RemoteTestResult result)
    {
        if (result.HostKeyUnconfirmed)
        {
            _testResult.Show(result.Error is null ? Strings.RemoteKeyToConfirm : Strings.RemoteKeyChangedShort, Tone.Caution);
            return true;
        }

        if (!result.Ok)
        {
            _testResult.Show(Strings.Format(Strings.RemoteTestFailed, result.Error), Tone.Error);
            return false;
        }

        var lines = new List<string>
        {
            Strings.Format(Strings.RemoteTestOk, result.HelperVersion, result.Os, Describe.Number(result.Files), Sizes.Format(result.Bytes),
                result.ServerFreeBytes is { } free ? Sizes.Format(free) : "?"),
        };
        foreach (var databases in result.Databases)
        {
            lines.Add(DbEngines.Title(databases.Engine) + ": " + (databases.Error
                ?? (databases.Names.Count == 0 ? Strings.RemoteTestNoDatabases : string.Join(", ", databases.Names))));
        }

        // The databases of the server join the list: ticked under «Все базы», for ticking under «Только выбранные».
        var rows = DbRows();
        var known = rows.Select(r => r.Item.Key).ToHashSet(StringComparer.Ordinal);
        var engines = ChosenEngines();
        foreach (var databases in result.Databases.Where(d => d.Error is null && engines.Contains(d.Engine)))
        {
            rows.AddRange(databases.Names.Where(n => known.Add(RemoteDatabaseChoice.Key(databases.Engine, n)))
                .Select(n => (new DbItem(databases.Engine, n), _dbAll.On)));
        }

        ShowDatabases(rows);

        if (result.HelperOutdated)
        {
            lines.Add(Strings.Format(Strings.RemoteTestOutdated, result.HelperVersion, HelperScript.Version));
        }

        if (result.Warnings is { } warnings)
        {
            lines.Add(Strings.Format(Strings.RemoteTestWarnings, warnings));
        }

        _testResult.Show(string.Join(Environment.NewLine, lines), result.HelperOutdated || result.Warnings is not null ? Tone.Caution : Tone.Ok);
        return false;
    }

    /// <summary>Pins a key of the server, as after a confirmation (also used by the pictures).</summary>
    internal void Pin(string? hostKey)
    {
        _pinned = hostKey;
        _hostKey.Show(hostKey is null ? Strings.RemoteHostKeyNone : Strings.Format(Strings.RemoteHostKeyPinned, hostKey), hostKey is null ? Tone.Caution : Tone.Plain);
    }

    /// <summary>Takes a key made by the service (also used by the pictures).</summary>
    internal void UseKey(Credential credential)
    {
        _credentialId = credential.Id;
        _publicKey = credential.PublicKey;

        // A task imported from a file without keys keeps the account, but the private key stayed on the old computer:
        // a new key is made here, and its line goes to the server.
        bool missing = !credential.HasSecret;
        _key.Show(L(Strings.RemoteKey) + ": " + Strings.Format(missing ? Strings.RemoteKeyNotHere : Strings.RemoteKeyIs, credential.DisplayName),
            missing ? Tone.Caution : Tone.Plain);
        _createKey.Text = Strings.RemoteCreateNewKey;
        _createKey.IsVisible = missing;
        UpdateBlock();
    }

    private static string L(string label) => label.TrimEnd(':', ' ');

    private static List<string> Split(string text) =>
        [.. text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];

    private HashSet<string> ChosenEngines() => [.. _engines.Where(e => e.On).Select(e => (string)e.Tag!)];

    private void Fill(TaskSource source)
    {
        _filling = true;
        _host.Load(source.Host);
        _port.Value = Math.Clamp(source.Port ?? 22, 1, 65535);
        _user.Load(source.User ?? "custos");
        _officeIp.Load(source.OfficeIp);
        _speed.Kbps = source.SpeedLimitKbps ?? 0;
        string preset = source.Preset is { } p && Presets.Contains(p) ? p : RemotePresets.IspmanagerSites;
        _preset.Load(Array.IndexOf(Presets, preset));
        ShowSettings(source.Helper ?? HelperSetup.Preset(preset));
        _filling = false;

        Pin(source.HostKeySha256);
        if (_context.FindCredential(source.CredentialId) is { Kind: CredentialKinds.SshKey } credential)
        {
            UseKey(credential);
        }
        else
        {
            _key.Show(L(Strings.RemoteKey) + ": " + Strings.RemoteKeyNone, Tone.Plain);
            UpdateBlock();
        }

        var choice = source.Databases;
        (choice?.AllDatabases ?? true ? _dbAll : _dbOnly).Load();
        List<string> keys = choice is null ? [] : choice.AllDatabases ? choice.Exclude : choice.Include;
        ShowDatabases([.. keys.Select(k => RemoteDatabaseChoice.TrySplit(k, out string engine, out string name) ? new DbItem(engine, name) : null)
            .OfType<DbItem>().Select(i => (i, !_dbAll.On))]);
    }

    private void ShowSettings(RemoteHelperSettings settings)
    {
        bool filling = _filling;
        _filling = true;
        _roots.Load(string.Join(Environment.NewLine, settings.Roots));
        _excludes.Load(string.Join(Environment.NewLine, settings.Excludes));
        foreach (var engine in _engines)
        {
            engine.Load(settings.DbEngines.Contains((string)engine.Tag!, StringComparer.Ordinal));
        }

        _filling = filling;
        UpdateBlock();
    }

    private RemoteHelperSettings Settings() => new()
    {
        Roots = Split(_roots.Text),
        Excludes = Split(_excludes.Text),
        DbEngines = [.. _engines.Where(e => e.On).Select(e => (string)e.Tag!)],
        DbExclude = [.. _original.Helper?.DbExclude ?? []],
        DbDefaultsFile = _original.Helper?.DbDefaultsFile,
        ExtensionData = _original.Helper?.ExtensionData,
    };

    /// <summary>The preset the lists match, or <see cref="RemotePresets.Custom"/>.</summary>
    private string PresetOf(RemoteHelperSettings settings)
    {
        string chosen = Presets[Math.Max(0, _preset.Selected)];
        var preset = HelperSetup.Preset(chosen);
        bool same = chosen != RemotePresets.Custom
            && preset.Roots.SequenceEqual(settings.Roots, StringComparer.Ordinal)
            && preset.Excludes.SequenceEqual(settings.Excludes, StringComparer.Ordinal)
            && preset.DbEngines.Order(StringComparer.Ordinal).SequenceEqual(settings.DbEngines.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            && settings.DbExclude.Count == 0;
        return same ? chosen : RemotePresets.Custom;
    }

    private void PresetChosen()
    {
        if (_filling)
        {
            return;
        }

        string preset = Presets[Math.Max(0, _preset.Selected)];
        if (preset != RemotePresets.Custom)
        {
            ShowSettings(HelperSetup.Preset(preset));
            EnginesChanged();
        }
    }

    private void ListsEdited()
    {
        if (_filling)
        {
            return;
        }

        int custom = Array.IndexOf(Presets, RemotePresets.Custom);
        if (PresetOf(Settings()) == RemotePresets.Custom && _preset.Selected != custom)
        {
            _preset.Load(custom);
        }

        UpdateBlock();
    }

    private string? Problem()
    {
        string host = _host.Text.Trim();
        if (host.Length == 0 || host.Any(c => char.IsWhiteSpace(c) || c is '@' or '/' or '\\'))
        {
            return Messages.ConfigSshHostRequired;
        }

        if (!_port.IsValid)
        {
            return _port.Range();
        }

        if (string.IsNullOrWhiteSpace(_user.Text))
        {
            return Messages.ConfigUserRequired;
        }

        if (!HelperSetup.IsValidFrom(_officeIp.Text))
        {
            return Messages.Format(Messages.ConfigSshBadOfficeIp, _officeIp.Text.Trim());
        }

        return !_speed.IsValid ? _speed.Range() : HelperSetup.Problem(Settings());
    }

    private void UpdateBlock()
    {
        if (_filling)
        {
            return;
        }

        var settings = Settings();
        _officeWarning.IsVisible = string.IsNullOrWhiteSpace(_officeIp.Text);
        _etcWarning.IsVisible = HelperSetup.TakesWholeEtc(settings);
        string? problem = Problem();
        bool ready = problem is null && _publicKey is not null;
        _block.Load(_publicKey is null ? Strings.RemoteBlockNeedsKey
            : problem ?? HelperSetup.InstallBlock(settings, PresetOf(settings), _publicKey, string.IsNullOrWhiteSpace(_officeIp.Text) ? null : _officeIp.Text.Trim())
                .ReplaceLineEndings(Environment.NewLine));
        _copy.IsEnabled = ready;
        _saveBlock.IsEnabled = ready;
        _test.IsEnabled = _credentialId is not null && !string.IsNullOrWhiteSpace(_host.Text);
    }

    private async Task CreateKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(_host.Text))
        {
            _host.Error = Messages.ConfigSshHostRequired;
            return;
        }

        var result = await _context.Monitor.RequestAsync(PipeProtocol.Methods.RemoteCreateKey,
            new RemoteKeyRequest { Host = _host.Text.Trim(), User = _user.Text.Trim() },
            IpcJsonContext.Default.RemoteKeyRequest, IpcJsonContext.Default.CredentialSaveResult);
        _context.RememberCredential(result.Credential);
        UseKey(result.Credential);
    }

    private async Task CopyAsync()
    {
        await CopyAsync(this, _block.Text);
        _testResult.Show(Strings.RemoteCopied, Tone.Plain);
    }

    private async Task SaveBlockAsync()
    {
        if (await PickSaveFileAsync(this, Strings.ButtonSaveAs.TrimEnd('…', '.'), "custos-helper-install.sh", "sh",
                EventDialog.Filter(Strings.RemoteBlockFilter)) is not { } path)
        {
            return;
        }

        try
        {
            // The block as it goes to the server: lines end with LF.
            await File.WriteAllTextAsync(path, _block.Text.ReplaceLineEndings("\n"), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(ex.Message);
        }
    }

    private async Task TestAsync()
    {
        if (Problem() is { } problem)
        {
            _testResult.Show(problem, Tone.Error);
            return;
        }

        _testResult.Show(Strings.TestRunning, Tone.Plain);
        try
        {
            var result = await Ask();
            if (ShowTest(result) && result.HostKeySha256 is { } seen && await ConfirmAsync(result, seen))
            {
                Pin(seen);
                _testResult.Show(Strings.TestRunning, Tone.Plain);
                ShowTest(await Ask());
            }
        }
        catch (ServiceUnavailableException ex)
        {
            _testResult.Show(ex.Message, Tone.Error);
        }

        Task<RemoteTestResult> Ask() => _context.Monitor.RequestAsync(PipeProtocol.Methods.RemoteTest, new RemoteTestRequest { Source = Build() },
            IpcJsonContext.Default.RemoteTestRequest, IpcJsonContext.Default.RemoteTestResult, TimeSpan.FromMinutes(11));
    }

    /// <summary>Shows the key of the server and asks whether to trust it; a changed key is a warning.</summary>
    private Task<bool> ConfirmAsync(RemoteTestResult result, string seen)
    {
        string text = result.Error is { } changed
            ? Strings.Format(Strings.RemoteKeyChanged, changed)
            : Strings.Format(Strings.RemoteKeyConfirm, _host.Text.Trim(), seen);
        return Dialogs.Ask(this, text, Strings.Yes, Strings.No);
    }

    private TaskSource Build()
    {
        var settings = Settings();
        var source = new TaskSource
        {
            Id = _original.Id,
            Kind = SourceKinds.Ssh,
            Host = _host.Text.Trim(),
            Port = _original.Port is null && _port.Value == 22 ? null : _port.Value,
            User = _user.Text.Trim(),
            CredentialId = _credentialId,
            HostKeySha256 = _pinned,
            Preset = PresetOf(settings),
            OfficeIp = string.IsNullOrWhiteSpace(_officeIp.Text) ? null : _officeIp.Text.Trim(),
            SpeedLimitKbps = _speed.Kbps > 0 ? _speed.Kbps : null,
            Helper = settings,
            Databases = DbChoice(settings),
            Recursive = _original.Recursive,
            ExtensionData = _original.ExtensionData,
        };
        source.Path = BackupEngine.RemoteLabel(source);
        return source;
    }

    /// <summary>The databases in the list, of the engines still chosen, with their ticks.</summary>
    private List<(DbItem Item, bool Ticked)> DbRows()
    {
        var engines = ChosenEngines();
        return [.. _dbList.Items.Select(i => ((DbItem)i.Tag!, i.On)).Where(r => engines.Contains(r.Item1.Engine))];
    }

    /// <summary>Fills the list; the engine is named only when there are several.</summary>
    private void ShowDatabases(List<(DbItem Item, bool Ticked)> rows)
    {
        bool several = rows.Select(r => r.Item.Engine).Distinct(StringComparer.Ordinal).Count() > 1;
        _listing = true;
        _dbList.Items.Clear();
        foreach (var (item, ticked) in rows)
        {
            var box = new CheckItem(several ? DbEngines.Title(item.Engine) + ": " + item.Name : item.Name, ticked) { Tag = item };
            box.OnChange = () =>
            {
                // Unticking a database under «Все базы» means a choice of databases: «Только выбранные».
                if (!_listing && _dbAll.On && !box.On)
                {
                    _dbOnly.On = true;
                }
            };
            _dbList.Items.Add(box);
        }

        _listing = false;
        bool any = _engines.Any(e => e.On);
        _dbAll.IsEnabled = any;
        _dbOnly.IsEnabled = any;
        _dbList.IsEnabled = any;
        _dbList.IsVisible = rows.Count > 0;
        _dbListEmpty.IsVisible = any && rows.Count == 0;
    }

    /// <summary>Databases of an engine no longer chosen leave the list.</summary>
    private void EnginesChanged()
    {
        if (!_filling)
        {
            ShowDatabases(DbRows());
        }
    }

    /// <summary>«Все базы» ticks every database; «Только выбранные» keeps the ticks as they are.</summary>
    private void DbChoiceChanged()
    {
        if (!_dbAll.On)
        {
            return;
        }

        _listing = true;
        foreach (var item in _dbList.Items)
        {
            item.On = true;
        }

        _listing = false;
    }

    /// <summary>The choice for the task: null for all databases (or no engines), else the unticked or the ticked ones.</summary>
    private RemoteDatabaseChoice? DbChoice(RemoteHelperSettings settings)
    {
        if (settings.DbEngines.Count == 0)
        {
            return null;
        }

        var rows = DbRows();
        var extension = _original.Databases?.ExtensionData;
        if (_dbAll.On)
        {
            var left = rows.Where(r => !r.Ticked).Select(r => r.Item.Key).ToList();
            return left.Count == 0 && extension is not { Count: > 0 } ? null
                : new RemoteDatabaseChoice { Exclude = left, ExtensionData = extension };
        }

        return new RemoteDatabaseChoice
        {
            AllDatabases = false,
            Include = [.. rows.Where(r => r.Ticked).Select(r => r.Item.Key)],
            ExtensionData = extension,
        };
    }

    /// <summary>A database in the list.</summary>
    private sealed record DbItem(string Engine, string Name)
    {
        public string Key => RemoteDatabaseChoice.Key(Engine, Name);
    }
}
