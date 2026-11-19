using Avalonia.Controls;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Gui;

namespace Custos.Desktop;

/// <summary>
/// What the pages of the editor share: the connection, the window for dialogs, the accounts, and what one page tells
/// the others (the backup type, the archive format, what the destinations can take). The same as EditorContext of
/// Custos.exe (TaskEditorForm.cs), so the rules of the pages are the same.
/// </summary>
internal sealed class EditorContext(ServiceMonitor monitor)
{
    private readonly List<Credential> _added = [];

    public ServiceMonitor Monitor { get; } = monitor;

    /// <summary>The editor window: the owner of the dialogs the pages open.</summary>
    public Window Owner { get; set; } = null!;

    /// <summary>Raised when an account was added while the editor is open.</summary>
    public event EventHandler? CredentialsChanged;

    /// <summary>Raised when the name, the backup type, the forced full backup, the archive format or what the
    /// destinations can take changed on their pages.</summary>
    public event EventHandler? ShapeChanged;

    /// <summary>The name as typed on the first page.</summary>
    public string TaskName { get; private set; } = string.Empty;

    /// <summary>The backup type chosen on the first page.</summary>
    public string BackupType { get; private set; } = BackupTypes.Incremental;

    /// <summary>The format chosen on the archive page.</summary>
    public string ArchiveFormat { get; private set; } = ArchiveFormats.None;

    public bool IsMirror => BackupType == BackupTypes.Mirror;

    /// <summary>Whether a full backup is forced on the first page (every N-th run, a weekday or a day of the month).</summary>
    public bool HasForcedFull { get; private set; }

    /// <summary>A destination that takes a file only from its start to its end (FTP, WebDAV, S3), in words; null if
    /// there is none. A 7z archive cannot be written there (section 6.5).</summary>
    public string? StreamOnlyDestination
    {
        get;
        set => Raise(ref field, value);
    }

    /// <summary>The longest WORM of the destinations, in days (section 6.3); 0 if none has it.</summary>
    public int WormDays
    {
        get;
        set => Raise(ref field, value);
    }

    /// <summary>Whether «A separate folder for each backup» is on, on the destinations page.</summary>
    public bool SeparateFolders
    {
        get;
        set => Raise(ref field, value);
    } = true;

    /// <summary>The first destination that does not let written files be replaced or deleted for a while (WORM, S3
    /// Object Lock); null if none. Such a destination takes only backups that are never overwritten.</summary>
    public TaskDestination? Protecting
    {
        get;
        set
        {
            if (!ReferenceEquals(field, value))
            {
                field = value;
                ShapeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The first cloud drive among the destinations; null if none. A mirror cannot go there (section 6.3).</summary>
    public TaskDestination? CloudDrive
    {
        get;
        set
        {
            if (!ReferenceEquals(field, value))
            {
                field = value;
                ShapeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Whether a destination can hold NTFS permissions, or none is added yet.</summary>
    public bool KeepsNtfs
    {
        get;
        set => Raise(ref field, value);
    } = true;

    /// <summary>Whether the task reads something, and whether a folder or file on a local disk is among it: a shadow
    /// copy is made of local disks only (section 6.4).</summary>
    public (bool Any, bool Local) Reads
    {
        get;
        set => Raise(ref field, value);
    }

    /// <summary>Raised when a destination that wants an archive was added (a cloud drive).</summary>
    public event EventHandler? ZipWanted;

    public void WantZip() => ZipWanted?.Invoke(this, EventArgs.Empty);

    /// <summary>How many database servers the task has now (a task with them needs no sources).</summary>
    public int DbServerCount { get; set; }

    /// <summary>Raised when a Linux server was added to the sources or removed from them.</summary>
    public event EventHandler? RemoteChanged;

    /// <summary>Whether the source is a Linux server: then it is the only one, and its databases are its own.</summary>
    public bool HasRemoteSource
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                RemoteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Whose profile %USERPROFILE% in the paths means; null until the task is first saved.</summary>
    public string? ProfileSid { get; init; }

    /// <summary>Tells the other pages what changed; null leaves a value as it is.</summary>
    public void SetShape(string? taskName = null, string? backupType = null, string? archiveFormat = null, bool? forcedFull = null)
    {
        bool changed = (taskName is not null && taskName != TaskName)
            || (backupType is not null && backupType != BackupType)
            || (archiveFormat is not null && archiveFormat != ArchiveFormat)
            || (forcedFull is not null && forcedFull != HasForcedFull);
        TaskName = taskName ?? TaskName;
        BackupType = backupType ?? BackupType;
        ArchiveFormat = archiveFormat ?? ArchiveFormat;
        HasForcedFull = forcedFull ?? HasForcedFull;
        if (changed)
        {
            ShapeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The stored Windows accounts, including the ones added in this editor.</summary>
    public List<Credential> Credentials => CredentialsOf(CredentialKinds.Windows);

    /// <summary>The stored accounts of a kind, including the ones added in this editor.</summary>
    public List<Credential> CredentialsOf(string kind)
    {
        var stored = Monitor.Snapshot?.Credentials ?? [];
        return
        [
            .. stored.Concat(_added.Where(a => !stored.Any(s => Describe.SameId(s.Id, a.Id))))
                .Where(c => c.Kind == kind)
                .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        ];
    }

    /// <summary>A stored account of any kind, including the ones saved in this editor; null if there is none.</summary>
    public Credential? FindCredential(string? id) =>
        id is null ? null
        : _added.LastOrDefault(c => Describe.SameId(c.Id, id)) ?? Monitor.Snapshot?.Credentials.FirstOrDefault(c => Describe.SameId(c.Id, id));

    /// <summary>Keeps an account saved by a dialog of this editor until the service sends it.</summary>
    public void RememberCredential(Credential credential)
    {
        _added.Add(credential);
        CredentialsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Asks for a new account of a kind and stores it in the service; null if the user cancelled.</summary>
    public async Task<Credential?> AddCredentialAsync(Window owner, string kind = CredentialKinds.Windows)
    {
        Credential? saved;
        if (CredentialKinds.IsCloud(kind))
        {
            // An account of a cloud drive is connected in the browser, not typed in (section 6.3).
            var connect = new CloudConnectDialog(Monitor, kind);
            saved = await connect.ShowAsync(owner) ? connect.Saved : null;
        }
        else
        {
            var dialog = new CredentialDialog(Monitor, null, kind);
            saved = await dialog.ShowAsync(owner) ? dialog.Saved : null;
        }

        if (saved is null)
        {
            return null;
        }

        RememberCredential(saved);
        return saved;
    }

    private void Raise<T>(ref T field, T value)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            ShapeChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>
/// The choice of a stored account of one kind, or of working without one, with «Добавить…» (or «Подключить…» for a
/// cloud drive) beside it: CredentialPicker of Custos.exe.
/// </summary>
internal sealed class AccountPicker
{
    private readonly EditorContext _context;
    private readonly Func<Window> _owner;
    private readonly List<string?> _ids = [];
    private string _kind = CredentialKinds.Windows;
    private string? _noneText = Strings.AccountService;
    private string? _selectedId;
    private bool _filling;

    public AccountPicker(EditorContext context, Func<Window> owner, string label, string? help = null)
    {
        _context = context;
        _owner = owner;
        Choice = new ChoiceItem(label, [], 0, width: 400) { Help = help };
        Choice.OnChange = () =>
        {
            if (!_filling && Choice.Selected >= 0 && Choice.Selected < _ids.Count)
            {
                _selectedId = _ids[Choice.Selected];
                SelectionChanged?.Invoke();
            }
        };
        Add = new ButtonItem("person_add", Strings.ButtonAddAccount, AddAsync) { ByField = true };
        Row = new RowItem(Choice, Add);
        context.CredentialsChanged += (_, _) => Fill();
        Fill();
    }

    /// <summary>The list and the button, for the form.</summary>
    public RowItem Row { get; }

    public ChoiceItem Choice { get; }

    public ButtonItem Add { get; }

    public event Action? SelectionChanged;

    /// <summary>The account id; null = the account of the service (SYSTEM), or no account at all.</summary>
    public string? SelectedId
    {
        get => _selectedId;
        set
        {
            _selectedId = value;
            Fill();
        }
    }

    /// <summary>Shows the accounts of another kind; an account of the kind shown before is no longer chosen.</summary>
    /// <param name="noneText">What working without an account is called; null if this kind cannot do without one.</param>
    public void SetKind(string kind, string? noneText)
    {
        if (_kind != kind && _context.FindCredential(_selectedId) is { } chosen && chosen.Kind != kind)
        {
            _selectedId = null;
        }

        _kind = kind;
        _noneText = noneText;
        Add.Text = CredentialKinds.IsCloud(kind) ? Strings.ButtonConnectAccount : Strings.ButtonAddAccount;
        Fill();
    }

    private void Fill()
    {
        _filling = true;
        try
        {
            _ids.Clear();
            var texts = new List<string>();
            if (_noneText is not null)
            {
                _ids.Add(null);
                texts.Add(_noneText);
            }

            var accounts = _context.CredentialsOf(_kind);
            foreach (var credential in accounts)
            {
                _ids.Add(credential.Id);
                texts.Add(credential.DisplayName);
            }

            if (_selectedId is not null && !accounts.Any(c => Describe.SameId(c.Id, _selectedId)))
            {
                _ids.Add(_selectedId);
                texts.Add(Strings.AccountUnknown);
            }

            Choice.Options = texts;
            int index = _ids.FindIndex(id => id is null ? _selectedId is null : _selectedId is not null && Describe.SameId(id, _selectedId));
            if (index < 0 && texts.Count > 0)
            {
                // No account of this kind chosen yet, and «none» is not offered: the list shows an empty choice.
                index = -1;
            }

            Choice.Load(Math.Max(index, -1));
        }
        finally
        {
            _filling = false;
        }
    }

    private async Task AddAsync()
    {
        if (await _context.AddCredentialAsync(_owner(), _kind) is { } added)
        {
            SelectedId = added.Id;
            SelectionChanged?.Invoke();
        }
    }
}
