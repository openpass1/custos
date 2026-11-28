using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Custos.Core.Ipc;
using Custos.Core.Storage;

namespace Custos.Core.Monitor;

/// <summary>What became of a command of the server (A.4).</summary>
public sealed record CommandOutcome(string Status, string? Message)
{
    public MonitorRestoreResult? Restore { get; init; }

    public static CommandOutcome Done(string? message = null) => new(CommandStatuses.Done, message);

    public static CommandOutcome Failed(string message) => new(CommandStatuses.Failed, message);

    public static CommandOutcome Rejected(string message) => new(CommandStatuses.Rejected, message);
}

/// <summary>Kinds of what <see cref="ServerLink"/> tells its host.</summary>
public enum LinkEvent
{
    Info,
    Warning,
    Error,

    /// <summary>No connection for 5 minutes and more (event 1100).</summary>
    ConnectionLost,

    /// <summary>The connection is back after <see cref="ConnectionLost"/> (event 1101).</summary>
    ConnectionRestored,
}

/// <summary>What <see cref="ServerLink"/> needs from the agent.</summary>
public interface IMonitorHost
{
    /// <summary>The address of the server from the settings, or null.</summary>
    string? ServerUrl { get; }

    /// <summary>«Allow the server to change tasks» (A.4).</summary>
    bool AllowRemoteEdit { get; }

    /// <summary>The body of <c>agent/register</c>.</summary>
    RegisterBody DescribeMachine(string publicKeyPem);

    /// <summary>The body of <c>agent/heartbeat</c>.</summary>
    HeartbeatBody DescribeState();

    /// <summary>The body of <c>agent/config</c> (A.6), with the notes of <paramref name="notes"/>.</summary>
    byte[] BuildConfigSnapshot(LinkState notes);

    /// <summary>The progress of the run in progress, or null when there is none or it is paused (A.5).</summary>
    RunProgressBody? Progress();

    /// <summary>Starts building the inventory (A.8); the host queues it with <see cref="ServerLink.Enqueue"/>.</summary>
    void RequestInventory();

    /// <summary>Carries out a command; null when the result is sent later (an update that restarts the service).</summary>
    Task<CommandOutcome?> ExecuteAsync(MonitorCommand command, CancellationToken cancellationToken);

    void Report(LinkEvent kind, string text);

    /// <summary>The state shown in the program window changed.</summary>
    void LinkChanged();
}

/// <summary>
/// The connection to the monitoring server (section 8, appendix A): registration, heartbeat with commands, the queue
/// of reports, configuration snapshots, progress and the inventory every 6 hours. Runs as one loop; the other
/// methods may be called from any thread.
/// </summary>
public sealed class ServerLink : IDisposable
{
    public const string InventoryPath = "agent/inventory";
    public const string InventoryKey = "inventory";

    /// <summary>A snapshot goes this long after the last local change (A.6).</summary>
    public static readonly TimeSpan ConfigDelay = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan InventoryInterval = TimeSpan.FromHours(6);

    /// <summary>After a rejected registration key (A.3, A.11).</summary>
    public static readonly TimeSpan RegistrationRetry = TimeSpan.FromMinutes(10);

    /// <summary>Event 1100 is written after this long without a connection.</summary>
    public static readonly TimeSpan LostAfter = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)];
    private static readonly TimeSpan MaxRateLimit = TimeSpan.FromHours(1);

    /// <summary>Commands that may take long: they run beside the others.</summary>
    private static readonly HashSet<string> LongCommands = new(StringComparer.Ordinal)
    {
        CommandKinds.UpdateInstall, CommandKinds.SevenZipUpdate, CommandKinds.LogFetch, CommandKinds.InventoryRefresh,
        CommandKinds.RestoreList, CommandKinds.RestoreStart,
    };

    private static readonly HashSet<string> KnownCommands = new(StringComparer.Ordinal)
    {
        CommandKinds.TaskRun, CommandKinds.TaskStop, CommandKinds.TaskPause, CommandKinds.TaskResume, CommandKinds.TaskEnable,
        CommandKinds.TaskDisable, CommandKinds.TaskUpsert, CommandKinds.TaskDelete, CommandKinds.SettingsUpdate,
        CommandKinds.LogFetch, CommandKinds.InventoryRefresh, CommandKinds.UpdateInstall, CommandKinds.SevenZipUpdate,
        CommandKinds.RestoreList, CommandKinds.RestoreStart, CommandKinds.RestoreStatus, CommandKinds.RestoreStop,
    };

    private readonly LinkStore _store;
    private readonly AgentDatabase _database;
    private readonly IMonitorHost _host;
    private readonly MonitorClient _client;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Lock _lock = new();
    private readonly Channel<MonitorCommand> _commands = Channel.CreateUnbounded<MonitorCommand>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Queue<string> _seenOrder = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> _detached = [];

    private string _state = ServerLinkStates.Off;
    private string? _detail;
    private string? _lastReported;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextHeartbeat = DateTimeOffset.MinValue;
    private DateTimeOffset? _configDueAt;
    private DateTimeOffset _nextProgress = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastContact;
    private DateTimeOffset? _offlineSince;
    private bool _lostReported;
    private int _failures;
    private TimeSpan _interval = TimeSpan.FromSeconds(60);
    private int _waiting = -1;

    public ServerLink(LinkStore store, AgentDatabase database, IMonitorHost host, MonitorClient client, TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The server has approved this agent and answered the last request.</summary>
    public bool Online
    {
        get
        {
            lock (_lock)
            {
                return _state == ServerLinkStates.Approved;
            }
        }
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>The loop: until <paramref name="cancellationToken"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var commands = CommandLoopAsync(cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                wait = await StepAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // A broken step must not end the connection; it is tried again.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _host.Report(LinkEvent.Error, Messages.Format(Messages.MonitorStepFailed, ex.Message));
                wait = Backoff[0];
            }

            await ((Task)_wake.WaitAsync(Clamp(wait), cancellationToken)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _commands.Writer.TryComplete();
        await commands.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Task[] detached;
        lock (_lock)
        {
            detached = [.. _detached];
        }

        await Task.WhenAll(detached).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>Makes the loop look at once (a report was queued, the address changed).</summary>
    public void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already woken.
        }
    }

    /// <summary>The configuration changed here: a snapshot goes in 10 seconds (A.6).</summary>
    public void ConfigChanged()
    {
        lock (_lock)
        {
            _configDueAt = Now + ConfigDelay;
        }

        Wake();
    }

    /// <summary>Queues a report (runs/start, runs/finish, inventory, a command result); it is sent strictly in order.</summary>
    public void Enqueue(string path, byte[] body, string? key = null)
    {
        _database.AddReport(path, body, key);
        Interlocked.Exchange(ref _waiting, -1);
        Wake();
    }

    /// <summary>
    /// Stores a new address and registration code (from the program window or the installer). With another address
    /// the binding starts over; with the same one the pinned server key stays (A.3).
    /// </summary>
    public void Connect(string code, bool newServer)
    {
        _store.Update(state =>
        {
            state.RegistrationCodeDpapi = _store.ProtectCode(code);
            if (newServer)
            {
                Forget(state);
            }
            else if (state.Status is null)
            {
                state.AgentId = null;
            }
        });
        if (newServer)
        {
            _database.ClearReports();
        }

        lock (_lock)
        {
            _retryAt = DateTimeOffset.MinValue;
            _nextHeartbeat = DateTimeOffset.MinValue;
            _failures = 0;
            _offlineSince = null;
            _lostReported = false;
            _lastReported = null;
        }

        Interlocked.Exchange(ref _waiting, -1);
        SetState(ServerLinkStates.Connecting, null);
        Wake();
    }

    /// <summary>«Reset the binding to the server» (A.3): forgets the server, its key, the code and the queued reports.</summary>
    public void Reset()
    {
        _store.Update(state =>
        {
            Forget(state);
            state.RegistrationCodeDpapi = null;
            state.Changed.Clear();
            state.DeletedTaskIds.Clear();
            state.Overwritten.Clear();
        });
        _database.ClearReports();
        lock (_lock)
        {
            _offlineSince = null;
            _lostReported = false;
            _lastReported = null;
            _configDueAt = null;
        }

        Interlocked.Exchange(ref _waiting, -1);
        SetState(ServerLinkStates.Off, null);
        Wake();
    }

    /// <summary>The state for the program window.</summary>
    public ServerLinkInfo Describe()
    {
        var state = _store.Get();
        string? url = _host.ServerUrl;
        int waiting = Volatile.Read(ref _waiting);
        if (waiting < 0)
        {
            try
            {
                waiting = _database.CountReports();
                Interlocked.Exchange(ref _waiting, waiting);
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                waiting = 0;
            }
        }

        lock (_lock)
        {
            return new ServerLinkInfo
            {
                State = url is null ? ServerLinkStates.Off : _state,
                Detail = _detail,
                ServerUrl = url,
                ServerHost = url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : null,
                ServerName = state.ServerName,
                ServerFingerprint = state.ServerPublicKey is { } pem ? MonitorCrypto.Fingerprint(pem) : null,
                AgentFingerprint = MonitorCrypto.Fingerprint(_store.AgentKey),
                AgentId = state.AgentId,
                HasCode = state.RegistrationCodeDpapi is not null,
                LastContact = _lastContact,
                OfflineSince = _offlineSince,
                Waiting = waiting,
            };
        }
    }

    /// <summary>A signed request to the server for the host (the log of a run, A.7). Not for reports: those are queued.</summary>
    public async Task<MonitorAnswer> SendAsync(string path, byte[] body, CancellationToken cancellationToken)
    {
        var (url, agentId) = Target();
        using var serverKey = MonitorCrypto.LoadPublicPem(_store.Get().ServerPublicKey);
        var answer = await _client.CallAsync(url, "POST", path, body, agentId, _store.AgentKey, serverKey, cancellationToken).ConfigureAwait(false);
        return answer;
    }

    /// <summary>Downloads a package of the server into <paramref name="file"/> (A.10).</summary>
    public async Task<MonitorAnswer> DownloadAsync(string path, string file, long maxBytes, CancellationToken cancellationToken, bool allowRedirects = true)
    {
        var (url, agentId) = Target();
        using var serverKey = MonitorCrypto.LoadPublicPem(_store.Get().ServerPublicKey);
        return await _client.DownloadAsync(url, path, file, maxBytes, agentId, _store.AgentKey, serverKey, cancellationToken, allowRedirects).ConfigureAwait(false);
    }

    /// <summary>Checks a signature of the server made over <paramref name="data"/> with the pinned key (A.10).</summary>
    public bool VerifyServerSignature(string data, string? signature)
    {
        using var serverKey = MonitorCrypto.LoadPublicPem(_store.Get().ServerPublicKey);
        return MonitorCrypto.Verify(serverKey, data, signature);
    }

    public void Dispose()
    {
        _wake.Dispose();
    }

    // ---- The loop ----------------------------------------------------------------------------------------------

    /// <summary>One round: registers, beats, sends what is due. Returns how long to wait before the next round.</summary>
    internal async Task<TimeSpan> StepAsync(CancellationToken cancellationToken)
    {
        string? url = _host.ServerUrl;
        if (url is null)
        {
            SetState(ServerLinkStates.Off, null);
            return Timeout.InfiniteTimeSpan;
        }

        var now = Now;
        DateTimeOffset retryAt;
        lock (_lock)
        {
            retryAt = _retryAt;
        }

        if (now < retryAt)
        {
            return retryAt - now;
        }

        var state = _store.Get();
        if (state.AgentId is null)
        {
            return await RegisterAsync(url, state, cancellationToken).ConfigureAwait(false);
        }

        DateTimeOffset nextHeartbeat;
        lock (_lock)
        {
            nextHeartbeat = _nextHeartbeat;
        }

        if (now >= nextHeartbeat && !await HeartbeatAsync(cancellationToken).ConfigureAwait(false))
        {
            return Wait();
        }

        if (Online)
        {
            if (await FlushAsync(cancellationToken).ConfigureAwait(false)
                && await SendConfigAsync(cancellationToken).ConfigureAwait(false))
            {
                await SendProgressAsync(cancellationToken).ConfigureAwait(false);
                CheckInventory();
            }
        }

        return Wait();
    }

    private TimeSpan Wait()
    {
        var now = Now;
        lock (_lock)
        {
            if (now < _retryAt)
            {
                return _retryAt - now;
            }

            var next = _nextHeartbeat;
            if (_state == ServerLinkStates.Approved)
            {
                if (_configDueAt is { } config && config < next)
                {
                    next = config;
                }

                if (_host.Progress() is not null && _nextProgress < next)
                {
                    next = _nextProgress;
                }
            }

            return next - now;
        }
    }

    private async Task<TimeSpan> RegisterAsync(string url, LinkState state, CancellationToken cancellationToken)
    {
        string? text = _store.GetCode();
        if (text is null)
        {
            SetState(ServerLinkStates.NoCode, state.RegistrationCodeDpapi is null ? null : Messages.MonitorCodeUnreadable);
            return Timeout.InfiniteTimeSpan;
        }

        var code = RegistrationCode.TryParse(text, out string? problem);
        if (code is null)
        {
            SetState(ServerLinkStates.NoCode, problem);
            return Timeout.InfiniteTimeSpan;
        }

        if (_state is ServerLinkStates.Off or ServerLinkStates.NoCode)
        {
            SetState(ServerLinkStates.Connecting, null);
        }

        var body = _host.DescribeMachine(_store.AgentPublicPem);
        var outcome = await _client.RegisterAsync(url, code, body, _store.AgentKey, state.ServerPublicKey, cancellationToken).ConfigureAwait(false);
        if (outcome.Accepted && outcome.Answer is { } answer)
        {
            var now = Now;
            _store.Update(s =>
            {
                s.AgentId = answer.AgentId;
                s.ServerPublicKey = answer.ServerPublicKey;
                s.ServerName = answer.ServerName;
                s.Status = answer.Status;
                s.RegisteredAt = now;
            });
            Contact(now);
            lock (_lock)
            {
                _nextHeartbeat = DateTimeOffset.MinValue;
            }

            _host.Report(LinkEvent.Info, Messages.Format(Messages.MonitorRegistered, Host(url), answer.AgentId,
                MonitorCrypto.Fingerprint(answer.ServerPublicKey)));
            SetState(answer.Status == ServerLinkStates.Approved ? ServerLinkStates.Approved : ServerLinkStates.Pending, null);
            return TimeSpan.Zero;
        }

        if (outcome.KeyRejected)
        {
            Contact(Now);
            RetryIn(RegistrationRetry);
            SetState(ServerLinkStates.KeyRejected, outcome.Reason);
            return RegistrationRetry;
        }

        if (outcome.Unreachable)
        {
            Unreachable(outcome.Raw);
            return Wait();
        }

        // A signed refusal, or an answer that failed the checks of A.3.
        Contact(Now);
        RetryIn(RegistrationRetry);
        SetState(outcome.Raw.Status == 403 && outcome.Raw.Error == "forbidden" ? ServerLinkStates.Forbidden : ServerLinkStates.Refused, outcome.Reason);
        return RegistrationRetry;
    }

    /// <summary>The heartbeat (A.4). False when the round should end here.</summary>
    private async Task<bool> HeartbeatAsync(CancellationToken cancellationToken)
    {
        string? restoreBinding = RestoreBinding;
        var body = JsonSerializer.SerializeToUtf8Bytes(_host.DescribeState(), MonitorJsonContext.Default.HeartbeatBody);
        var answer = await CallAsync("agent/heartbeat", body, cancellationToken).ConfigureAwait(false);
        if (!answer.Ok)
        {
            Failed(answer);
            return false;
        }

        var beat = answer.Read(MonitorJsonContext.Default.HeartbeatAnswer);
        if (beat is null)
        {
            Unreachable(answer);
            return false;
        }

        var now = Now;
        Contact(now);
        string status = beat.Status is ServerLinkStates.Approved or ServerLinkStates.Pending or ServerLinkStates.Disabled ? beat.Status : ServerLinkStates.Pending;
        if (_store.Get().Status != status)
        {
            _store.Update(s => s.Status = status);
            if (status == ServerLinkStates.Approved)
            {
                _host.Report(LinkEvent.Info, Messages.MonitorApproved);
            }
        }

        lock (_lock)
        {
            // A disabled agent sends only heartbeats, as often as the server says: rarely, but so that enabling it on
            // the site takes effect within minutes (A.4).
            _interval = TimeSpan.FromSeconds(Math.Clamp(beat.HeartbeatIntervalS, 10, 3600));
            _nextHeartbeat = now + _interval;
            if (beat.WantConfig && status == ServerLinkStates.Approved)
            {
                _configDueAt = now;
            }
        }

        SetState(status, null);
        if (status == ServerLinkStates.Approved)
        {
            foreach (var command in beat.Commands)
            {
                if (CommandKinds.IsRestore(command.Kind))
                {
                    command.RestoreBinding = restoreBinding;
                }

                Accept(command);
            }
        }

        return status == ServerLinkStates.Approved;
    }

    /// <summary>Identity of the currently registered agent at a pinned monitoring server.</summary>
    public string? RestoreBinding => _store.Get() is { AgentId: { } id, ServerPublicKey: { } key }
        ? id + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))) : null;

    /// <summary>Sends the queued reports in order (A.5, A.11). False when the round should end here.</summary>
    private async Task<bool> FlushAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var reports = _database.GetReports(20);
            if (reports.Count == 0)
            {
                return true;
            }

            foreach (var report in reports)
            {
                byte[] body = report.Body;
                while (true)
                {
                    var answer = await CallAsync(report.Path, body, cancellationToken).ConfigureAwait(false);
                    bool hasLog = HasLog(body);
                    if (answer.Status == 413 && (answer.Authentic || hasLog))
                    {
                        // Too big for the server (A.11): the same report without the whole log.
                        if (hasLog)
                        {
                            body = WithoutLog(body);
                            _database.ReplaceReport(report.Seq, body);
                            _host.Report(LinkEvent.Warning, answer.Authentic ? Messages.MonitorLogTooLarge : Messages.MonitorLogTooLargeProxy);
                            continue;
                        }

                        Drop(report, answer);
                        break;
                    }

                    if (answer.Ok)
                    {
                        _database.DeleteReport(report.Seq);
                        Contact(Now);
                        break;
                    }

                    if (answer.Authentic && answer.Status is >= 400 and < 500 and not (401 or 403 or 404 or 429))
                    {
                        // Wrong for the server and never right later: log it and go on (A.11).
                        Drop(report, answer);
                        break;
                    }

                    Failed(answer);
                    Interlocked.Exchange(ref _waiting, -1);
                    return false;
                }
            }

            Interlocked.Exchange(ref _waiting, -1);
            _host.LinkChanged();
        }
    }

    /// <summary>The configuration snapshot when due (A.6). False when the round should end here.</summary>
    private async Task<bool> SendConfigAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_configDueAt is not { } due || Now < due)
            {
                return true;
            }
        }

        var notes = _store.Get();
        byte[] body = _host.BuildConfigSnapshot(notes);
        var answer = await CallAsync("agent/config", body, cancellationToken).ConfigureAwait(false);
        if (answer.Ok)
        {
            _store.SnapshotSent(notes);
            lock (_lock)
            {
                // A change made while the snapshot was on its way moved the time on: then another one goes.
                if (_configDueAt is { } due && due <= Now)
                {
                    _configDueAt = null;
                }
            }

            return true;
        }

        if (answer.Authentic && answer.Status == 400)
        {
            lock (_lock)
            {
                _configDueAt = null;
            }

            _host.Report(LinkEvent.Error, Messages.Format(Messages.MonitorConfigRefused, answer.Describe()));
            return true;
        }

        Failed(answer);
        return false;
    }

    /// <summary>Progress every 10 seconds while a run goes on; not queued (A.5).</summary>
    private async Task SendProgressAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        if (now < _nextProgress || _host.Progress() is not { } progress)
        {
            return;
        }

        _nextProgress = now + ProgressInterval;
        var answer = await CallAsync("agent/runs/progress", JsonSerializer.SerializeToUtf8Bytes(progress, MonitorJsonContext.Default.RunProgressBody), cancellationToken).ConfigureAwait(false);
        if (answer.Status == 0 || !answer.Authentic)
        {
            Failed(answer);
        }
    }

    private void CheckInventory()
    {
        var last = _store.Get().LastInventoryAt;
        var now = Now;
        if (last is { } at && now - at < InventoryInterval && at <= now)
        {
            return;
        }

        _store.Update(s => s.LastInventoryAt = now);
        _host.RequestInventory();
    }

    // ---- Commands ----------------------------------------------------------------------------------------------

    private void Accept(MonitorCommand command)
    {
        if (!Guid.TryParse(command.Id, out _))
        {
            _host.Report(LinkEvent.Warning, Messages.Format(Messages.MonitorCommandBadId, command.Id, command.Kind));
            return;
        }

        lock (_lock)
        {
            // A command comes once (A.4); a repeat after a lost answer is not carried out twice.
            if (!_seen.Add(command.Id))
            {
                return;
            }

            _seenOrder.Enqueue(command.Id);
            if (_seenOrder.Count > 1000)
            {
                _seen.Remove(_seenOrder.Dequeue());
            }
        }

        _commands.Writer.TryWrite(command);
    }

    private async Task CommandLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var command in _commands.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Check(command) is { } refused)
                {
                    Finish(command, refused);
                    continue;
                }

                if (LongCommands.Contains(command.Kind))
                {
                    var task = Task.Run(() => ExecuteAsync(command, cancellationToken), CancellationToken.None);
                    lock (_lock)
                    {
                        _detached.RemoveAll(t => t.IsCompleted);
                        _detached.Add(task);
                    }
                }
                else
                {
                    await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The service is stopping; commands not carried out yet stay undone and expire on the server.
        }
    }

    private CommandOutcome? Check(MonitorCommand command)
    {
        if (command.ExpiresAt is { } expires && expires < Now)
        {
            return CommandOutcome.Rejected(Messages.MonitorCommandExpired);
        }

        if (!KnownCommands.Contains(command.Kind))
        {
            return CommandOutcome.Rejected(Messages.Format(Messages.MonitorCommandUnknown, command.Kind));
        }

        if (CommandKinds.Edits.Contains(command.Kind) && !_host.AllowRemoteEdit)
        {
            return CommandOutcome.Rejected(Messages.MonitorRemoteEditOff);
        }

        return null;
    }

    private async Task ExecuteAsync(MonitorCommand command, CancellationToken cancellationToken)
    {
        CommandOutcome? outcome;
        try
        {
            outcome = await _host.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // Whatever broke, the server hears about it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            outcome = CommandOutcome.Failed(Messages.Format(Messages.InternalError, ex.Message));
        }

        if (outcome is not null && (!CommandKinds.IsRestore(command.Kind) || command.RestoreBinding == RestoreBinding))
        {
            Finish(command, outcome);
        }
    }

    /// <summary>Queues the result of a command (A.4).</summary>
    public void Finish(MonitorCommand command, CommandOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(outcome);
        FinishById(command.Id, outcome);
        if (outcome.Status != CommandStatuses.Done)
        {
            _host.Report(LinkEvent.Warning, Messages.Format(Messages.MonitorCommandNotDone, command.Kind, outcome.Status, outcome.Message));
        }
    }

    /// <summary>Queues the result of a command known by its id only (an update finished after a restart).</summary>
    public void FinishById(string commandId, CommandOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (!Guid.TryParse(commandId, out _))
        {
            return;
        }

        var body = new CommandResultBody { Status = outcome.Status, Message = Limit(outcome.Message, 4000), Restore = outcome.Restore };
        Enqueue($"agent/commands/{commandId}/result", JsonSerializer.SerializeToUtf8Bytes(body, MonitorJsonContext.Default.CommandResultBody));
    }

    // ---- Answers -----------------------------------------------------------------------------------------------

    private async Task<MonitorAnswer> CallAsync(string path, byte[] body, CancellationToken cancellationToken)
    {
        var (url, agentId) = Target();
        using var serverKey = MonitorCrypto.LoadPublicPem(_store.Get().ServerPublicKey);
        return await _client.CallAsync(url, "POST", path, body, agentId, _store.AgentKey, serverKey, cancellationToken).ConfigureAwait(false);
    }

    private (string Url, string AgentId) Target()
    {
        string url = _host.ServerUrl ?? throw new InvalidOperationException(Messages.MonitorNotConnected);
        string agentId = _store.Get().AgentId ?? throw new InvalidOperationException(Messages.MonitorNotConnected);
        return (url, agentId);
    }

    /// <summary>A request that did not get 200: what A.11 says to do.</summary>
    private void Failed(MonitorAnswer answer)
    {
        if (!answer.Authentic)
        {
            Unreachable(answer);
            return;
        }

        var now = Now;
        Contact(now);
        switch (answer.Status, answer.Error)
        {
            case (404, _):
                // Unknown here: register again with the stored code; the pinned key stays (A.3).
                _store.Update(s =>
                {
                    s.AgentId = null;
                    s.Status = null;
                });
                _host.Report(LinkEvent.Warning, Messages.MonitorUnknownAgent);
                SetState(ServerLinkStates.Connecting, null);
                break;
            case (403, "pending" or "disabled"):
                _store.Update(s => s.Status = answer.Error);
                lock (_lock)
                {
                    _nextHeartbeat = now;
                }

                SetState(answer.Error!, null);
                break;
            case (403, _):
                RetryIn(_interval);
                SetState(ServerLinkStates.Forbidden, answer.ErrorBody?.Message);
                break;
            case (429, _):
                var wait = TimeSpan.FromSeconds(Math.Clamp(answer.ErrorBody?.RetryAfterS ?? 60, 1, (int)MaxRateLimit.TotalSeconds));
                RetryIn(wait);
                _host.Report(LinkEvent.Warning, Messages.Format(Messages.MonitorRateLimited, (int)wait.TotalSeconds));
                break;
            case (401, "clock_skew"):
                NextBackoff();
                SetState(ServerLinkStates.ClockSkew, Messages.Format(Messages.MonitorClockSkew, answer.ErrorBody?.ServerTime));
                break;
            default:
                // 401 bad_signature, 5xx: the server is there but does not work with us now.
                NextBackoff();
                SetState(ServerLinkStates.Offline, answer.Describe());
                break;
        }
    }

    /// <summary>No answer, or none with a valid signature: retry after 1, 2, 5, 10, 10… minutes (A.11).</summary>
    private void Unreachable(MonitorAnswer answer)
    {
        var now = Now;
        bool lost = false;
        TimeSpan since;
        lock (_lock)
        {
            _offlineSince ??= now;
            since = now - _offlineSince.Value;
            if (!_lostReported && since >= LostAfter)
            {
                _lostReported = true;
                lost = true;
            }
        }

        NextBackoff();
        SetState(ServerLinkStates.Offline, answer.Describe());
        if (lost)
        {
            _host.Report(LinkEvent.ConnectionLost, Messages.Format(Messages.MonitorConnectionLost, Host(_host.ServerUrl), (int)since.TotalMinutes, answer.Describe()));
        }
    }

    private void NextBackoff()
    {
        lock (_lock)
        {
            var delay = Backoff[Math.Min(_failures, Backoff.Length - 1)];
            _failures++;
            _retryAt = Now + delay;
        }
    }

    private void RetryIn(TimeSpan delay)
    {
        lock (_lock)
        {
            _retryAt = Now + delay;
        }
    }

    /// <summary>The server answered with a valid signature.</summary>
    private void Contact(DateTimeOffset now)
    {
        DateTimeOffset? since;
        bool restored;
        lock (_lock)
        {
            since = _offlineSince;
            restored = _lostReported;
            _lastContact = now;
            _offlineSince = null;
            _lostReported = false;
            _failures = 0;
        }

        if (restored && since is { } start)
        {
            _host.Report(LinkEvent.ConnectionRestored, Messages.Format(Messages.MonitorConnectionRestored, Host(_host.ServerUrl), (int)(now - start).TotalMinutes));
        }
    }

    private void Drop(QueuedReport report, MonitorAnswer answer)
    {
        _database.DeleteReport(report.Seq);
        _host.Report(LinkEvent.Error, Messages.Format(Messages.MonitorReportDropped, report.Path, answer.Describe()));
    }

    private void SetState(string state, string? detail)
    {
        bool changed;
        string? report = null;
        lock (_lock)
        {
            changed = _state != state || _detail != detail;
            _state = state;
            _detail = detail;
            string key = state + "\n" + detail;
            if (changed && _lastReported != key && state is not (ServerLinkStates.Approved or ServerLinkStates.Connecting or ServerLinkStates.Off))
            {
                _lastReported = key;
                report = detail is null ? StateText(state) : Messages.Format(Messages.MonitorStateDetail, StateText(state), detail);
            }
            else if (state == ServerLinkStates.Approved)
            {
                _lastReported = null;
            }
        }

        if (report is not null)
        {
            _host.Report(state is ServerLinkStates.Pending ? LinkEvent.Info : LinkEvent.Warning, report);
        }

        if (changed)
        {
            _host.LinkChanged();
        }
    }

    /// <summary>A state in words, for the log.</summary>
    public static string StateText(string state) => state switch
    {
        ServerLinkStates.NoCode => Messages.MonitorStateNoCode,
        ServerLinkStates.KeyRejected => Messages.MonitorStateKeyRejected,
        ServerLinkStates.Refused => Messages.MonitorStateRefused,
        ServerLinkStates.Pending => Messages.MonitorStatePending,
        ServerLinkStates.Approved => Messages.MonitorStateApproved,
        ServerLinkStates.Disabled => Messages.MonitorStateDisabled,
        ServerLinkStates.Forbidden => Messages.MonitorStateForbidden,
        ServerLinkStates.ClockSkew => Messages.MonitorStateClockSkew,
        ServerLinkStates.Offline => Messages.MonitorStateOffline,
        ServerLinkStates.Connecting => Messages.MonitorStateConnecting,
        _ => Messages.MonitorStateOff,
    };

    private static void Forget(LinkState state)
    {
        state.AgentId = null;
        state.ServerPublicKey = null;
        state.ServerName = null;
        state.Status = null;
        state.RegisteredAt = null;
        state.LastInventoryAt = null;
    }

    /// <summary>The report carries a whole log (not just <c>"log_gz_b64": null</c>).</summary>
    private static bool HasLog(byte[] body) => body.AsSpan().IndexOf("\"log_gz_b64\":\""u8) >= 0;

    private static byte[] WithoutLog(byte[] body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject json)
            {
                json.Remove("log_gz_b64");
                return JsonSerializer.SerializeToUtf8Bytes(json, MonitorJsonContext.Default.JsonObject);
            }
        }
        catch (JsonException)
        {
            // Not JSON: sent as it is and dropped by the rules for 400.
        }

        return body;
    }

    private static string? Limit(string? text, int length) => text is null || text.Length <= length ? text : text[..length];

    private static string Host(string? url) => url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : url ?? "?";

    private static TimeSpan Clamp(TimeSpan wait) =>
        wait == Timeout.InfiniteTimeSpan ? wait : wait < TimeSpan.Zero ? TimeSpan.Zero : wait > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : wait;
}
