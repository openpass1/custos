using Custos.Core;
using Custos.Service;
using Custos.Service.Agent;
using Custos.Service.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Serilog;

// Started by the Service Control Manager it runs as the Windows service; started from a console it runs in the
// foreground and also prints its log there (stop it with Ctrl+C). The console mode is for diagnostics only.
var runMode = WindowsServiceHelpers.IsWindowsService() ? RunMode.Service : RunMode.Console;
Log.Logger = LoggingSetup.CreateLogger(writeToConsole: runMode == RunMode.Console);

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddWindowsService(options => options.ServiceName = CustosInfo.ServiceName);
    builder.Logging.ClearProviders();
    builder.Services.AddSerilog();

    // A run in progress is stopped and cleaned up when the service stops; give it time for that.
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
    await host.RunAsync().ConfigureAwait(false);

    // The agent could not start (the error is in the log): a non-zero exit code makes the Service Control Manager
    // restart the service. The agent is taken before RunAsync, which disposes the host and its services.
    return agent.ExecuteTask is { IsFaulted: true } ? 1 : 0;
}
catch (Exception ex)
{
    // A non-zero exit code makes the Service Control Manager apply the recovery actions (restart).
    Log.Fatal(ex, "CUSTOS service terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}
