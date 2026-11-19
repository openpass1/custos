using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Custos.Core.Archives;
using Custos.Core.Destinations;
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

/// <summary>Opens archives of a task: the formats, the password of the task and the destination they lie in.</summary>
public sealed class ArchiveAccess(IArchiveProvider provider, string? password, DestinationStore? store = null)
{
    public IArchiveProvider Provider { get; } = provider;

    public string? Password { get; } = password;

    /// <summary>Where the sets lie: every file is opened through it, also the volumes 7-Zip reaches later from its
    /// own threads. Without one: plain files of this computer.</summary>
    public DestinationStore Store { get; } = store ?? LocalStore.Default;

    /// <summary>The same access to another destination.</summary>
    public ArchiveAccess WithStore(DestinationStore other) => new(Provider, Password, other);

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
        var store = Store;
        Stream stream = parts.Count == 1
            ? OpenPart(store, parts[0])
            : new ConcatenatedStream([.. parts.Select(store.GetFileSize)], i => OpenPart(store, parts[i]));
        try
        {
            return Provider.OpenReader(format, stream, Password);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>A short fingerprint of the password, so that cached results of one password are not used for another.</summary>
    internal string PasswordKey => Password is null ? "-" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Password)), 0, 8);

    /// <summary>An archive is read here and there, not from its start to its end.</summary>
    private static Stream OpenPart(DestinationStore store, string path) =>
        store is LocalStore local ? local.OpenRandom(path) : store.OpenRead(path);
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
    private static readonly Dictionary<string, SetManifestHeader?> HeaderCache = new(StringComparer.Ordinal);
    private static readonly Lock CacheLock = new();

    /// <summary>
    /// Sets of <paramref name="taskId"/> (null = of any task) directly below <paramref name="root"/>, oldest first: set
    /// folders, and archives when <paramref name="archives"/> is given. Folders and files without a readable manifest of this task are left
    /// alone; <paramref name="onProblem"/> hears about archives that could not be opened. With
    /// <paramref name="mirror"/> a mirror in <paramref name="root"/> itself counts too.
    /// </summary>
    public static List<StoredSet> Scan(string root, string? taskId, ArchiveAccess? archives = null, Action<string, string>? onProblem = null, bool mirror = false) =>
        Scan(archives?.Store ?? LocalStore.Default, root, taskId, archives, onProblem, mirror);

    /// <summary>The same in any kind of destination.</summary>
    public static List<StoredSet> Scan(DestinationStore store, string root, string? taskId, ArchiveAccess? archives = null, Action<string, string>? onProblem = null, bool mirror = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(root);
        var sets = new List<StoredSet>();
        var entries = store.List(root);
        if (mirror && entries.Any(e => !e.IsFolder && IsManifest(e.Name))
            && TryReadHeader(store, store.Combine(root, SetManifest.FileName)) is { BackupType: BackupTypes.Mirror } own
            && (taskId is null || string.Equals(own.TaskId, taskId, StringComparison.OrdinalIgnoreCase)))
        {
            // Never offered to retention: deleting this "set" would delete the destination folder.
            sets.Add(new StoredSet(root, own));
        }

        foreach (var folder in entries.Where(e => e.IsFolder))
        {
            if (folder.Name.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryReadHeader(store, store.Combine(folder.Path, SetManifest.FileName)) is { } header
                && (taskId is null || string.Equals(header.TaskId, taskId, StringComparison.OrdinalIgnoreCase)))
            {
                sets.Add(new StoredSet(folder.Path, header));
            }
        }

        if (archives is not null)
        {
            archives = ReferenceEquals(archives.Store, store) ? archives : archives.WithStore(store);
            foreach (var found in FindArchives(entries))
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

    /// <summary>The header of a manifest file of this computer, or null if it is missing or not a manifest.</summary>
    public static SetManifestHeader? TryReadHeader(string manifestPath) => TryReadHeader(LocalStore.Default, manifestPath);

    /// <summary>The header of a manifest file, or null if it is missing or not a manifest.</summary>
    /// <exception cref="IOException">The destination failed (a missing file is not a failure).</exception>
    public static SetManifestHeader? TryReadHeader(DestinationStore store, string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            using var stream = store.OpenRead(manifestPath);
            return SetManifest.ReadHeader(stream);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidDataException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && DestinationKinds.IsFileSystem(store.Kind))
        {
            // A file that cannot be read on a disk is not a manifest; a server that fails is a failed destination.
            return null;
        }
    }

    /// <summary>An archive found in a folder: one file, or the volumes .001, .002 … of one.</summary>
    public sealed record FoundArchive(string Format, IReadOnlyList<string> Parts, long Bytes, DateTime ModifiedUtc);

    /// <summary>Finished archives directly in <paramref name="root"/>: *.7z, *.zip and complete runs of *.7z.001, .002 …
    /// and of *.zip.001, .002 … (a ZIP archive in parts on a cloud drive, section 6.3).</summary>
    public static List<FoundArchive> FindArchives(DestinationStore store, string root)
    {
        ArgumentNullException.ThrowIfNull(store);
        return FindArchives(store.List(root));
    }

    private static List<FoundArchive> FindArchives(List<StoreEntry> entries)
    {
        var result = new List<FoundArchive>();
        var volumes = new Dictionary<string, SortedDictionary<int, StoreEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in entries.Where(e => !e.IsFolder))
        {
            string name = file.Name;
            if (name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new FoundArchive(ArchiveFormats.SevenZip, [file.Path], file.Size, file.ModifiedUtc));
            }
            else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new FoundArchive(ArchiveFormats.Zip, [file.Path], file.Size, file.ModifiedUtc));
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

            string format = parts.Values.First().Name.EndsWith(".7z.001", StringComparison.OrdinalIgnoreCase) ? ArchiveFormats.SevenZip : ArchiveFormats.Zip;
            result.Add(new FoundArchive(format, [.. parts.Values.Select(f => f.Path)],
                parts.Values.Sum(f => f.Size), parts.Values.Max(f => f.ModifiedUtc)));
        }

        return result;
    }

    private static bool IsManifest(string name) => string.Equals(name, SetManifest.FileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the whole manifest of a set: header and file list.</summary>
    /// <exception cref="InvalidDataException">The set has no readable manifest.</exception>
    /// <exception cref="ArchiveException">The archive cannot be opened.</exception>
    /// <param name="store">Where a set folder lies; null = where <paramref name="archives"/> points, or this computer.</param>
    public static (SetManifestHeader Header, List<ManifestFile> Files) ReadManifest(StoredSet set, ArchiveAccess? archives, DestinationStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        store ??= archives?.Store ?? LocalStore.Default;
        if (set.Storage == SetStorage.Folder)
        {
            using var headerStream = store.OpenRead(store.Combine(set.Location, SetManifest.FileName));
            var header = SetManifest.ReadHeader(headerStream);
            headerStream.Position = 0;
            return (header, SetManifest.ReadFiles(headerStream));
        }

        if (archives is null)
        {
            throw new InvalidDataException("No access to archives.");
        }

        using var reader = (ReferenceEquals(archives.Store, store) ? archives : archives.WithStore(store)).Open(set);
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
        string key = string.Join('|', archives.Store.CacheKey, found.Parts[0], found.Bytes, found.ModifiedUtc.Ticks, archives.PasswordKey);
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
    /// Which sets grandfather-father-son retention removes (section 6.6): of the newest days that have sets, the last
    /// set of each stays; of the newest weeks (ISO) and months that have full sets, the last full set of each stays;
    /// as many as <paramref name="gfs"/> says, with what those sets need. Weeks and months keep only full sets, so each
    /// of them is one set that restores alone, however the task makes its incremental or differential ones (the engine
    /// makes the first run of every week full in this mode). The newest full set and the protected set stay.
    /// </summary>
    public static List<StoredSet> PlanGfsRetention(IReadOnlyList<StoredSet> sets, GfsOptions gfs, TimeZoneInfo timeZone, string? protectedRunId)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(gfs);
        ArgumentNullException.ThrowIfNull(timeZone);
        var newestFirst = sets.OrderByDescending(s => s.Header.StartedAt).ToList();
        var fullsNewestFirst = newestFirst.Where(s => s.Header.BackupType == BackupTypes.Full).ToList();
        var points = new List<StoredSet>();
        void Pick(List<StoredSet> from, int count, Func<DateTime, (int, int, int)> period)
        {
            // GroupBy keeps the order in which periods first appear: newest first.
            points.AddRange(from.GroupBy(s => period(TimeZoneInfo.ConvertTime(s.Header.StartedAt, timeZone).DateTime))
                .Take(Math.Max(0, count))
                .Select(g => g.First()));
        }

        Pick(newestFirst, gfs.Daily, d => (d.Year, d.Month, d.Day));
        Pick(fullsNewestFirst, gfs.Weekly, IsoWeek);
        Pick(fullsNewestFirst, gfs.Monthly, d => (d.Year, d.Month, 0));
        return Unneeded(sets, points, protectedRunId);
    }

    /// <summary>The ISO week of a local time (weeks start on Monday) as a key: year, week, 0.</summary>
    internal static (int, int, int) IsoWeek(DateTime local) => (ISOWeek.GetYear(local), ISOWeek.GetWeekOfYear(local), 0);

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

    /// <summary>
    /// Deletes a set: its folder, or all files of its archive. A folder loses its manifest first, so that a set the
    /// destination will not let go yet (WORM, Object Lock: the manifest is written last and freed last) stays whole,
    /// and one that is deleted only in part is no longer offered for restore (section 6.6).
    /// </summary>
    /// <param name="store">Where the set lies; null = on this computer.</param>
    /// <param name="removing">Left in the folder once the manifest is gone, so that a later run of the task finishes a
    /// deletion that broke off; null = no marker.</param>
    public static void DeleteSet(StoredSet set, DestinationStore? store = null, UnfinishedMarker.Info? removing = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        store ??= LocalStore.Default;
        if (set.Storage == SetStorage.Folder)
        {
            store.DeleteFile(store.Combine(set.Location, SetManifest.FileName));
            if (removing is null)
            {
                store.DeleteFolder(set.Location);
                return;
            }

            // A marker that is there (a long run could not remove its own under WORM) does as well, and one that is
            // locked could not be replaced anyway.
            if (UnfinishedMarker.TryRead(store, set.Location) is null)
            {
                UnfinishedMarker.Write(store, set.Location, removing);
            }

            DeleteMarkedFolder(store, set.Location);
            return;
        }

        foreach (string part in set.Parts)
        {
            store.DeleteFile(part);
        }
    }

    /// <summary>
    /// The file of a set that is written last (the manifest of a folder, the last volume of an archive): where files
    /// are locked for a time (S3 Object Lock), its lock is the one that ends last.
    /// </summary>
    public static string LastWritten(StoredSet set, DestinationStore store)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(store);
        return set.Storage == SetStorage.Folder ? store.Combine(set.Location, SetManifest.FileName) : set.Parts[^1];
    }

    /// <summary>
    /// Deletes a set folder that has an <see cref="UnfinishedMarker"/>, the marker last: a deletion that breaks off
    /// (the storage refuses a file, the connection is lost) leaves it, and a later run of the task goes on with it.
    /// </summary>
    public static void DeleteMarkedFolder(DestinationStore store, string folder)
    {
        ArgumentNullException.ThrowIfNull(store);
        foreach (var entry in store.List(folder))
        {
            store.Cancellation.ThrowIfCancellationRequested();
            if (entry.IsFolder)
            {
                store.DeleteFolder(entry.Path);
            }
            else if (!string.Equals(entry.Name, UnfinishedMarker.FileName, StringComparison.OrdinalIgnoreCase))
            {
                store.DeleteFile(entry.Path);
            }
        }

        store.DeleteFolder(folder);
    }

    /// <summary>Deletes a folder of this computer. Read-only files are deleted too; links inside are removed, never followed.</summary>
    public static void DeleteSetFolder(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        LocalStore.Default.DeleteFolder(folder);
    }

    private static readonly Regex VolumeNameRegex = new(@"^(.+\.(?:7z|zip))\.(\d{3,})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Regex VolumeName() => VolumeNameRegex;
}
