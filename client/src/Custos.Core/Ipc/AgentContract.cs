using System.Text.Json.Serialization;
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

    /// <summary>A program password is set (section 4). The hash itself never leaves the service: in
    /// <see cref="Settings"/> it is always null.</summary>
    public bool ProgramPasswordSet { get; set; }

    /// <summary>The connection to the monitoring server (section 8).</summary>
    public ServerLinkInfo Server { get; set; } = new();

    /// <summary>New versions of the program on GitHub, for a computer without a monitoring site.</summary>
    public ProgramUpdateInfo ProgramUpdate { get; set; } = new();

    /// <summary>CUSTOS for Linux: whether the sources of the tasks with snapshots on can have them; empty elsewhere.</summary>
    public List<SnapshotReadiness> Snapshots { get; set; } = [];
}

/// <summary>What the service knows about new versions of the program on GitHub.</summary>
public sealed class ProgramUpdateInfo
{
    /// <summary>The service asks GitHub once a day: the setting is on and no monitoring site is connected.</summary>
    public bool Checking { get; set; }

    /// <summary>A monitoring site is connected: it decides about updates, GitHub is not asked.</summary>
    public bool ManagedBySite { get; set; }

    /// <summary>The package manager owns the installed service files.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ManagedByPackage { get; set; }

    /// <summary>When GitHub was last asked; null if never.</summary>
    public DateTimeOffset? CheckedAt { get; set; }

    /// <summary>The newest version on GitHub; null when it is not known.</summary>
    public string? Latest { get; set; }

    /// <summary><see cref="Latest"/> is newer than this program and can be installed.</summary>
    public bool Available { get; set; }

    /// <summary>The page of the release on GitHub: what is new.</summary>
    public string? PageUrl { get; set; }

    /// <summary>Why the last check failed; null when it worked.</summary>
    public string? Error { get; set; }

    /// <summary>An update is being downloaded or installed (from GitHub or from the site).</summary>
    public bool Installing { get; set; }
}

/// <summary>The connection to the monitoring server as the program window shows it.</summary>
public sealed class ServerLinkInfo
{
    /// <summary>One of <see cref="ServerLinkStates"/>.</summary>
    public string State { get; set; } = ServerLinkStates.Off;

    /// <summary>What is wrong, in words, when the state is not a good one.</summary>
    public string? Detail { get; set; }

    public string? ServerUrl { get; set; }

    /// <summary>The host name of <see cref="ServerUrl"/>, for the status bar.</summary>
    public string? ServerHost { get; set; }

    public string? ServerName { get; set; }

    /// <summary>Fingerprint of the pinned server key (A.2), to compare with the site.</summary>
    public string? ServerFingerprint { get; set; }

    /// <summary>Fingerprint of the key of this agent; the site shows it on the machine page.</summary>
    public string? AgentFingerprint { get; set; }

    public string? AgentId { get; set; }

    /// <summary>A registration code is stored (it is never shown again).</summary>
    public bool HasCode { get; set; }

    public DateTimeOffset? LastContact { get; set; }

    /// <summary>Since when the server cannot be reached; null while it can.</summary>
    public DateTimeOffset? OfflineSince { get; set; }

    /// <summary>Reports waiting to be sent.</summary>
    public int Waiting { get; set; }
}

/// <summary>Values of <see cref="ServerLinkInfo.State"/>.</summary>
public static class ServerLinkStates
{
    /// <summary>No server: the program works on its own.</summary>
    public const string Off = "off";

    /// <summary>An address but no registration code.</summary>
    public const string NoCode = "no_code";

    public const string Connecting = "connecting";

    /// <summary>The server did not accept the registration key (403 bad_registration_key).</summary>
    public const string KeyRejected = "key_rejected";

    /// <summary>The answer to the registration failed a check (signature, fingerprint, pinned key): see the detail.</summary>
    public const string Refused = "refused";

    public const string Pending = "pending";

    public const string Approved = "approved";

    public const string Disabled = "disabled";

    /// <summary>The address of this computer is not in the networks the server allows for agents.</summary>
    public const string Forbidden = "forbidden";

    /// <summary>The clocks of this computer and of the server differ too much.</summary>
    public const string ClockSkew = "clock_skew";

    /// <summary>No answer, or no answer with a valid signature.</summary>
    public const string Offline = "offline";
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

    /// <summary>The name of the task when the line was written, for the "Task" column.</summary>
    public string? TaskName { get; set; }

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
    public const int Page = 200;

    public required string TaskId { get; set; }

    public int Limit { get; set; } = Page;

    /// <summary>Only runs started before this time: the next page after the oldest run shown.</summary>
    public DateTimeOffset? Before { get; set; }

    /// <summary>Only runs with warnings, errors or stopped.</summary>
    public bool ProblemsOnly { get; set; }
}

public sealed class HistoryResult
{
    public List<RunRecord> Runs { get; set; } = [];

    /// <summary>There are older runs than these.</summary>
    public bool More { get; set; }
}

/// <summary>Parameters of <see cref="PipeProtocol.Methods.RunLog"/>.</summary>
public sealed class RunLogRequest
{
    public required string RunId { get; set; }

    /// <summary>One of <see cref="Agent.LogFilterLevels"/>.</summary>
    public string Level { get; set; } = Agent.LogFilterLevels.All;

    /// <summary>Only lines with this text.</summary>
    public string? Search { get; set; }
}

/// <summary>The log file of a run: the lines that match, the last <see cref="MaxLines"/> of them.</summary>
public sealed class RunLogResult
{
    public const int MaxLines = 10000;

    /// <summary>False when the file is gone: deleted after the retention period, or never written.</summary>
    public bool Found { get; set; }

    public string? Path { get; set; }

    public List<LogLine> Lines { get; set; } = [];

    /// <summary>Lines in the file.</summary>
    public int Total { get; set; }

    /// <summary>Lines that match the filter; more than <see cref="Lines"/> holds when there were too many.</summary>
    public int Matched { get; set; }

    /// <summary>How many days logs are kept, to tell why a file is gone.</summary>
    public int RetentionDays { get; set; }
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

    /// <summary>
    /// True for a file of exported tasks (section 9a): tasks and accounts keep their ids. A task whose id is here
    /// already is replaced; an account whose id is here stays as it is and only takes a password it lacks. False for
    /// Cobian Backup: everything is new.
    /// </summary>
    public bool KeepIds { get; set; }

    /// <summary>The own applications at the cloud drives that issued the imported tokens; taken for a service that has
    /// none set here.</summary>
    public Cloud.CloudAppsFile? CloudApps { get; set; }
}

/// <summary>Parameters of <see cref="PipeProtocol.Methods.TasksEnable"/>.</summary>
public sealed class TasksEnableRequest
{
    public List<string> TaskIds { get; set; } = [];

    /// <summary>True switches the tasks on, false off.</summary>
    public bool Enabled { get; set; }
}

public sealed class TasksEnableResult
{
    /// <summary>How many tasks changed; those already on (or off) do not count.</summary>
    public int Changed { get; set; }

    /// <summary>Tasks that could not be changed, each «name: why».</summary>
    public List<string> Problems { get; set; } = [];
}

/// <summary>Parameters of <see cref="PipeProtocol.Methods.TasksExport"/>.</summary>
public sealed class TasksExportRequest
{
    /// <summary>The tasks to export; empty = all of them.</summary>
    public List<string> TaskIds { get; set; } = [];

    /// <summary>The export password: the secrets of the accounts go into the file encrypted with it. Null = no secrets.</summary>
    public string? Password { get; set; }

    /// <summary>Also the accounts of the settings that no exported task uses.</summary>
    public bool AllAccounts { get; set; }
}

public sealed class TasksExportResult
{
    /// <summary>The file to save, UTF-8 JSON (<see cref="Transfer.ExportFile"/>).</summary>
    public required string Content { get; set; }

    public int Tasks { get; set; }

    /// <summary>Accounts whose secret could not be read on this computer and is not in the file.</summary>
    public List<string> Unreadable { get; set; } = [];
}

public sealed class CredentialSaveResult
{
    public required Credential Credential { get; set; }
}

/// <summary>Starts connecting an account of a cloud drive (section 6.3).</summary>
public sealed class CloudBeginRequest
{
    /// <summary><c>gdrive</c>, <c>yadisk</c> or <c>dropbox</c>.</summary>
    public required string Kind { get; set; }

    /// <summary>The loopback address the interface listens on (<c>http://127.0.0.1:port/</c>); null = the service
    /// shows the code on its page and the person pastes it.</summary>
    public string? RedirectUri { get; set; }

    /// <summary>The account to connect again (its token is replaced, the tasks keep it); null = a new account.</summary>
    public string? CredentialId { get; set; }
}

public sealed class CloudBeginResult
{
    /// <summary>Names the sign-in in <see cref="CloudFinishRequest"/>; the loopback answer carries it too.</summary>
    public required string State { get; set; }

    /// <summary>The page of the service to open in the browser.</summary>
    public required string Url { get; set; }

    /// <summary>The redirect address actually used; null or empty when the code is shown on the page.</summary>
    public string? RedirectUri { get; set; }
}

public sealed class CloudFinishRequest
{
    public required string State { get; set; }

    /// <summary>What came back: the query of the loopback request, or what the person pasted (the code or the whole
    /// address from the browser).</summary>
    public required string Answer { get; set; }
}

/// <summary>The applications of CUSTOS at the cloud drives (settings, «Облачные диски»).</summary>
public sealed class CloudAppsResult
{
    /// <summary>The administrator's own applications.</summary>
    public Cloud.CloudAppsFile Own { get; set; } = new();

    /// <summary>The services this version has an application for already.</summary>
    public List<string> BuiltIn { get; set; } = [];
}

public sealed class CloudAppsSaveRequest
{
    public required Cloud.CloudAppsFile Apps { get; set; }

    /// <summary>Only change this provider's application. Null replaces all applications as before.</summary>
    public string? Kind { get; set; }
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

    /// <summary>
    /// The SSH key or the TLS certificate the server showed ("SHA256:…"); null for servers that show none. The editor
    /// keeps it in <see cref="TaskDestination.ServerFingerprint"/> once the administrator has agreed.
    /// </summary>
    public string? Fingerprint { get; set; }

    /// <summary>The test stopped only because the destination does not trust <see cref="Fingerprint"/> yet: the
    /// editor asks whether to trust it and tries again.</summary>
    public bool TrustNeeded { get; set; }

    /// <summary>The server showed another key or certificate than the one the destination trusts.</summary>
    public bool FingerprintChanged { get; set; }

    /// <summary>More about the destination, a sentence each (an S3 bucket: Object Lock, versions).</summary>
    public List<string> Details { get; set; } = [];
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

/// <summary><see cref="PipeProtocol.Methods.PasswordUnlock"/> and <see cref="PipeProtocol.Methods.PasswordSet"/>.</summary>
public sealed class PasswordRequest
{
    /// <summary>The program password; for <see cref="PipeProtocol.Methods.PasswordSet"/> null removes it.</summary>
    public string? Password { get; set; }
}

/// <summary><see cref="PipeProtocol.Methods.PasswordState"/>.</summary>
public sealed class PasswordStateResult
{
    /// <summary>A program password is set on this computer.</summary>
    public bool Set { get; set; }

    /// <summary>This connection typed it and may change things now.</summary>
    public bool Unlocked { get; set; }
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
    /// <summary>The task whose destinations to search; null = search <see cref="Destination"/> or <see cref="Folder"/>.</summary>
    public string? TaskId { get; set; }

    /// <summary>A destination on a server or in a cloud drive to search, as the destination dialog gives it (a copy on
    /// another computer: the same account, the folder of the sets); null = <see cref="Folder"/>.</summary>
    public TaskDestination? Destination { get; set; }

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

    /// <summary>The task destination id, if the set was found through a task.</summary>
    public string? DestinationId { get; set; }

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

    /// <summary>If given, the set at <see cref="Location"/> must still have this run id.</summary>
    public string? SetRunId { get; set; }

    /// <summary><see cref="RestoreSetInfo.Where"/> of that set: the destination it lies in. Tells apart sets of the
    /// same name on two servers and saves connecting to the others; null looks in every destination.</summary>
    public string? Where { get; set; }

    /// <summary>When searching by task, open only this destination, even if several have the same label.</summary>
    public string? DestinationId { get; set; }

    /// <summary>Folder to restore into; null = the original places.</summary>
    public string? TargetFolder { get; set; }

    /// <summary>
    /// Folder for the database dumps of the set (they go into its <c>db</c> folder, with README.txt); null =
    /// <see cref="TargetFolder"/>. With neither, the dumps are not restored.
    /// </summary>
    public string? DumpsFolder { get; set; }

    public RestoreExisting Existing { get; set; } = RestoreExisting.Skip;
}

/// <summary>The restore accepted by the service. Older callers read it as <see cref="OkResult"/>.</summary>
public sealed class RestoreStartResult
{
    public bool Ok { get; set; } = true;
    public required string JobId { get; set; }
    public required string LogFile { get; set; }
}

public sealed class RestoreStopRequest
{
    /// <summary>If given, stop only this restore. With no id, stop the restore currently running.</summary>
    public string? JobId { get; set; }
}

/// <summary>The restore in progress or the last one (<see cref="PipeProtocol.Methods.RestoreStatus"/>).</summary>
public sealed class RestoreStatusResult
{
    public bool Running { get; set; }

    public string? JobId { get; set; }
    public string? SetName { get; set; }

    public string? Target { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public RestoreProgress? Progress { get; set; }

    /// <summary>Null while running.</summary>
    public RestoreResult? Result { get; set; }

    public string? LogFile { get; set; }
}

/// <summary><see cref="PipeProtocol.Methods.ServerConnect"/>.</summary>
public sealed class ServerConnectRequest
{
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>The registration code as shown on the site: the key and, after «#», the fingerprint of the server key.</summary>
    public string Code { get; set; } = string.Empty;
}
