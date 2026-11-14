using System.Text.Json;
using Custos.Core.Cloud;
using Custos.Core.Ipc;
using Custos.Core.Model;

namespace Custos.Core.Transfer;

/// <summary>Something the person should know about a task before importing it, as a code and an argument; the interface
/// puts it in words.</summary>
public sealed record ImportRemark(string Code, string? Argument = null);

/// <summary>Codes of <see cref="ImportRemark"/>.</summary>
public static class ImportRemarkCodes
{
    /// <summary>A task with the same id is on this computer and is replaced. Argument: its name here.</summary>
    public const string Replaces = "replaces";

    /// <summary>The password of an account is not in the file: it is asked for. Argument: the account.</summary>
    public const string PasswordMissing = "password_missing";

    /// <summary>The archive password is not in the file: it is asked for; old archives cannot be opened without it.</summary>
    public const string ArchivePasswordMissing = "archive_password_missing";

    /// <summary>The SSH key is not in the file: the task comes switched off until a new key is made. Argument: the account.</summary>
    public const string SshKeyMissing = "ssh_key_missing";

    /// <summary>The token of a cloud account is not in the file: the task comes switched off until the account is connected
    /// again. Argument: "service: account".</summary>
    public const string CloudMissing = "cloud_missing";

    /// <summary>The file has passwords and keys, but they are not opened with the export password yet: what the task
    /// needs may well be there. Given instead of the four remarks above.</summary>
    public const string SecretsLocked = "secrets_locked";
}

/// <summary>A task of the file as the import window shows it.</summary>
public sealed class ImportItem
{
    public required TaskDefinition Task { get; init; }

    /// <summary>The task with the same id on this computer, which the import replaces; null for a new one.</summary>
    public TaskDefinition? Here { get; init; }

    public required List<ImportRemark> Remarks { get; init; }
}

/// <summary>An account whose password the person is asked for: it is neither on this computer nor in the file.</summary>
public sealed class ImportAccount
{
    public required Credential Credential { get; init; }

    public required List<string> TaskNames { get; init; }

    /// <summary>An account stored here under another id with the same kind, user and domain: its password can be used
    /// instead of typing one.</summary>
    public Credential? Similar { get; init; }
}

/// <summary>The request to send, and the tasks it switches off because something they need is still missing.</summary>
public sealed record ImportBuild(TasksImportRequest Request, List<string> SwitchedOff);

/// <summary>
/// Plans the import of a file of exported tasks onto this computer (section 9a): what each task needs, which passwords
/// to ask for, and the request for the service. Tasks and accounts keep their ids, so the sets the tasks made before are
/// found by «Restore» and kept by retention. A task whose account still lacks its secret is imported switched off.
/// </summary>
public sealed class TaskImport
{
    private readonly Dictionary<string, Credential> _fileAccounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Credential> _here = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<Credential> _credentialsHere;

    /// <param name="secrets">The opened secrets of the file; null when it has none or they were not opened.</param>
    /// <param name="tasksHere">The tasks on this computer.</param>
    /// <param name="credentialsHere">The accounts on this computer (without secrets).</param>
    public TaskImport(ExportFile file, ExportSecrets? secrets, IReadOnlyList<TaskDefinition> tasksHere, IReadOnlyList<Credential> credentialsHere)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(tasksHere);
        File = file;
        Secrets = secrets;
        _credentialsHere = credentialsHere ?? throw new ArgumentNullException(nameof(credentialsHere));
        foreach (var credential in file.Credentials)
        {
            _fileAccounts.TryAdd(credential.Id, credential);
        }

        foreach (var credential in credentialsHere)
        {
            _here.TryAdd(credential.Id, credential);
        }

        // Accounts that wait for a task: exported with «all accounts». One this computer has already (by id, or the same
        // kind, user and domain) is not offered again.
        var used = TaskExport.UsedCredentialIds(file.Tasks);
        SpareAccounts = [.. file.Credentials.Where(c => TaskExport.IsListed(c.Kind) && !used.Contains(c.Id) && !_here.ContainsKey(c.Id)
            && !credentialsHere.Any(h => h.Kind == c.Kind
                && string.Equals(h.User, c.User, StringComparison.OrdinalIgnoreCase)
                && string.Equals(h.Domain ?? string.Empty, c.Domain ?? string.Empty, StringComparison.OrdinalIgnoreCase)))];

        Items = [.. file.Tasks.Select(task =>
        {
            var here = tasksHere.FirstOrDefault(t => SameId(t.Id, task.Id));
            var remarks = new List<ImportRemark>();
            if (here is not null)
            {
                remarks.Add(new ImportRemark(ImportRemarkCodes.Replaces, here.Name));
            }

            var lacking = TaskExport.UsedCredentialIds([task]).Where(Lacks).ToList();
            if (SecretsLocked && lacking.Count > 0)
            {
                // Not "not in the file": it most likely is, the file is just not opened.
                remarks.Add(new ImportRemark(ImportRemarkCodes.SecretsLocked));
            }
            else
            {
                remarks.AddRange(lacking.Select(id => Missing(_fileAccounts[id])));
            }

            return new ImportItem { Task = task, Here = here, Remarks = remarks };
        })];
    }

    public ExportFile File { get; }

    public ExportSecrets? Secrets { get; }

    /// <summary>The file has passwords and keys, and they are not opened.</summary>
    public bool SecretsLocked => File.Secrets is not null && Secrets is null;

    /// <summary>The tasks of the file, in its order.</summary>
    public IReadOnlyList<ImportItem> Items { get; }

    /// <summary>Accounts of the file that none of its tasks uses and that this computer does not have.</summary>
    public IReadOnlyList<Credential> SpareAccounts { get; }

    /// <summary>
    /// The accounts of the chosen tasks whose password the person types: they had one where they were exported, it is
    /// not in the file (or the file was not opened), and they are not stored here with one. SSH keys and cloud accounts
    /// are never typed: they are made again on this computer.
    /// </summary>
    public List<ImportAccount> AccountsToAsk(IEnumerable<ImportItem> chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        var tasks = chosen.Select(i => i.Task).ToList();
        var result = new List<ImportAccount>();
        foreach (var account in File.Credentials.Where(c => Lacks(c.Id) && !CredentialKinds.IsMadeHere(c.Kind)))
        {
            var users = tasks.Where(t => TaskExport.UsedCredentialIds([t]).Contains(account.Id)).Select(t => t.Name).ToList();
            if (users.Count == 0)
            {
                continue;
            }

            // An archive password belongs to its task: one of another task with a similar name is not the same password.
            var similar = account.Kind == CredentialKinds.ArchivePassword ? null : _credentialsHere.FirstOrDefault(c => c.HasSecret
                && c.Kind == account.Kind
                && !SameId(c.Id, account.Id)
                && string.Equals(c.User, account.User, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Domain ?? string.Empty, account.Domain ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            result.Add(new ImportAccount { Credential = account, TaskNames = users, Similar = similar });
        }

        return result;
    }

    /// <summary>The request for the chosen tasks.</summary>
    /// <param name="typed">Account id → the password typed for it (empty = none).</param>
    /// <param name="useStored">Account id of the file → id of a similar account here whose password is used instead.</param>
    /// <param name="spare">Also <see cref="SpareAccounts"/>, with their secrets from the file when it is opened.</param>
    public ImportBuild Build(IEnumerable<ImportItem> chosen, IReadOnlyDictionary<string, string> typed, IReadOnlyDictionary<string, string> useStored,
        bool spare = false)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(typed);
        ArgumentNullException.ThrowIfNull(useStored);
        var tasks = chosen.Select(i => i.Task.Clone()).ToList();
        foreach (var task in tasks)
        {
            Remap(task, useStored);
        }

        var request = new TasksImportRequest { KeepIds = true, CloudApps = Secrets?.CloudApps, Tasks = tasks };
        var lacking = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in TaskExport.UsedCredentialIds(tasks))
        {
            if (!_fileAccounts.TryGetValue(id, out var account))
            {
                // A similar account of this computer, chosen instead of the one in the file.
                continue;
            }

            string? secret = Secrets?.Credentials.GetValueOrDefault(id) is { Length: > 0 } inFile ? inFile
                : typed.TryGetValue(id, out string? text) && text.Length > 0 ? text
                : null;
            bool isHere = _here.TryGetValue(id, out var stored);
            if (isHere && stored!.HasSecret)
            {
                // Stored here already with its secret: kept as it is.
                continue;
            }

            if (!isHere || secret is not null)
            {
                request.Credentials.Add(new CredentialInput { Credential = Copy(account), Password = secret });
            }

            if (secret is null && account.HasSecret)
            {
                lacking.Add(id);
            }
        }

        if (spare)
        {
            foreach (var account in SpareAccounts)
            {
                string? secret = Secrets?.Credentials.GetValueOrDefault(account.Id) is { Length: > 0 } inFile ? inFile : null;
                request.Credentials.Add(new CredentialInput { Credential = Copy(account), Password = secret });
            }
        }

        var switchedOff = new List<string>();
        foreach (var task in tasks.Where(t => TaskExport.UsedCredentialIds([t]).Overlaps(lacking)))
        {
            if (task.Enabled)
            {
                task.Enabled = false;
                switchedOff.Add(task.Name);
            }
        }

        return new ImportBuild(request, switchedOff);
    }

    /// <summary>The account had a secret where it was exported, and here there is none: not stored with one, not in the
    /// opened file.</summary>
    private bool Lacks(string id) =>
        _fileAccounts.TryGetValue(id, out var account) && account.HasSecret
        && !(_here.TryGetValue(id, out var stored) && stored.HasSecret)
        && Secrets?.Credentials.GetValueOrDefault(id) is not { Length: > 0 };

    private static ImportRemark Missing(Credential account) => account.Kind switch
    {
        CredentialKinds.SshKey => new ImportRemark(ImportRemarkCodes.SshKeyMissing, account.DisplayName),
        CredentialKinds.ArchivePassword => new ImportRemark(ImportRemarkCodes.ArchivePasswordMissing),
        _ when CredentialKinds.IsCloud(account.Kind) => new ImportRemark(ImportRemarkCodes.CloudMissing, CloudService.NameOf(account.Kind) + ": " + account.DisplayName),
        _ => new ImportRemark(ImportRemarkCodes.PasswordMissing, account.DisplayName),
    };

    private static void Remap(TaskDefinition task, IReadOnlyDictionary<string, string> useStored)
    {
        string? Map(string? id) => id is not null && useStored.TryGetValue(id, out string? other) ? other : id;
        foreach (var source in task.Sources)
        {
            source.CredentialId = Map(source.CredentialId);
        }

        foreach (var destination in task.Destinations)
        {
            destination.CredentialId = Map(destination.CredentialId);
        }

        foreach (var server in task.DbServers)
        {
            server.CredentialId = Map(server.CredentialId);
        }

        task.Archive.PasswordCredentialId = Map(task.Archive.PasswordCredentialId);
    }

    private static Credential Copy(Credential credential) => new()
    {
        Id = credential.Id,
        Kind = credential.Kind,
        User = credential.User,
        Domain = credential.Domain,
        PublicKey = credential.PublicKey,
        ClientId = credential.ClientId,
        ExtensionData = credential.ExtensionData is null ? null : new Dictionary<string, JsonElement>(credential.ExtensionData),
    };

    private static bool SameId(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
