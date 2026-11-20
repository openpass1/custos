using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Custos.Core.Databases;
using Custos.Core.Ipc;
using Custos.Core.Model;

namespace Custos.Service.Platform;

/// <summary>
/// The database servers of a Linux server (section 6.2): «Find» lists the running ones (mysqld, mariadbd, postgres) with
/// the port they listen on (from ss), and the folder of their client programs; a task reaches a server of this computer
/// without a password the way a script of root does (<see cref="LocalAccess"/>).
/// </summary>
internal static partial class DbFinder
{
    private static readonly (string Process, string Engine)[] ServerProcesses =
    [
        ("mysqld", DbEngines.MySql),
        ("mariadbd", DbEngines.MariaDb),
        ("postgres", DbEngines.PostgreSql),
    ];

    /// <summary>Where the sockets of MySQL and MariaDB usually are: Debian and Ubuntu, then RHEL and its kin.</summary>
    private static readonly string[] MySqlSockets =
        ["/run/mysqld/mysqld.sock", "/var/run/mysqld/mysqld.sock", "/var/lib/mysql/mysql.sock", "/run/mariadb/mariadb.sock", "/tmp/mysql.sock"];

    /// <summary>Where PostgreSQL keeps its sockets.</summary>
    private static readonly string[] PostgresSocketFolders = ["/run/postgresql", "/var/run/postgresql", "/tmp"];

    public static DbFindResult Find(CancellationToken cancellationToken = default)
    {
        var result = new DbFindResult();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var listening = Listening();
        foreach (var (name, engine) in ServerProcesses)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // PostgreSQL runs many postgres processes; only the first of them listens. MySQL 8 also listens on
                    // 33060 (X protocol): the lowest port is the one for mysqldump.
                    var port = listening.GetValueOrDefault(process.Id);
                    if (port is null && engine == DbEngines.PostgreSql)
                    {
                        continue;
                    }

                    string? folder = ToolFolder(engine);
                    var described = folder is null ? null : DbServerFinder.Describe(folder);
                    int actualPort = port?.Port ?? DbEngines.DefaultPort(engine);
                    string host = port?.Host ?? "localhost";
                    if (!seen.Add(engine + "|" + host + "|" + actualPort))
                    {
                        continue;
                    }

                    result.Servers.Add(new DbCandidate
                    {
                        Engine = described?.Engine ?? engine,
                        BinDir = folder ?? "/usr/bin",
                        Version = described?.Version,
                        Host = host,
                        Port = actualPort,
                        Running = true,
                    });
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The folder of the client programs of an engine: where the system finds pg_dump or mysqldump (/usr/bin, with
    /// the wrapper of Debian that picks the newest PostgreSQL), else the newest /usr/pgsql-N/bin (packages of
    /// PostgreSQL for RHEL) or /usr/lib/postgresql/N/bin.
    /// </summary>
    public static string? ToolFolder(string engine)
    {
        string[] names = engine switch
        {
            DbEngines.PostgreSql => ["pg_dump"],
            DbEngines.MariaDb => ["mariadb-dump", "mysqldump"],
            _ => ["mysqldump", "mariadb-dump"],
        };
        foreach (string name in names)
        {
            if (Tool.Find(name) is { } path)
            {
                return Path.GetDirectoryName(path);
            }
        }

        if (engine != DbEngines.PostgreSql)
        {
            return null;
        }

        var versioned = new List<(int Version, string Folder)>();
        foreach (var (parent, pattern) in new[] { ("/usr", "pgsql-*"), ("/usr/lib/postgresql", "*") })
        {
            if (!Directory.Exists(parent))
            {
                continue;
            }

            foreach (string folder in Directory.EnumerateDirectories(parent, pattern))
            {
                string bin = Path.Combine(folder, "bin");
                string number = Path.GetFileName(folder).Replace("pgsql-", string.Empty, StringComparison.Ordinal);
                if (File.Exists(Path.Combine(bin, "pg_dump")) && int.TryParse(number.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out int version))
                {
                    versioned.Add((version, bin));
                }
            }
        }

        return versioned.Count == 0 ? null : versioned.MaxBy(v => v.Version).Folder;
    }

    /// <summary>
    /// How to reach a server of this computer without a password: MySQL and MariaDB through their socket as root
    /// (unix_socket or auth_socket of root, as on Debian and Ubuntu), PostgreSQL through its socket as the account
    /// postgres (peer authentication). Null when the server is not on this computer.
    /// </summary>
    public static DbLocalAccess? LocalAccess(TaskDatabaseServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!IsThisComputer(server.Host))
        {
            return null;
        }

        if (server.Engine != DbEngines.PostgreSql)
        {
            return new DbLocalAccess(MySqlSockets.FirstOrDefault(File.Exists));
        }

        int port = server.Port ?? DbEngines.DefaultPort(server.Engine);
        string socketName = ".s.PGSQL." + port.ToString(CultureInfo.InvariantCulture);
        string? folder = PostgresSocketFolders.FirstOrDefault(f => File.Exists(Path.Combine(f, socketName)));
        bool asPostgres = Native.IsRoot && Native.Account("postgres") is not null && Tool.Find("runuser") is not null;
        return new DbLocalAccess(folder ?? "/var/run/postgresql", asPostgres ? "postgres" : null, asPostgres ? Tool.Find("runuser") : null);
    }

    /// <summary>"localhost", "127.0.0.1", "::1" or nothing.</summary>
    internal static bool IsThisComputer(string? host) =>
        string.IsNullOrWhiteSpace(host) || host.Trim() is "localhost" or "127.0.0.1" or "::1" or "[::1]";

    /// <summary>The address and lowest TCP port each process listens on, from <c>ss -ltnpH</c>.</summary>
    private static Dictionary<int, (string Host, int Port)?> Listening()
    {
        var result = new Dictionary<int, (string Host, int Port)?>();
        var (code, _) = Tool.Run("ss", ["-l", "-t", "-n", "-p", "-H"], out string output, TimeSpan.FromSeconds(20));
        if (code != 0)
        {
            return result;
        }

        foreach (string line in output.Split('\n'))
        {
            // "LISTEN 0 151 127.0.0.1:3306 0.0.0.0:* users:(("mysqld",pid=812,fd=23))"
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 6)
            {
                continue;
            }

            var address = Address().Match(fields[3]);
            if (!address.Success || !int.TryParse(address.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int port))
            {
                continue;
            }

            string host = address.Groups[1].Value.Trim('[', ']');
            host = host is "*" or "0.0.0.0" or "::" ? "127.0.0.1" : host.Split('%')[0];
            foreach (Match pid in ProcessId().Matches(line))
            {
                int id = int.Parse(pid.Groups[1].Value, CultureInfo.InvariantCulture);
                if (result.GetValueOrDefault(id) is not { } known || port < known.Port)
                {
                    result[id] = (host, port);
                }
            }
        }

        return result;
    }

    [GeneratedRegex(@"^(.+):(\d+)$")]
    private static partial Regex Address();

    [GeneratedRegex(@"pid=(\d+)")]
    private static partial Regex ProcessId();
}
