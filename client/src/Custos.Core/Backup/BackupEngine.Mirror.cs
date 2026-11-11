using System.Globalization;
using System.IO.Enumeration;
using Custos.Core.Model;

namespace Custos.Core.Backup;

/// <summary>
/// Mirror (section 6.1): the destination folder is kept the same as the sources. A file is copied when the mirror
/// lacks it or holds another version (size, or time more than 2 seconds apart, as FAT keeps it). Files deleted in the
/// sources are deleted from the mirror; a safe mirror moves them, and the versions it replaces, to
/// <c>_custos_deleted\yyyy-MM-dd HH-mm\</c> and keeps them for <see cref="RetentionOptions.SafeMirrorDays"/> days.
/// </summary>
/// <remarks>
/// Only files the mirror's own manifest lists are ever deleted: whatever else lies in the folder stays. Nothing is
/// deleted when a source or one of its folders could not be read, because then its files only look deleted.
/// </remarks>
public sealed partial class BackupEngine
{
    /// <summary>The folder of a safe mirror for replaced and deleted files.</summary>
    public const string MirrorBinName = "_custos_deleted";

    private const string BinTimeFormat = "yyyy-MM-dd HH-mm";
    private static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>Each run makes a set folder of its own (a mirror never does).</summary>
    private static bool SeparateSets(TaskDefinition task) => task.Layout.SeparateFolders && task.BackupType != BackupTypes.Mirror;

    private static bool IsSafeMirror(RunContext run) =>
        run.BackupType == BackupTypes.Mirror && run.Task.MirrorMode == MirrorModes.Safe;

    private static bool SameFile(MirrorEntry held, long size, DateTime modifiedUtc) =>
        held.Size == size && (held.ModifiedUtc - modifiedUtc).Duration() <= TimeTolerance;

    /// <summary>A mirror inside a source (or a source inside the mirror) would copy into itself.</summary>
    /// <exception cref="BackupException">They overlap.</exception>
    private static void CheckMirrorRoot(TaskDefinition task, string root)
    {
        string mirror = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        foreach (var source in task.Sources.Where(s => s.Kind != SourceKinds.File && Path.IsPathFullyQualified(s.Path)))
        {
            string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Path));
            if (IsSameOrBelow(mirror, folder) || IsSameOrBelow(folder, mirror))
            {
                throw new BackupException(Messages.Format(Messages.MirrorOverlapsSource, root, source.Path));
            }
        }
    }

    private static bool IsSameOrBelow(string path, string folder) =>
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>What the mirror holds: the files in the folder and the manifest of the last run.</summary>
    private static void ReadMirror(RunContext run, Target target)
    {
        string manifest = Path.Combine(target.Root, SetManifest.FileName);
        if (SetCatalog.TryReadHeader(manifest) is { } header
            && header.BackupType == BackupTypes.Mirror
            && string.Equals(header.TaskId, run.Task.Id, StringComparison.OrdinalIgnoreCase))
        {
            target.SharedManifest = header;
            try
            {
                using var stream = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
                foreach (var file in SetManifest.ReadFiles(stream).Where(f => f.InSet))
                {
                    target.Previous[file.Path] = file;
                }
            }
            catch (InvalidDataException)
            {
                // A damaged manifest: nothing is deleted this time, the next run writes a good one.
                target.Previous.Clear();
            }
        }

        string bin = Path.Combine(target.Root, MirrorBinName);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var files = new FileSystemEnumerable<(string Path, MirrorEntry Entry)>(target.Root,
            (ref FileSystemEntry e) => (e.ToFullPath(), new MirrorEntry(e.Length, e.LastWriteTimeUtc.UtcDateTime)), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry e) => !string.Equals(e.ToFullPath(), bin, StringComparison.OrdinalIgnoreCase),
        };
        foreach (var (path, entry) in files)
        {
            string relative = Path.GetRelativePath(target.Root, path);
            if (!string.Equals(relative, SetManifest.FileName, StringComparison.OrdinalIgnoreCase)
                && !relative.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
            {
                target.Existing[relative] = entry;
            }
        }
    }

    /// <summary>Moves a file of the mirror into the bin of this run, keeping its place in the tree.</summary>
    private static void MoveToBin(RunContext run, Target target, string path)
    {
        if (target.Bin is null)
        {
            var local = TimeZoneInfo.ConvertTime(run.Request.StartedAt, run.Request.TimeZone).DateTime;
            string name = local.ToString(BinTimeFormat, CultureInfo.InvariantCulture);
            string bin = Path.Combine(target.Root, MirrorBinName, name);
            for (int i = 2; Directory.Exists(bin); i++)
            {
                bin = Path.Combine(target.Root, MirrorBinName, SetNaming.Numbered(name, i));
            }

            Directory.CreateDirectory(bin);
            target.Bin = bin;
        }

        string moved = Path.Combine(target.Bin, Path.GetRelativePath(target.Root, path));
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(path, moved, overwrite: true);
        target.MirrorRemoved++;
    }

    /// <summary>Removes from every mirror the files the last run put there that the sources no longer have.</summary>
    private void DeleteFromMirror(RunContext run)
    {
        if (run.SourceErrors > 0 || run.ActiveTargets.Count == 0)
        {
            return;
        }

        if (run.UnreadableFolders > 0)
        {
            run.Result.Warnings++;
            _log.Write(RunLogLevel.Warning, Messages.MirrorDeletionsSkipped);
            return;
        }

        // Every file and folder the sources have now, under its path in the mirror (also files gone since the scan:
        // they are still in the mirror and listed, so the next run deletes them).
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var context in run.Sources.Where(s => s.Scan is { Found: true }))
        {
            foreach (var file in context.Scan!.Files)
            {
                present.Add(StoredPath(context, file));
            }

            if (!context.Scan.IsFile)
            {
                folders.Add(context.Target);
                foreach (string folder in context.Scan.Folders)
                {
                    folders.Add(context.Target.Length == 0 ? folder : Path.Combine(context.Target, folder));
                }
            }
        }

        bool safe = IsSafeMirror(run);
        foreach (var target in run.ActiveTargets)
        {
            int deleted = 0;
            foreach (string path in target.Previous.Keys.Where(p => !present.Contains(p) && target.Existing.ContainsKey(p)).ToList())
            {
                run.Cancellation.ThrowIfCancellationRequested();
                string full = Path.Combine(target.Root, path);
                try
                {
                    target.Session.Run(() =>
                    {
                        if (safe)
                        {
                            MoveToBin(run, target, full);
                        }
                        else
                        {
                            File.SetAttributes(full, FileAttributes.Normal);
                            File.Delete(full);
                        }

                        RemoveEmptyParents(target.Root, full, folders);
                    });
                    target.Existing.Remove(path);
                    deleted++;
                    _log.Write(RunLogLevel.Detail, safe ? Messages.Format(Messages.MirrorFileMoved, path, target.Bin) : Messages.Format(Messages.MirrorFileDeleted, path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    run.Result.Warnings++;
                    _log.Write(RunLogLevel.Warning, Messages.Format(Messages.MirrorDeleteFailed, full, ex.Message));
                }
            }

            if (safe && target.MirrorRemoved > 0)
            {
                _log.Write(RunLogLevel.Info, Messages.Format(Messages.MirrorMoved, target.MirrorRemoved, target.Label, target.Bin));
            }
            else if (!safe && deleted > 0)
            {
                _log.Write(RunLogLevel.Info, Messages.Format(Messages.MirrorDeleted, deleted, target.Label));
            }
        }
    }

    /// <summary>Deletes the folders a deleted file leaves empty, up to the first folder the sources still have.</summary>
    private static void RemoveEmptyParents(string root, string file, HashSet<string> keep)
    {
        string? folder = Path.GetDirectoryName(file);
        string top = Path.TrimEndingDirectorySeparator(root);
        while (folder is not null && folder.Length > top.Length)
        {
            if (keep.Contains(Path.GetRelativePath(root, folder)) || Directory.EnumerateFileSystemEntries(folder).Any())
            {
                return;
            }

            Directory.Delete(folder);
            folder = Path.GetDirectoryName(folder);
        }
    }

    /// <summary>
    /// The manifest of a mirror after the run: every file it holds now, with the SHA-256 of this run or the one the
    /// last manifest had for the same version, and the files of the sources it could not get (not in the set).
    /// </summary>
    private static List<ManifestFile> MirrorFiles(RunContext run, Target target)
    {
        var files = new List<ManifestFile>();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var context in run.Sources.Where(s => s.Scan is { Found: true }))
        {
            foreach (var file in context.Scan!.Files)
            {
                string stored = StoredPath(context, file);
                listed.Add(stored);
                if (target.Needed.Contains(stored) && run.Copied.TryGetValue(file.OriginalPath, out string? hash))
                {
                    files.Add(new ManifestFile(stored, file.Size, file.LastWriteUtc, true, hash));
                }
                else if (target.Existing.TryGetValue(stored, out var held))
                {
                    files.Add(new ManifestFile(stored, held.Size, held.ModifiedUtc, true, CarriedHash(target, stored, held)));
                }
                else
                {
                    files.Add(new ManifestFile(stored, file.Size, file.LastWriteUtc, false, null));
                }
            }
        }

        // Files that should have gone but could not be deleted stay listed, so a later run deletes them.
        foreach (var (path, held) in target.Existing)
        {
            if (!listed.Contains(path) && target.Previous.ContainsKey(path))
            {
                files.Add(new ManifestFile(path, held.Size, held.ModifiedUtc, true, CarriedHash(target, path, held)));
            }
        }

        return files;
    }

    private static string? CarriedHash(Target target, string path, MirrorEntry held) =>
        target.Previous.TryGetValue(path, out var previous) && SameFile(held, previous.Size, previous.ModifiedUtc) ? previous.Sha256 : null;

    /// <summary>Deletes the folders of <see cref="MirrorBinName"/> older than the days the task keeps them.</summary>
    private void CleanMirrorBins(RunContext run)
    {
        int days = run.Task.Retention.SafeMirrorDays;
        if (run.BackupType != BackupTypes.Mirror || days <= 0)
        {
            return;
        }

        var limit = TimeZoneInfo.ConvertTime(run.Request.StartedAt, run.Request.TimeZone).DateTime.AddDays(-days);
        foreach (var target in run.Targets.Where(t => t.Completed))
        {
            string bin = Path.Combine(target.Root, MirrorBinName);
            List<string> old;
            try
            {
                old = target.Session.Run(() => Directory.Exists(bin)
                    ? [.. Directory.EnumerateDirectories(bin).Where(f => !string.Equals(f, target.Bin, StringComparison.OrdinalIgnoreCase) && BinTime(f) < limit)]
                    : new List<string>());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                run.Result.Warnings++;
                _log.Write(RunLogLevel.Warning, Messages.Format(Messages.MirrorBinRemoveFailed, bin, target.Label, ex.Message));
                continue;
            }

            foreach (string folder in old)
            {
                try
                {
                    target.Session.Run(() => SetCatalog.DeleteSetFolder(folder));
                    _log.Write(RunLogLevel.Info, Messages.Format(Messages.MirrorBinRemoved, folder, target.Label));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    run.Result.Warnings++;
                    _log.Write(RunLogLevel.Warning, Messages.Format(Messages.MirrorBinRemoveFailed, folder, target.Label, ex.Message));
                }
            }
        }
    }

    /// <summary>When a bin folder was made: from its name, or else from the folder itself (local time).</summary>
    private static DateTime BinTime(string folder)
    {
        string name = Path.GetFileName(folder);
        return name.Length >= BinTimeFormat.Length
            && DateTime.TryParseExact(name[..BinTimeFormat.Length], BinTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : Directory.GetCreationTime(folder);
    }
}
