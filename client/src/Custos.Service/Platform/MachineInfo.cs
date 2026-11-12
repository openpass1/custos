using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Custos.Service.Platform;

/// <summary>What the agent tells the monitoring server about this computer (A.3, A.4).</summary>
[SupportedOSPlatform("windows")]
internal static class MachineInfo
{
    /// <summary>MachineGuid of Windows: stays the same as long as Windows is not installed anew.</summary>
    public static string MachineGuid()
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid") as string ?? "unknown";
    }

    /// <summary>"Windows 11 Pro 24H2 (26100.2033)".</summary>
    public static string OsDescription()
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string product = key?.GetValue("ProductName") as string ?? "Windows";
        string? display = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string;
        string? build = key?.GetValue("CurrentBuildNumber") as string;
        object? ubr = key?.GetValue("UBR");
        if (int.TryParse(build, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && number >= 22000
            && product.StartsWith("Windows 10", StringComparison.Ordinal))
        {
            // Windows 11 still calls itself Windows 10 in ProductName.
            product = "Windows 11" + product["Windows 10".Length..];
        }

        string text = display is null ? product : product + " " + display;
        if (build is not null)
        {
            text += ubr is int revision ? string.Create(CultureInfo.InvariantCulture, $" ({build}.{revision})") : $" ({build})";
        }

        return text;
    }

    /// <summary>The time zone of the computer as an IANA name ("Europe/Moscow"), as the server wants it.</summary>
    public static string TimeZone()
    {
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId)
        {
            return local.Id;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out string? iana) ? iana : "UTC";
    }
}
