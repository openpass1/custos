using System.Text.Json;
using System.Text.Json.Nodes;
using Custos.Core.Json;
using Custos.Core.Model;
using Custos.Core.Monitor;

namespace Custos.Core.Agent;

/// <summary>A task saved by a command of the server (A.4).</summary>
/// <param name="Overwritten">The version that was replaced without the server having seen it (a conflict), or null.</param>
public sealed record ServerTaskChange(TaskDefinition Saved, TaskDefinition? Previous, TaskDefinition? Overwritten)
{
    public TaskChange Change => new(Saved, Previous);
}

/// <summary>An account sent with <c>task.upsert</c>, its secret already decrypted (A.4).</summary>
public sealed record ServerCredential(string Id, string Kind, string User, string? Domain, string Secret);

/// <summary>Changes that come from the monitoring server and the configuration snapshot (A.4, A.6).</summary>
public sealed partial class ConfigStore
{
    /// <summary>Kinds of accounts the server may send. An SSH key is made by the agent only (CUSTOS-remote-pull.md 4.1).</summary>
    public static readonly IReadOnlySet<string> ServerCredentialKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        CredentialKinds.Windows, CredentialKinds.Ftp, CredentialKinds.Sftp, CredentialKinds.S3, CredentialKinds.WebDav,
        CredentialKinds.ArchivePassword, CredentialKinds.Db,
    };

    /// <summary>Settings the server can never change (A.9).</summary>
    private static readonly HashSet<string> LocalSettings = new(StringComparer.Ordinal)
    {
        "allow_remote_edit", "server_url", "heartbeat_interval_s",
    };

    /// <summary>
    /// The configuration snapshot (A.6): every task, the accounts without secrets and the settings, read at one moment.
    /// </summary>
    public byte[] BuildSnapshot(LinkState notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            lock (_lock)
            {
                writer.WriteStartObject();
                writer.WriteNumber("config_revision", _tasks.ConfigRevision);
                writer.WriteStartArray("changed");
                foreach (var note in notes.Changed)
                {
                    writer.WriteStartObject();
                    writer.WriteString("task_id", note.TaskId);
                    writer.WriteNumber("revision", note.Revision);
                    writer.WriteString("updated_by", note.UpdatedBy);
                    if (note.UpdatedAt is { } at)
                    {
                        writer.WriteString("updated_at", UtcTimeConverter.ToText(at));
                    }
                    else
                    {
                        writer.WriteNull("updated_at");
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WritePropertyName("settings");
                JsonSerializer.Serialize(writer, _settings, ConfigJsonContext.Default.AgentSettings);
                writer.WriteStartArray("credentials");
                foreach (var stored in _credentials.Credentials)
                {
                    var credential = stored.Credential;
                    writer.WriteStartObject();
                    writer.WriteString("id", credential.Id);
                    writer.WriteString("kind", credential.Kind);
                    writer.WriteString("user", credential.User);
                    writer.WriteString("domain", credential.Domain);
                    writer.WriteBoolean("has_secret", stored.SecretDpapi is not null);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteStartArray("tasks");
                foreach (var task in _tasks.Tasks)
                {
                    JsonSerializer.Serialize(writer, task, ConfigJsonContext.Default.TaskDefinition);
                }

                writer.WriteEndArray();
                writer.WriteStartArray("deleted_task_ids");
                foreach (string id in notes.DeletedTaskIds.Where(id => Find(id) is null))
                {
                    writer.WriteStringValue(id);
                }

                writer.WriteEndArray();
                writer.WriteStartArray("overwritten");
                foreach (var task in notes.Overwritten)
                {
                    task.WriteTo(writer);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// <c>task.upsert</c> (A.4): creates or replaces a task with the accounts that came with it. The last edit wins: a
    /// task changed here meanwhile is replaced all the same, and its version is returned as overwritten.
    /// </summary>
    /// <exception cref="ConfigException">The task or an account is not valid.</exception>
    public ServerTaskChange UpsertFromServer(JsonElement taskJson, int baseRevision, IReadOnlyList<ServerCredential> credentials, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        TaskDefinition task;
        try
        {
            task = taskJson.Deserialize(ConfigJsonContext.Default.TaskDefinition) ?? throw new ConfigException(Messages.MonitorTaskNotObject);
        }
        catch (JsonException ex)
        {
            throw new ConfigException(Messages.Format(Messages.MonitorTaskUnreadable, ex.Message), ex);
        }

        // The id must come from the server: a task without one would get a new id here and never match the site.
        if (taskJson.ValueKind != JsonValueKind.Object || !taskJson.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
            || !Guid.TryParse(task.Id, out _))
        {
            throw new ConfigException(Messages.Format(Messages.MonitorTaskBadId, task.Id));
        }

        lock (_lock)
        {
            var stored = new List<StoredCredential>(_credentials.Credentials);
            foreach (var input in credentials)
            {
                if (!Guid.TryParse(input.Id, out _))
                {
                    throw new ConfigException(Messages.Format(Messages.MonitorTaskBadId, input.Id));
                }

                if (!ServerCredentialKinds.Contains(input.Kind))
                {
                    throw new ConfigException(Messages.Format(Messages.MonitorCredentialKindRefused, input.Kind));
                }

                var credential = CopyCredential(new Credential { Id = input.Id, Kind = input.Kind, User = input.User, Domain = input.Domain });
                ValidateCredential(credential);
                int index = stored.FindIndex(c => string.Equals(c.Credential.Id, input.Id, StringComparison.OrdinalIgnoreCase));
                if (index >= 0 && stored[index].Credential.Kind == CredentialKinds.SshKey)
                {
                    throw new ConfigException(Messages.ConfigSshKeyOnlyMade);
                }

                if (index >= 0 && CredentialKinds.IsCloud(stored[index].Credential.Kind))
                {
                    // A connected cloud account is never replaced from outside (section 6.3, A.9).
                    throw new ConfigException(Messages.CloudAccountOnlyConnected);
                }

                var protectedCredential = Protect(credential, input.Secret);
                if (index >= 0)
                {
                    protectedCredential.Credential.ExtensionData = stored[index].Credential.ExtensionData;
                    stored[index] = protectedCredential;
                }
                else
                {
                    stored.Add(protectedCredential);
                }
            }

            Normalize(task);
            var existing = Find(task.Id);
            KeepLocalFields(task, existing);

            Validate(task, stored.Select(c => c.Credential.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));
            bool conflict = existing is not null && existing.Revision != baseRevision;
            task.Revision = Math.Max(existing?.Revision ?? 0, baseRevision) + 1;
            task.UpdatedAt = Truncate(now);
            task.UpdatedBy = ServerAuthor(task.UpdatedBy);

            var tasks = new List<TaskDefinition>(_tasks.Tasks);
            int position = existing is null ? -1 : tasks.IndexOf(existing);
            if (position >= 0)
            {
                tasks[position] = task;
            }
            else
            {
                tasks.Add(task);
            }

            // Accounts first: a task must never point to an account that is not stored.
            if (credentials.Count > 0)
            {
                WriteCredentials(stored, bump: false);
            }

            WriteTasks(tasks);
            return new ServerTaskChange(task.Clone(), existing?.Clone(), conflict ? existing!.Clone() : null);
        }
    }

    /// <summary><c>task.enable</c> and <c>task.disable</c> (A.4): a new revision with the other state.</summary>
    /// <returns>The change, or null when the task is already so.</returns>
    /// <exception cref="ConfigException">No such task.</exception>
    public TaskChange? SetEnabled(string taskId, bool enabled, string updatedBy, DateTimeOffset now)
    {
        lock (_lock)
        {
            var existing = Find(taskId) ?? throw new ConfigException(Messages.ConfigTaskGone) { IsNotFound = true };
            if (existing.Enabled == enabled)
            {
                return null;
            }

            var copy = existing.Clone();
            copy.Enabled = enabled;
            copy.Revision = existing.Revision + 1;
            copy.UpdatedAt = Truncate(now);
            copy.UpdatedBy = updatedBy;
            var tasks = new List<TaskDefinition>(_tasks.Tasks);
            tasks[tasks.IndexOf(existing)] = copy;
            WriteTasks(tasks);
            return new TaskChange(copy.Clone(), existing.Clone());
        }
    }

    /// <summary>
    /// <c>settings.update</c> (A.4): only the fields sent change; the local ones (A.9) never do.
    /// </summary>
    /// <returns>The names of the settings changed.</returns>
    /// <exception cref="ConfigException">A value is not valid.</exception>
    public List<string> UpdateSettingsFromServer(JsonElement changes)
    {
        if (changes.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigException(Messages.MonitorSettingsNotObject);
        }

        lock (_lock)
        {
            var current = JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(_settings, ConfigJsonContext.Default.AgentSettings))!.AsObject();
            var changed = new List<string>();
            foreach (var property in changes.EnumerateObject())
            {
                if (LocalSettings.Contains(property.Name))
                {
                    continue;
                }

                current[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                changed.Add(property.Name);
            }

            AgentSettings settings;
            try
            {
                settings = current.Deserialize(ConfigJsonContext.Default.AgentSettings) ?? throw new ConfigException(Messages.MonitorSettingsNotObject);
            }
            catch (JsonException ex)
            {
                throw new ConfigException(Messages.Format(Messages.MonitorSettingsUnreadable, ex.Message), ex);
            }

            if (settings.ProgramPasswordHash is { } hash && !IsPasswordHash(hash))
            {
                throw new ConfigException(Messages.MonitorPasswordHashBad);
            }

            if (settings.LogRetentionDays is < 1 or > 3650)
            {
                throw new ConfigException(Messages.Format(Messages.ConfigBadRetentionDays, 1, 3650));
            }

            if (settings.HistoryRetentionDays is < 1 or > 3650)
            {
                throw new ConfigException(Messages.Format(Messages.ConfigBadHistoryDays, 1, 3650));
            }

            if (settings.DefaultLogLevel is not (LogLevels.Summary or LogLevels.Files))
            {
                throw new ConfigException(Messages.Format(Messages.MonitorSettingBad, "default_log_level", settings.DefaultLogLevel));
            }

            if (settings.ProcessPriority is not ("low" or "normal"))
            {
                throw new ConfigException(Messages.Format(Messages.MonitorSettingBad, "process_priority", settings.ProcessPriority));
            }

            if (settings.Language is not ("auto" or "ru" or "en"))
            {
                throw new ConfigException(Messages.Format(Messages.MonitorSettingBad, "language", settings.Language));
            }

            if (!TrayModes.IsKnown(settings.TrayMode))
            {
                throw new ConfigException(Messages.Format(Messages.MonitorSettingBad, "tray_mode", settings.TrayMode));
            }

            if (!Themes.IsKnown(settings.Theme))
            {
                throw new ConfigException(Messages.Format(Messages.MonitorSettingBad, "theme", settings.Theme));
            }

            // The local ones from the file, whatever the parsing did with them.
            settings.AllowRemoteEdit = _settings.AllowRemoteEdit;
            settings.ServerUrl = _settings.ServerUrl;
            settings.HeartbeatIntervalS = _settings.HeartbeatIntervalS;
            WriteJson(SettingsFileName, settings, ConfigJsonContext.Default.AgentSettings);
            _settings = settings;
            BumpRevision();
            return changed;
        }
    }

    /// <summary>Sets the address of the monitoring server (a local setting, A.9); null = no server.</summary>
    public void SetServerUrl(string? url)
    {
        lock (_lock)
        {
            var copy = CopySettings(_settings);
            copy.ServerUrl = url;
            WriteJson(SettingsFileName, copy, ConfigJsonContext.Default.AgentSettings);
            _settings = copy;
            BumpRevision();
        }
    }

    /// <summary>The form of the program password hash (A.2): <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;salt&gt;$&lt;hash&gt;</c>.</summary>
    public static bool IsPasswordHash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] parts = text.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out int iterations) || iterations is < 10_000 or > 10_000_000)
        {
            return false;
        }

        try
        {
            return Convert.FromBase64String(parts[2]).Length >= 8 && Convert.FromBase64String(parts[3]).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// What only this computer knows stays as it is (A.9): the pinned keys of Linux servers and of the servers of
    /// destinations, whose profile the paths use. The key or certificate of a destination is confirmed by an
    /// administrator of this computer; one that comes from the site is never taken, or whoever changes tasks on the
    /// site could point a destination at a server of their own.
    /// </summary>
    private static void KeepLocalFields(TaskDefinition task, TaskDefinition? existing)
    {
        foreach (var destination in task.Destinations)
        {
            var old = existing?.Destinations.FirstOrDefault(d => string.Equals(d.Id, destination.Id, StringComparison.OrdinalIgnoreCase));
            bool sameServer = old is not null && !DestinationKinds.IsFileSystem(destination.Kind)
                && old.Kind == destination.Kind && old.Port == destination.Port
                && string.Equals(old.Host?.Trim(), destination.Host?.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(old.Endpoint?.Trim(), destination.Endpoint?.Trim(), StringComparison.OrdinalIgnoreCase);
            destination.ServerFingerprint = sameServer ? old!.ServerFingerprint : null;
        }

        if (existing is null)
        {
            return;
        }

        task.ProfileSid ??= existing.ProfileSid;
        foreach (var source in task.Sources.Where(s => s.Kind == SourceKinds.Ssh))
        {
            var old = existing.Sources.FirstOrDefault(s => string.Equals(s.Id, source.Id, StringComparison.OrdinalIgnoreCase) && s.Kind == SourceKinds.Ssh);
            if (old is not null && string.Equals(old.Host, source.Host, StringComparison.OrdinalIgnoreCase) && old.Port == source.Port)
            {
                source.HostKeySha256 = old.HostKeySha256;
            }
        }
    }

    private static string ServerAuthor(string? updatedBy) =>
        updatedBy is not null && updatedBy.StartsWith("server:", StringComparison.Ordinal) && updatedBy.Length is > 7 and <= 200
            ? updatedBy : "server:site";

    private void BumpRevision() => WriteTasks([.. _tasks.Tasks]);
}
