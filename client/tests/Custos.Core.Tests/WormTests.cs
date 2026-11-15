using Custos.Core.Backup;
using Custos.Core.Destinations;
using Custos.Core.Model;

namespace Custos.Core.Tests;

/// <summary>A folder, a share or a USB disk whose storage has WORM (section 6.3): the store and its self-check.</summary>
public sealed class WormStoreTests : IDisposable
{
    private static readonly DateTimeOffset Written = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly WormSettings Worm = new(Days: 7, LockHours: 3);

    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private LocalStore Store(WormSettings? worm = null) => new(_temp.Path) { Worm = worm ?? Worm };

    private string CheckFile => _temp.Combine(WormCheck.FileName);

    [Fact]
    public void Worm_takes_the_days_and_the_delay_of_folders_shares_and_usb_disks_only()
    {
        var local = new TaskDestination { Kind = DestinationKinds.Unc, Path = @"\\nas\backup", WormDays = 7, WormLockHours = 3 };
        Assert.Equal(new WormSettings(7, 3), WormSettings.Of(local));
        Assert.True(local.ProtectsFiles);
        Assert.Equal(TimeSpan.FromHours(4), WormSettings.Of(local)!.LockedAfter);
        Assert.Equal(TimeSpan.FromDays(7) + TimeSpan.FromHours(4), WormSettings.Of(local)!.Protection);

        Assert.Null(WormSettings.Of(new TaskDestination { Kind = DestinationKinds.Local, Path = @"D:\Backup" }));
        Assert.Null(WormSettings.Of(new TaskDestination { Kind = DestinationKinds.Sftp, WormDays = 7 }));
        Assert.Equal(WormSettings.MaxLockHours, WormSettings.Of(new TaskDestination { Kind = DestinationKinds.Usb, WormDays = 1, WormLockHours = 1000 })!.LockHours);
    }

    [Fact]
    public void Worm_storage_never_renames_folders_and_does_not_try()
    {
        var store = Store();

        Assert.False(store.RenamesFolders);
        Assert.False(store.CanRenameFolders(_temp.Path));
        Assert.True(store.ProtectsFiles);
        Assert.Empty(Directory.GetFileSystemEntries(_temp.Path));
        Assert.Equal([Messages.Format(Messages.StoreWormInfo, 7, 3)], store.Describe());
    }

    [Fact]
    public void The_access_check_leaves_nothing_behind_where_the_lock_waits()
    {
        Store().Probe();

        Assert.Empty(Directory.GetFileSystemEntries(_temp.Path));
    }

    [Fact]
    public void A_file_is_locked_until_the_delay_the_days_and_a_margin_are_over()
    {
        var store = Store();
        string fresh = _temp.WriteFile("fresh.txt", "x", DateTime.UtcNow.AddHours(-1));
        string old = _temp.WriteFile("old.txt", "x", DateTime.UtcNow.AddDays(-8));

        var until = store.LockedUntil(fresh);
        Assert.NotNull(until);
        Assert.Equal(File.GetLastWriteTimeUtc(fresh) + Worm.Protection, until.Value.UtcDateTime);
        Assert.Null(store.LockedUntil(old));
        Assert.Null(store.LockedUntil(_temp.Combine("missing.txt")));
        Assert.Null(new LocalStore(_temp.Path).LockedUntil(fresh));
    }

    [Fact]
    public void Copies_on_worm_storage_do_not_keep_the_read_only_attribute()
    {
        // Clearing it before a deletion would be a change, and WORM refuses changes even when deletion is allowed.
        string plain = _temp.WriteFile("plain.txt", "x", DateTime.UtcNow);
        string worm = _temp.WriteFile("worm.txt", "x", DateTime.UtcNow);

        new LocalStore(_temp.Path).SetFileTimes(plain, DateTime.UtcNow, DateTime.UtcNow, FileAttributes.ReadOnly);
        Store().SetFileTimes(worm, DateTime.UtcNow, DateTime.UtcNow, FileAttributes.ReadOnly);

        Assert.True(File.GetAttributes(plain).HasFlag(FileAttributes.ReadOnly));
        Assert.False(File.GetAttributes(worm).HasFlag(FileAttributes.ReadOnly));
        new LocalStore(_temp.Path).DeleteFile(plain);
        Assert.False(File.Exists(plain));
    }

    [Fact]
    public void The_check_file_is_written_like_a_copy_of_an_old_file()
    {
        var result = WormCheck.Run(Store(), Worm, Written);

        Assert.Equal(WormCheckOutcome.Started, result.Outcome);
        Assert.Equal(Written, result.Written);
        Assert.Equal(Written + Worm.LockedAfter, result.Next);
        Assert.Equal(WormCheck.OldTime, File.GetLastWriteTimeUtc(CheckFile));
        Assert.Contains("written: 2026-11-01T10:00:00", File.ReadAllText(CheckFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Before_the_lock_the_check_only_waits()
    {
        var store = new GuardedStore(Store());
        WormCheck.Run(store, Worm, Written);

        var result = WormCheck.Run(store, Worm, Written.AddHours(2));

        Assert.Equal(WormCheckOutcome.Waiting, result.Outcome);
        Assert.Equal(Written, result.Written);
        Assert.Equal(Written + Worm.LockedAfter, result.Next);
        Assert.Empty(store.Deleted);
    }

    [Fact]
    public void A_refused_deletion_shows_that_the_protection_works()
    {
        var store = new GuardedStore(Store()) { Refuse = path => path.EndsWith(WormCheck.FileName, StringComparison.Ordinal) };
        WormCheck.Run(store, Worm, Written);

        var result = WormCheck.Run(store, Worm, Written.AddHours(5));

        Assert.Equal(WormCheckOutcome.Protected, result.Outcome);
        Assert.Equal(Written, result.Written);
        Assert.Equal(Written + Worm.Protection, result.Next);
        Assert.True(File.Exists(CheckFile));
    }

    [Fact]
    public void A_deletion_that_works_shows_no_protection_and_the_check_starts_again()
    {
        var store = Store();
        WormCheck.Run(store, Worm, Written);

        var result = WormCheck.Run(store, Worm, Written.AddHours(5));

        Assert.Equal(WormCheckOutcome.NotProtected, result.Outcome);
        Assert.Equal(Written, result.Written);
        Assert.Equal(Written.AddHours(5) + Worm.LockedAfter, result.Next);
        Assert.Equal(Written.AddHours(5), WormCheck.Run(store, Worm, Written.AddHours(6)).Written);
    }

    [Fact]
    public void After_the_days_the_check_file_goes_and_a_new_check_begins()
    {
        var store = Store();
        WormCheck.Run(store, Worm, Written);
        var later = Written + Worm.Protection;

        var result = WormCheck.Run(store, Worm, later);

        Assert.Equal(WormCheckOutcome.Started, result.Outcome);
        Assert.Equal(later, result.Written);
    }

    [Fact]
    public void A_file_still_protected_after_the_days_means_the_storage_keeps_files_longer()
    {
        var store = new GuardedStore(Store()) { Refuse = path => path.EndsWith(WormCheck.FileName, StringComparison.Ordinal) };
        WormCheck.Run(store, Worm, Written);

        // Within the margin the clocks may still differ.
        Assert.Equal(WormCheckOutcome.Waiting, WormCheck.Run(store, Worm, Written + Worm.Protection - TimeSpan.FromMinutes(30)).Outcome);
        Assert.Equal(WormCheckOutcome.ProtectedLonger, WormCheck.Run(store, Worm, Written + Worm.Protection + TimeSpan.FromHours(1)).Outcome);
    }

    [Fact]
    public void A_file_of_that_name_that_is_not_a_check_file_is_replaced()
    {
        File.WriteAllText(CheckFile, "something else");

        var result = WormCheck.Run(Store(), Worm, Written);

        Assert.Equal(WormCheckOutcome.Started, result.Outcome);
        Assert.Contains("written: ", File.ReadAllText(CheckFile), StringComparison.Ordinal);
    }
}

/// <summary>What runs do with a destination that has WORM, and with what earlier runs left (sections 6.3, 6.6).</summary>
public sealed class WormBackupTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Modified = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly TempFolder _temp = new();
    private readonly TestPlatform _platform = new();
    private readonly MemoryFileStateStore _store = new();
    private readonly TestLog _log = new();
    private readonly string _source;
    private readonly string _destination;

    public WormBackupTests()
    {
        _source = _temp.Combine("Data");
        _destination = _temp.Combine("Backup");
        _temp.WriteFile("Data/a.txt", "alpha", Modified);
        _temp.WriteFile("Data/sub/b.txt", "beta", Modified);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_set_on_worm_storage_is_written_in_place_and_the_protection_is_tested()
    {
        var task = NewTask(BackupTypes.Full, wormDays: 7);
        File.SetAttributes(_temp.Combine("Data", "a.txt"), FileAttributes.ReadOnly);

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Success, result.Status);
        string set = Path.Combine(_destination, result.SetName!);
        string copy = Path.Combine(set, "Data", "a.txt");
        Assert.Equal("alpha", File.ReadAllText(copy));
        Assert.Equal(Modified, File.GetLastWriteTimeUtc(copy));
        Assert.False(File.GetAttributes(copy).HasFlag(FileAttributes.ReadOnly));
        Assert.True(File.Exists(Path.Combine(set, SetManifest.FileName)));
        Assert.False(File.Exists(Path.Combine(set, UnfinishedMarker.FileName)));
        Assert.DoesNotContain(_log.Lines, l => l.Message == Messages.Format(Messages.FolderRenameRefused, _destination));
        Assert.True(File.Exists(Path.Combine(_destination, WormCheck.FileName)));
        Assert.Contains(_log.Lines, l => l.Level == RunLogLevel.Info && l.Message.StartsWith(_destination + ": ", StringComparison.Ordinal)
            && l.Message.Contains(WormCheck.FileName, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(BackupTypes.Mirror, true)]
    [InlineData(BackupTypes.Full, false)]
    public void Worm_storage_takes_neither_a_mirror_nor_a_shared_folder(string type, bool separate)
    {
        var task = NewTask(type, wormDays: 7);
        task.Layout.SeparateFolders = separate;

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.Contains(_log.Lines, l => l.Message.Contains(Messages.Format(Messages.WormNeedsSets, _destination, 7), StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(_destination, "Data")));
    }

    [Fact]
    public void Retention_keeps_a_set_until_its_protection_is_over()
    {
        var task = NewTask(BackupTypes.Full, wormDays: 7);
        task.Retention.KeepFull = 1;

        var first = Run(task, Start, "run-1");
        var second = Run(task, Start.AddDays(1), "run-2");

        Assert.Equal(RunStatuses.Success, second.Status);
        Assert.Equal(0, second.DeletedSets);
        Assert.True(Directory.Exists(Path.Combine(_destination, first.SetName!)));
        Assert.Contains(_log.Lines, l => l.Message.StartsWith(Messages.Format(Messages.SetLockedWorm, first.SetName, _destination, "\u0001").Split('\u0001')[0], StringComparison.Ordinal));

        // Nine days later its manifest, written last, is free; the second set is still protected.
        File.SetLastWriteTimeUtc(Path.Combine(_destination, first.SetName!, SetManifest.FileName), DateTime.UtcNow.AddDays(-9));
        var third = Run(task, Start.AddDays(2), "run-3");

        Assert.Equal(RunStatuses.Success, third.Status);
        Assert.Equal(1, third.DeletedSets);
        var left = Directory.GetDirectories(_destination).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal([second.SetName, third.SetName], left);
    }

    [Fact]
    public void A_chain_is_deleted_from_its_newest_set_and_a_refusal_stops_it()
    {
        var task = NewTask(BackupTypes.Incremental);
        task.Retention.KeepFull = 1;
        var run1 = Run(task, Start, "run-1");
        _temp.WriteFile("Data/a.txt", "alpha 2", Modified.AddHours(1));
        var run2 = Run(task, Start.AddDays(1), "run-2");
        _temp.WriteFile("Data/a.txt", "alpha 3", Modified.AddHours(2));
        var run3 = Run(task, Start.AddDays(2), "run-3");
        string set1 = Path.Combine(_destination, run1.SetName!);
        string set2 = Path.Combine(_destination, run2.SetName!);
        string set3 = Path.Combine(_destination, run3.SetName!);

        // The storage will not let a file of the second set go.
        var deleted = new List<string>();
        _platform.WrapStore = store => new GuardedStore(store, deleted)
        {
            Refuse = path => path.StartsWith(Path.Combine(set2, "Data"), StringComparison.Ordinal),
        };
        task.BackupType = BackupTypes.Full;
        var run4 = Run(task, Start.AddDays(3), "run-4");

        Assert.Equal(RunStatuses.Warning, run4.Status);
        Assert.Equal(1, run4.DeletedSets);
        Assert.False(Directory.Exists(set3));
        Assert.Contains(_log.Lines, l => l.Level == RunLogLevel.Warning && l.Message.Contains(run2.SetName!, StringComparison.Ordinal));
        Assert.True(deleted.IndexOf(Path.Combine(set3, SetManifest.FileName)) < deleted.IndexOf(Path.Combine(set2, SetManifest.FileName)));
        Assert.DoesNotContain(Path.Combine(set1, SetManifest.FileName), deleted);

        // The full set stays whole; what is left of the second one is marked, and no longer a set.
        Assert.True(File.Exists(Path.Combine(set1, SetManifest.FileName)));
        Assert.False(File.Exists(Path.Combine(set2, SetManifest.FileName)));
        var marker = UnfinishedMarker.TryRead(new LocalStore(_destination), set2);
        Assert.NotNull(marker);
        Assert.Equal(UnfinishedMarker.Removing, marker.State);
        Assert.Equal(task.Id, marker.TaskId);

        // The next run deletes the old chain and what the deletion left.
        _platform.WrapStore = null;
        var run5 = Run(task, Start.AddDays(4), "run-5");

        Assert.Equal(RunStatuses.Success, run5.Status);
        Assert.False(Directory.Exists(set1));
        Assert.False(Directory.Exists(set2));
        Assert.Contains(_log.Lines, l => l.Message == Messages.Format(Messages.LeftoverSetRemoved, run2.SetName, _destination));
    }

    [Fact]
    public void Sets_that_runs_never_finished_are_removed_once_nobody_writes_them()
    {
        var task = NewTask(BackupTypes.Full);
        var now = DateTimeOffset.UtcNow;
        string old = Unfinished("Docs 2026-10-20 10-00 (Full)", task.Id, UnfinishedMarker.Writing, now.AddDays(-3));
        string partial = Unfinished("Docs 2026-10-21 10-00 (Full)" + SetCatalog.PartialSuffix, task.Id, UnfinishedMarker.Writing, now.AddDays(-3));
        string young = Unfinished("Docs 2026-10-22 10-00 (Full)", task.Id, UnfinishedMarker.Writing, now.AddHours(-2));
        string removing = Unfinished("Docs 2026-10-23 10-00 (Full)", task.Id, UnfinishedMarker.Removing, now.AddMinutes(-5));
        string other = Unfinished("Other 2026-10-20 10-00 (Full)", "other-task", UnfinishedMarker.Writing, now.AddDays(-3));
        string unmarked = _temp.Combine("Backup", "Photos");
        _temp.WriteFile("Backup/Photos/p.jpg", "photo", Modified);
        string oldTrial = _temp.WriteFile("Backup/" + DestinationStore.ProbePrefix + "aaaa1111.tmp", "CUSTOS", DateTime.UtcNow.AddDays(-2));
        string youngTrial = _temp.WriteFile("Backup/" + DestinationStore.ProbePrefix + "bbbb2222.tmp", "CUSTOS", DateTime.UtcNow.AddHours(-1));

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Success, result.Status);
        Assert.False(Directory.Exists(old));
        Assert.False(Directory.Exists(partial));
        Assert.False(Directory.Exists(removing));
        Assert.True(Directory.Exists(young));
        Assert.True(Directory.Exists(other));
        Assert.True(Directory.Exists(unmarked));
        Assert.False(File.Exists(oldTrial));
        Assert.True(File.Exists(youngTrial));
        Assert.Contains(_log.Lines, l => l.Message.StartsWith(
            Messages.Format(Messages.UnfinishedSetRemoved, "Docs 2026-10-21 10-00 (Full)", _destination, "\u0001").Split('\u0001')[0], StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Message == Messages.Format(Messages.LeftoverSetRemoved, "Docs 2026-10-23 10-00 (Full)", _destination));
    }

    [Fact]
    public void On_worm_storage_an_unfinished_set_waits_for_its_protection_and_a_refusal_is_no_warning()
    {
        var task = NewTask(BackupTypes.Full, wormDays: 7);
        var now = DateTimeOffset.UtcNow;
        string recent = Unfinished("Docs 2026-10-20 10-00 (Full)", task.Id, UnfinishedMarker.Writing, now.AddDays(-3));
        string removing = Unfinished("Docs 2026-10-23 10-00 (Full)", task.Id, UnfinishedMarker.Removing, now.AddDays(-1));
        _platform.WrapStore = store => new GuardedStore(store)
        {
            Refuse = path => path.StartsWith(removing + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !path.EndsWith(UnfinishedMarker.FileName, StringComparison.Ordinal),
        };

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Success, result.Status);
        Assert.True(Directory.Exists(recent));
        Assert.True(File.Exists(Path.Combine(removing, UnfinishedMarker.FileName)));
        Assert.Contains(_log.Lines, l => l.Level == RunLogLevel.Info
            && l.Message == Messages.Format(Messages.UnfinishedSetProtected, "Docs 2026-10-23 10-00 (Full)", _destination));
    }

    /// <summary>A set folder with a file and a marker, its folder time set to <paramref name="since"/>.</summary>
    private string Unfinished(string name, string taskId, string state, DateTimeOffset since)
    {
        string folder = _temp.Combine("Backup", name);
        _temp.WriteFile("Backup/" + name + "/Data/a.txt", "alpha", Modified);
        var store = new LocalStore(_destination);
        UnfinishedMarker.Write(store, folder, new UnfinishedMarker.Info(taskId, "old-run", state, since));
        Directory.SetLastWriteTimeUtc(folder, since.UtcDateTime);
        return folder;
    }

    private TaskDefinition NewTask(string type, int wormDays = 0) => new()
    {
        Name = "Docs",
        BackupType = type,
        Sources = [new TaskSource { Path = _source }],
        Destinations = [new TaskDestination { Kind = DestinationKinds.Local, Path = _destination, WormDays = wormDays, WormLockHours = 3 }],
        Vss = new VssOptions { Enabled = false },
        Copy = new CopyOptions { ReadRetries = 0 },
    };

    private BackupResult Run(TaskDefinition task, DateTimeOffset startedAt, string? runId = null)
    {
        var engine = new BackupEngine(_platform, _store, _log);
        using var gate = new PauseGate();
        return engine.Run(new BackupRequest
        {
            Task = task,
            RunId = runId ?? Guid.NewGuid().ToString(),
            Trigger = RunTriggers.Manual,
            StartedAt = startedAt,
            TimeZone = TimeZoneInfo.Utc,
        }, gate, TestContext.Current.CancellationToken);
    }
}
