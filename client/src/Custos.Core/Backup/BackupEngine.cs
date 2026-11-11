using System.Diagnostics;
using System.Security.Cryptography;
using Custos.Core.Archives;
using Custos.Core.Model;
using Custos.Core.Text;

namespace Custos.Core.Backup;

/// <summary>
/// Performs one run of a task: full, incremental or differential copy or a mirror into one or more local, network or
/// USB destinations, as plain files or as a 7z or ZIP archive, with change tracking, set manifests, verification and
/// retention (sections 6.1–6.8 and 7 of the client spec).
/// </summary>
/// <remarks>
/// Every file is read once and written to all destinations at the same time; a destination that fails is dropped,
/// the others go on. A copy is never left looking finished when it is not: files are written under a temporary name
/// and renamed when complete, a set folder or archive carries ".partial" until it is complete. The database and the
/// destinations are only cleaned up after a successful run.
/// </remarks>
public sealed partial class BackupEngine
{
    public const string TempSuffix = ".custos-partial";

    private const int BufferSize = 1024 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly IBackupPlatform _platform;
    private readonly IFileStateStore _store;
    private readonly IRunLog _log;
    private readonly Action<BackupProgress>? _onProgress;
    private readonly BackupProgress _progress = new();
    private readonly Stopwatch _progressClock = Stopwatch.StartNew();
    private readonly SpeedMeter _speed = new();
    private readonly Dictionary<string, INetworkSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    // The archive writers read the sources and write the destinations on their own threads: progress, counters and
    // the failure of a destination are changed under this lock.
    private readonly Lock _sync = new();
    private TimeSpan _lastProgress = TimeSpan.MinValue;

    public BackupEngine(IBackupPlatform platform, IFileStateStore store, IRunLog log, Action<BackupProgress>? onProgress = null)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _onProgress = onProgress;
    }

    /// <summary>Runs the task to the end. Never throws for problems of the backup itself: they end up in the result.</summary>
    public BackupResult Run(BackupRequest request, PauseGate pause, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pause);
        var run = new RunContext(request, pause, cancellationToken);
        using (_platform.KeepAwake(Messages.Format(Messages.KeepAwakeReason, request.Task.Name)))
        {
            try
            {
                Execute(run);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                run.Result.Status = RunStatuses.Aborted;
                _log.Write(RunLogLevel.Warning, Messages.RunAborted);
            }
            catch (BackupException ex)
            {
                Fail(run, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fail(run, ex.Message);
            }
            finally
            {
                Cleanup(run);
            }

            // After the shadow copies and network logons are released: a service stopped before starts again now.
            RunPostEvents(run);
        }

        FinishResult(run);
        return run.Result;
    }

    private void Execute(RunContext run)
    {
        var task = run.Task;
        Phase(BackupPhases.Scan);
        CheckSupported(task);
        RunPreEvents(run);
        Phase(BackupPhases.Scan);
        if (IsArchive(task))
        {
            PrepareArchive(run);
        }

        PrepareTargets(run);
        if (run.ActiveTargets.Count == 0)
        {
            throw new BackupException(run.Result.ErrorMessage ?? Messages.NoDestinationAvailable);
        }

        DecideBackupType(run);
        _progress.BackupType = run.BackupType;
        run.Result.BackupType = run.BackupType;
        _log.Write(RunLogLevel.Info, Messages.Format(Messages.RunStarted, task.Name, Words.BackupType(run.BackupType), Words.Trigger(run.Request.Trigger, run.Request.ScheduledFor, null)));

        CreateSnapshots(run);
        run.Pause.WaitWhilePaused(run.Cancellation);
        ScanSources(run);
        SelectFiles(run);

        if (run.ToCopy.Count == 0 && run.BackupType is not (BackupTypes.Full or BackupTypes.Mirror))
        {
            run.Result.NothingToDo = true;
            _log.Write(RunLogLevel.Info, Messages.NothingChanged);
            return;
        }

        CheckFreeSpace(run);
        if (run.ActiveTargets.Count == 0)
        {
            return;
        }

        Phase(BackupPhases.Copy);
        run.VerifyNew = VerifyDue(run);
        if (IsArchive(task))
        {
            WriteArchive(run);
        }
        else
        {
            OpenSets(run);
            CopyFiles(run);
            if (run.BackupType == BackupTypes.Mirror)
            {
                DeleteFromMirror(run);
            }

            FinishSets(run);
        }

        if (!run.SetsFinished)
        {
            return;
        }

        Commit(run);
        ApplyRetention(run);
        CleanMirrorBins(run);
        VerifyStored(run);
    }

    // ---- Preparation -------------------------------------------------------------------------------------------

    private static void CheckSupported(TaskDefinition task)
    {
        if (task.BackupType == BackupTypes.Mirror && IsArchive(task))
        {
            throw new BackupException(Messages.MirrorNeedsPlainFiles);
        }

        if (task.BackupType is not (BackupTypes.Full or BackupTypes.Incremental or BackupTypes.Differential or BackupTypes.Mirror))
        {
            throw new BackupException(Messages.Format(Messages.BackupTypeNotSupported, task.BackupType));
        }

        if (task.Archive.Format is not (ArchiveFormats.None or ArchiveFormats.Zip or ArchiveFormats.SevenZip))
        {
            throw new BackupException(Messages.Format(Messages.ArchiveFormatUnknown, task.Archive.Format));
        }

        if (task.Sources.Count == 0)
        {
            throw new BackupException(Messages.NoSources);
        }

        if (task.Destinations.Count == 0)
        {
            throw new BackupException(Messages.NoDestinations);
        }
    }

    private void PrepareTargets(RunContext run)
    {
        foreach (var destination in run.Task.Destinations)
        {
            var target = new Target(destination, DescribeDestination(destination));
            run.Targets.Add(target);
            _progress.Destinations.Add(target.Progress);
            try
            {
                if (!DestinationKinds.IsSupported(destination.Kind))
                {
                    throw new BackupException(Messages.Format(Messages.DestinationKindNotSupported, destination.Kind));
                }

                target.Root = ResolveRoot(destination);
                if (run.Task.BackupType == BackupTypes.Mirror)
                {
                    CheckMirrorRoot(run.Task, target.Root);
                }

                target.Session = Session(destination.Kind == DestinationKinds.Unc ? destination.CredentialId : null);
                target.Session.Run(() =>
                {
                    Directory.CreateDirectory(target.Root);
                    if (run.Access is { } access)
                    {
                        // Archives always carry their date: every run is a set of its own.
                        target.Sets = SetCatalog.Scan(target.Root, run.Task.Id, access.WithSession(target.Session), (path, reason) =>
                            _log.Write(RunLogLevel.Detail, Messages.Format(Messages.ArchiveSkipped, path, reason)));
                    }
                    else if (run.Task.BackupType == BackupTypes.Mirror)
                    {
                        ReadMirror(run, target);
                    }
                    else if (run.Task.Layout.SeparateFolders)
                    {
                        target.Sets = SetCatalog.Scan(target.Root, run.Task.Id);
                    }
                    else
                    {
                        target.SharedManifest = SetCatalog.TryReadHeader(Path.Combine(target.Root, SetManifest.FileName));
                    }
                });
                string where = string.Equals(target.Label, target.Root, StringComparison.OrdinalIgnoreCase) ? target.Root : $"{target.Label} ({target.Root})";
                _log.Write(RunLogLevel.Info, Messages.Format(Messages.DestinationReady, where, AccountText(target.Session)));
            }
            catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                FailTarget(run, target, Messages.Format(Messages.DestinationUnavailable, target.Label, ex.Message));
            }
        }
    }

    private string ResolveRoot(TaskDestination destination) => ResolveRoot(_platform, destination);

    /// <summary>The folder of a destination: its path, or for a USB disk the folder on the disk with that label.</summary>
    /// <exception cref="BackupException">The disk is not connected or the path is not absolute.</exception>
    public static string ResolveRoot(IBackupPlatform platform, TaskDestination destination)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Kind == DestinationKinds.Usb)
        {
            string label = destination.UsbLabel ?? string.Empty;
            string volume = platform.FindVolumeByLabel(label)
                ?? throw new BackupException(Messages.Format(Messages.UsbDiskNotFound, label));
            return Path.Combine(volume, DestinationKinds.UsbFolder(destination.Path));
        }

        if (string.IsNullOrWhiteSpace(destination.Path) || !Path.IsPathFullyQualified(destination.Path))
        {
            throw new BackupException(Messages.Format(Messages.PathNotAbsolute, destination.Path));
        }

        return destination.Path;
    }

    private void DecideBackupType(RunContext run)
    {
        var task = run.Task;
        run.BackupType = task.BackupType;
        run.State = _store.GetBackupState(task.Id);
        if (run.BackupType is BackupTypes.Full or BackupTypes.Mirror)
        {
            return;
        }

        var local = TimeZoneInfo.ConvertTime(run.Request.StartedAt, run.Request.TimeZone).DateTime;
        bool fullToday = run.State.FullAt is { } fullAt && TimeZoneInfo.ConvertTime(fullAt, run.Request.TimeZone).Date == local.Date;
        string? reason = null;
        if (run.State.FullRunId is null)
        {
            reason = Messages.FullBecauseFirst;
        }
        else if (task.ForceFull.EveryRuns > 0 && run.State.RunsSinceFull + 1 >= task.ForceFull.EveryRuns)
        {
            reason = Messages.Format(Messages.FullBecauseEveryRuns, task.ForceFull.EveryRuns);
        }
        else if (task.ForceFull.Weekday is { } weekday && Scheduling.ScheduleCalculator.IsoWeekday(local) == weekday && !fullToday)
        {
            reason = Messages.FullBecauseWeekday;
        }
        else if (task.ForceFull.MonthDay is { } monthDay && !fullToday
            && (local.Day == monthDay || (monthDay == -1 && local.Day == DateTime.DaysInMonth(local.Year, local.Month))))
        {
            reason = Messages.FullBecauseMonthDay;
        }
        else
        {
            foreach (var target in run.ActiveTargets)
            {
                bool hasParent = task.Layout.SeparateFolders || IsArchive(task)
                    ? target.Sets.Any(s => string.Equals(s.Header.RunId, run.State.FullRunId, StringComparison.OrdinalIgnoreCase))
                    : target.SharedManifest is not null;
                if (!hasParent)
                {
                    reason = Messages.Format(Messages.FullBecauseParentMissing, run.State.FullSetName ?? run.State.FullRunId, target.Label);
                    break;
                }
            }
        }

        if (reason is not null)
        {
            run.BackupType = BackupTypes.Full;
            _log.Write(RunLogLevel.Info, reason);
        }
    }

    private void CreateSnapshots(RunContext run)
    {
        if (!run.Task.Vss.Enabled)
        {
            return;
        }

        var local = run.Task.Sources.Select(s => s.Path).Where(p => !IsNetworkPath(p) && Path.IsPathFullyQualified(p)).ToList();
        if (local.Count == 0)
        {
            return;
        }

        Phase(BackupPhases.Vss);
        try
        {
            run.Snapshots = _platform.CreateSnapshots(local);
            _log.Write(RunLogLevel.Info, Messages.VssCreated);
        }
        catch (BackupException ex)
        {
            run.Result.VssFailed = true;
            if (run.Task.Vss.OnFailure == VssFailureActions.Continue)
            {
                run.Result.Warnings++;
                _log.Write(RunLogLevel.Warning, Messages.Format(Messages.VssFailedContinue, ex.Message));
            }
            else
            {
                throw new BackupException(Messages.Format(Messages.VssFailedAbort, ex.Message), ex);
            }
        }
    }

    private void ScanSources(RunContext run)
    {
        Phase(BackupPhases.Scan);
        var filter = new FileFilter(run.Task.Filters);
        var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < run.Task.Sources.Count; i++)
        {
            run.Cancellation.ThrowIfCancellationRequested();
            var source = run.Task.Sources[i];
            var context = new SourceContext(source);
            run.Sources.Add(context);
            try
            {
                context.Session = Session(IsNetworkPath(source.Path) ? source.CredentialId : null);
                string readRoot = run.Snapshots.Map(source.Path);
                context.Scan = context.Session.Run(() => SourceScanner.Scan(source, i, readRoot, filter,
                    (path, reason) =>
                    {
                        run.Result.Warnings++;
                        run.UnreadableFolders++;
                        _log.Write(RunLogLevel.Warning, Messages.Format(Messages.FolderUnreadable, path, reason));
                    },
                    run.Cancellation));
            }
            catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                RecordError(run, Messages.Format(Messages.SourceUnavailable, source.Path, ex.Message));
                continue;
            }

            if (!context.Scan.Found)
            {
                RecordError(run, Messages.Format(Messages.SourceNotFound, source.Path));
                continue;
            }

            context.Target = context.Scan.IsFile
                ? Unique(usedTargets, context.Scan.Files[0].RelativePath)
                : run.Task.Layout.KeepTopDir ? Unique(usedTargets, SourceScanner.TopFolderName(source.Path)) : string.Empty;
            long bytes = context.Scan.Files.Sum(f => f.Size);
            run.Result.SourceFiles += context.Scan.Files.Count;
            run.Result.SourceBytes += bytes;
            _log.Write(RunLogLevel.Info, Messages.Format(Messages.SourceScanned, source.Path, context.Scan.Files.Count, Sizes.Format(bytes)));
        }
    }

    private void SelectFiles(RunContext run)
    {
        run.LastState = _store.LoadFileState(run.Task.Id, baseFull: false);
        var reference = run.BackupType switch
        {
            BackupTypes.Incremental => run.LastState,
            BackupTypes.Differential => _store.LoadFileState(run.Task.Id, baseFull: true),
            _ => null,
        };

        var targets = run.ActiveTargets;
        foreach (var context in run.Sources.Where(s => s.Scan is { Found: true }))
        {
            foreach (var file in context.Scan!.Files)
            {
                bool copy;
                if (run.BackupType == BackupTypes.Mirror)
                {
                    // Each destination gets what it lacks or holds in another version; the file is read once for all.
                    copy = false;
                    string stored = StoredPath(context, file);
                    foreach (var target in targets)
                    {
                        if (!target.Existing.TryGetValue(stored, out var held) || !SameFile(held, file.Size, file.LastWriteUtc))
                        {
                            target.Needed.Add(stored);
                            copy = true;
                        }
                    }
                }
                else
                {
                    copy = reference is null || !reference.TryGetValue(file.OriginalPath, out var known) || known != file.Stamp;
                }

                if (copy)
                {
                    run.ToCopy.Add((context, file));
                    run.BytesToCopy += file.Size;
                }
            }
        }

        _progress.FilesTotal = run.ToCopy.Count;
        _progress.BytesTotal = run.BytesToCopy;
        _log.Write(RunLogLevel.Info, Messages.Format(Messages.CopyPlanned, run.ToCopy.Count, Sizes.Format(run.BytesToCopy)));
        Report(force: true);
    }

    private void CheckFreeSpace(RunContext run)
    {
        // An archive needs about as much as the last one did per source byte (section 6.3).
        double ratio = IsArchive(run.Task) ? Math.Clamp(run.State.CompressionRatio ?? 1.0, 0.05, 1.1) : 1.0;
        long needed = (long)(run.BytesToCopy * ratio) + (1024 * 1024);
        foreach (var target in run.ActiveTargets)
        {
            long? free = target.Session.Run(() => _platform.GetFreeSpace(target.Root));
            if (free is { } available && available < needed)
            {
                run.Result.OutOfSpace = true;
                FailTarget(run, target, Messages.Format(Messages.NotEnoughSpace, target.Label, Sizes.Format(needed), Sizes.Format(available)));
            }
        }
    }

    // ---- Writing -----------------------------------------------------------------------------------------------

    private void OpenSets(RunContext run)
    {
        var task = run.Task;
        var startedLocal = TimeZoneInfo.ConvertTime(run.Request.StartedAt, run.Request.TimeZone).DateTime;
        run.SetName = SetNaming.Expand(task.Layout.NameTemplate, task.Name, startedLocal, _platform.ComputerName, run.BackupType);
        foreach (var target in run.ActiveTargets)
        {
            try
            {
                target.Session.Run(() =>
                {
                    if (SeparateSets(task))
                    {
                        target.SetName = SetNaming.MakeUnique(target.Root, run.SetName);
                        target.SetFolder = Path.Combine(target.Root, target.SetName);
                        target.WriteFolder = target.SetFolder + SetCatalog.PartialSuffix;
                        Directory.CreateDirectory(target.WriteFolder);
                    }
                    else
                    {
                        target.SetName = run.SetName;
                        target.SetFolder = target.Root;
                        target.WriteFolder = target.Root;
                    }

                    if (run.BackupType is BackupTypes.Full or BackupTypes.Mirror && !task.Layout.DeleteEmptyDirs)
                    {
                        // A full copy and a mirror reproduce the folder tree, empty folders included.
                        foreach (var context in run.Sources.Where(s => s.Scan is { Found: true, IsFile: false }))
                        {
                            foreach (string folder in context.Scan!.Folders)
                            {
                                CreateFolder(target, Path.Combine(target.WriteFolder, context.Target, folder));
                            }
                        }
                    }
                });
                target.Progress.State = DestinationStates.Writing;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FailTarget(run, target, Messages.Format(Messages.DestinationWriteFailed, target.Label, ex.Message));
            }
        }
    }

    private void CopyFiles(RunContext run)
    {
        var buffer = new byte[BufferSize];
        foreach (var (context, file) in run.ToCopy)
        {
            run.Pause.WaitWhilePaused(run.Cancellation);
            run.Cancellation.ThrowIfCancellationRequested();
            if (run.ActiveTargets.Count == 0)
            {
                break;
            }

            _progress.CurrentFile = file.OriginalPath;
            Report(force: false);
            if (CopyFile(run, context, file, StoredPath(context, file), buffer) is { } hash)
            {
                run.Copied[file.OriginalPath] = hash;
                run.Result.FilesProcessed++;
                run.Result.BytesWritten += file.Size;
                run.StoredBytes += file.Size;
                if (run.Request.LogEveryFile)
                {
                    _log.Write(RunLogLevel.Detail, Messages.Format(Messages.FileCopied, file.OriginalPath, Sizes.Format(file.Size)));
                }
            }

            _progress.FilesDone++;
        }

        _progress.CurrentFile = null;
        Report(force: true);
    }

    /// <summary>Copies one file to every active destination. Returns its SHA-256, or null if it was not copied.</summary>
    private string? CopyFile(RunContext run, SourceContext context, ScannedFile file, string targetPath, byte[] buffer)
    {
        int attempts = 1 + Math.Clamp(run.Task.Copy.ReadRetries, 0, 5);
        long progressAtStart = _progress.BytesDone;
        for (int attempt = 1; ; attempt++)
        {
            _progress.BytesDone = progressAtStart;
            var outputs = new List<Output>();
            try
            {
                using var input = context.Session.Run(() => new FileStream(
                    file.ReadPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan));
                OpenOutputs(run, targetPath, outputs);
                if (outputs.Count == 0)
                {
                    return null;
                }

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                while (true)
                {
                    run.Pause.WaitWhilePaused(run.Cancellation);
                    run.Cancellation.ThrowIfCancellationRequested();
                    int read = ReadSource(input, buffer);
                    if (read == 0)
                    {
                        break;
                    }

                    hash.AppendData(buffer, 0, read);
                    WriteOutputs(run, outputs, buffer, read);
                    if (outputs.Count == 0)
                    {
                        return null;
                    }

                    _progress.BytesDone += read;
                    _speed.Add(read);
                    Report(force: false);
                }

                CompleteOutputs(run, outputs, file);
                return outputs.Count == 0 ? null : SetManifest.ToHex(hash.GetHashAndReset());
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException && outputs.Count == 0)
            {
                // Deleted after the scan: not a problem, it is simply no longer part of the source.
                run.Vanished.Add(file.OriginalPath);
                _log.Write(RunLogLevel.Detail, Messages.Format(Messages.FileVanished, file.OriginalPath));
                return null;
            }
            catch (SourceReadException ex) when (attempt < attempts)
            {
                AbandonOutputs(outputs);
                _log.Write(RunLogLevel.Detail, Messages.Format(Messages.FileRetry, file.OriginalPath, ex.Message));
                Delay(run);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && ex is not SourceReadException && attempt < attempts && outputs.Count == 0)
            {
                // Opening the source failed: locked or gone for a moment.
                _log.Write(RunLogLevel.Detail, Messages.Format(Messages.FileRetry, file.OriginalPath, ex.Message));
                Delay(run);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AbandonOutputs(outputs);
                _progress.BytesDone = progressAtStart;
                run.Result.Warnings++;
                run.FailedFiles++;
                _log.Write(RunLogLevel.Warning, Messages.Format(Messages.FileFailed, file.OriginalPath, ex.Message));
                return null;
            }
            catch
            {
                AbandonOutputs(outputs);
                throw;
            }
        }
    }

    private void OpenOutputs(RunContext run, string targetPath, List<Output> outputs)
    {
        foreach (var target in run.ActiveTargets)
        {
            if (run.BackupType == BackupTypes.Mirror && !target.Needed.Contains(targetPath))
            {
                // This mirror holds the file already.
                continue;
            }

            string final = Path.Combine(target.WriteFolder, targetPath);
            string temp = final + TempSuffix;
            try
            {
                var stream = target.Session.Run(() =>
                {
                    CreateFolder(target, Path.GetDirectoryName(final)!);
                    return new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1);
                });
                outputs.Add(new Output(target, stream, temp, final));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FailTarget(run, target, Messages.Format(Messages.DestinationWriteFailed, target.Label, ex.Message));
            }
        }
    }

    private void WriteOutputs(RunContext run, List<Output> outputs, byte[] buffer, int count)
    {
        for (int i = outputs.Count - 1; i >= 0; i--)
        {
            var output = outputs[i];
            try
            {
                output.Stream.Write(buffer, 0, count);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outputs.RemoveAt(i);
                Abandon(output);
                FailTarget(run, output.Target, Messages.Format(Messages.DestinationWriteFailed, output.Target.Label, ex.Message));
            }
        }
    }

    private void CompleteOutputs(RunContext run, List<Output> outputs, ScannedFile file)
    {
        for (int i = outputs.Count - 1; i >= 0; i--)
        {
            var output = outputs[i];
            try
            {
                output.Stream.Dispose();
                output.Target.Session.Run(() =>
                {
                    if (File.Exists(output.Final))
                    {
                        if (IsSafeMirror(run))
                        {
                            // A safe mirror keeps the version it replaces (section 6.1).
                            MoveToBin(run, output.Target, output.Final);
                        }
                        else
                        {
                            File.SetAttributes(output.Final, FileAttributes.Normal);
                        }
                    }

                    File.Move(output.Temp, output.Final, overwrite: true);
                    ApplyMetadata(output.Final, file);
                });
                output.Target.Files++;
                output.Target.Bytes += file.Size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outputs.RemoveAt(i);
                Abandon(output);
                FailTarget(run, output.Target, Messages.Format(Messages.DestinationWriteFailed, output.Target.Label, ex.Message));
            }
        }
    }

    private static void ApplyMetadata(string path, ScannedFile file)
    {
        File.SetCreationTimeUtc(path, file.CreationUtc);
        File.SetLastWriteTimeUtc(path, file.LastWriteUtc);
        var kept = file.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive);
        if (kept != 0 && kept != FileAttributes.Archive)
        {
            File.SetAttributes(path, kept);
        }
    }

    private static int ReadSource(FileStream input, byte[] buffer)
    {
        try
        {
            return input.Read(buffer, 0, buffer.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SourceReadException(ex.Message, ex);
        }
    }

    private static void AbandonOutputs(List<Output> outputs)
    {
        foreach (var output in outputs)
        {
            Abandon(output);
        }

        outputs.Clear();
    }

    private static void Abandon(Output output)
    {
        try
        {
            output.Stream.Dispose();
            output.Target.Session.Run(() => File.Delete(output.Temp));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The destination is gone; the temporary file never looks like a finished copy anyway.
        }
    }

    private void FinishSets(RunContext run)
    {
        // A set missing a whole source is not a backup of the task. A destination that failed does not stop the
        // others from finishing theirs (section 6.3), but then the run is an error and nothing is committed.
        if (run.SourceErrors > 0)
        {
            return;
        }

        var finished = DateTimeOffset.UtcNow;
        string status = run.Result.Warnings > 0 ? RunStatuses.Warning : RunStatuses.Success;

        if (run.VerifyNew)
        {
            Phase(BackupPhases.Verify);
        }

        foreach (var target in run.ActiveTargets)
        {
            var files = run.BackupType == BackupTypes.Mirror ? MirrorFiles(run, target) : null;
            var header = BuildHeader(run, target.SetName, finished, status);
            if (files is not null)
            {
                header.SetFileCount = files.Count(f => f.InSet);
                header.SetBytes = files.Where(f => f.InSet).Sum(f => f.Size);
            }

            try
            {
                if (run.VerifyNew)
                {
                    VerifyNewFolder(run, target);
                }

                target.Session.Run(() =>
                {
                    string manifest = Path.Combine(target.WriteFolder, SetManifest.FileName);
                    string temp = manifest + TempSuffix;
                    using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
                    {
                        SetManifest.Write(stream, header, files ?? ManifestFiles(run));
                        stream.Flush(flushToDisk: true);
                    }

                    File.Move(temp, manifest, overwrite: true);
                    if (!string.Equals(target.WriteFolder, target.SetFolder, StringComparison.Ordinal))
                    {
                        Directory.Move(target.WriteFolder, target.SetFolder);
                    }

                    if (run.Task.Layout.DeleteEmptyDirs && !SeparateSets(run.Task))
                    {
                        RemoveEmptyFolders(target.SetFolder);
                    }
                });
                target.Completed = true;
                target.NewSet = new StoredSet(target.SetFolder, header);
                target.Progress.State = DestinationStates.Done;
                _log.Write(RunLogLevel.Info, Messages.Format(Messages.SetWritten, target.SetName, target.Label, target.Files, Sizes.Format(target.Bytes)));
            }
            catch (BackupException ex)
            {
                FailTarget(run, target, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FailTarget(run, target, Messages.Format(Messages.DestinationWriteFailed, target.Label, ex.Message));
            }
        }

        run.SetsFinished = run.Targets.All(t => t.Completed);
        run.Verified = run.VerifyNew && run.SetsFinished;
    }

    private SetManifestHeader BuildHeader(RunContext run, string setName, DateTimeOffset finished, string status, int pendingFiles = 0, long pendingBytes = 0) => new()
    {
        SetName = setName,
        TaskId = run.Task.Id,
        TaskName = run.Task.Name,
        RunId = run.Request.RunId,
        BackupType = run.BackupType,
        ParentFullRunId = run.BackupType is BackupTypes.Full or BackupTypes.Mirror ? null : run.State.FullRunId,
        ParentFullSetName = run.BackupType is BackupTypes.Full or BackupTypes.Mirror ? null : run.State.FullSetName,
        Computer = _platform.ComputerName,
        StartedAt = run.Request.StartedAt,
        FinishedAt = finished,
        Status = status,
        Sources = [.. run.Sources.Where(s => s.Scan is { Found: true }).Select(s => new ManifestSource { Id = s.Source.Id, Path = s.Source.Path, Target = s.Target })],
        FileCount = run.Result.SourceFiles,
        SetFileCount = run.Copied.Count + pendingFiles,
        SetBytes = run.StoredBytes + pendingBytes,
    };

    /// <param name="pending">Files the archive writer has yet to read (7-Zip packs by name, so the manifest can come
    /// before them): they count as in the set, without a SHA-256.</param>
    private static IEnumerable<ManifestFile> ManifestFiles(RunContext run, HashSet<string>? pending = null)
    {
        foreach (var context in run.Sources.Where(s => s.Scan is { Found: true }))
        {
            foreach (var file in context.Scan!.Files)
            {
                if (run.Vanished.Contains(file.OriginalPath))
                {
                    continue;
                }

                bool inSet = run.Copied.TryGetValue(file.OriginalPath, out string? hash) || pending?.Contains(file.OriginalPath) == true;
                yield return new ManifestFile(StoredPath(context, file), file.Size, file.LastWriteUtc, inSet, hash);
            }
        }
    }

    /// <summary>Where a file goes inside the set: a file source under its (unique) name, a folder source below its
    /// top folder or directly in the set folder.</summary>
    private static string StoredPath(SourceContext context, ScannedFile file) =>
        context.Scan!.IsFile ? context.Target
            : context.Target.Length == 0 ? file.RelativePath
            : Path.Combine(context.Target, file.RelativePath);

    // ---- After a successful run --------------------------------------------------------------------------------

    private void Commit(RunContext run)
    {
        var files = new List<KeyValuePair<string, FileStamp>>();
        var selected = run.ToCopy.Select(c => c.File.OriginalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var context in run.Sources.Where(s => s.Scan is { Found: true }))
        {
            foreach (var file in context.Scan!.Files)
            {
                if (run.Vanished.Contains(file.OriginalPath))
                {
                    continue;
                }

                if (!selected.Contains(file.OriginalPath) || run.Copied.ContainsKey(file.OriginalPath))
                {
                    files.Add(new(file.OriginalPath, file.Stamp));
                }
                else if (run.LastState.TryGetValue(file.OriginalPath, out var previous))
                {
                    // Not copied this time: keep what the last copy had, so the next run tries again.
                    files.Add(new(file.OriginalPath, previous));
                }
            }
        }

        double? ratio = IsArchive(run.Task) && run.StoredBytes > 0 ? (double)run.ArchiveBytes / run.StoredBytes : null;
        _store.CommitRun(run.Task.Id, new CommittedRun(run.Request.RunId, run.BackupType, run.SetName, run.Request.StartedAt, run.Verified, ratio), files);
        run.Committed = true;
    }

    private void ApplyRetention(RunContext run)
    {
        var retention = run.Task.Retention;
        foreach (var target in run.Targets.Where(t => t.Completed && t.NewSet is not null))
        {
            target.Kept = SeparateSets(run.Task) || IsArchive(run.Task) ? [.. target.Sets, target.NewSet!] : [target.NewSet!];
        }

        if (!(SeparateSets(run.Task) || IsArchive(run.Task))
            || retention.Mode is not (RetentionModes.Count or RetentionModes.Age or RetentionModes.Gfs))
        {
            return;
        }

        Phase(BackupPhases.Retention);
        foreach (var target in run.Targets.Where(t => t.Completed && t.NewSet is not null))
        {
            var plan = retention.Mode switch
            {
                RetentionModes.Age => SetCatalog.PlanAgeRetention(target.Kept, retention.MaxAgeDays, run.Request.StartedAt, run.Request.RunId),
                RetentionModes.Gfs => SetCatalog.PlanGfsRetention(target.Kept, retention.Gfs, run.Request.TimeZone, run.Request.RunId),
                _ => SetCatalog.PlanCountRetention(target.Kept, retention.KeepFull, run.Request.RunId),
            };
            foreach (var set in plan)
            {
                run.Cancellation.ThrowIfCancellationRequested();
                try
                {
                    target.Session.Run(() => SetCatalog.DeleteSet(set));
                    target.Kept.Remove(set);
                    run.Result.DeletedSets++;
                    _log.Write(RunLogLevel.Info, Messages.Format(Messages.SetDeleted, set.Header.SetName, target.Label));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    run.Result.Warnings++;
                    _log.Write(RunLogLevel.Warning, Messages.Format(Messages.SetDeleteFailed, set.Header.SetName, target.Label, ex.Message));
                }
            }
        }
    }

    // ---- Finishing ---------------------------------------------------------------------------------------------

    private void Cleanup(RunContext run)
    {
        foreach (var target in run.Targets.Where(t => !t.Completed))
        {
            RemoveArchiveLeftovers(target);
        }

        foreach (var target in run.Targets.Where(t => !t.Completed && t.WriteFolder.Length > 0
            && !string.Equals(t.WriteFolder, t.SetFolder, StringComparison.Ordinal)))
        {
            // Our own unfinished set folder: remove it, it is not a backup.
            try
            {
                target.Session.Run(() => SetCatalog.DeleteSetFolder(target.WriteFolder));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Write(RunLogLevel.Warning, Messages.Format(Messages.PartialNotRemoved, target.WriteFolder, ex.Message));
            }
        }

        run.Snapshots.Dispose();
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }

    private static void FinishResult(RunContext run)
    {
        var result = run.Result;
        if (result.Status != RunStatuses.Aborted)
        {
            result.Status = result.Status == RunStatuses.Error ? RunStatuses.Error : ComputeStatus(run);
            if (RunStatuses.IsSuccessful(result.Status) && !result.NothingToDo && !run.SetsFinished)
            {
                result.Status = RunStatuses.Error;
            }
        }

        result.SetName = run.SetName;
        foreach (var target in run.Targets)
        {
            result.Destinations.Add(new DestinationResult
            {
                DestinationId = target.Destination.Id,
                Status = target.Completed || (result.NothingToDo && !target.Failed) ? RunStatuses.Success : RunStatuses.Error,
                SetName = target.Completed ? target.SetName : null,
                Bytes = target.Bytes,
                Error = target.Error,
            });
        }

        if (result.Status == RunStatuses.Error && result.ErrorMessage is null)
        {
            result.ErrorMessage = run.Targets.FirstOrDefault(t => t.Error is not null)?.Error ?? Messages.CopyIncomplete;
        }
    }

    private static string ComputeStatus(RunContext run)
    {
        if (run.Result.Errors > 0 || run.Targets.Any(t => t.Failed))
        {
            return RunStatuses.Error;
        }

        return run.Result.Warnings > 0 ? RunStatuses.Warning : RunStatuses.Success;
    }

    private void Fail(RunContext run, string message)
    {
        run.Result.Status = RunStatuses.Error;
        run.Result.Errors++;
        run.Result.ErrorMessage ??= message;
        _log.Write(RunLogLevel.Error, message);
    }

    private void RecordError(RunContext run, string message)
    {
        run.SourceErrors++;
        run.Result.Errors++;
        run.Result.ErrorMessage ??= message;
        _log.Write(RunLogLevel.Error, message);
    }

    private void FailTarget(RunContext run, Target target, string message)
    {
        lock (_sync)
        {
            if (target.Failed)
            {
                return;
            }

            target.Failed = true;
            target.Error = message;
            run.Result.Errors++;
            target.Progress.State = DestinationStates.Failed;
            target.Progress.Error = message;
            run.Result.ErrorMessage ??= message;
            _log.Write(RunLogLevel.Error, message);
            Report(force: true);
        }
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------

    private INetworkSession Session(string? credentialId)
    {
        if (credentialId is null)
        {
            return ProcessSession.Instance;
        }

        if (!_sessions.TryGetValue(credentialId, out var session))
        {
            session = _platform.OpenSession(credentialId);
            _sessions.Add(credentialId, session);
        }

        return session;
    }

    private static void CreateFolder(Target target, string folder)
    {
        if (target.CreatedFolders.Add(folder))
        {
            Directory.CreateDirectory(folder);
        }
    }

    private static void RemoveEmptyFolders(string root)
    {
        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            RemoveEmptyFolders(folder);
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
    }

    private static void Delay(RunContext run)
    {
        if (run.Cancellation.WaitHandle.WaitOne(RetryDelay))
        {
            run.Cancellation.ThrowIfCancellationRequested();
        }
    }

    private void Phase(string phase)
    {
        _progress.Phase = phase;
        Report(force: true);
    }

    private void Report(bool force)
    {
        if (_onProgress is null)
        {
            return;
        }

        lock (_sync)
        {
            var now = _progressClock.Elapsed;
            if (!force && now - _lastProgress < ProgressInterval)
            {
                return;
            }

            _lastProgress = now;
            _progress.SpeedBps = _speed.BytesPerSecond;
            long remaining = Math.Max(0, _progress.BytesTotal - _progress.BytesDone);
            _progress.EtaS = _progress.SpeedBps > 0 && _progress.Phase == BackupPhases.Copy ? remaining / _progress.SpeedBps : null;
            _onProgress(_progress.Copy());
        }
    }

    private static string DescribeDestination(TaskDestination destination) => destination.Kind == DestinationKinds.Usb
        ? Messages.Format(Messages.UsbDiskLabel, destination.UsbLabel)
        : destination.Path;

    private static string AccountText(INetworkSession session) =>
        session.Account is { } account ? Messages.Format(Messages.AsAccount, account) : string.Empty;

    private static string Unique(HashSet<string> used, string name)
    {
        string candidate = name;
        for (int i = 2; !used.Add(candidate); i++)
        {
            string extension = Path.GetExtension(name);
            candidate = string.Concat(Path.GetFileNameWithoutExtension(name), " (", i.ToString(System.Globalization.CultureInfo.InvariantCulture), ")", extension);
        }

        return candidate;
    }

    /// <summary>A UNC path (<c>\\server\share</c>): read through a network logon, never through VSS.</summary>
    public static bool IsNetworkPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal);

    // ---- Types -------------------------------------------------------------------------------------------------

    private sealed class RunContext(BackupRequest request, PauseGate pause, CancellationToken cancellation)
    {
        public BackupRequest Request { get; } = request;

        public TaskDefinition Task => Request.Task;

        public PauseGate Pause { get; } = pause;

        public CancellationToken Cancellation { get; } = cancellation;

        public BackupResult Result { get; } = new() { BackupType = request.Task.BackupType };

        public string BackupType { get; set; } = request.Task.BackupType;

        public TaskBackupState State { get; set; } = TaskBackupState.Empty;

        public ISnapshotSet Snapshots { get; set; } = NoSnapshots.Instance;

        public List<Target> Targets { get; } = [];

        public List<Target> ActiveTargets => [.. Targets.Where(t => !t.Failed)];

        public List<SourceContext> Sources { get; } = [];

        public Dictionary<string, FileStamp> LastState { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(SourceContext Context, ScannedFile File)> ToCopy { get; } = [];

        public long BytesToCopy { get; set; }

        /// <summary>Copied files: original path → SHA-256.</summary>
        public Dictionary<string, string> Copied { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int FailedFiles { get; set; }

        /// <summary>Sources that were missing or could not be read at all.</summary>
        public int SourceErrors { get; set; }

        /// <summary>The events before were started, so the events after run too.</summary>
        public bool EventsStarted { get; set; }

        /// <summary>Folders of the sources that could not be read: their files look deleted, but are not.</summary>
        public int UnreadableFolders { get; set; }

        /// <summary>Files deleted between the scan and the copy.</summary>
        public HashSet<string> Vanished { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Files that could not be read (archives only; the copy counts them in <see cref="FailedFiles"/>).</summary>
        public HashSet<string> Failed { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? SetName { get; set; }

        public bool SetsFinished { get; set; }

        public bool Committed { get; set; }

        /// <summary>Source bytes stored in the set (plain or packed).</summary>
        public long StoredBytes { get; set; }

        // ---- Archives

        public ArchiveAccess? Access { get; set; }

        public IArchiveWriter? Writer { get; set; }

        /// <summary>The manifest packed into the archive; set when the writer gets to it.</summary>
        public SetManifestHeader? Header { get; set; }

        /// <summary>Size of the archive (all volumes).</summary>
        public long ArchiveBytes { get; set; }

        /// <summary>This run verifies its new copy (section 6.5).</summary>
        public bool VerifyNew { get; set; }

        /// <summary>The new copy was verified in every destination.</summary>
        public bool Verified { get; set; }
    }

    private sealed class SourceContext(TaskSource source)
    {
        public TaskSource Source { get; } = source;

        public INetworkSession Session { get; set; } = ProcessSession.Instance;

        public SourceScan? Scan { get; set; }

        /// <summary>Folder (or file name) of this source inside the set; "" = the set folder itself.</summary>
        public string Target { get; set; } = string.Empty;
    }

    private sealed class Target(TaskDestination destination, string label)
    {
        public TaskDestination Destination { get; } = destination;

        public string Label { get; } = label;

        public DestinationProgress Progress { get; } = new() { DestinationId = destination.Id, Label = label };

        public INetworkSession Session { get; set; } = ProcessSession.Instance;

        public string Root { get; set; } = string.Empty;

        public List<StoredSet> Sets { get; set; } = [];

        public SetManifestHeader? SharedManifest { get; set; }

        public string SetName { get; set; } = string.Empty;

        public string SetFolder { get; set; } = string.Empty;

        public string WriteFolder { get; set; } = string.Empty;

        public HashSet<string> CreatedFolders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Failed { get; set; }

        public string? Error { get; set; }

        public bool Completed { get; set; }

        public StoredSet? NewSet { get; set; }

        /// <summary>Sets of the task in this destination after retention, the new one included.</summary>
        public List<StoredSet> Kept { get; set; } = [];

        public int Files { get; set; }

        public long Bytes { get; set; }

        // ---- Archives

        /// <summary>The archive being written here (a file or the volumes).</summary>
        public Stream? Output { get; set; }

        /// <summary>Final names of the archive files, in order.</summary>
        public List<string> Parts { get; } = [];

        /// <summary>The names they are written under (<see cref="Parts"/> + ".partial").</summary>
        public List<string> Temps { get; } = [];

        /// <summary>Files already given their final name.</summary>
        public List<string> Renamed { get; } = [];

        // ---- Mirror

        /// <summary>The files in the mirror before the run: path in the mirror → size and time.</summary>
        public Dictionary<string, MirrorEntry> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The files the last run put into the mirror (its manifest): only these are ever deleted.</summary>
        public Dictionary<string, ManifestFile> Previous { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Files this mirror lacks or holds in another version.</summary>
        public HashSet<string> Needed { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Where a safe mirror puts what this run replaces or deletes; made when first needed.</summary>
        public string? Bin { get; set; }

        public int MirrorRemoved { get; set; }
    }

    private readonly record struct MirrorEntry(long Size, DateTime ModifiedUtc);

    private sealed record Output(Target Target, FileStream Stream, string Temp, string Final);

    private sealed class SourceReadException(string message, Exception inner) : IOException(message, inner);

    /// <summary>Speed over the last few seconds.</summary>
    private sealed class SpeedMeter
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Queue<(TimeSpan Time, long Bytes)> _samples = new();
        private long _total;

        public long BytesPerSecond
        {
            get
            {
                Trim();
                if (_samples.Count < 2)
                {
                    return 0;
                }

                var first = _samples.Peek();
                double seconds = (_clock.Elapsed - first.Time).TotalSeconds;
                return seconds <= 0.2 ? 0 : (long)((_total - first.Bytes) / seconds);
            }
        }

        public void Add(long bytes)
        {
            _total += bytes;
            _samples.Enqueue((_clock.Elapsed, _total));
            Trim();
        }

        private void Trim()
        {
            var limit = _clock.Elapsed - TimeSpan.FromSeconds(5);
            while (_samples.Count > 2 && _samples.Peek().Time < limit)
            {
                _samples.Dequeue();
            }
        }
    }
}
