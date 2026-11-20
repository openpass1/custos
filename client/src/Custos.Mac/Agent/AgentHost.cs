using Custos.Core;
using Custos.Service.Agent;
using Custos.Service.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Custos.Service;

/// <summary>
/// The service of macOS: <c>Custos agent</c>. Started by launchd (the job <see cref="CustosInfo.MacServiceLabel"/>,
/// which passes <c>--launchd</c>) it is the service; started from a terminal with sudo it runs in the foreground and
/// also prints its log there (stop it with Ctrl+C), for diagnostics only. launchd stops it with SIGTERM, which stops
/// the host as the Service Control Manager does on Windows.
/// </summary>
internal static class AgentHost
{
    /// <param name="stop">Stops the service as SIGTERM does; for the tests, which run it inside their own process.</param>
    public static async Task<int> RunAsync(string[] args, CancellationToken stop = default)
    {
        var runMode = args.Contains("--launchd", StringComparer.Ordinal) ? RunMode.Service : RunMode.Console;
        Log.Logger = LoggingSetup.CreateLogger(writeToConsole: runMode == RunMode.Console);
        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                ContentRootPath = AppContext.BaseDirectory,
                DisableDefaults = false,
            });
            builder.Logging.ClearProviders();
            builder.Services.AddSerilog();

            // A run in progress is stopped and cleaned up when the service stops; give it time for that (launchd
            // waits 30 seconds, ExitTimeOut of the job).
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(25));

            builder.Services.AddSingleton(new ServiceInfo(runMode));
            builder.Services.AddSingleton<AgentEvents>();
            builder.Services.AddSingleton<AgentService>();

            // Started in this order and stopped in reverse: the interfaces hear "stopping" before the runs are stopped.
            builder.Services.AddHostedService<ServiceLifecycle>();
            builder.Services.AddHostedService(services => services.GetRequiredService<AgentService>());
            builder.Services.AddHostedService<PipeServer>();

            using var host = builder.Build();
            var agent = host.Services.GetRequiredService<AgentService>();
            await host.RunAsync(stop).ConfigureAwait(false);

            // The agent could not start (the error is in the log): launchd starts the job again (KeepAlive).
            return agent.ExecuteTask is { IsFaulted: true } ? 1 : 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "CUSTOS service terminated unexpectedly");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }
}
