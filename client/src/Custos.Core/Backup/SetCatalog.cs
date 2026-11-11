using Custos.Core.Model;

namespace Custos.Core.Backup;

/// <summary>A finished set found in a destination.</summary>
public sealed record StoredSet(string FolderPath, SetManifestHeader Header);

/// <summary>
/// Finds the sets of a task by reading the manifests in a destination (sections 6.6 and 6.11): what is stored is
/// what the destination holds, not what the local database remembers.
/// </summary>
public static class SetCatalog
{
    /// <summary>Folder suffix of a set that is still being written.</summary>
    public const string PartialSuffix = ".partial";

    /// <summary>Sets of <paramref name="taskId"/> in the folders directly below <paramref name="root"/>, oldest first.
    /// Folders without a readable manifest of this task are left alone.</summary>
    public static List<StoredSet> Scan(string root, string taskId)
    {
        ArgumentNullException.ThrowIfNull(root);
        var sets = new List<StoredSet>();
        if (!Directory.Exists(root))
        {
            return sets;
        }

        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            if (folder.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryReadHeader(Path.Combine(folder, SetManifest.FileName)) is { } header
                && string.Equals(header.TaskId, taskId, StringComparison.OrdinalIgnoreCase))
            {
                sets.Add(new StoredSet(folder, header));
            }
        }

        sets.Sort((a, b) => a.Header.StartedAt.CompareTo(b.Header.StartedAt));
        return sets;
    }

    /// <summary>The header of a manifest file, or null if it is missing or not a manifest.</summary>
    public static SetManifestHeader? TryReadHeader(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            return SetManifest.ReadHeader(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Which sets retention by count removes (section 6.6): only whole chains (a full set with the incremental and
    /// differential sets built on it) beyond the newest <paramref name="keepFull"/>. The newest full set always stays,
    /// and so does <paramref name="protectedRunId"/> (the set just made).
    /// </summary>
    public static List<StoredSet> PlanCountRetention(IReadOnlyList<StoredSet> sets, int keepFull, string? protectedRunId)
    {
        ArgumentNullException.ThrowIfNull(sets);
        keepFull = Math.Max(1, keepFull);
        var fulls = sets.Where(s => s.Header.BackupType == BackupTypes.Full)
            .OrderByDescending(s => s.Header.StartedAt)
            .ToList();
        if (fulls.Count <= keepFull)
        {
            return [];
        }

        // The newest fulls stay, and so does the full set the newest chain builds on: a full set made by a run that
        // failed elsewhere was never committed, so the incremental sets after it still point to the older one.
        var kept = fulls.Take(keepFull).ToList();
        string? protectedParent = sets.FirstOrDefault(s => string.Equals(s.Header.RunId, protectedRunId, StringComparison.OrdinalIgnoreCase))?.Header.ParentFullRunId;
        kept.AddRange(fulls.Skip(keepFull).Where(s => string.Equals(s.Header.RunId, protectedParent, StringComparison.OrdinalIgnoreCase)));
        var keptIds = kept.Select(s => s.Header.RunId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedFullIds = fulls.Where(s => !keptIds.Contains(s.Header.RunId)).Select(s => s.Header.RunId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownFullIds = fulls.Select(s => s.Header.RunId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldestKept = kept.Min(s => s.Header.StartedAt);

        var remove = new List<StoredSet>();
        foreach (var set in sets)
        {
            if (string.Equals(set.Header.RunId, protectedRunId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool isFull = set.Header.BackupType == BackupTypes.Full;
            string? parent = set.Header.ParentFullRunId;
            // A set whose full set is gone is useless; it goes once it is older than every kept chain.
            bool orphan = parent is null || !knownFullIds.Contains(parent);
            bool removeIt = isFull
                ? removedFullIds.Contains(set.Header.RunId)
                : (parent is not null && removedFullIds.Contains(parent)) || (orphan && set.Header.StartedAt < oldestKept);
            if (removeIt)
            {
                remove.Add(set);
            }
        }

        return remove;
    }

    /// <summary>Deletes a set folder. Read-only files are deleted too; links inside are removed, never followed.</summary>
    public static void DeleteSetFolder(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var root = new DirectoryInfo(folder);
        if (!root.Exists)
        {
            return;
        }

        DeleteTree(root);
    }

    private static void DeleteTree(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            if (entry is DirectoryInfo child && (entry.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                DeleteTree(child);
                continue;
            }

            if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }

            if (entry is DirectoryInfo link)
            {
                link.Delete(recursive: false);
            }
            else
            {
                entry.Delete();
            }
        }

        if ((directory.Attributes & FileAttributes.ReadOnly) != 0)
        {
            directory.Attributes &= ~FileAttributes.ReadOnly;
        }

        directory.Delete(recursive: false);
    }
}
