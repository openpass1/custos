using System.Globalization;
using System.Text;
using Custos.Core.Ipc;

namespace Custos.Core.Agent;

/// <summary>
/// A log line as the service writes it to its files (section 6.10), in local time:
/// <c>2026-11-15 16:10:05 [W] text</c>. The daily journal puts the task in brackets after the level:
/// <c>2026-11-15 16:10:05 [W] [Docs] text</c>.
/// </summary>
public static class LogText
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>"2026-11-15 16:10:05 [W] ".</summary>
    private const int PrefixLength = 24;

    public static char Mark(string level) => level switch
    {
        LogLineLevels.Error => 'E',
        LogLineLevels.Warning => 'W',
        LogLineLevels.Detail => 'D',
        _ => 'I',
    };

    public static string Level(char mark) => mark switch
    {
        'E' => LogLineLevels.Error,
        'W' => LogLineLevels.Warning,
        'D' => LogLineLevels.Detail,
        _ => LogLineLevels.Info,
    };

    /// <summary>One line of a file. Line breaks inside the text become spaces when <paramref name="oneLine"/> is set.</summary>
    public static string Format(DateTimeOffset time, string level, string? taskName, string text, bool oneLine = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(PrefixLength + text.Length + 16);
        builder.Append(time.ToLocalTime().ToString(TimeFormat, CultureInfo.InvariantCulture))
            .Append(" [").Append(Mark(level)).Append("] ");
        if (!string.IsNullOrEmpty(taskName))
        {
            builder.Append('[').Append(OneLine(taskName)).Append("] ");
        }

        builder.Append(oneLine ? OneLine(text) : text);
        return builder.ToString();
    }

    /// <summary>
    /// Reads a line written by <see cref="Format"/>; null for a line in another form (the continuation of a text with
    /// line breaks). With <paramref name="readTask"/>, "[name] " right after the level is taken as the task.
    /// </summary>
    public static LogLine? Parse(string raw, bool readTask)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length < PrefixLength - 1
            || raw[19] != ' ' || raw[20] != '[' || raw[22] != ']'
            || !DateTime.TryParseExact(raw.AsSpan(0, 19), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
        {
            return null;
        }

        string rest = raw.Length > PrefixLength ? raw[PrefixLength..] : string.Empty;
        string? task = null;
        if (readTask && rest.StartsWith('['))
        {
            int end = rest.IndexOf("] ", StringComparison.Ordinal);
            if (end > 1)
            {
                task = rest[1..end];
                rest = rest[(end + 2)..];
            }
        }

        return new LogLine
        {
            Time = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)),
            Level = Level(raw[21]),
            TaskName = task,
            Text = rest,
        };
    }

    /// <summary>
    /// Reads a log file that may still be written to. Lines that match go to the result, at most
    /// <paramref name="maxLines"/> of them, the last ones; a line in another form continues the line before it.
    /// </summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be read.</exception>
    public static LogExcerpt Read(string path, Func<LogLine, bool> match, int maxLines, bool readTask = false)
    {
        ArgumentNullException.ThrowIfNull(match);
        var kept = new Queue<LogLine>();
        int total = 0;
        int matched = 0;
        LogLine? previous = null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } raw)
        {
            var line = Parse(raw, readTask);
            if (line is null)
            {
                if (raw.Length == 0)
                {
                    continue;
                }

                // The next line of a text with line breaks: the time and level of the line it belongs to.
                line = new LogLine
                {
                    Time = previous?.Time ?? DateTimeOffset.MinValue,
                    Level = previous?.Level ?? LogLineLevels.Info,
                    TaskName = previous?.TaskName,
                    Text = raw,
                };
            }
            else
            {
                previous = line;
            }

            total++;
            if (!match(line))
            {
                continue;
            }

            matched++;
            kept.Enqueue(line);
            if (kept.Count > maxLines)
            {
                kept.Dequeue();
            }
        }

        return new LogExcerpt([.. kept], total, matched);
    }

    private static string OneLine(string text) =>
        text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal)
            ? text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ')
            : text;
}

/// <summary>What <see cref="LogText.Read"/> found: the lines kept, how many lines the file has and how many matched.</summary>
public sealed record LogExcerpt(List<LogLine> Lines, int Total, int Matched);

/// <summary>Which lines of a log to show (the filters of the "Log" tab and of the log of a run).</summary>
public sealed record LogFilter(string Level, string? Search)
{
    public static readonly LogFilter All = new(LogFilterLevels.All, null);

    public bool Matches(LogLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!LogFilterLevels.Matches(Level, line.Level))
        {
            return false;
        }

        string? search = Search?.Trim();
        return string.IsNullOrEmpty(search)
            || line.Text.Contains(search, StringComparison.CurrentCultureIgnoreCase)
            || (line.TaskName?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }
}

/// <summary>Values of <see cref="LogFilter.Level"/>.</summary>
public static class LogFilterLevels
{
    /// <summary>Every line.</summary>
    public const string All = "all";

    /// <summary>Warnings and errors.</summary>
    public const string Problems = "problems";

    /// <summary>Errors only.</summary>
    public const string Errors = "errors";

    public static bool IsKnown(string? value) => value is All or Problems or Errors;

    public static bool Matches(string filter, string level) => filter switch
    {
        Errors => level == LogLineLevels.Error,
        Problems => level is LogLineLevels.Warning or LogLineLevels.Error,
        _ => true,
    };
}
