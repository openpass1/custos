using Custos.Core;
using Microsoft.Extensions.Logging;

namespace Custos.Service;

/// <summary>
/// Every message the service logs. IDs from <see cref="CustosEventIds"/> also go to the Windows event log;
/// IDs below 1000 are internal and go to the event log only at Error level (as <see cref="CustosEventIds.UnexpectedError"/>).
/// </summary>
internal static partial class ServiceLog
{
    [LoggerMessage(EventId = CustosEventIds.ServiceStarted, Level = LogLevel.Information,
        Message = "CUSTOS service {Version} started ({RunMode}, account {Account})")]
    public static partial void ServiceStarted(ILogger logger, string version, RunMode runMode, string account);

    [LoggerMessage(EventId = CustosEventIds.ServiceStopped, Level = LogLevel.Information,
        Message = "CUSTOS service {Version} stopped")]
    public static partial void ServiceStopped(ILogger logger, string version);

    [LoggerMessage(EventId = 100, Level = LogLevel.Information,
        Message = "Interface pipe {PipeName} is open")]
    public static partial void PipeOpened(ILogger logger, string pipeName);

    [LoggerMessage(EventId = 101, Level = LogLevel.Error,
        Message = "Cannot open the interface pipe {PipeName}; retrying in {RetrySeconds} s. The interface cannot reach the service until then")]
    public static partial void PipeFailed(ILogger logger, Exception exception, string pipeName, int retrySeconds);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information,
        Message = "Interface connected: connection {ConnectionId}, user {User}, administrator {IsAdmin}, session {SessionId}, process {ProcessId}, version {ClientVersion}")]
    public static partial void ClientConnected(ILogger logger, long connectionId, string user, bool isAdmin, int? sessionId, int? processId, string clientVersion);

    [LoggerMessage(EventId = 103, Level = LogLevel.Information,
        Message = "Interface disconnected: connection {ConnectionId}")]
    public static partial void ClientDisconnected(ILogger logger, long connectionId);

    [LoggerMessage(EventId = 104, Level = LogLevel.Warning,
        Message = "Interface connection {ConnectionId} broke the protocol and was closed")]
    public static partial void ClientProtocolError(ILogger logger, Exception exception, long connectionId);

    [LoggerMessage(EventId = 105, Level = LogLevel.Warning,
        Message = "Cannot identify the user of interface connection {ConnectionId}")]
    public static partial void ClientIdentityFailed(ILogger logger, Exception exception, long connectionId);

    [LoggerMessage(EventId = 106, Level = LogLevel.Warning,
        Message = "Interface connection {ConnectionId} uses protocol {ClientProtocol}, the service uses {ServiceProtocol}")]
    public static partial void ClientProtocolMismatch(ILogger logger, long connectionId, int clientProtocol, int serviceProtocol);

    [LoggerMessage(EventId = 107, Level = LogLevel.Error,
        Message = "Request {Method} on interface connection {ConnectionId} failed")]
    public static partial void RequestFailed(ILogger logger, Exception exception, string method, long connectionId);

    [LoggerMessage(EventId = 108, Level = LogLevel.Error,
        Message = "Interface connection {ConnectionId} failed")]
    public static partial void ConnectionFailed(ILogger logger, Exception exception, long connectionId);

    [LoggerMessage(EventId = 109, Level = LogLevel.Warning,
        Message = "User {User} (connection {ConnectionId}) is not allowed to call {Method}")]
    public static partial void RequestForbidden(ILogger logger, string user, long connectionId, string method);

    [LoggerMessage(EventId = 110, Level = LogLevel.Information,
        Message = "User {User} (connection {ConnectionId}) typed the program password")]
    public static partial void PasswordAccepted(ILogger logger, string user, long connectionId);

    [LoggerMessage(EventId = 111, Level = LogLevel.Warning,
        Message = "User {User} (connection {ConnectionId}) typed a wrong program password")]
    public static partial void PasswordRejected(ILogger logger, string user, long connectionId);

    // ---- Agent: schedule, queue and runs ------------------------------------------------------------------------

    [LoggerMessage(EventId = 200, Level = LogLevel.Error,
        Message = "Checking the schedule failed; it is checked again in 30 s")]
    public static partial void ScheduleFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 201, Level = LogLevel.Error,
        Message = "Cannot restrict access to {Problem}. Users other than administrators may be able to change the configuration")]
    public static partial void FolderNotProtected(ILogger logger, string problem);

    [LoggerMessage(EventId = 202, Level = LogLevel.Error,
        Message = "Configuration: {Problem}")]
    public static partial void ConfigProblem(ILogger logger, string problem);

    [LoggerMessage(EventId = 203, Level = LogLevel.Warning,
        Message = "{Count} runs were cut short when the service last stopped; their history records are closed with an error")]
    public static partial void InterruptedRunsClosed(ILogger logger, int count);

    [LoggerMessage(EventId = 204, Level = LogLevel.Information,
        Message = "Deleted {Count} shadow copies left over from the last start of the service")]
    public static partial void LeftoverShadowsDeleted(ILogger logger, int count);

    [LoggerMessage(EventId = 205, Level = LogLevel.Error,
        Message = "The run in progress did not stop within {Seconds} s")]
    public static partial void RunnerDidNotStop(ILogger logger, int seconds);

    [LoggerMessage(EventId = 206, Level = LogLevel.Information,
        Message = "Deleted old run logs: {Files} files and {Runs} history records")]
    public static partial void OldLogsDeleted(ILogger logger, int files, int runs);

    [LoggerMessage(EventId = 207, Level = LogLevel.Error,
        Message = "The run of task {Task} failed unexpectedly")]
    public static partial void RunCrashed(ILogger logger, Exception exception, string task);

    [LoggerMessage(EventId = 208, Level = LogLevel.Error,
        Message = "The database of the service failed; the history and the change tracking may be incomplete")]
    public static partial void DatabaseFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 209, Level = LogLevel.Information,
        Message = "7z.dll {Version} loaded from {Path}")]
    public static partial void SevenZipLoaded(ILogger logger, string version, string path);

    [LoggerMessage(EventId = 210, Level = LogLevel.Information,
        Message = "Restore of {Set} to {Target} started by {User}")]
    public static partial void RestoreStarted(ILogger logger, string set, string target, string user);

    [LoggerMessage(EventId = 212, Level = LogLevel.Warning,
        Message = "The priority of the service could not be changed: {Reason}")]
    public static partial void PriorityNotSet(ILogger logger, string reason);

    [LoggerMessage(EventId = 211, Level = LogLevel.Information,
        Message = "Restore of {Set} finished: {Status}, {Restored} files restored, {Failed} failed")]
    public static partial void RestoreFinished(ILogger logger, string set, string status, long restored, long failed);

    // ---- Agent: events of the Windows event log (section 6.10) --------------------------------------------------

    [LoggerMessage(EventId = CustosEventIds.SevenZipUnavailable, Level = LogLevel.Warning,
        Message = "7z.dll cannot be used: {Reason} Tasks with the 7z format will fail; ZIP and plain copies work")]
    public static partial void SevenZipUnavailable(ILogger logger, string reason);

    [LoggerMessage(EventId = CustosEventIds.SevenZipUpdated, Level = LogLevel.Information,
        Message = "7-Zip updated from {Old} to {New} by {User}; the old file is kept as 7z.dll.old")]
    public static partial void SevenZipUpdated(ILogger logger, string old, string @new, string user);

    [LoggerMessage(EventId = CustosEventIds.SevenZipUpdateFailed, Level = LogLevel.Error,
        Message = "7-Zip was not updated: {Reason}")]
    public static partial void SevenZipUpdateFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = CustosEventIds.TaskStarted, Level = LogLevel.Information,
        Message = "Task {Task} started ({Trigger}). Run log: {LogFile}")]
    public static partial void TaskStarted(ILogger logger, string task, string trigger, string logFile);

    [LoggerMessage(EventId = CustosEventIds.TaskSucceeded, Level = LogLevel.Information,
        Message = "Task {Task} completed successfully: {Files} files, {Size} written")]
    public static partial void TaskSucceeded(ILogger logger, string task, long files, string size);

    [LoggerMessage(EventId = CustosEventIds.TaskCompletedWithWarnings, Level = LogLevel.Warning,
        Message = "Task {Task} completed with {Warnings} warnings. See the run log")]
    public static partial void TaskCompletedWithWarnings(ILogger logger, string task, int warnings);

    [LoggerMessage(EventId = CustosEventIds.TaskFailed, Level = LogLevel.Error,
        Message = "Task {Task} failed: {Error}")]
    public static partial void TaskFailed(ILogger logger, string task, string error);

    [LoggerMessage(EventId = CustosEventIds.TaskAborted, Level = LogLevel.Warning,
        Message = "Task {Task} was aborted: {Reason}")]
    public static partial void TaskAborted(ILogger logger, string task, string reason);

    [LoggerMessage(EventId = CustosEventIds.CatchUpRun, Level = LogLevel.Information,
        Message = "Task {Task} missed its run at {Slot}; a catch-up run is queued")]
    public static partial void CatchUpRun(ILogger logger, string task, string slot);

    [LoggerMessage(EventId = CustosEventIds.PowerAction, Level = LogLevel.Information,
        Message = "After task {Task}: {Action} of the computer now")]
    public static partial void PowerAction(ILogger logger, string task, string action);

    [LoggerMessage(EventId = CustosEventIds.PowerCancelled, Level = LogLevel.Information,
        Message = "After task {Task}: {Action} of the computer cancelled by {User}")]
    public static partial void PowerCancelled(ILogger logger, string task, string action, string user);

    [LoggerMessage(EventId = CustosEventIds.PowerFailed, Level = LogLevel.Error,
        Message = "After task {Task}: {Action} of the computer failed: {Error}")]
    public static partial void PowerFailed(ILogger logger, string task, string action, string error);

    [LoggerMessage(EventId = CustosEventIds.VssFailed, Level = LogLevel.Warning,
        Message = "Task {Task}: the shadow copy (VSS) could not be made")]
    public static partial void VssFailed(ILogger logger, string task);

    [LoggerMessage(EventId = CustosEventIds.DestinationFull, Level = LogLevel.Error,
        Message = "Task {Task}: not enough free space at a destination")]
    public static partial void DestinationFull(ILogger logger, string task);

    [LoggerMessage(EventId = CustosEventIds.ServerConnectionLost, Level = LogLevel.Warning, Message = "{Text}")]
    public static partial void ServerConnectionLost(ILogger logger, string text);

    [LoggerMessage(EventId = CustosEventIds.ServerConnectionRestored, Level = LogLevel.Information, Message = "{Text}")]
    public static partial void ServerConnectionRestored(ILogger logger, string text);

    [LoggerMessage(EventId = 1102, Message = "Monitoring server: {Text}")]
    public static partial void MonitorMessage(ILogger logger, LogLevel level, string text);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Error, Message = "The connection to the monitoring server stopped")]
    public static partial void MonitorCrashed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = CustosEventIds.UpdateInstalled, Level = LogLevel.Information,
        Message = "CUSTOS updated from {Old} to {New} by the monitoring server")]
    public static partial void UpdateInstalled(ILogger logger, string old, string @new);

    [LoggerMessage(EventId = CustosEventIds.UpdateRejected, Level = LogLevel.Error,
        Message = "The update of CUSTOS to {Version} was not installed: {Reason}")]
    public static partial void UpdateRejected(ILogger logger, string version, string reason);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Information,
        Message = "Installing the update of CUSTOS to {Version}: the service stops and Custos.Updater replaces the program files")]
    public static partial void UpdateStarting(ILogger logger, string version);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Information, Message = "GitHub: {Text}")]
    public static partial void ProgramCheck(ILogger logger, string text);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Error, Message = "The daily check of GitHub for a new version failed")]
    public static partial void ProgramCheckCrashed(ILogger logger, Exception exception);
}
