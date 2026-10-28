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

/// <summary>
/// Keeps a connection to the service: connects, says hello, follows its events and reconnects when the service
/// restarts. Raises <see cref="StatusChanged"/> on the UI thread.
/// </summary>
internal sealed class ServiceMonitor : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private readonly SynchronizationContext _ui;
    private readonly CancellationTokenSource _stop = new();

    public ServiceMonitor()
    {
        _ui = SynchronizationContext.Current
            ?? throw new InvalidOperationException("ServiceMonitor must be created on the UI thread.");
    }

    public event EventHandler? StatusChanged;

    public ServiceStatus Status { get; private set; } = new(ServiceState.Connecting);

    public void Start() => _ = RunAsync(_stop.Token);

    /// <summary>The service state in a few words, as in the status bar and the tray tooltip.</summary>
    public static string Describe(ServiceState state) => state switch
    {
        ServiceState.Connecting => Strings.StateConnecting,
        ServiceState.Running => Strings.StateRunning,
        ServiceState.Stopping => Strings.StateStopping,
        _ => Strings.StateUnavailable,
    };

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    // Runs on the UI thread: every await resumes there, so Status is only touched by one thread.
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await ServiceClient.ConnectAsync(OnServiceEvent, ConnectTimeout, cancellationToken);
                await using (client)
                {
                    var hello = await client.RequestAsync(
                        PipeProtocol.Methods.Hello,
                        new HelloRequest { Protocol = PipeProtocol.Version, ClientVersion = CustosInfo.Version },
                        IpcJsonContext.Default.HelloRequest,
                        IpcJsonContext.Default.HelloResult,
                        cancellationToken).WaitAsync(RequestTimeout, cancellationToken);
                    SetStatus(new ServiceStatus(ServiceState.Running, hello));

                    await client.Completion.WaitAsync(cancellationToken);
                    SetStatus(new ServiceStatus(ServiceState.Unavailable, Problem: Strings.ProblemConnectionLost));
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
                SetStatus(new ServiceStatus(ServiceState.Unavailable, Problem: DescribeProblem(ex)));
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

    // Called on a thread-pool thread by the connection's reader loop.
    private void OnServiceEvent(PipeMessage message)
    {
        if (message.Event != PipeProtocol.Events.ServiceState)
        {
            return;
        }

        var state = message.GetData(IpcJsonContext.Default.ServiceStateEvent);
        if (state.State == PipeProtocol.ServiceStates.Stopping)
        {
            _ui.Post(_ => SetStatus(Status with { State = ServiceState.Stopping }), null);
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
