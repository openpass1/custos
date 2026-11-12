using System.IO.Compression;
using System.Text;
using Custos.Core.Backup;
using Custos.Core.Model;

namespace Custos.Core.Monitor;

/// <summary>The reports of a run for the server (A.5, A.7), built from the history record and the run log.</summary>
public static class RunReports
{
    public const string StartPath = "agent/runs/start";
    public const string FinishPath = "agent/runs/finish";

    /// <summary>Lines of <c>log_tail</c>.</summary>
    public const int TailLines = 100;

    /// <summary>A whole log is sent only up to this size packed (the server takes at most 8 MB of JSON).</summary>
    public const long MaxPackedLog = 5L * 1024 * 1024;

    public static RunStartBody Start(RunRecord record, int taskRevision)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new RunStartBody
        {
            RunId = record.RunId,
            TaskId = record.TaskId,
            TaskRevision = taskRevision,
            BackupType = record.BackupType,
            Trigger = record.Trigger,
            ScheduledFor = record.Trigger is RunTriggers.Schedule or RunTriggers.CatchUp ? record.ScheduledFor : null,
            StartedAt = record.StartedAt,
        };
    }

    /// <param name="logFile">The run log; its end goes as <c>log_tail</c>, and the whole of it for a warning or an error.</param>
    public static RunFinishBody Finish(RunRecord record, BackupResult result, string? logFile)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(result);
        string status = record.Status ?? RunStatuses.Error;
        var verify = record.Verify ?? result.Verify.ToRecord();
        return new RunFinishBody
        {
            RunId = record.RunId,
            TaskId = record.TaskId,
            Status = status,
            FinishedAt = record.FinishedAt ?? DateTimeOffset.UtcNow,
            SourceFiles = record.SourceFiles,
            SourceBytes = record.SourceBytes,
            FilesProcessed = record.FilesProcessed,
            BytesWritten = record.BytesWritten,
            Errors = record.Errors,
            Warnings = record.Warnings,
            ErrorMessage = record.ErrorMessage,
            Destinations =
            [
                .. record.Destinations.Select(d => new FinishDestination
                {
                    DestinationId = d.DestinationId,
                    Status = d.Status,
                    SetName = d.SetName,
                    Bytes = d.Bytes,
                    Error = d.Error,
                }),
            ],
            Verify = verify is null ? new FinishVerify() : new FinishVerify
            {
                Performed = true,
                Ok = verify.Ok,
                Scope = verify.Scope,
                SetsChecked = verify.SetsChecked,
                FailedSets = verify.FailedSets,
            },
            Retention = new FinishRetention { DeletedSets = result.DeletedSets },
            Databases = record.Databases.Count == 0 ? null
                : [.. record.Databases.Select(d => new FinishDatabase { Engine = d.Engine, Name = d.Name, Bytes = d.Bytes, Ok = d.Ok, Error = d.Error })],
            Remote = record.Remote is { } remote
                ? new FinishRemote { HelperVersion = remote.HelperVersion, ServerFreeBytes = remote.ServerFreeBytes, FilesChanged = remote.FilesChanged, FilesDeleted = remote.FilesDeleted }
                : null,
            LogTail = logFile is null ? null : Tail(logFile, TailLines),
            LogGzB64 = logFile is not null && status is RunStatuses.Warning or RunStatuses.Error ? Pack(logFile) : null,
        };
    }

    /// <summary>The last lines of a text file, or null if it cannot be read.</summary>
    public static string? Tail(string file, int lines)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // The end only: a log of millions of files is not read whole for 100 lines.
            const int Window = 256 * 1024;
            if (stream.Length > Window)
            {
                stream.Seek(-Window, SeekOrigin.End);
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var all = reader.ReadToEnd().Split('\n');
            int skip = stream.Length > Window ? 1 : 0;
            var kept = all.Skip(skip).Select(l => l.TrimEnd('\r')).ToList();
            if (kept.Count > 0 && kept[^1].Length == 0)
            {
                kept.RemoveAt(kept.Count - 1);
            }

            return string.Join('\n', kept.Skip(Math.Max(0, kept.Count - lines)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The whole file packed with gzip, in base64; null when it is missing or too big even packed.</summary>
    public static string? Pack(string file, long maxPacked = MaxPackedLog)
    {
        try
        {
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var packed = new MemoryStream();
            using (var gzip = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    gzip.Write(buffer, 0, read);
                    if (packed.Length > maxPacked)
                    {
                        return null;
                    }
                }
            }

            return packed.Length > maxPacked ? null : Convert.ToBase64String(packed.GetBuffer(), 0, (int)packed.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
