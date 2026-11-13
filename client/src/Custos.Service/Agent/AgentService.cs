using System.Globalization;
using System.Runtime.Versioning;
using Custos.Core;
using Custos.Core.Agent;
using Custos.Core.Backup;
using Custos.Core.Databases;
using Custos.Core.Destinations;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Storage;
using Custos.Core.Text;
using Custos.Service.Platform;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Custos.Service.Agent;

/// <summary>A request the agent refused; <see cref="Code"/> is one of <see cref="PipeProtocol.Errors"/>.</summary>
internal sealed class AgentRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The heart of the service: the configuration, the schedule with catch-up runs, the queue and the run in progress,
/// the live log and the history (sections 6.7 and 6.10). The interfaces talk to it through <see cref="Ipc.PipeServer"/>.
/// </summary>
/// <remarks>
/// Threads: the schedule is checked in <see cref="ExecuteAsync"/>; runs execute one at a time on their own thread
/// (the copy engine is synchronous); requests of the interfaces come from the thread pool. Everything shared is
/// guarded by <see cref="_lock"/> or is thread-safe by itself.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class AgentService(ServiceInfo info, AgentEvents events, ILogger<AgentService> logger) : BackgroundService
{
    private const int HistoryLimit = 1000;
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;
    private static readonly TimeSpan MaxSleep = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LogFlushInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RunnerStopTimeout = TimeSpan.FromSeconds(20);

    private readonly ConfigStore _config = new(DataFolders.Config, new DpapiProtector());
    private readonly PendingRuns _queue = new();
    private readonly LiveLog _liveLog = new();
    private readonly SemaphoreSlim _scheduleWake = new(0);
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly Lock _lock = new();
    private readonly Lock _publishLock = new();
    private readonly Dictionary<string, DateTimeOffset> _lastSlots = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LogLine> _pendingLines = [];
    private readonly List<string> _problems = [];
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AgentDatabase? _database;
    private WindowsBackupPlatform? _platform;
    private ActiveRun? _current;
    private string? _sevenZipProblem;
    private long _version;
    private CancellationToken _stopping;

    /// <summary>Completes once the configuration and the database are loaded.</summary>
    public Task Ready => _ready.Task;

    private AgentDatabase Database => _database ?? throw new AgentRequestException(PipeProtocol.Errors.Internal, "The service is starting.");

    private WindowsBackupPlatform Platform => _platform ?? throw new AgentRequestException(PipeProtocol.Errors.Internal, "The service is starting.");

    /// <summary>
    /// Whether users other than administrators may change tasks and settings (section 4). Off by default:
    /// the service runs as SYSTEM, so this is as good as making every user an administrator.
    /// </summary>
    public bool AllUsersMayEdit => _config.GetSettings().AllowAllUsersEdit;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Leave the start of the host at once; loading the configuration and the database may take a while.
        await Task.Yield();
        _stopping = stoppingToken;
        Thread? runner = null;
        try
        {
            Start();
            runner = new Thread(RunnerLoop) { Name = "CUSTOS runs", IsBackground = true };
            runner.Start();
            _ = FlushLogLoopAsync(stoppingToken);

            var lastCleanup = DateTimeOffset.MinValue;
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - lastCleanup > CleanupInterval)
                {
                    lastCleanup = now;
                    Cleanup(now);
                }

                TimeSpan sleep;
                try
                {
                    sleep = CheckSchedule(now);
                }
#pragma warning disable CA1031 // A broken schedule check must not stop the service; it is retried.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    ServiceLog.ScheduleFailed(logger, ex);
                    sleep = MaxSleep;
                }

                await ((Task)_scheduleWake.WaitAsync(sleep, stoppingToken)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        finally
        {
            _ready.TrySetResult();
            StopRuns(runner);
            await _linkLoop.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    // ---- Requests from the interfaces --------------------------------------------------------------------------

    public AgentSnapshot GetSnapshot()
    {
        // The number is taken before the state is read: a snapshot that read older data always has a lower
        // number than the one published after the change, and the interface keeps the highest.
        long version = Interlocked.Increment(ref _version);
        var tasks = _config.GetTasks();
        var lastRuns = TryDatabase(db => db.GetLastRuns()) ?? [];
        var lastSuccess = TryDatabase(db => db.GetLastSuccessfulRuns()) ?? [];
        var priorities = Priorities(tasks);
        var queue = _queue.Snapshot(id => priorities.TryGetValue(id, out int p) ? p : null);
        var now = DateTimeOffset.UtcNow;
        var zone = TimeZoneInfo.Local;
        ActiveRun? current;
        Dictionary<string, DateTimeOffset> slots;
        List<string> problems;
        PowerCountdown? power;
        lock (_lock)
        {
            current = _current;
            power = _powerCountdown;
            slots = new Dictionary<string, DateTimeOffset>(_lastSlots, StringComparer.OrdinalIgnoreCase);
            problems = [.. _problems];
        }

        var settings = _config.GetSettings();
        settings.ProgramPasswordHash = null;
        return new AgentSnapshot
        {
            Version = version,
            ConfigRevision = _config.ConfigRevision,
            ComputerName = Environment.MachineName,
            Tasks = tasks,
            Credentials = _config.GetCredentials(),
            Settings = settings,
            States =
            [
                .. tasks.Select(t => new TaskState
                {
                    TaskId = t.Id,
                    LastRun = lastRuns.GetValueOrDefault(t.Id),
                    LastSuccess = lastSuccess.GetValueOrDefault(t.Id),
                    NextRun = SlotPlanner.NextRun(t, slots.TryGetValue(t.Id, out var slot) ? slot : null, now, zone),
                    Queued = queue.Any(q => SameId(q.TaskId, t.Id)),
                    Running = current is not null && SameId(current.Task.Id, t.Id),
                }),
            ],
            Queue = queue,
            Current = current?.Describe(),
            Problems = [.. problems, .. _config.Problems],
            SevenZipVersion = _platform?.Archives.SevenZipVersion,
            SevenZipProblem = _sevenZipProblem,
            Power = power,
            Server = _link?.Describe() ?? new ServerLinkInfo(),
        };
    }

    public List<LogLine> GetRecentLog() => _liveLog.Recent();

    public List<RunRecord> GetHistory(string taskId, int limit) => Database.GetHistory(taskId, Math.Clamp(limit, 1, HistoryLimit));

    public OkResult RunTask(string taskId, string user)
    {
        var task = _config.GetTask(taskId) ?? throw NotFound();
        string? refused = Enqueue(task, RunTriggers.Manual, null, user);
        return new OkResult { Ok = refused is null, Message = refused };
    }

    public OkResult RunAll(string user)
    {
        int queued = 0;
        foreach (var task in _config.GetTasks().Where(t => t.Enabled).OrderByDescending(t => t.Priority))
        {
            if (Enqueue(task, RunTriggers.Manual, null, user, quiet: true) is null)
            {
                queued++;
            }
        }

        return new OkResult { Ok = queued > 0 };
    }

    public OkResult Stop(string? taskId, string user)
    {
        ActiveRun? current;
        lock (_lock)
        {
            current = _current;
        }

        if (current is not null && (taskId is null || SameId(taskId, current.Task.Id)))
        {
            current.Stopping = true;
            current.Cancel();
            Log(LogLineLevels.Warning, Messages.Format(Messages.RunStopping, current.Task.Name, user), current.Task.Id, current.RunId);
            PublishSnapshot();
            return OkResult.Instance;
        }

        if (taskId is not null && _queue.Remove(taskId))
        {
            Log(LogLineLevels.Info, Messages.Format(Messages.RemovedFromQueue, _config.GetTask(taskId)?.Name ?? taskId, user), taskId);
            PublishSnapshot();
            return OkResult.Instance;
        }

        return new OkResult { Ok = false };
    }

    /// <summary>
    /// «Stop all tasks» of the tray: empties the queue first (or the next run would start the moment the current one
    /// stops), then stops the run in progress. Ok when there was something to stop.
    /// </summary>
    public OkResult StopAll(string user)
    {
        var removed = _queue.Clear();
        foreach (var run in removed)
        {
            Log(LogLineLevels.Info, Messages.Format(Messages.RemovedFromQueue, _config.GetTask(run.TaskId)?.Name ?? run.TaskId, user), run.TaskId);
        }

        bool stopped = Stop(null, user).Ok;
        if (removed.Count > 0 && !stopped)
        {
            PublishSnapshot();
        }

        return new OkResult { Ok = stopped || removed.Count > 0 };
    }

    public OkResult SetPaused(string? taskId, bool paused, string user)
    {
        ActiveRun? current;
        lock (_lock)
        {
            current = _current;
        }

        if (current is null || (taskId is not null && !SameId(taskId, current.Task.Id)) || current.Pause.IsPaused == paused)
        {
            return new OkResult { Ok = false };
        }

        if (paused)
        {
            current.Pause.Pause();
        }
        else
        {
            current.Pause.Resume();
        }

        Log(LogLineLevels.Info, Messages.Format(paused ? Messages.RunPaused : Messages.RunResumed, current.Task.Name, user), current.Task.Id, current.RunId);
        PublishSnapshot();
        return OkResult.Instance;
    }

    public TaskDefinition SaveTask(TaskSaveRequest request, string user, string? sid)
    {
        // %USERPROFILE% and the like in the paths refer to whoever made the task (section 6.2).
        request.Task.ProfileSid ??= sid;
        TaskChange change;
        try
        {
            change = _config.SaveTask(request.Task, request.BaseRevision, "local:" + user, DateTimeOffset.UtcNow);
        }
        catch (ConfigException ex)
        {
            throw Refused(ex);
        }

        if (change.ScheduleReset)
        {
            SetLastSlot(change.Saved.Id, DateTimeOffset.UtcNow);
        }

        Log(LogLineLevels.Info, Messages.Format(change.Previous is null ? Messages.TaskCreatedBy : Messages.TaskChangedBy, change.Saved.Name, user), change.Saved.Id);
        NoteConfigChange(change.Saved);
        PublishSnapshot();
        _scheduleWake.Release();
        return change.Saved;
    }

    public OkResult DeleteTask(string taskId, string user)
    {
        lock (_lock)
        {
            if (_current is not null && SameId(_current.Task.Id, taskId))
            {
                throw new AgentRequestException(PipeProtocol.Errors.Conflict, Messages.DeleteWhileRunning);
            }
        }

        _queue.Remove(taskId);
        var removed = _config.DeleteTask(taskId) ?? throw NotFound();
        TryDatabase(db =>
        {
            db.ForgetTask(taskId);
            return true;
        });
        lock (_lock)
        {
            _lastSlots.Remove(taskId);
        }

        Log(LogLineLevels.Info, Messages.Format(Messages.TaskDeletedBy, removed.Name, user), taskId);
        NoteConfigChange(deletedTaskId: removed.Id);
        PublishSnapshot();
        return OkResult.Instance;
    }

    public OkResult Import(TasksImportRequest request, string user, string? sid)
    {
        foreach (var task in request.Tasks)
        {
            task.ProfileSid ??= sid;
        }

        List<TaskChange> changes;
        try
        {
            changes = _config.Import(request.Tasks, [.. request.Credentials.Select(c => (c.Credential, c.Password))], "local:" + user, DateTimeOffset.UtcNow);
        }
        catch (ConfigException ex)
        {
            throw Refused(ex);
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var change in changes)
        {
            SetLastSlot(change.Saved.Id, now);
            NoteConfigChange(change.Saved);
        }

        Log(LogLineLevels.Info, Messages.Format(Messages.TasksImportedBy, changes.Count, user));
        PublishSnapshot();
        return OkResult.Instance;
    }

    public Credential SaveCredential(CredentialInput input)
    {
        try
        {
            var saved = _config.SaveCredential(input.Credential, input.Password);
            NoteConfigChange();
            PublishSnapshot();
            return saved;
        }
        catch (ConfigException ex)
        {
            throw Refused(ex);
        }
    }

    public OkResult DeleteCredential(string credentialId)
    {
        try
        {
            if (!_config.DeleteCredential(credentialId))
            {
                throw NotFound();
            }

            Core.Cloud.OAuthTokens.Forget(credentialId);
        }
        catch (ConfigException ex)
        {
            throw Refused(ex);
        }

        NoteConfigChange();
        PublishSnapshot();
        return OkResult.Instance;
    }

    public OkResult SaveSettings(AgentSettings settings, string user)
    {
        try
        {
            _config.SaveSettings(settings);
        }
        catch (ConfigException ex)
        {
            throw Refused(ex);
        }

        ApplyLanguage();
        Log(LogLineLevels.Info, Messages.Format(Messages.SettingsChangedBy, user));
        NoteConfigChange();
        PublishSnapshot();
        return OkResult.Instance;
    }

    /// <summary>"Find" in the database server dialog: running servers and folders of their programs.</summary>
    public DbFindResult FindDatabases() => DbFinder.Find(_stopping);

    /// <summary>Connects to a database server as the task would and lists its databases ("Test connection").</summary>
    public DbTestResult TestDatabase(DbTestRequest request, string? sid)
    {
        ArgumentNullException.ThrowIfNull(request);
        var server = request.Server;
        string? profile = request.ProfileSid ?? sid;
        var variables = new PathVariables(Platform.Variables(profile), profile is not null, request.TaskName ?? string.Empty,
            Platform.ComputerName, BackupTypes.Full, DateTime.Now);
        server.BinDir = variables.Expand(server.BinDir ?? string.Empty, out string? problem);
        if (problem is not null)
        {
            return new DbTestResult { Error = problem };
        }

        if (!DbEngines.IsKnown(server.Engine))
        {
            return new DbTestResult { Error = Messages.Format(Messages.DbEngineUnknown, server.Engine) };
        }

        if (!Path.IsPathFullyQualified(server.BinDir))
        {
            return new DbTestResult { Error = Messages.Format(Messages.PathNotAbsolute, server.BinDir) };
        }

        try
        {
            DbAccount? account;
            if (request.Credential is { } typed)
            {
                string? password = typed.Password;
                if (password is null && _config.GetCredential(typed.Credential.Id) is not null)
                {
                    password = _config.GetPassword(typed.Credential.Id);
                }

                account = new DbAccount(typed.Credential.User, password ?? string.Empty);
            }
            else
            {
                account = server.CredentialId is { } id ? Platform.GetDbAccount(id) : null;
            }

            var client = Platform.CreateDbClient(server, account);
            (client as ProcessDbClient)?.CheckTools();
            var info = client.Inspect(_stopping);
            var system = DbTools.SystemDatabases(server.Engine);
            var comparer = DbTools.NameComparer(server.Engine);
            return new DbTestResult
            {
                Ok = true,
                Version = info.Version,
                Databases = [.. info.Databases.Where(n => !system.Contains(n, comparer))],
                DataDir = info.DataDir,
            };
        }
        catch (Exception ex) when (ex is DbException or BackupException or ConfigException)
        {
            return new DbTestResult { Error = ex.Message };
        }
    }

    /// <summary>Tries to write to a destination as the task would ("Check access" in the editor).</summary>
    public DestinationTestResult TestDestination(DestinationTestRequest request, string? sid)
    {
        var destination = request.Destination;
        string? profile = request.ProfileSid ?? sid;
        var variables = new PathVariables(Platform.Variables(profile), profile is not null, request.TaskName ?? string.Empty,
            Platform.ComputerName, request.BackupType ?? BackupTypes.Full, DateTime.Now);
        destination.Path = variables.Expand(destination.Path ?? string.Empty, out string? problem);
        if (problem is not null)
        {
            return new DestinationTestResult { Error = problem };
        }

        if (!DestinationKinds.IsSupported(destination.Kind))
        {
            return new DestinationTestResult { Error = Messages.Format(Messages.DestinationKindNotSupported, destination.Kind) };
        }

        if (DestinationStores.Problem(destination) is { } lacking)
        {
            return new DestinationTestResult { Error = lacking };
        }

        NetworkLogon? session = null;
        DestinationStore? store = null;
        string? folder = null;
        try
        {
            if (destination.Kind == DestinationKinds.Unc && request.Credential is { Password: { } password } logon)
            {
                session = NetworkLogon.Open(logon.Credential.User, logon.Credential.Domain, password);
            }

            // An account typed in the editor and not saved yet is tried as it is.
            var account = !DestinationKinds.IsFileSystem(destination.Kind) && request.Credential is { Password: { } secret } typed
                ? new StoreAccount(typed.Credential.User, secret)
                : null;
            store = DestinationStores.Open(Platform, destination, session, account);
            folder = DestinationKinds.IsFileSystem(destination.Kind) ? store.Root : store.Label;
            store.CheckReady();
            store.Probe();
            return new DestinationTestResult
            {
                Ok = true,
                Folder = folder,
                FreeBytes = store.GetSpace(store.Root).FreeBytes,
                Fingerprint = (store as RemoteStore)?.SeenFingerprint,
                Details = [.. store.Describe()],
            };
        }
        catch (ServerTrustException ex)
        {
            return new DestinationTestResult
            {
                Folder = folder,
                Error = ex.Message,
                Fingerprint = ex.Seen.Length > 0 ? ex.Seen : null,
                TrustNeeded = ex.Seen.Length > 0,
                FingerprintChanged = ex.Expected is not null,
            };
        }
        catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new DestinationTestResult { Folder = folder, Error = ex.Message };
        }
        finally
        {
            store?.Dispose();
            session?.Dispose();
        }
    }

    // ---- Start and stop ----------------------------------------------------------------------------------------

    private void Start()
    {
        foreach (string problem in DataFolders.Prepare())
        {
            ServiceLog.FolderNotProtected(logger, problem);
        }

        _config.Load();
        ApplyLanguage();
        foreach (string problem in _config.Problems)
        {
            ServiceLog.ConfigProblem(logger, problem);
        }

        _database = OpenDatabase();
        int interrupted = _database.CloseInterruptedRuns(Messages.RunInterrupted);
        if (interrupted > 0)
        {
            ServiceLog.InterruptedRunsClosed(logger, interrupted);
        }

        var shadows = new ShadowCopies(DataFolders.PendingShadowsFile);
        int leftovers = shadows.DeleteLeftovers();
        if (leftovers > 0)
        {
            ServiceLog.LeftoverShadowsDeleted(logger, leftovers);
        }

        _platform = new WindowsBackupPlatform(_config, shadows);
        Core.Archives.SevenZip.SevenZipInstaller.CleanUp(AppContext.BaseDirectory, DataFolders.Updates);
        LoadSevenZip();

        // A task the database has no slot for starts counting now; the others catch up what they missed.
        var now = DateTimeOffset.UtcNow;
        foreach (var task in _config.GetTasks())
        {
            if (_database.GetLastSlot(task.Id) is { } slot)
            {
                lock (_lock)
                {
                    _lastSlots[task.Id] = slot;
                }
            }
            else
            {
                SetLastSlot(task.Id, now);
            }
        }

        _liveLog.Added += line =>
        {
            lock (_pendingLines)
            {
                _pendingLines.Add(line);
            }
        };
        Log(LogLineLevels.Info, Messages.Format(Messages.AgentStarted, info.Version));
        foreach (string problem in _config.Problems)
        {
            Log(LogLineLevels.Error, problem);
        }

        StartMonitor(_stopping);
        _ready.TrySetResult();
    }

    /// <summary>
    /// Opens the database. A damaged one is put aside and a new one started: the history and the change tracking
    /// start over (the next run of each task is a full copy), but the backups go on.
    /// </summary>
    private AgentDatabase OpenDatabase()
    {
        try
        {
            return new AgentDatabase(DataFolders.DatabaseFile);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            ServiceLog.DatabaseFailed(logger, ex);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                string file = DataFolders.DatabaseFile + suffix;
                if (File.Exists(file))
                {
                    File.Move(file, $"{DataFolders.DatabaseFile}.damaged-{stamp}{suffix}");
                }
            }

            var database = new AgentDatabase(DataFolders.DatabaseFile);
            lock (_lock)
            {
                _problems.Add(Messages.DatabaseRecreated);
            }

            return database;
        }
    }

    private void StopRuns(Thread? runner)
    {
        ActiveRun? current;
        lock (_lock)
        {
            current = _current;
        }

        if (current is not null)
        {
            Log(LogLineLevels.Warning, Messages.ServiceStopping, current.Task.Id, current.RunId);
            current.Cancel();
        }

        _queueSignal.Release();
        if (runner is not null && !runner.Join(RunnerStopTimeout))
        {
            ServiceLog.RunnerDidNotStop(logger, (int)RunnerStopTimeout.TotalSeconds);
        }
    }

    private void ApplyLanguage()
    {
        Messages.Culture = _config.GetSettings().Language switch
        {
            "ru" => CultureInfo.GetCultureInfo("ru-RU"),
            "en" => CultureInfo.GetCultureInfo("en-US"),
            _ => null,
        };
    }

    private void Cleanup(DateTimeOffset now)
    {
        var before = now.AddDays(-_config.GetSettings().LogRetentionDays);
        int files = RunLogFile.DeleteOld(DataFolders.RunLogs, before);
        int runs = TryDatabase(db => db.DeleteRunsBefore(before));
        if (files + runs > 0)
        {
            ServiceLog.OldLogsDeleted(logger, files, runs);
        }
    }

    // ---- Schedule and queue ------------------------------------------------------------------------------------

    /// <summary>Queues what is due and says how long to sleep until the next slot.</summary>
    private TimeSpan CheckSchedule(DateTimeOffset now)
    {
        // The time zone or its rules may have changed since the last check.
        TimeZoneInfo.ClearCachedData();
        var zone = TimeZoneInfo.Local;
        var sleep = MaxSleep;
        foreach (var task in _config.GetTasks().Where(t => t.Enabled))
        {
            DateTimeOffset? last;
            lock (_lock)
            {
                last = _lastSlots.TryGetValue(task.Id, out var slot) ? slot : null;
            }

            if (last is null)
            {
                SetLastSlot(task.Id, now);
                last = now;
            }

            var check = SlotPlanner.Check(task.Schedule, last, now, zone);
            if (check.Slot is { } due)
            {
                string at = due.ToLocalTime().ToString("g", Messages.FormatCulture);
                switch (check.Decision)
                {
                    case SlotDecision.Run:
                        Enqueue(task, RunTriggers.Schedule, due, null);
                        break;
                    case SlotDecision.CatchUp:
                        if (Enqueue(task, RunTriggers.CatchUp, due, null) is null)
                        {
                            Log(LogLineLevels.Info, Messages.Format(Messages.CatchUpRun, task.Name, at), task.Id);
                            ServiceLog.CatchUpRun(logger, task.Name, at);
                        }

                        break;
                    case SlotDecision.Missed:
                        Log(LogLineLevels.Warning, Messages.Format(task.Schedule.CatchUp ? Messages.MissedTooOld : Messages.MissedNoCatchUp, task.Name, at), task.Id);
                        break;
                }

                SetLastSlot(task.Id, due);
                last = due;
            }

            if (SlotPlanner.NextRun(task, last, now, zone) is { } next && next - now < sleep)
            {
                sleep = next - now;
            }
        }

        return sleep < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : sleep;
    }

    /// <returns>Null if queued, otherwise why not, in words.</returns>
    private string? Enqueue(TaskDefinition task, string trigger, DateTimeOffset? scheduledFor, string? requestedBy, bool quiet = false)
    {
        bool busy;
        lock (_lock)
        {
            busy = _current is not null && SameId(_current.Task.Id, task.Id);
        }

        var entry = new QueuedRun
        {
            TaskId = task.Id,
            Trigger = trigger,
            ScheduledFor = scheduledFor,
            RequestedBy = requestedBy,
            QueuedAt = DateTimeOffset.UtcNow,
        };
        if (busy || !_queue.TryEnqueue(entry))
        {
            string message = trigger == RunTriggers.Manual
                ? Messages.Format(Messages.AlreadyQueued, task.Name)
                : Messages.Format(Messages.SlotSkippedBusy, task.Name, scheduledFor?.ToLocalTime().ToString("g", Messages.FormatCulture));
            if (!quiet)
            {
                Log(LogLineLevels.Warning, message, task.Id);
            }

            return message;
        }

        Log(LogLineLevels.Info, Messages.Format(Messages.Queued, task.Name, Words.Trigger(trigger, scheduledFor, requestedBy)), task.Id);
        _queueSignal.Release();
        PublishSnapshot();
        return null;
    }

    private void RunnerLoop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                // With an action after a run waiting, the wait ends when its countdown is over.
                if (!_queueSignal.Wait(PowerWait(), _stopping))
                {
                    FirePowerIfDue();
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (!_stopping.IsCancellationRequested)
            {
                var priorities = Priorities(_config.GetTasks());
                if (_queue.TryDequeue(id => priorities.TryGetValue(id, out int p) ? p : null) is not { } next)
                {
                    break;
                }

                try
                {
                    // An update of 7z.dll or of the program finishes first; one sent by the server goes before this run.
                    while (Volatile.Read(ref _sevenZipWanted) > 0 && !_stopping.IsCancellationRequested)
                    {
                        Thread.Sleep(200);
                    }

                    _sevenZipGate.Wait(_stopping);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                PostponePower();
                try
                {
                    Execute(next);
                }
#pragma warning disable CA1031 // One broken run must not stop the queue.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    ServiceLog.RunCrashed(logger, ex, next.TaskId);
                    lock (_lock)
                    {
                        _current = null;
                    }

                    PublishSnapshot();
                }
                finally
                {
                    _sevenZipGate.Release();
                }
            }
        }
    }

    // ---- One run -----------------------------------------------------------------------------------------------

    private void Execute(QueuedRun queued)
    {
        var task = _config.GetTask(queued.TaskId);
        if (task is null)
        {
            return;
        }

        var settings = _config.GetSettings();
        string level = task.LogLevel is LogLevels.Summary or LogLevels.Files ? task.LogLevel : settings.DefaultLogLevel;
        string runId = Guid.NewGuid().ToString();
        var startedAt = DateTimeOffset.UtcNow;
        using var file = RunLogFile.Create(DataFolders.RunLogs, task.Name, runId, startedAt);
        using var pause = new PauseGate();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        var active = new ActiveRun(runId, task, queued, startedAt, pause, cancel);
        var record = new RunRecord
        {
            RunId = runId,
            TaskId = task.Id,
            TaskName = task.Name,
            BackupType = task.BackupType,
            Trigger = queued.Trigger,
            ScheduledFor = queued.ScheduledFor,
            RequestedBy = queued.RequestedBy,
            StartedAt = startedAt,
        };

        // The dumps of the last run tell how many bytes the dumps will add now: their size is unknown until taken.
        long dumpBytesHint = task.DbServers.Count == 0 ? 0
            : TryDatabase(db => db.GetHistory(task.Id, 10).FirstOrDefault(r => r.Databases.Count > 0)?.Databases.Sum(d => d.Bytes) ?? 0);
        TryDatabase(db =>
        {
            db.SaveRun(record);
            return true;
        });
        ReportRunStart(record, task.Revision);
        lock (_lock)
        {
            _current = active;
        }

        PublishSnapshot();
        ServiceLog.TaskStarted(logger, task.Name, queued.Trigger, file.Path);
        var log = new RunLog(this, file, task.Id, runId);
        bool low = settings.ProcessPriority != "normal";
        SetPriority(low);

        BackupResult result;
        try
        {
            var engine = new BackupEngine(Platform, Database, log, progress => OnProgress(active, progress));
            result = engine.Run(new BackupRequest
            {
                Task = task,
                RunId = runId,
                Trigger = queued.Trigger,
                ScheduledFor = queued.ScheduledFor,
                StartedAt = startedAt,
                LogEveryFile = level == LogLevels.Files,
                TimeZone = TimeZoneInfo.Local,
                LowPriority = low,
                DumpBytesHint = dumpBytesHint,
            }, pause, cancel.Token);
        }
#pragma warning disable CA1031 // Whatever broke, the run ends with an error and the queue goes on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ServiceLog.RunCrashed(logger, ex, task.Name);
            string message = Messages.Format(Messages.InternalError, ex.Message);
            log.Write(RunLogLevel.Error, message);
            result = new BackupResult { BackupType = task.BackupType, Status = RunStatuses.Error, Errors = 1, ErrorMessage = message };
        }
        finally
        {
            SetPriority(low: false);
        }

        if (result.Status == RunStatuses.Aborted && _stopping.IsCancellationRequested)
        {
            result.ErrorMessage ??= Messages.ServiceStopping;
        }

        var finishedAt = DateTimeOffset.UtcNow;
        record.BackupType = result.BackupType;
        record.FinishedAt = finishedAt;
        record.Status = result.Status;
        record.SourceFiles = result.SourceFiles;
        record.SourceBytes = result.SourceBytes;
        record.FilesProcessed = result.FilesProcessed;
        record.BytesWritten = result.BytesWritten;
        record.Errors = result.Errors;
        record.Warnings = result.Warnings;
        record.ErrorMessage = result.ErrorMessage;
        record.SetName = result.SetName;
        record.Destinations = result.Destinations;
        record.Databases = result.Databases;
        record.Remote = result.Remote;
        record.Verify = result.Verify.ToRecord();
        TryDatabase(db =>
        {
            db.SaveRun(record);
            return true;
        });

        log.Write(result.Status switch
        {
            RunStatuses.Success => RunLogLevel.Info,
            RunStatuses.Warning or RunStatuses.Aborted => RunLogLevel.Warning,
            _ => RunLogLevel.Error,
        }, Messages.Format(Messages.RunFinished, task.Name, Words.Status(result.Status), result.FilesProcessed,
            Sizes.Format(result.BytesWritten), Sizes.FormatDuration(finishedAt - startedAt)));
        ReportRunFinish(record, result, file.Path);
        ReportToEventLog(task, result);
        NotePowerAction(task, result);

        lock (_lock)
        {
            _current = null;
        }

        PublishSnapshot();
    }

    /// <summary>
    /// "Low" priority (section 6.6): the whole service, with the threads 7-Zip starts, runs below normal while a run
    /// lasts, so the processor is used fully only while nobody else needs it. Files get low I/O priority in the engine.
    /// </summary>
    private void SetPriority(bool low)
    {
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            self.PriorityClass = low ? System.Diagnostics.ProcessPriorityClass.BelowNormal : System.Diagnostics.ProcessPriorityClass.Normal;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            ServiceLog.PriorityNotSet(logger, ex.Message);
        }
    }

    private void ReportToEventLog(TaskDefinition task, BackupResult result)
    {
        if (result.VssFailed)
        {
            ServiceLog.VssFailed(logger, task.Name);
        }

        if (result.OutOfSpace)
        {
            ServiceLog.DestinationFull(logger, task.Name);
        }

        switch (result.Status)
        {
            case RunStatuses.Success:
                string written = Sizes.Format(result.BytesWritten);
                ServiceLog.TaskSucceeded(logger, task.Name, result.FilesProcessed, written);
                break;
            case RunStatuses.Warning:
                ServiceLog.TaskCompletedWithWarnings(logger, task.Name, result.Warnings);
                break;
            case RunStatuses.Aborted:
                ServiceLog.TaskAborted(logger, task.Name, result.ErrorMessage ?? string.Empty);
                break;
            default:
                ServiceLog.TaskFailed(logger, task.Name, result.ErrorMessage ?? string.Empty);
                break;
        }
    }

    private void OnProgress(ActiveRun run, BackupProgress progress)
    {
        run.Progress = progress;
        events.Publish(PipeMessage.Notification(
            PipeProtocol.Events.RunProgress,
            new RunProgressEvent { RunId = run.RunId, TaskId = run.Task.Id, Paused = run.Pause.IsPaused, Progress = progress },
            IpcJsonContext.Default.RunProgressEvent));
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------

    private void PublishSnapshot()
    {
        if (!_ready.Task.IsCompleted)
        {
            return;
        }

        // One at a time, so that a newer version never carries older data.
        lock (_publishLock)
        {
            events.Publish(PipeMessage.Notification(PipeProtocol.Events.AgentChanged, GetSnapshot(), IpcJsonContext.Default.AgentSnapshot));
        }
    }

    private async Task FlushLogLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(LogFlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                List<LogLine> lines;
                lock (_pendingLines)
                {
                    if (_pendingLines.Count == 0)
                    {
                        continue;
                    }

                    lines = [.. _pendingLines];
                    _pendingLines.Clear();
                }

                events.Publish(PipeMessage.Notification(PipeProtocol.Events.LogLines, new LogResult { Lines = lines }, IpcJsonContext.Default.LogResult));
            }
        }
        catch (OperationCanceledException)
        {
            // The service is stopping.
        }
    }

    private void Log(string level, string text, string? taskId = null, string? runId = null) => _liveLog.Add(level, text, taskId, runId);

    private void SetLastSlot(string taskId, DateTimeOffset slot)
    {
        lock (_lock)
        {
            _lastSlots[taskId] = slot;
        }

        TryDatabase(db =>
        {
            db.SetLastSlot(taskId, slot);
            return true;
        });
    }

    /// <summary>Runs a database call; a failure is logged and gives null, so a broken database does not stop the backups.</summary>
    private T? TryDatabase<T>(Func<AgentDatabase, T> action)
    {
        if (_database is null)
        {
            return default;
        }

        try
        {
            return action(_database);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            ServiceLog.DatabaseFailed(logger, ex);
            return default;
        }
    }

    private static Dictionary<string, int> Priorities(List<TaskDefinition> tasks)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            result[task.Id] = task.Priority;
        }

        return result;
    }

    private static AgentRequestException NotFound() => new(PipeProtocol.Errors.NotFound, Messages.ConfigTaskGone);

    private static AgentRequestException Refused(ConfigException ex) => new(
        ex.IsConflict ? PipeProtocol.Errors.Conflict : ex.IsNotFound ? PipeProtocol.Errors.NotFound : PipeProtocol.Errors.Invalid,
        ex.Message);

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The run in progress.</summary>
    private sealed class ActiveRun(string runId, TaskDefinition task, QueuedRun queued, DateTimeOffset startedAt, PauseGate pause, CancellationTokenSource cancel)
    {
        public string RunId { get; } = runId;

        public TaskDefinition Task { get; } = task;

        public PauseGate Pause { get; } = pause;

        public volatile bool Stopping;

        public BackupProgress? Progress { get; set; }

        public void Cancel()
        {
            try
            {
                cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run has just ended.
            }
        }

        public CurrentRun Describe() => new()
        {
            RunId = RunId,
            TaskId = Task.Id,
            TaskName = Task.Name,
            Trigger = queued.Trigger,
            ScheduledFor = queued.ScheduledFor,
            RequestedBy = queued.RequestedBy,
            StartedAt = startedAt,
            Paused = Pause.IsPaused,
            Stopping = Stopping,
            Progress = Progress,
        };
    }

    /// <summary>Where the engine's lines go: the run's file and the live log.</summary>
    private sealed class RunLog(AgentService agent, RunLogFile file, string taskId, string runId) : IRunLog
    {
        public void Write(RunLogLevel level, string message)
        {
            file.Write(level, message);
            agent.Log(LogLineLevels.From(level), message, taskId, runId);
        }
    }
}
