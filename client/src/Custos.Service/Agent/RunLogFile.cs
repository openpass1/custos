using System.Globalization;
using System.Text;
using Custos.Core.Backup;

namespace Custos.Service.Agent;

/// <summary>
/// The log file of one run (section 6.10): <c>logs\runs\2026-11-04 16-41-03 Docs 1a2b3c4d.log</c>, UTF-8, one line per
/// message with the local time. A file that cannot be written does not stop the run.
/// </summary>
internal sealed class RunLogFile : IDisposable
{
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
        string name = string.Create(CultureInfo.InvariantCulture,
            $"{startedAt.ToLocalTime():yyyy-MM-dd HH-mm-ss} {SetNaming.CleanName(taskName)} {runId[..Math.Min(8, runId.Length)]}.log");
        string path = System.IO.Path.Combine(folder, name);
        try
        {
            Directory.CreateDirectory(folder);
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            return new RunLogFile(path, new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RunLogFile(path, null);
        }
    }

    public void Write(RunLogLevel level, string text)
    {
        if (_writer is null)
        {
            return;
        }

        char mark = level switch
        {
            RunLogLevel.Error => 'E',
            RunLogLevel.Warning => 'W',
            RunLogLevel.Detail => 'D',
            _ => 'I',
        };
        string line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{mark}] {text}");
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

    /// <summary>The log file of a run (its name ends with the first 8 characters of the run id), or null when it is gone.</summary>
    public static string? Find(string folder, string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        if (!Directory.Exists(folder) || runId.Length < 8)
        {
            return null;
        }

        return new DirectoryInfo(folder).EnumerateFiles("* " + runId[..8].ToLowerInvariant() + ".log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    /// <summary>Deletes run logs older than the retention period.</summary>
    public static int DeleteOld(string folder, DateTimeOffset before)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        int deleted = 0;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.log"))
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

        return deleted;
    }
}
