using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json.Serialization.Metadata;

namespace Custos.Core.Ipc;

/// <summary>A request the service answered with an error.</summary>
public sealed class ServiceRequestException(string code, string message) : Exception(message)
{
    /// <summary>One of <see cref="PipeProtocol.Errors"/>.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// The interface's connection to the service: requests with responses, plus events pushed by the service.
/// One instance is one connection; after <see cref="Completion"/> finishes, connect again with a new instance.
/// </summary>
public sealed class ServiceClient : IAsyncDisposable
{
    private readonly PipeChannel _channel;
    private readonly Action<PipeMessage>? _onEvent;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<long, TaskCompletionSource<PipeMessage>> _pending = [];
    private readonly Lock _pendingLock = new();
    private readonly Task _readLoop;
    private Exception? _closeReason;
    private bool _closed;
    private long _lastId;

    private ServiceClient(Stream connectedPipe, Action<PipeMessage>? onEvent)
    {
        _channel = new PipeChannel(connectedPipe, PipeProtocol.MaxMessageBytes);
        _onEvent = onEvent;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>Finishes when the connection is closed, by either side. Never faults.</summary>
    public Task Completion => _readLoop;

    /// <summary>Why the connection closed; set once <see cref="Completion"/> has finished.</summary>
    public Exception? CloseReason => Volatile.Read(ref _closeReason);

    /// <summary>Connects to the service.</summary>
    /// <param name="onEvent">Called for every event from the service, on a thread-pool thread.</param>
    /// <param name="timeout">How long to wait for the pipe to appear.</param>
    /// <exception cref="TimeoutException">The service is not running.</exception>
    /// <exception cref="UnauthorizedAccessException">The pipe refused this user.</exception>
    public static async Task<ServiceClient> ConnectAsync(
        Action<PipeMessage>? onEvent,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string pipeName = PipeProtocol.PipeName)
    {
        var pipe = CreatePipe(pipeName);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new ServiceClient(pipe, onEvent);
    }

    /// <summary>Sends a request and waits for its response.</summary>
    /// <exception cref="ServiceRequestException">The service answered with an error.</exception>
    /// <exception cref="IOException">The connection closed before the response arrived.</exception>
    public async Task<TResult> RequestAsync<TParams, TResult>(
        string method,
        TParams parameters,
        JsonTypeInfo<TParams> parametersType,
        JsonTypeInfo<TResult> resultType,
        CancellationToken cancellationToken)
    {
        long id = Interlocked.Increment(ref _lastId);
        var response = new TaskCompletionSource<PipeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock)
        {
            if (_closed)
            {
                throw new IOException("The connection to the service is closed.", CloseReason);
            }

            _pending.Add(id, response);
        }

        try
        {
            await _channel.SendAsync(PipeMessage.Request(id, method, parameters, parametersType), cancellationToken).ConfigureAwait(false);
            var message = await response.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (message.Error is { } error)
            {
                throw new ServiceRequestException(error.Code, error.Message);
            }

            return message.GetData(resultType);
        }
        finally
        {
            lock (_pendingLock)
            {
                _pending.Remove(id);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _channel.DisposeAsync().ConfigureAwait(false);
        await _readLoop.ConfigureAwait(false);
        _stop.Dispose();
    }

    private static NamedPipeClientStream CreatePipe(string pipeName)
    {
        if (OperatingSystem.IsWindows())
        {
            // Ask only for what the service's pipe grants to ordinary users. The service may identify the caller
            // (to check administrator rights) but may not act on its behalf.
            return new NamedPipeClientStream(
                ".",
                pipeName,
                PipeAccessRights.ReadWrite,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification,
                HandleInheritability.None);
        }

        return new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    }

    private async Task ReadLoopAsync()
    {
        Exception? reason = null;
        try
        {
            while (await _channel.ReceiveAsync(_stop.Token).ConfigureAwait(false) is { } message)
            {
                Dispatch(message);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Closed by DisposeAsync.
        }
        catch (Exception ex)
        {
            // Broken pipe, protocol violation or a failing event handler: the connection is over either way.
            reason = ex;
        }

        TaskCompletionSource<PipeMessage>[] orphans;
        lock (_pendingLock)
        {
            _closed = true;
            Volatile.Write(ref _closeReason, reason);
            orphans = [.. _pending.Values];
            _pending.Clear();
        }

        var closed = new IOException("The connection to the service closed.", reason);
        foreach (var orphan in orphans)
        {
            orphan.TrySetException(closed);
        }
    }

    private void Dispatch(PipeMessage message)
    {
        switch (message.Kind)
        {
            case PipeMessageKind.Response when message.Id is { } id:
                TaskCompletionSource<PipeMessage>? waiter;
                lock (_pendingLock)
                {
                    _pending.TryGetValue(id, out waiter);
                }

                waiter?.TrySetResult(message);
                break;

            case PipeMessageKind.Event:
                _onEvent?.Invoke(message);
                break;

            default:
                throw new InvalidDataException($"Unexpected message of kind '{message.Kind}' from the service.");
        }
    }
}
