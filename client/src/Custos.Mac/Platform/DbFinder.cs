using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Custos.Core.Databases;
using Custos.Core.Ipc;
using Custos.Core.Model;

namespace Custos.Service.Platform;

/// <summary>
/// "Find" in the database server dialog on macOS (section 6.2): the running servers (mysqld, mariadbd, postgres) with
/// the port they listen on (from lsof), then the folders of programs of Homebrew, MAMP, Postgres.app and the
/// installers of MySQL and PostgreSQL.
/// </summary>
internal static partial class DbFinder
{
    private static readonly (string Process, string Engine)[] ServerProcesses =
    [
        ("mysqld", DbEngines.MySql),
        ("mariadbd", DbEngines.MariaDb),
        ("postgres", DbEngines.PostgreSql),
    ];

    /// <summary>Where the programs usually are; each is searched a few levels down (like Program Files on Windows).</summary>
    private static readonly string[] Roots =
    [
        "/opt/homebrew/opt", "/opt/homebrew/Cellar", "/usr/local/opt", "/usr/local/Cellar", "/usr/local",
        "/Applications/MAMP/Library", "/Applications/Postgres.app/Contents/Versions", "/Library/PostgreSQL", "/opt/local",
    ];

    public static DbFindResult Find(CancellationToken cancellationToken = default)
    {
        var result = new DbFindResult();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var runningFolders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, engine) in ServerProcesses)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? folder = ProgramFolder(process);
                    if (folder is null)
                    {
                        continue;
                    }

                    // PostgreSQL runs many postgres processes; only the first of them listens. MySQL 8 also listens on
                    // 33060 (X protocol): the lowest port is the one for mysqldump.
                    var (host, port) = Listening(process.Id);
                    if (port is null && engine == DbEngines.PostgreSql)
                    {
                        continue;
                    }

                    var described = DbServerFinder.Describe(folder);
                    int actualPort = port ?? DbEngines.DefaultPort(engine);
                    string actualHost = host ?? "127.0.0.1";
                    if (!seen.Add(folder + "|" + actualHost + "|" + actualPort))
                    {
                        continue;
                    }

                    runningFolders.Add(folder);
                    result.Servers.Add(new DbCandidate
                    {
                        Engine = described?.Engine ?? engine,
                        BinDir = folder,
                        Version = described?.Version ?? DbServerFinder.FolderVersion(folder),
                        Host = actualHost,
                        Port = actualPort,
                        Running = true,
                    });
                }
            }
        }

        foreach (var folder in DbServerFinder.Scan(Roots.Where(Directory.Exists), cancellationToken))
        {
            if (runningFolders.Add(folder.BinDir))
            {
                result.Servers.Add(new DbCandidate
                {
                    Engine = folder.Engine,
                    BinDir = folder.BinDir,
                    Version = folder.Version,
                    Port = DbEngines.DefaultPort(folder.Engine),
                });
            }
        }

        return result;
    }

    private static string? ProgramFolder(Process process)
    {
        try
        {
            return process.MainModule?.FileName is { } path ? Path.GetDirectoryName(path) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The address and lowest TCP port a process listens on, from <c>lsof</c>; nulls if none.</summary>
    private static (string? Host, int? Port) Listening(int pid)
    {
        try
        {
            var (code, output) = Tool.Run("/usr/sbin/lsof", ["-nP", "-a", "-p", pid.ToString(CultureInfo.InvariantCulture), "-iTCP", "-sTCP:LISTEN", "-Fn"], TimeSpan.FromSeconds(10));
            if (code != 0)
            {
                return (null, null);
            }

            (string? Host, int Port)? best = null;
            foreach (string line in output.Split('\n'))
            {
                // "n127.0.0.1:3306", "n*:5432", "n[::1]:5432"
                var match = ListenLine().Match(line.Trim());
                if (match.Success && int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
                    && (best is null || port < best.Value.Port))
                {
                    string address = match.Groups[1].Value.Trim('[', ']');
                    best = (address is "*" or "0.0.0.0" or "::" ? "127.0.0.1" : address, port);
                }
            }

            return best is { } found ? (found.Host, found.Port) : (null, null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (null, null);
        }
    }

    [GeneratedRegex(@"^n(.+):(\d+)$")]
    private static partial Regex ListenLine();
}
