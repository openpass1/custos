using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Custos.Core.Archives;
using Custos.Core.Model;

namespace Custos.Core.Backup;

/// <summary>How a set is stored.</summary>
public enum SetStorage
{
    /// <summary>A folder of plain files with <c>custos-set.json</c> in it.</summary>
    Folder,

    /// <summary>A 7z or ZIP archive (maybe in volumes) with <c>custos-set.json</c> inside.</summary>
    Archive,
}

/// <summary>A finished set found in a destination.</summary>
/// <param name="Location">The set folder, or the (first) archive file.</param>
public sealed record StoredSet(string Location, SetManifestHeader Header)
{
    public SetStorage Storage { get; init; } = SetStorage.Folder;

    /// <summary>For archives: one of <see cref="ArchiveFormats"/>.</summary>
    public string? Format { get; init; }

    /// <summary>For archives: every file of the set (the volumes in order). For folders: the folder.</summary>
    public IReadOnlyList<string> Parts { get; init; } = [];

    /// <summary>Bytes the set takes in the destination (archives only; 0 for folders).</summary>
    public long Bytes { get; init; }

    /// <summary>The files to delete or to read, whatever the storage.</summary>
    public IReadOnlyList<string> AllParts => Storage == SetStorage.Folder ? [Location] : Parts;
}

/// <summary>Opens archives of a task: the formats, the password of the task and the network logon of the destination.</summary>
public sealed class ArchiveAccess(IArchiveProvider provider, string? password, INetworkSession? session = null)
{
    public IArchiveProvider Provider { get; } = provider;

    public string? Password { get; } = password;

    /// <summary>Every file is opened inside this logon, also the volumes 7-Zip reaches later from its own threads.</summary>
    public INetworkSession Session { get; } = session ?? ProcessSession.Instance;

    /// <summary>The same access through another network logon.</summary>
    public ArchiveAccess WithSession(INetworkSession other) => new(Provider, Password, other);

    /// <summary>Opens a stored archive set.</summary>
    /// <exception cref="ArchiveException">It cannot be opened.</exception>
    /// <exception cref="IOException">A volume cannot be read.</exception>
    public IArchiveReader Open(StoredSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return Open(set.Format ?? ArchiveFormats.SevenZip, set.Parts);
    }

    /// <summary>Opens an archive from its file or its volumes in order.</summary>
    public IArchiveReader Open(string format, IReadOnlyList<string> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var session = Session;
        Stream stream = parts.Count == 1
            ? session.Run(() => OpenPart(parts[0]))
            : new ConcatenatedStream(session.Run(() => parts.Select(p => new FileInfo(p).Length).ToList()), i => session.Run(() => OpenPart(parts[i])));
        return Provider.OpenReader(format, stream, Password);
    }

    /// <summary>A short fingerprint of the password, so that cached results of one password are not used for another.</summary>
    internal string PasswordKey => Password is null ? "-" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Password)), 0, 8);

    private static FileStream OpenPart(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
}

/// <summary>
/// Finds the sets of a task by reading the manifests in a destination (sections 6.6 and 6.11): what is stored is
/// what the destination holds, not what the local database remembers.
/// </summary>
public static partial class SetCatalog
{
    /// <summary>Suffix of a set folder or an archive file that is still being written.</summary>
    public const string PartialSuffix = ".partial";

    private const int HeaderCacheLimit = 20_000;

    // Headers of archives read before, by path, size, time and password: reading them again means opening (and for
    // encrypted names, decrypting) every archive of the destination on every run.
    private static readonly Dictionary<string, SetManifestHeader?> HeaderCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock CacheLock = new();

    /// <summary>
    /// Sets of <paramref name="taskId"/> (null = of any task) directly below <paramref name="root"/>, oldest first: set
    /// folders, and archives when <paramref name="archives"/> is given. Folders and files without a readable manifest of this task are left
    /// alone; <paramref name="onProblem"/> hears about archives that could not be opened. With
    /// <paramref name="mirror"/> a mirror in <paramref name="root"/> itself counts too.
    /// </summary>
    public static List<StoredSet> Scan(string root, string? taskId, ArchiveAccess? archives = null, Action<string, string>? onProblem = null, bool mirror = false)
    {
        ArgumentNullException.ThrowIfNull(root);
        var sets = new List<StoredSet>();
        if (!Directory.Exists(root))
        {
            return sets;
        }

        if (mirror && TryReadHeader(Path.Combine(root, SetManifest.FileName)) is { BackupType: BackupTypes.Mirror } own
            && (taskId is null || string.Equals(own.TaskId, taskId, StringComparison.OrdinalIgnoreCase)))
        {
            // Never offered to retention: deleting this "set" would delete the destination folder.
            sets.Add(new StoredSet(root, own));
        }

        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            if (folder.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryReadHeader(Path.Combine(folder, SetManifest.FileName)) is { } header
                && (taskId is null || string.Equals(header.TaskId, taskId, StringComparison.OrdinalIgnoreCase)))
            {
                sets.Add(new StoredSet(folder, header));
            }
        }

        if (archives is not null)
        {
            foreach (var found in FindArchives(root))
            {
                var header = ReadArchiveHeader(found, archives, onProblem);
                if (header is not null && (taskId is null || string.Equals(header.TaskId, taskId, StringComparison.OrdinalIgnoreCase)))
                {
                    sets.Add(new StoredSet(found.Parts[0], header)
                    {
                        Storage = SetStorage.Archive,
                        Format = found.Format,
                        Parts = found.Parts,
                        Bytes = found.Bytes,
                    });
                }
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

    /// <summary>An archive found in a folder: one file, or the volumes .001, .002 … of one.</summary>
    public sealed record FoundArchive(string Format, IReadOnlyList<string> Parts, long Bytes, DateTime ModifiedUtc);

    /// <summary>Finished archives directly in <paramref name="root"/>: *.7z, *.zip and complete runs of *.7z.001, .002 ….</summary>
    public static List<FoundArchive> FindArchives(string root)
    {
        var result = new List<FoundArchive>();
        var volumes = new Dictionary<string, SortedDictionary<int, FileInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            string name = file.Name;
            if (name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new FoundArchive(ArchiveFormats.SevenZip, [file.FullName], file.Length, file.LastWriteTimeUtc));
            }
            else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new FoundArchive(ArchiveFormats.Zip, [file.FullName], file.Length, file.LastWriteTimeUtc));
            }
            else if (VolumeName().Match(name) is { Success: true } match)
            {
                string key = match.Groups[1].Value;
                if (!volumes.TryGetValue(key, out var parts))
                {
                    volumes[key] = parts = [];
                }

                parts[int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)] = file;
            }
        }

        foreach (var parts in volumes.Values)
        {
            // Only a complete run 1, 2, … n is an archive; anything else is a leftover.
            if (parts.Keys.First() != 1 || parts.Keys.Last() != parts.Count)
            {
                continue;
            }

            result.Add(new FoundArchive(ArchiveFormats.SevenZip, [.. parts.Values.Select(f => f.FullName)],
                parts.Values.Sum(f => f.Length), parts.Values.Max(f => f.LastWriteTimeUtc)));
        }

        return result;
    }

    /// <summary>Reads the whole manifest of a set: header and file list.</summary>
    /// <exception cref="InvalidDataException">The set has no readable manifest.</exception>
    /// <exception cref="ArchiveException">The archive cannot be opened.</exception>
    public static (SetManifestHeader Header, List<ManifestFile> Files) ReadManifest(StoredSet set, ArchiveAccess? archives)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.Storage == SetStorage.Folder)
        {
            string path = Path.Combine(set.Location, SetManifest.FileName);
            using var headerStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            var header = SetManifest.ReadHeader(headerStream);
            headerStream.Position = 0;
            return (header, SetManifest.ReadFiles(headerStream));
        }

        if (archives is null)
        {
            throw new InvalidDataException("No access to archives.");
        }

        using var reader = archives.Open(set);
        using var manifest = ExtractManifest(reader) ?? throw new InvalidDataException(Messages.Format(Messages.ManifestMissing, set.Location));
        var parsed = SetManifest.ReadHeader(manifest);
        manifest.Position = 0;
        return (parsed, SetManifest.ReadFiles(manifest));
    }

    /// <summary><c>custos-set.json</c> of an opened archive, or null if it has none.</summary>
    /// <exception cref="ArchiveException">The manifest cannot be unpacked (wrong password, damage).</exception>
    public static MemoryStream? ExtractManifest(IArchiveReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var entry = reader.Entries.LastOrDefault(e => !e.IsFolder && string.Equals(e.Path, SetManifest.FileName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        var buffer = new MemoryStream();
        var outcome = ExtractOutcome.Damaged;
        reader.Extract([entry.Index], _ => buffer, (_, result) => outcome = result, CancellationToken.None);
        if (outcome != ExtractOutcome.Ok)
        {
            throw new ArchiveException(outcome == ExtractOutcome.WrongPassword ? Messages.ArchiveWrongPassword : Messages.Format(Messages.ArchiveItemDamaged, entry.Path))
            {
                WrongPassword = outcome == ExtractOutcome.WrongPassword,
            };
        }

        buffer.Position = 0;
        return buffer;
    }

    private static SetManifestHeader? ReadArchiveHeader(FoundArchive found, ArchiveAccess archives, Action<string, string>? onProblem)
    {
        string key = string.Join('|', found.Parts[0], found.Bytes, found.ModifiedUtc.Ticks, archives.PasswordKey);
        lock (CacheLock)
        {
            if (HeaderCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        SetManifestHeader? header = null;
        try
        {
            using var reader = archives.Open(found.Format, found.Parts);
            using var manifest = ExtractManifest(reader);
            header = manifest is null ? null : SetManifest.ReadHeader(manifest);
        }
        catch (Exception ex) when (ex is ArchiveException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Not ours, another password, or damaged: in any case not a set this task can use.
            onProblem?.Invoke(found.Parts[0], ex.Message);
        }

        lock (CacheLock)
        {
            if (HeaderCache.Count >= HeaderCacheLimit)
            {
                HeaderCache.Clear();
            }

            HeaderCache[key] = header;
        }

        return header;
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

    /// <summary>
    /// Which sets retention by age removes (section 6.6): sets older than <paramref name="maxAgeDays"/> days, except
    /// what a younger set still needs (its full set, and for an incremental set the incremental sets before it). So a
    /// full set goes only when every set built on it is old too. The newest full set and the protected set stay.
    /// </summary>
    public static List<StoredSet> PlanAgeRetention(IReadOnlyList<StoredSet> sets, int maxAgeDays, DateTimeOffset now, string? protectedRunId)
    {
        ArgumentNullException.ThrowIfNull(sets);
        if (maxAgeDays <= 0)
        {
            return [];
        }

        var limit = now - TimeSpan.FromDays(maxAgeDays);
        return Unneeded(sets, sets.Where(s => s.Header.StartedAt >= limit), protectedRunId);
    }

    /// <summary>
    /// Which sets grandfather-father-son retention removes (section 6.6): of the newest days, weeks (ISO) and months
    /// that have sets, the last set of each stays, as many as <paramref name="gfs"/> says, with what those sets need.
    /// The newest full set and the protected set stay.
    /// </summary>
    public static List<StoredSet> PlanGfsRetention(IReadOnlyList<StoredSet> sets, GfsOptions gfs, TimeZoneInfo timeZone, string? protectedRunId)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(gfs);
        ArgumentNullException.ThrowIfNull(timeZone);
        var newestFirst = sets.OrderByDescending(s => s.Header.StartedAt).ToList();
        var points = new List<StoredSet>();
        void Pick(int count, Func<DateTime, (int, int, int)> period)
        {
            // GroupBy keeps the order in which periods first appear: newest first.
            points.AddRange(newestFirst.GroupBy(s => period(TimeZoneInfo.ConvertTime(s.Header.StartedAt, timeZone).DateTime))
                .Take(Math.Max(0, count))
                .Select(g => g.First()));
        }

        Pick(gfs.Daily, d => (d.Year, d.Month, d.Day));
        Pick(gfs.Weekly, d => (ISOWeek.GetYear(d), ISOWeek.GetWeekOfYear(d), 0));
        Pick(gfs.Monthly, d => (d.Year, d.Month, 0));
        return Unneeded(sets, points, protectedRunId);
    }

    /// <summary>Every set that neither <paramref name="wanted"/> nor what it needs, the newest full set and the
    /// protected set include.</summary>
    private static List<StoredSet> Unneeded(IReadOnlyList<StoredSet> sets, IEnumerable<StoredSet> wanted, string? protectedRunId)
    {
        var keep = new HashSet<StoredSet>(wanted, ReferenceEqualityComparer.Instance);
        keep.UnionWith(sets.Where(s => string.Equals(s.Header.RunId, protectedRunId, StringComparison.OrdinalIgnoreCase)));
        if (sets.Where(s => s.Header.BackupType == BackupTypes.Full).MaxBy(s => s.Header.StartedAt) is { } newestFull)
        {
            keep.Add(newestFull);
        }

        foreach (var set in keep.ToList())
        {
            keep.UnionWith(Needs(sets, set));
        }

        return [.. sets.Where(s => !keep.Contains(s))];
    }

    /// <summary>The sets <paramref name="set"/> builds on (those that are there): its full set, and for an incremental
    /// set the incremental sets of the chain before it.</summary>
    private static IEnumerable<StoredSet> Needs(IReadOnlyList<StoredSet> sets, StoredSet set)
    {
        string? parent = set.Header.ParentFullRunId;
        if (set.Header.BackupType is BackupTypes.Full or BackupTypes.Mirror || parent is null)
        {
            return [];
        }

        return sets.Where(s => string.Equals(s.Header.ParentFullRunId ?? s.Header.RunId, parent, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(s.Header.RunId, parent, StringComparison.OrdinalIgnoreCase)
                || (set.Header.BackupType == BackupTypes.Incremental && s.Header.BackupType == BackupTypes.Incremental && s.Header.StartedAt < set.Header.StartedAt)));
    }

    /// <summary>
    /// The sets a restore of <paramref name="target"/> needs, oldest first (section 6.11): the full set it builds on,
    /// then for an incremental set every incremental set of the chain up to it, for a differential set only itself.
    /// </summary>
    /// <exception cref="InvalidDataException">A set of the chain is missing.</exception>
    public static List<StoredSet> RestoreChain(IReadOnlyList<StoredSet> sets, StoredSet target)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(target);
        if (target.Header.BackupType is BackupTypes.Full or BackupTypes.Mirror)
        {
            return [target];
        }

        string? parentId = target.Header.ParentFullRunId;
        var full = sets.FirstOrDefault(s => s.Header.BackupType == BackupTypes.Full && string.Equals(s.Header.RunId, parentId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(Messages.Format(Messages.RestoreFullMissing, target.Header.ParentFullSetName ?? parentId));
        var chain = new List<StoredSet> { full };
        if (target.Header.BackupType == BackupTypes.Incremental)
        {
            chain.AddRange(sets.Where(s => s.Header.BackupType == BackupTypes.Incremental
                && string.Equals(s.Header.ParentFullRunId, parentId, StringComparison.OrdinalIgnoreCase)
                && s.Header.StartedAt < target.Header.StartedAt
                && !string.Equals(s.Header.RunId, target.Header.RunId, StringComparison.OrdinalIgnoreCase)));
        }

        chain.Add(target);
        chain.Sort((a, b) => a.Header.StartedAt.CompareTo(b.Header.StartedAt));
        return chain;
    }

    /// <summary>Deletes a set: its folder, or all files of its archive.</summary>
    public static void DeleteSet(StoredSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.Storage == SetStorage.Folder)
        {
            DeleteSetFolder(set.Location);
            return;
        }

        foreach (string part in set.Parts)
        {
            var file = new FileInfo(part);
            if (!file.Exists)
            {
                continue;
            }

            if (file.IsReadOnly)
            {
                file.IsReadOnly = false;
            }

            file.Delete();
        }
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

    [GeneratedRegex(@"^(.+\.7z)\.(\d{3,})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VolumeName();
}
