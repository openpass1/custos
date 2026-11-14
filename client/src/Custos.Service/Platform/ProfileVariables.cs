using System.Runtime.Versioning;
using System.Security.Principal;
using Custos.Core.Backup;
using Microsoft.Win32;

namespace Custos.Service.Platform;

/// <summary>
/// The environment variables of a user profile for the paths of a task (section 6.2). The service runs as SYSTEM:
/// its own <c>%USERPROFILE%</c> is the profile of the system, so these come from the profile of the task's user.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProfileVariables
{
    private const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    /// <summary>A lookup: the variables of the profile with this SID first, then those of the system.</summary>
    public static Func<string, string?> For(string? sid)
    {
        var values = sid is null ? [] : Read(sid);
        return name => values.TryGetValue(name, out string? value) ? value
            : PathVariables.ProfileNames.Contains(name) ? null
            : Environment.GetEnvironmentVariable(name);
    }

    /// <summary>Whether this computer has a profile for the SID (a task from another computer may name one it lacks).</summary>
    public static bool HasProfile(string sid)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(Path.Combine(ProfileList, sid));
            return key?.GetValue("ProfileImagePath") is string { Length: > 0 };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return false;
        }
    }

    private static Dictionary<string, string> Read(string sid)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? profile;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(Path.Combine(ProfileList, sid));
            profile = key?.GetValue("ProfileImagePath") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            profile = null;
        }

        if (string.IsNullOrWhiteSpace(profile))
        {
            return values;
        }

        profile = Environment.ExpandEnvironmentVariables(profile);
        string local = Path.Combine(profile, "AppData", "Local");
        values["USERPROFILE"] = profile;
        values["APPDATA"] = Path.Combine(profile, "AppData", "Roaming");
        values["LOCALAPPDATA"] = local;
        values["TEMP"] = Path.Combine(local, "Temp");
        values["TMP"] = values["TEMP"];
        if (Path.GetPathRoot(profile) is { Length: > 0 } root)
        {
            values["HOMEDRIVE"] = root.TrimEnd('\\');
            values["HOMEPATH"] = profile[values["HOMEDRIVE"].Length..];
        }

        try
        {
            if (new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value is { } account)
            {
                int slash = account.IndexOf('\\', StringComparison.Ordinal);
                values["USERDOMAIN"] = slash > 0 ? account[..slash] : Environment.MachineName;
                values["USERNAME"] = slash > 0 ? account[(slash + 1)..] : account;
            }
        }
        catch (SystemException)
        {
            // A deleted account: the paths of its profile still work.
        }

        // While the user is signed in, the registry of the profile is loaded: their own variables (OneDrive and
        // others) and the real folders of the session.
        ReadUserKey(sid + @"\Environment", values);
        ReadUserKey(sid + @"\Volatile Environment", values);
        return values;
    }

    private static void ReadUserKey(string path, Dictionary<string, string> values)
    {
        try
        {
            using var key = Registry.Users.OpenSubKey(path);
            if (key is null)
            {
                return;
            }

            var own = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in key.GetValueNames())
            {
                if (name.Length > 0 && key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value)
                {
                    own[name] = value;
                }
            }

            // Values like %USERPROFILE%\OneDrive refer to the profile, not to the service.
            foreach (var (name, value) in own)
            {
                if (name.Equals("PATH", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                values[name] = PathVariables.ExpandEnvironment(value, n => values.TryGetValue(n, out string? v) ? v : Environment.GetEnvironmentVariable(n));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Not readable: the variables derived from the profile folder stay.
        }
    }
}
