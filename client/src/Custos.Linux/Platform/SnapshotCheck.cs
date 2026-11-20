using Custos.Core.Backup;
using Custos.Core.Model;
using Custos.Core.Text;

namespace Custos.Service.Platform;

/// <summary>
/// Whether the sources of the tasks with snapshots on can have them, volume by volume: what <c>custos status</c> shows
/// and the inventory sends to the site (A.8), with what to do about it. The usual problem is a volume group without
/// free room (an Ubuntu Server installed with the whole disk given to its volume); a snapshot that ran out of room in
/// the last run and one whose room is about what the last run needed are warned about too.
/// </summary>
internal static class SnapshotCheck
{
    /// <summary>Below this much room for a snapshot whose needs are not known yet, a busy run may fill it.</summary>
    public const long LowRoomBytes = 1024L * 1024 * 1024;

    /// <summary>The room should be this many times what the snapshot of the last run took.</summary>
    public const double Margin = 1.5;

    /// <summary>The volumes of the enabled tasks with snapshots on, one entry per volume with the tasks that use it.</summary>
    public static List<SnapshotReadiness> Check(IEnumerable<TaskDefinition> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var table = MountTable.Read();
        List<LogicalVolume>? volumes = null;
        string? lvmProblem = null;
        var usage = ShadowCopies.ReadUsage();
        var subvolumes = new Dictionary<string, string?>(StringComparer.Ordinal);
        var entries = new Dictionary<string, SnapshotReadiness>(StringComparer.Ordinal);
        foreach (var task in tasks.Where(t => t.Enabled && t.Vss.Enabled))
        {
            var paths = task.Sources.Select(s => s.Path)
                .Where(p => Path.IsPathFullyQualified(p) && !BackupEngine.IsNetworkPath(p) && !p.Contains('%', StringComparison.Ordinal))
                .ToList();
            if (paths.Count == 0)
            {
                continue;
            }

            var mounts = SnapshotPlan.Involved(paths, table);
            volumes ??= Lvm.List(out lvmProblem);
            foreach (var part in SnapshotPlan.Describe(mounts, volumes, lvmProblem).Where(p => !p.IsBoot))
            {
                var entry = Evaluate(part, usage, subvolumes);
                string key = part.Volume?.FullName ?? part.Mount.MountPoint;
                if (entries.TryGetValue(key, out var known))
                {
                    if (Severity(entry.State) > Severity(known.State))
                    {
                        entry.TaskIds = known.TaskIds;
                        entries[key] = entry;
                        known = entry;
                    }
                }
                else
                {
                    entries[key] = entry;
                    known = entry;
                }

                if (!known.TaskIds.Contains(task.Id, StringComparer.OrdinalIgnoreCase))
                {
                    known.TaskIds.Add(task.Id);
                }
            }
        }

        return [.. entries.Values.OrderBy(e => SnapshotPlan.Depth(e.Mount)).ThenBy(e => e.Mount, StringComparer.Ordinal)];
    }

    /// <summary>The state of one volume, with the advice for it.</summary>
    internal static SnapshotReadiness Evaluate(VolumePart part, IReadOnlyDictionary<string, SnapshotUsage> usage, Dictionary<string, string?>? subvolumes = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(usage);
        var mount = part.Mount;
        var entry = new SnapshotReadiness
        {
            Mount = mount.MountPoint,
            Device = mount.Source,
            FileSystem = mount.FileSystem,
            Method = part.Method,
            State = SnapshotStates.Ok,
            VolumeGroup = part.Volume?.Group,
            LogicalVolume = part.Volume?.Name,
            FreeBytes = part.Volume?.GroupFree,
            VolumeBytes = part.Volume?.Size,
        };

        if (part.Method == SnapshotMethods.None || (part.Problem is not null && part.Method != SnapshotMethods.Lvm))
        {
            entry.State = SnapshotStates.Unsupported;
            entry.Detail = LinuxStrings.Format(LinuxStrings.DetailUnsupported, mount.MountPoint, part.Problem ?? "?");
            return entry;
        }

        if (part.Method == SnapshotMethods.Btrfs)
        {
            // Only a subvolume can have a snapshot: a folder of one mounted on its own (a bind mount) cannot.
            string? problem = null;
            if (subvolumes is null || !subvolumes.TryGetValue(mount.MountPoint, out problem))
            {
                var (code, output) = Tool.Run("btrfs", ["subvolume", "show", mount.MountPoint], TimeSpan.FromMinutes(1));
                problem = code == 0 ? null : LinuxStrings.Format(LinuxStrings.ReasonNotSubvolume, Tool.Reason(output, "btrfs", code));
                subvolumes?.Add(mount.MountPoint, problem);
            }

            if (problem is not null)
            {
                entry.State = SnapshotStates.Unsupported;
                entry.Detail = LinuxStrings.Format(LinuxStrings.DetailUnsupported, mount.MountPoint, problem);
            }

            return entry;
        }

        if (part.Method != SnapshotMethods.Lvm || part.Volume!.IsThin)
        {
            return entry;
        }

        var volume = part.Volume;
        string group = volume.Group;
        var last = usage.GetValueOrDefault(volume.FullName);
        entry.LastSizeBytes = last?.Size;
        entry.LastUsedBytes = last?.Used;
        entry.LastRunAt = last?.At;
        long room = part.Extents * volume.ExtentSize;
        string free = Sizes.Format(volume.GroupFree);
        if (part.Problem is not null)
        {
            entry.State = SnapshotStates.NoSpace;
            entry.Detail = LinuxStrings.Format(LinuxStrings.DetailNoSpace, group, mount.MountPoint, free);
        }
        else if (last is { Overflowed: true } && room <= last.Size * 1.2)
        {
            entry.State = SnapshotStates.Overflowed;
            entry.Detail = LinuxStrings.Format(LinuxStrings.DetailOverflowed, mount.MountPoint, Sizes.Format(last.Size), group);
        }
        else if (last is { Overflowed: false } && room < last.Used * Margin)
        {
            entry.State = SnapshotStates.LowSpace;
            entry.Detail = LinuxStrings.Format(LinuxStrings.DetailLowSpace, group, free, Sizes.Format(last.Used), mount.MountPoint);
        }
        else if (last is null && room < LowRoomBytes)
        {
            entry.State = SnapshotStates.LowSpace;
            entry.Detail = LinuxStrings.Format(LinuxStrings.DetailLowSpaceUnknown, group, free, mount.MountPoint);
        }

        return entry;
    }

    private static int Severity(string state) => state switch
    {
        SnapshotStates.NoSpace or SnapshotStates.Overflowed => 3,
        SnapshotStates.LowSpace => 2,
        SnapshotStates.Unsupported => 1,
        _ => 0,
    };
}
