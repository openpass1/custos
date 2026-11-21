using System.Reflection;

namespace Custos.Core;

/// <summary>Names and locations shared by all CUSTOS programs.</summary>
public static class CustosInfo
{
    /// <summary>Windows service name, as used by sc.exe and the installer.</summary>
    public const string ServiceName = "CustosService";

    /// <summary>The label of the launchd service of macOS (/Library/LaunchDaemons/org.custos.agent.plist) and the bundle
    /// identifier of Custos.app.</summary>
    public const string MacServiceLabel = "org.custos.agent";

    /// <summary>The systemd unit of the service of CUSTOS for Linux (/etc/systemd/system/custos.service).</summary>
    public const string LinuxServiceUnit = "custos";

    /// <summary>Source name in the Windows Application event log.</summary>
    public const string EventLogSource = "CUSTOS";

    /// <summary>How long log files are kept unless the settings say otherwise (section 6.10).</summary>
    public const int DefaultLogRetentionDays = 180;

    /// <summary>How long the run history is kept unless the settings say otherwise (section 6.10).</summary>
    public const int DefaultHistoryRetentionDays = 365;

    /// <summary>The repository on GitHub whose releases (tags <c>client-vX.Y.Z</c>) bring new versions of the program
    /// to computers without a monitoring site. A fork that releases its own builds changes it.</summary>
    public const string GitHubRepository = "openpass1/custos";

    /// <summary>The page with the ways of supporting the project, linked from the status bar of the main window.</summary>
    public const string DonatePage = "https://github.com/" + GitHubRepository + "/blob/main/DONATE.md";

    /// <summary>Product version, for example "0.1.0". Every CUSTOS assembly is built with the same version.</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>
    /// <c>C:\ProgramData\CUSTOS</c>: settings, database and logs of the service; on macOS
    /// <c>/Library/Application Support/CUSTOS</c>, on Linux <c>/var/lib/custos</c>. Outside Windows the variable
    /// CUSTOS_DATA_DIR can point elsewhere (tests of the services of macOS and Linux without root).
    /// </summary>
    public static string DataDirectory { get; } = ReadDataDirectory();

    /// <summary><c>C:\ProgramData\CUSTOS\logs</c>; on Linux <c>/var/log/custos</c>, where logs belong there (inside the
    /// data folder when CUSTOS_DATA_DIR points elsewhere).</summary>
    public static string LogsDirectory { get; } = OperatingSystem.IsLinux() && !CustomDataDirectory
        ? "/var/log/custos"
        : Path.Combine(DataDirectory, "logs");

    /// <summary><c>C:\ProgramData\CUSTOS\logs\journal</c>: the journal of each day (section 6.10).</summary>
    public static string JournalDirectory { get; } = Path.Combine(LogsDirectory, "journal");

    private static string ReadDataDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CUSTOS");
        }

        if (Environment.GetEnvironmentVariable("CUSTOS_DATA_DIR") is { Length: > 0 } custom)
        {
            return Path.GetFullPath(custom);
        }

        return OperatingSystem.IsMacOS() ? "/Library/Application Support/CUSTOS" : "/var/lib/custos";
    }

    private static bool CustomDataDirectory =>
        !OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("CUSTOS_DATA_DIR") is { Length: > 0 };

    private static string ReadVersion()
    {
        var assembly = typeof(CustosInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return informational ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
