using Custos.Core.Backup;

namespace Custos.Service.Platform;

/// <summary>
/// The variables of a user's account for the paths of a task (section 6.2): <c>%HOME%</c> and <c>%USERPROFILE%</c>
/// are the home folder of the account the task names (Linux keeps the name of the account where Windows keeps a SID).
/// A task made on the site names none: then they are those of the service, root.
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
            values["APPDATA"] = Path.Combine(account.Home, ".config");
            values["LOCALAPPDATA"] = Path.Combine(account.Home, ".local", "share");
        }

        return name => values.TryGetValue(name, out string? value) ? value
            : PathVariables.ProfileNames.Contains(name) || name.Equals("HOME", StringComparison.OrdinalIgnoreCase) ? null
            : Environment.GetEnvironmentVariable(name);
    }

    /// <summary>Whether this computer has the account and its home folder (a task from another computer may name one it lacks).</summary>
    public static bool HasProfile(string user) =>
        !string.IsNullOrEmpty(user) && Native.Account(user) is { Home.Length: > 0 } account && Directory.Exists(account.Home);
}
