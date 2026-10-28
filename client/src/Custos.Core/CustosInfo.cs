using System.Reflection;

namespace Custos.Core;

/// <summary>Names and locations shared by all CUSTOS programs.</summary>
public static class CustosInfo
{
    /// <summary>Windows service name, as used by sc.exe and the installer.</summary>
    public const string ServiceName = "CustosService";

    /// <summary>Source name in the Windows Application event log.</summary>
    public const string EventLogSource = "CUSTOS";

    /// <summary>How long log files are kept unless the settings say otherwise (section 6.10).</summary>
    public const int DefaultLogRetentionDays = 180;

    /// <summary>Product version, for example "0.1.0". Every CUSTOS assembly is built with the same version.</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary><c>C:\ProgramData\CUSTOS</c>: settings, database and logs of the service.</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CUSTOS");

    /// <summary><c>C:\ProgramData\CUSTOS\logs</c>.</summary>
    public static string LogsDirectory { get; } = Path.Combine(DataDirectory, "logs");

    private static string ReadVersion()
    {
        var assembly = typeof(CustosInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return informational ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
