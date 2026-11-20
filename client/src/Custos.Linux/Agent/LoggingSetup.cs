using System.Globalization;
using Custos.Core;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Custos.Service;

/// <summary>
/// Service logging on Linux: a daily file in /var/log/custos (deleted by the service after the retention period of the
/// settings). Run by systemd, the warnings and errors also go to its journal (journalctl -u custos), short and without
/// the time, which the journal adds itself.
/// </summary>
internal static class LoggingSetup
{
    /// <summary>The files of the service log: <c>service-20261115.log</c>.</summary>
    public const string ServiceLogPattern = "service-*.log";

    private const string FileTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    private const string JournalTemplate = "[{Level:u3}] {Message:lj}{NewLine}{Exception}";

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

        configuration = writeToConsole
            ? configuration.WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            : configuration.WriteTo.Console(outputTemplate: JournalTemplate, formatProvider: CultureInfo.InvariantCulture, restrictedToMinimumLevel: LogEventLevel.Warning);

        return configuration.CreateLogger();
    }
}
