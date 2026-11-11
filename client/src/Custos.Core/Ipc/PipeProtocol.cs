namespace Custos.Core.Ipc;

/// <summary>
/// The local channel between the interface (Custos.exe) and the service (section 3.2 of the client spec).
/// </summary>
/// <remarks>
/// Transport: the named pipe <c>\\.\pipe\custos</c>. Every message is a frame: a 4-byte little-endian length
/// followed by that many bytes of UTF-8 JSON (<see cref="PipeMessage"/>). The interface sends requests, the
/// service answers each with a response carrying the same id and may send events at any time.
/// </remarks>
public static class PipeProtocol
{
    /// <summary>Pipe name; the full Windows path is <c>\\.\pipe\custos</c>.</summary>
    public const string PipeName = "custos";

    /// <summary>Version of this local protocol. The interface and the service are always installed together,
    /// so a mismatch means a broken installation.</summary>
    public const int Version = 1;

    /// <summary>Largest message the service accepts from an interface.</summary>
    public const int MaxRequestBytes = 1024 * 1024;

    /// <summary>Largest message the interface accepts from the service.</summary>
    public const int MaxMessageBytes = 16 * 1024 * 1024;

    /// <summary>Request methods. Methods marked "administrators" are refused to other users (section 4).</summary>
    public static class Methods
    {
        /// <summary>First request on every connection: <see cref="HelloRequest"/> → <see cref="HelloResult"/>.</summary>
        public const string Hello = "hello";

        /// <summary><see cref="EmptyRequest"/> → <see cref="AgentSnapshot"/>.</summary>
        public const string Snapshot = "agent.snapshot";

        /// <summary>The recent lines of the live log: <see cref="EmptyRequest"/> → <see cref="LogResult"/>.</summary>
        public const string LogRecent = "log.recent";

        /// <summary><see cref="HistoryRequest"/> → <see cref="HistoryResult"/>.</summary>
        public const string HistoryGet = "history.get";

        /// <summary>Queue a task: <see cref="TaskRequest"/> → <see cref="OkResult"/>.</summary>
        public const string TaskRun = "tasks.run";

        /// <summary>Queue every enabled task: <see cref="EmptyRequest"/> → <see cref="OkResult"/>.</summary>
        public const string RunAll = "tasks.run_all";

        /// <summary>Stop a run or take it out of the queue: <see cref="RunControlRequest"/> → <see cref="OkResult"/>.</summary>
        public const string RunStop = "runs.stop";

        /// <summary><see cref="RunControlRequest"/> → <see cref="OkResult"/>.</summary>
        public const string RunPause = "runs.pause";

        /// <summary><see cref="RunControlRequest"/> → <see cref="OkResult"/>.</summary>
        public const string RunResume = "runs.resume";

        /// <summary>Administrators. Create or change a task: <see cref="TaskSaveRequest"/> → <see cref="TaskSaveResult"/>.</summary>
        public const string TaskSave = "tasks.save";

        /// <summary>Administrators. <see cref="TaskRequest"/> → <see cref="OkResult"/>.</summary>
        public const string TaskDelete = "tasks.delete";

        /// <summary>Administrators. Add imported tasks with their accounts: <see cref="TasksImportRequest"/> → <see cref="OkResult"/>.</summary>
        public const string TasksImport = "tasks.import";

        /// <summary>Administrators. <see cref="CredentialInput"/> → <see cref="CredentialSaveResult"/>.</summary>
        public const string CredentialSave = "credentials.save";

        /// <summary>Administrators. <see cref="CredentialRequest"/> → <see cref="OkResult"/>.</summary>
        public const string CredentialDelete = "credentials.delete";

        /// <summary>Administrators. Try writing to a destination: <see cref="DestinationTestRequest"/> → <see cref="DestinationTestResult"/>.</summary>
        public const string DestinationTest = "destinations.test";

        /// <summary>Administrators. <see cref="SettingsSaveRequest"/> → <see cref="OkResult"/>.</summary>
        public const string SettingsSave = "settings.save";

        /// <summary>Administrators only, always. Sets to restore: <see cref="RestoreListRequest"/> → <see cref="RestoreListResult"/>.</summary>
        public const string RestoreList = "restore.list";

        /// <summary>Administrators only, always. <see cref="RestoreStartRequest"/> → <see cref="OkResult"/>.</summary>
        public const string RestoreStart = "restore.start";

        /// <summary>Administrators only, always. <see cref="EmptyRequest"/> → <see cref="RestoreStatusResult"/>.</summary>
        public const string RestoreStatus = "restore.status";

        /// <summary>Administrators only, always. <see cref="EmptyRequest"/> → <see cref="OkResult"/>.</summary>
        public const string RestoreStop = "restore.stop";

        /// <summary>Everyone: whoever sits at the computer may keep it on. <see cref="EmptyRequest"/> → <see cref="OkResult"/>.</summary>
        public const string PowerCancel = "power.cancel";
    }

    /// <summary>Event names.</summary>
    public static class Events
    {
        /// <summary>The service state changed: <see cref="ServiceStateEvent"/>.</summary>
        public const string ServiceState = "service.state";

        /// <summary>Tasks, their states, the queue or the settings changed: the new <see cref="AgentSnapshot"/>.</summary>
        public const string AgentChanged = "agent.changed";

        /// <summary>Progress of the run in progress, a few times a second: <see cref="RunProgressEvent"/>.</summary>
        public const string RunProgress = "run.progress";

        /// <summary>New lines of the live log, a few times a second at most: <see cref="LogResult"/>.</summary>
        public const string LogLines = "log.lines";
    }

    /// <summary>Values of <see cref="HelloResult.State"/> and <see cref="ServiceStateEvent.State"/>.</summary>
    public static class ServiceStates
    {
        public const string Running = "running";
        public const string Stopping = "stopping";
    }

    /// <summary>Values of <see cref="PipeError.Code"/>.</summary>
    public static class Errors
    {
        public const string BadRequest = "bad_request";
        public const string UnknownMethod = "unknown_method";
        public const string ProtocolMismatch = "protocol_mismatch";
        public const string Internal = "internal";

        /// <summary>Only administrators may do this (section 4).</summary>
        public const string Forbidden = "forbidden";

        /// <summary>No task, account or run with this id.</summary>
        public const string NotFound = "not_found";

        /// <summary>The task was changed by someone else since the editor opened it.</summary>
        public const string Conflict = "conflict";

        /// <summary>The data is not acceptable; the message says why, in words for the user.</summary>
        public const string Invalid = "invalid";

        /// <summary>The first request must be hello.</summary>
        public const string HelloFirst = "hello_first";
    }
}
