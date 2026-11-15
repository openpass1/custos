using System.Runtime.Versioning;
using Custos.Core;
using Custos.Core.Agent;
using Custos.Core.Archives;
using Custos.Core.Backup;
using Custos.Core.Destinations;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Service.Platform;
using Microsoft.Extensions.Logging;

namespace Custos.Service.Agent;

/// <summary>Restore (section 6.11) and the 7z.dll of the service (section 6.5).</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class AgentService
{
    private readonly Lock _restoreLock = new();
    private RestoreJob? _restore;

    /// <summary>Loads 7z.dll at start, so that its version is in the log and a missing or broken one is known before a
    /// task needs it. Without it only the tasks with 7z fail.</summary>
    private void LoadSevenZip()
    {
        if (Platform.Archives is not ArchiveProvider provider)
        {
            return;
        }

        try
        {
            string version = provider.LoadSevenZip();
            _sevenZipProblem = null;
            ServiceLog.SevenZipLoaded(logger, version, provider.SevenZipPath);
        }
        catch (ArchiveException ex)
        {
            _sevenZipProblem = ex.Message;
            ServiceLog.SevenZipUnavailable(logger, ex.Message);
        }
    }

    public RestoreListResult ListRestoreSets(RestoreListRequest request)
    {
        var result = new RestoreListResult();
        foreach (var place in Places(request, result.Problems))
        {
            using (place)
            {
                foreach (var set in place.Sets)
                {
                    result.Sets.Add(new RestoreSetInfo
                    {
                        Location = set.Location,
                        Where = place.Label,
                        Header = set.Header,
                        Storage = set.Storage == SetStorage.Folder ? "folder" : set.Format ?? ArchiveFormats.SevenZip,
                        Bytes = set.Bytes,
                        Parts = Math.Max(1, set.Parts.Count),
                    });
                }
            }
        }

        result.Sets.Sort((a, b) => b.Header.StartedAt.CompareTo(a.Header.StartedAt));
        return result;
    }

    public OkResult StartRestore(RestoreStartRequest request, string user)
    {
        lock (_restoreLock)
        {
            if (_restore is { Running: true })
            {
                throw new AgentRequestException(PipeProtocol.Errors.Invalid, Messages.RestoreAlreadyRunning);
            }

            if (_sevenZipUpdating)
            {
                throw new AgentRequestException(PipeProtocol.Errors.Conflict, Messages.SevenZipBusyUpdate);
            }
        }

        foreach (string? folder in (string?[])[request.TargetFolder, request.DumpsFolder])
        {
            if (folder is not null && !Path.IsPathFullyQualified(folder))
            {
                throw new AgentRequestException(PipeProtocol.Errors.Invalid, Messages.Format(Messages.PathNotAbsolute, folder));
            }
        }

        var problems = new List<string>();
        Place? found = null;
        StoredSet? set = null;
        foreach (var place in Places(request, problems, request.Where))
        {
            var match = found is null ? place.Sets.FirstOrDefault(s => string.Equals(s.Location, request.Location, StringComparison.OrdinalIgnoreCase)) : null;
            if (match is not null)
            {
                found = place;
                set = match;
            }
            else
            {
                place.Dispose();
            }
        }

        if (found is null || set is null)
        {
            throw new AgentRequestException(PipeProtocol.Errors.NotFound, problems.FirstOrDefault() ?? Messages.Format(Messages.RestoreSetGone, request.Location));
        }

        var started = DateTimeOffset.UtcNow;
        string runId = "restore-" + Guid.NewGuid().ToString("N")[..8];
        var file = RunLogFile.Create(DataFolders.RunLogs, Messages.RestoreLogName, runId, started);
        var job = new RestoreJob(set.Header.SetName, request.TargetFolder ?? Messages.RestoreToOriginal, started, file.Path);
        lock (_restoreLock)
        {
            if (_restore is { Running: true } || _sevenZipUpdating)
            {
                found.Dispose();
                file.Dispose();
                throw new AgentRequestException(PipeProtocol.Errors.Invalid, _sevenZipUpdating ? Messages.SevenZipBusyUpdate : Messages.RestoreAlreadyRunning);
            }

            _restore?.Dispose();
            _restore = job;
        }

        ServiceLog.RestoreStarted(logger, set.Header.SetName, job.Target, user);
        var log = new RunLog(this, file, set.Header.TaskId, _config.GetTaskName(set.Header.TaskId), runId);
        var restoreRequest = new RestoreRequest
        {
            Sets = found.Sets,
            Set = set,
            TargetFolder = request.TargetFolder,
            DumpsFolder = request.DumpsFolder,
            Existing = request.Existing,
            Access = found.Access,
        };
        var thread = new Thread(() => RunRestore(job, found, file, log, restoreRequest)) { Name = "CUSTOS restore", IsBackground = true };
        thread.Start();
        return OkResult.Instance;
    }

    public RestoreStatusResult GetRestoreStatus()
    {
        lock (_restoreLock)
        {
            return _restore?.Describe() ?? new RestoreStatusResult();
        }
    }

    public OkResult StopRestore(string user)
    {
        lock (_restoreLock)
        {
            if (_restore is not { Running: true } job)
            {
                return new OkResult { Ok = false, Message = Messages.RestoreNotRunning };
            }

            job.Cancel();
        }

        Log(Core.Ipc.LogLineLevels.Warning, Messages.Format(Messages.RestoreStopRequested, user));
        return OkResult.Instance;
    }

    private void RunRestore(RestoreJob job, Place place, RunLogFile file, RunLog log, RestoreRequest request)
    {
        RestoreResult result;
        try
        {
            var engine = new RestoreEngine(log, progress => job.Progress = progress);
            result = engine.Run(request, job.Cancellation);
        }
        catch (OperationCanceledException)
        {
            result = new RestoreResult { Status = RunStatuses.Aborted, ErrorMessage = Messages.RestoreAborted };
            log.Write(RunLogLevel.Warning, Messages.RestoreAborted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArchiveException or BackupException)
        {
            result = new RestoreResult { Status = RunStatuses.Error, ErrorMessage = ex.Message };
            log.Write(RunLogLevel.Error, ex.Message);
        }
#pragma warning disable CA1031 // A failing restore must not take the service down; it is reported.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            result = new RestoreResult { Status = RunStatuses.Error, ErrorMessage = Messages.Format(Messages.InternalError, ex.Message) };
            log.Write(RunLogLevel.Error, result.ErrorMessage);
            ServiceLog.RunCrashed(logger, ex, Messages.RestoreLogName);
        }
        finally
        {
            place.Dispose();
            file.Dispose();
        }

        lock (_restoreLock)
        {
            job.Finish(result);
        }

        ServiceLog.RestoreFinished(logger, job.SetName, result.Status, result.FilesRestored, result.FilesFailed);
    }

    /// <summary>The places a restore looks in: every destination of the task, or the folder; with their sets.</summary>
    /// <param name="only">The place a set was found in (<see cref="RestoreSetInfo.Where"/>): the others are not opened.</param>
    private List<Place> Places(RestoreSource request, List<string> problems, string? only = null)
    {
        var places = new List<Place>();
        if (request.TaskId is { } taskId)
        {
            var task = _config.GetTask(taskId) ?? throw NotFound();
            string? password = request.Password;
            if (password is null && task.Archive.Encrypt && task.Archive.PasswordCredentialId is { } credentialId)
            {
                try
                {
                    password = Platform.GetArchivePassword(credentialId);
                }
                catch (BackupException ex)
                {
                    problems.Add(ex.Message);
                }
            }

            var variables = PathVariables.For(Platform, task, DateTime.Now);
            foreach (var destination in task.Clone().Destinations)
            {
                // Variables in the path are filled in as for a run now; a date in the path finds this month's sets.
                destination.Path = variables.Expand(destination.Path, out string? problem);
                if (problem is not null)
                {
                    problems.Add(problem);
                    continue;
                }

                string label = DestinationStores.Label(destination);
                if (only is not null && !string.Equals(label, only, StringComparison.Ordinal))
                {
                    continue;
                }

                if (OpenPlace(label, () => Platform.OpenStore(destination), password, taskId, problems) is { } place)
                {
                    places.Add(place);
                }
            }
        }
        else if (request.Destination is { } destination)
        {
            // A destination on a server or in a cloud drive, typed for this restore (another computer: the same
            // account, the folder of the sets).
            if (!DestinationKinds.IsSupported(destination.Kind) || DestinationKinds.IsFileSystem(destination.Kind))
            {
                throw new AgentRequestException(PipeProtocol.Errors.Invalid, Messages.Format(Messages.DestinationKindNotSupported, destination.Kind));
            }

            if (DestinationStores.Problem(destination) is { } lacking)
            {
                throw new AgentRequestException(PipeProtocol.Errors.Invalid, lacking);
            }

            string label = DestinationStores.Label(destination);
            if ((only is null || string.Equals(label, only, StringComparison.Ordinal))
                && OpenPlace(label, () => Platform.OpenStore(destination), request.Password, null, problems) is { } place)
            {
                places.Add(place);
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.Folder) && Path.IsPathFullyQualified(request.Folder))
        {
            var folder = new TaskDestination
            {
                Kind = request.CredentialId is null ? DestinationKinds.Local : DestinationKinds.Unc,
                Path = request.Folder,
                CredentialId = request.CredentialId,
            };
            if (OpenPlace(request.Folder, () => Platform.OpenStore(folder), request.Password, null, problems) is { } place)
            {
                places.Add(place);
            }
        }
        else
        {
            throw new AgentRequestException(PipeProtocol.Errors.Invalid, Messages.Format(Messages.PathNotAbsolute, request.Folder));
        }

        return places;
    }

    private Place? OpenPlace(string label, Func<DestinationStore> open, string? password, string? taskId, List<string> problems)
    {
        DestinationStore? store = null;
        try
        {
            store = open();
            if (store.HasFolders && !store.FolderExists(store.Root))
            {
                throw new DirectoryNotFoundException(Messages.Format(Messages.RestoreFolderMissing, store.Root));
            }

            var access = new ArchiveAccess(Platform.Archives, password, store);
            var sets = SetCatalog.Scan(store, store.Root, taskId, access,
                (path, reason) => problems.Add(Messages.Format(Messages.ArchiveSkipped, path, reason)), mirror: true);
            var place = new Place(label, store, access, sets);
            store = null;
            return place;
        }
        catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            problems.Add(Messages.Format(Messages.DestinationUnavailable, label, ex.Message));
            return null;
        }
        finally
        {
            store?.Dispose();
        }
    }

    /// <summary>A destination opened for a restore: its logon or connection stays open until the restore is done.</summary>
    private sealed class Place(string label, DestinationStore store, ArchiveAccess access, List<StoredSet> sets) : IDisposable
    {
        public string Label { get; } = label;

        public ArchiveAccess Access { get; } = access;

        public List<StoredSet> Sets { get; } = sets;

        public void Dispose() => store.Dispose();
    }

    /// <summary>The restore in progress, or the last one.</summary>
    private sealed class RestoreJob(string setName, string target, DateTimeOffset startedAt, string logFile) : IDisposable
    {
        private readonly CancellationTokenSource _cancel = new();

        public string SetName { get; } = setName;

        public string Target { get; } = target;

        public bool Running { get; private set; } = true;

        public CancellationToken Cancellation => _cancel.Token;

        public RestoreProgress? Progress { get; set; }

        private RestoreResult? Result { get; set; }

        private DateTimeOffset? FinishedAt { get; set; }

        public void Cancel() => _cancel.Cancel();

        /// <summary>Called when the next restore replaces this finished one.</summary>
        public void Dispose() => _cancel.Dispose();

        public void Finish(RestoreResult result)
        {
            Result = result;
            FinishedAt = DateTimeOffset.UtcNow;
            Running = false;
        }

        public RestoreStatusResult Describe() => new()
        {
            Running = Running,
            SetName = SetName,
            Target = Target,
            StartedAt = startedAt,
            FinishedAt = FinishedAt,
            Progress = Progress,
            Result = Result,
            LogFile = logFile,
        };
    }
}
