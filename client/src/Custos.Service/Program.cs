using Custos.Core;
using Custos.Service;
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

    builder.Services.AddSingleton(new ServiceInfo(runMode));
    builder.Services.AddHostedService<ServiceLifecycle>();
    builder.Services.AddHostedService<PipeServer>();

    using var host = builder.Build();
    await host.RunAsync().ConfigureAwait(false);
    return 0;
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
