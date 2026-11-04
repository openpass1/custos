using System.Text.Json.Serialization.Metadata;
using Custos.Core;
using Custos.Core.Ipc;

namespace Custos.Gui;

internal enum ServiceState
{
    Connecting,
    Running,
    Stopping,
    Unavailable,
}

/// <summary>What the interface knows about the service right now.</summary>
/// <param name="Hello">The service's answer to the last successful hello, while <see cref="ServiceState.Running"/>.</param>
/// <param name="Problem">Why the service is unavailable, in words for the user.</param>
internal sealed record ServiceStatus(ServiceState State, HelloResult? Hello = null, string? Problem = null);

/// <summary>New lines of the live log.</summary>
internal sealed class LogLinesEventArgs(IReadOnlyList<LogLine> lines) : EventArgs
{
    public IReadOnlyList<LogLine> Lines { get; } = lines;
}

/// <summary>A request the interface could not send or the service refused; the message is for the user.</summary>
internal sealed class ServiceUnavailableException(string message) : Exception(message);

/// <summary>
/// Keeps a connection to the service: connects, says hello, holds the latest snapshot, the live log and the progress,
/// and reconnects when the service restarts. Everything is raised and changed on the UI thread.
/// </summary>
internal sealed class ServiceMonitor : IDisposable
{
    public const int MaxLogLines = 2000;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private readonly SynchronizationContext _ui;
    private readonly CancellationTokenSource _stop = new();
    private readonly SortedList<long, LogLine> _log = [];
    private ServiceClient? _client;
    private int _generation;
    private long _snapshotVersion;

    public ServiceMonitor()
    {
        _ui = SynchronizationContext.Current
            ?? throw new InvalidOperationException("ServiceMonitor must be created on the UI thread.");
    }

    public event EventHandler? StatusChanged;

    /// <summary><see cref="Snapshot"/> changed (or became null when the connection was lost).</summary>
    public event EventHandler? SnapshotChanged;

    /// <summary><see cref="Progress"/> changed.</summary>
    public event EventHandler? ProgressChanged;

    /// <summary>Lines were added at the end of <see cref="Log"/>.</summary>
    public event EventHandler<LogLinesEventArgs>? LogAdded;

    /// <summary><see cref="Log"/> was replaced as a whole (a new connection).</summary>
    public event EventHandler? LogReset;

    public ServiceStatus Status { get; private set; } = new(ServiceState.Connecting);

    /// <summary>Tasks, states, queue and settings as the service last reported them; null without a connection.</summary>
    public AgentSnapshot? Snapshot { get; private set; }

    /// <summary>Progress of the run in progress, or null.</summary>
    public RunProgressEvent? Progress { get; private set; }

    /// <summary>The live log, oldest first, at most <see cref="MaxLogLines"/> lines.</summary>
    public IList<LogLine> Log => _log.Values;

    public bool IsConnected => _client is not null && Status.State == ServiceState.Running;

    /// <summary>Whether this user may change tasks and settings. The service decides; this only greys out buttons.</summary>
    public bool CanEdit => IsConnected && (Status.Hello?.IsAdmin == true || Snapshot?.Settings.AllowAllUsersEdit == true);

    public void Start() => _ = RunAsync(_stop.Token);

    /// <summary>The service state in a few words, as in the status bar and the tray tooltip.</summary>
    public static string Describe(ServiceState state) => state switch
    {
        ServiceState.Connecting => Strings.StateConnecting,
        ServiceState.Running => Strings.StateRunning,
        ServiceState.Stopping => Strings.StateStopping,
        _ => Strings.StateUnavailable,
    };

    /// <summary>Sends a request to the service.</summary>
    /// <exception cref="ServiceUnavailableException">No connection, or the service refused; the message says why.</exception>
    public async Task<TResult> RequestAsync<TParams, TResult>(
        string method,
        TParams parameters,
        JsonTypeInfo<TParams> parametersType,
        JsonTypeInfo<TResult> resultType,
        TimeSpan? timeout = null)
    {
        var client = _client;
        if (client is null || Status.State != ServiceState.Running)
        {
            throw new ServiceUnavailableException(Strings.ProblemNoConnection);
        }

        try
        {
            return await client.RequestAsync(method, parameters, parametersType, resultType, _stop.Token)
                .WaitAsync(timeout ?? DefaultRequestTimeout, _stop.Token);
        }
        catch (ServiceRequestException ex)
        {
            throw new ServiceUnavailableException(ex.Code switch
            {
                PipeProtocol.Errors.Forbidden or PipeProtocol.Errors.NotFound or PipeProtocol.Errors.Conflict
                    or PipeProtocol.Errors.Invalid or PipeProtocol.Errors.Internal => ex.Message,
                _ => Strings.Format(Strings.ProblemRequestFailed, ex.Message),
            });
        }
        catch (TimeoutException)
        {
            throw new ServiceUnavailableException(Strings.ProblemNoAnswer);
        }
        catch (IOException)
        {
            throw new ServiceUnavailableException(Strings.ProblemNoConnection);
        }
    }

    /// <summary>A request without parameters that answers <see cref="OkResult"/>.</summary>
    public Task<OkResult> CommandAsync(string method) =>
        RequestAsync(method, EmptyRequest.Instance, IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.OkResult);

    /// <summary>A run control request (run, stop, pause, resume) for one task, or the run in progress.</summary>
    public Task<OkResult> RunControlAsync(string method, string? taskId) => method == PipeProtocol.Methods.TaskRun
        ? RequestAsync(method, new TaskRequest { TaskId = taskId! }, IpcJsonContext.Default.TaskRequest, IpcJsonContext.Default.OkResult)
        : RequestAsync(method, new RunControlRequest { TaskId = taskId }, IpcJsonContext.Default.RunControlRequest, IpcJsonContext.Default.OkResult);

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    // For the interface tests (tests/Custos.Gui.Tests): data as if the service had sent it, without a connection.
    internal void SimulateSnapshot(AgentSnapshot snapshot) => ApplySnapshot(_generation, snapshot);

    internal void SimulateProgress(RunProgressEvent progress) => ApplyProgress(_generation, progress);

    internal void SimulateLog(List<LogLine> lines) => ApplyLog(_generation, lines);

    // Runs on the UI thread: every await resumes there, so the state is only touched by one thread.
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            int generation = ++_generation;
            try
            {
                var client = await ServiceClient.ConnectAsync(message => OnServiceEvent(generation, message), ConnectTimeout, cancellationToken);
                await using (client)
                {
                    var hello = await client.RequestAsync(
                        PipeProtocol.Methods.Hello,
                        new HelloRequest { Protocol = PipeProtocol.Version, ClientVersion = CustosInfo.Version },
                        IpcJsonContext.Default.HelloRequest,
                        IpcJsonContext.Default.HelloResult,
                        cancellationToken).WaitAsync(HelloTimeout, cancellationToken);

                    _client = client;
                    _snapshotVersion = 0;
                    _log.Clear();
                    LogReset?.Invoke(this, EventArgs.Empty);
                    SetStatus(new ServiceStatus(ServiceState.Running, hello));
                    await LoadAsync(client, generation, cancellationToken);

                    await client.Completion.WaitAsync(cancellationToken);
                    Disconnected(new ServiceStatus(ServiceState.Unavailable, Problem: Strings.ProblemConnectionLost));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Whatever went wrong, show it and try again.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Disconnected(new ServiceStatus(ServiceState.Unavailable, Problem: DescribeProblem(ex)));
            }

            try
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>The first snapshot and the recent log of a new connection. Events may already be arriving.</summary>
    private async Task LoadAsync(ServiceClient client, int generation, CancellationToken cancellationToken)
    {
        var snapshot = await client.RequestAsync(PipeProtocol.Methods.Snapshot, EmptyRequest.Instance,
            IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.AgentSnapshot, cancellationToken).WaitAsync(DefaultRequestTimeout, cancellationToken);
        ApplySnapshot(generation, snapshot);

        var recent = await client.RequestAsync(PipeProtocol.Methods.LogRecent, EmptyRequest.Instance,
            IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.LogResult, cancellationToken).WaitAsync(DefaultRequestTimeout, cancellationToken);
        if (generation != _generation)
        {
            return;
        }

        foreach (var line in recent.Lines)
        {
            _log.TryAdd(line.Seq, line);
        }

        TrimLog();
        LogReset?.Invoke(this, EventArgs.Empty);
    }

    private void Disconnected(ServiceStatus status)
    {
        _client = null;
        bool hadData = Snapshot is not null || Progress is not null;
        Snapshot = null;
        Progress = null;
        SetStatus(status);
        if (hadData)
        {
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
            ProgressChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // Called on a thread-pool thread by the connection's reader loop. Must not throw: that would end the connection.
    private void OnServiceEvent(int generation, PipeMessage message)
    {
        try
        {
            switch (message.Event)
            {
                case PipeProtocol.Events.ServiceState:
                    var state = message.GetData(IpcJsonContext.Default.ServiceStateEvent);
                    if (state.State == PipeProtocol.ServiceStates.Stopping)
                    {
                        _ui.Post(_ =>
                        {
                            if (generation == _generation)
                            {
                                SetStatus(Status with { State = ServiceState.Stopping });
                            }
                        }, null);
                    }

                    break;

                case PipeProtocol.Events.AgentChanged:
                    var snapshot = message.GetData(IpcJsonContext.Default.AgentSnapshot);
                    _ui.Post(_ => ApplySnapshot(generation, snapshot), null);
                    break;

                case PipeProtocol.Events.RunProgress:
                    var progress = message.GetData(IpcJsonContext.Default.RunProgressEvent);
                    _ui.Post(_ => ApplyProgress(generation, progress), null);
                    break;

                case PipeProtocol.Events.LogLines:
                    var lines = message.GetData(IpcJsonContext.Default.LogResult).Lines;
                    _ui.Post(_ => ApplyLog(generation, lines), null);
                    break;
            }
        }
        catch (InvalidDataException)
        {
            // An event this version does not understand: skip it.
        }
    }

    private void ApplySnapshot(int generation, AgentSnapshot snapshot)
    {
        // The service numbers its snapshots; one computed earlier may arrive later and must not win.
        if (generation != _generation || snapshot.Version <= _snapshotVersion)
        {
            return;
        }

        _snapshotVersion = snapshot.Version;
        Snapshot = snapshot;
        SnapshotChanged?.Invoke(this, EventArgs.Empty);

        var current = snapshot.Current;
        if (current is null)
        {
            if (Progress is not null)
            {
                Progress = null;
                ProgressChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (Progress is null || Progress.RunId != current.RunId || Progress.Paused != current.Paused)
        {
            // Within the same run the progress events are newer than the snapshot.
            bool sameRun = Progress is not null && Progress.RunId == current.RunId;
            Progress = new RunProgressEvent
            {
                RunId = current.RunId,
                TaskId = current.TaskId,
                Paused = current.Paused,
                Progress = (sameRun ? Progress!.Progress : current.Progress) ?? new Core.Backup.BackupProgress(),
            };
            ProgressChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyProgress(int generation, RunProgressEvent progress)
    {
        if (generation != _generation || Snapshot?.Current?.RunId is not { } running || running != progress.RunId)
        {
            return;
        }

        Progress = progress;
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyLog(int generation, List<LogLine> lines)
    {
        if (generation != _generation)
        {
            return;
        }

        var added = new List<LogLine>(lines.Count);
        long last = _log.Count > 0 ? _log.Keys[^1] : long.MinValue;
        foreach (var line in lines)
        {
            if (line.Seq > last && _log.TryAdd(line.Seq, line))
            {
                added.Add(line);
                last = line.Seq;
            }
        }

        if (added.Count == 0)
        {
            return;
        }

        TrimLog();
        LogAdded?.Invoke(this, new LogLinesEventArgs(added));
    }

    private void TrimLog()
    {
        while (_log.Count > MaxLogLines)
        {
            _log.RemoveAt(0);
        }
    }

    private void SetStatus(ServiceStatus status)
    {
        if (status == Status)
        {
            return;
        }

        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string DescribeProblem(Exception ex) => ex switch
    {
        TimeoutException => Strings.ProblemNotRunning,
        UnauthorizedAccessException => Strings.ProblemAccessDenied,
        ServiceRequestException { Code: PipeProtocol.Errors.ProtocolMismatch } => Strings.ProblemVersionMismatch,
        _ => ex.Message,
    };
}
