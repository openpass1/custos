using Custos.Core.Destinations;
using Custos.Core.Model;

namespace Custos.Core.Backup;

/// <summary>
/// What earlier runs left in a destination (section 6.6): set folders of this task that a run or a deletion never
/// finished, and trial files of «Проверить доступ»; and the check that WORM protects the files (section 6.3).
/// </summary>
public sealed partial class BackupEngine
{
    /// <summary>A run that did not finish its set left it at least this long ago: no run of the task writes it now.</summary>
    private static readonly TimeSpan LeftoverAge = TimeSpan.FromDays(1);

    private void RemoveLeftovers(RunContext run)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var target in run.Targets.Where(t => t.Completed))
        {
            var store = target.Store;
            var worm = store.Worm;

            // Under WORM nothing can be deleted before its protection is over.
            var wait = worm is not null && worm.Protection > LeftoverAge ? worm.Protection : LeftoverAge;
            List<StoreEntry> entries;
            try
            {
                entries = store.List(target.Root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var known = new HashSet<string>(target.Kept.Select(s => s.Location), StringComparer.Ordinal) { target.SetFolder, target.WriteFolder };
            foreach (var entry in entries)
            {
                run.Cancellation.ThrowIfCancellationRequested();
                if (entry.Name.StartsWith(DestinationStore.ProbePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    RemoveTrial(store, entry, now, wait);
                }
                else if (entry.IsFolder && SeparateSets(run.Task) && !known.Contains(entry.Path))
                {
                    RemoveUnfinishedSet(run, target, entry, now, wait);
                }
            }
        }
    }

    /// <summary>Removes a set folder of this task that has its marker and no manifest, once nobody writes it.</summary>
    private void RemoveUnfinishedSet(RunContext run, Target target, StoreEntry folder, DateTimeOffset now, TimeSpan wait)
    {
        var store = target.Store;
        UnfinishedMarker.Info? marker;
        try
        {
            marker = UnfinishedMarker.TryRead(store, folder.Path);
            if (marker is null
                || !string.Equals(marker.TaskId, run.Task.Id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(marker.RunId, run.Request.RunId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            bool partial = folder.Name.EndsWith(SetCatalog.PartialSuffix, StringComparison.OrdinalIgnoreCase);
            if (!partial && SetCatalog.TryReadHeader(store, store.Combine(folder.Path, SetManifest.FileName)) is not null)
            {
                // A complete set whose marker could not be removed (WORM locked it during a long run).
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (marker.State == UnfinishedMarker.Writing)
        {
            var changed = folder.ModifiedUtc > DateTime.MinValue
                ? new DateTimeOffset(DateTime.SpecifyKind(folder.ModifiedUtc, DateTimeKind.Utc))
                : marker.Since;
            if (now - (changed > marker.Since ? changed : marker.Since) < wait)
            {
                return;
            }
        }

        string name = folder.Name.EndsWith(SetCatalog.PartialSuffix, StringComparison.OrdinalIgnoreCase)
            ? folder.Name[..^SetCatalog.PartialSuffix.Length]
            : folder.Name;
        try
        {
            SetCatalog.DeleteMarkedFolder(store, folder.Path);
            _log.Write(RunLogLevel.Info, marker.State == UnfinishedMarker.Writing
                ? Messages.Format(Messages.UnfinishedSetRemoved, name, target.Label, LocalTime(run, marker.Since))
                : Messages.Format(Messages.LeftoverSetRemoved, name, target.Label));
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && store.ProtectsFiles)
        {
            // Some files are still protected; the marker stays, and a later run goes on.
            _log.Write(RunLogLevel.Info, Messages.Format(Messages.UnfinishedSetProtected, name, target.Label));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            run.Result.Warnings++;
            _log.Write(RunLogLevel.Warning, Messages.Format(Messages.UnfinishedSetNotRemoved, name, target.Label, ex.Message));
        }
    }

    /// <summary>A trial file or folder that could not be removed when it was made (WORM that locks at once).</summary>
    private static void RemoveTrial(DestinationStore store, StoreEntry entry, DateTimeOffset now, TimeSpan wait)
    {
        if (entry.ModifiedUtc == DateTime.MinValue || now - new DateTimeOffset(DateTime.SpecifyKind(entry.ModifiedUtc, DateTimeKind.Utc)) < wait)
        {
            return;
        }

        try
        {
            if (entry.IsFolder)
            {
                store.DeleteFolder(entry.Path);
            }
            else
            {
                store.DeleteFile(entry.Path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tried again by the next run.
        }
    }

    /// <summary>The next step of the check that WORM protects the files (section 6.3), for every destination with it.</summary>
    private void CheckProtection(RunContext run)
    {
        foreach (var target in run.Targets.Where(t => t.Completed))
        {
            if (target.Store is not { Worm: { } worm } store)
            {
                continue;
            }

            try
            {
                var now = DateTimeOffset.UtcNow;
                var result = WormCheck.Run(store, worm, now);
                switch (result.Outcome)
                {
                    case WormCheckOutcome.Started:
                        _log.Write(RunLogLevel.Info, Messages.Format(Messages.WormCheckStarted, target.Label, LocalTime(run, result.Next)));
                        break;
                    case WormCheckOutcome.Protected:
                        _log.Write(RunLogLevel.Info, Messages.Format(Messages.WormCheckProtected, target.Label, LocalTime(run, result.Written)));
                        break;
                    case WormCheckOutcome.NotProtected:
                        run.Result.Warnings++;
                        _log.Write(RunLogLevel.Warning, Messages.Format(Messages.WormCheckNotProtected, target.Label, LocalTime(run, result.Written),
                            (int)Math.Round((now - result.Written).TotalHours)));
                        break;
                    case WormCheckOutcome.ProtectedLonger:
                        run.Result.Warnings++;
                        _log.Write(RunLogLevel.Warning, Messages.Format(Messages.WormCheckLonger, target.Label, LocalTime(run, result.Written), worm.Days));
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                run.Result.Warnings++;
                _log.Write(RunLogLevel.Warning, Messages.Format(Messages.WormCheckFailed, target.Label, ex.Message));
            }
        }
    }
}
