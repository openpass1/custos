using Custos.Core;
using Microsoft.Extensions.Logging;

namespace Custos.Service;

/// <summary>
/// Every message the service logs. IDs from <see cref="CustosEventIds"/> also go to the Windows event log;
/// IDs below 1000 are internal and go to the event log only at Error level (as <see cref="CustosEventIds.UnexpectedError"/>).
/// </summary>
internal static partial class ServiceLog
{
    [LoggerMessage(EventId = CustosEventIds.ServiceStarted, Level = LogLevel.Information,
        Message = "CUSTOS service {Version} started ({RunMode}, account {Account})")]
    public static partial void ServiceStarted(ILogger logger, string version, RunMode runMode, string account);

    [LoggerMessage(EventId = CustosEventIds.ServiceStopped, Level = LogLevel.Information,
        Message = "CUSTOS service {Version} stopped")]
    public static partial void ServiceStopped(ILogger logger, string version);

    [LoggerMessage(EventId = 100, Level = LogLevel.Information,
        Message = "Interface pipe {PipeName} is open")]
    public static partial void PipeOpened(ILogger logger, string pipeName);

    [LoggerMessage(EventId = 101, Level = LogLevel.Error,
        Message = "Cannot open the interface pipe {PipeName}; retrying in {RetrySeconds} s. The interface cannot reach the service until then")]
    public static partial void PipeFailed(ILogger logger, Exception exception, string pipeName, int retrySeconds);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information,
        Message = "Interface connected: connection {ConnectionId}, user {User}, administrator {IsAdmin}, session {SessionId}, process {ProcessId}, version {ClientVersion}")]
    public static partial void ClientConnected(ILogger logger, long connectionId, string user, bool isAdmin, int? sessionId, int? processId, string clientVersion);

    [LoggerMessage(EventId = 103, Level = LogLevel.Information,
        Message = "Interface disconnected: connection {ConnectionId}")]
    public static partial void ClientDisconnected(ILogger logger, long connectionId);

    [LoggerMessage(EventId = 104, Level = LogLevel.Warning,
        Message = "Interface connection {ConnectionId} broke the protocol and was closed")]
    public static partial void ClientProtocolError(ILogger logger, Exception exception, long connectionId);

    [LoggerMessage(EventId = 105, Level = LogLevel.Warning,
        Message = "Cannot identify the user of interface connection {ConnectionId}")]
    public static partial void ClientIdentityFailed(ILogger logger, Exception exception, long connectionId);

    [LoggerMessage(EventId = 106, Level = LogLevel.Warning,
        Message = "Interface connection {ConnectionId} uses protocol {ClientProtocol}, the service uses {ServiceProtocol}")]
    public static partial void ClientProtocolMismatch(ILogger logger, long connectionId, int clientProtocol, int serviceProtocol);

    [LoggerMessage(EventId = 107, Level = LogLevel.Error,
        Message = "Request {Method} on interface connection {ConnectionId} failed")]
    public static partial void RequestFailed(ILogger logger, Exception exception, string method, long connectionId);

    [LoggerMessage(EventId = 108, Level = LogLevel.Error,
        Message = "Interface connection {ConnectionId} failed")]
    public static partial void ConnectionFailed(ILogger logger, Exception exception, long connectionId);
}
