using System.Security.Principal;
using Custos.Core;

namespace Custos.Service;

/// <summary>How the process was started.</summary>
internal enum RunMode
{
    /// <summary>By the Service Control Manager.</summary>
    Service,

    /// <summary>From a console, for diagnostics.</summary>
    Console,
}

/// <summary>Facts about this service process.</summary>
internal sealed class ServiceInfo(RunMode runMode)
{
    public RunMode RunMode { get; } = runMode;

    public string Version { get; } = CustosInfo.Version;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    /// <summary>Account the process runs under; NT AUTHORITY\SYSTEM for the installed service.</summary>
    public string Account { get; } = ReadAccount();

    private static string ReadAccount()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.Name;
    }
}
