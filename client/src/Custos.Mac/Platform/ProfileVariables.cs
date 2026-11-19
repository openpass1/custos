using Custos.Core.Backup;

namespace Custos.Service.Platform;

/// <summary>
/// The variables of a user's account for the paths of a task (section 6.2). The service runs as root, so
/// <c>%USERPROFILE%</c> and <c>%HOME%</c> are the home folder of whoever made the task. On macOS the task keeps the
/// name of that account where Windows keeps a SID.
/// </summary>
internal static class ProfileVariables
{
    /// <summary>A lookup: the variables of the account with this name first, then those of the service.</summary>
    public static Func<string, string?> For(string? user)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (user is not null && Native.Account(user) is { } account && account.Home.Length > 0)
        {
            values["USERPROFILE"] = account.Home;
            values["HOME"] = account.Home;
            values["USERNAME"] = account.Name;
            values["USER"] = account.Name;
            values["TMPDIR"] = Path.Combine(account.Home, "Library", "Caches");
            values["APPDATA"] = Path.Combine(account.Home, "Library", "Application Support");
            values["LOCALAPPDATA"] = values["APPDATA"];
        }

        return name => values.TryGetValue(name, out string? value) ? value
            : PathVariables.ProfileNames.Contains(name) || name.Equals("HOME", StringComparison.OrdinalIgnoreCase) ? null
            : Environment.GetEnvironmentVariable(name);
    }

    /// <summary>Whether this Mac has the account and its home folder (a task from another computer may name one it lacks).</summary>
    public static bool HasProfile(string user) =>
        !string.IsNullOrEmpty(user) && Native.Account(user) is { Home.Length: > 0 } account && Directory.Exists(account.Home);
}
