namespace Custos.Core.Archives;

/// <summary>
/// One output written to several destinations at once (section 6.3): the archive is packed once and every byte goes
/// to all of them. A destination that fails is dropped and reported; the others go on. Seeking moves all of them,
/// which is what 7z needs to write its header position at the end.
/// </summary>
public sealed class MultiTargetStream : Stream
{
    private readonly List<StreamTarget> _targets;
    private readonly Action<StreamTarget, Exception> _onFailure;
    private readonly bool _canSeek;
    private long _position;
    private long _length;

    public MultiTargetStream(IEnumerable<StreamTarget> targets, Action<StreamTarget, Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(targets);
        _targets = [.. targets];
        _onFailure = onFailure ?? throw new ArgumentNullException(nameof(onFailure));
        _canSeek = _targets.All(t => t.Stream.CanSeek);
    }

    /// <summary>Destinations still being written.</summary>
    public IReadOnlyList<StreamTarget> Active => [.. _targets.Where(t => !t.Failed)];

    public override bool CanRead => false;

    public override bool CanSeek => _canSeek;

    public override bool CanWrite => true;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        foreach (var target in _targets)
        {
            if (target.Failed)
            {
                continue;
            }

            try
            {
                target.Stream.Write(buffer);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fail(target, ex);
            }
        }

        _position += buffer.Length;
        _length = Math.Max(_length, _position);
        ThrowIfNoneLeft();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (!_canSeek)
        {
            throw new NotSupportedException();
        }

        long position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        ArgumentOutOfRangeException.ThrowIfNegative(position, nameof(offset));
        foreach (var target in _targets.Where(t => !t.Failed))
        {
            try
            {
                target.Stream.Seek(position, SeekOrigin.Begin);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fail(target, ex);
            }
        }

        ThrowIfNoneLeft();
        _position = position;
        return position;
    }

    public override void SetLength(long value)
    {
        foreach (var target in _targets.Where(t => !t.Failed))
        {
            try
            {
                target.Stream.SetLength(value);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fail(target, ex);
            }
        }

        ThrowIfNoneLeft();
        _length = value;
    }

    public override void Flush()
    {
        foreach (var target in _targets.Where(t => !t.Failed))
        {
            try
            {
                target.Stream.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fail(target, ex);
            }
        }

        ThrowIfNoneLeft();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void Fail(StreamTarget target, Exception ex)
    {
        target.Failed = true;
        target.Error = ex;
        _onFailure(target, ex);
    }

    private void ThrowIfNoneLeft()
    {
        if (_targets.All(t => t.Failed))
        {
            throw new AllTargetsFailedException(_targets.FirstOrDefault()?.Error);
        }
    }
}

/// <summary>A destination of a <see cref="MultiTargetStream"/>.</summary>
public sealed class StreamTarget(object owner, Stream stream)
{
    /// <summary>Whatever the caller uses to tell its destinations apart.</summary>
    public object Owner { get; } = owner;

    public Stream Stream { get; } = stream;

    public bool Failed { get; internal set; }

    public Exception? Error { get; internal set; }
}

/// <summary>Every destination of a <see cref="MultiTargetStream"/> has failed; there is no point packing further.</summary>
public sealed class AllTargetsFailedException : IOException
{
    public AllTargetsFailedException()
    {
    }

    public AllTargetsFailedException(string message)
        : base(message)
    {
    }

    public AllTargetsFailedException(Exception? inner)
        : base(inner?.Message ?? "No destination is left.", inner)
    {
    }

    public AllTargetsFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Splits the output into volumes of a fixed size (section 6.5): <c>name.7z.001</c>, <c>.002</c> … like 7-Zip does.
/// The parts are plain pieces of one stream, so 7-Zip opens them from the first one. Seeking back (7z rewrites its
/// start header at the end) reopens an earlier part.
/// </summary>
public sealed class VolumeStream : Stream
{
    private readonly long _volumeSize;
    private readonly Func<int, Stream> _createPart;
    private readonly Func<int, Stream> _reopenPart;
    private readonly List<long> _partLengths = [];
    private Stream? _open;
    private int _openIndex = -1;
    private long _position;
    private long _length;

    /// <param name="volumeSize">Bytes per volume.</param>
    /// <param name="createPart">Creates part number n (1, 2, …) for writing.</param>
    /// <param name="reopenPart">Opens an existing part n for writing again.</param>
    public VolumeStream(long volumeSize, Func<int, Stream> createPart, Func<int, Stream> reopenPart)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(volumeSize, 1024L * 1024, nameof(volumeSize));
        _volumeSize = volumeSize;
        _createPart = createPart ?? throw new ArgumentNullException(nameof(createPart));
        _reopenPart = reopenPart ?? throw new ArgumentNullException(nameof(reopenPart));
    }

    /// <summary>Parts written so far.</summary>
    public int PartCount => _partLengths.Count;

    public override bool CanRead => false;

    public override bool CanSeek => true;

    public override bool CanWrite => true;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            int part = (int)(_position / _volumeSize);
            long inPart = _position % _volumeSize;
            var stream = Part(part);
            if (stream.Position != inPart)
            {
                stream.Seek(inPart, SeekOrigin.Begin);
            }

            int count = (int)Math.Min(buffer.Length, _volumeSize - inPart);
            stream.Write(buffer[..count]);
            buffer = buffer[count..];
            _position += count;
            _partLengths[part] = Math.Max(_partLengths[part], inPart + count);
            _length = Math.Max(_length, _position);
        }
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

    public override void SetLength(long value)
    {
        if (value < _length)
        {
            throw new NotSupportedException("A split archive cannot be made shorter.");
        }

        if (value > _length)
        {
            long keep = _position;
            _position = value - 1;
            Write([0]);
            _position = keep;
        }
    }

    public override void Flush() => _open?.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _open?.Dispose();
            _open = null;
        }

        base.Dispose(disposing);
    }

    private Stream Part(int index)
    {
        if (_openIndex == index && _open is not null)
        {
            return _open;
        }

        _open?.Flush();
        _open?.Dispose();
        _open = null;
        while (_partLengths.Count < index)
        {
            // A gap (seek past the end): the parts in between are created empty and filled later.
            _createPart(_partLengths.Count + 1).Dispose();
            _partLengths.Add(0);
        }

        if (index == _partLengths.Count)
        {
            _open = _createPart(index + 1);
            _partLengths.Add(0);
        }
        else
        {
            _open = _reopenPart(index + 1);
        }

        _openIndex = index;
        return _open;
    }
}

/// <summary>Several files read as one stream: the volumes of a split archive.</summary>
public sealed class ConcatenatedStream : Stream
{
    private readonly Func<int, Stream> _openPart;
    private readonly long[] _starts;
    private readonly long _length;
    private Stream? _open;
    private int _openIndex = -1;
    private long _position;

    /// <param name="partLengths">Length of each part, in order.</param>
    /// <param name="openPart">Opens part i (0-based) for reading.</param>
    public ConcatenatedStream(IReadOnlyList<long> partLengths, Func<int, Stream> openPart)
    {
        ArgumentNullException.ThrowIfNull(partLengths);
        _openPart = openPart ?? throw new ArgumentNullException(nameof(openPart));
        _starts = new long[partLengths.Count + 1];
        for (int i = 0; i < partLengths.Count; i++)
        {
            _starts[i + 1] = _starts[i] + partLengths[i];
        }

        _length = _starts[^1];
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

        int part = Array.BinarySearch(_starts, _position);
        part = part >= 0 ? part : ~part - 1;
        while (part < _starts.Length - 2 && _starts[part + 1] <= _position)
        {
            part++;
        }

        var stream = Part(part);
        long inPart = _position - _starts[part];
        if (stream.Position != inPart)
        {
            stream.Seek(inPart, SeekOrigin.Begin);
        }

        int wanted = (int)Math.Min(buffer.Length, _starts[part + 1] - _position);
        int read = stream.Read(buffer[..wanted]);
        if (read == 0)
        {
            throw new EndOfStreamException("A volume of the archive is shorter than it was.");
        }

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
            _open?.Dispose();
            _open = null;
        }

        base.Dispose(disposing);
    }

    private Stream Part(int index)
    {
        if (_openIndex != index || _open is null)
        {
            _open?.Dispose();
            _open = _openPart(index);
            _openIndex = index;
        }

        return _open;
    }
}
