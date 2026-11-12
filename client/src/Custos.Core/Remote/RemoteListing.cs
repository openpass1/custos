using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Custos.Core.Remote;

/// <summary>Paths of a Linux server: bytes, not always UTF-8, compared with case.</summary>
public static class RemotePath
{
    private static readonly UTF8Encoding Strict = new(false, throwOnInvalidBytes: true);

    /// <summary>A key that keeps every path apart: the path itself, or for a name that is not UTF-8 "\0" + base64.</summary>
    public static string Key(ReadOnlySpan<byte> path) => TryUtf8(path, out string? text) ? text : "\0" + Convert.ToBase64String(path);

    /// <summary>The <see cref="Key"/> of a file of a set manifest (its <c>path_b64</c> when the name is not UTF-8).</summary>
    public static string Key(Backup.ManifestFile file) => file.PathBase64 is { } base64 ? "\0" + base64 : file.Path;

    /// <summary>The path for people: bytes that are not UTF-8 become U+FFFD.</summary>
    public static string Display(ReadOnlySpan<byte> path) => Encoding.UTF8.GetString(path);

    /// <summary>The path of a key for people.</summary>
    public static string Display(string key) => Display(FromKey(key));

    /// <summary>The bytes of a key made by <see cref="Key"/>.</summary>
    public static byte[] FromKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.StartsWith('\0') ? Convert.FromBase64String(key[1..]) : Encoding.UTF8.GetBytes(key);
    }

    public static bool TryUtf8(ReadOnlySpan<byte> bytes, out string text)
    {
        try
        {
            text = Strict.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or lies below it ("/var/www/a" in "/var/www").</summary>
    public static bool IsUnder(string path, string root)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(root);
        return string.Equals(path, root, StringComparison.Ordinal)
            || (path.Length > root.Length && path.StartsWith(root, StringComparison.Ordinal) && path[root.Length] == '/');
    }

    /// <summary>Unix time in seconds with a fraction ("1731312345.1234567890") → ticks since 1970.</summary>
    public static long ParseUnixTicks(ReadOnlySpan<byte> text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        bool negative = text[0] == '-';
        if (negative)
        {
            text = text[1..];
        }

        int dot = text.IndexOf((byte)'.');
        var whole = dot < 0 ? text : text[..dot];
        long seconds = 0;
        foreach (byte b in whole)
        {
            if (b is < (byte)'0' or > (byte)'9')
            {
                throw new FormatException("Not a time: " + Encoding.ASCII.GetString(text));
            }

            seconds = checked((seconds * 10) + (b - '0'));
        }

        long fraction = 0;
        if (dot >= 0)
        {
            var digits = text[(dot + 1)..];
            for (int i = 0; i < 7; i++)
            {
                int digit = i < digits.Length && digits[i] is >= (byte)'0' and <= (byte)'9' ? digits[i] - '0' : 0;
                fraction = (fraction * 10) + digit;
            }
        }

        long ticks = checked((seconds * TimeSpan.TicksPerSecond) + fraction);
        return negative ? -ticks : ticks;
    }

    /// <summary>Ticks since 1970 → UTC time; out of range becomes the limit.</summary>
    public static DateTime FromUnixTicks(long ticks)
    {
        long max = DateTime.MaxValue.Ticks - DateTime.UnixEpoch.Ticks;
        long min = -DateTime.UnixEpoch.Ticks;
        return DateTime.UnixEpoch.AddTicks(Math.Clamp(ticks, min, max));
    }
}

/// <summary>One entry of the <c>manifest</c> of custos-helper: a file, a folder, a link…</summary>
public sealed class RemoteEntry
{
    /// <summary>The letter of <c>find %y</c>: f, d, l, p, c, b, D.</summary>
    public required char Type { get; init; }

    public required long Size { get; init; }

    /// <summary>Modified, ticks since 1970.</summary>
    public required long MTimeTicks { get; init; }

    /// <summary>Changed (ctime), ticks since 1970.</summary>
    public required long CTimeTicks { get; init; }

    /// <summary>Permissions, octal ("644").</summary>
    public required string Mode { get; init; }

    public required long Uid { get; init; }

    public required long Gid { get; init; }

    public required string User { get; init; }

    public required string Group { get; init; }

    /// <summary>Target of a link; empty for the rest.</summary>
    public required byte[] Link { get; init; }

    /// <summary>The absolute path ("/var/www/site/index.php").</summary>
    public required byte[] Path { get; init; }

    public string Key => RemotePath.Key(Path);

    public bool IsFile => Type == 'f';

    public bool IsFolder => Type == 'd';
}

/// <summary>Reads the <c>manifest</c> of custos-helper: gzip, eleven fields per entry, each ending with NUL.</summary>
public static class RemoteListing
{
    public const int FieldsPerEntry = 11;

    /// <summary>More entries than this is taken for a broken or hostile server.</summary>
    public const int MaxEntries = 5_000_000;

    private const int MaxField = 64 * 1024;

    /// <exception cref="InvalidDataException">The data is not a listing of custos-helper.</exception>
    public static List<RemoteEntry> Read(Stream gzipped, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gzipped);
        using var input = new GZipStream(gzipped, CompressionMode.Decompress, leaveOpen: true);
        var entries = new List<RemoteEntry>();
        var fields = new byte[FieldsPerEntry][];
        int index = 0;
        var field = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        try
        {
            while ((read = input.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int start = 0;
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] != 0)
                    {
                        continue;
                    }

                    field.Write(buffer, start, i - start);
                    start = i + 1;
                    CheckField(field);
                    fields[index++] = field.ToArray();
                    field.SetLength(0);
                    if (index == FieldsPerEntry)
                    {
                        entries.Add(Parse(fields));
                        index = 0;
                        if (entries.Count > MaxEntries)
                        {
                            throw new InvalidDataException(Messages.Format(Messages.RemoteListingTooLong, MaxEntries));
                        }
                    }
                }

                field.Write(buffer, start, read - start);
                CheckField(field);
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new InvalidDataException(Messages.Format(Messages.RemoteListingBroken, ex.Message), ex);
        }

        if (index != 0 || field.Length != 0)
        {
            throw new InvalidDataException(Messages.Format(Messages.RemoteListingBroken, "cut off"));
        }

        return entries;
    }

    private static void CheckField(MemoryStream field)
    {
        if (field.Length > MaxField)
        {
            throw new InvalidDataException(Messages.Format(Messages.RemoteListingBroken, "field too long"));
        }
    }

    private static RemoteEntry Parse(byte[][] f)
    {
        if (f[0].Length != 1 || f[10].Length == 0 || f[10][0] != '/')
        {
            throw new FormatException("bad entry");
        }

        return new RemoteEntry
        {
            Type = (char)f[0][0],
            Size = Number(f[1]),
            MTimeTicks = RemotePath.ParseUnixTicks(f[2]),
            CTimeTicks = RemotePath.ParseUnixTicks(f[3]),
            Mode = Ascii(f[4]),
            Uid = Number(f[5]),
            Gid = Number(f[6]),
            User = Encoding.UTF8.GetString(f[7]),
            Group = Encoding.UTF8.GetString(f[8]),
            Link = f[9],
            Path = TrimSlash(f[10]),
        };
    }

    private static byte[] TrimSlash(byte[] path)
    {
        int length = path.Length;
        while (length > 1 && path[length - 1] == '/')
        {
            length--;
        }

        return length == path.Length ? path : path[..length];
    }

    private static long Number(byte[] text) => long.Parse(Ascii(text), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    private static string Ascii(byte[] text) =>
        text.All(b => b is >= 0x20 and < 0x7f) ? Encoding.ASCII.GetString(text) : throw new FormatException("not ASCII");
}
