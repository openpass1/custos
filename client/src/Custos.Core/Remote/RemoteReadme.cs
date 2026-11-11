using System.Globalization;
using System.Text;
using Custos.Core.Backup;
using Custos.Core.Databases;
using Custos.Core.Model;

namespace Custos.Core.Remote;

/// <summary>
/// README.txt of a restored set of a Linux server (CUSTOS-remote-pull.md 4.5): how to put the tars and the dumps back
/// on a server, with commands for a root shell there.
/// </summary>
public static class RemoteReadme
{
    /// <summary>Where settings from the tars of /etc are unpacked, instead of over the running server.</summary>
    public const string OldSettingsFolder = "/root/old-etc";

    /// <param name="folder">The folder of the restore: the tars, «db» and README.txt lie in it.</param>
    /// <param name="tars">Each tar: its root on the server ("" for entries outside the roots) and its file name.</param>
    /// <param name="dumps">The restored dumps: where they lie now and whether they are complete.</param>
    public static string Build(SetManifestHeader header, string folder,
        IReadOnlyList<(string Root, string File)> tars,
        IReadOnlyList<(ManifestDatabase Database, string Path, bool Complete)> dumps)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(tars);
        ArgumentNullException.ThrowIfNull(dumps);
        var text = new StringBuilder();
        string server = header.Remote is { } remote
            ? (remote.Hostname.Length > 0 && remote.Hostname != remote.Host ? $"{remote.Host} ({remote.Hostname})" : remote.Host)
            : header.Computer;
        Line(text, Messages.Format(Messages.RemoteReadmeTitle, server, header.SetName,
            header.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        text.Append('\n');
        Lines(text, Messages.RemoteReadmeIntro, string.Empty);

        if (tars.Count > 0)
        {
            text.Append('\n');
            Line(text, Messages.RemoteReadmeFiles);
            foreach (var (root, file) in tars.OrderBy(t => IsSettings(t.Root)).ThenBy(t => t.Root, StringComparer.Ordinal))
            {
                text.Append('\n');
                Line(text, "  " + (root.Length > 0 ? root : Messages.RemoteReadmeOther));
                if (IsSettings(root))
                {
                    Lines(text, root == "/etc" ? Messages.RemoteReadmeWholeEtc : Messages.RemoteReadmeEtc, "    ");
                    Line(text, $"    mkdir -p {OldSettingsFolder} && tar -xpf {Quote(file)} -C {OldSettingsFolder}");
                }
                else
                {
                    Line(text, $"    tar -xpf {Quote(file)} -C /");
                }
            }

            text.Append('\n');
            Lines(text, Messages.RemoteReadmeTarNotes, "  ");
        }

        var complete = dumps.Where(d => d.Complete).ToList();
        if (complete.Count > 0)
        {
            text.Append('\n');
            Line(text, Messages.RemoteReadmeDatabases);
            foreach (var engine in complete.GroupBy(d => d.Database.Engine).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                text.Append('\n');
                Line(text, "  " + DbEngines.Title(engine.Key));
                bool postgres = engine.Key == DbEngines.PostgreSql;
                string client = engine.Key == DbEngines.MariaDb ? "mariadb" : "mysql";
                foreach (var (database, path, _) in engine.OrderBy(d => d.Database.Name is null ? 0 : 1).ThenBy(d => d.Database.Name, StringComparer.Ordinal))
                {
                    string file = Quote(Relative(folder, path));
                    if (database.Name is null)
                    {
                        Line(text, "    " + (postgres ? Messages.ReadmeUsersPostgres : Messages.ReadmeUsersMySql));
                        Line(text, postgres ? $"      sudo -u postgres psql -d postgres < {file}" : $"      {client} < {file}");
                        continue;
                    }

                    string name = database.Name;
                    Line(text, "    " + Messages.Format(Messages.ReadmeDatabase, name));
                    if (postgres)
                    {
                        if (name != "postgres")
                        {
                            Line(text, $"      sudo -u postgres createdb {Quote(name)}");
                        }

                        Line(text, $"      sudo -u postgres psql -d {Quote(name)} < {file}");
                    }
                    else
                    {
                        Line(text, $"      {client} -e {Quote($"CREATE DATABASE IF NOT EXISTS `{name.Replace("`", "``", StringComparison.Ordinal)}`")}");
                        Line(text, $"      {client} {Quote(name)} < {file}");
                    }
                }

                if (postgres)
                {
                    Line(text, "    " + Messages.ReadmePostgresNote);
                }
                else if (engine.Key == DbEngines.MariaDb)
                {
                    Line(text, "    " + Messages.ReadmeMariaDbNote);
                }
            }
        }

        var incomplete = dumps.Where(d => !d.Complete).ToList();
        var missing = header.Databases.Where(d => d.Ok == false && d.File is null).ToList();
        if (incomplete.Count > 0 || missing.Count > 0)
        {
            text.Append('\n');
            Line(text, Messages.ReadmeProblems);
            foreach (var (database, path, _) in incomplete)
            {
                Line(text, "  " + Messages.Format(Messages.ReadmeIncomplete, Relative(folder, path), database.Error ?? Messages.DumpNoEndLine));
            }

            foreach (var database in missing)
            {
                Line(text, "  " + Messages.Format(Messages.ReadmeMissing, DbEngines.Title(database.Engine), database.Name ?? DbTools.UsersFileStem, database.Error ?? "?"));
            }
        }

        return text.ToString();
    }

    /// <summary>Whether a root holds settings that must not be unpacked over a running server: /etc and anything
    /// below it, and folders named «etc» elsewhere (/usr/local/mgr5/etc).</summary>
    public static bool IsSettings(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return root == "/etc" || root.StartsWith("/etc/", StringComparison.Ordinal)
            || root.EndsWith("/etc", StringComparison.Ordinal) || root.Contains("/etc/", StringComparison.Ordinal);
    }

    /// <summary>Quotes a word for sh if it needs it.</summary>
    public static string Quote(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        return word.Length > 0 && word.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or ':' or '@' or '%' or '+' or '=')
            ? word
            : "'" + word.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static string Relative(string folder, string path) => Path.GetRelativePath(folder, path).Replace('\\', '/');

    // Unix line ends: the file is read on the server.
    private static void Line(StringBuilder text, string line) => text.Append(line).Append('\n');

    private static void Lines(StringBuilder text, string message, string indent)
    {
        foreach (string line in message.Split('\n'))
        {
            Line(text, indent + line.TrimEnd('\r'));
        }
    }
}
