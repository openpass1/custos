using System.Globalization;
using Custos.Core;
using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Remote;
using Custos.Core.Text;

namespace Custos.Gui;

/// <summary>
/// The wizard of a Linux server as source (CUSTOS-remote-pull.md 4.2): where the server is, from where the key may
/// log on, what custos-helper gives out, the key and the block to paste into a root session there, the check of the
/// connection that pins the key of the server, and the databases to dump, ticked from what the check found. The other
/// pages of the editor do the rest.
/// </summary>
internal sealed class RemoteSourceDialog : DialogForm
{
    private static readonly string[] Presets = [RemotePresets.IspmanagerSites, RemotePresets.IspmanagerFull, RemotePresets.Linux, RemotePresets.Custom];
    private static readonly string[] Engines = [DbEngines.MariaDb, DbEngines.MySql, DbEngines.PostgreSql];

    private readonly EditorContext _context;
    private readonly TaskSource _original;
    private readonly TextBox _host = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 22, Width = 90 };
    private readonly TextBox _user = new() { Dock = DockStyle.Fill, Text = "custos" };
    private readonly TextBox _officeIp = new() { Dock = DockStyle.Fill };
    private readonly Label _officeWarning = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Glyphs.WarningText, Margin = new Padding(0, 0, 0, 6), Text = Strings.RemoteOfficeEmpty };
    private readonly ComboBox _preset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly TextBox _roots = Lines(86);
    private readonly TextBox _excludes = Lines(70);
    private readonly CheckBox[] _engines = [.. Engines.Select(e => new CheckBox { Text = DbEngines.Title(e), AutoSize = true, Tag = e })];
    private readonly Label _etcWarning = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Glyphs.WarningText, Margin = new Padding(0, 0, 0, 6), Text = Strings.RemoteEtcWarning };
    private readonly NumericUpDown _speed = new() { Minimum = 0, Maximum = 10_000_000, Width = 110, ThousandsSeparator = true };
    private readonly Label _key = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
    private readonly Button _createKey = new() { Text = Strings.RemoteCreateKey, AutoSize = true };
    private readonly TextBox _block = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        WordWrap = false,
        ScrollBars = ScrollBars.Both,
        Height = 130,
        Font = new Font(FontFamily.GenericMonospace, 8.5F),
    };

    private readonly Button _copy = new() { Text = Strings.ButtonCopy, AutoSize = true, Margin = new Padding(0) };
    private readonly Button _saveBlock = new() { Text = Strings.ButtonSaveAs, AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
    private readonly Label _hostKey = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
    private readonly Button _test = new() { Text = Strings.ButtonTestConnection, AutoSize = true };
    private readonly Label _testResult = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(8, 6, 0, 0) };
    private readonly RadioButton _dbAll = new() { Text = Strings.DbChoiceAll, AutoSize = true, Checked = true, Margin = new Padding(0, 3, 16, 0) };
    private readonly RadioButton _dbOnly = new() { Text = Strings.DbChoiceOnly, AutoSize = true, Margin = new Padding(0, 3, 0, 0) };
    private readonly CheckedListBox _dbList = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = true };
    private readonly Label _dbListEmpty = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 6), Text = Strings.RemoteDbListEmpty };
    private string? _credentialId;
    private string? _publicKey;
    private string? _pinned;
    private bool _filling;
    private bool _listing;

    public RemoteSourceDialog(EditorContext context, TaskSource source)
        : base(source.Kind == SourceKinds.Ssh ? Strings.RemoteEditTitle : Strings.RemoteAddTitle, new Size(760, 720))
    {
        _context = context;
        _original = source;

        // A long window: it may be made larger, up to the whole screen.
        MakeSizable(new Size(640, 480));
        foreach (string preset in Presets)
        {
            _preset.Items.Add(PresetTitle(preset));
        }

        var where = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        where.Controls.AddRange([_port, new Label { Text = Strings.RemoteUser, AutoSize = true, Margin = new Padding(16, 6, 6, 0) }, _user]);
        _user.Width = 140;
        _user.Dock = DockStyle.None;
        AddRow(Strings.RemoteHost, _host, Ui.HelpIcon(Tips, Strings.RemoteHostHelp));
        AddRow(Strings.RemotePort, where);
        AddRow(Strings.RemoteOfficeIp, _officeIp, Ui.HelpIcon(Tips, Strings.RemoteOfficeIpHelp));
        AddWide(_officeWarning);

        AddHeading(Strings.RemoteWhatHeading);
        AddRow(Strings.RemotePreset, _preset);
        AddRow(Strings.RemoteRoots, _roots, Ui.HelpIcon(Tips, Strings.RemoteRootsHelp));
        AddWide(_etcWarning);
        AddRow(Strings.RemoteExcludes, _excludes, Ui.HelpIcon(Tips, Strings.RemoteExcludesHelp));
        var engines = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        engines.Controls.AddRange(_engines);
        AddRow(Strings.RemoteEngines, engines);

        // The databases to dump: the list fills in on «Test connection» at the bottom; four lines are seen at once.
        var choice = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        choice.Controls.AddRange([_dbAll, _dbOnly]);
        AddRow(Strings.RemoteDbChoice, choice, Ui.HelpIcon(Tips, Strings.RemoteDbChoiceHelp));
        _dbList.Height = (_dbList.ItemHeight * 4) + 6;
        AddRow(string.Empty, _dbList);
        Body.Controls.Add(new Label { AutoSize = true });
        Body.Controls.Add(_dbListEmpty);
        Body.SetColumnSpan(_dbListEmpty, 2);

        AddHeading(Strings.RemoteKeyHeading);
        AddRow(Strings.RemoteKey, _key, _createKey);
        AddNote(Strings.RemoteBlockNote);
        AddWide(_block);
        var blockButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 6) };
        blockButtons.Controls.AddRange([_copy, _saveBlock]);
        AddWide(blockButtons);

        AddHeading(Strings.RemoteCheckHeading);
        AddWide(_hostKey);
        var test = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        test.Controls.AddRange([_test, _testResult]);
        AddWide(test);

        AddHeading(Strings.RemoteMoreHeading);
        var speed = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        speed.Controls.AddRange([_speed, new Label { Text = Strings.RemoteSpeedUnit, AutoSize = true, Margin = new Padding(6, 6, 0, 0) }]);
        AddRow(Strings.RemoteSpeed, speed, Ui.HelpIcon(Tips, Strings.RemoteSpeedHelp));
        AddNote(Strings.RemoteNextPages);
        EndBuild();

        Fill(source);
        _host.TextChanged += (_, _) => UpdateBlock();
        _officeIp.TextChanged += (_, _) => UpdateBlock();
        _preset.SelectedIndexChanged += (_, _) => PresetChosen();
        _roots.TextChanged += (_, _) => ListsEdited();
        _excludes.TextChanged += (_, _) => ListsEdited();
        foreach (var engine in _engines)
        {
            engine.CheckedChanged += (_, _) =>
            {
                ListsEdited();
                EnginesChanged();
            };
        }

        _dbAll.CheckedChanged += (_, _) => DbChoiceChanged();
        _dbList.ItemCheck += (_, e) => DatabaseTicked(e);

        _createKey.Click += (_, _) => CreateKey();
        _copy.Click += (_, _) => CopyBlock();
        _saveBlock.Click += (_, _) => SaveBlock();
        _test.Click += (_, _) => TestConnection();
    }

    /// <summary>The source as set here (after OK).</summary>
    public TaskSource Source { get; private set; } = new();

    /// <summary>The block shown for pasting (also read by the window tests).</summary>
    internal string BlockText => _block.Text;

    internal static string PresetTitle(string? preset) => preset switch
    {
        RemotePresets.IspmanagerSites => Strings.RemotePresetIspSites,
        RemotePresets.IspmanagerFull => Strings.RemotePresetIspFull,
        RemotePresets.Linux => Strings.RemotePresetLinux,
        _ => Strings.RemotePresetCustom,
    };

    /// <summary>"all", "all but: old_shop" or "only: shop, blog" (task card).</summary>
    internal static string DescribeDatabases(RemoteDatabaseChoice? choice) => choice switch
    {
        { AllDatabases: false } => Strings.Format(Strings.DbOnly, RemoteDatabaseChoice.Describe(choice.Include)),
        { Exclude.Count: > 0 } => Strings.Format(Strings.DbAllExcept, RemoteDatabaseChoice.Describe(choice.Exclude)),
        _ => Strings.DbAll,
    };

    protected override void OnOk()
    {
        if (Problem() is { } problem)
        {
            Ui.ShowError(this, problem);
            return;
        }

        if (_credentialId is null)
        {
            Ui.ShowError(this, Messages.ConfigSshKeyRequired);
            return;
        }

        if (_dbOnly.Checked && _dbOnly.Enabled && _dbList.CheckedItems.Count == 0)
        {
            Ui.ShowError(this, Strings.ErrorDbNothingChosen);
            return;
        }

        if (_pinned is null && MessageBox.Show(this, Strings.RemoteSaveUnconfirmed, Strings.AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        Source = Build();
        DialogResult = DialogResult.OK;
    }

    /// <summary>Shows what a test found (also used by the window tests). True when the key of the server must be
    /// confirmed first.</summary>
    internal bool ShowTest(RemoteTestResult result)
    {
        if (result.HostKeyUnconfirmed)
        {
            _testResult.ForeColor = Glyphs.WarningText;
            _testResult.Text = result.Error is null ? Strings.RemoteKeyToConfirm : Strings.RemoteKeyChangedShort;
            return true;
        }

        if (!result.Ok)
        {
            _testResult.ForeColor = Glyphs.ErrorText;
            _testResult.Text = Strings.Format(Strings.RemoteTestFailed, result.Error);
            return false;
        }

        var lines = new List<string>
        {
            Strings.Format(Strings.RemoteTestOk, result.HelperVersion, result.Os, Ui.Number(result.Files), Sizes.Format(result.Bytes),
                result.ServerFreeBytes is { } free ? Sizes.Format(free) : "?"),
        };
        foreach (var databases in result.Databases)
        {
            lines.Add(DbEngines.Title(databases.Engine) + ": " + (databases.Error
                ?? (databases.Names.Count == 0 ? Strings.RemoteTestNoDatabases : string.Join(", ", databases.Names))));
        }

        // The databases of the server join the list: ticked under «All databases», for ticking under «Selected databases».
        var rows = DbRows();
        var known = rows.Select(r => r.Item.Key).ToHashSet(StringComparer.Ordinal);
        var engines = _engines.Where(e => e.Checked).Select(e => (string)e.Tag!).ToHashSet(StringComparer.Ordinal);
        foreach (var databases in result.Databases.Where(d => d.Error is null && engines.Contains(d.Engine)))
        {
            rows.AddRange(databases.Names.Where(n => known.Add(RemoteDatabaseChoice.Key(databases.Engine, n)))
                .Select(n => (new DbItem(databases.Engine, n), _dbAll.Checked)));
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

        _testResult.ForeColor = result.HelperOutdated || result.Warnings is not null ? Glyphs.WarningText : Glyphs.Green;
        _testResult.Text = string.Join(Environment.NewLine, lines);
        return false;
    }

    /// <summary>Pins a key of the server, as after a confirmation (also used by the window tests).</summary>
    internal void Pin(string? hostKey)
    {
        _pinned = hostKey;
        _hostKey.ForeColor = hostKey is null ? Glyphs.WarningText : SystemColors.ControlText;
        _hostKey.Text = hostKey is null ? Strings.RemoteHostKeyNone : Strings.Format(Strings.RemoteHostKeyPinned, hostKey);
    }

    /// <summary>Takes a key made by the service (also used by the window tests).</summary>
    internal void UseKey(Credential credential)
    {
        _credentialId = credential.Id;
        _publicKey = credential.PublicKey;

        // A task imported from a file without keys keeps the account, but the private key stayed on the old computer:
        // a new key is made here, and its line goes to the server.
        bool missing = !credential.HasSecret;
        _key.Text = Strings.Format(missing ? Strings.RemoteKeyNotHere : Strings.RemoteKeyIs, credential.DisplayName);
        _key.ForeColor = missing ? Glyphs.WarningText : SystemColors.ControlText;
        _createKey.Text = Strings.RemoteCreateNewKey;
        _createKey.Visible = missing;
        UpdateBlock();
    }

    /// <summary>Whether «Create new key» is offered (window tests).</summary>
    internal bool OffersNewKey => _createKey.Visible;

    private static TextBox Lines(int height) => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        AcceptsReturn = true,
        ScrollBars = ScrollBars.Vertical,
        Height = height,
    };

    private static List<string> Split(string text, char[] separators) =>
        [.. text.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];

    private void AddHeading(string text)
    {
        var heading = new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 12, 0, 4),
        };
        AddWide(heading);
    }

    private void Fill(TaskSource source)
    {
        _filling = true;
        _host.Text = source.Host ?? string.Empty;
        _port.Value = Math.Clamp(source.Port ?? 22, 1, 65535);
        _user.Text = source.User ?? "custos";
        _officeIp.Text = source.OfficeIp ?? string.Empty;
        _speed.Value = Math.Clamp(source.SpeedLimitKbps ?? 0, 0, (int)_speed.Maximum);
        string preset = source.Preset is { } p && Presets.Contains(p) ? p : RemotePresets.IspmanagerSites;
        _preset.SelectedIndex = Array.IndexOf(Presets, preset);
        ShowSettings(source.Helper ?? HelperSetup.Preset(preset));
        _filling = false;

        Pin(source.HostKeySha256);
        if (_context.FindCredential(source.CredentialId) is { Kind: CredentialKinds.SshKey } credential)
        {
            UseKey(credential);
        }
        else
        {
            _key.Text = Strings.RemoteKeyNone;
            UpdateBlock();
        }

        _testResult.Text = string.Empty;
        var choice = source.Databases;
        _dbAll.Checked = choice?.AllDatabases ?? true;
        _dbOnly.Checked = !_dbAll.Checked;
        List<string> keys = choice is null ? [] : choice.AllDatabases ? choice.Exclude : choice.Include;
        ShowDatabases([.. keys.Select(k => RemoteDatabaseChoice.TrySplit(k, out string engine, out string name) ? new DbItem(engine, name) : null)
            .OfType<DbItem>().Select(i => (i, !_dbAll.Checked))]);
    }

    private void ShowSettings(RemoteHelperSettings settings)
    {
        bool filling = _filling;
        _filling = true;
        _roots.Text = string.Join(Environment.NewLine, settings.Roots);
        _excludes.Text = string.Join(Environment.NewLine, settings.Excludes);
        foreach (var engine in _engines)
        {
            engine.Checked = settings.DbEngines.Contains((string)engine.Tag!, StringComparer.Ordinal);
        }

        _filling = filling;
        UpdateBlock();
    }

    private RemoteHelperSettings Settings() => new()
    {
        Roots = Split(_roots.Text, ['\r', '\n']),
        Excludes = Split(_excludes.Text, ['\r', '\n']),
        DbEngines = [.. _engines.Where(e => e.Checked).Select(e => (string)e.Tag!)],
        DbExclude = [.. _original.Helper?.DbExclude ?? []],
        DbDefaultsFile = _original.Helper?.DbDefaultsFile,
    };

    /// <summary>The preset the lists match, or <see cref="RemotePresets.Custom"/>.</summary>
    private string PresetOf(RemoteHelperSettings settings)
    {
        string chosen = Presets[Math.Max(0, _preset.SelectedIndex)];
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

        string preset = Presets[Math.Max(0, _preset.SelectedIndex)];
        if (preset != RemotePresets.Custom)
        {
            ShowSettings(HelperSetup.Preset(preset));
        }
    }

    private void ListsEdited()
    {
        if (_filling)
        {
            return;
        }

        if (PresetOf(Settings()) == RemotePresets.Custom && _preset.SelectedIndex != Array.IndexOf(Presets, RemotePresets.Custom))
        {
            _filling = true;
            _preset.SelectedIndex = Array.IndexOf(Presets, RemotePresets.Custom);
            _filling = false;
        }

        UpdateBlock();
    }

    private string? Problem()
    {
        if (string.IsNullOrWhiteSpace(_host.Text) || _host.Text.Trim().Any(c => char.IsWhiteSpace(c) || c is '@' or '/' or '\\'))
        {
            return Messages.ConfigSshHostRequired;
        }

        if (string.IsNullOrWhiteSpace(_user.Text))
        {
            return Messages.ConfigUserRequired;
        }

        if (!HelperSetup.IsValidFrom(_officeIp.Text))
        {
            return Messages.Format(Messages.ConfigSshBadOfficeIp, _officeIp.Text.Trim());
        }

        return HelperSetup.Problem(Settings());
    }

    private void UpdateBlock()
    {
        if (_filling)
        {
            return;
        }

        var settings = Settings();
        _officeWarning.Visible = string.IsNullOrWhiteSpace(_officeIp.Text);
        _etcWarning.Visible = HelperSetup.TakesWholeEtc(settings);
        string? problem = Problem();
        bool ready = problem is null && _publicKey is not null;
        _block.Text = _publicKey is null ? Strings.RemoteBlockNeedsKey
            : problem ?? HelperSetup.InstallBlock(settings, PresetOf(settings), _publicKey, string.IsNullOrWhiteSpace(_officeIp.Text) ? null : _officeIp.Text.Trim())
                .ReplaceLineEndings(Environment.NewLine);
        _copy.Enabled = ready;
        _saveBlock.Enabled = ready;
        _test.Enabled = _credentialId is not null && !string.IsNullOrWhiteSpace(_host.Text);
    }

    /// <summary>The block as it goes to the server: lines end with LF.</summary>
    private string BlockForServer() => _block.Text.ReplaceLineEndings("\n");

    private void CreateKey()
    {
        if (string.IsNullOrWhiteSpace(_host.Text))
        {
            Ui.ShowError(this, Messages.ConfigSshHostRequired);
            return;
        }

        _createKey.Enabled = false;
        Ui.Run(this, async () =>
        {
            try
            {
                var result = await _context.Monitor.RequestAsync(PipeProtocol.Methods.RemoteCreateKey,
                    new RemoteKeyRequest { Host = _host.Text.Trim(), User = _user.Text.Trim() },
                    IpcJsonContext.Default.RemoteKeyRequest, IpcJsonContext.Default.CredentialSaveResult);
                _context.RememberCredential(result.Credential);
                UseKey(result.Credential);
            }
            finally
            {
                _createKey.Enabled = true;
            }
        });
    }

    private void CopyBlock()
    {
        Clipboard.SetText(_block.Text);
        _testResult.ForeColor = SystemColors.GrayText;
        _testResult.Text = Strings.RemoteCopied;
    }

    private void SaveBlock()
    {
        using var dialog = new SaveFileDialog
        {
            FileName = "custos-helper-install.sh",
            Filter = Strings.RemoteBlockFilter,
            DefaultExt = "sh",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, BlockForServer(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.ShowError(this, ex.Message);
        }
    }

    private void TestConnection()
    {
        if (Problem() is { } problem)
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
                if (ShowTest(result) && result.HostKeySha256 is { } seen && Confirm(result, seen))
                {
                    Pin(seen);
                    _testResult.ForeColor = SystemColors.GrayText;
                    _testResult.Text = Strings.TestRunning;
                    ShowTest(await Ask());
                }
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

        Task<RemoteTestResult> Ask() => _context.Monitor.RequestAsync(PipeProtocol.Methods.RemoteTest, new RemoteTestRequest { Source = Build() },
            IpcJsonContext.Default.RemoteTestRequest, IpcJsonContext.Default.RemoteTestResult, TimeSpan.FromMinutes(11));
    }

    /// <summary>Shows the key of the server and asks whether to trust it; a changed key is a warning.</summary>
    private bool Confirm(RemoteTestResult result, string seen)
    {
        string text = result.Error is { } changed
            ? Strings.Format(Strings.RemoteKeyChanged, changed)
            : Strings.Format(Strings.RemoteKeyConfirm, _host.Text.Trim(), seen);
        return MessageBox.Show(this, text, Strings.AppTitle, MessageBoxButtons.YesNo,
            result.Error is null ? MessageBoxIcon.Question : MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private TaskSource Build()
    {
        var settings = Settings();
        var source = new TaskSource
        {
            Id = _original.Id,
            Kind = SourceKinds.Ssh,
            Host = _host.Text.Trim(),
            Port = (int)_port.Value,
            User = _user.Text.Trim(),
            CredentialId = _credentialId,
            HostKeySha256 = _pinned,
            Preset = PresetOf(settings),
            OfficeIp = string.IsNullOrWhiteSpace(_officeIp.Text) ? null : _officeIp.Text.Trim(),
            SpeedLimitKbps = _speed.Value > 0 ? (int)_speed.Value : null,
            Helper = settings,
            Databases = DbChoice(settings),
        };
        source.Path = BackupEngine.RemoteLabel(source);
        return source;
    }

    /// <summary>The databases in the list, of the engines still chosen, with their ticks.</summary>
    private List<(DbItem Item, bool Ticked)> DbRows()
    {
        var engines = _engines.Where(e => e.Checked).Select(e => (string)e.Tag!).ToHashSet(StringComparer.Ordinal);
        return [.. _dbList.Items.Cast<DbItem>().Select((item, i) => (item, _dbList.GetItemChecked(i))).Where(r => engines.Contains(r.item.Engine))];
    }

    /// <summary>Fills the list; the engine is named only when there are several.</summary>
    private void ShowDatabases(List<(DbItem Item, bool Ticked)> rows)
    {
        bool several = rows.Select(r => r.Item.Engine).Distinct(StringComparer.Ordinal).Count() > 1;
        _listing = true;
        _dbList.BeginUpdate();
        _dbList.Items.Clear();
        foreach (var (item, ticked) in rows)
        {
            _dbList.Items.Add(item with { ShowEngine = several }, ticked);
        }

        _dbList.EndUpdate();
        _listing = false;
        bool any = _engines.Any(e => e.Checked);
        _dbAll.Enabled = any;
        _dbOnly.Enabled = any;
        _dbList.Enabled = any;
        _dbListEmpty.Visible = any && rows.Count == 0;
    }

    /// <summary>Databases of an engine no longer chosen leave the list.</summary>
    private void EnginesChanged()
    {
        if (!_filling)
        {
            ShowDatabases(DbRows());
        }
    }

    /// <summary>«All databases» ticks every database; «Selected databases» keeps the ticks as they are.</summary>
    private void DbChoiceChanged()
    {
        if (!_dbAll.Checked)
        {
            return;
        }

        for (int i = 0; i < _dbList.Items.Count; i++)
        {
            _dbList.SetItemChecked(i, true);
        }
    }

    /// <summary>Unticking a database under «All databases» means a choice of databases: «Selected databases».</summary>
    private void DatabaseTicked(ItemCheckEventArgs e)
    {
        if (!_listing && _dbAll.Checked && e.NewValue == CheckState.Unchecked)
        {
            _dbOnly.Checked = true;
        }
    }

    /// <summary>The choice for the task: null for all databases (or no engines), else the unticked or the ticked ones.</summary>
    private RemoteDatabaseChoice? DbChoice(RemoteHelperSettings settings)
    {
        if (settings.DbEngines.Count == 0)
        {
            return null;
        }

        var rows = DbRows();
        if (_dbAll.Checked)
        {
            var left = rows.Where(r => !r.Ticked).Select(r => r.Item.Key).ToList();
            return left.Count == 0 ? null : new RemoteDatabaseChoice { Exclude = left };
        }

        return new RemoteDatabaseChoice { AllDatabases = false, Include = [.. rows.Where(r => r.Ticked).Select(r => r.Item.Key)] };
    }

    /// <summary>A database in the list.</summary>
    private sealed record DbItem(string Engine, string Name, bool ShowEngine = false)
    {
        public string Key => RemoteDatabaseChoice.Key(Engine, Name);

        public override string ToString() => ShowEngine ? DbEngines.Title(Engine) + ": " + Name : Name;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _block.Font.Dispose();
        }

        base.Dispose(disposing);
    }
}
