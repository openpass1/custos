using System.Globalization;
using Custos.Core;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Custos.Service;

/// <summary>
/// Service logging on macOS: a daily file in /Library/Application Support/CUSTOS/logs (deleted by the service after
/// the retention period of the settings). launchd keeps what the process prints in service-console.log.
/// </summary>
internal static class LoggingSetup
{
    /// <summary>The files of the service log: <c>service-20261115.log</c>.</summary>
    public const string ServiceLogPattern = "service-*.log";

    private const string FileTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    public static Logger CreateLogger(bool writeToConsole)
    {
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .WriteTo.File(
                Path.Combine(CustosInfo.LogsDirectory, "service-.log"),
                outputTemplate: FileTemplate,
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: null);

        if (writeToConsole)
        {
            configuration.WriteTo.Console(formatProvider: CultureInfo.InvariantCulture);
        }

        return configuration.CreateLogger();
    }
}
