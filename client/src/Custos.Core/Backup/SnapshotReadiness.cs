namespace Custos.Core.Backup;

/// <summary>
/// Whether the sources on one volume can be read from a snapshot (CUSTOS for Linux, where that depends on how the disk
/// is laid out). The service checks it for the tasks with snapshots on, sends it to the site in the inventory (A.8),
/// and <c>custos status</c> shows it: a volume group without free room for an LVM snapshot is the usual reason.
/// </summary>
public sealed class SnapshotReadiness
{
    /// <summary>The mount point of the volume: "/", "/home".</summary>
    public required string Mount { get; set; }

    /// <summary>Its device: "/dev/mapper/ubuntu--vg-ubuntu--lv".</summary>
    public string? Device { get; set; }

    /// <summary>Its file system: "ext4", "xfs", "btrfs".</summary>
    public string? FileSystem { get; set; }

    /// <summary>One of <see cref="SnapshotMethods"/>.</summary>
    public required string Method { get; set; }

    /// <summary>One of <see cref="SnapshotStates"/>.</summary>
    public required string State { get; set; }

    /// <summary>The LVM volume group ("ubuntu-vg"), for <see cref="SnapshotMethods.Lvm"/>.</summary>
    public string? VolumeGroup { get; set; }

    /// <summary>The LVM logical volume ("ubuntu-lv").</summary>
    public string? LogicalVolume { get; set; }

    /// <summary>Room for snapshots: the free space of the volume group (VFree); null where it does not apply.</summary>
    public long? FreeBytes { get; set; }

    /// <summary>Size of the volume.</summary>
    public long? VolumeBytes { get; set; }

    /// <summary>Size of the snapshot of the last run on this volume (LVM), or null.</summary>
    public long? LastSizeBytes { get; set; }

    /// <summary>How much of it the changes made during that run took, or null.</summary>
    public long? LastUsedBytes { get; set; }

    /// <summary>When that run was.</summary>
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>What is wrong and what to do, in words (the language of the program); null when all is well.</summary>
    public string? Detail { get; set; }

    /// <summary>The tasks with snapshots on whose sources lie on this volume.</summary>
    public List<string> TaskIds { get; set; } = [];
}

/// <summary>Values of <see cref="SnapshotReadiness.Method"/>.</summary>
public static class SnapshotMethods
{
    /// <summary>A snapshot of an LVM logical volume (lvcreate --snapshot): needs free room in its volume group.</summary>
    public const string Lvm = "lvm";

    /// <summary>A read-only snapshot of a btrfs subvolume.</summary>
    public const string Btrfs = "btrfs";

    /// <summary>A snapshot of a ZFS dataset.</summary>
    public const string Zfs = "zfs";

    /// <summary>The volume cannot have one (an ordinary partition, for example): it is read as it is.</summary>
    public const string None = "none";
}

/// <summary>Values of <see cref="SnapshotReadiness.State"/>.</summary>
public static class SnapshotStates
{
    /// <summary>Snapshots work.</summary>
    public const string Ok = "ok";

    /// <summary>They work, but the room left is about what the last run needed or less.</summary>
    public const string LowSpace = "low_space";

    /// <summary>No snapshot can be made: the volume group has no free room.</summary>
    public const string NoSpace = "no_space";

    /// <summary>The snapshot of the last run ran out of room before the files were read: that run failed.</summary>
    public const string Overflowed = "overflowed";

    /// <summary>The volume cannot have a snapshot (<see cref="SnapshotMethods.None"/>).</summary>
    public const string Unsupported = "unsupported";

    /// <summary>Whether the site and <c>custos status</c> show it as a warning.</summary>
    public static bool IsProblem(string state) => state != Ok;
}
