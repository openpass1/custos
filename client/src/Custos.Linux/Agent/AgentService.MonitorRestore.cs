using System.Text.Json;
using System.Text.Json.Serialization;
using Custos.Core.Backup;
using Custos.Core.Destinations;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Monitor;
using Custos.Service.Platform;
using Microsoft.Extensions.Logging;

namespace Custos.Service.Agent;

internal sealed partial class AgentService
{
    private const int MonitorRestoreSetLimit = 100;

    private CommandOutcome ExecuteMonitorRestore(MonitorCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? binding = command.RestoreBinding;
        if (binding is null || !CanMonitorRestore(binding))
        {
            return MonitorRestoreError("not_allowed", rejected: true);
        }

        try
        {
            var payload = command.Payload;
            int fieldCount = command.Kind switch
            {
                CommandKinds.RestoreList => 1,
                CommandKinds.RestoreStart => 3,
                _ => 2,
            };
            if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Count() != fieldCount
                || !ReadRestoreId(payload, "task_id", out Guid task))
            {
                return MonitorRestoreError("bad_request", rejected: true);
            }

            string taskId = task.ToString("D");
            if (command.Kind is CommandKinds.RestoreStatus or CommandKinds.RestoreStop)
            {
                return ReadRestoreId(payload, "job_id", out Guid id)
                    ? MonitorRestoreStatus(binding, taskId, id, command.Kind == CommandKinds.RestoreStop)
                    : MonitorRestoreError("bad_request", rejected: true);
            }

            if (_config.GetTask(taskId) is null)
            {
                return MonitorRestoreError("task_missing");
            }

            if (command.Kind == CommandKinds.RestoreList)
            {
                return MonitorRestoreList(taskId);
            }

            return TryRestoreGuid(command.Id, out Guid job) && ReadRestoreId(payload, "set_run_id", out Guid set)
                && ReadRestoreId(payload, "destination_id", out Guid destination)
                ? MonitorRestoreStart(binding, taskId, job, set, destination, command.ExpiresAt, cancellationToken)
                : MonitorRestoreError("bad_request", rejected: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentRequestException ex)
        {
            return MonitorRestoreError(ex.Code == PipeProtocol.Errors.NotFound ? "task_missing" : "scan_failed");
        }
#pragma warning disable CA1031 // Raw filesystem and provider errors must not leave the agent through this command.
        catch (Exception)
#pragma warning restore CA1031
        {
            return MonitorRestoreError("internal_error");
        }
    }

    private bool CanMonitorRestore(string binding) => _link?.RestoreBinding == binding
        && _linkStore?.Get().Status == ServerLinkStates.Approved && _config.GetSettings().AllowRemoteEdit;

    private CommandOutcome MonitorRestoreList(string taskId)
    {
        var result = ListRestoreSets(new RestoreListRequest { TaskId = taskId }, ConfineMonitorRestoreStore);
        var available = new List<MonitorRestoreSet>();
        bool partial = result.Problems.Count != 0;
        foreach (var set in result.Sets)
        {
            string name = set.Header.SetName;
            if (!TryRestoreGuid(set.Header.RunId, out Guid run) || !TryRestoreGuid(set.DestinationId, out Guid destination)
                || set.Header.BackupType is not (BackupTypes.Full or BackupTypes.Incremental or BackupTypes.Differential or BackupTypes.Mirror)
                || set.Storage is not ("folder" or ArchiveFormats.Zip or ArchiveFormats.SevenZip) || string.IsNullOrWhiteSpace(name)
                || name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal) || name.Any(char.IsControl))
            {
                partial = true;
                continue;
            }

            if (name.Length > 120)
            {
                name = name[..119] + "…";
            }

            available.Add(new MonitorRestoreSet(run.ToString("D"), destination.ToString("D"), name, set.Header.StartedAt,
                set.Header.BackupType, set.Storage, set.Parts, Math.Max(0, set.Bytes)));
        }

        var groups = available.GroupBy(set => (set.SetRunId, set.DestinationId)).ToList();
        partial |= groups.Any(group => group.Count() > 1);
        var distinct = groups.Where(group => group.Count() == 1)
            .Select(group => group.First()).Take(MonitorRestoreSetLimit + 1).ToList();
        bool truncated = distinct.Count > MonitorRestoreSetLimit;
        if (truncated)
        {
            distinct.RemoveAt(MonitorRestoreSetLimit);
        }

        return CommandOutcome.Done() with
        {
            Restore = new MonitorRestoreResult { Sets = distinct, Truncated = truncated, Partial = partial },
        };
    }

    private CommandOutcome MonitorRestoreStart(string binding, string taskId, Guid id, Guid set, Guid destination,
        DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        lock (_restoreLock)
        {
            if (ReadMonitorRestoreRecord(id) is not null)
            {
                return MonitorRestoreStatus(binding, taskId, id, stop: false);
            }
        }

        string jobId = id.ToString("D");
        long configRevision = _config.ConfigRevision;
        var record = new MonitorRestoreRecord(binding, taskId, new MonitorRestoreJob(jobId, "running"));
        var managed = new ManagedRestore(jobId, Path.Combine(MonitorRestoreFiles.RecordFolder(id), "logs"), ConfineMonitorRestoreStore, () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanMonitorRestore(binding) || configRevision != _config.ConfigRevision
                || expiresAt is not { } deadline || deadline <= DateTimeOffset.UtcNow)
            {
                throw new AgentRequestException(PipeProtocol.Errors.Forbidden, "Remote restore is no longer allowed.");
            }

            MonitorRestoreFiles.Reserve(id);
            SaveMonitorRestoreRecord(id, record);
        }, status => SaveCompletedMonitorRestore(id, record, status));

        try
        {
            _ = StartRestore(new RestoreStartRequest
            {
                TaskId = taskId,
                Location = string.Empty, // The service finds the set by its run id in the named configured destination.
                SetRunId = set.ToString("D"),
                DestinationId = destination.ToString("D"),
                TargetFolder = MonitorRestoreFiles.Output(id),
                Existing = RestoreExisting.Skip,
            }, ServerUser, managed);
        }
        catch (AgentRequestException ex)
        {
            return ex.Code switch
            {
                PipeProtocol.Errors.Forbidden => MonitorRestoreError("not_allowed", rejected: true),
                PipeProtocol.Errors.NotFound => MonitorRestoreError("set_missing"),
                _ => MonitorRestoreError("busy"),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MonitorRestoreError("output_unavailable");
        }

        return MonitorRestoreAnswer(record.Job);
    }

    private CommandOutcome MonitorRestoreStatus(string binding, string taskId, Guid id, bool stop)
    {
        lock (_restoreLock)
        {
            var record = ReadMonitorRestoreRecord(id);
            if (record is null || record.Binding != binding || record.TaskId != taskId)
            {
                return MonitorRestoreError("job_missing");
            }

            string jobId = id.ToString("D");
            if (_restore is { } current && current.Id == jobId)
            {
                if (stop && current.Running)
                {
                    current.Cancel();
                }

                return MonitorRestoreAnswer(DescribeMonitorRestore(jobId, current.Describe()));
            }

            return MonitorRestoreAnswer(record.Job.State == "running" ? record.Job with { State = "interrupted" } : record.Job);
        }
    }

    private static void ConfineMonitorRestoreStore(DestinationStore store)
    {
        if (store is LocalStore local)
        {
            MonitorRestoreFiles.CheckSource(local.Root);
            local.OpenConfinedFile = path => MonitorRestoreFiles.OpenSource(local.Root, path);
        }
    }

    private static MonitorRestoreJob DescribeMonitorRestore(string jobId, RestoreStatusResult status)
    {
        var progress = status.Progress;
        var result = status.Result;
        string state = status.Running ? "running"
            : result?.Status is RunStatuses.Success or RunStatuses.Warning or RunStatuses.Error or RunStatuses.Aborted ? result.Status : "interrupted";
        return new MonitorRestoreJob(jobId, state)
        {
            FilesDone = progress?.FilesDone,
            FilesTotal = progress?.FilesTotal,
            BytesDone = progress is null ? null : Math.Max(0, progress.BytesDone),
            BytesTotal = progress is null ? null : Math.Max(0, progress.BytesTotal),
            FilesRestored = result?.FilesRestored,
            FilesSkipped = result?.FilesSkipped,
            FilesFailed = result?.FilesFailed,
            Warnings = result?.Warnings,
        };
    }

    private static MonitorRestoreRecord? ReadMonitorRestoreRecord(Guid id)
    {
        string folder = MonitorRestoreFiles.RecordFolder(id);
        string path = Path.Combine(folder, "job.json");
        if (Native.Stat(path) is null)
        {
            return null;
        }

        using var file = MonitorRestoreFiles.OpenSource(folder, path);
        return JsonSerializer.Deserialize(file, MonitorRestoreJsonContext.Default.MonitorRestoreRecord);
    }

    private static void SaveMonitorRestoreRecord(Guid id, MonitorRestoreRecord record)
    {
        string path = Path.Combine(MonitorRestoreFiles.RecordFolder(id), "job.json");
        string temp = path + ".new";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(record, MonitorRestoreJsonContext.Default.MonitorRestoreRecord));
        File.Move(temp, path, overwrite: true);
    }

    private void SaveCompletedMonitorRestore(Guid id, MonitorRestoreRecord record, RestoreStatusResult status)
    {
        try
        {
            SaveMonitorRestoreRecord(id, record with { Job = DescribeMonitorRestore(record.Job.JobId, status) });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ServiceLog.MonitorMessage(logger, LogLevel.Warning, "Could not save the result of a remote restore.");
        }
    }

    private static CommandOutcome MonitorRestoreAnswer(MonitorRestoreJob job) =>
        CommandOutcome.Done() with { Restore = new MonitorRestoreResult { Job = job } };

    private static CommandOutcome MonitorRestoreError(string error, bool rejected = false) =>
        new(rejected ? CommandStatuses.Rejected : CommandStatuses.Failed, null) { Restore = new MonitorRestoreResult { Error = error } };

    private static bool ReadRestoreId(JsonElement data, string name, out Guid id) => TryRestoreGuid(Text(data, name), out id);

    private static bool TryRestoreGuid(string? text, out Guid id) => Guid.TryParseExact(text, "D", out id) && id != Guid.Empty;
}

internal sealed record MonitorRestoreRecord(string Binding, string TaskId, MonitorRestoreJob Job);

[JsonSerializable(typeof(MonitorRestoreRecord))]
internal sealed partial class MonitorRestoreJsonContext : JsonSerializerContext;
