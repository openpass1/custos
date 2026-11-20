using System.Text;
using Custos.Core.Backup;
using Custos.Service.Ipc;
using Custos.Service.Platform;

namespace Custos.Linux.Tests;

/// <summary>The platform classes of Linux that need neither root nor real volumes: what they read and decide.</summary>
public sealed class PlatformTests
{
    private const string MountInfo = """
        22 1 252:0 / / rw,relatime shared:1 - ext4 /dev/mapper/ubuntu--vg-ubuntu--lv rw
        23 22 0:21 / /proc rw,nosuid shared:12 - proc proc rw
        24 22 0:5 / /dev rw,nosuid shared:2 - devtmpfs udev rw
        25 22 0:22 / /sys rw,nosuid shared:7 - sysfs sysfs rw
        26 22 0:25 / /run rw,nosuid shared:5 - tmpfs tmpfs rw
        27 22 8:2 / /boot rw,relatime shared:30 - ext4 /dev/sda2 rw
        28 27 8:1 / /boot/efi rw,relatime shared:31 - vfat /dev/sda1 rw
        29 22 0:40 / /srv/nas rw,relatime shared:40 - cifs //nas/backup rw
        30 22 0:41 /@home /home rw,relatime shared:41 - btrfs /dev/sdb1 rw
        31 22 252:1 / /var/lib/mysql rw,relatime shared:42 - xfs /dev/mapper/data--vg-mysql rw
        32 22 0:43 / /srv/my\040files rw,relatime shared:43 - ext4 /dev/sdc1 rw
        33 23 0:44 / /proc/sys/fs/binfmt_misc rw,relatime shared:44 - binfmt_misc binfmt_misc rw
        """;

    private static MountTable Table() => new([.. MountInfo.Split('\n').Select(MountTable.Parse).OfType<MountEntry>()]);

    [Fact]
    public void Mountinfo_lines_are_read_with_device_root_and_escaped_names()
    {
        var table = Table();

        Assert.Equal(12, table.Entries.Count);
        var home = table.At("/home")!;
        Assert.Equal("/@home", home.Root);
        Assert.Equal("btrfs", home.FileSystem);
        Assert.Equal((252u, 0u), (table.At("/")!.Major, table.At("/")!.Minor));
        Assert.Equal("/srv/my files", table.At("/srv/my files")!.MountPoint);
        Assert.Null(MountTable.Parse("garbage"));
    }

    [Fact]
    public void A_path_lies_on_the_deepest_mount_above_it()
    {
        var table = Table();

        Assert.Equal("/", table.Of("/etc/fstab")!.MountPoint);
        Assert.Equal("/home", table.Of("/home/anna/doc.txt")!.MountPoint);
        Assert.Equal("/boot/efi", table.Of("/boot/efi/EFI")!.MountPoint);
        Assert.Contains("/home", table.Below("/").Select(m => m.MountPoint));
        Assert.Equal(["/boot/efi"], table.Below("/boot").Select(m => m.MountPoint));
        Assert.Equal("/proc", table.Below("/").First(m => m.MountPoint.StartsWith("/p", StringComparison.Ordinal)).MountPoint);
    }

    [Fact]
    public void Mounts_of_the_system_and_of_other_computers_are_told_apart()
    {
        Assert.True(MountTable.IsSystem("proc"));
        Assert.True(MountTable.IsSystem("tmpfs"));
        Assert.True(MountTable.IsSystem("overlay"));
        Assert.False(MountTable.IsSystem("ext4"));
        Assert.True(MountTable.IsNetwork("cifs"));
        Assert.True(MountTable.IsNetwork("nfs4"));
        Assert.True(MountTable.IsNetwork("fuse.sshfs"));
        Assert.False(MountTable.IsNetwork("xfs"));
    }

    [Fact]
    public void The_volumes_of_a_source_leave_out_the_system_network_and_what_is_below_them()
    {
        var involved = SnapshotPlan.Involved(["/"], Table()).Select(m => m.MountPoint).ToList();

        Assert.Equal(["/", "/boot", "/home", "/boot/efi", "/srv/my files", "/var/lib/mysql"], involved);
        Assert.DoesNotContain("/proc/sys/fs/binfmt_misc", involved);
        Assert.Equal(["/home"], SnapshotPlan.Involved(["/home/anna"], Table()).Select(m => m.MountPoint));
    }

    [Fact]
    public void Each_volume_gets_its_method_and_the_free_room_is_shared_within_a_group()
    {
        var table = Table();
        const long gib = 1024L * 1024 * 1024;
        LogicalVolume[] volumes =
        [
            new("ubuntu-vg", "ubuntu-lv", 252, 0, 20 * gib, "-wi-ao----", 10 * gib, 4 * 1024 * 1024, "/dev/mapper/ubuntu--vg-ubuntu--lv"),
            new("data-vg", "mysql", 252, 1, 50 * gib, "-wi-ao----", 0, 4 * 1024 * 1024, "/dev/mapper/data--vg-mysql"),
        ];

        var parts = SnapshotPlan.Describe(SnapshotPlan.Involved(["/", "/var/lib/mysql"], table), volumes, null);

        var root = parts.Single(p => p.Mount.MountPoint == "/");
        Assert.Equal(SnapshotMethods.Lvm, root.Method);
        Assert.Null(root.Problem);
        Assert.Equal((long)(10 * gib / (4 * 1024 * 1024) * SnapshotPlan.FreeShare), root.Extents);
        var mysql = parts.Single(p => p.Mount.MountPoint == "/var/lib/mysql");
        Assert.Contains("data-vg", mysql.Problem!, StringComparison.Ordinal);
        var boot = parts.Single(p => p.Mount.MountPoint == "/boot");
        Assert.True(boot.IsBoot);
        Assert.Equal(SnapshotMethods.None, boot.Method);
        Assert.Equal(SnapshotMethods.Btrfs, parts.Single(p => p.Mount.MountPoint == "/home").Method);
        Assert.Equal(SnapshotMethods.None, parts.Single(p => p.Mount.MountPoint == "/srv/my files").Method);
    }

    [Fact]
    public void Lvs_lines_are_read()
    {
        var volume = Lvm.Parse("  ubuntu-vg|ubuntu-lv|252|0|21470642176|-wi-ao----|0|4194304|/dev/mapper/ubuntu--vg-ubuntu--lv")!;

        Assert.Equal("ubuntu-vg/ubuntu-lv", volume.FullName);
        Assert.Equal(21470642176, volume.Size);
        Assert.Equal(0, volume.GroupFree);
        Assert.False(volume.IsThin);
        Assert.False(volume.IsSnapshot);
        Assert.True(Lvm.Parse("vg|snap|252|3|1073741824|swi-a-s---|0|4194304|")!.IsSnapshot);
        Assert.True(Lvm.Parse("vg|snap|252|3|1073741824|swi-I-s---|0|4194304|")!.IsInvalid);
        Assert.True(Lvm.Parse("vg|thin|252|4|1073741824|Vwi-aotz--|0|4194304|")!.IsThin);
        Assert.Null(Lvm.Parse("nonsense"));
    }

    [Fact]
    public void Nested_btrfs_subvolumes_inside_the_sources_are_found_outermost_first()
    {
        const string listing = """
            ID 258 gen 40 top level 256 path @/var/lib/docker/btrfs/subvolumes/aaa
            ID 259 gen 40 top level 258 path <FS_TREE>/@/var/lib/docker/btrfs/subvolumes/aaa/inner
            ID 260 gen 40 top level 256 path @/srv/data
            ID 261 gen 40 top level 256 path @/.custos-snap-1a2b3c4d-0
            ID 262 gen 40 top level 256 path @/opt/other
            ID 263 gen 40 top level 256 path srv/www
            """;
        var root = new MountEntry(0, 30, "/@", "/", "btrfs", "/dev/sda2");
        var table = new MountTable([root, new MountEntry(0, 31, "/@/srv/data", "/srv/data", "btrfs", "/dev/sda2")]);

        var nested = ShadowCopies.NestedSubvolumes(listing, root, table, ["/var/lib", "/srv"]);

        Assert.Equal(["/srv/www", "/var/lib/docker/btrfs/subvolumes/aaa"], nested);

        // The top of the file system mounted ("/"): paths from the top are paths in the mount.
        var top = new MountEntry(0, 40, "/", "/data", "btrfs", "/dev/sdb1");
        Assert.Equal(["/data/x"], ShadowCopies.NestedSubvolumes("ID 300 gen 1 top level 5 path x\n", top, new MountTable([top]), ["/data"]));
    }

    [Fact]
    public void A_path_in_a_snapshot_view_stands_for_the_same_path_of_the_real_tree()
    {
        string view = Path.Combine(DataFolders.Snapshots, "1a2b3c4d", "view");

        Assert.Equal("/home/anna", ShadowCopies.Original(view + "/home/anna"));
        Assert.Equal("/", ShadowCopies.Original(view));
        Assert.Equal("/etc", ShadowCopies.Original("/etc"));
        Assert.Equal(Path.Combine(DataFolders.Snapshots, "1a2b3c4d", "d0"), ShadowCopies.Original(Path.Combine(DataFolders.Snapshots, "1a2b3c4d", "d0")));
    }

    [Fact]
    public void Snapshots_of_xfs_are_mounted_without_its_uuid_and_log()
    {
        Assert.Equal("ro,nouuid,norecovery", ShadowCopies.MountOptions("xfs"));
        Assert.Equal("ro", ShadowCopies.MountOptions("ext4"));
    }

    [Fact]
    public void The_readiness_of_an_lvm_volume_follows_its_free_room_and_the_last_run()
    {
        const long mib = 1024L * 1024;
        var mount = new MountEntry(252, 0, "/", "/", "ext4", "/dev/mapper/vg-root");
        var volume = new LogicalVolume("vg", "root", 252, 0, 20_000 * mib, "-wi-ao----", 5_000 * mib, 4 * mib, "/dev/mapper/vg-root");
        var none = new Dictionary<string, SnapshotUsage>();
        var part = new VolumePart(mount, SnapshotMethods.Lvm, volume, null, Extents: 4 * 1000 / 4 * 1);

        Assert.Equal(SnapshotStates.Ok, SnapshotCheck.Evaluate(part with { Extents = 1200 }, none).State);
        var small = SnapshotCheck.Evaluate(part with { Extents = 100 }, none);
        Assert.Equal(SnapshotStates.LowSpace, small.State);
        Assert.Contains("vgextend vg", small.Detail!, StringComparison.Ordinal);

        var busy = new Dictionary<string, SnapshotUsage> { ["vg/root"] = new() { Size = 4000 * mib, Used = 3000 * mib, At = DateTimeOffset.UtcNow } };
        Assert.Equal(SnapshotStates.LowSpace, SnapshotCheck.Evaluate(part with { Extents = 1000 }, busy).State);
        Assert.Equal(SnapshotStates.Ok, SnapshotCheck.Evaluate(part with { Extents = 2000 }, busy).State);

        var overflowed = new Dictionary<string, SnapshotUsage> { ["vg/root"] = new() { Size = 4000 * mib, Used = 4000 * mib, Overflowed = true, At = DateTimeOffset.UtcNow } };
        var full = SnapshotCheck.Evaluate(part with { Extents = 1000 }, overflowed);
        Assert.Equal(SnapshotStates.Overflowed, full.State);
        Assert.Equal(4000 * mib, full.LastSizeBytes);

        var noRoom = SnapshotCheck.Evaluate(part with { Problem = "no room" }, none);
        Assert.Equal(SnapshotStates.NoSpace, noRoom.State);
        Assert.Contains("pvcreate", noRoom.Detail!, StringComparison.Ordinal);
        Assert.Equal("vg", noRoom.VolumeGroup);

        var plain = SnapshotCheck.Evaluate(new VolumePart(mount with { FileSystem = "ext4", Source = "/dev/sda1" }, SnapshotMethods.None, null, "plain"), none);
        Assert.Equal(SnapshotStates.Unsupported, plain.State);
    }

    [Fact]
    public void Network_paths_split_into_server_share_and_rest()
    {
        Assert.True(SmbSession.TryParse(@"\\nas\backup\pc1\x", out string server, out string share, out string rest));
        Assert.Equal(("nas", "backup", "pc1/x"), (server, share, rest));
        Assert.True(SmbSession.TryParse("//nas/backup", out server, out share, out rest));
        Assert.Equal(("nas", "backup", string.Empty), (server, share, rest));
        Assert.False(SmbSession.TryParse("/srv/backup", out _, out _, out _));
    }

    [Fact]
    public void The_credentials_file_of_mount_cifs_takes_the_domain_from_the_user()
    {
        Assert.Equal("username=anna\npassword=p@ss word\ndomain=OFFICE\n", SmbSession.CredentialsText(@"OFFICE\anna", null, "p@ss word"));
        Assert.Equal("username=anna\npassword=x\n", SmbSession.CredentialsText("anna", null, "x"));
        Assert.Equal("username=anna\npassword=x\ndomain=CORP\n", SmbSession.CredentialsText("anna", "CORP", "x"));
        Assert.Equal(LinuxStrings.ShareAccessDenied, SmbSession.Reason("mount error(13): Permission denied", 32));
        Assert.Equal(LinuxStrings.ShareNotFound, SmbSession.Reason("mount error(112): Host is down", 32));
    }

    [Fact]
    public void Labels_of_disks_are_read_as_udev_writes_them()
    {
        Assert.Equal("Backup disk", LinuxBackupPlatform.UdevName(@"Backup\x20disk"));
        Assert.Equal("BACKUP-USB", LinuxBackupPlatform.UdevName("BACKUP-USB"));
        Assert.Equal("Копии", LinuxBackupPlatform.UdevName(@"\xd0\x9a\xd0\xbe\xd0\xbf\xd0\xb8\xd0\xb8"));
    }

    [Fact]
    public void The_name_of_the_system_comes_from_os_release()
    {
        string file = Path.Combine(TestPlace.NewFolder("os"), "os-release");
        File.WriteAllText(file, "NAME=\"Ubuntu\"\nPRETTY_NAME=\"Ubuntu 24.04.3 LTS\"\n# comment\nID=ubuntu\n");

        var values = MachineInfo.ReadOsRelease(file);

        Assert.Equal("Ubuntu 24.04.3 LTS", values["PRETTY_NAME"]);
        Assert.Equal("ubuntu", values["ID"]);
        Assert.Contains("Linux", MachineInfo.OsDescription(), StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("unknown", MachineInfo.MachineGuid());
    }

    [Fact]
    public void Passwords_are_protected_with_the_key_of_this_computer()
    {
        var protector = new KeyFileProtector();
        byte[] secret = Encoding.UTF8.GetBytes("Пароль от NAS");

        byte[] stored = protector.Protect(secret);

        Assert.Equal(secret, protector.Unprotect(stored));
        Assert.NotEqual(secret, stored[3..]);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyFileProtector.KeyFile));
        stored[^1] ^= 1;
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => protector.Unprotect(stored));
    }

    [Fact]
    public void Owner_and_permissions_of_a_file_are_kept_and_restored()
    {
        string file = Path.Combine(TestPlace.NewFolder("posix"), "f.txt");
        File.WriteAllText(file, "x");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        byte[] kept = PosixSecurity.Read(file)!;
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);

        Assert.StartsWith("custos-posix-1:", Encoding.ASCII.GetString(kept), StringComparison.Ordinal);
        Assert.True(PosixSecurity.Write(file, kept));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(file));
        Assert.False(PosixSecurity.Write(file, [1, 2, 3]));
    }

    [Fact]
    public void Root_is_an_administrator()
    {
        Assert.True(ClientIdentity.IsAdministrator(Native.Account("root")!));
        Assert.Equal(0u, Native.Account(0)!.Uid);
    }

    [Fact]
    public void Database_servers_of_this_computer_are_told_by_their_address()
    {
        Assert.True(DbFinder.IsThisComputer("localhost"));
        Assert.True(DbFinder.IsThisComputer("127.0.0.1"));
        Assert.True(DbFinder.IsThisComputer(""));
        Assert.False(DbFinder.IsThisComputer("db.example.org"));
        Assert.Null(DbFinder.LocalAccess(new Core.Model.TaskDatabaseServer { Host = "10.0.0.5" }));
        Assert.Null(DbFinder.LocalAccess(new Core.Model.TaskDatabaseServer { Host = "localhost" })!.RunAs);
    }

    [Fact]
    public void Folders_of_the_system_and_of_custos_are_skipped_when_met_in_a_source()
    {
        var platform = new LinuxBackupPlatform(new Core.Agent.ConfigStore(DataFolders.Config, new KeyFileProtector()), new ShadowCopies(DataFolders.PendingShadowsFile));

        Assert.NotNull(platform.SkipFolder("/proc"));
        Assert.NotNull(platform.SkipFolder(DataFolders.Snapshots));
        Assert.NotNull(platform.SkipFolder("/srv/.custos-snap-1a2b3c4d-0"));
        Assert.Null(platform.SkipFolder("/etc"));
        Assert.NotNull(platform.SkipFolder(Path.Combine(DataFolders.Snapshots, "1a2b3c4d", "view", "proc")));
    }
}
