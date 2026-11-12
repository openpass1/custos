using System.Diagnostics;

namespace Custos.Core.Destinations;

/// <summary>
/// Limits the speed of writing to a network destination (section 6.3). One limiter serves every file of the
/// destination, so the limit holds for the destination as a whole.
/// </summary>
public sealed class SpeedLimiter
{
    private readonly long _bytesPerSecond;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _sync = new();
    private double _allowance;
    private TimeSpan _last;

    public SpeedLimiter(long bytesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);
        _bytesPerSecond = bytesPerSecond;
        _allowance = Slice;
    }

    /// <summary>The limit.</summary>
    public long BytesPerSecond => _bytesPerSecond;

    /// <summary>The largest piece to let through at once: a tenth of a second of data, so the flow is even.</summary>
    public int Slice => (int)Math.Clamp(_bytesPerSecond / 10, 1024, 1024 * 1024);

    /// <summary>A limiter for a limit in KB/s; null for "no limit".</summary>
    public static SpeedLimiter? ForKbps(int kbps) => kbps > 0 ? new SpeedLimiter(kbps * 1024L) : null;

    /// <summary>Waits until <paramref name="count"/> bytes (at most <see cref="Slice"/>) may pass.</summary>
    public void Take(int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_sync)
            {
                var now = _clock.Elapsed;
                _allowance = Math.Min(Slice * 2.0, _allowance + ((now - _last).TotalSeconds * _bytesPerSecond));
                _last = now;
                if (_allowance >= count)
                {
                    _allowance -= count;
                    return;
                }

                wait = TimeSpan.FromSeconds((count - _allowance) / _bytesPerSecond);
            }

            if (cancellationToken.WaitHandle.WaitOne(wait))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }
}

/// <summary>A stream whose writes (and reads) pass a <see cref="SpeedLimiter"/>. Everything else goes straight through.</summary>
public sealed class ThrottledStream(Stream inner, SpeedLimiter limiter, CancellationToken cancellationToken = default) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            int count = Math.Min(buffer.Length, limiter.Slice);
            limiter.Take(count, cancellationToken);
            inner.Write(buffer[..count]);
            buffer = buffer[count..];
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int read = inner.Read(buffer[..Math.Min(buffer.Length, limiter.Slice)]);
        if (read > 0)
        {
            limiter.Take(read, cancellationToken);
        }

        return read;
    }

    public override void Flush() => inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Hands bytes from the thread that writes a file to the thread that uploads it (FTP), a megabyte at a time: the
/// writer waits while the buffer is full, the reader while it is empty. A failure on either side ends the other.
/// </summary>
internal sealed class BlockingPipe
{
    private readonly byte[] _buffer;
    private readonly object _gate = new();
    private int _start;
    private int _count;
    private bool _completed;
    private Exception? _readerFailure;
    private bool _aborted;

    public BlockingPipe(int capacity = 1024 * 1024)
    {
        _buffer = new byte[capacity];
        Reader = new ReadSide(this);
        Writer = new WriteSide(this);
    }

    /// <summary>What the uploading thread reads; ends when the writer completes.</summary>
    public Stream Reader { get; }

    /// <summary>What the file is written into.</summary>
    public Stream Writer { get; }

    /// <summary>Bytes written into the pipe so far.</summary>
    public long Written { get; private set; }

    /// <summary>The writer has no more data: the reader sees the end.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            System.Threading.Monitor.PulseAll(_gate);
        }
    }

    /// <summary>The writer gives up: the reader gets an <see cref="IOException"/>, so the upload stops.</summary>
    public void Abort()
    {
        lock (_gate)
        {
            _aborted = true;
            System.Threading.Monitor.PulseAll(_gate);
        }
    }

    /// <summary>The upload failed: the writer gets this failure on its next write.</summary>
    public void FailReader(Exception failure)
    {
        lock (_gate)
        {
            _readerFailure ??= failure;
            System.Threading.Monitor.PulseAll(_gate);
        }
    }

    private void Write(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            while (data.Length > 0)
            {
                if (_readerFailure is not null)
                {
                    throw new StoreException(_readerFailure.Message, _readerFailure);
                }

                int free = _buffer.Length - _count;
                if (free == 0)
                {
                    System.Threading.Monitor.Wait(_gate, TimeSpan.FromSeconds(1));
                    continue;
                }

                int copied = Math.Min(free, data.Length);
                int end = (_start + _count) % _buffer.Length;
                int first = Math.Min(copied, _buffer.Length - end);
                data[..first].CopyTo(_buffer.AsSpan(end));
                data[first..copied].CopyTo(_buffer);
                _count += copied;
                Written += copied;
                data = data[copied..];
                System.Threading.Monitor.PulseAll(_gate);
            }
        }
    }

    private int Read(Span<byte> target)
    {
        lock (_gate)
        {
            while (true)
            {
                if (_aborted)
                {
                    throw new IOException("The upload was abandoned.");
                }

                if (_count > 0)
                {
                    int copied = Math.Min(_count, target.Length);
                    int first = Math.Min(copied, _buffer.Length - _start);
                    _buffer.AsSpan(_start, first).CopyTo(target);
                    _buffer.AsSpan(0, copied - first).CopyTo(target[first..]);
                    _start = (_start + copied) % _buffer.Length;
                    _count -= copied;
                    System.Threading.Monitor.PulseAll(_gate);
                    return copied;
                }

                if (_completed)
                {
                    return 0;
                }

                System.Threading.Monitor.Wait(_gate, TimeSpan.FromSeconds(1));
            }
        }
    }

    private sealed class ReadSide(BlockingPipe pipe) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => pipe.Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => pipe.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.Run(() => pipe.Read(buffer.AsSpan(offset, count)), cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Run(() => pipe.Read(buffer.Span), cancellationToken));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class WriteSide(BlockingPipe pipe) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => pipe.Written;

        public override long Position
        {
            get => pipe.Written;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => pipe.Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) => pipe.Write(buffer);

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>
/// Reads a remote file that can only be fetched from a given position onwards (FTP <c>REST</c>, HTTP <c>Range</c>) as
/// a stream that can seek. Reading forward uses one download; a jump starts another one. The end of the file is kept
/// in memory, because readers of ZIP archives look for the list of contents there, a few bytes at a time.
/// </summary>
internal sealed class RangeReadStream : Stream
{
    private const int TailSize = 256 * 1024;
    private const int SkipLimit = 256 * 1024;

    private readonly Func<long, Stream> _openAt;
    private readonly long _length;
    private Stream? _inner;
    private long _innerPosition;
    private long _position;
    private byte[]? _tail;

    /// <param name="openAt">Starts a download at the given position; the stream is read forward and disposed.</param>
    public RangeReadStream(long length, Func<long, Stream> openAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _length = length;
        _openAt = openAt ?? throw new ArgumentNullException(nameof(openAt));
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0 || _position >= _length)
        {
            return 0;
        }

        long tailStart = Math.Max(0, _length - TailSize);
        if (_position >= tailStart && (_tail is not null || _inner is null || _innerPosition != _position))
        {
            // The end of the file: fetched once, then served from memory, whatever the order of the reads.
            _tail ??= ReadTail(tailStart);
            int fromTail = (int)Math.Min(buffer.Length, _length - _position);
            _tail.AsSpan((int)(_position - tailStart), fromTail).CopyTo(buffer);
            _position += fromTail;
            return fromTail;
        }

        if (_inner is not null && _position > _innerPosition && _position - _innerPosition <= SkipLimit)
        {
            Skip(_position - _innerPosition);
        }

        if (_inner is null || _innerPosition != _position)
        {
            _inner?.Dispose();
            _inner = null;
            _inner = _openAt(_position);
            _innerPosition = _position;
        }

        int wanted = (int)Math.Min(buffer.Length, _length - _position);
        int read = _inner.Read(buffer[..wanted]);
        if (read == 0)
        {
            throw new EndOfStreamException(Messages.StoreFileShorter);
        }

        _innerPosition += read;
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        ArgumentOutOfRangeException.ThrowIfNegative(position, nameof(offset));
        _position = position;
        return position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner?.Dispose();
            _inner = null;
        }

        base.Dispose(disposing);
    }

    private byte[] ReadTail(long start)
    {
        // One download at a time: FTP has a single data connection.
        _inner?.Dispose();
        _inner = null;
        var tail = new byte[_length - start];
        using var stream = _openAt(start);
        int total = 0;
        while (total < tail.Length)
        {
            int read = stream.Read(tail, total, tail.Length - total);
            if (read == 0)
            {
                throw new EndOfStreamException(Messages.StoreFileShorter);
            }

            total += read;
        }

        return tail;
    }

    private void Skip(long count)
    {
        Span<byte> scratch = stackalloc byte[8192];
        while (count > 0)
        {
            int read = _inner!.Read(scratch[..(int)Math.Min(scratch.Length, count)]);
            if (read == 0)
            {
                throw new EndOfStreamException(Messages.StoreFileShorter);
            }

            count -= read;
            _innerPosition += read;
        }
    }
}

/// <summary>
/// A stream of a client library whose failures become <see cref="IOException"/>: the engine tells a failed
/// destination from a bug by the type of the exception.
/// </summary>
internal sealed class GuardedStream(Stream inner, Func<Exception, Exception?> translate, Action? closed = null) : Stream
{
    private bool _disposed;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => Guard(() => inner.Length);

    public override long Position
    {
        get => Guard(() => inner.Position);
        set => Guard(() => inner.Position = value);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            return inner.Read(buffer, offset, count);
        }
        catch (Exception ex) when (translate(ex) is { } failure)
        {
            throw failure;
        }
    }

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return inner.Read(buffer);
        }
        catch (Exception ex) when (translate(ex) is { } failure)
        {
            throw failure;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        try
        {
            inner.Write(buffer, offset, count);
        }
        catch (Exception ex) when (translate(ex) is { } failure)
        {
            throw failure;
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        try
        {
            inner.Write(buffer);
        }
        catch (Exception ex) when (translate(ex) is { } failure)
        {
            throw failure;
        }
    }

    public override void Flush() => Guard(() =>
    {
        inner.Flush();
        return 0;
    });

    public override long Seek(long offset, SeekOrigin origin) => Guard(() => inner.Seek(offset, origin));

    public override void SetLength(long value) => Guard(() =>
    {
        inner.SetLength(value);
        return 0;
    });

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try
            {
                inner.Dispose();
            }
            catch (Exception ex) when (translate(ex) is { } failure)
            {
                throw failure;
            }
            finally
            {
                closed?.Invoke();
            }
        }

        base.Dispose(disposing);
    }

    private T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (translate(ex) is { } failure)
        {
            throw failure;
        }
    }
}

/// <summary>
/// Lets a file that is uploaded strictly forward be changed at places already written, which 7z does with the first
/// bytes of an archive when it is complete (section 6.5). What is written at the end goes on to the upload; what is
/// written earlier is kept as a patch and applied when the upload is over.
/// </summary>
internal sealed class PatchedWriteStream(Stream forward) : Stream
{
    private static readonly byte[] Zeros = new byte[64 * 1024];
    private long _sent;
    private long _position;
    private long? _length;

    /// <summary>Changes to the part already uploaded, in the order they were made.</summary>
    public List<(long Offset, byte[] Data)> Patches { get; } = [];

    /// <summary>The length the file is cut to at the end, when the writer asked for a shorter one; null otherwise.</summary>
    public long? CutTo => _length is { } length && length < _sent ? length : null;

    public override bool CanRead => false;

    public override bool CanSeek => true;

    public override bool CanWrite => true;

    public override long Length => Math.Max(_sent, _length ?? 0);

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_position < _sent)
        {
            int inside = (int)Math.Min(buffer.Length, _sent - _position);
            Patches.Add((_position, buffer[..inside].ToArray()));
            _position += inside;
            buffer = buffer[inside..];
        }

        if (buffer.Length == 0)
        {
            return;
        }

        Fill(_position);
        forward.Write(buffer);
        _sent += buffer.Length;
        _position = _sent;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => Length + offset,
        };
        ArgumentOutOfRangeException.ThrowIfNegative(position, nameof(offset));
        _position = position;
        return position;
    }

    public override void SetLength(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        _length = value;
    }

    public override void Flush() => forward.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>Sends what a longer length asked for still lacks. Called once, before the upload is completed.</summary>
    public void Finish()
    {
        if (_length is { } length)
        {
            Fill(length);
        }
    }

    /// <summary>Zeros up to a position past the end: a file has no holes on a server.</summary>
    private void Fill(long position)
    {
        while (_sent < position)
        {
            int count = (int)Math.Min(Zeros.Length, position - _sent);
            forward.Write(Zeros, 0, count);
            _sent += count;
        }
    }
}
