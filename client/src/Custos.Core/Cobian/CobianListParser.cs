using System.Text;

namespace Custos.Core.Cobian;

/// <summary>One task from a Cobian Backup 11 list: its <c>key=value</c> lines.</summary>
public sealed class CobianTaskBlock
{
    public CobianTaskBlock(int lineNumber, IReadOnlyDictionary<string, string> values)
    {
        LineNumber = lineNumber;
        Values = values;
    }

    /// <summary>Line of the file where the block starts, for messages.</summary>
    public int LineNumber { get; }

    /// <summary>Values by key; keys compare case-insensitively.</summary>
    public IReadOnlyDictionary<string, string> Values { get; }

    public string? Get(string key) => Values.TryGetValue(key, out var value) ? value : null;
}

/// <summary>
/// Reads Cobian Backup 11 task lists (<c>MainList.lst</c>, section 9 of the client spec): blocks between
/// <c>{11//</c> and <c>//11}</c> with one <c>key=value</c> per line.
/// </summary>
public static class CobianListParser
{
    private const string BlockStart = "{11//";
    private const string BlockEnd = "//11}";

    /// <summary>Reads a list file, detecting its encoding (UTF-16 or UTF-8 with a byte order mark, UTF-8, or the ANSI code page).</summary>
    public static IReadOnlyList<CobianTaskBlock> ReadFile(string path) => Parse(Decode(File.ReadAllBytes(path)));

    /// <summary>Turns the bytes of a list file into text.</summary>
    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.Unicode, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        }

        // No byte order mark: UTF-16 without BOM shows up as zero bytes between ASCII letters.
        if (bytes.Length >= 4 && bytes[1] == 0 && bytes[3] == 0)
        {
            return Encoding.Unicode.GetString(bytes);
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return AnsiEncoding().GetString(bytes);
        }
    }

    public static IReadOnlyList<CobianTaskBlock> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var blocks = new List<CobianTaskBlock>();
        Dictionary<string, string>? current = null;
        int start = 0;
        int number = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } rawLine)
        {
            number++;
            string line = rawLine.Trim();
            if (line.StartsWith(BlockStart, StringComparison.Ordinal))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                start = number;
                line = line[BlockStart.Length..].Trim();
                if (line.Length == 0)
                {
                    continue;
                }
            }

            if (current is null)
            {
                continue;
            }

            bool ends = line.EndsWith(BlockEnd, StringComparison.Ordinal);
            if (ends)
            {
                line = line[..^BlockEnd.Length].Trim();
            }

            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                // The first value of a key wins; Cobian writes each key once.
                current.TryAdd(line[..equals].Trim(), line[(equals + 1)..].Trim());
            }

            if (ends)
            {
                blocks.Add(new CobianTaskBlock(start, current));
                current = null;
            }
        }

        if (current is { Count: > 0 })
        {
            blocks.Add(new CobianTaskBlock(start, current));
        }

        return blocks;
    }

    /// <summary>
    /// Splits a list the way Delphi's <c>TStringList.CommaText</c> writes it: items separated by commas (or spaces
    /// outside quotes), items with commas or spaces in double quotes, a quote inside quotes doubled.
    /// </summary>
    public static IReadOnlyList<string> SplitCommaText(string? value)
    {
        var items = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return items;
        }

        int i = 0;
        while (i < value.Length)
        {
            while (i < value.Length && value[i] <= ' ')
            {
                i++;
            }

            if (i >= value.Length)
            {
                break;
            }

            var item = new StringBuilder();
            if (value[i] == '"')
            {
                i++;
                while (i < value.Length)
                {
                    if (value[i] == '"')
                    {
                        if (i + 1 < value.Length && value[i + 1] == '"')
                        {
                            item.Append('"');
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    item.Append(value[i++]);
                }
            }
            else
            {
                while (i < value.Length && value[i] != ',' && value[i] > ' ')
                {
                    item.Append(value[i++]);
                }
            }

            items.Add(item.ToString());
            while (i < value.Length && value[i] <= ' ')
            {
                i++;
            }

            if (i < value.Length && value[i] == ',')
            {
                i++;
            }
        }

        return items;
    }

    /// <summary>
    /// Decodes Cobian's <c>Date/Time</c>: 64 characters '0' and '1', the bits of a Delphi <c>TDateTime</c> (a double
    /// counting days from 30.12.1899). Returns null if the text is not such a value.
    /// </summary>
    public static double? DecodeDateTimeBits(string? bits)
    {
        if (bits is null)
        {
            return null;
        }

        bits = bits.Trim().Trim('"');
        if (bits.Length != 64 || bits.Any(c => c is not ('0' or '1')))
        {
            return null;
        }

        // Most significant bit first, as a binary number is written. If that gives nonsense, try the other order.
        double value = BitsToDouble(bits);
        if (IsPlausibleDateTime(value))
        {
            return value;
        }

        double reversed = BitsToDouble(new string(bits.Reverse().ToArray()));
        return IsPlausibleDateTime(reversed) ? reversed : null;
    }

    /// <summary>Time of day of a Delphi <c>TDateTime</c>, rounded to the minute.</summary>
    public static TimeSpan TimeOfDay(double dateTime)
    {
        double fraction = Math.Abs(dateTime - Math.Truncate(dateTime));
        int minutes = (int)Math.Round(fraction * 24 * 60) % (24 * 60);
        return TimeSpan.FromMinutes(minutes);
    }

    private static double BitsToDouble(string bits)
    {
        ulong raw = 0;
        foreach (char c in bits)
        {
            raw = (raw << 1) | (c == '1' ? 1UL : 0UL);
        }

        return BitConverter.UInt64BitsToDouble(raw);
    }

    // 0 is 30.12.1899, 2958465 is 31.12.9999.
    private static bool IsPlausibleDateTime(double value) => double.IsFinite(value) && value >= 0 && value < 2958466;

    private static Encoding AnsiEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        int codePage = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        try
        {
            return Encoding.GetEncoding(codePage is 0 or 65001 ? 1251 : codePage);
        }
        catch (ArgumentException)
        {
            return Encoding.GetEncoding(1251);
        }
    }
}
