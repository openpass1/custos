using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Custos.Core.Json;
using Custos.Core.Model;
using Custos.Core.Scheduling;

namespace Custos.Core.Agent;

/// <summary>Protects stored passwords. The service uses DPAPI under SYSTEM (section 3.3).</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] data);

    /// <exception cref="System.Security.Cryptography.CryptographicException">The data cannot be decrypted here.</exception>
    byte[] Unprotect(byte[] data);
}

/// <summary>A change the store refused, with the reason in words for the user.</summary>
public sealed class ConfigException : Exception
{
    public ConfigException()
    {
    }

    public ConfigException(string message)
        : base(message)
    {
    }

    public ConfigException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The task was changed by someone else meanwhile.</summary>
    public bool IsConflict { get; init; }

    /// <summary>No such task or account.</summary>
    public bool IsNotFound { get; init; }
}

/// <summary>A saved task and what it was before (null for a new one).</summary>
public sealed record TaskChange(TaskDefinition Saved, TaskDefinition? Previous)
{
    /// <summary>The slots of the schedule may have moved: the schedule changed, or the task was created or enabled.</summary>
    public bool ScheduleReset => Previous is null || (Saved.Enabled && !Previous.Enabled)
        || !JsonEquals(Saved.Schedule, Previous.Schedule, ConfigJsonContext.Default.ScheduleOptions);

    private static bool JsonEquals<T>(T a, T b, JsonTypeInfo<T> type) =>
        JsonSerializer.Serialize(a, type) == JsonSerializer.Serialize(b, type);
}

/// <summary>
/// The configuration of the agent: <c>config\tasks.json</c>, <c>config\settings.json</c> and
/// <c>config\credentials.json</c> (sections 3.3 and A.9). Every change is written at once, through a temporary file,
/// keeping the previous version as <c>.bak</c>. Thread-safe; the objects it returns are copies.
/// </summary>
public sealed class ConfigStore
{
    public const string TasksFileName = "tasks.json";
    public const string SettingsFileName = "settings.json";
    public const string CredentialsFileName = "credentials.json";


    private readonly string _directory;
    private readonly ISecretProtector _protector;
    private readonly Lock _lock = new();
    private readonly List<string> _problems = [];
    private TasksFile _tasks = new();
    private AgentSettings _settings = new();
    private CredentialsFile _credentials = new();

    public ConfigStore(string directory, ISecretProtector protector)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    /// <summary>Problems found while loading (a damaged file and what was done about it), for the log and the interface.</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            lock (_lock)
            {
                return [.. _problems];
            }
        }
    }

    public long ConfigRevision
    {
        get
        {
            lock (_lock)
            {
                return _tasks.ConfigRevision;
            }
        }
    }

    /// <summary>Reads the three files. A missing file means defaults; a damaged one is set aside and its backup used.</summary>
    public void Load()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_directory);
            _problems.Clear();
            _tasks = Read(TasksFileName, ConfigJsonContext.Default.TasksFile) ?? new TasksFile();
            _settings = Read(SettingsFileName, ConfigJsonContext.Default.AgentSettings) ?? new AgentSettings();
            _credentials = Read(CredentialsFileName, ConfigJsonContext.Default.CredentialsFile) ?? new CredentialsFile();
            foreach (var task in _tasks.Tasks)
            {
                Normalize(task);
            }
        }
    }

    // ---- Tasks -------------------------------------------------------------------------------------------------

    public List<TaskDefinition> GetTasks()
    {
        lock (_lock)
        {
            return [.. _tasks.Tasks.Select(t => t.Clone())];
        }
    }

    public TaskDefinition? GetTask(string taskId)
    {
        lock (_lock)
        {
            return Find(taskId)?.Clone();
        }
    }

    /// <summary>Creates or replaces a task.</summary>
    /// <param name="baseRevision">The revision the editor started from; null for a new task.</param>
    /// <param name="updatedBy"><c>local:DOMAIN\user</c> (A.9).</param>
    /// <exception cref="ConfigException">The task is not valid, or was changed meanwhile.</exception>
    public TaskChange SaveTask(TaskDefinition task, int? baseRevision, string updatedBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_lock)
        {
            var copy = task.Clone();
            Normalize(copy);
            Validate(copy);
            var existing = Find(copy.Id);
            if (existing is not null && baseRevision is null)
            {
                throw new ConfigException(Messages.Format(Messages.ConfigTaskExists, existing.Name)) { IsConflict = true };
            }

            if (existing is null && baseRevision is not null)
            {
                throw new ConfigException(Messages.ConfigTaskGone) { IsNotFound = true };
            }

            if (existing is not null && existing.Revision != baseRevision)
            {
                throw new ConfigException(Messages.Format(Messages.ConfigTaskChanged, existing.UpdatedBy ?? "?")) { IsConflict = true };
            }

            copy.Revision = (existing?.Revision ?? 0) + 1;
            copy.UpdatedAt = Truncate(now);
            copy.UpdatedBy = updatedBy;
            var tasks = new List<TaskDefinition>(_tasks.Tasks);
            int index = existing is null ? -1 : tasks.IndexOf(existing);
            if (index >= 0)
            {
                tasks[index] = copy;
            }
            else
            {
                tasks.Add(copy);
            }

            WriteTasks(tasks);
            return new TaskChange(copy.Clone(), existing?.Clone());
        }
    }

    /// <summary>Removes a task. Its copies in the destinations stay where they are.</summary>
    /// <returns>The removed task, or null if there was none.</returns>
    public TaskDefinition? DeleteTask(string taskId)
    {
        lock (_lock)
        {
            var existing = Find(taskId);
            if (existing is null)
            {
                return null;
            }

            WriteTasks([.. _tasks.Tasks.Where(t => !ReferenceEquals(t, existing))]);
            return existing.Clone();
        }
    }

    /// <summary>Adds imported tasks and their accounts in one go (section 9).</summary>
    public List<TaskChange> Import(IReadOnlyList<TaskDefinition> tasks, IReadOnlyList<(Credential Credential, string? Password)> credentials, string updatedBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(credentials);
        lock (_lock)
        {
            var stored = new List<StoredCredential>(_credentials.Credentials);
            foreach (var (credential, password) in credentials)
            {
                var copy = CopyCredential(credential);
                ValidateCredential(copy);
                if (stored.Any(c => string.Equals(c.Credential.Id, copy.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ConfigException(Messages.Format(Messages.ConfigCredentialExists, copy.DisplayName)) { IsConflict = true };
                }

                stored.Add(Protect(copy, password));
            }

            var known = stored.Select(c => c.Credential.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var list = new List<TaskDefinition>(_tasks.Tasks);
            var changes = new List<TaskChange>();
            foreach (var task in tasks)
            {
                var copy = task.Clone();
                Normalize(copy);
                Validate(copy, known);
                if (list.Any(t => string.Equals(t.Id, copy.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    copy.Id = TaskDefinition.NewId();
                }

                copy.Revision = 1;
                copy.UpdatedAt = Truncate(now);
                copy.UpdatedBy = updatedBy;
                list.Add(copy);
                changes.Add(new TaskChange(copy.Clone(), null));
            }

            // Accounts first: a task must never point to an account that is not stored.
            WriteCredentials(stored);
            WriteTasks(list);
            return changes;
        }
    }

    // ---- Settings ----------------------------------------------------------------------------------------------

    public AgentSettings GetSettings()
    {
        lock (_lock)
        {
            return CopySettings(_settings);
        }
    }

    /// <exception cref="ConfigException">A value is out of range.</exception>
    public void SaveSettings(AgentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_lock)
        {
            var copy = CopySettings(settings);
            // The program password is set through its own dialog (stage 5), never by a plain settings save.
            copy.ProgramPasswordHash = _settings.ProgramPasswordHash;
            if (copy.LogRetentionDays is < 1 or > 3650)
            {
                throw new ConfigException(Messages.Format(Messages.ConfigBadRetentionDays, 1, 3650));
            }

            if (copy.DefaultLogLevel is not (LogLevels.Summary or LogLevels.Files))
            {
                copy.DefaultLogLevel = LogLevels.Summary;
            }

            WriteJson(SettingsFileName, copy, ConfigJsonContext.Default.AgentSettings);
            _settings = copy;
        }
    }

    // ---- Credentials -------------------------------------------------------------------------------------------

    /// <summary>The stored accounts, without secrets.</summary>
    public List<Credential> GetCredentials()
    {
        lock (_lock)
        {
            return [.. _credentials.Credentials.Select(c => CopyCredential(c.Credential))];
        }
    }

    public Credential? GetCredential(string credentialId)
    {
        lock (_lock)
        {
            return FindCredential(credentialId) is { } stored ? CopyCredential(stored.Credential) : null;
        }
    }

    /// <summary>Creates or changes an account.</summary>
    /// <param name="password">The new password; null keeps the stored one.</param>
    public Credential SaveCredential(Credential credential, string? password)
    {
        ArgumentNullException.ThrowIfNull(credential);
        lock (_lock)
        {
            var copy = CopyCredential(credential);
            ValidateCredential(copy);
            var list = new List<StoredCredential>(_credentials.Credentials);
            var existing = FindCredential(copy.Id);
            StoredCredential updated;
            if (existing is not null && password is null)
            {
                copy.HasSecret = existing.SecretDpapi is not null;
                updated = new StoredCredential { Credential = copy, SecretDpapi = existing.SecretDpapi };
            }
            else
            {
                updated = Protect(copy, password);
            }

            int index = existing is null ? -1 : list.IndexOf(existing);
            if (index >= 0)
            {
                list[index] = updated;
            }
            else
            {
                list.Add(updated);
            }

            WriteCredentials(list);
            return CopyCredential(updated.Credential);
        }
    }

    /// <exception cref="ConfigException">A task still uses the account.</exception>
    public bool DeleteCredential(string credentialId)
    {
        lock (_lock)
        {
            var existing = FindCredential(credentialId);
            if (existing is null)
            {
                return false;
            }

            var users = _tasks.Tasks.Where(t => UsesCredential(t, credentialId)).Select(t => t.Name).ToList();
            if (users.Count > 0)
            {
                throw new ConfigException(Messages.Format(Messages.ConfigCredentialInUse, existing.Credential.DisplayName, string.Join(", ", users)));
            }

            WriteCredentials([.. _credentials.Credentials.Where(c => !ReferenceEquals(c, existing))]);
            return true;
        }
    }

    /// <summary>The password of an account, or null if it has none.</summary>
    /// <exception cref="ConfigException">No such account, or its password cannot be decrypted on this machine.</exception>
    public string? GetPassword(string credentialId)
    {
        StoredCredential stored;
        lock (_lock)
        {
            stored = FindCredential(credentialId) ?? throw new ConfigException(Messages.CredentialNotFound) { IsNotFound = true };
        }

        if (stored.SecretDpapi is null)
        {
            return null;
        }

        try
        {
            byte[] secret = _protector.Unprotect(Convert.FromBase64String(stored.SecretDpapi));
            return Encoding.UTF8.GetString(secret);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            throw new ConfigException(Messages.Format(Messages.CredentialUnreadable, stored.Credential.DisplayName), ex);
        }
    }

    public static bool UsesCredential(TaskDefinition task, string credentialId)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.Sources.Any(s => string.Equals(s.CredentialId, credentialId, StringComparison.OrdinalIgnoreCase))
            || task.Destinations.Any(d => string.Equals(d.CredentialId, credentialId, StringComparison.OrdinalIgnoreCase))
            || string.Equals(task.Archive.PasswordCredentialId, credentialId, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Plumbing ----------------------------------------------------------------------------------------------

    private TaskDefinition? Find(string taskId) =>
        _tasks.Tasks.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));

    private StoredCredential? FindCredential(string credentialId) =>
        _credentials.Credentials.FirstOrDefault(c => string.Equals(c.Credential.Id, credentialId, StringComparison.OrdinalIgnoreCase));

    private void Validate(TaskDefinition task, HashSet<string>? knownCredentials = null)
    {
        if (string.IsNullOrWhiteSpace(task.Name))
        {
            throw new ConfigException(Messages.ConfigNameRequired);
        }

        if (task.Name.Length > 200)
        {
            throw new ConfigException(Messages.Format(Messages.ConfigNameTooLong, 200));
        }

        knownCredentials ??= _credentials.Credentials.Select(c => c.Credential.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referenced = task.Sources.Select(s => s.CredentialId).Concat(task.Destinations.Select(d => d.CredentialId)).OfType<string>();
        foreach (string id in referenced)
        {
            if (!knownCredentials.Contains(id))
            {
                throw new ConfigException(Messages.Format(Messages.ConfigCredentialMissing, id));
            }
        }

        var schedule = task.Schedule;
        foreach (string time in schedule.Times)
        {
            if (ScheduleCalculator.ParseTime(time) is null)
            {
                throw new ConfigException(Messages.Format(Messages.ConfigBadTime, time));
            }
        }

        if (schedule.Type == ScheduleTypes.Interval && schedule.IntervalMinutes is < 1 or > 1440)
        {
            throw new ConfigException(Messages.Format(Messages.ConfigBadInterval, 1, 1440));
        }

        if (task.Retention.KeepFull < 1)
        {
            throw new ConfigException(Messages.ConfigBadKeepFull);
        }
    }

    private static void ValidateCredential(Credential credential)
    {
        if (string.IsNullOrWhiteSpace(credential.User))
        {
            throw new ConfigException(Messages.ConfigUserRequired);
        }
    }

    /// <summary>Fills in what an older or hand-written file may lack, so the rest of the program can rely on it.</summary>
    private static void Normalize(TaskDefinition task)
    {
        task.Name = (task.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(task.Id))
        {
            task.Id = TaskDefinition.NewId();
        }

        task.Sources ??= [];
        task.Destinations ??= [];
        task.Filters ??= new TaskFilters();
        task.Schedule ??= new ScheduleOptions();
        task.Schedule.Times ??= [];
        task.Schedule.DaysOfWeek ??= [];
        task.Schedule.DaysOfMonth ??= [];
        task.Retention ??= new RetentionOptions();
        task.Layout ??= new LayoutOptions();
        task.Copy ??= new CopyOptions();
        task.Copy.ReadRetries = Math.Clamp(task.Copy.ReadRetries, 0, 5);
        UniqueIds(task.Sources, s => s.Id, (s, id) => s.Id = id);
        UniqueIds(task.Destinations, d => d.Id, (d, id) => d.Id = id);
    }

    private static void UniqueIds<T>(List<T> items, Func<T, string> get, Action<T, string> set)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            string id = get(item);
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
            {
                id = TaskDefinition.NewId();
                set(item, id);
                seen.Add(id);
            }
        }
    }

    private StoredCredential Protect(Credential credential, string? password)
    {
        credential.HasSecret = !string.IsNullOrEmpty(password);
        string? secret = null;
        if (!string.IsNullOrEmpty(password))
        {
            byte[] data = Encoding.UTF8.GetBytes(password);
            secret = Convert.ToBase64String(_protector.Protect(data));
        }

        return new StoredCredential { Credential = credential, SecretDpapi = secret };
    }

    private void WriteTasks(List<TaskDefinition> tasks)
    {
        var file = new TasksFile { ConfigRevision = _tasks.ConfigRevision + 1, Tasks = tasks, ExtensionData = _tasks.ExtensionData };
        WriteJson(TasksFileName, file, ConfigJsonContext.Default.TasksFile);
        _tasks = file;
    }

    private void WriteCredentials(List<StoredCredential> credentials)
    {
        var file = new CredentialsFile { Credentials = credentials, ExtensionData = _credentials.ExtensionData };
        WriteJson(CredentialsFileName, file, ConfigJsonContext.Default.CredentialsFile);
        _credentials = file;
    }

    private void WriteJson<T>(string name, T value, JsonTypeInfo<T> type)
    {
        string path = Path.Combine(_directory, name);
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, value, type);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            File.Replace(temp, path, path + ".bak", ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    private T? Read<T>(string name, JsonTypeInfo<T> type)
        where T : class
    {
        string path = Path.Combine(_directory, name);
        string backup = path + ".bak";
        if (!File.Exists(path))
        {
            if (!File.Exists(backup))
            {
                return null;
            }

            _problems.Add(Messages.Format(Messages.ConfigRestoredFromBackup, name));
            return TryRead(backup, type, out var restored, out _) ? restored : null;
        }

        if (TryRead(path, type, out var value, out string? error))
        {
            return value;
        }

        // Keep the damaged file for a human to look at; never write over it.
        string kept = path + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        try
        {
            File.Move(path, kept);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            kept = path;
        }

        if (File.Exists(backup) && TryRead(backup, type, out var fromBackup, out _))
        {
            _problems.Add(Messages.Format(Messages.ConfigDamagedUsedBackup, name, error, kept));
            return fromBackup;
        }

        _problems.Add(Messages.Format(Messages.ConfigDamaged, name, error, kept));
        return null;
    }

    private static bool TryRead<T>(string path, JsonTypeInfo<T> type, out T? value, out string? error)
        where T : class
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            value = JsonSerializer.Deserialize(stream, type);
            error = value is null ? "null" : null;
            return value is not null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            value = null;
            error = ex.Message;
            return false;
        }
    }

    private static Credential CopyCredential(Credential credential) => new()
    {
        Id = string.IsNullOrWhiteSpace(credential.Id) ? TaskDefinition.NewId() : credential.Id,
        Kind = credential.Kind,
        User = credential.User?.Trim() ?? string.Empty,
        Domain = string.IsNullOrWhiteSpace(credential.Domain) ? null : credential.Domain.Trim(),
        HasSecret = credential.HasSecret,
        ExtensionData = credential.ExtensionData is null ? null : new Dictionary<string, JsonElement>(credential.ExtensionData),
    };

    private static AgentSettings CopySettings(AgentSettings settings) =>
        JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(settings, ConfigJsonContext.Default.AgentSettings), ConfigJsonContext.Default.AgentSettings)!;

    private static DateTimeOffset Truncate(DateTimeOffset time) => new(time.UtcTicks - (time.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
}
