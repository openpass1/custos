using System.Runtime.InteropServices;

namespace Custos.Service.Platform;

/// <summary>What the agent tells the monitoring server about this computer (A.3, A.4).</summary>
internal static class MachineInfo
{
    private const string OsReleaseFile = "/etc/os-release";

    /// <summary>The id of the installation (/etc/machine-id): stays the same until the system is installed anew.</summary>
    public static string MachineGuid()
    {
        foreach (string file in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            try
            {
                if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } id)
                {
                    return id;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The next one.
            }
        }

        return "unknown";
    }

    /// <summary>"Linux: Ubuntu 24.04.3 LTS (x64)". The site tells Linux computers from the others by the word "Linux".</summary>
    public static string OsDescription()
    {
        string name = ReadOsRelease(OsReleaseFile).GetValueOrDefault("PRETTY_NAME") ?? RuntimeInformation.OSDescription;
        string arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        return name.Contains("Linux", StringComparison.OrdinalIgnoreCase) ? $"{name} ({arch})" : $"Linux: {name} ({arch})";
    }

    /// <summary>The time zone of the computer as an IANA name ("Europe/Moscow"), as the server wants it.</summary>
    public static string TimeZone()
    {
        var local = TimeZoneInfo.Local;
        return local.HasIanaId && local.Id != "Local" ? local.Id : "UTC";
    }

    /// <summary>The fields of /etc/os-release (KEY="value" lines).</summary>
    internal static Dictionary<string, string> ReadOsRelease(string file)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(file))
            {
                return values;
            }

            foreach (string line in File.ReadLines(file))
            {
                int equals = line.IndexOf('=', StringComparison.Ordinal);
                if (equals <= 0 || line.StartsWith('#'))
                {
                    continue;
                }

                string value = line[(equals + 1)..].Trim();
                if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                {
                    value = value[1..^1];
                }

                values[line[..equals].Trim()] = value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An empty list.
        }

        return values;
    }
}
