using Custos.Core.Backup;
using Custos.Core.Model;

namespace Custos.Core.Ipc;

/// <summary>
/// Everything the interface shows, as one object (<see cref="PipeProtocol.Methods.Snapshot"/> and
/// <see cref="PipeProtocol.Events.AgentChanged"/>). Progress and log lines come as separate events.
/// </summary>
public sealed class AgentSnapshot
{
    /// <summary>Grows with every change; the interface keeps the snapshot with the highest version.</summary>
    public long Version { get; set; }

    /// <summary>Revision of the configuration (tasks.json).</summary>
    public long ConfigRevision { get; set; }

    public string ComputerName { get; set; } = string.Empty;

    public List<TaskDefinition> Tasks { get; set; } = [];

    /// <summary>Stored accounts, without their secrets.</summary>
    public List<Credential> Credentials { get; set; } = [];

    public AgentSettings Settings { get; set; } = new();

    /// <summary>One entry per task: last runs, next run, queued or running.</summary>
    public List<TaskState> States { get; set; } = [];

    /// <summary>Runs waiting in the queue, in the order they will run.</summary>
    public List<QueuedRun> Queue { get; set; } = [];

    /// <summary>The run in progress, or null.</summary>
    public CurrentRun? Current { get; set; }

    /// <summary>Problems of the service the user should know about (unreadable configuration and the like).</summary>
    public List<string> Problems { get; set; } = [];

    /// <summary>Version of the 7z.dll the service uses, like "26.06"; null if it could not be loaded.</summary>
    public string? SevenZipVersion { get; set; }

    /// <summary>Why 7z.dll cannot be used (tasks with 7z fail); null when it works.</summary>
    public string? SevenZipProblem { get; set; }

    /// <summary>The computer is about to shut down, restart or sleep (section 6.9); every session shows a countdown.</summary>
    public PowerCountdown? Power { get; set; }
}

/// <summary>An action after a run that is counting down; <c>power.cancel</c> stops it.</summary>
public sealed class PowerCountdown
{
    /// <summary>One of <see cref="Model.AfterActions"/>.</summary>
    public string Action { get; set; } = Model.AfterActions.Shutdown;

    /// <summary>The task whose run asked for it.</summary>
    public string TaskName { get; set; } = string.Empty;

    /// <summary>When it happens unless cancelled.</summary>
    public DateTimeOffset Deadline { get; set; }
}

/// <summary>What the service knows about the runs of one task.</summary>
public sealed class TaskState
{
    public required string TaskId { get; set; }

    public RunRecord? LastRun { get; set; }

    /// <summary>The last run with status success or warning.</summary>
    public RunRecord? LastSuccess { get; set; }

    /// <summary>The next schedule slot, or null for a manual or disabled task.</summary>
    public DateTimeOffset? NextRun { get; set; }

    public bool Queued { get; set; }

    public bool Running { get; set; }
}

/// <summary>A run waiting in the queue.</summary>
public sealed class QueuedRun
{
    public required string TaskId { get; set; }

    /// <summary>One of <see cref="RunTriggers"/>.</summary>
    public required string Trigger { get; set; }

    public DateTimeOffset? ScheduledFor { get; set; }

    public string? RequestedBy { get; set; }

    public DateTimeOffset QueuedAt { get; set; }
}

/// <summary>The run in progress.</summary>
public sealed class CurrentRun
{
    public required string RunId { get; set; }

    public required string TaskId { get; set; }

    public required string TaskName { get; set; }

    public required string Trigger { get; set; }

    public DateTimeOffset? ScheduledFor { get; set; }

    public string? RequestedBy { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public bool Paused { get; set; }

    /// <summary>Being stopped: the stop was asked for, the run is cleaning up.</summary>
    public bool Stopping { get; set; }

    public BackupProgress? Progress { get; set; }
}

/// <summary>Payload of <see cref="PipeProtocol.Events.RunProgress"/>.</summary>
public sealed class RunProgressEvent
{
    public required string RunId { get; set; }

    public required string TaskId { get; set; }

    public bool Paused { get; set; }

    public required BackupProgress Progress { get; set; }
}

/// <summary>One line of the live log (<see cref="PipeProtocol.Events.LogLines"/>).</summary>
public sealed class LogLine
{
    /// <summary>Grows by one with every line since the service started.</summary>
    public long Seq { get; set; }

    public DateTimeOffset Time { get; set; }

    /// <summary>One of <see cref="LogLineLevels"/>.</summary>
    public string Level { get; set; } = LogLineLevels.Info;

    public string? TaskId { get; set; }

    public string? RunId { get; set; }

    public string Text { get; set; } = string.Empty;
}

/// <summary>Values of <see cref="LogLine.Level"/>.</summary>
public static class LogLineLevels
{
    public const string Detail = "detail";
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";

    public static string From(RunLogLevel level) => level switch
    {
        RunLogLevel.Detail => Detail,
        RunLogLevel.Warning => Warning,
        RunLogLevel.Error => Error,
        _ => Info,
    };
}

/// <summary>A request without parameters.</summary>
public sealed class EmptyRequest
{
    public static readonly EmptyRequest Instance = new();
}

/// <summary>A request about one task.</summary>
public sealed class TaskRequest
{
    public required string TaskId { get; set; }
}

/// <summary>Parameters of <see cref="PipeProtocol.Methods.HistoryGet"/>.</summary>
public sealed class HistoryRequest
{
    public required string TaskId { get; set; }

    public int Limit { get; set; } = 200;
}

public sealed class HistoryResult
{
    public List<RunRecord> Runs { get; set; } = [];
}

public sealed class LogResult
{
    public List<LogLine> Lines { get; set; } = [];
}

/// <summary>Parameters of <see cref="PipeProtocol.Methods.TaskSave"/>.</summary>
public sealed class TaskSaveRequest
{
    public required TaskDefinition Task { get; set; }

    /// <summary>The revision the editor started from; null for a new task. A different stored revision is a conflict.</summary>
    public int? BaseRevision { get; set; }
}

public sealed class TaskSaveResult
{
    public required TaskDefinition Task { get; set; }
}

/// <summary>An account with its password, as the interface sends it. The password never travels back.</summary>
public sealed class CredentialInput
{
    public required Credential Credential { get; set; }

    /// <summary>The new password; null keeps the stored one.</summary>
    public string? Password { get; set; }
}

/// <summary>Parameters of <see cref="PipeProtocol.Methods.TasksImport"/>: new tasks and the accounts they use.</summary>
public sealed class TasksImportRequest
{
    public List<TaskDefinition> Tasks { get; set; } = [];

    public List<CredentialInput> Credentials { get; set; } = [];
}

public sealed class CredentialSaveResult
{
    public required Credential Credential { get; set; }
}

public sealed class CredentialRequest
{
    public required string CredentialId { get; set; }
}

/// <summary>Parameters of <see cref="PipeProtocol.Methods.RunStop"/>, <see cref="PipeProtocol.Methods.RunPause"/>
/// and <see cref="PipeProtocol.Methods.RunResume"/>.</summary>
public sealed class RunControlRequest
{
    /// <summary>The task whose run to act on (running or queued); null = the run in progress.</summary>
    public string? TaskId { get; set; }
}

/// <summary>Parameters of <see cref="PipeProtocol.Methods.DestinationTest"/>.</summary>
public sealed class DestinationTestRequest
{
    public required TaskDestination Destination { get; set; }

    /// <summary>A password typed in the editor but not saved yet; null uses the stored one.</summary>
    public CredentialInput? Credential { get; set; }

    /// <summary>The task being edited, for the variables in the path (section 6.2); null for none.</summary>
    public string? TaskName { get; set; }

    /// <summary>One of <see cref="Model.BackupTypes"/>, for <c>{type}</c>.</summary>
    public string? BackupType { get; set; }

    /// <summary>The profile of the task; null takes the profile of whoever asks, as saving would.</summary>
    public string? ProfileSid { get; set; }
}

public sealed class DestinationTestResult
{
    public bool Ok { get; set; }

    /// <summary>The folder that was tried (for USB: on the disk found).</summary>
    public string? Folder { get; set; }

    public long? FreeBytes { get; set; }

    /// <summary>What went wrong, in words.</summary>
    public string? Error { get; set; }
}

/// <summary>What <see cref="PipeProtocol.Methods.DatabasesFind"/> found: running servers first.</summary>
public sealed class DbFindResult
{
    public List<DbCandidate> Servers { get; set; } = [];
}

/// <summary>A database server, or only the folder of its programs, found on this computer.</summary>
public sealed class DbCandidate
{
    /// <summary>One of <see cref="Model.DbEngines"/>.</summary>
    public required string Engine { get; set; }

    /// <summary>The folder of the client programs (mysqldump, pg_dump …).</summary>
    public required string BinDir { get; set; }

    public string? Version { get; set; }

    /// <summary>Where the server listens; for a server that is not running, the usual address.</summary>
    public string Host { get; set; } = "127.0.0.1";

    public int? Port { get; set; }

    /// <summary>A running server was found; otherwise only its programs.</summary>
    public bool Running { get; set; }
}

/// <summary>Parameters of <see cref="PipeProtocol.Methods.DatabasesTest"/>.</summary>
public sealed class DbTestRequest
{
    public required TaskDatabaseServer Server { get; set; }

    /// <summary>
    /// The account typed in the editor: its user, and the password (null keeps the stored one of the same id, ""
    /// means none). Null uses the stored account of <see cref="TaskDatabaseServer.CredentialId"/>.
    /// </summary>
    public CredentialInput? Credential { get; set; }

    /// <summary>The task being edited, for the variables in the folder of the programs; null for none.</summary>
    public string? TaskName { get; set; }

    /// <summary>The profile of the task; null takes the profile of whoever asks.</summary>
    public string? ProfileSid { get; set; }
}

public sealed class DbTestResult
{
    public bool Ok { get; set; }

    public string? Version { get; set; }

    /// <summary>The databases a dump may take: all but the system ones.</summary>
    public List<string> Databases { get; set; } = [];

    /// <summary>The data folder of the server, if the account may see it.</summary>
    public string? DataDir { get; set; }

    /// <summary>What went wrong, in words.</summary>
    public string? Error { get; set; }
}

/// <summary>Parameters of <see cref="PipeProtocol.Methods.RemoteCreateKey"/>.</summary>
public sealed class RemoteKeyRequest
{
    /// <summary>The server, for the name of the account ("custos@vps.example.org").</summary>
    public required string Host { get; set; }

    public string User { get; set; } = "custos";
}

/// <summary>Parameters of <see cref="PipeProtocol.Methods.RemoteTest"/>.</summary>
public sealed class RemoteTestRequest
{
    /// <summary>The source as the editor has it: address, port, user, key and the pinned key of the server (null
    /// when none is pinned yet: then the answer only shows the key).</summary>
    public required TaskSource Source { get; set; }
}

public sealed class RemoteTestResult
{
    /// <summary>The server answered and custos-helper works.</summary>
    public bool Ok { get; set; }

    /// <summary>The key the server showed, "SHA256:…" (also when it is not the pinned one).</summary>
    public string? HostKeySha256 { get; set; }

    /// <summary>The key did not match the pinned one (or none was pinned): nothing else was asked.</summary>
    public bool HostKeyUnconfirmed { get; set; }

    public string? HelperVersion { get; set; }

    /// <summary>custos-helper is older than the one of this program (still works).</summary>
    public bool HelperOutdated { get; set; }

    public string? Hostname { get; set; }

    public string? Os { get; set; }

    public List<string> Roots { get; set; } = [];

    public long? ServerFreeBytes { get; set; }

    /// <summary>The databases by engine.</summary>
    public List<RemoteDatabases> Databases { get; set; } = [];

    public long Entries { get; set; }

    public long Files { get; set; }

    public long Bytes { get; set; }

    /// <summary>Warnings of custos-helper while listing (unreadable folders …).</summary>
    public string? Warnings { get; set; }

    /// <summary>What went wrong, in words.</summary>
    public string? Error { get; set; }
}

public sealed class RemoteDatabases
{
    public required string Engine { get; set; }

    public List<string> Names { get; set; } = [];

    /// <summary>The list could not be read.</summary>
    public string? Error { get; set; }
}

public sealed class SettingsSaveRequest
{
    public required AgentSettings Settings { get; set; }
}

/// <summary>Result of requests that only say "done".</summary>
/// <summary>What <see cref="PipeProtocol.Methods.SevenZipCheck"/> found out.</summary>
public sealed class SevenZipCheckResult
{
    /// <summary>The version of the 7z.dll in use, or null if none works.</summary>
    public string? Installed { get; set; }

    /// <summary>The newest release on GitHub, or null if it could not be found out.</summary>
    public string? Latest { get; set; }

    public bool UpdateAvailable { get; set; }

    /// <summary>Why the newest version is not known (offline, GitHub refused).</summary>
    public string? Error { get; set; }
}

public sealed class OkResult
{
    public static readonly OkResult Instance = new();

    public bool Ok { get; set; } = true;

    /// <summary>A remark for the user, for example why nothing was queued.</summary>
    public string? Message { get; set; }
}

/// <summary>Where to look for sets to restore (<see cref="PipeProtocol.Methods.RestoreList"/>): the destinations of a
/// task, or a folder (for example a backup disk on a new computer).</summary>
public class RestoreSource
{
    /// <summary>The task whose destinations to search; null = search <see cref="Folder"/>.</summary>
    public string? TaskId { get; set; }

    public string? Folder { get; set; }

    /// <summary>Stored account for a network folder; null = the service's own account.</summary>
    public string? CredentialId { get; set; }

    /// <summary>Password of encrypted archives, typed for this restore; null = the password of the task.</summary>
    public string? Password { get; set; }
}

public sealed class RestoreListRequest : RestoreSource;

/// <summary>A set that can be restored.</summary>
public sealed class RestoreSetInfo
{
    /// <summary>The set folder or the (first) archive file; names the set in <see cref="RestoreStartRequest"/>.</summary>
    public required string Location { get; set; }

    /// <summary>Where it was found, in words: the destination of the task or the folder.</summary>
    public string Where { get; set; } = string.Empty;

    public required SetManifestHeader Header { get; set; }

    /// <summary><c>folder</c>, <c>zip</c> or <c>7z</c>.</summary>
    public string Storage { get; set; } = "folder";

    /// <summary>Size in the destination (archives); 0 for folders.</summary>
    public long Bytes { get; set; }

    /// <summary>Volumes of a split archive (1 = one file).</summary>
    public int Parts { get; set; } = 1;
}

public sealed class RestoreListResult
{
    /// <summary>Newest first.</summary>
    public List<RestoreSetInfo> Sets { get; set; } = [];

    /// <summary>Destinations or archives that could not be read, in words.</summary>
    public List<string> Problems { get; set; } = [];
}

/// <summary>Parameters of <see cref="PipeProtocol.Methods.RestoreStart"/>.</summary>
public sealed class RestoreStartRequest : RestoreSource
{
    /// <summary><see cref="RestoreSetInfo.Location"/> of the set whose date to restore.</summary>
    public required string Location { get; set; }

    /// <summary>Folder to restore into; null = the original places.</summary>
    public string? TargetFolder { get; set; }

    /// <summary>
    /// Folder for the database dumps of the set (they go into its <c>db</c> folder, with README.txt); null =
    /// <see cref="TargetFolder"/>. With neither, the dumps are not restored.
    /// </summary>
    public string? DumpsFolder { get; set; }

    public RestoreExisting Existing { get; set; } = RestoreExisting.Skip;
}

/// <summary>The restore in progress or the last one (<see cref="PipeProtocol.Methods.RestoreStatus"/>).</summary>
public sealed class RestoreStatusResult
{
    public bool Running { get; set; }

    public string? SetName { get; set; }

    public string? Target { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public RestoreProgress? Progress { get; set; }

    /// <summary>Null while running.</summary>
    public RestoreResult? Result { get; set; }

    public string? LogFile { get; set; }
}
