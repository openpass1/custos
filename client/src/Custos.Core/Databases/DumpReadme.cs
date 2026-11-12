using System.Globalization;
using System.Text;
using Custos.Core.Backup;
using Custos.Core.Model;

namespace Custos.Core.Databases;

/// <summary>
/// The README.txt next to restored dumps (section 6.2): which dumps there are and the commands that load them. CUSTOS
/// never loads a dump into a server itself. The commands are for cmd (Windows PowerShell 5 would spoil the input of
/// "&lt;"), run from the folder that holds the folder «db».
/// </summary>
public static class DumpReadme
{
    /// <param name="folder">The folder that holds «db».</param>
    /// <param name="dumps">The restored dumps: where they lie now and whether they are complete.</param>
    public static string Build(SetManifestHeader header, string folder, IReadOnlyList<(ManifestDatabase Database, string Path, bool Complete)> dumps)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(dumps);
        var text = new StringBuilder();
        text.AppendLine(Messages.Format(Messages.ReadmeTitle, header.SetName,
            header.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), header.Computer));
        text.AppendLine();
        foreach (string line in Messages.ReadmeIntro.Split('\n'))
        {
            text.AppendLine(line.TrimEnd('\r'));
        }

        foreach (var server in dumps.Where(d => d.Complete).GroupBy(d => (d.Database.Engine, d.Database.Server)))
        {
            string engine = server.Key.Engine;
            (string host, string port) = SplitServer(server.Key.Server, engine);
            text.AppendLine();
            text.Append(DbEngines.Title(engine)).Append(' ').AppendLine(server.Key.Server);
            bool postgres = engine == DbEngines.PostgreSql;
            string connect = postgres ? $"-h {host} -p {port} -U postgres" : $"-h {host} -P {port} -u root -p";

            // MariaDB dumps may start with a line only the mariadb client understands (the "sandbox mode").
            string mysql = engine == DbEngines.MariaDb ? "mariadb" : "mysql";
            foreach (var (database, path, _) in server.OrderBy(d => d.Database.Name is null ? 0 : 1).ThenBy(d => d.Database.Name, StringComparer.OrdinalIgnoreCase))
            {
                string file = Quote(Relative(folder, path));
                if (database.Name is null)
                {
                    text.Append("  ").AppendLine(postgres ? Messages.ReadmeUsersPostgres : Messages.ReadmeUsersMySql);
                    text.Append("    ").AppendLine(postgres ? $"psql {connect} -d postgres -f {file}" : $"{mysql} {connect} < {file}");
                    continue;
                }

                text.Append("  ").AppendLine(Messages.Format(Messages.ReadmeDatabase, database.Name));
                if (postgres)
                {
                    if (database.Name != "postgres")
                    {
                        // The database «postgres» is on every server already.
                        text.Append("    ").AppendLine(CultureInfo.InvariantCulture, $"createdb {connect} {Quote(database.Name)}");
                    }

                    text.Append("    ").AppendLine(CultureInfo.InvariantCulture, $"psql {connect} -d {Quote(database.Name)} -f {file}");
                }
                else
                {
                    text.Append("    ").AppendLine(CultureInfo.InvariantCulture, $"{mysql} {connect} -e \"CREATE DATABASE IF NOT EXISTS `{database.Name.Replace("`", "``", StringComparison.Ordinal)}`\"");
                    text.Append("    ").AppendLine(CultureInfo.InvariantCulture, $"{mysql} {connect} {Quote(database.Name)} < {file}");
                }
            }

            if (postgres)
            {
                text.Append("  ").AppendLine(Messages.ReadmePostgresNote);
            }
            else if (engine == DbEngines.MariaDb)
            {
                text.Append("  ").AppendLine(Messages.ReadmeMariaDbNote);
            }
        }

        var incomplete = dumps.Where(d => !d.Complete).ToList();
        var missing = header.Databases.Where(d => d.Ok == false && d.File is null).ToList();
        if (incomplete.Count > 0 || missing.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Messages.ReadmeProblems);
            foreach (var (database, path, _) in incomplete)
            {
                text.Append("  ").AppendLine(Messages.Format(Messages.ReadmeIncomplete, Relative(folder, path), database.Error ?? Messages.DumpNoEndLine));
            }

            foreach (var database in missing)
            {
                text.Append("  ").AppendLine(Messages.Format(Messages.ReadmeMissing, DbEngines.Title(database.Engine), database.Name ?? DbTools.UsersFileStem, database.Error ?? "?"));
            }
        }

        return text.ToString();
    }

    private static (string Host, string Port) SplitServer(string server, string engine)
    {
        int colon = server.LastIndexOf(':');
        return colon > 0
            ? (server[..colon], server[(colon + 1)..])
            : (server.Length > 0 ? server : "127.0.0.1", DbEngines.DefaultPort(engine).ToString(CultureInfo.InvariantCulture));
    }

    private static string Relative(string folder, string path)
    {
        string relative = Path.GetRelativePath(folder, path);
        return relative.Replace('/', '\\');
    }

    /// <summary>Quotes a word for cmd if it needs it.</summary>
    private static string Quote(string word) =>
        word.Length > 0 && word.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '\\' or ':') ? word : "\"" + word + "\"";
}
