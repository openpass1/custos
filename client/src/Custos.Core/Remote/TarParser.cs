using System.Buffers;
using System.Globalization;
using System.Text;

namespace Custos.Core.Remote;

/// <summary>A member of a tar stream, from its header (with the pax or GNU long-name records before it).</summary>
public sealed class TarMember
{
    /// <summary>The name in the tar, relative ("var/www/site/index.php"), without a trailing "/".</summary>
    public required byte[] Name { get; init; }

    /// <summary>The type flag: '0' file, '1' hard link, '2' symbolic link, '5' folder…</summary>
    public required char Type { get; init; }

    /// <summary>Bytes of data that follow the header.</summary>
    public required long Size { get; init; }

    public required string Mode { get; init; }

    public required long Uid { get; init; }

    public required long Gid { get; init; }

    public required string User { get; init; }

    public required string Group { get; init; }

    /// <summary>Modified, ticks since 1970.</summary>
    public required long MTimeTicks { get; init; }

    public required byte[] Link { get; init; }

    /// <summary>The absolute path on the server ("/var/www/site/index.php").</summary>
    public byte[] AbsolutePath => [(byte)'/', .. Name];

    /// <summary><see cref="RemotePath.Key"/> of <see cref="AbsolutePath"/>.</summary>
    public string Key => RemotePath.Key(AbsolutePath);

    public bool IsFile => Type is '0' or '\0' or '7';

    public bool IsFolder => Type == '5';
}

/// <summary>What a <see cref="TarParser"/> finds, in stream order.</summary>
public interface ITarSink
{
    /// <summary>A member begins. <paramref name="header"/> holds all its header blocks, extended ones included.</summary>
    void MemberStart(TarMember member, ReadOnlySpan<byte> header);

    /// <summary>Data of the current member (without the padding to 512 bytes).</summary>
    void MemberData(ReadOnlySpan<byte> data);

    /// <summary>The current member ends (its padding was read).</summary>
    void MemberEnd();
}

/// <summary>
/// Follows a tar stream (POSIX pax as custos-helper writes it, ustar, GNU long names) block by block without keeping
/// it: the data passes through, only the headers are read. Nothing is unpacked anywhere (CUSTOS-remote-pull.md 4.6).
/// </summary>
public sealed class TarParser(ITarSink sink)
{
    public const int BlockSize = 512;

    /// <summary>Extended headers larger than this are taken for a broken stream.</summary>
    private const int MaxExtended = 1024 * 1024;

    private readonly ITarSink _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    private readonly byte[] _block = new byte[BlockSize];
    private readonly ArrayBufferWriter<byte> _headers = new();
    private readonly ArrayBufferWriter<byte> _extended = new();
    private int _blockFill;
    private State _state = State.Header;
    private long _remaining;
    private int _padding;
    private char _extendedType;
    private Dictionary<string, byte[]>? _pax;
    private byte[]? _longName;
    private byte[]? _longLink;
    private int _zeroBlocks;

    private enum State
    {
        Header,
        Extended,
        Data,
        Padding,
        Ended,
    }

    /// <summary>The two zero blocks at the end were read.</summary>
    public bool Ended => _state == State.Ended;

    /// <summary>Bytes taken so far.</summary>
    public long Position { get; private set; }

    /// <summary>Takes the next bytes of the stream.</summary>
    /// <exception cref="InvalidDataException">The stream is not a tar or is damaged.</exception>
    public void Write(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            switch (_state)
            {
                case State.Ended:
                    Position += data.Length;
                    return;

                case State.Data:
                {
                    int take = (int)Math.Min(_remaining, data.Length);
                    _sink.MemberData(data[..take]);
                    Advance(ref data, take);
                    _remaining -= take;
                    if (_remaining == 0)
                    {
                        EndData();
                    }

                    break;
                }

                case State.Padding:
                {
                    int take = Math.Min(_padding, data.Length);
                    Advance(ref data, take);
                    _padding -= take;
                    if (_padding == 0)
                    {
                        _state = State.Header;
                        _sink.MemberEnd();
                    }

                    break;
                }

                default:
                {
                    int take = Math.Min(BlockSize - _blockFill, data.Length);
                    data[..take].CopyTo(_block.AsSpan(_blockFill));
                    _blockFill += take;
                    Advance(ref data, take);
                    if (_blockFill == BlockSize)
                    {
                        _blockFill = 0;
                        if (_state == State.Header)
                        {
                            Header();
                        }
                        else
                        {
                            ExtendedBlock();
                        }
                    }

                    break;
                }
            }
        }
    }

    /// <summary>Checks that the stream ended where a tar ends.</summary>
    /// <exception cref="InvalidDataException">It was cut off.</exception>
    public void Complete()
    {
        if (_state != State.Ended)
        {
            throw new InvalidDataException(Messages.Format(Messages.RemoteTarCut, Position));
        }
    }

    private void Advance(ref ReadOnlySpan<byte> data, int count)
    {
        data = data[count..];
        Position += count;
    }

    private void Header()
    {
        if (_block.AsSpan().IndexOfAnyExcept((byte)0) < 0)
        {
            if (++_zeroBlocks == 2)
            {
                _state = State.Ended;
            }

            return;
        }

        _zeroBlocks = 0;
        if (!ChecksumFits(_block))
        {
            throw new InvalidDataException(Messages.Format(Messages.RemoteTarDamaged, Position - BlockSize));
        }

        char type = (char)_block[156];
        long size = Size(_block.AsSpan(124, 12));
        if (type is 'x' or 'g' or 'L' or 'K')
        {
            if (size > MaxExtended)
            {
                throw new InvalidDataException(Messages.Format(Messages.RemoteTarDamaged, Position - BlockSize));
            }

            if (type != 'g')
            {
                _headers.Write(_block.AsSpan());
            }

            _extendedType = type;
            _extended.ResetWrittenCount();
            _remaining = size;
            _state = size > 0 ? State.Extended : State.Header;
            if (size == 0)
            {
                ApplyExtended();
            }

            return;
        }

        _headers.Write(_block.AsSpan());
        var member = BuildMember(type, size);
        _pax = null;
        _longName = null;
        _longLink = null;
        _sink.MemberStart(member, _headers.WrittenSpan);
        _headers.ResetWrittenCount();
        _remaining = member.Size;
        _padding = (int)((BlockSize - (member.Size % BlockSize)) % BlockSize);
        if (_remaining > 0)
        {
            _state = State.Data;
        }
        else
        {
            EndData();
        }
    }

    private void ExtendedBlock()
    {
        if (_extendedType != 'g')
        {
            _headers.Write(_block.AsSpan());
        }

        int take = (int)Math.Min(_remaining, BlockSize);
        _extended.Write(_block.AsSpan(0, take));
        _remaining -= take;
        if (_remaining == 0)
        {
            ApplyExtended();
            _state = State.Header;
        }
    }

    private void EndData()
    {
        if (_padding > 0)
        {
            _state = State.Padding;
        }
        else
        {
            _state = State.Header;
            _sink.MemberEnd();
        }
    }

    private void ApplyExtended()
    {
        byte[] data = _extended.WrittenSpan.ToArray();
        switch (_extendedType)
        {
            case 'L':
                _longName = CString(data);
                break;
            case 'K':
                _longLink = CString(data);
                break;
            case 'x':
                _pax = ParsePax(data);
                break;
        }
    }

    private TarMember BuildMember(char type, long headerSize)
    {
        var block = _block.AsSpan();
        byte[] name;
        if (_pax?.TryGetValue("path", out byte[]? paxPath) == true)
        {
            name = paxPath;
        }
        else if (_longName is not null)
        {
            name = _longName;
        }
        else
        {
            name = CString(block[..100]);
            if (block.Slice(257, 6).SequenceEqual("ustar\0"u8))
            {
                byte[] prefix = CString(block.Slice(345, 155));
                if (prefix.Length > 0)
                {
                    name = [.. prefix, (byte)'/', .. name];
                }
            }
        }

        int length = name.Length;
        while (length > 1 && name[length - 1] == '/')
        {
            length--;
        }

        byte[] link = _pax?.TryGetValue("linkpath", out byte[]? paxLink) == true ? paxLink : _longLink ?? CString(block.Slice(157, 100));
        long size = PaxNumber("size") ?? headerSize;
        if (type is '1' or '2' or '3' or '4' or '5' or '6')
        {
            size = 0;
        }

        return new TarMember
        {
            Name = length == name.Length ? name : name[..length],
            Type = type,
            Size = size,
            Mode = Convert.ToString(Octal(block.Slice(100, 8)) & 0xfff, 8),
            Uid = PaxNumber("uid") ?? Size(block.Slice(108, 8)),
            Gid = PaxNumber("gid") ?? Size(block.Slice(116, 8)),
            User = PaxText("uname") ?? Encoding.UTF8.GetString(CString(block.Slice(265, 32))),
            Group = PaxText("gname") ?? Encoding.UTF8.GetString(CString(block.Slice(297, 32))),
            MTimeTicks = _pax?.TryGetValue("mtime", out byte[]? mtime) == true ? RemotePath.ParseUnixTicks(mtime) : Size(block.Slice(136, 12)) * TimeSpan.TicksPerSecond,
            Link = link,
        };
    }

    private long? PaxNumber(string key) =>
        _pax?.TryGetValue(key, out byte[]? value) == true && long.TryParse(Encoding.ASCII.GetString(value), NumberStyles.None, CultureInfo.InvariantCulture, out long number)
            ? number
            : null;

    private string? PaxText(string key) => _pax?.TryGetValue(key, out byte[]? value) == true ? Encoding.UTF8.GetString(value) : null;

    private static Dictionary<string, byte[]> ParsePax(byte[] data)
    {
        var records = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int position = 0;
        while (position < data.Length)
        {
            int space = Array.IndexOf(data, (byte)' ', position);
            if (space < 0 || !int.TryParse(Encoding.ASCII.GetString(data, position, space - position), NumberStyles.None, CultureInfo.InvariantCulture, out int length)
                || length <= space - position || position + length > data.Length || data[position + length - 1] != '\n')
            {
                throw new InvalidDataException(Messages.Format(Messages.RemoteTarDamaged, -1));
            }

            int equals = Array.IndexOf(data, (byte)'=', space + 1, position + length - space - 1);
            if (equals < 0)
            {
                throw new InvalidDataException(Messages.Format(Messages.RemoteTarDamaged, -1));
            }

            string key = Encoding.UTF8.GetString(data, space + 1, equals - space - 1);
            records[key] = data[(equals + 1)..(position + length - 1)];
            position += length;
        }

        return records;
    }

    private static bool ChecksumFits(byte[] block)
    {
        long expected = Octal(block.AsSpan(148, 8));
        long sum = 0;
        for (int i = 0; i < BlockSize; i++)
        {
            sum += i is >= 148 and < 156 ? ' ' : block[i];
        }

        return sum == expected;
    }

    /// <summary>A number field: octal text, or base-256 when its first byte has the high bit (GNU, large values).</summary>
    private static long Size(ReadOnlySpan<byte> field)
    {
        if ((field[0] & 0x80) != 0)
        {
            long value = field[0] & 0x7f;
            for (int i = 1; i < field.Length; i++)
            {
                value = checked((value << 8) | field[i]);
            }

            return value;
        }

        return Octal(field);
    }

    private static long Octal(ReadOnlySpan<byte> field)
    {
        long value = 0;
        foreach (byte b in field)
        {
            if (b is 0 or (byte)' ')
            {
                if (value != 0)
                {
                    break;
                }

                continue;
            }

            if (b is < (byte)'0' or > (byte)'7')
            {
                throw new InvalidDataException(Messages.Format(Messages.RemoteTarDamaged, -1));
            }

            value = checked((value * 8) + (b - '0'));
        }

        return value;
    }

    private static byte[] CString(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        return (end < 0 ? field : field[..end]).ToArray();
    }
}
