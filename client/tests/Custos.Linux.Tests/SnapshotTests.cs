using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Service.Platform;

namespace Custos.Linux.Tests;

/// <summary>
/// Snapshots on real volumes made of loop disks (<see cref="Disks"/>): LVM with ext4 and XFS, a btrfs subvolume with a
/// nested one, a volume group without free room, and sources of which only some volumes can have one. After every run
/// nothing of the snapshot is left: no volume, no mount, no line in the file of pending ones. Needs root; the LVM ones
/// need the device-mapper of the kernel too (GitHub runners have it, most containers do not).
/// </summary>
[Collection(ServiceGroup.Name)]
public sealed class SnapshotTests(ServiceFixture service)
{
    [Theory]
    [InlineData("ext4", 600, 300)]
    [InlineData("xfs", 700, 320)]
    public async Task An_lvm_volume_is_read_from_its_snapshot_which_is_removed_afterwards(string fileSystem, int disk, int volume)
    {
        Disks.Require(Disks.CanLvm, "Needs root, LVM and the device-mapper of the kernel.");
        Disks.Require(Disks.Have("mkfs." + fileSystem), $"mkfs.{fileSystem} is not installed.");
        using var lvm = new Disks.LvmVolume(disk, volume, fileSystem);
        string source = Path.Combine(lvm.MountPoint, "src");
        ServiceTests.WriteFiles(source);
        string destination = TestPlace.NewFolder("lvm-dst");
        var task = await service.SaveAsync(WithSnapshots(ServiceTests.NewTask("LVM " + fileSystem, source, destination), VssFailureActions.Abort));

        var run = await service.RunAsync(task.Id);

        Assert.True(run.Status == RunStatuses.Success, run.ErrorMessage);
        Assert.Equal(ServiceTests.Files.Length, run.FilesProcessed);
        Assert.Contains(await service.RunLogAsync(run.RunId), l => l.Contains("LVM " + lvm.MountPoint, StringComparison.Ordinal));
        Assert.Equal(["data"], lvm.Volumes());
        Assert.Empty(Disks.Leftovers());
        var usage = ShadowCopies.ReadUsage()[lvm.Group + "/data"];
        Assert.True(usage.Size >= SnapshotPlan.MinSnapshotBytes, usage.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.False(usage.Overflowed);

        var state = await Readiness(task.Id);
        Assert.Equal(SnapshotStates.Ok, state.State);
        Assert.Equal(lvm.Group, state.VolumeGroup);
    }

    /// <summary>The usual case on a server installed with the whole disk given to the volume: no room in the group. Set
    /// to stop, the task stops and says why; set to go on, it copies without a snapshot with a warning. Either way the
    /// state for the site says «no room» and how to add a disk to the group.</summary>
    [Fact]
    public async Task Without_free_room_in_the_volume_group_the_task_stops_or_goes_on_and_says_how_to_fix_it()
    {
        Disks.Require(Disks.CanLvm, "Needs root, LVM and the device-mapper of the kernel.");
        using var lvm = new Disks.LvmVolume(300, 0);
        string source = Path.Combine(lvm.MountPoint, "src");
        ServiceTests.WriteFiles(source);
        string destination = TestPlace.NewFolder("full-dst");
        var task = await service.SaveAsync(WithSnapshots(ServiceTests.NewTask("LVM full", source, destination), VssFailureActions.Abort));

        var stopped = await service.RunAsync(task.Id);

        Assert.Equal(RunStatuses.Error, stopped.Status);
        Assert.Contains(lvm.Group, stopped.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(destination, "a.txt", SearchOption.AllDirectories));

        task.Vss.OnFailure = VssFailureActions.Continue;
        task = await service.SaveAsync(task, task.Revision);
        var copied = await service.RunAsync(task.Id);

        Assert.Equal(RunStatuses.Warning, copied.Status);
        Assert.Single(Directory.GetFiles(destination, "a.txt", SearchOption.AllDirectories));
        Assert.Equal(["data"], lvm.Volumes());
        Assert.Empty(Disks.Leftovers());

        var state = await Readiness(task.Id);
        Assert.Equal(SnapshotStates.NoSpace, state.State);
        Assert.Contains("vgextend " + lvm.Group, state.Detail, StringComparison.Ordinal);
    }

    /// <summary>A btrfs subvolume mounted as Ubuntu mounts "@": the snapshot is a read-only subvolume next to the files,
    /// and a subvolume inside the sources (as Docker makes) is read as it is, since a snapshot has an empty folder in
    /// its place.</summary>
    [Fact]
    public async Task A_btrfs_subvolume_is_read_from_its_snapshot_and_a_nested_one_as_it_is()
    {
        Disks.Require(Disks.CanBtrfs, "Needs root and btrfs.");
        using var btrfs = new Disks.BtrfsVolume();
        ServiceTests.WriteFiles(btrfs.Source);
        await File.WriteAllTextAsync(Path.Combine(btrfs.Nested, "nested.txt"), "nested", TestContext.Current.CancellationToken);
        string destination = TestPlace.NewFolder("btrfs-dst");
        var task = await service.SaveAsync(WithSnapshots(ServiceTests.NewTask("btrfs", btrfs.Source, destination), VssFailureActions.Abort));

        var run = await service.RunAsync(task.Id);

        Assert.True(run.Status == RunStatuses.Success, run.ErrorMessage);
        Assert.Equal(ServiceTests.Files.Length + 1, run.FilesProcessed);
        string copied = Assert.Single(Directory.GetFiles(destination, "nested.txt", SearchOption.AllDirectories));
        Assert.Equal("nested", await File.ReadAllTextAsync(copied, TestContext.Current.CancellationToken));
        Assert.Contains(await service.RunLogAsync(run.RunId), l => l.Contains("btrfs " + btrfs.MountPoint, StringComparison.Ordinal));
        Assert.Empty(btrfs.Snapshots());
        Assert.Empty(Disks.Leftovers());

        var state = await Readiness(task.Id);
        Assert.Equal(SnapshotStates.Ok, state.State);
        Assert.Equal(SnapshotMethods.Btrfs, state.Method);
    }

    /// <summary>Sources on two volumes, one of which cannot have a snapshot: set to stop, nothing is copied; set to go
    /// on, both are copied, the one without a snapshot with a warning.</summary>
    [Fact]
    public async Task When_only_some_volumes_of_the_sources_get_a_snapshot_the_task_stops_or_goes_on_as_set()
    {
        Disks.Require(Disks.CanLvm, "Needs root, LVM and the device-mapper of the kernel.");
        Assert.SkipWhen(Disks.CanSnapshot(TestPlace.Root), "The temporary folder of this computer can have snapshots.");
        using var lvm = new Disks.LvmVolume(600, 300);
        string onLvm = Path.Combine(lvm.MountPoint, "src");
        ServiceTests.WriteFiles(onLvm);
        var (plain, destination) = ServiceTests.Folders("partial");
        var task = await service.SaveAsync(WithSnapshots(ServiceTests.NewTask("Partial", [onLvm, plain], destination), VssFailureActions.Abort));

        var stopped = await service.RunAsync(task.Id);

        Assert.Equal(RunStatuses.Error, stopped.Status);
        Assert.Empty(Directory.GetFiles(destination, "a.txt", SearchOption.AllDirectories));
        Assert.Equal(["data"], lvm.Volumes());
        Assert.Empty(Disks.Leftovers());

        task.Vss.OnFailure = VssFailureActions.Continue;
        task = await service.SaveAsync(task, task.Revision);
        var copied = await service.RunAsync(task.Id);

        Assert.Equal(RunStatuses.Warning, copied.Status);
        Assert.Equal(2, Directory.GetFiles(destination, "a.txt", SearchOption.AllDirectories).Length);
        Assert.Contains(await service.RunLogAsync(copied.RunId), l => l.Contains("LVM " + lvm.MountPoint, StringComparison.Ordinal));
        Assert.Equal(["data"], lvm.Volumes());
        Assert.Empty(Disks.Leftovers());
    }

    private static TaskDefinition WithSnapshots(TaskDefinition task, string onFailure)
    {
        task.Vss = new VssOptions { Enabled = true, OnFailure = onFailure };
        return task;
    }

    /// <summary>The state of the snapshots of the task's volume, as the site gets it.</summary>
    private async Task<SnapshotReadiness> Readiness(string taskId)
    {
        var state = await ServiceFixture.WaitAsync(service.SnapshotAsync, s => s.Snapshots.Any(v => v.TaskIds.Contains(taskId)));
        return state.Snapshots.First(v => v.TaskIds.Contains(taskId));
    }
}
