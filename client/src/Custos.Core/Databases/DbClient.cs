using System.Diagnostics;
using System.Globalization;
using System.Text;
using Custos.Core.Model;

namespace Custos.Core.Databases;

/// <summary>Login and password of a database server; an empty or null password means none.</summary>
public sealed record DbAccount(string User, string? Password);

/// <summary>What a connection check finds out about a database server.</summary>
/// <param name="Version">As the server reports it ("8.0.36", "16.4").</param>
/// <param name="Databases">All databases, the system ones included, in the order of the server.</param>
/// <param name="DataDir">The data folder of the server; null when the account may not see it.</param>
public sealed record DbServerInfo(string Version, IReadOnlyList<string> Databases, string? DataDir);

/// <summary>How a dump program ended: its exit code and the last lines it wrote to stderr.</summary>
public readonly record struct DbDumpExit(int Code, string Errors);

/// <summary>A dump being taken: the SQL comes out of <see cref="Output"/>. Disposing before the end stops the program.</summary>
public interface IDbDump : IDisposable
{
    Stream Output { get; }

    /// <summary>Waits for the program to end, after <see cref="Output"/> was read to its end.</summary>
    DbDumpExit WaitForExit();
}

/// <summary>Talks to one database server through the programs of its client (section 6.2).</summary>
public interface IDbClient
{
    /// <summary>Connects and lists the databases.</summary>
    /// <exception cref="DbException">The programs are missing, or the server cannot be reached or refuses the login.</exception>
    DbServerInfo Inspect(CancellationToken cancellationToken);

    /// <summary>Starts the dump of one database, or with null of the users and rights of the server.</summary>
    /// <param name="version">The version from <see cref="Inspect"/> (the users of MySQL need it).</param>
    /// <exception cref="DbException">The program is missing or cannot be started.</exception>
    IDbDump StartDump(string? database, string version, CancellationToken cancellationToken);
}

/// <summary>A database server cannot be reached or a program of its client failed; the message is for the log.</summary>
public sealed class DbException : Exception
{
    public DbException()
    {
    }

    public DbException(string message)
        : base(message)
    {
    }

    public DbException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// How a database server of this computer is reached without a password, as a script run by root does on Linux: MySQL
/// and MariaDB through their socket as the account root (unix_socket or auth_socket), PostgreSQL through its socket as
/// the system account of the server (peer authentication).
/// </summary>
/// <param name="Socket">The socket file of MySQL and MariaDB, or the folder of the socket of PostgreSQL; null = the
/// default of the programs.</param>
/// <param name="RunAs">The system account the programs run as ("postgres"), or null for the account of the service.</param>
/// <param name="RunUser">The program that switches to it (/usr/sbin/runuser).</param>
public sealed record DbLocalAccess(string? Socket, string? RunAs = null, string? RunUser = null);

/// <summary>
/// The client programs of MySQL, MariaDB and PostgreSQL, run without a window. The dump parameters are those of
/// custos-helper (section 3.3 of CUSTOS-remote-pull.md). The password goes to the program through an environment
/// variable (<c>MYSQL_PWD</c>, <c>PGPASSWORD</c>), never on its command line; nothing is ever typed in, so a program
/// that would ask for a password fails instead of waiting.
/// </summary>
/// <param name="local">A server of this computer reached without a password (Linux), or null for host and port.</param>
public sealed class ProcessDbClient(TaskDatabaseServer server, DbAccount? account, Encoding? consoleEncoding = null, DbLocalAccess? local = null) : IDbClient
{
    /// <summary>Longest a connection check or the list of users may take.</summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);

    private const int ConnectTimeoutSeconds = 15;

    private readonly TaskDatabaseServer _server = server ?? throw new ArgumentNullException(nameof(server));

    private bool IsPostgres => _server.Engine == DbEngines.PostgreSql;

    private int Port => _server.Port ?? DbEngines.DefaultPort(_server.Engine);

    public DbServerInfo Inspect(CancellationToken cancellationToken)
    {
        string tool = QueryTool();
        if (IsPostgres)
        {
            // One query: the first record is the version and the data folder (hidden from accounts that may not see
            // it), then one record per database. Records end with NUL, fields are separated by U+001F.
            const string query = "SELECT 'v' || chr(31) || current_setting('server_version') || chr(31) || "
                + "coalesce((SELECT setting FROM pg_settings WHERE name = 'data_directory'), '') "
                + "UNION ALL (SELECT 'd' || chr(31) || datname FROM pg_database WHERE datallowconn AND NOT datistemplate ORDER BY datname)";
            var output = Query(tool, [.. PostgresConnection("postgres"), "-X", "-A", "-t", "-q", "-0", "-c", query], cancellationToken);
            string version = string.Empty;
            string? dataDir = null;
            var databases = new List<string>();
            foreach (string record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = record.TrimStart('\n', '\r').Split('\u001f');
                if (fields[0] == "v" && fields.Length >= 3)
                {
                    version = fields[1];
                    dataDir = fields[2].Length > 0 ? fields[2] : null;
                }
                else if (fields[0] == "d" && fields.Length >= 2)
                {
                    databases.Add(fields[1]);
                }
            }

            return new DbServerInfo(version, databases, dataDir);
        }
        else
        {
            // The first line: version and data folder; then one database per line (batch mode escapes \t \n \\).
            string output = Query(tool, [.. MySqlConnection(), $"--connect-timeout={ConnectTimeoutSeconds}", "--batch", "--skip-column-names",
                "-e", "SELECT VERSION(), @@datadir; SHOW DATABASES"], cancellationToken);
            string[] lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                throw new DbException(Messages.Format(Messages.DbNoAnswer, Path.GetFileName(tool)));
            }

            string[] first = lines[0].Split('\t');
            string version = Unescape(first[0]);
            string? dataDir = first.Length > 1 && first[1] != "NULL" ? Unescape(first[1]) : null;
            return new DbServerInfo(version, [.. lines.Skip(1).Select(Unescape)], dataDir);
        }
    }

    public IDbDump StartDump(string? database, string version, CancellationToken cancellationToken)
    {
        if (database is not null && database.StartsWith('-'))
        {
            // It would be read as an option; custos-helper refuses such names too.
            throw new DbException(Messages.Format(Messages.DbNameUnsupported, database));
        }

        if (IsPostgres)
        {
            if (database is null)
            {
                string all = DbTools.DumpAllTool(_server.BinDir) ?? throw Missing("pg_dumpall");
                return Launch(all, [.. PostgresConnection(null), "--globals-only"], Environment(), cancellationToken);
            }

            string pgDump = DbTools.DumpTool(_server.BinDir, _server.Engine) ?? throw Missing("pg_dump");
            return Launch(pgDump, [.. PostgresConnection(null), database], Environment(), cancellationToken);
        }

        // A MariaDB set up as MySQL still has --system=users (and roles SHOW CREATE USER does not take).
        if (database is null && _server.Engine == DbEngines.MySql && !IsMariaDb(version))
        {
            return MySqlUsers(version, cancellationToken);
        }

        string dump = DbTools.DumpTool(_server.BinDir, _server.Engine) ?? throw Missing(_server.Engine == DbEngines.MariaDb ? "mariadb-dump" : "mysqldump");
        List<string> arguments = database is null
            ? [.. MySqlConnection(), "--comments", "--system=users", "--insert-ignore"]
            : [.. MySqlConnection(), "--single-transaction", "--quick", "--routines", "--triggers", "--events", "--hex-blob", "--comments", database];
        return Launch(dump, arguments, Environment(), cancellationToken);
    }

    /// <summary>
    /// MySQL has no <c>--system=users</c>: SHOW CREATE USER and SHOW GRANTS of every account but mysql.*, written as
    /// <c>CREATE USER IF NOT EXISTS …</c>, the same as custos-helper does. The list is small, so it is made in memory.
    /// </summary>
    private InMemoryDump MySqlUsers(string version, CancellationToken cancellationToken)
    {
        string tool = QueryTool();
        string accounts = Query(tool, [.. MySqlConnection(), "--batch", "--skip-column-names", "--raw",
            "-e", "SELECT CONCAT(QUOTE(user), '@', QUOTE(host)) FROM mysql.user WHERE user NOT LIKE 'mysql.%' ORDER BY user, host"], cancellationToken);
        var script = new StringBuilder();
        if (AtLeast(version, 8, 0, 17))
        {
            // Password hashes as hex, so that binary ones survive the text file.
            script.Append("SET SESSION print_identified_with_as_hex = ON;");
        }

        foreach (string line in accounts.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            script.Append(CultureInfo.InvariantCulture, $" SHOW CREATE USER {line}; SHOW GRANTS FOR {line};");
        }

        var text = new StringBuilder();
        text.Append("-- CUSTOS ").Append(CustosInfo.Version).Append(": MySQL accounts and grants\n");
        if (script.Length > 0 && script.ToString().Contains("SHOW", StringComparison.Ordinal))
        {
            string statements = Query(tool, [.. MySqlConnection(), "--batch", "--skip-column-names", "--raw", "-e", script.ToString()], cancellationToken);
            foreach (string line in statements.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string statement = line.StartsWith("CREATE USER ", StringComparison.Ordinal) ? "CREATE USER IF NOT EXISTS " + line["CREATE USER ".Length..] : line;
                text.Append(statement).Append(";\n");
            }
        }

        text.Append("-- Dump completed on ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
        return new InMemoryDump(Encoding.UTF8.GetBytes(text.ToString()));
    }

    /// <summary>Checks that the folder holds every program the dumps need, not only the one that lists databases.</summary>
    /// <exception cref="DbException">A program is missing.</exception>
    public void CheckTools()
    {
        QueryTool();
        if (DbTools.DumpTool(_server.BinDir, _server.Engine) is null)
        {
            throw Missing(IsPostgres ? "pg_dump" : _server.Engine == DbEngines.MariaDb ? "mariadb-dump" : "mysqldump");
        }

        if (IsPostgres && DbTools.DumpAllTool(_server.BinDir) is null)
        {
            throw Missing("pg_dumpall");
        }
    }

    private static bool IsMariaDb(string version) => version.Contains("MariaDB", StringComparison.OrdinalIgnoreCase);

    private string QueryTool() => DbTools.QueryTool(_server.BinDir, _server.Engine)
        ?? throw Missing(IsPostgres ? "psql" : _server.Engine == DbEngines.MariaDb ? "mariadb" : "mysql");

    private DbException Missing(string tool) =>
        new(Messages.Format(Messages.DbToolMissing, DbTools.ExeName(tool), _server.BinDir.Length == 0 ? "?" : _server.BinDir, DbTools.NeededTools(_server.Engine)));

    /// <summary>Starts a program of the server; with <see cref="DbLocalAccess.RunAs"/> as that system account (runuser).</summary>
    private ProcessDump Launch(string tool, List<string> arguments, Dictionary<string, string?> environment, CancellationToken cancellationToken) =>
        local is { RunAs: { } user, RunUser: { } runuser }
            ? ProcessDump.Start(runuser, ["-u", user, "--", tool, .. arguments], environment, consoleEncoding, cancellationToken)
            : ProcessDump.Start(tool, arguments, environment, consoleEncoding, cancellationToken);

    private List<string> MySqlConnection()
    {
        // "localhost" makes the programs of MySQL and MariaDB use the socket of the server.
        List<string> arguments = local is null ? [$"--host={_server.Host}", $"--port={Port.ToString(CultureInfo.InvariantCulture)}"]
            : local.Socket is { } socket ? ["--host=localhost", $"--socket={socket}"]
            : ["--host=localhost"];
        if (account is { User.Length: > 0 })
        {
            arguments.Add($"--user={account.User}");
        }

        arguments.Add("--default-character-set=utf8mb4");
        return arguments;
    }

    private List<string> PostgresConnection(string? database)
    {
        // A folder as the host is where PostgreSQL keeps its socket.
        var arguments = new List<string> { "--no-password", $"--host={local?.Socket ?? _server.Host}", $"--port={Port.ToString(CultureInfo.InvariantCulture)}" };
        if (account is { User.Length: > 0 })
        {
            arguments.Add($"--username={account.User}");
        }

        if (database is not null)
        {
            arguments.Add($"--dbname={database}");
        }

        return arguments;
    }

    private Dictionary<string, string?> Environment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            // Never one of the service's own: only the password of this server.
            ["MYSQL_PWD"] = null,
            ["PGPASSWORD"] = null,
            ["PGPASSFILE"] = null,
        };
        string? password = account?.Password is { Length: > 0 } secret ? secret : null;
        if (IsPostgres)
        {
            environment["PGPASSWORD"] = password;
            environment["PGCONNECT_TIMEOUT"] = ConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            environment["MYSQL_PWD"] = password;
        }

        return environment;
    }

    /// <summary>Runs a query program to its end and returns what it printed (UTF-8).</summary>
    private string Query(string tool, List<string> arguments, CancellationToken cancellationToken)
    {
        var environment = Environment();
        if (IsPostgres)
        {
            environment["PGCLIENTENCODING"] = "UTF8";
        }

        using var dump = Launch(tool, arguments, environment, cancellationToken);
        using var buffer = new MemoryStream();
        var copy = dump.Output.CopyToAsync(buffer, cancellationToken);
        try
        {
            if (!copy.Wait(QueryTimeout, cancellationToken))
            {
                throw new DbException(Messages.Format(Messages.DbTimedOut, Path.GetFileName(tool), (int)QueryTimeout.TotalSeconds));
            }
        }
        catch (AggregateException ex) when (ex.InnerException is IOException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new DbException(ex.InnerException.Message, ex.InnerException);
        }

        var exit = dump.WaitForExit();
        if (exit.Code != 0)
        {
            throw new DbException(Messages.Format(Messages.DbProgramFailed, Path.GetFileName(tool), exit.Code, Brief(exit.Errors)));
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>The stderr of a program in one line for the log.</summary>
    public static string Brief(string errors)
    {
        string text = string.Join(" ", (errors ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length == 0 ? Messages.DbNoDetails : text.Length > 500 ? text[..500] + "…" : text;
    }

    /// <summary>Undoes the escapes of the batch mode of mysql: \t \n \\ \0.</summary>
    internal static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                char next = value[++i];
                builder.Append(next switch { 't' => '\t', 'n' => '\n', 'r' => '\r', '0' => '\0', _ => next });
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>Whether a version text ("8.0.36", "5.7.44-log", "11.4.2-MariaDB") is at least a.b.c.</summary>
    internal static bool AtLeast(string version, int major, int minor, int patch)
    {
        var parts = new List<int>();
        foreach (string part in (version ?? string.Empty).Split('.', 4))
        {
            int length = 0;
            while (length < part.Length && char.IsAsciiDigit(part[length]))
            {
                length++;
            }

            if (length == 0 || !int.TryParse(part.AsSpan(0, length), NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            {
                break;
            }

            parts.Add(number);
            if (length < part.Length)
            {
                break;
            }
        }

        if (parts.Count == 0)
        {
            return false;
        }

        while (parts.Count < 3)
        {
            parts.Add(0);
        }

        return (parts[0], parts[1], parts[2]).CompareTo((major, minor, patch)) >= 0;
    }

    /// <summary>A dump made in memory (the users of MySQL).</summary>
    private sealed class InMemoryDump(byte[] data) : IDbDump
    {
        public Stream Output { get; } = new MemoryStream(data, writable: false);

        public DbDumpExit WaitForExit() => new(0, string.Empty);

        public void Dispose() => Output.Dispose();
    }
}

/// <summary>A program whose output is read as a stream; stderr is kept (the last lines) for the log.</summary>
internal sealed class ProcessDump : IDbDump
{
    private const int KeptErrorChars = 8 * 1024;

    private readonly Process _process;
    private readonly StringBuilder _errors = new();
    private readonly Lock _sync = new();
    private readonly CancellationTokenRegistration _cancel;
    private bool _exited;

    private ProcessDump(Process process, CancellationToken cancellationToken)
    {
        _process = process;
        _cancel = cancellationToken.Register(Kill);
    }

    public Stream Output => _process.StandardOutput.BaseStream;

    public static ProcessDump Start(string program, List<string> arguments, Dictionary<string, string?> environment, Encoding? errorEncoding, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardErrorEncoding = errorEncoding,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (Path.GetDirectoryName(program) is { Length: > 0 } folder)
        {
            start.WorkingDirectory = folder;
        }

        foreach (var (name, value) in environment)
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }

        var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            process.Dispose();
            throw new DbException(Messages.Format(Messages.DbProgramNotStarted, program, ex.Message), ex);
        }

        var dump = new ProcessDump(process, cancellationToken);
        process.ErrorDataReceived += (_, e) => dump.AddError(e.Data);
        process.BeginErrorReadLine();

        // Nothing is ever typed in: a program that asks for a password gets the end of its input at once.
        process.StandardInput.Close();
        return dump;
    }

    public DbDumpExit WaitForExit()
    {
        _process.WaitForExit();
        _exited = true;
        lock (_sync)
        {
            return new DbDumpExit(_process.ExitCode, _errors.ToString());
        }
    }

    public void Dispose()
    {
        _cancel.Dispose();
        if (!_exited)
        {
            Kill();
        }

        _process.Dispose();
    }

    private void AddError(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_sync)
        {
            _errors.Append(line).Append('\n');
            if (_errors.Length > KeptErrorChars)
            {
                _errors.Remove(0, _errors.Length - KeptErrorChars);
            }
        }
    }

    private void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // It ended on its own meanwhile.
        }
    }
}
