using Custos.Core.Agent;

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

    /// <summary>
    /// The socket of the service on macOS, which stands in for the pipe <see cref="PipeName"/>: in /var/run, where only
    /// root may create files, so no other program can pose as the service. CUSTOS_SOCKET can point elsewhere (tests).
    /// </summary>
    public static string UnixSocketPath =>
        Environment.GetEnvironmentVariable("CUSTOS_SOCKET") is { Length: > 0 } custom ? custom : "/var/run/custos.sock";

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

        /// <summary>The log file of one run: <see cref="RunLogRequest"/> → <see cref="RunLogResult"/>.</summary>
        public const string RunLog = "runs.log";

        /// <summary>Queue a task: <see cref="TaskRequest"/> → <see cref="OkResult"/>.</summary>
        public const string TaskRun = "tasks.run";

        /// <summary>Queue every enabled task: <see cref="EmptyRequest"/> → <see cref="OkResult"/>.</summary>
        public const string RunAll = "tasks.run_all";

        /// <summary>Stop a run or take it out of the queue: <see cref="RunControlRequest"/> → <see cref="OkResult"/>.</summary>
        public const string RunStop = "runs.stop";

        /// <summary>Empty the queue and stop the run in progress: <see cref="EmptyRequest"/> → <see cref="OkResult"/>.</summary>
        public const string RunStopAll = "runs.stop_all";

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

        /// <summary>Administrators. Switch several tasks on or off at once: <see cref="TasksEnableRequest"/> →
        /// <see cref="TasksEnableResult"/>.</summary>
        public const string TasksEnable = "tasks.enable";

        /// <summary>Administrators only, always: the file of exported tasks, with the secrets of their accounts when a
        /// password is given (section 9a). <see cref="TasksExportRequest"/> → <see cref="TasksExportResult"/>.</summary>
        public const string TasksExport = "tasks.export";

        /// <summary>Administrators. <see cref="CredentialInput"/> → <see cref="CredentialSaveResult"/>.</summary>
        public const string CredentialSave = "credentials.save";

        /// <summary>Administrators. <see cref="CredentialRequest"/> → <see cref="OkResult"/>.</summary>
        public const string CredentialDelete = "credentials.delete";

        /// <summary>Administrators. Try writing to a destination: <see cref="DestinationTestRequest"/> → <see cref="DestinationTestResult"/>.</summary>
        public const string DestinationTest = "destinations.test";

        /// <summary>Administrators. Database servers and their programs on this computer: <see cref="EmptyRequest"/> →
        /// <see cref="DbFindResult"/>.</summary>
        public const string DatabasesFind = "databases.find";

        /// <summary>Administrators. Connect to a database server and list its databases: <see cref="DbTestRequest"/> →
        /// <see cref="DbTestResult"/>.</summary>
        public const string DatabasesTest = "databases.test";

        /// <summary>Administrators. A new SSH key for a Linux server, kept by the service: <see cref="RemoteKeyRequest"/> →
        /// <see cref="CredentialSaveResult"/> (with the public key).</summary>
        public const string RemoteCreateKey = "remote.create_key";

        /// <summary>Administrators. Connect to a Linux server with the key of a task and ask custos-helper what it has:
        /// <see cref="RemoteTestRequest"/> → <see cref="RemoteTestResult"/>.</summary>
        public const string RemoteTest = "remote.test";

        /// <summary>Administrators. <see cref="SettingsSaveRequest"/> → <see cref="OkResult"/>.</summary>
        public const string SettingsSave = "settings.save";

        /// <summary>Everyone. Whether a program password is set and whether this connection has typed it:
        /// <see cref="EmptyRequest"/> → <see cref="PasswordStateResult"/>.</summary>
        public const string PasswordState = "password.state";

        /// <summary>Everyone. The program password typed by the user (section 4): this connection may change things for
        /// <see cref="ProgramPassword.UnlockTime"/> after its last change. <see cref="PasswordRequest"/> →
        /// <see cref="OkResult"/>, or the error <see cref="Errors.Invalid"/> for a wrong password.</summary>
        public const string PasswordUnlock = "password.unlock";

        /// <summary>Everyone. Forgets the typed password of this connection (the main window was closed):
        /// <see cref="EmptyRequest"/> → <see cref="OkResult"/>.</summary>
        public const string PasswordLock = "password.lock";

        /// <summary>Administrators only, always. Sets, changes or removes the program password:
        /// <see cref="PasswordRequest"/> (null = remove) → <see cref="OkResult"/>.</summary>
        public const string PasswordSet = "password.set";

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

        /// <summary>Administrators only, always: asks GitHub for the newest 7-Zip. <see cref="EmptyRequest"/> →
        /// <see cref="SevenZipCheckResult"/>.</summary>
        public const string SevenZipCheck = "sevenzip.check";

        /// <summary>Administrators only, always: downloads, checks and installs the newest 7-Zip (takes up to a few
        /// minutes). <see cref="EmptyRequest"/> → <see cref="OkResult"/>.</summary>
        public const string SevenZipUpdate = "sevenzip.update";

        /// <summary>The address of the monitoring server and the registration code (section 8, A.3). Administrators only.</summary>
        public const string ServerConnect = "server.connect";

        /// <summary>Forgets the monitoring server and its key (A.3). Administrators only.</summary>
        public const string ServerReset = "server.reset";

        /// <summary>Administrators only, always. Starts connecting an account of a cloud drive (section 6.3):
        /// <see cref="CloudBeginRequest"/> → <see cref="CloudBeginResult"/>, the page to open in the browser.</summary>
        public const string CloudBegin = "cloud.begin";

        /// <summary>Administrators only, always. What came back from the browser; the service trades it for tokens and
        /// stores the account: <see cref="CloudFinishRequest"/> → <see cref="CredentialSaveResult"/>.</summary>
        public const string CloudFinish = "cloud.finish";

        /// <summary>Administrators only, always. <see cref="EmptyRequest"/> → <see cref="CloudAppsResult"/>.</summary>
        public const string CloudAppsGet = "cloud.apps_get";

        /// <summary>Administrators only, always. <see cref="CloudAppsSaveRequest"/> → <see cref="OkResult"/>.</summary>
        public const string CloudAppsSave = "cloud.apps_save";

        /// <summary>Administrators only, always: asks GitHub for a new version of the program now.
        /// <see cref="EmptyRequest"/> → <see cref="ProgramUpdateInfo"/>.</summary>
        public const string ProgramCheck = "program.check";

        /// <summary>Administrators only, always: downloads the newest version from GitHub, checks it and hands it to
        /// Custos.Updater, which restarts the service. <see cref="EmptyRequest"/> → <see cref="OkResult"/>.</summary>
        public const string ProgramUpdate = "program.update";
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

        /// <summary>A program password is set and this connection has not typed it (or did so too long ago): ask
        /// for it with <see cref="Methods.PasswordUnlock"/> and send the request again.</summary>
        public const string PasswordRequired = "password_required";
    }
}
