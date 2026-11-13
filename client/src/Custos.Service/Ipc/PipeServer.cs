using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using Custos.Core;
using Custos.Core.Ipc;
using Custos.Service.Agent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Custos.Service.Ipc;

/// <summary>
/// Serves the interfaces of all user sessions over <c>\\.\pipe\custos</c>: answers their requests and pushes the
/// events of the agent to every connected interface (section 3.2).
/// </summary>
/// <remarks>
/// Each connection must start with <see cref="PipeProtocol.Methods.Hello"/>; the user is then taken from the pipe
/// (Windows says who it is, not the interface). Requests run side by side, so a slow one (checking a network
/// destination) does not hold up the others. Changes are allowed to administrators only (section 4).
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class PipeServer(ServiceInfo info, AgentService agent, AgentEvents events, ILogger<PipeServer> logger) : BackgroundService
{
    private const int MaxRequestsInFlight = 8;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    /// <summary>An interface that does not take a message within this time is disconnected; it connects again.</summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan StoppingNoticeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Methods that change what the service does as SYSTEM.</summary>
    // Restoring writes anywhere as SYSTEM: only administrators, even when everybody may edit tasks (section 4).
    private static readonly HashSet<string> AdminOnlyMethods =
    [
        PipeProtocol.Methods.RestoreList,
        PipeProtocol.Methods.RestoreStart,
        PipeProtocol.Methods.RestoreStatus,
        PipeProtocol.Methods.RestoreStop,

        // A new 7z.dll is loaded by SYSTEM.
        PipeProtocol.Methods.SevenZipCheck,
        PipeProtocol.Methods.SevenZipUpdate,

        // The server may change tasks and install updates: who it is, only an administrator decides (A.3).
        PipeProtocol.Methods.ServerConnect,
        PipeProtocol.Methods.ServerReset,

        // An account of a cloud drive lets SYSTEM into somebody's cloud (section 6.3).
        PipeProtocol.Methods.CloudBegin,
        PipeProtocol.Methods.CloudFinish,
        PipeProtocol.Methods.CloudAppsGet,
        PipeProtocol.Methods.CloudAppsSave,
    ];

    private static readonly HashSet<string> AdminMethods =
    [
        PipeProtocol.Methods.TaskSave,
        PipeProtocol.Methods.TaskDelete,
        PipeProtocol.Methods.TasksImport,
        PipeProtocol.Methods.CredentialSave,
        PipeProtocol.Methods.CredentialDelete,
        PipeProtocol.Methods.DestinationTest,
        PipeProtocol.Methods.DatabasesFind,
        PipeProtocol.Methods.DatabasesTest,
        PipeProtocol.Methods.RemoteCreateKey,
        PipeProtocol.Methods.RemoteTest,
        PipeProtocol.Methods.SettingsSave,
    ];

    private readonly ConcurrentDictionary<long, Connection> _connections = new();
    private long _lastConnectionId;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Tell the interfaces before the pipe goes away, so they show "stopping" rather than "not responding".
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(StoppingNoticeTimeout);
            var stopping = new ServiceStateEvent { State = PipeProtocol.ServiceStates.Stopping };
            await BroadcastAsync(
                PipeMessage.Notification(PipeProtocol.Events.ServiceState, stopping, IpcJsonContext.Default.ServiceStateEvent),
                timeout.Token).ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var broadcaster = Task.Run(() => BroadcastLoopAsync(stoppingToken), CancellationToken.None);
        NamedPipeServerStream? listener = null;
        bool announced = false;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Always keep one instance listening. The next one is created while the just-connected one is
                    // still open, so the service never lets go of the pipe name. FirstPipeInstance is only needed
                    // when no instance of ours exists.
                    listener ??= ServicePipe.CreateServer(PipeProtocol.PipeName, firstInstance: _connections.IsEmpty);
                    if (!announced)
                    {
                        ServiceLog.PipeOpened(logger, PipeProtocol.PipeName);
                        announced = true;
                    }

                    await listener.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                    var connected = listener;
                    listener = ServicePipe.CreateServer(PipeProtocol.PipeName, firstInstance: false);
                    StartConnection(connected, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ServiceLog.PipeFailed(logger, ex, PipeProtocol.PipeName, (int)RetryDelay.TotalSeconds);
                    announced = false;
                    if (listener is not null)
                    {
                        await listener.DisposeAsync().ConfigureAwait(false);
                        listener = null;
                    }

                    await Task.Delay(RetryDelay, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
        }
        finally
        {
            if (listener is not null)
            {
                await listener.DisposeAsync().ConfigureAwait(false);
            }

            await Task.WhenAll(_connections.Values.Select(c => c.Completion)).ConfigureAwait(false);
            await broadcaster.ConfigureAwait(false);
        }
    }

    // ---- Events ------------------------------------------------------------------------------------------------

    private async Task BroadcastLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var message in events.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await BroadcastAsync(message, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The service is stopping.
        }
    }

    /// <summary>Sends an event to every interface that has said hello.</summary>
    private Task BroadcastAsync(PipeMessage message, CancellationToken cancellationToken) =>
        Task.WhenAll(_connections.Values.Where(c => c.Identity is not null).Select(c => SendAsync(c, message, cancellationToken)));

    /// <summary>
    /// Sends one message. An interface that does not take it in time is disconnected: its pipe may now hold half a
    /// message, and the interface connects again by itself.
    /// </summary>
    private static async Task SendAsync(Connection connection, PipeMessage message, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection.Closing);
        timeout.CancelAfter(SendTimeout);
        try
        {
            await connection.Channel.SendAsync(message, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            connection.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The interface is gone; its reader loop cleans up.
            connection.Close();
        }
    }

    // ---- Connections -------------------------------------------------------------------------------------------

    private void StartConnection(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        long id = Interlocked.Increment(ref _lastConnectionId);
        var connection = new Connection(id, pipe, stoppingToken);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Completion = completion.Task;
        _connections[id] = connection;
        _ = Task.Run(async () =>
        {
            try
            {
                await ServeAsync(connection).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // A broken connection must not take down the service.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                ServiceLog.ConnectionFailed(logger, ex, id);
            }
            finally
            {
                _connections.TryRemove(id, out _);
                await connection.DisposeAsync().ConfigureAwait(false);
                completion.SetResult();
            }
        }, CancellationToken.None);
    }

    private async Task ServeAsync(Connection connection)
    {
        var token = connection.Closing;
        using var inFlight = new SemaphoreSlim(MaxRequestsInFlight);
        var running = new List<Task>();
        try
        {
            while (await connection.Channel.ReceiveAsync(token).ConfigureAwait(false) is { } request)
            {
                if (request.Kind != PipeMessageKind.Request || request.Id is not { } requestId || request.Method is null)
                {
                    throw new InvalidDataException($"Expected a request, got a message of kind '{request.Kind}'.");
                }

                if (request.Method == PipeProtocol.Methods.Hello)
                {
                    await SendAsync(connection, Hello(connection, requestId, request), token).ConfigureAwait(false);
                    continue;
                }

                if (connection.Identity is not { } identity)
                {
                    await SendAsync(connection, PipeMessage.Failure(requestId, PipeProtocol.Errors.HelloFirst, "Say hello first."), token).ConfigureAwait(false);
                    continue;
                }

                // A runaway interface waits here rather than piling up work in the service.
                await inFlight.WaitAsync(token).ConfigureAwait(false);
                running.RemoveAll(t => t.IsCompleted);
                running.Add(Task.Run(async () =>
                {
                    try
                    {
                        await HandleAsync(connection, identity, requestId, request).ConfigureAwait(false);
                    }
                    finally
                    {
                        inFlight.Release();
                    }
                }, CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The service is stopping, or the interface could not keep up and was disconnected.
        }
        catch (InvalidDataException ex)
        {
            ServiceLog.ClientProtocolError(logger, ex, connection.Id);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The interface closed or crashed: normal end of a connection.
        }
        finally
        {
            connection.Close();
            await Task.WhenAll(running).ConfigureAwait(false);
        }

        if (connection.Identity is not null)
        {
            ServiceLog.ClientDisconnected(logger, connection.Id);
        }
    }

    private PipeMessage Hello(Connection connection, long requestId, PipeMessage request)
    {
        HelloRequest hello;
        try
        {
            hello = request.GetData(IpcJsonContext.Default.HelloRequest);
        }
        catch (InvalidDataException ex)
        {
            return PipeMessage.Failure(requestId, PipeProtocol.Errors.BadRequest, ex.Message);
        }

        if (hello.Protocol != PipeProtocol.Version)
        {
            ServiceLog.ClientProtocolMismatch(logger, connection.Id, hello.Protocol, PipeProtocol.Version);
            return PipeMessage.Failure(requestId, PipeProtocol.Errors.ProtocolMismatch,
                $"The service speaks protocol {PipeProtocol.Version}, the interface {hello.Protocol}.");
        }

        var identity = connection.Identity;
        if (identity is null)
        {
            try
            {
                identity = ClientIdentity.Read(connection.Pipe);
            }
#pragma warning disable CA1031 // Without an identity the interface still works, with the rights of an ordinary user.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                ServiceLog.ClientIdentityFailed(logger, ex, connection.Id);
                identity = new ClientIdentity("?", IsAdmin: false, SessionId: null, ProcessId: null);
            }

            connection.Identity = identity;
            ServiceLog.ClientConnected(logger, connection.Id, identity.User, identity.IsAdmin, identity.SessionId, identity.ProcessId, hello.ClientVersion);
        }

        var result = new HelloResult
        {
            Protocol = PipeProtocol.Version,
            ServiceVersion = info.Version,
            State = PipeProtocol.ServiceStates.Running,
            StartedAt = info.StartedAt,
            User = identity.User,
            IsAdmin = identity.IsAdmin,
            SessionId = identity.SessionId,
        };
        return PipeMessage.Success(requestId, result, IpcJsonContext.Default.HelloResult);
    }

    private async Task HandleAsync(Connection connection, ClientIdentity identity, long requestId, PipeMessage request)
    {
        string method = request.Method!;
        PipeMessage response;
        try
        {
            await agent.Ready.WaitAsync(connection.Closing).ConfigureAwait(false);
            if ((AdminOnlyMethods.Contains(method) && !identity.IsAdmin) || (AdminMethods.Contains(method) && !identity.IsAdmin && !agent.AllUsersMayEdit))
            {
                ServiceLog.RequestForbidden(logger, identity.User, connection.Id, method);
                string refusal = method is PipeProtocol.Methods.SevenZipCheck or PipeProtocol.Methods.SevenZipUpdate ? Messages.SevenZipNotAllowed
                    : method is PipeProtocol.Methods.ServerConnect or PipeProtocol.Methods.ServerReset ? Messages.MonitorNotAllowed
                    : method.StartsWith("cloud.", StringComparison.Ordinal) ? Messages.CloudNotAllowed
                    : AdminOnlyMethods.Contains(method) ? Messages.RestoreNotAllowed
                    : Messages.NotAllowed;
                response = PipeMessage.Failure(requestId, PipeProtocol.Errors.Forbidden, refusal);
            }
            else
            {
                response = method switch
                {
                    // These wait for GitHub (and the update for a download), so they do not block a thread meanwhile.
                    PipeProtocol.Methods.SevenZipCheck => PipeMessage.Success(requestId,
                        await agent.CheckSevenZipAsync(connection.Closing).ConfigureAwait(false), IpcJsonContext.Default.SevenZipCheckResult),
                    PipeProtocol.Methods.SevenZipUpdate => Ok(requestId,
                        await agent.UpdateSevenZipAsync(identity.User, connection.Closing).ConfigureAwait(false)),
                    _ => Dispatch(identity, requestId, request),
                };
            }
        }
        catch (OperationCanceledException) when (connection.Closing.IsCancellationRequested)
        {
            return;
        }
        catch (InvalidDataException ex)
        {
            response = PipeMessage.Failure(requestId, PipeProtocol.Errors.BadRequest, ex.Message);
        }
        catch (AgentRequestException ex)
        {
            response = PipeMessage.Failure(requestId, ex.Code, ex.Message);
        }
#pragma warning disable CA1031 // One failing request must not take down the connection or the service.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ServiceLog.RequestFailed(logger, ex, method, connection.Id);
            response = PipeMessage.Failure(requestId, PipeProtocol.Errors.Internal, Messages.Format(Messages.InternalError, ex.Message));
        }

        await SendAsync(connection, response, connection.Closing).ConfigureAwait(false);
    }

    private PipeMessage Dispatch(ClientIdentity identity, long id, PipeMessage request)
    {
        var json = IpcJsonContext.Default;
        string user = identity.User;
        return request.Method switch
        {
            PipeProtocol.Methods.Snapshot =>
                PipeMessage.Success(id, agent.GetSnapshot(), json.AgentSnapshot),
            PipeProtocol.Methods.LogRecent =>
                PipeMessage.Success(id, new LogResult { Lines = agent.GetRecentLog() }, json.LogResult),
            PipeProtocol.Methods.HistoryGet => History(id, request.GetData(json.HistoryRequest)),
            PipeProtocol.Methods.TaskRun =>
                Ok(id, agent.RunTask(request.GetData(json.TaskRequest).TaskId, user)),
            PipeProtocol.Methods.RunAll =>
                Ok(id, agent.RunAll(user)),
            PipeProtocol.Methods.RunStop =>
                Ok(id, agent.Stop(request.GetData(json.RunControlRequest).TaskId, user)),
            PipeProtocol.Methods.RunStopAll =>
                Ok(id, agent.StopAll(user)),
            PipeProtocol.Methods.RunPause =>
                Ok(id, agent.SetPaused(request.GetData(json.RunControlRequest).TaskId, paused: true, user)),
            PipeProtocol.Methods.RunResume =>
                Ok(id, agent.SetPaused(request.GetData(json.RunControlRequest).TaskId, paused: false, user)),
            PipeProtocol.Methods.TaskSave =>
                PipeMessage.Success(id, new TaskSaveResult { Task = agent.SaveTask(request.GetData(json.TaskSaveRequest), user, identity.Sid) }, json.TaskSaveResult),
            PipeProtocol.Methods.TaskDelete =>
                Ok(id, agent.DeleteTask(request.GetData(json.TaskRequest).TaskId, user)),
            PipeProtocol.Methods.TasksImport =>
                Ok(id, agent.Import(request.GetData(json.TasksImportRequest), user, identity.Sid)),
            PipeProtocol.Methods.CredentialSave =>
                PipeMessage.Success(id, new CredentialSaveResult { Credential = agent.SaveCredential(request.GetData(json.CredentialInput)) }, json.CredentialSaveResult),
            PipeProtocol.Methods.CredentialDelete =>
                Ok(id, agent.DeleteCredential(request.GetData(json.CredentialRequest).CredentialId)),
            PipeProtocol.Methods.DestinationTest =>
                PipeMessage.Success(id, agent.TestDestination(request.GetData(json.DestinationTestRequest), identity.Sid), json.DestinationTestResult),
            PipeProtocol.Methods.DatabasesFind =>
                PipeMessage.Success(id, agent.FindDatabases(), json.DbFindResult),
            PipeProtocol.Methods.DatabasesTest =>
                PipeMessage.Success(id, agent.TestDatabase(request.GetData(json.DbTestRequest), identity.Sid), json.DbTestResult),
            PipeProtocol.Methods.RemoteCreateKey =>
                PipeMessage.Success(id, new CredentialSaveResult { Credential = agent.CreateSshKey(request.GetData(json.RemoteKeyRequest), user) }, json.CredentialSaveResult),
            PipeProtocol.Methods.RemoteTest =>
                PipeMessage.Success(id, agent.TestRemote(request.GetData(json.RemoteTestRequest)), json.RemoteTestResult),
            PipeProtocol.Methods.SettingsSave =>
                Ok(id, agent.SaveSettings(request.GetData(json.SettingsSaveRequest).Settings, user)),
            PipeProtocol.Methods.RestoreList =>
                PipeMessage.Success(id, agent.ListRestoreSets(request.GetData(json.RestoreListRequest)), json.RestoreListResult),
            PipeProtocol.Methods.RestoreStart =>
                Ok(id, agent.StartRestore(request.GetData(json.RestoreStartRequest), user)),
            PipeProtocol.Methods.RestoreStatus =>
                PipeMessage.Success(id, agent.GetRestoreStatus(), json.RestoreStatusResult),
            PipeProtocol.Methods.RestoreStop =>
                Ok(id, agent.StopRestore(user)),
            PipeProtocol.Methods.PowerCancel =>
                Ok(id, agent.CancelPower(user)),
            PipeProtocol.Methods.ServerConnect => ServerConnect(id, request.GetData(json.ServerConnectRequest), user),
            PipeProtocol.Methods.CloudBegin =>
                PipeMessage.Success(id, agent.BeginCloud(request.GetData(json.CloudBeginRequest)), json.CloudBeginResult),
            PipeProtocol.Methods.CloudFinish =>
                PipeMessage.Success(id, new CredentialSaveResult { Credential = agent.FinishCloud(request.GetData(json.CloudFinishRequest), user) }, json.CredentialSaveResult),
            PipeProtocol.Methods.CloudAppsGet =>
                PipeMessage.Success(id, agent.GetCloudApps(), json.CloudAppsResult),
            PipeProtocol.Methods.CloudAppsSave =>
                Ok(id, agent.SaveCloudApps(request.GetData(json.CloudAppsSaveRequest).Apps, user)),
            PipeProtocol.Methods.ServerReset =>
                Ok(id, agent.ResetServer(user)),
            _ => PipeMessage.Failure(id, PipeProtocol.Errors.UnknownMethod, $"Unknown method '{request.Method}'."),
        };
    }

    private PipeMessage History(long id, HistoryRequest request) =>
        PipeMessage.Success(id, new HistoryResult { Runs = agent.GetHistory(request.TaskId, request.Limit) }, IpcJsonContext.Default.HistoryResult);

    private PipeMessage ServerConnect(long id, ServerConnectRequest request, string user) =>
        Ok(id, agent.ConnectServer(request.ServerUrl, request.Code, user));

    private static PipeMessage Ok(long id, OkResult result) => PipeMessage.Success(id, result, IpcJsonContext.Default.OkResult);

    /// <summary>One connected interface.</summary>
    private sealed class Connection : IAsyncDisposable
    {
        private readonly CancellationTokenSource _closing;
        private ClientIdentity? _identity;

        public Connection(long id, NamedPipeServerStream pipe, CancellationToken stoppingToken)
        {
            Id = id;
            Pipe = pipe;
            Channel = new PipeChannel(pipe, PipeProtocol.MaxRequestBytes);
            _closing = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            Closing = _closing.Token;
        }

        public long Id { get; }

        public NamedPipeServerStream Pipe { get; }

        public PipeChannel Channel { get; }

        /// <summary>Cancelled when the connection is being closed or the service stops.</summary>
        public CancellationToken Closing { get; }

        /// <summary>Who is connected; null until the interface has said hello.</summary>
        public ClientIdentity? Identity
        {
            get => Volatile.Read(ref _identity);
            set => Volatile.Write(ref _identity, value);
        }

        public Task Completion { get; set; } = Task.CompletedTask;

        public void Close()
        {
            try
            {
                _closing.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already closed.
            }
        }

        public async ValueTask DisposeAsync()
        {
            Close();
            await Channel.DisposeAsync().ConfigureAwait(false);
            _closing.Dispose();
        }
    }
}
