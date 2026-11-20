using Custos.Core;
using Custos.Core.Backup;
using Custos.Core.Text;

namespace Custos.Service.Platform;

/// <summary>One mounted volume the sources of a task lie on, and how it can be read.</summary>
/// <param name="Method">One of <see cref="SnapshotMethods"/>.</param>
/// <param name="Volume">The logical volume, for <see cref="SnapshotMethods.Lvm"/>.</param>
/// <param name="Problem">Why it cannot have a snapshot (read as it is), or null.</param>
/// <param name="Extents">How big its LVM snapshot may be, in extents of its group (0 for a thin volume).</param>
internal sealed record VolumePart(MountEntry Mount, string Method, LogicalVolume? Volume, string? Problem, long Extents = 0)
{
    /// <summary>The partitions of the boot loader: they change only with a new kernel and never have a snapshot, so they
    /// are read as they are without a warning.</summary>
    public bool IsBoot => SnapshotPlan.BootMounts.Contains(Mount.MountPoint);

    /// <summary>A snapshot will be tried.</summary>
    public bool CanSnapshot => Problem is null && Method != SnapshotMethods.None;

    /// <summary>"/home" or "/ (ubuntu-vg/ubuntu-lv)", for the log.</summary>
    public string Name => Volume is null ? Mount.MountPoint : $"{Mount.MountPoint} ({Volume.FullName})";
}

/// <summary>
/// Which volumes the sources of a task lie on, and whether each can have a snapshot: LVM (with free room in its group),
/// btrfs or ZFS. Used by <see cref="ShadowCopies"/> before a run and by <see cref="SnapshotCheck"/> for the warnings.
/// </summary>
internal static class SnapshotPlan
{
    /// <summary>The smallest LVM snapshot worth making: less fills up within minutes on any server.</summary>
    public const long MinSnapshotBytes = 256L * 1024 * 1024;

    /// <summary>The share of the free room of a volume group the snapshots of one run may take; the rest stays free for
    /// whatever else needs it while the run lasts.</summary>
    public const double FreeShare = 0.95;

    public static readonly HashSet<string> BootMounts = new(StringComparer.Ordinal) { "/boot", "/boot/efi", "/efi" };

    /// <summary>
    /// The mounts the sources lie on and the mounts below them that hold data of this computer, shallow ones first. The
    /// mounts of the system (/proc, /sys, /run), of other computers (NFS, SMB) and of CUSTOS are left out, with everything
    /// below them: the scan skips them too (<see cref="LinuxBackupPlatform.SkipFolder"/>).
    /// </summary>
    public static List<MountEntry> Involved(IEnumerable<string> paths, MountTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var result = new Dictionary<string, MountEntry>(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            string full = MountTable.Trim(Path.GetFullPath(path));
            if (table.Of(full) is { } own)
            {
                result.TryAdd(own.MountPoint, own);
            }

            var skipped = new List<string>();
            foreach (var below in table.Below(full))
            {
                if (skipped.Exists(s => Inside(below.MountPoint, s)))
                {
                    continue;
                }

                if (MountTable.IsSystem(below.FileSystem) || MountTable.IsNetwork(below.FileSystem) || DataFolders.IsMountFolder(below.MountPoint))
                {
                    skipped.Add(below.MountPoint);
                    continue;
                }

                result.TryAdd(below.MountPoint, below);
            }
        }

        return [.. result.Values.OrderBy(m => Depth(m.MountPoint)).ThenBy(m => m.MountPoint, StringComparer.Ordinal)];
    }

    /// <summary>How each mount can be read, with the room of the LVM snapshots shared out within each volume group.</summary>
    /// <param name="lvmProblem">Why lvs failed, if it did.</param>
    public static List<VolumePart> Describe(IReadOnlyList<MountEntry> mounts, IReadOnlyList<LogicalVolume> volumes, string? lvmProblem)
    {
        var parts = mounts.Select(m => Describe(m, volumes, lvmProblem)).ToList();

        // One snapshot per logical volume: a volume mounted twice (a bind mount) is read from the same one.
        var lvmParts = parts.Where(p => p.Method == SnapshotMethods.Lvm && p.Problem is null && !p.Volume!.IsThin)
            .GroupBy(p => p.Volume!.FullName, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var group in lvmParts.GroupBy(p => p.Volume!.Group, StringComparer.Ordinal))
        {
            var first = group.First().Volume!;
            long free = (long)(first.GroupFree / first.ExtentSize * FreeShare);
            long share = free / group.Count();
            foreach (var part in group)
            {
                var volume = part.Volume!;

                // A snapshot never needs more than the volume itself (and a little for its own records).
                long whole = (volume.Size / volume.ExtentSize) + Math.Max(1, volume.Size / volume.ExtentSize / 100);
                sizes[volume.FullName] = Math.Min(share, whole);
            }
        }

        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            if (part.Method != SnapshotMethods.Lvm || part.Problem is not null || part.Volume!.IsThin)
            {
                continue;
            }

            long extents = sizes.GetValueOrDefault(part.Volume.FullName);
            parts[i] = extents * part.Volume.ExtentSize < MinSnapshotBytes
                ? part with { Problem = LinuxStrings.Format(LinuxStrings.ReasonNoFreeRoom, part.Volume.Group, Sizes.Format(part.Volume.GroupFree)) }
                : part with { Extents = extents };
        }

        return parts;
    }

    /// <summary>How one mount can be read (the room of an LVM snapshot is decided by the other overload).</summary>
    internal static VolumePart Describe(MountEntry mount, IReadOnlyList<LogicalVolume> volumes, string? lvmProblem)
    {
        ArgumentNullException.ThrowIfNull(mount);
        switch (mount.FileSystem)
        {
            case "btrfs":
                return new VolumePart(mount, SnapshotMethods.Btrfs, null, Tool.Find("btrfs") is null ? LinuxStrings.Format(LinuxStrings.ReasonToolMissing, "btrfs-progs") : null);
            case "zfs":
                return new VolumePart(mount, SnapshotMethods.Zfs, null, Tool.Find("zfs") is null ? LinuxStrings.Format(LinuxStrings.ReasonToolMissing, "zfsutils") : null);
        }

        if (Lvm.Find(volumes, mount) is { } volume)
        {
            return volume.IsSnapshot
                ? new VolumePart(mount, SnapshotMethods.None, volume, LinuxStrings.Format(LinuxStrings.ReasonIsSnapshot, volume.FullName))
                : new VolumePart(mount, SnapshotMethods.Lvm, volume, null);
        }

        bool mapped = mount.Source.StartsWith("/dev/mapper/", StringComparison.Ordinal) || mount.Source.StartsWith("/dev/dm-", StringComparison.Ordinal);
        string problem = mapped && !Lvm.Installed ? LinuxStrings.Format(LinuxStrings.ReasonToolMissing, "lvm2")
            : mapped && lvmProblem is not null ? lvmProblem
            : MountTable.IsSystem(mount.FileSystem) ? LinuxStrings.Format(LinuxStrings.ReasonSystemFs, mount.FileSystem)
            : LinuxStrings.Format(LinuxStrings.ReasonPlain, mount.Source, mount.FileSystem);
        return new VolumePart(mount, SnapshotMethods.None, null, problem);
    }

    /// <summary>The note of the run log for a volume read without a snapshot.</summary>
    public static string Note(string mountPoint, string reason) => LinuxStrings.Format(LinuxStrings.NoteNoSnapshot, mountPoint, reason);

    internal static bool Inside(string path, string folder) =>
        path == folder || folder == "/" || path.StartsWith(folder + "/", StringComparison.Ordinal);

    internal static int Depth(string mountPoint) => mountPoint == "/" ? 0 : mountPoint.Count(c => c == '/');
}
