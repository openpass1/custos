using Custos.Core;

namespace Custos.Service;

/// <summary>How the process was started.</summary>
internal enum RunMode
{
    /// <summary>By launchd, as the service.</summary>
    Service,

    /// <summary>From a terminal, for diagnostics.</summary>
    Console,
}

/// <summary>Facts about this service process.</summary>
internal sealed class ServiceInfo(RunMode runMode)
{
    public RunMode RunMode { get; } = runMode;

    public string Version { get; } = CustosInfo.Version;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    /// <summary>Account the process runs under; root for the installed service.</summary>
    public string Account { get; } = Environment.UserName;
}
