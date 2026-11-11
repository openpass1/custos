using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Custos.Core.Databases;
using Custos.Core.Model;

namespace Custos.Core.Remote;

/// <summary>The answer of <c>info</c> (CUSTOS-remote-pull.md 3.3).</summary>
public sealed class HelperInfo
{
    public string HelperVersion { get; init; } = string.Empty;

    public int Protocol { get; init; }

    public string Hostname { get; init; } = string.Empty;

    public string Os { get; init; } = string.Empty;

    /// <summary>The time of the server, Unix seconds.</summary>
    public long Now { get; init; }

    public string ConfigSha256 { get; init; } = string.Empty;

    public List<string> DbEngines { get; init; } = [];

    public List<HelperRoot> Roots { get; init; } = [];
}

/// <summary>A root of helper.conf and the free space on its disk (null when unknown).</summary>
public sealed record HelperRoot(string Path, long? FreeBytes);

/// <summary>custos-helper refused or failed; the message is for the log.</summary>
public sealed class HelperException : Exception
{
    public HelperException()
    {
    }

    public HelperException(string message)
        : base(message)
    {
    }

    public HelperException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Calls custos-helper over an SSH session. Everything that comes back is untrusted (4.6): the answers are limited in
/// size, JSON is read only with System.Text.Json, and names are checked.
/// </summary>
public sealed partial class HelperClient(ISshSession session, long memoryLimit = HelperClient.DefaultMemoryLimit)
{
    /// <summary>
    /// The memory guard (4.6): SSH.NET takes data from the network even when the archive cannot keep up and keeps it
    /// all in memory. A run stops when more than this waits unread.
    /// </summary>
    public const long DefaultMemoryLimit = 256L * 1024 * 1024;

    public const int MaxInfoBytes = 64 * 1024;

    public const int MaxDatabases = 10_000;

    private readonly ISshSession _session = session ?? throw new ArgumentNullException(nameof(session));

    /// <summary>The key of the server this session was opened with.</summary>
    public string HostKeySha256 => _session.HostKeySha256;

    /// <exception cref="HelperException">custos-helper failed or answered nonsense.</exception>
    /// <exception cref="SshFailedException">The connection broke.</exception>
    public HelperInfo Info(CancellationToken cancellationToken)
    {
        var (output, exit) = Small("info", MaxInfoBytes, cancellationToken);
        Check("info", exit, warningsAllowed: false);
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            var info = new HelperInfo
            {
                HelperVersion = Text(root, "helper_version"),
                Protocol = root.TryGetProperty("protocol", out var protocol) && protocol.ValueKind == JsonValueKind.Number ? protocol.GetInt32() : 0,
                Hostname = Text(root, "hostname"),
                Os = Text(root, "os"),
                Now = root.TryGetProperty("now", out var now) && now.ValueKind == JsonValueKind.Number ? now.GetInt64() : 0,
                ConfigSha256 = Text(root, "config_sha256"),
            };
            if (root.TryGetProperty("db_engines", out var engines) && engines.ValueKind == JsonValueKind.Array)
            {
                info.DbEngines.AddRange(engines.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)
                    .OfType<string>().Where(DbEngines.IsKnown).Distinct(StringComparer.Ordinal));
            }

            if (root.TryGetProperty("roots", out var roots) && roots.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in roots.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object))
                {
                    string path = Text(item, "path");
                    long? free = item.TryGetProperty("free_bytes", out var bytes) && bytes.ValueKind == JsonValueKind.Number ? bytes.GetInt64() : null;
                    if (path.StartsWith('/'))
                    {
                        info.Roots.Add(new HelperRoot(path, free));
                    }
                }
            }

            if (info.Now <= 0 || info.HelperVersion.Length == 0)
            {
                throw new HelperException(Messages.Format(Messages.RemoteBadAnswer, "info", Brief(output)));
            }

            return info;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new HelperException(Messages.Format(Messages.RemoteBadAnswer, "info", Brief(output)), ex);
        }
    }

    /// <summary>The databases of an engine that may be dumped (custos-helper leaves out the system ones itself).</summary>
    public List<string> DbList(string engine, CancellationToken cancellationToken)
    {
        string operation = "db-list " + Word(engine);
        var (output, exit) = Small(operation, MaxDatabases * 66, cancellationToken);
        Check(operation, exit, warningsAllowed: true);
        var names = new List<string>();
        foreach (string line in output.Split('\n'))
        {
            string name = line.TrimEnd('\r');
            if (name.Length == 0)
            {
                continue;
            }

            if (!DatabaseName().IsMatch(name) || names.Count >= MaxDatabases)
            {
                throw new HelperException(Messages.Format(Messages.RemoteBadAnswer, operation, Brief(name)));
            }

            names.Add(name);
        }

        return names;
    }

    /// <summary>Everything in the roots. Exit code 1 (a warning) is allowed: its messages come back in the result.</summary>
    public (List<RemoteEntry> Entries, string Warnings) Manifest(CancellationToken cancellationToken)
    {
        using var stream = Start("manifest", cancellationToken);
        List<RemoteEntry> entries;
        try
        {
            entries = RemoteListing.Read(stream, cancellationToken);
            // Whatever follows the gzip data (nothing, normally) is read away, so that the exit code comes.
            stream.CopyTo(Stream.Null);
        }
        catch (InvalidDataException ex)
        {
            throw new HelperException(Messages.Format(Messages.RemoteBadAnswer, "manifest", ex.Message), ex);
        }

        var exit = stream.WaitForExit();
        Check("manifest", exit, warningsAllowed: true);
        return (entries, exit.Code == 1 ? exit.Errors : string.Empty);
    }

    /// <summary>Starts a command whose output is read as a stream: dumps and the tar of the files.</summary>
    public RemoteStream Start(string command, CancellationToken cancellationToken)
    {
        if (!CommandText().IsMatch(command))
        {
            throw new ArgumentException("Not a custos-helper command: " + command, nameof(command));
        }

        return new RemoteStream(_session.Start(command, cancellationToken), memoryLimit);
    }

    /// <summary>The tar of the folders and of the files changed after <paramref name="since"/> (Unix seconds; 0 = all).</summary>
    public RemoteStream StartTar(long since, int speedLimitKbps, CancellationToken cancellationToken)
    {
        string command = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"tar-since {Math.Max(0, since)}");
        if (speedLimitKbps > 0)
        {
            command += " " + speedLimitKbps.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return Start(command + " gzip", cancellationToken);
    }

    /// <summary>The dumps of one engine as a database client (section 6.2 shared with the dumps on Windows).</summary>
    public IDbClient DbClient(string engine) => new RemoteDbClient(this, Word(engine));

    /// <summary>Throws for an exit code that means failure, with the message of custos-helper.</summary>
    public static void Check(string operation, SshExit exit, bool warningsAllowed)
    {
        if (exit.Code == 0 || (exit.Code == 1 && warningsAllowed))
        {
            return;
        }

        string errors = Brief(exit.Errors);
        throw new HelperException(exit.Code switch
        {
            64 => Messages.Format(Messages.RemoteHelperRefused, operation, errors),
            78 => Messages.Format(Messages.RemoteHelperConfig, errors),
            126 or 127 => Messages.Format(Messages.RemoteHelperMissing, errors),
            _ => Messages.Format(Messages.RemoteHelperFailed, operation, exit.Code, errors),
        });
    }

    /// <summary>The first lines of a message, for the log.</summary>
    public static string Brief(string text)
    {
        var lines = (text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string brief = string.Join(" / ", lines.Take(3));
        if (lines.Length > 3)
        {
            brief += " / …";
        }

        return brief.Length > 500 ? brief[..500] + "…" : brief;
    }

    private (string Output, SshExit Exit) Small(string command, int limit, CancellationToken cancellationToken)
    {
        using var stream = Start(command, cancellationToken);
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new HelperException(Messages.Format(Messages.RemoteAnswerTooLong, command.Split(' ')[0], limit));
            }

            buffer.Write(chunk, 0, read);
        }

        return (Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), stream.WaitForExit());
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static string Word(string engine) =>
        DbEngines.IsKnown(engine) ? engine : throw new ArgumentException("Unknown engine: " + engine, nameof(engine));

    [GeneratedRegex("^[A-Za-z0-9_$-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseName();

    [GeneratedRegex("^[A-Za-z0-9_$ -]+$", RegexOptions.CultureInvariant)]
    private static partial Regex CommandText();
}

/// <summary>
/// The stdout of a command of custos-helper, with the memory guard: reading fails when SSH.NET holds more unread data
/// than allowed. Failures of the connection come as <see cref="IOException"/>.
/// </summary>
public sealed class RemoteStream(ISshCommand command, long memoryLimit) : Stream
{
    private bool _disposed;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Bytes taken from the server so far.</summary>
    public long BytesRead { get; private set; }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        long waiting = command.Buffered;
        if (waiting > memoryLimit)
        {
            throw new IOException(Messages.Format(Messages.RemoteMemoryGuard, Text.Sizes.Format(waiting)));
        }

        int read = command.Output.Read(buffer);
        BytesRead += read;
        return read;
    }

    /// <summary>Waits for the command to end, after the output was read to its end.</summary>
    /// <exception cref="SshFailedException">The connection broke.</exception>
    public SshExit WaitForExit() => command.WaitForExit();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            command.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>The databases of one engine on the server, dumped through custos-helper (always with <c>gzip</c>).</summary>
internal sealed class RemoteDbClient(HelperClient helper, string engine) : IDbClient
{
    public DbServerInfo Inspect(CancellationToken cancellationToken)
    {
        try
        {
            return new DbServerInfo(string.Empty, helper.DbList(engine, cancellationToken), null);
        }
        catch (Exception ex) when (ex is HelperException or SshFailedException or IOException)
        {
            throw new DbException(ex.Message, ex);
        }
    }

    public IDbDump StartDump(string? database, string version, CancellationToken cancellationToken)
    {
        try
        {
            string command = database is null ? $"db-users {engine} gzip" : $"db-dump {engine} {database} gzip";
            return new Dump(helper.Start(command, cancellationToken));
        }
        catch (Exception ex) when (ex is SshFailedException or ArgumentException)
        {
            throw new DbException(ex.Message, ex);
        }
    }

    private sealed class Dump(RemoteStream remote) : IDbDump
    {
        public Stream Output { get; } = new Inflated(remote);

        public DbDumpExit WaitForExit()
        {
            try
            {
                var exit = remote.WaitForExit();
                return new DbDumpExit(exit.Code, exit.Errors);
            }
            catch (SshFailedException ex)
            {
                return new DbDumpExit(-1, ex.Message);
            }
        }

        public void Dispose()
        {
            Output.Dispose();
            remote.Dispose();
        }
    }

    /// <summary>The dump unpacked on the fly; damaged gzip data is a read error like a broken connection.</summary>
    private sealed class Inflated(Stream compressed) : Stream
    {
        private readonly GZipStream _gzip = new(compressed, CompressionMode.Decompress, leaveOpen: true);
        private bool _ended;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_ended)
            {
                return 0;
            }

            try
            {
                int read = _gzip.Read(buffer);
                if (read == 0)
                {
                    _ended = true;
                    // Anything after the gzip data is read away, so that the command can end.
                    compressed.CopyTo(Stream.Null);
                }

                return read;
            }
            catch (InvalidDataException ex)
            {
                throw new IOException(Messages.Format(Messages.RemoteBadAnswer, "gzip", ex.Message), ex);
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _gzip.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
