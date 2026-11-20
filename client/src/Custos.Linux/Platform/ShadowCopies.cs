using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Custos.Core;
using Custos.Core.Backup;
using Custos.Core.Text;

namespace Custos.Service.Platform;

/// <summary>
/// The Linux counterpart of the shadow copies of Windows: a snapshot of every volume the sources lie on that can have
/// one (an LVM logical volume with free room in its group, a btrfs subvolume, a ZFS dataset), all made one right after
/// the other, and one read-only view of them in <see cref="DataFolders.Snapshots"/>/&lt;id&gt;/view that looks like
/// the real tree: the sources are read from <c>view + path</c>. A volume without a snapshot is put into the view as it
/// is (read-only), and the run gets a warning naming it; the partitions of the boot loader (/boot, /boot/efi) are read
/// as they are without one.
/// </summary>
/// <remarks>
/// <para>An LVM snapshot keeps the old blocks of what changes while it exists in the free room of the volume group; if
/// the changes outgrow it the snapshot becomes invalid and what was read from it cannot be trusted: <see cref="ISnapshotSet.Verify"/>
/// fails the run then. How much room the snapshots took is kept (<see cref="DataFolders.SnapshotUsageFile"/>), so that
/// <see cref="SnapshotCheck"/> can warn before it runs out.</para>
/// <para>Snapshots outlive the process: what is made is written to a file first, and whatever a crash left is removed
/// when the service starts again (LVM snapshots also carry the tag «custos»). The service runs in its own mount
/// namespace (PrivateMounts= of its unit), so nobody else sees the view, and it dies with the process.</para>
/// </remarks>
internal sealed class ShadowCopies(string pendingFile)
{
    /// <summary>Names of the snapshots of btrfs subvolumes, made at the top of the subvolume:
    /// "/.custos-snap-1a2b3c4d-0".</summary>
    internal const string BtrfsPrefix = ".custos-snap-";

    private const string ZfsPrefix = "custos-snap-";

    private readonly Lock _fileLock = new();

    public ISnapshotSet Create(IReadOnlyCollection<string> paths)
    {
        var table = MountTable.Read();
        var mounts = SnapshotPlan.Involved(paths, table);
        string? lvmProblem = null;
        var volumes = mounts.Any(m => m.FileSystem is not ("btrfs" or "zfs")) ? Lvm.List(out lvmProblem) : [];
        var parts = SnapshotPlan.Describe(mounts, volumes, lvmProblem);
        var wanted = parts.Where(p => !p.IsBoot).ToList();
        if (wanted.Count == 0)
        {
            return NoSnapshots.Instance;
        }

        if (!wanted.Exists(p => p.CanSnapshot))
        {
            throw new BackupException(Reasons(wanted.Select(p => (p.Mount.MountPoint, p.Problem!))));
        }

        string id = Guid.NewGuid().ToString("N")[..8];
        var set = new Set(this, id, [.. paths.Select(p => MountTable.Trim(Path.GetFullPath(p)))]);
        try
        {
            set.Build(parts, table);
            return set;
        }
        catch
        {
            set.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Removes what a run that never ended left behind: the view and its mounts, the snapshots it made (from the file
    /// and, for LVM, by the tag), and the shares of network folders still mounted.
    /// </summary>
    public int DeleteLeftovers()
    {
        SmbSession.UnmountLeftovers();
        UnmountBelow(DataFolders.Snapshots);
        List<(string Kind, string Value)> pending;
        lock (_fileLock)
        {
            pending = File.Exists(pendingFile) ? [.. File.ReadAllLines(pendingFile).Select(ParseLine).OfType<(string, string)>()] : [];
        }

        int count = 0;
        foreach (var (kind, value) in pending)
        {
            if (Remove(kind, value))
            {
                count += kind == "dir" ? 0 : 1;
                Forget(kind, value);
            }
        }

        if (Lvm.Installed)
        {
            foreach (string leftover in Lvm.Leftovers())
            {
                count += Lvm.Remove(leftover) ? 1 : 0;
            }
        }

        return count;
    }

    /// <summary>The path of the real tree a path inside a view stands for ("/var/lib/custos/snapshots/1a2b3c4d/view/home/a"
    /// → "/home/a"); any other path as it is.</summary>
    public static string Original(string readPath)
    {
        string root = DataFolders.Snapshots + "/";
        if (!readPath.StartsWith(root, StringComparison.Ordinal))
        {
            return readPath;
        }

        string rest = readPath[root.Length..];
        int slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 || !rest.AsSpan(slash).StartsWith("/view", StringComparison.Ordinal))
        {
            return readPath;
        }

        string inView = rest[(slash + "/view".Length)..];
        return inView.Length == 0 ? "/" : inView.StartsWith('/') ? inView : readPath;
    }

    /// <summary>"/: reason; /home: reason", for a run that gets no snapshot at all.</summary>
    internal static string Reasons(IEnumerable<(string MountPoint, string Reason)> problems) =>
        string.Join("; ", problems.Select(p => $"{p.MountPoint}: {p.Reason.TrimEnd('.')}"));

    /// <summary>The files of the snapshots of the last runs, per logical volume.</summary>
    internal static Dictionary<string, SnapshotUsage> ReadUsage()
    {
        try
        {
            if (File.Exists(DataFolders.SnapshotUsageFile))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(DataFolders.SnapshotUsageFile), LinuxJsonContext.Default.DictionaryStringSnapshotUsage)
                    ?? new Dictionary<string, SnapshotUsage>(StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Started over.
        }

        return new Dictionary<string, SnapshotUsage>(StringComparer.Ordinal);
    }

    private static void SaveUsage(IReadOnlyDictionary<string, SnapshotUsage> latest)
    {
        if (latest.Count == 0)
        {
            return;
        }

        try
        {
            var all = ReadUsage();
            foreach (var (volume, usage) in latest)
            {
                all[volume] = usage;
            }

            string temporary = DataFolders.SnapshotUsageFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, LinuxJsonContext.Default.DictionaryStringSnapshotUsage));
            File.Move(temporary, DataFolders.SnapshotUsageFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only the warnings lose a measurement.
        }
    }

    /// <summary>Unmounts everything mounted below a folder (not the folder itself), the deepest first.</summary>
    internal static bool UnmountBelow(string folder)
    {
        bool clean = true;
        foreach (var mount in MountTable.Read().Below(folder).Reverse())
        {
            clean &= Unmount(mount.MountPoint);
        }

        return clean;
    }

    private static bool Unmount(string mountPoint)
    {
        if (Tool.Run("umount", [mountPoint], TimeSpan.FromMinutes(2)).ExitCode == 0)
        {
            return true;
        }

        // Still in use (a process left a file open): detached now, gone once it is closed.
        return Tool.Run("umount", ["-l", mountPoint], TimeSpan.FromMinutes(1)).ExitCode == 0;
    }

    private static (string Kind, string Value)? ParseLine(string line)
    {
        int tab = line.IndexOf('\t', StringComparison.Ordinal);
        return tab > 0 && tab < line.Length - 1 ? (line[..tab], line[(tab + 1)..]) : null;
    }

    /// <summary>Removes one thing a run made; true when it is gone.</summary>
    private static bool Remove(string kind, string value)
    {
        switch (kind)
        {
            case "lvm":
                return Lvm.Remove(value);
            case "btrfs":
                if (!Directory.Exists(value))
                {
                    return true;
                }

                var (code, output) = Tool.Run("btrfs", ["subvolume", "delete", value], TimeSpan.FromMinutes(5));
                return code == 0 || !Directory.Exists(value) || output.Contains("No such file", StringComparison.Ordinal);
            case "zfs":
                var (zfsCode, zfsOutput) = Tool.Run("zfs", ["destroy", value], TimeSpan.FromMinutes(5));
                return zfsCode == 0 || zfsOutput.Contains("could not find", StringComparison.OrdinalIgnoreCase)
                    || zfsOutput.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
            case "dir":
                return RemoveEmptyTree(value);
            default:
                return true;
        }
    }

    /// <summary>Removes a folder of a view with the empty folders in it, never anything else: nothing is deleted while
    /// anything is still mounted there.</summary>
    private static bool RemoveEmptyTree(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return true;
        }

        var table = MountTable.Read();
        if (table.At(folder) is not null || table.Below(folder).Any())
        {
            return false;
        }

        try
        {
            var folders = Directory.EnumerateDirectories(folder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = 0,
                IgnoreInaccessible = true,
            }).OrderByDescending(f => f.Length).ToList();
            foreach (string inner in folders)
            {
                TryDeleteEmpty(inner);
            }

            TryDeleteEmpty(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return !Directory.Exists(folder);
    }

    private static void TryDeleteEmpty(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not empty or not ours: left as it is.
        }
    }

    private void Remember(string kind, string value)
    {
        lock (_fileLock)
        {
            File.AppendAllLines(pendingFile, [kind + "\t" + value]);
        }
    }

    private void Forget(string kind, string value)
    {
        lock (_fileLock)
        {
            if (!File.Exists(pendingFile))
            {
                return;
            }

            string line = kind + "\t" + value;
            var left = File.ReadAllLines(pendingFile).Where(l => l.Length > 0 && l != line).ToList();
            if (left.Count == 0)
            {
                File.Delete(pendingFile);
            }
            else
            {
                File.WriteAllLines(pendingFile, left);
            }
        }
    }

    /// <summary>The private mount of the snapshots folder: what is mounted below it never spreads to the rest of the
    /// system and the other way round, whatever the propagation of "/" is.</summary>
    private static void MakePrivateRoot()
    {
        if (MountTable.Read().At(DataFolders.Snapshots) is null)
        {
            Tool.Run("mount", ["--bind", DataFolders.Snapshots, DataFolders.Snapshots], TimeSpan.FromMinutes(1));
        }

        Tool.Run("mount", ["--make-private", DataFolders.Snapshots], TimeSpan.FromMinutes(1));
    }

    /// <summary>Mount options for a snapshot of a file system: XFS refuses a second file system with the same UUID and
    /// must not replay its log on it.</summary>
    internal static string MountOptions(string fileSystem) => fileSystem == "xfs" ? "ro,nouuid,norecovery" : "ro";

    /// <summary>
    /// The subvolumes inside a btrfs mount that are not mounted on their own (those of Docker, of snapper): a snapshot
    /// has only empty folders in their place. Those inside the sources are put into the view as they are. «list -o»
    /// gives the subvolumes right inside the mounted one, the outermost: a bind mount has the ones inside them too.
    /// </summary>
    internal static List<string> NestedSubvolumes(MountEntry mount, MountTable table, IReadOnlyList<string> sources)
    {
        var (code, _) = Tool.Run("btrfs", ["subvolume", "list", "-o", mount.MountPoint], out string output, TimeSpan.FromMinutes(1));
        return code == 0 ? NestedSubvolumes(output, mount, table, sources) : [];
    }

    /// <summary>The parsing of <see cref="NestedSubvolumes(MountEntry, MountTable, IReadOnlyList{string})"/>: lines
    /// "ID 258 gen 31 top level 256 path @/var/lib/docker/btrfs/subvolumes/x". The path is from the top of the file
    /// system ("&lt;FS_TREE&gt;/" in front with -a); versions of btrfs-progs give the ones below the mounted subvolume
    /// from there instead ("var/lib/docker/…"), which is taken as is.</summary>
    internal static List<string> NestedSubvolumes(string listing, MountEntry mount, MountTable table, IReadOnlyList<string> sources)
    {
        string root = mount.Root.Trim('/');
        var found = new List<string>();
        foreach (string line in listing.Split('\n'))
        {
            int at = line.IndexOf(" path ", StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            string path = line[(at + " path ".Length)..].Trim();
            if (path.StartsWith("<FS_TREE>/", StringComparison.Ordinal))
            {
                path = path["<FS_TREE>/".Length..];
            }

            string inMount = root.Length == 0 || path == root ? path[root.Length..]
                : path.StartsWith(root + "/", StringComparison.Ordinal) ? path[(root.Length + 1)..]
                : path;
            if (inMount.Length == 0)
            {
                continue;
            }

            string location = (mount.MountPoint == "/" ? string.Empty : mount.MountPoint) + "/" + inMount;
            if (location.Contains("/" + BtrfsPrefix, StringComparison.Ordinal)
                || table.At(location) is not null
                || !sources.Any(s => SnapshotPlan.Inside(location, s) || SnapshotPlan.Inside(s, location)))
            {
                continue;
            }

            found.Add(location);
        }

        found.Sort(StringComparer.Ordinal);
        var outermost = new List<string>();
        foreach (string location in found)
        {
            if (!outermost.Exists(o => SnapshotPlan.Inside(location, o)))
            {
                outermost.Add(location);
            }
        }

        return outermost;
    }

    /// <summary>The snapshots of one run and their view.</summary>
    private sealed class Set(ShadowCopies owner, string id, IReadOnlyList<string> sources) : ISnapshotSet
    {
        private readonly string _root = Path.Combine(DataFolders.Snapshots, id);
        private readonly List<string> _notes = [];
        private readonly List<string> _made = [];
        private readonly List<(string Kind, string Value)> _snapshots = [];
        private readonly List<LvmSnapshot> _lvm = [];
        private readonly HashSet<string> _inView = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _devices = new(StringComparer.Ordinal);
        private bool _disposed;
        private int _counter;

        private string View => _root + "/view";

        public string? Description => _made.Count == 0 ? null : string.Join(", ", _made);

        public IReadOnlyList<string> Notes => _notes;

        public string Map(string path) => View + MountTable.Trim(Path.GetFullPath(path));

        public void Build(List<VolumePart> parts, MountTable table)
        {
            owner.Remember("dir", _root);
            Directory.CreateDirectory(DataFolders.Snapshots);
            MakePrivateRoot();
            Directory.CreateDirectory(View, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            // First all the snapshots, one right after the other, so that the volumes are taken at about the same moment.
            var taken = new Dictionary<VolumePart, string>();
            var failed = new Dictionary<VolumePart, string>();
            foreach (var part in parts.Where(p => p.CanSnapshot))
            {
                try
                {
                    taken[part] = Take(part);
                }
                catch (BackupException ex)
                {
                    failed[part] = ex.Message;
                }
            }

            // Then the view, the outer volumes before the ones mounted inside them.
            foreach (var part in parts)
            {
                string target = Target(part.Mount.MountPoint);
                if (taken.TryGetValue(part, out string? snapshot))
                {
                    try
                    {
                        string nested = MountSnapshot(part, snapshot, target, table);
                        _made.Add(Kind(part.Method) + " " + part.Mount.MountPoint + nested);
                        continue;
                    }
                    catch (BackupException ex)
                    {
                        failed[part] = ex.Message;
                    }
                }

                BindLive(part.Mount.MountPoint, target);
                if (!part.IsBoot)
                {
                    _notes.Add(SnapshotPlan.Note(part.Name, part.Problem ?? failed.GetValueOrDefault(part) ?? "?"));
                }
            }

            if (_made.Count == 0)
            {
                throw new BackupException(Reasons(parts.Where(p => !p.IsBoot).Select(p => (p.Mount.MountPoint, p.Problem ?? failed.GetValueOrDefault(p) ?? "?"))));
            }
        }

        public string? Verify()
        {
            foreach (var snapshot in _lvm.Where(s => !s.Thin))
            {
                var now = Lvm.Get(snapshot.Group, snapshot.Name);
                double? percent = now is null || now.IsInvalid ? 100 : Lvm.UsedPercent(snapshot.Group + "/" + snapshot.Name);
                if (percent >= 100)
                {
                    snapshot.Overflowed = true;
                    return LinuxStrings.Format(LinuxStrings.SnapshotOverflowed, snapshot.MountPoint, Sizes.Format(snapshot.Bytes), snapshot.Group);
                }
            }

            return null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var usage = new Dictionary<string, SnapshotUsage>(StringComparer.Ordinal);
            foreach (var snapshot in _lvm.Where(s => !s.Thin))
            {
                var now = Lvm.Get(snapshot.Group, snapshot.Name);
                bool overflowed = snapshot.Overflowed || now is null || now.IsInvalid;
                double percent = overflowed ? 100 : Lvm.UsedPercent(snapshot.Group + "/" + snapshot.Name) ?? 0;
                usage[snapshot.Origin] = new SnapshotUsage
                {
                    Size = snapshot.Bytes,
                    Used = (long)(snapshot.Bytes * Math.Min(percent, 100) / 100),
                    Overflowed = overflowed || percent >= 100,
                    At = DateTimeOffset.UtcNow,
                };
            }

            SaveUsage(usage);
            UnmountBelow(_root);
            foreach (var (kind, value) in _snapshots.AsEnumerable().Reverse())
            {
                if (Remove(kind, value))
                {
                    owner.Forget(kind, value);
                }
            }

            if (RemoveEmptyTree(_root))
            {
                owner.Forget("dir", _root);
            }
        }

        /// <summary>Makes the snapshot of one volume: its device (LVM), its folder (btrfs) or its name (ZFS).</summary>
        private string Take(VolumePart part)
        {
            int number = _counter++;
            switch (part.Method)
            {
                case SnapshotMethods.Lvm:
                {
                    var volume = part.Volume!;
                    if (_lvm.Find(s => s.Origin == volume.FullName) is { } existing)
                    {
                        return existing.Device;
                    }

                    string name = $"{Lvm.SnapshotPrefix}{id}-{number}";
                    Track("lvm", volume.Group + "/" + name);
                    if ((volume.IsThin ? Lvm.CreateThinSnapshot(volume, name) : Lvm.CreateSnapshot(volume, name, part.Extents)) is { } problem)
                    {
                        throw new BackupException(problem);
                    }

                    var made = Lvm.Get(volume.Group, name);
                    string device = made?.Path ?? $"/dev/{volume.Group}/{name}";
                    _lvm.Add(new LvmSnapshot(volume.Group, name, volume.FullName, device, part.Mount.MountPoint, volume.IsThin, made?.Size ?? part.Extents * volume.ExtentSize));
                    return device;
                }

                case SnapshotMethods.Btrfs:
                {
                    var (isSubvolume, output) = Tool.Run("btrfs", ["subvolume", "show", part.Mount.MountPoint], TimeSpan.FromMinutes(1));
                    if (isSubvolume != 0)
                    {
                        throw new BackupException(LinuxStrings.Format(LinuxStrings.ReasonNotSubvolume, Tool.Reason(output, "btrfs", isSubvolume)));
                    }

                    string path = (part.Mount.MountPoint == "/" ? string.Empty : part.Mount.MountPoint) + "/" + BtrfsPrefix + id + "-" + number.ToString(CultureInfo.InvariantCulture);
                    Track("btrfs", path);
                    var (code, said) = Tool.Run("btrfs", ["subvolume", "snapshot", "-r", part.Mount.MountPoint, path], TimeSpan.FromMinutes(5));
                    return code == 0 ? path : throw new BackupException(Tool.Reason(said, "btrfs", code));
                }

                default:
                {
                    string name = part.Mount.Source + "@" + ZfsPrefix + id + "-" + number.ToString(CultureInfo.InvariantCulture);
                    Track("zfs", name);
                    var (code, said) = Tool.Run("zfs", ["snapshot", name], TimeSpan.FromMinutes(5));
                    return code == 0 ? name : throw new BackupException(Tool.Reason(said, "zfs", code));
                }
            }
        }

        /// <summary>Puts a snapshot into the view; returns what to add to its name in the log.</summary>
        private string MountSnapshot(VolumePart part, string snapshot, string target, MountTable table)
        {
            switch (part.Method)
            {
                case SnapshotMethods.Lvm:
                {
                    if (!_devices.TryGetValue(snapshot, out string? mounted))
                    {
                        mounted = _root + "/d" + _devices.Count.ToString(CultureInfo.InvariantCulture);
                        Directory.CreateDirectory(mounted);
                        Mount(["-t", part.Mount.FileSystem, "-o", MountOptions(part.Mount.FileSystem), snapshot, mounted]);
                        _devices[snapshot] = mounted;
                    }

                    string inside = part.Mount.Root == "/" ? mounted : mounted + part.Mount.Root;
                    Bind(inside, target, readOnly: true);
                    return string.Empty;
                }

                case SnapshotMethods.Btrfs:
                {
                    Bind(snapshot, target, readOnly: true);
                    var nested = NestedSubvolumes(part.Mount, table, sources);
                    foreach (string location in nested)
                    {
                        Bind(location, View + location, readOnly: true);
                    }

                    return nested.Count == 0 ? string.Empty : " " + LinuxStrings.Format(LinuxStrings.NestedSubvolumes, nested.Count);
                }

                default:
                    Mount(["-t", "zfs", snapshot, target]);
                    return string.Empty;
            }
        }

        private static void BindLive(string mountPoint, string target) => Bind(mountPoint, target, readOnly: true);

        private static void Bind(string source, string target, bool readOnly)
        {
            Mount(["--bind", source, target]);
            if (readOnly)
            {
                // Read-only in two steps: older mount tools ignore "ro" on a bind.
                Tool.Run("mount", ["-o", "remount,bind,ro", target], TimeSpan.FromMinutes(1));
            }
        }

        private static void Mount(string[] arguments)
        {
            var (code, output) = Tool.Run("mount", arguments, TimeSpan.FromMinutes(2));
            if (code != 0)
            {
                throw new BackupException(LinuxStrings.Format(LinuxStrings.ReasonMountFailed, Tool.Reason(output, "mount", code)));
            }
        }

        /// <summary>Where a mount point is in the view; a folder of a volume already there must exist in it.</summary>
        private string Target(string mountPoint)
        {
            string target = mountPoint == "/" ? View : View + mountPoint;
            bool coveredByView = _inView.Any(m => m != mountPoint && SnapshotPlan.Inside(mountPoint, m));
            _inView.Add(mountPoint);
            if (Directory.Exists(target))
            {
                return target;
            }

            if (coveredByView)
            {
                throw new BackupException(LinuxStrings.Format(LinuxStrings.ReasonNoFolder, mountPoint));
            }

            Directory.CreateDirectory(target);
            return target;
        }

        private void Track(string kind, string value)
        {
            owner.Remember(kind, value);
            _snapshots.Add((kind, value));
        }

        private static string Kind(string method) => method switch
        {
            SnapshotMethods.Lvm => "LVM",
            SnapshotMethods.Zfs => "ZFS",
            _ => method,
        };
    }

    /// <summary>An LVM snapshot of a run.</summary>
    private sealed record LvmSnapshot(string Group, string Name, string Origin, string Device, string MountPoint, bool Thin, long Bytes)
    {
        public bool Overflowed { get; set; }
    }
}

/// <summary>How much room the LVM snapshot of one volume took in the last run.</summary>
internal sealed class SnapshotUsage
{
    /// <summary>Its size.</summary>
    public long Size { get; set; }

    /// <summary>What the changes made during the run took of it.</summary>
    public long Used { get; set; }

    /// <summary>It ran out of room.</summary>
    public bool Overflowed { get; set; }

    public DateTimeOffset At { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, SnapshotUsage>))]
internal sealed partial class LinuxJsonContext : JsonSerializerContext;
