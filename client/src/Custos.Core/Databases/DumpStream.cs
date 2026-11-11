using System.Security.Cryptography;
using System.Text;
using Custos.Core.Model;

namespace Custos.Core.Databases;

/// <summary>
/// When a dump counts as complete (section 4.3 of CUSTOS-remote-pull.md, the same rule as for custos-helper): the
/// program ended with code 0, and the dump ends with its end line. MySQL and MariaDB: the last line starts with
/// <c>-- Dump completed</c>. PostgreSQL: after <c>-- PostgreSQL database dump complete</c> (or <c>… database cluster
/// dump complete</c> for the users) only <c>--</c>, empty lines and one <c>\unrestrict …</c> line may follow.
/// </summary>
public static class DumpCheck
{
    /// <summary>How much of the end of a dump is enough to check it.</summary>
    public const int TailBytes = 4096;

    /// <summary>The line CUSTOS adds to a dump that failed, so that it never looks complete.</summary>
    public const string IncompleteMarker = "-- CUSTOS: THIS DUMP IS INCOMPLETE";

    /// <summary>Whether the end of a dump has its end line.</summary>
    public static bool HasEndLine(string engine, ReadOnlySpan<byte> tail)
    {
        var lines = Encoding.UTF8.GetString(tail).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        if (engine != DbEngines.PostgreSql)
        {
            string? last = lines.LastOrDefault(l => l.Trim().Length > 0);
            return last is not null && last.StartsWith("-- Dump completed", StringComparison.Ordinal);
        }

        int unrestrict = 0;
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line is "-- PostgreSQL database dump complete" or "-- PostgreSQL database cluster dump complete")
            {
                return true;
            }

            if (line.StartsWith(@"\unrestrict ", StringComparison.Ordinal) && unrestrict++ == 0)
            {
                continue;
            }

            if (line.Length > 0 && line != "--")
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Null if the dump is complete, otherwise why not, in words.</summary>
    public static string? Problem(string engine, ReadOnlySpan<byte> tail, DbDumpExit exit)
    {
        if (exit.Code != 0)
        {
            return Messages.Format(Messages.DumpExitCode, exit.Code, ProcessDbClient.Brief(exit.Errors));
        }

        if (!HasEndLine(engine, tail))
        {
            return exit.Errors.Trim().Length > 0
                ? Messages.Format(Messages.DumpNoEndLineWithErrors, ProcessDbClient.Brief(exit.Errors))
                : Messages.DumpNoEndLine;
        }

        return null;
    }
}

/// <summary>
/// The output of a dump program as it goes into a set: counted, hashed (SHA-256), its end kept for the check. At the
/// end the program's exit code and the end line are checked (<see cref="DumpCheck"/>); a dump that failed gets a last
/// line <c>-- CUSTOS: THIS DUMP IS INCOMPLETE …</c>, so that it never looks complete, even inside an archive that was
/// already written when the failure showed. Read by one thread at a time.
/// </summary>
public sealed class DumpStream : Stream
{
    private const int ChunkSize = 256 * 1024;

    private readonly IDbDump _dump;
    private readonly string _engine;
    private readonly Action? _beforeRead;
    private readonly Action<int>? _onRead;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly byte[] _tail = new byte[DumpCheck.TailBytes];
    private int _tailLength;
    private byte[]? _pending;
    private int _pendingOffset;
    private int _pendingLength;
    private bool _sourceEnded;
    private bool _disposed;

    /// <param name="beforeRead">Called before each read from the program (pause, cancellation).</param>
    /// <param name="onRead">Told how many bytes of the dump were read (progress).</param>
    public DumpStream(IDbDump dump, string engine, Action? beforeRead = null, Action<int>? onRead = null)
    {
        _dump = dump ?? throw new ArgumentNullException(nameof(dump));
        _engine = engine;
        _beforeRead = beforeRead;
        _onRead = onRead;
    }

    /// <summary>Bytes of the dump itself, without the line added on failure.</summary>
    public long Bytes { get; private set; }

    /// <summary>The whole output has been read and checked.</summary>
    public bool Finished { get; private set; }

    /// <summary>Why the dump is not complete; null when it is (or before <see cref="Finished"/>).</summary>
    public string? Error { get; private set; }

    /// <summary>SHA-256 of everything this stream gave out; set when <see cref="Finished"/>.</summary>
    public string? Sha256 { get; private set; }

    /// <summary>Called once when the dump is finished and checked.</summary>
    public event EventHandler? Completed;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => Bytes;
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Waits for the first output of the program. False when it ended without any and failed (a server that refused
    /// the login, a database that is gone): then there is nothing worth storing and <see cref="Error"/> says why.
    /// </summary>
    public bool Prefetch()
    {
        var buffer = new byte[ChunkSize];
        int read = ReadSource(buffer);
        if (read > 0)
        {
            _pending = buffer;
            _pendingOffset = 0;
            _pendingLength = read;
            return true;
        }

        return Error is null;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        while (true)
        {
            if (_pending is not null)
            {
                int count = Math.Min(buffer.Length, _pendingLength - _pendingOffset);
                _pending.AsSpan(_pendingOffset, count).CopyTo(buffer);
                _pendingOffset += count;
                if (_pendingOffset >= _pendingLength)
                {
                    _pending = null;
                }

                Hash(buffer[..count]);
                return count;
            }

            if (_sourceEnded)
            {
                Finish();
                return 0;
            }

            int read = ReadSource(buffer);
            if (read > 0)
            {
                Hash(buffer[..read]);
                return read;
            }

            // The end of the program: the line that marks a failure (if any) comes next, then the end.
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
        if (disposing && !_disposed)
        {
            _disposed = true;
            if (!Finished)
            {
                Error ??= Messages.DumpStopped;
            }

            _dump.Dispose();
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Reads from the program; at its end checks it and queues the failure line if needed.</summary>
    private int ReadSource(Span<byte> buffer)
    {
        if (_sourceEnded)
        {
            return 0;
        }

        _beforeRead?.Invoke();
        int read;
        try
        {
            read = _dump.Output.Read(buffer);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _sourceEnded = true;
            Fail(ex.Message);
            return 0;
        }

        if (read > 0)
        {
            Bytes += read;
            KeepTail(buffer[..read]);
            _onRead?.Invoke(read);
            return read;
        }

        _sourceEnded = true;
        var exit = _dump.WaitForExit();
        string? problem = DumpCheck.Problem(_engine, _tail.AsSpan(0, _tailLength), exit);
        if (problem is not null)
        {
            Fail(problem);
        }

        return 0;
    }

    private void Fail(string problem)
    {
        Error = problem;
        string line = "\n" + DumpCheck.IncompleteMarker + ": " + problem.Replace('\r', ' ').Replace('\n', ' ') + "\n";
        _pending = Encoding.UTF8.GetBytes(line);
        _pendingOffset = 0;
        _pendingLength = _pending.Length;
    }

    private void Hash(ReadOnlySpan<byte> data) => _hash.AppendData(data);

    private void KeepTail(ReadOnlySpan<byte> data)
    {
        if (data.Length >= _tail.Length)
        {
            data[^_tail.Length..].CopyTo(_tail);
            _tailLength = _tail.Length;
            return;
        }

        int keep = Math.Min(_tailLength, _tail.Length - data.Length);
        _tail.AsSpan(_tailLength - keep, keep).CopyTo(_tail);
        data.CopyTo(_tail.AsSpan(keep));
        _tailLength = keep + data.Length;
    }

    private void Finish()
    {
        if (Finished)
        {
            return;
        }

        Finished = true;
        Sha256 = Backup.SetManifest.ToHex(_hash.GetHashAndReset());
        Completed?.Invoke(this, EventArgs.Empty);
    }
}
