using Custos.Core.Backup;
using Custos.Service.Platform;

namespace Custos.Linux.Tests;

/// <summary>
/// Disks for the tests of snapshots: files attached as loop devices, with an LVM volume group or a btrfs file system on
/// them, mounted below the folder of the tests and taken apart again afterwards. Needs root, the tools of LVM and btrfs
/// and, for LVM, the device-mapper of the kernel (a container may have none: then those tests are skipped).
/// </summary>
internal static class Disks
{
    private const long MiB = 1024L * 1024;

    /// <summary>
    /// Skips a test of snapshots that cannot run here; with CUSTOS_REQUIRE_SNAPSHOTS=1 (CI as root, where they must run)
    /// fails it instead, so that they are never skipped there unnoticed.
    /// </summary>
    public static void Require(bool can, string why)
    {
        if (!can && TestPlace.IsRoot && Environment.GetEnvironmentVariable("CUSTOS_REQUIRE_SNAPSHOTS") == "1")
        {
            Assert.Fail(why + " CUSTOS_REQUIRE_SNAPSHOTS=1 says the tests of snapshots must run here.");
        }

        Assert.SkipUnless(can, why);
    }

    /// <summary>Whether all these programs are installed.</summary>
    public static bool Have(params string[] tools) => tools.All(t => Tool.Find(t) is not null);

    /// <summary>Whether LVM can make volumes here: its tools, root and the device-mapper of the kernel.</summary>
    public static bool CanLvm { get; } = TestPlace.IsRoot && Have("losetup", "pvcreate", "vgcreate", "lvcreate", "lvs", "dmsetup", "mkfs.ext4")
        && Tool.Run("dmsetup", ["version"]) is (0, var said) && said.Contains("Driver version", StringComparison.Ordinal);

    /// <summary>Whether btrfs file systems can be made and mounted here: the kernel knows btrfs, or loads its module
    /// (Ubuntu loads it only at the first mount of one).</summary>
    public static bool CanBtrfs { get; } = TestPlace.IsRoot && Have("losetup", "mkfs.btrfs", "btrfs")
        && (File.ReadAllText("/proc/filesystems").Contains("btrfs", StringComparison.Ordinal) || Tool.Run("modprobe", ["btrfs"]).ExitCode == 0);

    /// <summary>Runs a program and gives its standard output; a failure fails the test with what it said.</summary>
    public static string Run(string program, params string[] arguments)
    {
        var (code, output) = Tool.Run(program, arguments, out string standardOutput, TimeSpan.FromMinutes(2));
        Assert.True(code == 0, $"{program} {string.Join(' ', arguments)}: {code} {output}");
        return standardOutput;
    }

    /// <summary>Runs a program while taking things apart: what fails is left as it is.</summary>
    public static void Try(string program, params string[] arguments) => Tool.Run(program, arguments, TimeSpan.FromMinutes(2));

    /// <summary>Whether a folder lies on a volume the service can make a snapshot of.</summary>
    public static bool CanSnapshot(string path)
    {
        var table = MountTable.Read();
        var mounts = SnapshotPlan.Involved([path], table);
        var volumes = mounts.Any(m => m.FileSystem is not ("btrfs" or "zfs")) && Lvm.Installed ? Lvm.List(out _) : [];
        return SnapshotPlan.Describe(mounts, volumes, null).Exists(p => p.CanSnapshot);
    }

    /// <summary>The snapshots of CUSTOS still there: LVM volumes by their name, and what is mounted in its folder.</summary>
    public static List<string> Leftovers()
    {
        var left = new List<string>();
        if (Lvm.Installed)
        {
            left.AddRange(Lvm.List(out _).Where(v => v.Name.StartsWith(Lvm.SnapshotPrefix, StringComparison.Ordinal)).Select(v => v.FullName));
        }

        left.AddRange(MountTable.Read().Below(DataFolders.Snapshots).Select(m => m.MountPoint));
        if (File.Exists(DataFolders.PendingShadowsFile))
        {
            left.AddRange(File.ReadAllLines(DataFolders.PendingShadowsFile).Select(l => "pending: " + l));
        }

        if (Directory.Exists(DataFolders.Snapshots))
        {
            left.AddRange(Directory.GetDirectories(DataFolders.Snapshots).Select(d => "folder: " + d));
        }

        return left;
    }

    /// <summary>A file of this size attached as a loop device.</summary>
    public sealed class Loop : IDisposable
    {
        public Loop(int megabytes)
        {
            Image = Path.Combine(TestPlace.Root, "disk-" + Guid.NewGuid().ToString("N")[..6] + ".img");
            using (var file = File.Create(Image))
            {
                file.SetLength(megabytes * MiB);
            }

            Device = Run("losetup", "-f", "--show", Image).Trim();
        }

        public string Image { get; }

        public string Device { get; }

        public void Dispose()
        {
            Try("losetup", "-d", Device);
            File.Delete(Image);
        }
    }

    /// <summary>
    /// A volume group of one loop disk with the logical volume "data" on it, formatted and mounted. The room left in the
    /// group is what a snapshot gets: <paramref name="volumeMegabytes"/> 0 gives the volume all of it.
    /// </summary>
    public sealed class LvmVolume : IDisposable
    {
        private readonly Loop _disk;

        public LvmVolume(int diskMegabytes, int volumeMegabytes, string fileSystem = "ext4")
        {
            _disk = new Loop(diskMegabytes);
            Group = "clt" + Guid.NewGuid().ToString("N")[..6];
            try
            {
                Run("pvcreate", "-q", "-y", _disk.Device);
                Run("vgcreate", "-q", "-y", Group, _disk.Device);
                Run("lvcreate", "-q", "-y", "-Wy", "-n", "data", volumeMegabytes == 0 ? "-l" : "-L",
                    volumeMegabytes == 0 ? "100%FREE" : volumeMegabytes + "M", Group);
                Device = "/dev/" + Group + "/data";
                Try("udevadm", "settle");
                if (fileSystem == "xfs")
                {
                    Run("mkfs.xfs", "-q", "-f", Device);
                }
                else
                {
                    Run("mkfs." + fileSystem, "-q", "-F", Device);
                }

                MountPoint = TestPlace.NewFolder("lvm-" + fileSystem);
                Run("mount", Device, MountPoint);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public string Group { get; }

        public string Device { get; } = string.Empty;

        public string MountPoint { get; } = string.Empty;

        /// <summary>The logical volumes of the group: "data", and the snapshots of CUSTOS while there are any.</summary>
        public List<string> Volumes() =>
            [.. Run("lvs", "--noheadings", "-o", "lv_name", Group).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        public void Dispose()
        {
            if (MountPoint.Length > 0 && MountTable.Read().At(MountPoint) is not null)
            {
                Try("umount", MountPoint);
            }

            Try("lvremove", "-q", "-y", "-f", Group);
            Try("vgremove", "-q", "-y", "-f", Group);
            Try("pvremove", "-q", "-y", "-ff", _disk.Device);
            _disk.Dispose();
        }
    }

    /// <summary>
    /// A btrfs file system on a loop disk with its subvolume "@data" mounted (as Ubuntu mounts "@" at "/"), and in it the
    /// folder "src" with the subvolume "src/nested": the way Docker and snapper leave subvolumes inside the tree.
    /// </summary>
    public sealed class BtrfsVolume : IDisposable
    {
        private readonly Loop _disk;

        public BtrfsVolume()
        {
            _disk = new Loop(300);
            try
            {
                Run("mkfs.btrfs", "-q", "-f", _disk.Device);
                string top = TestPlace.NewFolder("btrfs-top");
                Run("mount", _disk.Device, top);
                Run("btrfs", "-q", "subvolume", "create", Path.Combine(top, "@data"));
                Run("umount", top);
                MountPoint = TestPlace.NewFolder("btrfs");
                Run("mount", "-o", "subvol=@data", _disk.Device, MountPoint);
                Directory.CreateDirectory(Source);
                Run("btrfs", "-q", "subvolume", "create", Nested);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public string MountPoint { get; } = string.Empty;

        public string Source => Path.Combine(MountPoint, "src");

        public string Nested => Path.Combine(Source, "nested");

        /// <summary>The subvolumes right inside "@data" whose names are those of the snapshots of CUSTOS.</summary>
        public List<string> Snapshots() =>
            [.. Run("btrfs", "subvolume", "list", "-o", MountPoint).Split('\n').Where(l => l.Contains(ShadowCopies.BtrfsPrefix, StringComparison.Ordinal))];

        public void Dispose()
        {
            if (MountPoint.Length > 0 && MountTable.Read().At(MountPoint) is not null)
            {
                Try("umount", MountPoint);
            }

            _disk.Dispose();
        }
    }
}
