using Custos.Core.Cobian;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Text;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>
/// Import of Cobian Backup 11 tasks (section 9 of the spec): reads MainList.lst, shows every task with what to check,
/// asks once for the password of each "run as" account, and adds the chosen tasks switched off (CobianImportForm of
/// Custos.exe).
/// </summary>
internal sealed class CobianImportDialog : FormDialog
{
    private readonly ServiceMonitor _monitor;
    private readonly TextItem _file;
    private readonly NoteItem _found = new(string.Empty) { IsVisible = false };
    private readonly HeadingItem _accountsHeading = new(Strings.ImportAccounts) { IsVisible = false };
    private readonly List<CheckItem> _tasks = [];
    private readonly Dictionary<string, AccountRow> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private CobianImportResult? _result;

    public CobianImportDialog(ServiceMonitor monitor)
        : base(Strings.ImportTitle, 820, sizable: true)
    {
        _monitor = monitor;
        _file = new TextItem(L(Strings.ImportFile), GuessListFile() ?? string.Empty, 400)
        {
            ActionIcon = "folder_open",
            ActionTip = Strings.ButtonBrowse,
            Action = BrowseAsync,
        };
        Items.Add(new RowItem(_file, new ButtonItem("refresh", Strings.ImportRead, () =>
        {
            ReadList();
            return Task.CompletedTask;
        })));
        Items.Add(new NoteItem(Strings.ImportNote));
        Items.Add(_found);
        Items.Add(_accountsHeading);
        OkButton.Content = Strings.ImportButton;
        OkButton.IsEnabled = false;
        Opened += (_, _) =>
        {
            if (_file.Text.Length > 0)
            {
                ReadList();
            }
        };
    }

    /// <summary>The tasks read from the list, with their check boxes.</summary>
    internal IReadOnlyList<CheckItem> TaskItems => _tasks;

    /// <summary>Accounts whose password is asked for, one row each.</summary>
    internal int AccountCount => _accounts.Count;

    /// <summary>Reads a list as «Прочитать» does.</summary>
    internal void ReadFile(string path)
    {
        _file.Text = path;
        ReadList();
    }

    /// <summary>A remark of the importer in words.</summary>
    internal static string Remark(ImportWarning warning)
    {
        string argument = warning.Argument ?? string.Empty;
        return warning.Code switch
        {
            ImportWarningCodes.ScheduleUnknown => Strings.Format(Strings.WarnScheduleUnknown, argument),
            ImportWarningCodes.ScheduleNotSupported => Strings.WarnScheduleNotSupported,
            ImportWarningCodes.ScheduleAssumed => Strings.Format(Strings.WarnScheduleAssumed, argument),
            ImportWarningCodes.TimeUnreadable => Strings.WarnTimeUnreadable,
            ImportWarningCodes.WeekdaysAssumed => Strings.Format(Strings.WarnWeekdaysAssumed, argument),
            ImportWarningCodes.BackupTypeAssumed => Strings.WarnBackupTypeAssumed,
            ImportWarningCodes.BackupTypeNotSupported => Strings.Format(Strings.WarnBackupTypeNotSupported, argument),
            ImportWarningCodes.CompressionAssumed => Strings.WarnCompressionAssumed,
            ImportWarningCodes.EncryptionPassword => Strings.WarnEncryptionPassword,
            ImportWarningCodes.SplitIgnored => Strings.WarnSplitIgnored,
            ImportWarningCodes.EventsInComment => Strings.Format(Strings.WarnEventsInComment, argument),
            ImportWarningCodes.SourceUnknown => Strings.Format(Strings.WarnSourceUnknown, argument),
            ImportWarningCodes.FtpNotSupported => Strings.Format(Strings.WarnFtpNotSupported, argument),
            ImportWarningCodes.NoSources => Strings.WarnNoSources,
            ImportWarningCodes.NoDestinations => Strings.WarnNoDestinations,
            ImportWarningCodes.FixedDayAssumed => Strings.Format(Strings.WarnFixedDayAssumed, argument),
            ImportWarningCodes.FiltersImported => Strings.Format(Strings.WarnFiltersImported, argument),
            ImportWarningCodes.RunAsAccount => Strings.Format(Strings.WarnRunAsAccount, argument),
            ImportWarningCodes.RunAsLocalOnly => Strings.Format(Strings.WarnRunAsLocalOnly, argument),
            _ => warning.Code + (argument.Length > 0 ? ": " + argument : string.Empty),
        };
    }

    protected override async Task<bool> OkAsync()
    {
        var chosen = Chosen();
        if (chosen.Count == 0)
        {
            return false;
        }

        var request = new TasksImportRequest();
        var replace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _accounts.Values)
        {
            if (row.UseStored)
            {
                replace[row.Account.Credential.Id] = row.Existing!.Id;
                continue;
            }

            if (row.Password.Text.Length == 0)
            {
                row.Password.Error = Strings.Format(Strings.ImportPasswordRequired, row.Account.Credential.DisplayName);
                return false;
            }

            request.Credentials.Add(new CredentialInput { Credential = row.Account.Credential, Password = row.Password.Text });
        }

        foreach (var imported in chosen)
        {
            var task = imported.Task.Clone();
            task.Enabled = false;
            foreach (var source in task.Sources.Where(s => s.CredentialId is not null && replace.ContainsKey(s.CredentialId)))
            {
                source.CredentialId = replace[source.CredentialId!];
            }

            foreach (var destination in task.Destinations.Where(d => d.CredentialId is not null && replace.ContainsKey(d.CredentialId)))
            {
                destination.CredentialId = replace[destination.CredentialId!];
            }

            request.Tasks.Add(task);
        }

        await _monitor.RequestAsync(PipeProtocol.Methods.TasksImport, request,
            IpcJsonContext.Default.TasksImportRequest, IpcJsonContext.Default.OkResult);
        await Dialogs.Show(this, Strings.Format(Strings.ImportDone, request.Tasks.Count));
        return true;
    }

    private static string L(string label) => label.TrimEnd(':', ' ');

    private static string? GuessListFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            string path = Path.Combine(Environment.GetFolderPath(folder), "Cobian Backup 11", "DB", "MainList.lst");
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static bool Uses(TaskDefinition task, string credentialId) =>
        task.Sources.Any(s => string.Equals(s.CredentialId, credentialId, StringComparison.OrdinalIgnoreCase))
        || task.Destinations.Any(d => string.Equals(d.CredentialId, credentialId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Under the name of a task: its type, schedule, whether Cobian ran it, and what to check.</summary>
    private static string Details(ImportedTask imported)
    {
        var task = imported.Task;
        var lines = new List<string>
        {
            Words.BackupType(task.BackupType) + " · " + TaskDescriber.ScheduleWithCatchUp(task.Schedule) + " · "
                + L(Strings.ColumnInCobian) + ": " + (imported.EnabledInCobian ? Strings.Yes : Strings.No).ToLowerInvariant(),
        };
        lines.AddRange(imported.Warnings.Count == 0 ? [Strings.ImportNoRemarks] : imported.Warnings.Select(w => "• " + Remark(w)));
        return string.Join(Environment.NewLine, lines);
    }

    private async Task BrowseAsync()
    {
        if (await PickFileAsync(this, Strings.ImportTitle, _file.Value, EventDialog.Filter(Strings.ImportFilter)) is { } path)
        {
            ReadFile(path);
        }
    }

    private void ReadList()
    {
        ShowError(null);
        try
        {
            _result = CobianImporter.Convert(CobianListParser.ReadFile(_file.Value.Trim('"')));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or ArgumentException or NotSupportedException)
        {
            _result = null;
            ShowError(Strings.Format(Strings.ImportCannotRead, ex.Message));
        }

        // A new list has new account ids: the rows of the old one go.
        foreach (var item in _tasks)
        {
            Items.Remove(item);
        }

        _tasks.Clear();
        foreach (var row in _accounts.Values)
        {
            row.RemoveFrom(Items);
        }

        _accounts.Clear();
        int at = Items.IndexOf(_found) + 1;
        foreach (var imported in _result?.Tasks ?? [])
        {
            var item = new CheckItem(imported.Task.Name, on: true, Details(imported)) { Tag = imported, OnChange = UpdateAccounts };
            _tasks.Add(item);
            Items.Insert(at++, item);
        }

        if (_result is null)
        {
            _found.Show(null);
        }
        else
        {
            _found.Show(Strings.Format(Strings.ImportFound, _result.Tasks.Count), Tone.Plain);
        }

        UpdateAccounts();
    }

    private List<ImportedTask> Chosen() => [.. _tasks.Where(i => i.On).Select(i => (ImportedTask)i.Tag!)];

    /// <summary>
    /// One row per account that the chosen tasks use for network paths; its password is asked once. Rows of accounts
    /// that are still needed keep what was typed into them when other tasks are ticked or unticked.
    /// </summary>
    private void UpdateAccounts()
    {
        if (_result is null)
        {
            _accountsHeading.IsVisible = false;
            OkButton.IsEnabled = false;
            return;
        }

        var chosen = Chosen();
        var stored = _monitor.Snapshot?.Credentials ?? [];
        var needed = _result.Accounts.Where(a => chosen.Any(t => Uses(t.Task, a.Credential.Id))).ToList();
        foreach (string id in _accounts.Keys.Where(id => !needed.Any(a => string.Equals(a.Credential.Id, id, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            _accounts[id].RemoveFrom(Items);
            _accounts.Remove(id);
        }

        int at = Items.IndexOf(_accountsHeading) + 1;
        foreach (var account in needed)
        {
            if (!_accounts.TryGetValue(account.Credential.Id, out var row))
            {
                var existing = stored.FirstOrDefault(c => c.Kind == account.Credential.Kind
                    && string.Equals(c.User, account.Credential.User, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(c.Domain ?? ".", account.Credential.Domain ?? ".", StringComparison.OrdinalIgnoreCase));
                row = new AccountRow(account, existing);
                _accounts[account.Credential.Id] = row;
                row.InsertInto(Items, at);
            }

            at = row.After(Items);
        }

        _accountsHeading.IsVisible = needed.Count > 0;
        OkButton.IsEnabled = chosen.Count > 0;
    }

    /// <summary>An account to import: its password or "use the stored one", and the tasks using it.</summary>
    private sealed class AccountRow
    {
        private readonly CheckItem? _useStored;
        private readonly RowItem _row;
        private readonly NoteItem _tasks;

        public AccountRow(ImportedAccount account, Credential? existing)
        {
            Account = account;
            Existing = existing;
            Password = new TextItem(account.Credential.DisplayName, width: 300) { Secret = true };
            _tasks = new NoteItem(Strings.Format(Strings.ImportAccountTasks, string.Join(", ", account.TaskNames)));
            if (existing is not null)
            {
                // The same account is stored already: its password is used unless the box is unticked.
                _useStored = new CheckItem(Strings.ImportUseStored, on: true) { Inline = true };
                _useStored.OnChange = () => Password.IsEnabled = !_useStored.On;
                Password.IsEnabled = false;
                _row = new RowItem(Password, _useStored);
            }
            else
            {
                _row = new RowItem(Password);
            }
        }

        public ImportedAccount Account { get; }

        public Credential? Existing { get; }

        public TextItem Password { get; }

        public bool UseStored => Existing is not null && _useStored?.On == true;

        public void InsertInto(System.Collections.ObjectModel.ObservableCollection<FormItem> items, int at)
        {
            items.Insert(at, _row);
            items.Insert(at + 1, _tasks);
        }

        /// <summary>The place after this row.</summary>
        public int After(System.Collections.ObjectModel.ObservableCollection<FormItem> items) => items.IndexOf(_tasks) + 1;

        public void RemoveFrom(System.Collections.ObjectModel.ObservableCollection<FormItem> items)
        {
            Password.Text = string.Empty;
            items.Remove(_row);
            items.Remove(_tasks);
        }
    }
}
