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

public sealed class SettingsSaveRequest
{
    public required AgentSettings Settings { get; set; }
}

/// <summary>Result of requests that only say "done".</summary>
public sealed class OkResult
{
    public static readonly OkResult Instance = new();

    public bool Ok { get; set; } = true;

    /// <summary>A remark for the user, for example why nothing was queued.</summary>
    public string? Message { get; set; }
}
