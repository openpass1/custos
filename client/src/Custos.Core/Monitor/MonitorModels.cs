using System.Text.Json;
using System.Text.Json.Serialization;
using Custos.Core.Json;

namespace Custos.Core.Monitor;

/// <summary>
/// JSON of the requests and answers of the monitoring protocol (appendix A): snake_case, times in UTC to the second,
/// nulls written out as in the examples of protocol.md.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    Converters = [typeof(UtcTimeConverter)])]
[JsonSerializable(typeof(RegisterBody))]
[JsonSerializable(typeof(RegisterAnswer))]
[JsonSerializable(typeof(HeartbeatBody))]
[JsonSerializable(typeof(HeartbeatAnswer))]
[JsonSerializable(typeof(RunStartBody))]
[JsonSerializable(typeof(RunProgressBody))]
[JsonSerializable(typeof(RunFinishBody))]
[JsonSerializable(typeof(InventoryBody))]
[JsonSerializable(typeof(CommandResultBody))]
[JsonSerializable(typeof(RunLogBody))]
[JsonSerializable(typeof(ErrorAnswer))]
[JsonSerializable(typeof(UpsertSecret))]
[JsonSerializable(typeof(Dictionary<string, UpsertSecret>))]
[JsonSerializable(typeof(LinkState))]
[JsonSerializable(typeof(PendingUpdate))]
[JsonSerializable(typeof(UpdaterResult))]
[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(ServerSetup))]
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonObject))]
public sealed partial class MonitorJsonContext : JsonSerializerContext;

/// <summary><c>POST agent/register</c> (A.3).</summary>
public sealed class RegisterBody
{
    public required string Hostname { get; set; }

    public required string MachineGuid { get; set; }

    public required string Os { get; set; }

    public required string AgentVersion { get; set; }

    /// <summary><c>legacy</c> for the edition for Windows 7 and 8.1; not sent by the standard edition (A.3).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Edition { get; set; } = CustosEdition.Wire;

    /// <summary>Sent by Linux agents that support cloud drives (A.3).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CloudDestinations { get; set; } = CustosEdition.IsLinux ? true : null;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RemoteRestore { get; set; } = CustosEdition.IsLinux ? true : null;

    public required string TimeZone { get; set; }

    public required string PublicKey { get; set; }
}

public sealed class RegisterAnswer
{
    public string AgentId { get; set; } = string.Empty;

    /// <summary><c>pending</c> or <c>approved</c>.</summary>
    public string Status { get; set; } = string.Empty;

    public string ServerPublicKey { get; set; } = string.Empty;

    public string? ServerName { get; set; }
}

/// <summary><c>POST agent/heartbeat</c> (A.4).</summary>
public sealed class HeartbeatBody
{
    public required string AgentVersion { get; set; }

    /// <summary><c>legacy</c> for the edition for Windows 7 and 8.1; not sent by the standard edition (A.4).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Edition { get; set; } = CustosEdition.Wire;

    /// <summary>Sent by Linux agents that support cloud drives (A.4).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CloudDestinations { get; set; } = CustosEdition.IsLinux ? true : null;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RemoteRestore { get; set; } = CustosEdition.IsLinux ? true : null;

    public string? SevenzipVersion { get; set; }

    public long ConfigRevision { get; set; }

    public required string TimeZone { get; set; }

    public DateTimeOffset ServiceStartedAt { get; set; }

    /// <summary><c>idle</c>, <c>running</c> or <c>paused</c>.</summary>
    public string State { get; set; } = HeartbeatStates.Idle;

    public HeartbeatRun? Running { get; set; }

    public List<string> Queue { get; set; } = [];
}

public static class HeartbeatStates
{
    public const string Idle = "idle";
    public const string Running = "running";
    public const string Paused = "paused";
}

public sealed class HeartbeatRun
{
    public required string RunId { get; set; }

    public required string TaskId { get; set; }

    public DateTimeOffset? PausedAt { get; set; }
}

public sealed class HeartbeatAnswer
{
    /// <summary><c>pending</c>, <c>approved</c> or <c>disabled</c>.</summary>
    public string Status { get; set; } = string.Empty;

    public DateTimeOffset? ServerTime { get; set; }

    public int HeartbeatIntervalS { get; set; } = 60;

    public bool WantConfig { get; set; }

    public List<MonitorCommand> Commands { get; set; } = [];
}

/// <summary>A command from the server (A.4).</summary>
public sealed class MonitorCommand
{
    public string Id { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public JsonElement Payload { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>The local server binding at delivery, supplied by ServerLink rather than by the sender.</summary>
    [JsonIgnore]
    public string? RestoreBinding { get; set; }
}

/// <summary>Kinds of <see cref="MonitorCommand"/>.</summary>
public static class CommandKinds
{
    public const string TaskRun = "task.run";
    public const string TaskStop = "task.stop";
    public const string TaskPause = "task.pause";
    public const string TaskResume = "task.resume";
    public const string TaskEnable = "task.enable";
    public const string TaskDisable = "task.disable";
    public const string TaskUpsert = "task.upsert";
    public const string TaskDelete = "task.delete";
    public const string SettingsUpdate = "settings.update";
    public const string LogFetch = "log.fetch";
    public const string InventoryRefresh = "inventory.refresh";
    public const string UpdateInstall = "update.install";
    public const string SevenZipUpdate = "sevenzip.update";
    public const string RestoreList = "restore.list";
    public const string RestoreStart = "restore.start";
    public const string RestoreStatus = "restore.status";
    public const string RestoreStop = "restore.stop";

    public static bool IsRestore(string kind) => kind is RestoreList or RestoreStart or RestoreStatus or RestoreStop;

    /// <summary>Commands refused when «Allow the server to change tasks» is off (A.4).</summary>
    public static readonly IReadOnlySet<string> Edits = new HashSet<string>(StringComparer.Ordinal)
    {
        TaskUpsert, TaskDelete, TaskEnable, TaskDisable, SettingsUpdate, RestoreList, RestoreStart, RestoreStatus, RestoreStop,
    };
}

/// <summary>Values of <see cref="CommandResultBody.Status"/>.</summary>
public static class CommandStatuses
{
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Rejected = "rejected";
}

/// <summary><c>POST agent/commands/{id}/result</c>.</summary>
public sealed class CommandResultBody
{
    public required string Status { get; set; }

    public string? Message { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MonitorRestoreResult? Restore { get; set; }
}

/// <summary>Bounded metadata for a remote restore command. It carries neither paths nor credentials.</summary>
public sealed class MonitorRestoreResult
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MonitorRestoreSet>? Sets { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Truncated { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Partial { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MonitorRestoreJob? Job { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }
}

public sealed record MonitorRestoreSet(string SetRunId, string DestinationId, string Name, DateTimeOffset StartedAt,
    string BackupType, string Storage, int Parts, long Bytes);

public sealed record MonitorRestoreJob(string JobId, string State)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? FilesDone { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? FilesTotal { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? BytesDone { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? BytesTotal { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? FilesRestored { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? FilesSkipped { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? FilesFailed { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Warnings { get; init; }
}

/// <summary>One account of <c>secrets</c> in <c>task.upsert</c> (A.4).</summary>
public sealed class UpsertSecret
{
    public string Kind { get; set; } = string.Empty;

    public string User { get; set; } = string.Empty;

    public string? Domain { get; set; }

    public string SecretEnc { get; set; } = string.Empty;
}

/// <summary><c>POST agent/runs/start</c> (A.5).</summary>
public sealed class RunStartBody
{
    public required string RunId { get; set; }

    public required string TaskId { get; set; }

    public int TaskRevision { get; set; }

    public required string BackupType { get; set; }

    public required string Trigger { get; set; }

    public DateTimeOffset? ScheduledFor { get; set; }

    public DateTimeOffset StartedAt { get; set; }
}

/// <summary><c>POST agent/runs/progress</c> (A.5).</summary>
public sealed class RunProgressBody
{
    public required string RunId { get; set; }

    public required string Phase { get; set; }

    public double Percent { get; set; }

    public long FilesDone { get; set; }

    public long FilesTotal { get; set; }

    public long BytesDone { get; set; }

    public long BytesTotal { get; set; }

    public long SpeedBps { get; set; }

    public long? EtaS { get; set; }

    public string? CurrentFile { get; set; }
}

/// <summary><c>POST agent/runs/finish</c> (A.5).</summary>
public sealed class RunFinishBody
{
    public required string RunId { get; set; }

    public required string TaskId { get; set; }

    public required string Status { get; set; }

    public DateTimeOffset FinishedAt { get; set; }

    public long SourceFiles { get; set; }

    public long SourceBytes { get; set; }

    public long FilesProcessed { get; set; }

    public long BytesWritten { get; set; }

    public int Errors { get; set; }

    public int Warnings { get; set; }

    public string? ErrorMessage { get; set; }

    public List<FinishDestination> Destinations { get; set; } = [];

    public FinishVerify Verify { get; set; } = new();

    public FinishRetention Retention { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FinishDatabase>? Databases { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FinishRemote? Remote { get; set; }

    public string? LogTail { get; set; }

    /// <summary>The whole log, gzip and base64; only for <c>warning</c> and <c>error</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogGzB64 { get; set; }
}

public sealed class FinishDestination
{
    public required string DestinationId { get; set; }

    public required string Status { get; set; }

    public string? SetName { get; set; }

    public long Bytes { get; set; }

    public string? Error { get; set; }
}

public sealed class FinishVerify
{
    public bool Performed { get; set; }

    public bool? Ok { get; set; }

    /// <summary><c>new</c>, <c>stored</c> or null.</summary>
    public string? Scope { get; set; }

    public int SetsChecked { get; set; }

    public List<string> FailedSets { get; set; } = [];
}

public sealed class FinishRetention
{
    public int DeletedSets { get; set; }
}

public sealed class FinishDatabase
{
    public required string Engine { get; set; }

    public required string Name { get; set; }

    public long Bytes { get; set; }

    public bool Ok { get; set; }

    public string? Error { get; set; }
}

public sealed class FinishRemote
{
    public string HelperVersion { get; set; } = string.Empty;

    public long? ServerFreeBytes { get; set; }

    public long? FilesChanged { get; set; }

    public long? FilesDeleted { get; set; }
}

/// <summary><c>POST agent/runs/{run_id}/log</c> (A.7).</summary>
public sealed class RunLogBody
{
    public required string LogGzB64 { get; set; }
}

/// <summary><c>POST agent/inventory</c> (A.8).</summary>
public sealed class InventoryBody
{
    public List<InventoryDestination> Destinations { get; set; } = [];

    /// <summary>CUSTOS for Linux: whether the sources of the tasks with snapshots on can have them (A.8); not sent by
    /// the other editions.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Backup.SnapshotReadiness>? Snapshots { get; set; }
}

public sealed class InventoryDestination
{
    public required string TaskId { get; set; }

    public required string DestinationId { get; set; }

    public required string Kind { get; set; }

    public string? Path { get; set; }

    public long? TotalBytes { get; set; }

    public long? FreeBytes { get; set; }

    public List<InventorySet> Sets { get; set; } = [];
}

public sealed class InventorySet
{
    public required string Name { get; set; }

    public string? Type { get; set; }

    public DateTimeOffset? Time { get; set; }

    public long? Bytes { get; set; }

    public DateTimeOffset? VerifiedAt { get; set; }

    public bool? VerifyOk { get; set; }
}

/// <summary>Body of an error answer (A.11).</summary>
public sealed class ErrorAnswer
{
    public string? Error { get; set; }

    public string? Message { get; set; }

    public int? RetryAfterS { get; set; }

    public string? ServerTime { get; set; }
}

/// <summary>
/// What the agent keeps about its server in <c>config\server.json</c>: the binding (A.3) and what has not been sent
/// yet. The registration code is stored here too, but only under DPAPI.
/// </summary>
public sealed class LinkState
{
    /// <summary>The registration code under DPAPI (base64), or null.</summary>
    public string? RegistrationCodeDpapi { get; set; }

    public string? AgentId { get; set; }

    /// <summary>The pinned server key (PEM), or null before the first accepted registration.</summary>
    public string? ServerPublicKey { get; set; }

    public string? ServerName { get; set; }

    /// <summary><c>pending</c>, <c>approved</c> or <c>disabled</c>, as the server last said.</summary>
    public string? Status { get; set; }

    public DateTimeOffset? RegisteredAt { get; set; }

    public DateTimeOffset? LastInventoryAt { get; set; }

    /// <summary>Tasks changed on this computer or by commands since the last configuration snapshot (A.6).</summary>
    public List<TaskChangeNote> Changed { get; set; } = [];

    public List<string> DeletedTaskIds { get; set; } = [];

    /// <summary>Versions of tasks a server edit overwrote (A.4), as JSON, to go with the next snapshot.</summary>
    public List<JsonElement> Overwritten { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>An entry of <c>changed</c> of the configuration snapshot (A.6).</summary>
public sealed class TaskChangeNote
{
    public required string TaskId { get; set; }

    public int Revision { get; set; }

    public string? UpdatedBy { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>
/// An update of the program that has been handed to Custos.Updater: written before the service stops, read when it
/// starts again, so that the result of <c>update.install</c> still reaches the server (A.10).
/// </summary>
public sealed class PendingUpdate
{
    public string? CommandId { get; set; }

    public required string FromVersion { get; set; }

    public required string ToVersion { get; set; }

    public DateTimeOffset StartedAt { get; set; }
}

/// <summary><c>config\update-result.json</c>: what Custos.Updater did (it writes the same fields by hand).</summary>
public sealed class UpdaterResult
{
    public bool Ok { get; set; }

    public string? FromVersion { get; set; }

    public string? ToVersion { get; set; }

    /// <summary>What went wrong, in words; null when it went well.</summary>
    public string? Message { get; set; }

    /// <summary>The old files were put back.</summary>
    public bool RolledBack { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary><c>manifest.json</c> of an update package (A.10).</summary>
public sealed class UpdateManifest
{
    public string Version { get; set; } = string.Empty;

    public int MinProtocol { get; set; }

    /// <summary><c>legacy</c> for a package of the legacy edition (Windows 7 and 8.1); absent for the standard one.</summary>
    public string? Edition { get; set; }

    public List<UpdateManifestFile> Files { get; set; } = [];
}

public sealed class UpdateManifestFile
{
    public string Path { get; set; } = string.Empty;

    public string? Sha256 { get; set; }
}

/// <summary>
/// <c>config\server-setup.json</c>: the address and registration code typed into the installer (section 10.1). The
/// service takes them at its next start, keeps the code under DPAPI and deletes the file.
/// </summary>
public sealed class ServerSetup
{
    public string? ServerUrl { get; set; }

    public string? RegistrationCode { get; set; }
}
