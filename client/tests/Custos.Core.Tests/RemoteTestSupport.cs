using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Custos.Core.Remote;

namespace Custos.Core.Tests;

/// <summary>An entry of the files of a <see cref="FakeLinuxServer"/>.</summary>
internal sealed class FakeEntry
{
    /// <summary>The letter of <c>find %y</c>: f, d or l.</summary>
    public char Type { get; set; } = 'f';

    public byte[] Content { get; set; } = [];

    /// <summary>Unix seconds.</summary>
    public long MTime { get; set; }

    /// <summary>Unix seconds.</summary>
    public long CTime { get; set; }

    public string Mode { get; set; } = "644";

    public string User { get; set; } = "www-data";

    public string? Link { get; set; }
}

/// <summary>
/// A Linux server with custos-helper, without a network: it answers the operations of custos-helper (info, db-list,
/// db-dump, db-users, manifest, tar-since) from files and databases the test gives, the way the script does.
/// </summary>
internal sealed class FakeLinuxServer : ISshConnector
{
    public string HostKey { get; set; } = "SHA256:" + new string('A', 43);

    /// <summary>The clock of the server, Unix seconds.</summary>
    public long Now { get; set; } = 1_800_000_000;

    public string HelperVersion { get; set; } = HelperScript.Version;

    public int Protocol { get; set; } = HelperScript.Protocol;

    public string ConfigSha256 { get; set; } = new('c', 64);

    public List<string> Roots { get; } = ["/var/www", "/etc/nginx"];

    /// <summary>Every entry by its path (bytes as Latin-1 chars would break names, so the key is the path as bytes in base64 when not UTF-8).</summary>
    public SortedDictionary<string, FakeEntry> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>Databases by engine.</summary>
    public Dictionary<string, List<string>> Databases { get; } = [];

    /// <summary>Dumps that break off (exit code 2, no end line).</summary>
    public HashSet<string> BrokenDumps { get; } = new(StringComparer.Ordinal);

    /// <summary>Connections that fail before one works.</summary>
    public int Unreachable { get; set; }

    /// <summary>Exit code and stderr of tar-since (1 = some files changed while read).</summary>
    public (int Code, string Errors) TarExit { get; set; } = (0, string.Empty);

    /// <summary>tar-since stops after this many bytes of tar (as when the connection breaks); -1 = never.</summary>
    public long TarCutAfter { get; set; } = -1;

    /// <summary>What SSH.NET would hold unread while the tar is read (for the memory guard).</summary>
    public long TarBuffered { get; set; }

    /// <summary>Every command run, in order.</summary>
    public List<string> Commands { get; } = [];

    /// <summary>The pinned key each connection came with.</summary>
    public List<string?> PinnedKeys { get; } = [];

    public void AddFile(string path, string content, long? time = null) => AddFile(Encoding.UTF8.GetBytes(path), content, time);

    public void AddFile(byte[] path, string content, long? time = null)
    {
        long when = time ?? Now;
        AddParents(path, when);
        Files[RemotePath.Key(path)] = new FakeEntry { Content = Encoding.UTF8.GetBytes(content), MTime = when, CTime = when };
    }

    public void AddLink(string path, string target, long? time = null)
    {
        long when = time ?? Now;
        AddParents(Encoding.UTF8.GetBytes(path), when);
        Files[path] = new FakeEntry { Type = 'l', Link = target, MTime = when, CTime = when, Mode = "777" };
    }

    public void Delete(string path) => Files.Remove(path);

    public static string SqlOf(string engine, string name, bool complete = true)
    {
        var text = new StringBuilder();
        text.Append("-- dump of ").Append(engine).Append(' ').Append(name).Append('\n');
        text.Append("CREATE TABLE t (id int);\nINSERT INTO t VALUES (1);\n");
        if (complete)
        {
            text.Append(engine == Model.DbEngines.PostgreSql
                ? (name == "_users" ? "--\n-- PostgreSQL database cluster dump complete\n--\n\n" : "--\n-- PostgreSQL database dump complete\n--\n\n")
                : "-- Dump completed on 2026-11-01 10:00:00\n");
        }

        return text.ToString();
    }

    public ISshSession Connect(SshTarget target, CancellationToken cancellationToken)
    {
        PinnedKeys.Add(target.HostKeySha256);
        if (Unreachable > 0)
        {
            Unreachable--;
            throw new SshFailedException("connection refused");
        }

        if (target.HostKeySha256 != HostKey)
        {
            throw new SshHostKeyException("host key " + HostKey, target.HostKeySha256, HostKey);
        }

        return new Session(this);
    }

    private void AddParents(byte[] path, long time)
    {
        for (int i = path.Length - 1; i > 0; i--)
        {
            if (path[i] != '/')
            {
                continue;
            }

            string parent = RemotePath.Key(path.AsSpan(0, i));
            if (!Files.ContainsKey(parent))
            {
                Files[parent] = new FakeEntry { Type = 'd', MTime = time, CTime = time, Mode = "755" };
            }
        }
    }

    private bool InRoots(string key)
    {
        string path = RemotePath.Display(key);
        return Roots.Any(r => RemotePath.IsUnder(path, r));
    }

    private (byte[] Output, int Code, string Errors) Run(string command)
    {
        Commands.Add(command);
        string[] words = command.Split(' ');
        switch (words[0])
        {
            case "info":
                var info = new
                {
                    helper_version = HelperVersion,
                    protocol = Protocol,
                    hostname = "vps",
                    os = "Debian GNU/Linux 12",
                    now = Now,
                    config_sha256 = ConfigSha256,
                    db_engines = Databases.Keys.ToArray(),
                    roots = Roots.Select(r => new { path = r, free_bytes = 50_000_000_000L }).ToArray(),
                };
                return (JsonSerializer.SerializeToUtf8Bytes(info), 0, string.Empty);

            case "db-list":
                return (Encoding.UTF8.GetBytes(string.Join('\n', Databases.GetValueOrDefault(words[1]) ?? []) + "\n"), 0, string.Empty);

            case "db-dump":
            case "db-users":
            {
                string engine = words[1];
                string name = words[0] == "db-users" ? "_users" : words[2];
                bool broken = BrokenDumps.Contains(name);
                return (Gzip(Encoding.UTF8.GetBytes(SqlOf(engine, name, !broken))), broken ? 2 : 0, broken ? "mariadb-dump: Lost connection" : string.Empty);
            }

            case "manifest":
                return (Gzip(Manifest()), 0, string.Empty);

            case "tar-since":
            {
                byte[] tar = Tar(long.Parse(words[1], CultureInfo.InvariantCulture));
                if (TarCutAfter >= 0 && tar.Length > TarCutAfter)
                {
                    return (Gzip(tar[..(int)TarCutAfter]), 2, "tar: Read error: connection lost");
                }

                return (Gzip(tar), TarExit.Code, TarExit.Errors);
            }

            default:
                return ([], 64, "custos-helper: unknown operation");
        }
    }

    private byte[] Manifest()
    {
        using var output = new MemoryStream();
        foreach (var (key, entry) in Files.Where(f => InRoots(f.Key)))
        {
            void Field(string text) => Bytes(Encoding.UTF8.GetBytes(text));
            void Bytes(byte[] bytes)
            {
                output.Write(bytes);
                output.WriteByte(0);
            }

            Field(entry.Type.ToString());
            Field((entry.Type == 'f' ? entry.Content.Length : 4096).ToString(CultureInfo.InvariantCulture));
            Field(entry.MTime.ToString(CultureInfo.InvariantCulture) + ".5000000000");
            Field(entry.CTime.ToString(CultureInfo.InvariantCulture) + ".5000000000");
            Field(entry.Mode);
            Field("33");
            Field("33");
            Field(entry.User);
            Field(entry.User);
            Field(entry.Link ?? string.Empty);
            Bytes(RemotePath.FromKey(key));
        }

        return output.ToArray();
    }

    /// <summary>A pax tar as custos-helper makes it: relative names, every folder, files changed after
    /// <paramref name="since"/> (by ctime).</summary>
    private byte[] Tar(long since)
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (key, entry) in Files.Where(f => InRoots(f.Key)))
            {
                if (entry.Type != 'd' && since > 0 && entry.CTime <= since)
                {
                    continue;
                }

                // Names that are not UTF-8 cannot be written by TarWriter; the test of them uses the parser directly.
                string name = RemotePath.Display(key)[1..];
                var tarEntry = new PaxTarEntry(entry.Type switch { 'd' => TarEntryType.Directory, 'l' => TarEntryType.SymbolicLink, _ => TarEntryType.RegularFile }, name)
                {
                    ModificationTime = DateTimeOffset.FromUnixTimeSeconds(entry.MTime),
                    Mode = (UnixFileMode)Convert.ToInt32(entry.Mode, 8),
                    Uid = 33,
                    Gid = 33,
                    UserName = entry.User,
                    GroupName = entry.User,
                };
                if (entry.Type == 'l')
                {
                    tarEntry.LinkName = entry.Link!;
                }
                else if (entry.Type == 'f')
                {
                    tarEntry.DataStream = new MemoryStream(entry.Content);
                }

                writer.WriteEntry(tarEntry);
            }
        }

        return output.ToArray();
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    private sealed class Session(FakeLinuxServer server) : ISshSession
    {
        public string HostKeySha256 => server.HostKey;

        public ISshCommand Start(string command, CancellationToken cancellationToken)
        {
            var (output, code, errors) = server.Run(command);
            return new Command(new MemoryStream(output), new SshExit(code, errors), command.StartsWith("tar-since", StringComparison.Ordinal) ? server.TarBuffered : 0);
        }

        public void Dispose()
        {
        }
    }

    private sealed class Command(MemoryStream output, SshExit exit, long buffered) : ISshCommand
    {
        public Stream Output => output;

        public long Buffered => output.Position < output.Length ? buffered : 0;

        public SshExit WaitForExit() => exit;

        public void Dispose() => output.Dispose();
    }
}
