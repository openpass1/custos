using Custos.Core;

namespace Custos.Service.Platform;

/// <summary>Where the service keeps its files on macOS: <c>/Library/Application Support/CUSTOS</c> (section 3.3).</summary>
internal static class DataFolders
{
    private const UnixFileMode Closed = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode Open = Closed | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    public static string Config { get; } = Path.Combine(CustosInfo.DataDirectory, "config");

    public static string Database { get; } = Path.Combine(CustosInfo.DataDirectory, "db");

    public static string RunLogs { get; } = Path.Combine(CustosInfo.LogsDirectory, "runs");

    /// <summary>The journal of each day.</summary>
    public static string Journal => CustosInfo.JournalDirectory;

    /// <summary>Downloaded updates.</summary>
    public static string Updates { get; } = Path.Combine(CustosInfo.DataDirectory, "updates");

    /// <summary>Where the snapshots of a run are mounted (see <see cref="ShadowCopies"/>).</summary>
    public static string Snapshots { get; } = Path.Combine(CustosInfo.DataDirectory, "snapshots");

    /// <summary>Where shares of network folders are mounted (see <see cref="SmbSession"/>).</summary>
    public static string Shares { get; } = Path.Combine(CustosInfo.DataDirectory, "shares");

    public static string DatabaseFile { get; } = Path.Combine(Database, "custos.db");

    /// <summary>Snapshots made and not yet deleted (see <see cref="ShadowCopies"/>).</summary>
    public static string PendingShadowsFile { get; } = Path.Combine(Database, "snapshots.txt");

    /// <summary>
    /// Creates the folders and closes <c>config</c>, <c>db</c> and <c>updates</c> to everyone but root (section 3.3):
    /// without that a user could make himself root by editing tasks.json, since the service runs what it says. Files
    /// a user may have put there beforehand get root as their owner. The logs stay readable for everyone.
    /// </summary>
    /// <returns>What could not be protected, for the service log.</returns>
    public static List<string> Prepare()
    {
        var problems = new List<string>();
        Create(CustosInfo.DataDirectory, Open, problems);
        Create(CustosInfo.LogsDirectory, Open, problems);
        Create(RunLogs, Open, problems);
        Create(Journal, Open, problems);
        foreach (string folder in new[] { Config, Database, Updates })
        {
            Create(folder, Closed, problems);
            if (Native.IsRoot)
            {
                TakeOwnership(folder, problems);
            }
        }

        // Snapshots and shares are mounted inside these two: never walked through.
        Create(Snapshots, Closed, problems);
        Create(Shares, Closed, problems);

        return problems;
    }

    private static void Create(string folder, UnixFileMode mode, List<string> problems)
    {
        try
        {
            Directory.CreateDirectory(folder, mode);
            File.SetUnixFileMode(folder, mode);
            if (Native.IsRoot && Native.chown(folder, 0, 0) != 0)
            {
                problems.Add($"{folder}: chown failed");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            problems.Add($"{folder}: {ex.Message}");
        }
    }

    private static void TakeOwnership(string folder, List<string> problems)
    {
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = 0,
                IgnoreInaccessible = true,
            }))
            {
                if (Native.lchown(entry, 0, 0) != 0)
                {
                    problems.Add($"{entry}: chown failed");
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            problems.Add($"{folder}: {ex.Message}");
        }
    }
}
