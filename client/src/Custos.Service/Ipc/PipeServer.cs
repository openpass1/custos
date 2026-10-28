using System.Collections.Concurrent;
using System.IO.Pipes;
using Custos.Core.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Custos.Service.Ipc;

/// <summary>
/// Serves the interfaces of all user sessions over <c>\\.\pipe\custos</c>: answers their requests and pushes events
/// to every connected interface (section 3.2).
/// </summary>
internal sealed class PipeServer(ServiceInfo info, ILogger<PipeServer> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BroadcastTimeout = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<long, Connection> _connections = new();
    private long _lastConnectionId;

    /// <summary>Sends an event to every connected interface. A connection that cannot take it is dropped.</summary>
    public async Task BroadcastAsync(PipeMessage message, CancellationToken cancellationToken)
    {
        var sends = _connections.Values.Select(async connection =>
        {
            try
            {
                await connection.Channel.SendAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // The interface is gone; its reader loop will clean up.
            }
        });
        await Task.WhenAll(sends).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Tell the interfaces before the pipe goes away, so they show "stopping" rather than "not responding".
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(BroadcastTimeout);
            var stopping = new ServiceStateEvent { State = PipeProtocol.ServiceStates.Stopping };
            await BroadcastAsync(
                PipeMessage.Notification(PipeProtocol.Events.ServiceState, stopping, IpcJsonContext.Default.ServiceStateEvent),
                timeout.Token).ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
        }
    }

    private void StartConnection(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        long id = Interlocked.Increment(ref _lastConnectionId);
        var channel = new PipeChannel(pipe, PipeProtocol.MaxRequestBytes);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connections[id] = new Connection(channel, completion.Task);
        _ = Task.Run(async () =>
        {
            try
            {
                await ServeAsync(id, pipe, channel, stoppingToken).ConfigureAwait(false);
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
                await channel.DisposeAsync().ConfigureAwait(false);
                completion.SetResult();
            }
        }, CancellationToken.None);
    }

    private async Task ServeAsync(long id, NamedPipeServerStream pipe, PipeChannel channel, CancellationToken stoppingToken)
    {
        ClientIdentity? identity = null;
        try
        {
            while (await channel.ReceiveAsync(stoppingToken).ConfigureAwait(false) is { } request)
            {
                if (request.Kind != PipeMessageKind.Request || request.Id is not { } requestId || request.Method is null)
                {
                    throw new InvalidDataException($"Expected a request, got a message of kind '{request.Kind}'.");
                }

                PipeMessage response;
                try
                {
                    response = request.Method switch
                    {
                        PipeProtocol.Methods.Hello => Hello(id, requestId, request.GetData(IpcJsonContext.Default.HelloRequest), pipe, ref identity),
                        _ => PipeMessage.Failure(requestId, PipeProtocol.Errors.UnknownMethod, $"Unknown method '{request.Method}'."),
                    };
                }
                catch (InvalidDataException ex)
                {
                    response = PipeMessage.Failure(requestId, PipeProtocol.Errors.BadRequest, ex.Message);
                }
#pragma warning disable CA1031 // One failing request must not take down the connection or the service.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    ServiceLog.RequestFailed(logger, ex, request.Method, id);
                    response = PipeMessage.Failure(requestId, PipeProtocol.Errors.Internal, "The service could not process the request. See the service log.");
                }

                await channel.SendAsync(response, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Service is stopping.
        }
        catch (InvalidDataException ex)
        {
            ServiceLog.ClientProtocolError(logger, ex, id);
        }
        catch (IOException)
        {
            // The interface closed or crashed: normal end of a connection.
        }

        if (identity is not null)
        {
            ServiceLog.ClientDisconnected(logger, id);
        }
    }

    private PipeMessage Hello(long id, long requestId, HelloRequest hello, NamedPipeServerStream pipe, ref ClientIdentity? identity)
    {
        if (hello.Protocol != PipeProtocol.Version)
        {
            ServiceLog.ClientProtocolMismatch(logger, id, hello.Protocol, PipeProtocol.Version);
            return PipeMessage.Failure(requestId, PipeProtocol.Errors.ProtocolMismatch,
                $"The service speaks protocol {PipeProtocol.Version}, the interface {hello.Protocol}.");
        }

        if (identity is null)
        {
            try
            {
                identity = ClientIdentity.Read(pipe);
            }
#pragma warning disable CA1031 // Without an identity the interface still works, with the rights of an ordinary user.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                ServiceLog.ClientIdentityFailed(logger, ex, id);
                identity = new ClientIdentity("?", IsAdmin: false, SessionId: null, ProcessId: null);
            }

            ServiceLog.ClientConnected(logger, id, identity.User, identity.IsAdmin, identity.SessionId, identity.ProcessId, hello.ClientVersion);
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

    private sealed record Connection(PipeChannel Channel, Task Completion);
}
