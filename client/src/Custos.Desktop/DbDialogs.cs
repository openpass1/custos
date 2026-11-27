using Custos.Core;
using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>
/// Adds or changes one database server of a task (section 6.2): its kind, the folder of its programs, where it
/// listens, the account, and which databases to dump. «Найти» lists the servers on this computer, «Проверить
/// подключение» lists the databases of the server (DbServerDialog of Custos.exe).
/// </summary>
internal sealed class DbServerDialog : FormDialog
{
    private static readonly string[] Engines = [DbEngines.MySql, DbEngines.MariaDb, DbEngines.PostgreSql];

    private readonly EditorContext _context;
    private readonly TaskDatabaseServer _original;
    private readonly Credential? _credential;
    private readonly ChoiceItem _engine = new(L(Strings.DbEngine), [.. Engines.Select(DbEngines.Title)], 0, 260);
    private readonly TextItem _binDir;
    private readonly TextItem _host = new(L(Strings.DbHost), width: 240) { Help = Strings.DbHostHelp };
    private readonly NumberItem _port = new(L(Strings.DbPort), 3306, 1, 65535, 80);
    private readonly TextItem _user = new(L(Strings.DbUser), width: 240);
    private readonly TextItem _password = new(L(Strings.DbPassword), width: 240) { Secret = true };
    private readonly CheckItem _noPassword = new(Strings.DbNoPassword);
    private readonly RadioGroup _choice = new();
    private readonly RadioItem _all;
    private readonly RadioItem _only;
    private readonly CheckListItem _databases = new(150);
    private readonly ButtonItem _test;
    private readonly NoteItem _testResult = new(string.Empty) { IsVisible = false };
    private string? _dataDir;
    private string _shownEngine;
    private bool _listing;

    public DbServerDialog(EditorContext context, TaskDatabaseServer server, bool isNew)
        : base(isNew ? Strings.DbServerAddTitle : Strings.DbServerEditTitle, 700, sizable: true)
    {
        _context = context;
        _original = server;
        _credential = context.FindCredential(server.CredentialId);
        _binDir = new TextItem(L(Strings.DbBinDir), width: 330)
        {
            ActionIcon = "folder_open",
            ActionTip = Strings.ButtonBrowse,
            Action = BrowseAsync,
        };
        var find = new ButtonItem("search", Strings.DbFind, FindAsync);
        _all = new RadioItem(_choice, Strings.DbChoiceAll);
        _only = new RadioItem(_choice, Strings.DbChoiceOnly);
        _test = new ButtonItem("network_check", Strings.ButtonTestConnection, TestAsync) { Failed = ex => _testResult.Show(ex.Message, Tone.Error) };

        Items.Add(_engine);
        Items.Add(new RowItem(_binDir, find));
        Items.Add(new NoteItem(CustosEdition.IsLinux ? DesktopStrings.LinuxDbBinDirHelp : Strings.DbBinDirHelp));
        Items.Add(new RowItem(_host, _port));
        Items.Add(_user);
        Items.Add(_password);
        Items.Add(_noPassword);
        Items.Add(new NoteItem(_credential is null ? Strings.DbAccountNote : Strings.DbAccountNote + " " + Strings.DbKeepPasswordNote));
        Items.Add(new RowItem(_all, _only));
        Items.Add(_databases);
        Items.Add(new NoteItem(Strings.DbListNote));
        Items.Add(_test);
        Items.Add(_testResult);

        int index = Array.IndexOf(Engines, server.Engine);
        _engine.Load(index >= 0 ? index : 0);
        _shownEngine = Engine;
        _binDir.Load(server.BinDir);
        _host.Load(server.Host);
        _port.Value = server.Port ?? DbEngines.DefaultPort(Engine);
        _user.Load(_credential?.User ?? DefaultUser(Engine));
        _noPassword.Load(_credential is { HasSecret: false });
        _dataDir = server.DataDir;
        (server.AllDatabases ? _all : _only).Load();

        // A tick always means "dumped": with «Все базы» the stored exclusions are the unticked ones.
        foreach (string name in server.AllDatabases ? server.Exclude : server.Include)
        {
            AddDatabase(name, !server.AllDatabases);
        }

        UpdatePassword();
        _all.OnChange = ChoiceChanged;
        _engine.OnChange = EngineChanged;
        _noPassword.OnChange = UpdatePassword;
        foreach (var field in new[] { _binDir, _host, _user, _password })
        {
            field.OnChange = () => _testResult.Show(null);
        }
    }

    /// <summary>The server as entered, after OK.</summary>
    public TaskDatabaseServer Server { get; private set; } = new();

    private string Engine => Engines[Math.Max(0, _engine.Selected)];

    /// <summary>The password to store: "" for none, null to keep the stored one.</summary>
    private string? Password => _noPassword.On ? string.Empty : _password.Text.Length > 0 ? _password.Text : null;

    protected override async Task<bool> OkAsync()
    {
        if (Check() is { } problem)
        {
            ShowError(problem);
            return false;
        }

        string? password = Password;
        if (_credential is null && password is null)
        {
            _password.Error = Strings.ErrorPasswordRequired;
            return false;
        }

        var credential = new Credential
        {
            Id = _credential?.Id ?? TaskDefinition.NewId(),
            Kind = CredentialKinds.Db,
            User = _user.Text.Trim(),
            ExtensionData = _credential?.ExtensionData,
        };
        var server = Build();
        var result = await _context.Monitor.RequestAsync(PipeProtocol.Methods.CredentialSave, new CredentialInput { Credential = credential, Password = password },
            IpcJsonContext.Default.CredentialInput, IpcJsonContext.Default.CredentialSaveResult);
        _context.RememberCredential(result.Credential);
        server.CredentialId = result.Credential.Id;
        Server = server;
        _password.Load(string.Empty);
        return true;
    }

    /// <summary>Shows what the test found; the databases of the server join the list (also used by the pictures).</summary>
    internal void ShowTest(DbTestResult result)
    {
        if (!result.Ok)
        {
            _testResult.Show(Strings.Format(Strings.DbTestFailed, result.Error), Tone.Error);
            return;
        }

        _dataDir = result.DataDir;
        var present = _databases.Items.Select(i => (string)i.Tag!).ToHashSet(StringComparer.Ordinal);
        foreach (string name in result.Databases.Where(n => !present.Contains(n)))
        {
            AddDatabase(name, _all.On);
        }

        _testResult.Show(Strings.Format(Strings.DbTestOk, result.Version, result.Databases.Count)
            + (result.DataDir is { } dataDir ? Environment.NewLine + Strings.Format(Strings.DbTestDataDir, dataDir) : string.Empty), Tone.Ok);
    }

    private static string L(string label) => label.TrimEnd(':', ' ');

    private static string DefaultUser(string engine) => engine == DbEngines.PostgreSql ? "postgres" : "root";

    /// <summary>What is wrong with the fields, in words, or null.</summary>
    private string? Check()
    {
        return _binDir.Value.Length == 0 ? Strings.ErrorDbBinDirRequired
            : !(CustosEdition.IsLinux ? LinuxEditorPaths.IsLocal(_binDir.Value) : PathVariables.LooksAbsolute(_binDir.Value))
                ? Strings.Format(CustosEdition.IsLinux ? DesktopStrings.LinuxErrorLocalPath : Strings.ErrorPathNotAbsolute, _binDir.Value)
            : string.IsNullOrWhiteSpace(_host.Text) ? Strings.ErrorDbHostRequired
            : !_port.IsValid ? _port.Range()
            : string.IsNullOrWhiteSpace(_user.Text) ? Strings.ErrorUserRequired
            : _only.On && !_databases.Items.Any(i => i.On) ? Strings.ErrorDbNothingChosen
            : null;
    }

    /// <summary>The server from the fields; the account is set on OK.</summary>
    private TaskDatabaseServer Build()
    {
        var chosen = _databases.Items.Where(i => i.On).Select(i => (string)i.Tag!).ToList();
        var left = _databases.Items.Where(i => !i.On).Select(i => (string)i.Tag!).ToList();
        return new TaskDatabaseServer
        {
            Id = _original.Id,
            Engine = Engine,
            BinDir = _binDir.Value,
            Host = _host.Text.Trim(),
            Port = _port.Value,
            CredentialId = _original.CredentialId,
            AllDatabases = _all.On,
            Exclude = _all.On ? left : [],
            Include = _all.On ? [] : chosen,
            DataDir = _dataDir,
            ExtensionData = _original.ExtensionData,
        };
    }

    private void AddDatabase(string name, bool ticked)
    {
        var item = new CheckItem(name, ticked) { Tag = name };
        item.OnChange = () =>
        {
            // Unticking a database under «Все базы» means a choice of databases: «Только выбранные».
            if (!_listing && _all.On && !item.On)
            {
                _only.On = true;
            }
        };
        _databases.Items.Add(item);
    }

    /// <summary>«Все базы» ticks every database; «Только выбранные» keeps the ticks as they are.</summary>
    private void ChoiceChanged()
    {
        if (!_all.On)
        {
            return;
        }

        _listing = true;
        foreach (var item in _databases.Items)
        {
            item.On = true;
        }

        _listing = false;
    }

    private void UpdatePassword()
    {
        _password.IsEnabled = !_noPassword.On;
        if (_noPassword.On)
        {
            _password.Load(string.Empty);
        }
    }

    /// <summary>The port and user follow the kind of server while they still have the usual values of the old one.</summary>
    private void EngineChanged()
    {
        string old = _shownEngine;
        _shownEngine = Engine;
        if (_port.Value == DbEngines.DefaultPort(old))
        {
            _port.Value = DbEngines.DefaultPort(Engine);
        }

        if (_user.Text.Trim() == DefaultUser(old))
        {
            _user.Load(DefaultUser(Engine));
        }

        _testResult.Show(null);
    }

    private async Task BrowseAsync()
    {
        if (await PickFolderAsync(this, L(Strings.DbBinDir), _binDir.Value) is { } folder)
        {
            _binDir.Text = folder;
        }
    }

    private async Task FindAsync()
    {
        var dialog = new DbFindDialog(_context.Monitor);
        if (!await dialog.ShowAsync(this) || dialog.Selected is not { } found)
        {
            return;
        }

        int index = Array.IndexOf(Engines, found.Engine);
        if (index >= 0)
        {
            _engine.Selected = index;
        }

        _binDir.Text = found.BinDir;
        _host.Text = found.Host;
        _port.Value = Math.Clamp(found.Port ?? DbEngines.DefaultPort(found.Engine), 1, 65535);
    }

    private async Task TestAsync()
    {
        if (Check() is { } problem)
        {
            _testResult.Show(problem, Tone.Error);
            return;
        }

        var account = new CredentialInput
        {
            Credential = new Credential { Id = _credential?.Id ?? TaskDefinition.NewId(), Kind = CredentialKinds.Db, User = _user.Text.Trim() },
            Password = Password,
        };
        _testResult.Show(Strings.TestRunning, Tone.Plain);
        try
        {
            var result = await _context.Monitor.RequestAsync(PipeProtocol.Methods.DatabasesTest, new DbTestRequest
            {
                Server = Build(),
                Credential = account,
                TaskName = _context.TaskName,
                ProfileSid = _context.ProfileSid,
            },
                IpcJsonContext.Default.DbTestRequest, IpcJsonContext.Default.DbTestResult, TimeSpan.FromMinutes(2));
            ShowTest(result);
        }
        catch (ServiceUnavailableException ex)
        {
            _testResult.Show(ex.Message, Tone.Error);
        }
    }
}

/// <summary>«Найти» of <see cref="DbServerDialog"/>: the database servers and the folders of their programs on this computer.</summary>
internal sealed class DbFindDialog : FormDialog
{
    private readonly ServiceMonitor _monitor;
    private readonly RadioGroup _found = new();
    private readonly NoteItem _status = new(Strings.DbFindRunning);
    private readonly int _first;

    public DbFindDialog(ServiceMonitor monitor)
        : base(Strings.DbFindTitle, 720)
    {
        _monitor = monitor;
        Items.Add(new NoteItem(Strings.DbFindNote));
        Items.Add(_status);
        _first = Items.Count;
        OkButton.IsEnabled = false;
        Opened += (_, _) => Safe.Run(SearchAsync, ex => _status.Show(ex.Message, Tone.Error));
    }

    /// <summary>The server picked, after OK.</summary>
    public DbCandidate? Selected { get; private set; }

    protected override Task<bool> OkAsync()
    {
        Selected = _found.Items.FirstOrDefault(i => i.On)?.Tag as DbCandidate;
        return Task.FromResult(Selected is not null);
    }

    /// <summary>Shows what the service found (also used by the pictures).</summary>
    internal void ShowFound(DbFindResult result)
    {
        while (Items.Count > _first)
        {
            Items.RemoveAt(_first);
        }

        foreach (var server in result.Servers)
        {
            string title = DbEngines.Title(server.Engine) + (server.Version is { Length: > 0 } version ? " " + version : string.Empty);
            string where = server.Running ? $"{title} — {server.Host}:{server.Port}" : title;
            var item = new RadioItem(_found, where, (server.Running ? Strings.DbRunning : Strings.DbNotRunning) + " · " + server.BinDir,
                on: Items.Count == _first) { Tag = server };
            Items.Add(item);
        }

        _status.Show(result.Servers.Count == 0 ? Strings.DbFindNothing : null, Tone.Plain);
        OkButton.IsEnabled = result.Servers.Count > 0;
    }

    private async Task SearchAsync()
    {
        _status.Show(Strings.DbFindRunning, Tone.Plain);
        var result = await _monitor.RequestAsync(PipeProtocol.Methods.DatabasesFind, EmptyRequest.Instance,
            IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.DbFindResult, TimeSpan.FromMinutes(2));
        ShowFound(result);
    }
}
