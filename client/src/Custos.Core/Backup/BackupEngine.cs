using System.Diagnostics;
using System.Security.Cryptography;
using Custos.Core.Archives;
using Custos.Core.Destinations;
using Custos.Core.Model;
using Custos.Core.Text;

namespace Custos.Core.Backup;

/// <summary>
/// Performs one run of a task: full, incremental or differential copy or a mirror into one or more destinations (a
/// folder, a share, a USB disk, an FTP, SFTP or WebDAV server, an S3 bucket), as plain files or as a 7z or ZIP archive, with change tracking, set manifests, verification and
/// retention (sections 6.1–6.8 and 7 of the client spec).
/// </summary>
/// <remarks>
/// Every file is read once and written to all destinations at the same time; a destination that fails is dropped,
/// the others go on. A copy is never left looking finished when it is not: files are written under a temporary name
/// and renamed when complete, a set folder or archive carries ".partial" until it is complete (in S3, where nothing
/// can be renamed, a set is complete when its manifest, written last, is there). The database and the destinations
/// are only cleaned up after a successful run. The destinations are reached only through
/// <see cref="DestinationStore"/>, so all of this is the same for every kind of them.
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
    private bool _lowIo;

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
        _lowIo = request.LowPriority;
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
        run.Variables = PathVariables.For(_platform, run.Task, TimeZoneInfo.ConvertTime(run.Request.StartedAt, run.Request.TimeZone).DateTime);
        run.Task = run.Variables.ExpandPaths(run.Task);
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

        if (IsRemote(task))
        {
            PrepareRemote(run);
        }
        else
        {
            if (HasDatabases(task))
            {
                PlanDatabases(run);
            }

            CreateSnapshots(run);
            run.Pause.WaitWhilePaused(run.Cancellation);
            ScanSources(run);
            SelectFiles(run);
        }

        if (run.ToCopy.Count == 0 && run.BackupType is not (BackupTypes.Full or BackupTypes.Mirror) && !DumpsToTake(run).Any()
            && run.Remote is not { Planned.Count: > 0 })
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
            CheckSnapshots(run);
        }
        else
        {
            OpenSets(run);
            WriteDumps(run);
            CopyFiles(run);
            if (run.Remote is not null)
            {
                WriteRemoteTar(run);
            }

            CopyFolderSecurity(run);
            if (run.BackupType == BackupTypes.Mirror)
            {
                DeleteFromMirror(run);
            }

            CheckSnapshots(run);
            FinishSets(run);
        }

        if (!run.SetsFinished)
        {
            return;
        }

        if (run.DbErrors > 0 || run.RemoteErrors > 0)
        {
            // The set stays, marked as an error; the next run takes the changes again and nothing is deleted.
            _log.Write(RunLogLevel.Warning, run.DbErrors > 0 ? Messages.DbSetKeptNotCommitted : Messages.RemoteSetKeptNotCommitted);
            return;
        }

        Commit(run);
        ApplyRetention(run);
        RemoveLeftovers(run);
        CheckProtection(run);
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

        if (task.Sources.Count == 0 && !HasDatabases(task))
        {
            throw new BackupException(Messages.NoSources);
        }

        if (IsRemote(task))
        {
            CheckRemoteSupported(task);
        }

        if (task.Destinations.Count == 0)
        {
            throw new BackupException(Messages.NoDestinations);
        }

        foreach (var destination in task.Destinations)
        {
            if (task.Archive.Format == ArchiveFormats.SevenZip && DestinationKinds.IsSupported(destination.Kind) && !DestinationKinds.CanSeek(destination.Kind))
            {
                // 7z goes back to the start of the archive when it is complete (section 6.5).
                throw new BackupException(Messages.Format(Messages.SevenZipNeedsSeek, DestinationStores.Label(destination)));
            }

            if (DestinationKinds.IsCloudDrive(destination.Kind) && task.BackupType == BackupTypes.Mirror)
            {
                // A mirror compares and replaces file by file: too many requests for a cloud drive (section 6.3).
                throw new BackupException(Messages.Format(Messages.CloudNoMirror, DestinationStores.Label(destination)));
            }

            if (destination.ProtectsFiles && (task.BackupType == BackupTypes.Mirror || !(SeparateSets(task) || IsArchive(task))))
            {
                // A locked file cannot be replaced, and these two ways of copying replace files (section 6.3).
                throw new BackupException(ProtectionNeedsSets(destination, DestinationStores.Label(destination)));
            }
        }
    }

    /// <summary>Why a destination that protects its files takes only archives and separate set folders.</summary>
    public static string ProtectionNeedsSets(TaskDestination destination, string label)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return WormSettings.Of(destination) is { } worm
            ? Messages.Format(Messages.WormNeedsSets, label, worm.Days)
            : Messages.Format(Messages.ObjectLockNeedsSets, label);
    }

    private void PrepareTargets(RunContext run)
    {
        foreach (var destination in run.Task.Destinations)
        {
            var target = new Target(destination, DestinationStores.Label(destination));
            run.Targets.Add(target);
            _progress.Destinations.Add(target.Progress);
            try
            {
                if (!DestinationKinds.IsSupported(destination.Kind))
                {
                    throw new BackupException(CustosEdition.DestinationNotSupported(destination.Kind));
                }

                var store = _platform.OpenStore(destination, destination.Kind == DestinationKinds.Unc ? Session(destination.CredentialId) : null);
                store.Cancellation = run.Cancellation;
                store.LowPriority = _lowIo;
                target.Store = store;
                target.Root = store.Root;
                target.SecurityFailed = !store.KeepsSecurity;
                if (run.Task.BackupType == BackupTypes.Mirror && DestinationKinds.IsFileSystem(destination.Kind))
                {
                    CheckMirrorRoot(run.Task, target.Root);
                }
                else if (CustosEdition.IsLinux && store is LocalStore && !IsRemote(run.Task))
                {
                    CheckLinuxDestinationRoot(run.Task, target.Root);
                }

                store.CreateFolder(target.Root);
                store.CheckReady();
                if (run.Access is { } access)
                {
                    // Archives always carry their date: every run is a set of its own.
                    target.Sets = SetCatalog.Scan(store, target.Root, run.Task.Id, access.WithStore(store), (path, reason) =>
                        _log.Write(RunLogLevel.Detail, Messages.Format(Messages.ArchiveSkipped, path, reason)));
                }
                else if (run.Task.BackupType == BackupTypes.Mirror)
                {
                    ReadMirror(run, target);
                }
                else if (SeparateSets(run.Task))
                {
                    target.Sets = SetCatalog.Scan(store, target.Root, run.Task.Id);
                }
                else
                {
                    target.SharedManifest = SetCatalog.TryReadHeader(store, store.Combine(target.Root, SetManifest.FileName));
                }

                string where = string.Equals(target.Label, target.Root, StringComparison.OrdinalIgnoreCase) || !DestinationKinds.IsFileSystem(destination.Kind)
                    ? target.Label
                    : $"{target.Label} ({target.Root})";
                _log.Write(RunLogLevel.Info, Messages.Format(Messages.DestinationReady, where, AccountText(store.Account)));
            }
            catch (Exception ex) when (ex is BackupException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                FailTarget(run, target, Messages.Format(Messages.DestinationUnavailable, target.Label, ex.Message));
            }
        }
    }

    /// <summary>The folder of a local destination: its path, or for a USB disk the folder on the disk with that label.</summary>
    /// <exception cref="BackupException">The disk is not connected or the path is not absolute.</exception>
    public static string ResolveRoot(IBackupPlatform platform, TaskDestination destination) => DestinationStores.ResolveRoot(platform, destination);

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
        else if (task.ForceFull.MonthDay is { } monthDay && !fullToday && Scheduling.ScheduleCalculator.IsMonthDay(monthDay, local))
        {
            reason = Messages.FullBecauseMonthDay;
        }
        else if (GfsWantsFull(run, local))
        {
            reason = Messages.FullBecauseGfsWeek;
        }
        else
        {
            foreach (var target in run.ActiveTargets)
            {
                bool hasParent = SeparateSets(task) || IsArchive(task)
                    ? target.Sets.Any(s => string.Equals(s.Header.RunId, run.State.FullRunId, StringComparison.OrdinalIgnoreCase))
                    : target.SharedManifest is not null;
                if (!hasParent)
                {
                    reason = Messages.Format(Messages.FullBecauseParentMissing, run.State.FullSetName ?? run.State.FullRunId, target.Label);
                    break;
                }
            }

            if (reason is null && IsRemote(task))
            {
                reason = RemoteBaseMissing(run);
            }
        }

        if (reason is not null)
        {
            run.BackupType = BackupTypes.Full;
            _log.Write(RunLogLevel.Info, reason);
        }
    }

    /// <summary>
    /// Grandfather-father-son keeps only full sets for weeks and months (section 6.6), so in this mode the first run of
    /// every week (ISO, local time) is full: the last full backup was made in an earlier week.
    /// </summary>
    private static bool GfsWantsFull(RunContext run, DateTime local)
    {
        var task = run.Task;
        return task.Retention.Mode == RetentionModes.Gfs
            && (SeparateSets(task) || IsArchive(task))
            && run.State.FullAt is { } fullAt
            && SetCatalog.IsoWeek(TimeZoneInfo.ConvertTime(fullAt, run.Request.TimeZone).DateTime) != SetCatalog.IsoWeek(local);
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

            // macOS makes an APFS snapshot instead of a shadow copy (VSS), Linux one of LVM, btrfs or ZFS per volume
            // (none at all when the sources lie only on /boot).
            if (run.Snapshots.Description is { } made)
            {
                _log.Write(RunLogLevel.Info, Messages.Format(Messages.LinuxSnapshotCreated, made));
            }
            else if (!CustosEdition.IsLinux)
            {
                _log.Write(RunLogLevel.Info, OperatingSystem.IsMacOS() ? Messages.MacSnapshotCreated : Messages.VssCreated);
            }

            foreach (string note in run.Task.Vss.OnFailure == VssFailureActions.Continue ? run.Snapshots.Notes : [])
            {
                run.Result.Warnings++;
                _log.Write(RunLogLevel.Warning, note);
            }
        }
        catch (BackupException ex)
        {
            run.Result.VssFailed = true;
            if (run.Task.Vss.OnFailure == VssFailureActions.Continue)
            {
                run.Result.Warnings++;
                _log.Write(RunLogLevel.Warning, Messages.Format(
                    CustosEdition.IsLinux ? Messages.LinuxSnapshotFailedContinue : OperatingSystem.IsMacOS() ? Messages.MacSnapshotFailedContinue : Messages.VssFailedContinue,
                    ex.Message));
            }
            else
            {
                throw new BackupException(Messages.Format(
                    CustosEdition.IsLinux ? Messages.LinuxSnapshotFailedAbort : OperatingSystem.IsMacOS() ? Messages.MacSnapshotFailedAbort : Messages.VssFailedAbort,
                    ex.Message), ex);
            }

            return;
        }

        // Linux: some volumes may have a snapshot and others not; a task that insists on snapshots stops then.
        if (run.Snapshots.Notes.Count > 0 && run.Task.Vss.OnFailure != VssFailureActions.Continue)
        {
            string notes = string.Join(" ", run.Snapshots.Notes);
            run.Snapshots.Dispose();
            run.Snapshots = NoSnapshots.Instance;
            run.Result.VssFailed = true;
            throw new BackupException(Messages.Format(Messages.LinuxSnapshotPartialAbort, notes));
        }
    }

    /// <summary>A snapshot that cannot be trusted any more (it ran out of room while the files were read) fails the run
    /// before anything is kept: the next run reads the files again.</summary>
    private static void CheckSnapshots(RunContext run)
    {
        if (run.Snapshots.Verify() is { } broken)
        {
            throw new BackupException(broken);
        }
    }

    private void ScanSources(RunContext run)
    {
        Phase(BackupPhases.Scan);
        var filter = new FileFilter(run.Task.Filters);
        var excluded = CustosEdition.IsLinux ? run.Targets.Where(target => target.Root.Length != 0 && target.Store is LocalStore)
            .SelectMany(target => new[] { NormalizeLocalPath(target.Root), CanonicalLocalPath(target.Root) }).ToHashSet(PathNames.Comparer) : [];
        var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (HasDatabases(run.Task))
        {
            // The dumps have this folder of the set; a source folder of that name becomes «db (2)».
            usedTargets.Add(Databases.DbTools.SetFolder);
        }

        for (int i = 0; i < run.Task.Sources.Count; i++)
        {
            run.Cancellation.ThrowIfCancellationRequested();
            var source = run.Task.Sources[i];
            var context = new SourceContext(source);
            run.Sources.Add(context);
            try
            {
                context.Session = Session(IsNetworkPath(source.Path) ? source.CredentialId : null);
                string readRoot = context.Session.LocalPath(run.Snapshots.Map(source.Path));
                context.ReadRoot = readRoot;
                string? liveRoot = excluded.Count != 0 ? CanonicalLocalPath(context.Session.LocalPath(source.Path)) : null;
                context.Scan = context.Session.Run(() => SourceScanner.Scan(source, i, readRoot, filter,
                    (path, reason) =>
                    {
                        run.Result.Warnings++;
                        run.UnreadableFolders++;
                        _log.Write(RunLogLevel.Warning, Messages.Format(Messages.SourceEntryUnreadable, path, reason));
                    },
                    run.Cancellation,
                    _platform.SkipFolder,
                    (path, reason) => _log.Write(RunLogLevel.Info, Messages.Format(Messages.FolderSkippedMount, path, reason)),
                    _platform.IsRegularSourceFile,
                    (path, original) =>
                    {
                        if (liveRoot is null || !excluded.Contains(Path.Combine(liveRoot, Path.GetRelativePath(readRoot, path))))
                        {
                            return false;
                        }

                        run.Result.Warnings++;
                        _log.Write(RunLogLevel.Warning, Messages.Format(Messages.NestedDestinationSkipped, original));
                        return true;
                    }));
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
            if (context.Target.Length == 0 && HasDatabases(run.Task)
                && context.Scan.Folders.Concat(context.Scan.Files.Select(f => f.RelativePath)).Any(p => IsInDbFolder(p)))
            {
                RecordError(run, Messages.Format(Messages.DbFolderTaken, source.Path));
                continue;
            }

            long bytes = context.Scan.Files.Sum(f => f.Size);
            run.Result.SourceFiles += context.Scan.Files.Count;
            run.Result.SourceBytes += bytes;
            _log.Write(RunLogLevel.Info, Messages.Format(Messages.SourceScanned, source.Path, context.Scan.Files.Count, Sizes.Format(bytes)));
        }
    }

    private void CheckLinuxDestinationRoot(TaskDefinition task, string root)
    {
        string destination = CanonicalLocalPath(root);
        foreach (var source in task.Sources.Where(source => !IsNetworkPath(source.Path) && Path.IsPathFullyQualified(source.Path)))
        {
            if (IsSameOrBelow(CanonicalLocalPath(source.Path), destination))
            {
                throw new BackupException(Messages.Format(Messages.DestinationContainsSource, root, source.Path));
            }
        }
    }

    private static string NormalizeLocalPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private string CanonicalLocalPath(string path) => NormalizeLocalPath(_platform.CanonicalLocalPath(NormalizeLocalPath(path)));

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

        _progress.FilesTotal = run.ToCopy.Count + DumpsToTake(run).Count();
        _progress.BytesTotal = run.BytesToCopy + (DumpsToTake(run).Any() ? Math.Max(0, run.Request.DumpBytesHint) : 0);
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
            // A server that does not tell how much room it has is not checked (section 6.3).
            if (FreeSpace(target) is { } available && available < needed)
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
            var store = target.Store;
            try
            {
                if (SeparateSets(task))
                {
                    target.SetName = SetNaming.MakeUnique(SetNaming.TakenNames(store, target.Root), run.SetName);
                    target.SetFolder = store.Combine(target.Root, target.SetName);

                    // Where a folder cannot be renamed (S3, a NAS with WORM), the set is written in place: it counts
                    // when its manifest, written last, is there.
                    bool renames = store.CanRenameFolders(target.Root);
                    if (store.RenamesFolders && !renames)
                    {
                        _log.Write(RunLogLevel.Info, Messages.Format(Messages.FolderRenameRefused, target.Label));
                    }

                    target.WriteFolder = renames ? target.SetFolder + SetCatalog.PartialSuffix : target.SetFolder;
                    target.OwnsWriteFolder = true;
                    store.CreateFolder(target.WriteFolder);

                    // If this run never finishes the set (the computer is switched off), a later run finds it by this.
                    UnfinishedMarker.Write(store, target.WriteFolder,
                        new UnfinishedMarker.Info(task.Id, run.Request.RunId, UnfinishedMarker.Writing, run.Request.StartedAt));
                }
                else
                {
                    target.SetName = run.SetName;
                    target.SetFolder = target.Root;
                    target.WriteFolder = target.Root;
                    if (run.BackupType == BackupTypes.Full && store.KeepsTimes)
                    {
                        ReadHeldFiles(run, target);
                    }
                }

                if (run.BackupType is BackupTypes.Full or BackupTypes.Mirror && !task.Layout.DeleteEmptyDirs && store.HasFolders)
                {
                    // A full copy and a mirror reproduce the folder tree, empty folders included.
                    foreach (var context in run.Sources.Where(s => s.Scan is { Found: true, IsFile: false }))
                    {
                        foreach (string folder in context.Scan!.Folders)
                        {
                            CreateFolder(target, store.Combine(target.WriteFolder, context.Target, folder));
                        }
                    }
                }

                target.Progress.State = DestinationStates.Writing;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FailTarget(run, target, Messages.Format(Messages.DestinationWriteFailed, target.Label, ex.Message));
            }
        }
    }

    /// <summary>
    /// What a shared folder holds before a full copy into it, for example the files Cobian left there (section 6.4):
    /// a file with the size and time of its source is not written again, the copy only reads its source for the
    /// SHA-256. Replacing a file takes more rights than adding one, and some storage does not allow it at all.
    /// </summary>
    /// <remarks>Only the folders the sources go to are read, not the whole destination, which may hold much else.
    /// If they cannot be read, every file is simply written.</remarks>
    private void ReadHeldFiles(RunContext run, Target target)
    {
        var store = target.Store;
        var sources = run.Sources.Where(s => s.Scan is { Found: true }).ToList();
        var places = sources.Any(s => !s.Scan!.IsFile && s.Target.Length == 0)
            ? [(Folder: target.Root, Below: true)]
            : sources.Select(s => s.Scan!.IsFile ? (Folder: target.Root, Below: false) : (Folder: store.Combine(target.Root, s.Target), Below: true)).Distinct().ToList();
        try
        {
            foreach (var (folder, below) in places)
            {
                var entries = below ? store.ListFiles(folder) : store.List(folder).Where(e => !e.IsFolder);
                foreach (var entry in entries)
                {
                    target.Existing[MirrorPath(store, target.Root, entry.Path)] = new MirrorEntry(entry.Size, entry.ModifiedUtc);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            target.Existing.Clear();
            _log.Write(RunLogLevel.Detail, Messages.Format(Messages.FolderUnreadable, target.Root, ex.Message));
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
            if (CopyFile(run, context, file, StoredPath(context, file), buffer, out bool written) is { } hash)
            {
                run.Copied[file.OriginalPath] = hash;
                run.StoredBytes += file.Size;
                if (written)
                {
                    run.Result.FilesProcessed++;
                    run.Result.BytesWritten += file.Size;
                }

                if (run.Request.LogEveryFile)
                {
                    _log.Write(RunLogLevel.Detail, written
                        ? Messages.Format(Messages.FileCopied, file.OriginalPath, Sizes.Format(file.Size))
                        : Messages.Format(Messages.FileAlreadyThere, file.OriginalPath));
                }
            }

            _progress.FilesDone++;
        }

        _progress.CurrentFile = null;
        Report(force: true);
        foreach (var target in run.ActiveTargets.Where(t => t.HeldFiles > 0))
        {
            _log.Write(RunLogLevel.Info, Messages.Format(Messages.HeldFilesKept, target.Label, target.HeldFiles, Sizes.Format(target.HeldBytes)));
        }
    }

    /// <summary>Copies one file to every active destination. Returns its SHA-256, or null if no destination has it now.</summary>
    /// <param name="written">The file was written somewhere; false when every destination had it already.</param>
    private string? CopyFile(RunContext run, SourceContext context, ScannedFile file, string targetPath, byte[] buffer, out bool written)
    {
        written = false;
        int attempts = 1 + Math.Clamp(run.Task.Copy.ReadRetries, 0, 5);
        long progressAtStart = _progress.BytesDone;
        for (int attempt = 1; ; attempt++)
        {
            _progress.BytesDone = progressAtStart;
            var outputs = new List<Output>();
            try
            {
                using var input = OpenSourceFile(context, file, 1);
                var held = OpenOutputs(run, targetPath, file, outputs);
                if (outputs.Count == 0 && held.Count == 0)
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
                    if (outputs.Count == 0 && held.Count == 0)
                    {
                        return null;
                    }

                    _progress.BytesDone += read;
                    _speed.Add(read);
                    Report(force: false);
                }

                byte[]? security = KeepsSecurity(run) && outputs.Count > 0 ? context.Session.Run(() => _platform.ReadSecurity(file.ReadPath, folder: false)) : null;
                CompleteOutputs(run, outputs, file, targetPath, security);
                if (outputs.Count == 0 && held.Count == 0)
                {
                    return null;
                }

                foreach (var target in held)
                {
                    target.HeldFiles++;
                    target.HeldBytes += file.Size;
                }

                written = outputs.Count > 0;
                return SetManifest.ToHex(hash.GetHashAndReset());
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException && outputs.Count == 0)
            {
                // Deleted after the scan: not a problem, it is simply no longer part of the source.
                run.Vanished.Add(file.OriginalPath);
                ReportVanishedSource(file);
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

    private void ReportVanishedSource(ScannedFile file) =>
        _log.Write(CustosEdition.IsLinux ? RunLogLevel.Info : RunLogLevel.Detail, Messages.Format(Messages.FileVanished, file.OriginalPath));

    /// <returns>The destinations that hold this version of the file already (a full copy into a shared folder): they
    /// are not written.</returns>
    private List<Target> OpenOutputs(RunContext run, string targetPath, ScannedFile file, List<Output> outputs)
    {
        var held = new List<Target>();
        foreach (var target in run.ActiveTargets)
        {
            if (run.BackupType == BackupTypes.Mirror)
            {
                if (!target.Needed.Contains(targetPath))
                {
                    // This mirror holds the file already.
                    continue;
                }
            }
            else if (target.Existing.TryGetValue(targetPath, out var there) && SameFile(there, file.Size, file.LastWriteUtc))
            {
                held.Add(target);
                continue;
            }

            var store = target.Store;
            string final = store.Combine(target.WriteFolder, targetPath);
            try
            {
                if (store.Parent(final) is { } folder)
                {
                    CreateFolder(target, folder);
                }

                outputs.Add(new Output(target, store.BeginWrite(final, TempSuffix), final));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FailTarget(run, target, Messages.Format(Messages.DestinationWriteFailed, target.Label, ex.Message));
            }
        }

        return held;
    }

    private void WriteOutputs(RunContext run, List<Output> outputs, byte[] buffer, int count)
    {
        for (int i = outputs.Count - 1; i >= 0; i--)
        {
            var output = outputs[i];
            try
            {
                output.Writer.Stream.Write(buffer, 0, count);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outputs.RemoveAt(i);
                Abandon(output);
                FailTarget(run, output.Target, Messages.Format(Messages.DestinationWriteFailed, output.Target.Label, ex.Message));
            }
        }
    }

    private void CompleteOutputs(RunContext run, List<Output> outputs, ScannedFile file, string targetPath, byte[]? security)
    {
        for (int i = outputs.Count - 1; i >= 0; i--)
        {
            var output = outputs[i];
            try
            {
                var store = output.Target.Store;
                if (IsSafeMirror(run) && store.FileExists(output.Final))
                {
                    // A safe mirror keeps the version it replaces (section 6.1).
                    MoveToBin(run, output.Target, output.Final);
                }

                output.Writer.Commit();
                store.SetFileTimes(output.Final, file.CreationUtc, file.LastWriteUtc, file.Attributes);
                if (security is not null)
                {
                    ApplySecurity(run, output.Target, output.Final, folder: false, security);
                }

                output.Target.Files++;
                output.Target.Bytes += file.Size;
            }
            catch (ReplaceDeniedException ex)
            {
                // The file that was there stays; the destination goes on with the other files (section 6.4).
                outputs.RemoveAt(i);
                Abandon(output);
                output.Target.Refused.Add(targetPath);
                run.Refused.Add(file.OriginalPath);
                run.Result.Warnings++;
                _log.Write(RunLogLevel.Warning, Messages.Format(Messages.FileReplaceRefused, output.Target.Label, ex.Path, ex.Message));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outputs.RemoveAt(i);
                Abandon(output);
                FailTarget(run, output.Target, Messages.Format(Messages.DestinationWriteFailed, output.Target.Label, ex.Message));
            }
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

    /// <summary>Gives up a file being written. Never throws: if the destination is gone, the temporary file stays, and
    /// it never looks like a finished copy anyway.</summary>
    private static void Abandon(Output output) => output.Writer.Abort();

    private void FinishSets(RunContext run)
    {
        // A set missing a whole source is not a backup of the task. A destination that failed does not stop the
        // others from finishing theirs (section 6.3), but then the run is an error and nothing is committed.
        if (run.SourceErrors > 0)
        {
            return;
        }

        var finished = DateTimeOffset.UtcNow;
        string status = SetStatus(run);

        if (run.VerifyNew)
        {
            Phase(BackupPhases.Verify);
        }

        var remoteFiles = run.Remote is not null ? RemoteManifestFiles(run, out _, out _) : null;
        foreach (var target in run.ActiveTargets)
        {
            var files = run.BackupType == BackupTypes.Mirror ? MirrorFiles(run, target)
                : remoteFiles ?? (target.Refused.Count > 0 ? [.. ManifestFiles(run, target: target)] : null);
            var header = BuildHeader(run, target.SetName, finished, status);
            if (files is not null)
            {
                header.SetFileCount = files.Count(f => f.InSet && f.Posix?.Type is null or "f");
                header.SetBytes = files.Where(f => f.InSet).Sum(f => f.Size);
            }

            try
            {
                if (run.VerifyNew)
                {
                    VerifyNewFolder(run, target);
                }

                var store = target.Store;
                using (var writer = store.BeginWrite(store.Combine(target.WriteFolder, SetManifest.FileName), TempSuffix, StoreWriteMode.Stream))
                {
                    writer.Durable = true;
                    SetManifest.Write(writer.Stream, header, files ?? ManifestFiles(run));
                    writer.Commit();
                }

                if (SeparateSets(run.Task))
                {
                    UnfinishedMarker.TryRemove(store, target.WriteFolder);
                }

                if (!string.Equals(target.WriteFolder, target.SetFolder, StringComparison.Ordinal))
                {
                    store.MoveFolder(target.WriteFolder, target.SetFolder);
                }

                if (run.Task.Layout.DeleteEmptyDirs && !SeparateSets(run.Task) && store.HasFolders)
                {
                    RemoveEmptyFolders(store, target.SetFolder);
                }

                target.Completed = true;
                target.NewSet = new StoredSet(target.SetFolder, header);
                target.Progress.State = DestinationStates.Done;
                _log.Write(RunLogLevel.Info, Messages.Format(Messages.SetWritten, target.SetName, target.Label, target.Files, Sizes.Format(target.Bytes)));
            }
            catch (BackupException ex)
            {
                FailTarget(run, target, ex.Message);
            }
            catch (ReplaceDeniedException ex)
            {
                // The manifest of a shared folder is rewritten at every run, and such storage does not let it be.
                FailTarget(run, target, Messages.Format(Messages.ManifestReplaceRefused, target.Label, ex.Path, ex.Message));
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
        Sources = run.Remote is { } remote
            ? [new ManifestSource { Id = remote.Source.Id, Path = RemoteLabel(remote.Source), Target = RemoteTarName }]
            : [.. run.Sources.Where(s => s.Scan is { Found: true }).Select(s => new ManifestSource { Id = s.Source.Id, Path = s.Source.Path, Target = s.Target })],
        SourceKind = run.Remote is null ? null : SourceKinds.Ssh,
        Remote = run.Remote?.Info is null ? null : ManifestRemoteOf(run),
        Databases = ManifestDatabases(run),
        FileCount = run.Result.SourceFiles,
        SetFileCount = run.Copied.Count + pendingFiles,
        SetBytes = run.StoredBytes + pendingBytes,
    };

    /// <summary>The status a set is written with: an error when a dump failed (the set is kept all the same).</summary>
    private static string SetStatus(RunContext run) =>
        run.DbErrors > 0 || run.RemoteErrors > 0 ? RunStatuses.Error : run.Result.Warnings > 0 ? RunStatuses.Warning : RunStatuses.Success;

    /// <summary>A path of a source that would land in the «db» folder of the set, which belongs to the dumps.</summary>
    private static bool IsInDbFolder(string relativePath) =>
        string.Equals(relativePath, Databases.DbTools.SetFolder, StringComparison.OrdinalIgnoreCase)
        || relativePath.StartsWith(Databases.DbTools.SetFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || relativePath.StartsWith(Databases.DbTools.SetFolder + "/", StringComparison.OrdinalIgnoreCase);

    /// <param name="pending">Files the archive writer has yet to read (7-Zip packs by name, so the manifest can come
    /// before them): they count as in the set, without a SHA-256.</param>
    /// <param name="target">The destination of a plain copy: a file it would not let replace is not in its set.</param>
    private static IEnumerable<ManifestFile> ManifestFiles(RunContext run, HashSet<string>? pending = null, Target? target = null)
    {
        foreach (var context in run.Sources.Where(s => s.Scan is { Found: true }))
        {
            foreach (var file in context.Scan!.Files)
            {
                if (run.Vanished.Contains(file.OriginalPath))
                {
                    continue;
                }

                string stored = StoredPath(context, file);
                bool inSet = run.Copied.TryGetValue(file.OriginalPath, out string? hash) || pending?.Contains(file.OriginalPath) == true;
                if (inSet && target?.Refused.Contains(stored) == true)
                {
                    (inSet, hash) = (false, null);
                }

                yield return new ManifestFile(stored, file.Size, file.LastWriteUtc, inSet, hash);
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
        var selected = run.ToCopy.Select(c => c.File.OriginalPath).ToHashSet(PathNames.Comparer);
        foreach (var context in run.Sources.Where(s => s.Scan is { Found: true }))
        {
            foreach (var file in context.Scan!.Files)
            {
                if (run.Vanished.Contains(file.OriginalPath))
                {
                    continue;
                }

                if (!selected.Contains(file.OriginalPath) || (run.Copied.ContainsKey(file.OriginalPath) && !run.Refused.Contains(file.OriginalPath)))
                {
                    files.Add(new(file.OriginalPath, file.Stamp));
                }
                else if (run.LastState.TryGetValue(file.OriginalPath, out var previous))
                {
                    // Not copied this time (or not to every destination): keep what the last copy had, so the next
                    // run tries again.
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
            var locked = LockedChains(run, target, plan);
            var removing = new UnfinishedMarker.Info(run.Task.Id, run.Request.RunId, UnfinishedMarker.Removing, DateTimeOffset.UtcNow);
            bool worm = target.Store.Worm is not null;

            // A chain goes from its newest set to its full set: one that breaks off leaves a shorter chain that
            // still restores (section 6.6).
            foreach (var chain in plan.GroupBy(ChainOf, StringComparer.OrdinalIgnoreCase))
            {
                if (locked.Contains(chain.Key))
                {
                    continue;
                }

                foreach (var set in chain.OrderByDescending(s => s.Header.StartedAt))
                {
                    run.Cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        SetCatalog.DeleteSet(set, target.Store, removing);
                        target.Kept.Remove(set);
                        run.Result.DeletedSets++;
                        _log.Write(RunLogLevel.Info, Messages.Format(Messages.SetDeleted, set.Header.SetName, target.Label));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        run.Result.Warnings++;
                        _log.Write(RunLogLevel.Warning, Messages.Format(worm ? Messages.SetDeleteRefusedWorm : Messages.SetDeleteFailed,
                            set.Header.SetName, target.Label, ex.Message));
                        break;
                    }
                }
            }
        }
    }

    /// <summary>A time for the run log, in the time zone of the run.</summary>
    private static string LocalTime(RunContext run, DateTimeOffset time) =>
        TimeZoneInfo.ConvertTime(time, run.Request.TimeZone).ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The full set a set belongs to: sets are only ever deleted as whole chains (section 6.6).</summary>
    private static string ChainOf(StoredSet set) => set.Header.ParentFullRunId ?? set.Header.RunId;

    /// <summary>
    /// The chains retention has to leave for now, because the destination still refuses to delete one of their sets
    /// (S3 Object Lock, section 6.3). That is not an error: the first run after the time deletes them.
    /// </summary>
    private HashSet<string> LockedChains(RunContext run, Target target, List<StoredSet> plan)
    {
        var chains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in plan)
        {
            DateTimeOffset? until;
            try
            {
                until = target.Store.LockedUntil(SetCatalog.LastWritten(set, target.Store));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The destination does not tell: deleting will.
                until = null;
            }

            if (until is { } time)
            {
                chains.Add(ChainOf(set));
                _log.Write(RunLogLevel.Info, Messages.Format(target.Store.Worm is not null ? Messages.SetLockedWorm : Messages.SetLocked,
                    set.Header.SetName, target.Label, LocalTime(run, time)));
            }
        }

        return chains;
    }

    // ---- Finishing ---------------------------------------------------------------------------------------------

    private void Cleanup(RunContext run)
    {
        foreach (var target in run.Targets.Where(t => !t.Completed))
        {
            RemoveArchiveLeftovers(target);
        }

        foreach (var target in run.Targets.Where(t => !t.Completed && t.OwnsWriteFolder))
        {
            // Our own unfinished set folder: remove it, it is not a backup.
            try
            {
                target.Store.Cancellation = CancellationToken.None;
                target.Store.DeleteFolder(target.WriteFolder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                _log.Write(RunLogLevel.Warning, Messages.Format(Messages.PartialNotRemoved, target.WriteFolder, ex.Message));
            }
        }

        foreach (var target in run.Targets)
        {
            try
            {
                target.Store.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The connection was gone already.
            }
        }

        run.Snapshots.Dispose();
        run.Remote?.Session?.Dispose();
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
        FinishDatabases(run);
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

    /// <summary>Low I/O priority for the files of a run (section 6.6): the user's programs read and write first.</summary>
    private FileStream Lowered(FileStream stream)
    {
        if (_lowIo)
        {
            _platform.LowerIoPriority(stream.SafeFileHandle);
        }

        return stream;
    }

    private static void CreateFolder(Target target, string folder)
    {
        if (target.Store.HasFolders && target.CreatedFolders.Add(folder))
        {
            target.Store.CreateFolder(folder);
        }
    }

    /// <summary>Removes the folders below <paramref name="root"/> that hold nothing; links are left alone.</summary>
    private static void RemoveEmptyFolders(DestinationStore store, string root)
    {
        foreach (var folder in store.List(root).Where(e => e.IsFolder))
        {
            RemoveEmptyFolders(store, folder.Path);
            store.RemoveEmptyFolder(folder.Path);
        }
    }

    /// <summary>Bytes free in a destination; null if it does not tell.</summary>
    private static long? FreeSpace(Target target)
    {
        try
        {
            return target.Store.GetSpace(target.Root).FreeBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
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

    private static string AccountText(string? account) =>
        account is not null ? Messages.Format(Messages.AsAccount, account) : string.Empty;

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
        (path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal))
        || (path.StartsWith("//", StringComparison.Ordinal) && path.Length > 2 && path[2] != '/');

    // ---- Types -------------------------------------------------------------------------------------------------

    private sealed class RunContext(BackupRequest request, PauseGate pause, CancellationToken cancellation)
    {
        public BackupRequest Request { get; } = request;

        /// <summary>The task with the variables in its paths filled in.</summary>
        public TaskDefinition Task { get; set; } = request.Task;

        public PathVariables Variables { get; set; } = new(_ => null, hasProfile: false, request.Task.Name, string.Empty, request.Task.BackupType, DateTime.Now);

        public PauseGate Pause { get; } = pause;

        public CancellationToken Cancellation { get; } = cancellation;

        public BackupResult Result { get; } = new() { BackupType = request.Task.BackupType };

        public string BackupType { get; set; } = request.Task.BackupType;

        public TaskBackupState State { get; set; } = TaskBackupState.Empty;

        public ISnapshotSet Snapshots { get; set; } = NoSnapshots.Instance;

        public List<Target> Targets { get; } = [];

        public List<Target> ActiveTargets => [.. Targets.Where(t => !t.Failed)];

        public List<SourceContext> Sources { get; } = [];

        public Dictionary<string, FileStamp> LastState { get; set; } = new(PathNames.Comparer);

        public List<(SourceContext Context, ScannedFile File)> ToCopy { get; } = [];

        public long BytesToCopy { get; set; }

        /// <summary>Copied files: original path → SHA-256.</summary>
        public Dictionary<string, string> Copied { get; } = new(PathNames.Comparer);

        public int FailedFiles { get; set; }

        /// <summary>Sources that were missing or could not be read at all.</summary>
        public int SourceErrors { get; set; }

        /// <summary>The events before were started, so the events after run too.</summary>
        public bool EventsStarted { get; set; }

        /// <summary>The database dumps of the run, planned at its start (section 6.2).</summary>
        public List<PlannedDump> Dumps { get; } = [];

        /// <summary>Database servers that could not be reached and dumps that failed.</summary>
        public int DbErrors { get; set; }

        /// <summary>A Linux server as the source (stage 2a); null for the other tasks.</summary>
        public RemoteRun? Remote { get; set; }

        /// <summary>custos-helper ended the tar with an error: the set is kept, nothing is committed.</summary>
        public int RemoteErrors { get; set; }

        /// <summary>Folders of the sources that could not be read: their files look deleted, but are not.</summary>
        public int UnreadableFolders { get; set; }

        /// <summary>Files deleted between the scan and the copy.</summary>
        public HashSet<string> Vanished { get; } = new(PathNames.Comparer);

        /// <summary>Files that could not be read (archives only; the copy counts them in <see cref="FailedFiles"/>).</summary>
        public HashSet<string> Failed { get; } = new(PathNames.Comparer);

        /// <summary>Files a destination would not let replace (original paths): the next run copies them again.</summary>
        public HashSet<string> Refused { get; } = new(PathNames.Comparer);

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

        /// <summary>The path read: inside the shadow copy when there is one.</summary>
        public string ReadRoot { get; set; } = string.Empty;
    }

    private sealed class Target(TaskDestination destination, string label)
    {
        public TaskDestination Destination { get; } = destination;

        public string Label { get; } = label;

        public DestinationProgress Progress { get; } = new() { DestinationId = destination.Id, Label = label };

        /// <summary>Where the files go; until the destination is opened, a stand-in that is never written to.</summary>
        public DestinationStore Store { get; set; } = LocalStore.Default;

        public string Root { get; set; } = string.Empty;

        public List<StoredSet> Sets { get; set; } = [];

        public SetManifestHeader? SharedManifest { get; set; }

        public string SetName { get; set; } = string.Empty;

        public string SetFolder { get; set; } = string.Empty;

        public string WriteFolder { get; set; } = string.Empty;

        /// <summary><see cref="WriteFolder"/> is a set folder this run made: removed if the run does not finish it.</summary>
        public bool OwnsWriteFolder { get; set; }

        public HashSet<string> CreatedFolders { get; } = new(PathNames.Comparer);

        /// <summary>Permissions could not be set here: no more tries in this run.</summary>
        public bool SecurityFailed { get; set; }

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

        /// <summary>The files of the archive as they are written, in the order of <see cref="Parts"/>.</summary>
        public List<StoreWriter> Writers { get; } = [];

        /// <summary>Where each of them is read before it has its name (<see cref="StoreWriter.ReadPath"/>).</summary>
        public List<string> Temps => [.. Writers.Select(w => w.ReadPath)];

        /// <summary>Files already given their final name.</summary>
        public List<string> Renamed { get; } = [];

        /// <summary>The names in the destination folder, read once when a free set name is looked for.</summary>
        public HashSet<string>? TakenNames { get; set; }

        /// <summary>Files of the run this destination would not let replace (paths in the set): it keeps its own.</summary>
        public HashSet<string> Refused { get; } = new(PathNames.Comparer);

        /// <summary>Files a full copy into a shared folder found there in the same version and did not write.</summary>
        public int HeldFiles { get; set; }

        public long HeldBytes { get; set; }

        // ---- Mirror

        /// <summary>The files in the mirror before the run (or, for a full copy into a shared folder, in the folders its
        /// sources go to): path in the destination → size and time.</summary>
        public Dictionary<string, MirrorEntry> Existing { get; } = new(PathNames.Comparer);

        /// <summary>The files the last run put into the mirror (its manifest): only these are ever deleted.</summary>
        public Dictionary<string, ManifestFile> Previous { get; } = new(PathNames.Comparer);

        /// <summary>Files this mirror lacks or holds in another version.</summary>
        public HashSet<string> Needed { get; } = new(PathNames.Comparer);

        /// <summary>Where a safe mirror puts what this run replaces or deletes; made when first needed.</summary>
        public string? Bin { get; set; }

        public int MirrorRemoved { get; set; }
    }

    private readonly record struct MirrorEntry(long Size, DateTime ModifiedUtc);

    private sealed record Output(Target Target, StoreWriter Writer, string Final);

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
