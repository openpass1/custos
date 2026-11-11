using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Custos.Service;

/// <summary>Writes the "service started" and "service stopped" events (1000 and 1001).</summary>
internal sealed class ServiceLifecycle(ServiceInfo info, ILogger<ServiceLifecycle> logger) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        ServiceLog.ServiceStarted(logger, info.Version, info.RunMode, info.Account);
        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        ServiceLog.ServiceStopped(logger, info.Version);
        return Task.CompletedTask;
    }
}
