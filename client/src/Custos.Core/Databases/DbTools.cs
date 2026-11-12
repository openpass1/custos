using System.Text;
using Custos.Core.Model;

namespace Custos.Core.Databases;

/// <summary>
/// The client programs of a database server (section 6.2) and the names CUSTOS gives to dumps. MySQL and MariaDB use
/// <c>mysqldump</c> and <c>mysql</c> (MariaDB prefers its own <c>mariadb-dump</c> and <c>mariadb</c>), PostgreSQL
/// uses <c>pg_dump</c>, <c>pg_dumpall</c> and <c>psql</c>.
/// </summary>
public static class DbTools
{
    /// <summary>Databases of the server itself; never dumped as a database of their own (section 3.3 of
    /// CUSTOS-remote-pull.md: the same list as custos-helper). Users and rights go to <c>_users.sql</c>.</summary>
    public static IReadOnlyList<string> SystemDatabases(string engine) => engine == DbEngines.PostgreSql
        ? ["template0", "template1"]
        : ["information_schema", "performance_schema", "sys", "mysql"];

    /// <summary>Database names of MySQL and MariaDB on Windows are not case-sensitive; those of PostgreSQL are.</summary>
    public static StringComparer NameComparer(string engine) =>
        engine == DbEngines.PostgreSql ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>"mysqldump" → "mysqldump.exe" on Windows; unchanged elsewhere (tests on Linux).</summary>
    public static string ExeName(string tool) => OperatingSystem.IsWindows() ? tool + ".exe" : tool;

    /// <summary>The program that dumps one database, or null if the folder has none.</summary>
    public static string? DumpTool(string binDir, string engine) => engine switch
    {
        DbEngines.PostgreSql => Find(binDir, "pg_dump"),
        DbEngines.MariaDb => Find(binDir, "mariadb-dump") ?? Find(binDir, "mysqldump"),
        _ => Find(binDir, "mysqldump") ?? Find(binDir, "mariadb-dump"),
    };

    /// <summary>The program that runs queries, or null.</summary>
    public static string? QueryTool(string binDir, string engine) => engine switch
    {
        DbEngines.PostgreSql => Find(binDir, "psql"),
        DbEngines.MariaDb => Find(binDir, "mariadb") ?? Find(binDir, "mysql"),
        _ => Find(binDir, "mysql") ?? Find(binDir, "mariadb"),
    };

    /// <summary>The program that dumps users and roles of PostgreSQL, or null.</summary>
    public static string? DumpAllTool(string binDir) => Find(binDir, "pg_dumpall");

    /// <summary>The names of the programs CUSTOS needs, for messages.</summary>
    public static string NeededTools(string engine) => engine switch
    {
        DbEngines.PostgreSql => "pg_dump, pg_dumpall, psql",
        DbEngines.MariaDb => "mariadb-dump (mysqldump), mariadb (mysql)",
        _ => "mysqldump, mysql",
    };

    /// <summary>The engine a folder of client programs belongs to, or null if it holds none of them.</summary>
    public static string? EngineOf(string binDir)
    {
        if (Find(binDir, "pg_dump") is not null)
        {
            return DbEngines.PostgreSql;
        }

        if (Find(binDir, "mariadb-dump") is not null || Find(binDir, "mariadbd") is not null)
        {
            return DbEngines.MariaDb;
        }

        if (Find(binDir, "mysqldump") is not null)
        {
            // Older MariaDB has only the mysql* names; its folder still says what it is.
            return binDir.Contains("mariadb", StringComparison.OrdinalIgnoreCase) ? DbEngines.MariaDb : DbEngines.MySql;
        }

        return null;
    }

    /// <summary>
    /// The name of a dump file: the database name with everything but <c>A-Z a-z 0-9 _ . -</c> replaced by "_" (the
    /// real name is in the manifest), made unique among <paramref name="used"/> with a number ("site_2"). A name that
    /// is empty, starts with a dot or is "_users" (the file of the users) gets a "_" in front.
    /// </summary>
    public static string FileName(string database, HashSet<string> used)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(used);
        var builder = new StringBuilder(database.Length);
        foreach (char c in database)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '_');
        }

        string name = builder.ToString();
        if (name.Length == 0 || name[0] == '.' || string.Equals(name, UsersFileStem, StringComparison.OrdinalIgnoreCase))
        {
            name = "_" + name;
        }

        if (name.Length > 120)
        {
            name = name[..120];
        }

        string candidate = name;
        for (int i = 2; !used.Add(candidate); i++)
        {
            candidate = name + "_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return candidate;
    }

    /// <summary>The file of the users and rights of a server: <c>_users.sql</c>.</summary>
    public const string UsersFileStem = "_users";

    /// <summary>The folder of the dumps inside a set.</summary>
    public const string SetFolder = "db";

    private static string? Find(string binDir, string tool)
    {
        if (string.IsNullOrWhiteSpace(binDir))
        {
            return null;
        }

        try
        {
            string path = Path.Combine(binDir, ExeName(tool));
            return File.Exists(path) ? path : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
