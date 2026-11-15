using System.Globalization;
using Custos.Core;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.EventLog;

namespace Custos.Service;

/// <summary>
/// Service logging: a daily file in C:\ProgramData\CUSTOS\logs (deleted by the service after the retention period of
/// the settings) and the Windows Application event log under the source "CUSTOS" (section 6.10).
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
                retainedFileCountLimit: null)
            .WriteTo.Logger(eventLog => eventLog
                .Filter.ByIncludingOnly(EventLogIds.ShouldWrite)
                .WriteTo.EventLog(
                    CustosInfo.EventLogSource,
                    logName: "Application",
                    manageEventSource: false,
                    outputTemplate: "{Message:lj}{NewLine}{Exception}",
                    formatProvider: CultureInfo.InvariantCulture,
                    eventIdProvider: new EventLogIds()));

        if (writeToConsole)
        {
            configuration.WriteTo.Console(formatProvider: CultureInfo.InvariantCulture);
        }

        return configuration.CreateLogger();
    }

    /// <summary>Chooses which events reach the Windows event log and with which ID.</summary>
    private sealed class EventLogIds : IEventIdProvider
    {
        public static bool ShouldWrite(LogEvent logEvent) =>
            CustosEventIds.IsPublished(GetId(logEvent)) || logEvent.Level >= LogEventLevel.Error;

        public ushort ComputeEventId(LogEvent logEvent)
        {
            int id = GetId(logEvent);
            return (ushort)(CustosEventIds.IsPublished(id) ? id : CustosEventIds.UnexpectedError);
        }

        // Microsoft.Extensions.Logging passes its EventId to Serilog as a structure { Id, Name }.
        private static int GetId(LogEvent logEvent) =>
            logEvent.Properties.TryGetValue("EventId", out var value)
            && value is StructureValue structure
            && structure.Properties.FirstOrDefault(p => p.Name == "Id")?.Value is ScalarValue { Value: int id }
                ? id
                : 0;
    }
}
