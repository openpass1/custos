using System.Globalization;
using System.Xml.Linq;

namespace Custos.Service.Platform;

/// <summary>What the agent tells the monitoring server about this Mac (A.3, A.4).</summary>
internal static class MachineInfo
{
    private const string SystemVersionFile = "/System/Library/CoreServices/SystemVersion.plist";

    /// <summary>The hardware UUID of the Mac: stays the same when macOS is installed anew.</summary>
    public static string MachineGuid() => Native.HostUuid() ?? ReadMachineId() ?? "unknown";

    /// <summary>"macOS 26.1 (25B78)". The site tells Macs from Windows computers by the word "macOS".</summary>
    public static string OsDescription()
    {
        try
        {
            if (File.Exists(SystemVersionFile))
            {
                var values = ReadPlist(XDocument.Load(SystemVersionFile));
                string version = values.GetValueOrDefault("ProductVersion") ?? "?";
                return values.TryGetValue("ProductBuildVersion", out string? build) ? $"macOS {version} ({build})" : $"macOS {version}";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // Below: the version .NET knows.
        }

        return OperatingSystem.IsMacOS()
            ? string.Create(CultureInfo.InvariantCulture, $"macOS {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}")
            : System.Runtime.InteropServices.RuntimeInformation.OSDescription;
    }

    /// <summary>The time zone of the computer as an IANA name ("Europe/Moscow"), as the server wants it.</summary>
    public static string TimeZone()
    {
        var local = TimeZoneInfo.Local;
        return local.HasIanaId && local.Id != "Local" ? local.Id : "UTC";
    }

    /// <summary>The string keys and values of the top dictionary of a property list.</summary>
    internal static Dictionary<string, string> ReadPlist(XDocument plist)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var dict = plist.Root?.Element("dict");
        if (dict is null)
        {
            return values;
        }

        string? key = null;
        foreach (var element in dict.Elements())
        {
            if (element.Name == "key")
            {
                key = element.Value;
            }
            else if (key is not null)
            {
                if (element.Name == "string")
                {
                    values[key] = element.Value;
                }

                key = null;
            }
        }

        return values;
    }

    /// <summary>Linux (tests): the id of the installation.</summary>
    private static string? ReadMachineId()
    {
        try
        {
            return File.Exists("/etc/machine-id") ? File.ReadAllText("/etc/machine-id").Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
