using System.Globalization;
using System.Text;
using Custos.Core.Backup;
using Custos.Core.Ipc;

namespace Custos.Core.Agent;

/// <summary>
/// The log file of one run (section 6.10): <c>logs\runs\2026-11\2026-11-04 16-41-03 Docs 1a2b3c4d.log</c>, a folder for
/// each month, UTF-8, one line per message with the local time (<see cref="LogText"/>). A file that cannot be written
/// does not stop the run.
/// </summary>
public sealed class RunLogFile : IRunLog, IDisposable
{
    private const string MonthFormat = "yyyy-MM";

    private readonly StreamWriter? _writer;
    private readonly Lock _lock = new();

    private RunLogFile(string path, StreamWriter? writer)
    {
        Path = path;
        _writer = writer;
    }

    public string Path { get; }

    public static RunLogFile Create(string folder, string taskName, string runId, DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(runId);
        var local = startedAt.ToLocalTime();
        string name = string.Create(CultureInfo.InvariantCulture,
            $"{local:yyyy-MM-dd HH-mm-ss} {SetNaming.CleanName(taskName)} {runId[..Math.Min(8, runId.Length)]}.log");
        string month = System.IO.Path.Combine(folder, local.ToString(MonthFormat, CultureInfo.InvariantCulture));
        string path = System.IO.Path.Combine(month, name);
        try
        {
            Directory.CreateDirectory(month);
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            return new RunLogFile(path, new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RunLogFile(path, null);
        }
    }

    void IRunLog.Write(RunLogLevel level, string message) => Write(level, message);

    public void Write(RunLogLevel level, string text)
    {
        if (_writer is null)
        {
            return;
        }

        string line = LogText.Format(DateTimeOffset.Now, LogLineLevels.From(level), null, text);
        lock (_lock)
        {
            try
            {
                _writer.WriteLine(line);
            }
            catch (IOException)
            {
                // The disk with the logs is full or gone; the run goes on.
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try
            {
                _writer?.Dispose();
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// The log file of a run (its name ends with the first 8 characters of the run id), or null when it is gone. Looks
    /// in the folders of the months and in the folder itself, where versions before 0.5 put every file.
    /// </summary>
    public static string? Find(string folder, string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        if (!Directory.Exists(folder) || runId.Length < 8)
        {
            return null;
        }

        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("* " + runId[..8].ToLowerInvariant() + ".log", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Deletes run logs older than the retention period, and the folders of months left empty.</summary>
    public static int DeleteOld(string folder, DateTimeOffset before)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        int deleted = 0;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.log", SearchOption.AllDirectories))
        {
            try
            {
                if (file.LastWriteTimeUtc < before.UtcDateTime)
                {
                    file.Delete();
                    deleted++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        foreach (var month in new DirectoryInfo(folder).EnumerateDirectories())
        {
            try
            {
                if (IsMonth(month.Name) && !month.EnumerateFileSystemInfos().Any())
                {
                    month.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return deleted;
    }

    /// <summary>
    /// Puts the files that versions before 0.5 kept all in one folder into the folders of their months, by the date at the
    /// start of the name. A file that cannot be moved stays where it is.
    /// </summary>
    /// <returns>How many files were moved.</returns>
    public static int MoveToMonthFolders(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        int moved = 0;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.log", SearchOption.TopDirectoryOnly).ToList())
        {
            if (file.Name.Length < 11 || file.Name[10] != ' '
                || !DateTime.TryParseExact(file.Name.AsSpan(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                continue;
            }

            try
            {
                string month = System.IO.Path.Combine(folder, day.ToString(MonthFormat, CultureInfo.InvariantCulture));
                string target = System.IO.Path.Combine(month, file.Name);
                if (File.Exists(target))
                {
                    continue;
                }

                Directory.CreateDirectory(month);
                file.MoveTo(target);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return moved;
    }

    private static bool IsMonth(string name) =>
        DateTime.TryParseExact(name, MonthFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}
