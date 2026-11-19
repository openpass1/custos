using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Custos.Core.Model;

namespace Custos.Core.Databases;

/// <summary>A folder with the client programs of a database server, found on this computer.</summary>
/// <param name="Engine">One of <see cref="DbEngines"/>.</param>
/// <param name="BinDir">The folder that holds the programs.</param>
/// <param name="Version">From the programs or the folder name ("8.0", "16"); null when neither tells.</param>
public sealed record DbBinFolder(string Engine, string BinDir, string? Version);

/// <summary>
/// Looks for the client programs of MySQL, MariaDB and PostgreSQL in the usual places: the folders of development
/// panels (OSPanel, Open Server, Laragon, XAMPP, WAMP) and of the installers (<c>Program Files\MySQL\…</c>,
/// <c>MariaDB 11.4</c>, <c>PostgreSQL\17</c>). Only folder names are read and nothing is started: the service runs
/// this as SYSTEM. The running servers are found by the service (processes and listening ports).
/// </summary>
public static partial class DbServerFinder
{
    /// <summary>How deep under a panel or server folder the programs may lie (Laragon: bin\mysql\mysql-8.0\bin).</summary>
    public const int PanelDepth = 4;

    /// <summary>Folders below a root where panels and servers are looked for (C:\OSPanel, D:\Programs\OSPanel).</summary>
    public const int RootDepth = 2;

    // Folders that hold data, sites or logs rather than programs; there can be very many of them.
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "data", "userdata", "home", "domains", "www", "htdocs", "projects", "logs", "log", "temp", "tmp", "backup",
        "backups", "node_modules", ".git", "share", "include", "lib", "doc", "docs", "symbols", "addons", "config",
        "pgAdmin 4", "StackBuilder", "Windows", "$Recycle.Bin", "System Volume Information", "Users",
    };

    /// <summary>The folders with database programs under <paramref name="roots"/> (drives, Program Files).</summary>
    public static List<DbBinFolder> Scan(IEnumerable<string> roots, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var found = new List<DbBinFolder>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string folder in Interesting(root, RootDepth, cancellationToken))
            {
                Search(folder, PanelDepth, found, seen, cancellationToken);
            }
        }

        return found;
    }

    /// <summary>What a folder of programs is: its engine and version, or null if it holds none of the programs.</summary>
    public static DbBinFolder? Describe(string binDir)
    {
        if (DbTools.EngineOf(binDir) is not { } engine)
        {
            return null;
        }

        string? tool = DbTools.DumpTool(binDir, engine);
        return new DbBinFolder(engine, binDir, ProgramVersion(tool) ?? FolderVersion(binDir));
    }

    /// <summary>"8.0" from "MySQL-8.0\bin", "16" from "PostgreSQL\16\bin", "11.4.2" from "mariadb-11.4.2-winx64\bin".</summary>
    public static string? FolderVersion(string binDir)
    {
        ArgumentNullException.ThrowIfNull(binDir);
        string folder = Path.TrimEndingDirectorySeparator(binDir.Replace('\\', Path.DirectorySeparatorChar));
        for (int i = 0; i < 2 && folder.Length > 0; i++)
        {
            folder = Path.GetDirectoryName(folder) ?? string.Empty;
            var match = VersionInName().Match(Path.GetFileName(folder));
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    /// <summary>Whether a folder name is that of a panel or a database server.</summary>
    public static bool IsInterestingName(string name) => InterestingName().IsMatch(name);

    private static IEnumerable<string> Interesting(string root, int depth, CancellationToken cancellationToken)
    {
        foreach (string folder in Subfolders(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(folder);
            if (IsInterestingName(name))
            {
                yield return folder;
            }
            else if (depth > 1 && !Skipped.Contains(name))
            {
                foreach (string inner in Interesting(folder, depth - 1, cancellationToken))
                {
                    yield return inner;
                }
            }
        }
    }

    private static void Search(string folder, int depth, List<DbBinFolder> found, HashSet<string> seen, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!seen.Add(folder))
        {
            return;
        }

        if (Describe(folder) is { } described)
        {
            found.Add(described);
            return;
        }

        if (depth == 0)
        {
            return;
        }

        foreach (string inner in Subfolders(folder))
        {
            if (!Skipped.Contains(Path.GetFileName(inner)))
            {
                Search(inner, depth - 1, found, seen, cancellationToken);
            }
        }
    }

    private static string[] Subfolders(string folder)
    {
        try
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
            string[] folders = Directory.GetDirectories(folder, "*", options);
            Array.Sort(folders, StringComparer.OrdinalIgnoreCase);
            return folders;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static string? ProgramVersion(string? program)
    {
        if (program is null || !OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            // The version resource of the .exe: read from the file, the program is not started.
            var info = FileVersionInfo.GetVersionInfo(program);
            return info.FileMajorPart > 0
                ? info.FileBuildPart > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}")
                    : string.Create(CultureInfo.InvariantCulture, $"{info.FileMajorPart}.{info.FileMinorPart}")
                : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static readonly Regex VersionInNameRegex = new(@"(?:^|[^0-9.])(\d{1,2}(?:\.\d{1,3}){0,2})(?:[^0-9.]|$)", RegexOptions.CultureInvariant);

    private static Regex VersionInName() => VersionInNameRegex;

    private static readonly Regex InterestingNameRegex = new(@"^(ospanel|open ?server|openserver|osp|laragon|xampp|wamp|mysql|mariadb|postgres|postgresql|pgsql)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static Regex InterestingName() => InterestingNameRegex;
}
