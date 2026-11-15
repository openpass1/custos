using System.Globalization;
using System.Text;
using Custos.Core.Ipc;

namespace Custos.Core.Agent;

/// <summary>
/// The journal of the service on disk (section 6.10): a file for each day, <c>logs\journal\2026-11-15.log</c>, with the
/// lines of the "Log" tab and the task in brackets (<see cref="LogText"/>). It is kept as long as the other logs and is
/// not loaded into the program; after a restart the service takes its last lines back into the live log.
/// Thread-safe. A file that cannot be written does not stop anything.
/// </summary>
public sealed class JournalFile(string folder) : IDisposable
{
    private const string DayFormat = "yyyy-MM-dd";

    /// <summary>How many days back <see cref="ReadRecent"/> looks at most.</summary>
    private const int MaxDaysBack = 31;

    private readonly Lock _lock = new();
    private StreamWriter? _writer;
    private DateOnly _day;

    public string Folder => folder;

    public static string PathFor(string folder, DateOnly day) =>
        Path.Combine(folder, day.ToString(DayFormat, CultureInfo.InvariantCulture) + ".log");

    public void Write(LogLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var day = DateOnly.FromDateTime(line.Time.ToLocalTime().DateTime);
        string text = LogText.Format(line.Time, line.Level, line.TaskName, line.Text, oneLine: true);
        lock (_lock)
        {
            if (_writer is null || day != _day)
            {
                Open(day);
            }

            try
            {
                _writer?.WriteLine(text);
            }
            catch (IOException)
            {
                // The disk with the logs is full or gone; the next line tries again with a new file.
                Close();
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            Close();
        }
    }

    /// <summary>
    /// The last lines of the journal, oldest first, at most <paramref name="capacity"/>, from the newest files (a month
    /// back at most). <paramref name="taskId"/> gives the id of a task by its name, or null when there is none now.
    /// </summary>
    public static List<LogLine> ReadRecent(string folder, int capacity, Func<string, string?> taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        var result = new List<LogLine>();
        if (capacity <= 0 || !Directory.Exists(folder))
        {
            return result;
        }

        var files = DayFiles(folder).OrderByDescending(f => f.Day).Take(MaxDaysBack);
        foreach (var file in files)
        {
            LogExcerpt excerpt;
            try
            {
                excerpt = LogText.Read(file.Path, _ => true, capacity - result.Count, readTask: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var line in excerpt.Lines)
            {
                line.TaskId = line.TaskName is { } name ? taskId(name) : null;
            }

            result.InsertRange(0, excerpt.Lines);
            if (result.Count >= capacity)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>Deletes the files of days older than the retention period.</summary>
    public static int DeleteOld(string folder, DateTimeOffset before)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        int deleted = 0;
        foreach (var file in DayFiles(folder))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file.Path) < before.UtcDateTime)
                {
                    File.Delete(file.Path);
                    deleted++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return deleted;
    }

    private static IEnumerable<(DateOnly Day, string Path)> DayFiles(string folder)
    {
        foreach (string path in Directory.EnumerateFiles(folder, "????-??-??.log"))
        {
            if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(path), DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                yield return (day, path);
            }
        }
    }

    private void Open(DateOnly day)
    {
        Close();
        _day = day;
        try
        {
            Directory.CreateDirectory(folder);
            var stream = new FileStream(PathFor(folder, day), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _writer = null;
        }
    }

    private void Close()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (IOException)
        {
        }

        _writer = null;
    }
}
