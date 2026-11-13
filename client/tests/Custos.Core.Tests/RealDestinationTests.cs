using System.Text;
using Amazon.S3.Model;
using Custos.Core.Backup;
using Custos.Core.Destinations;
using Custos.Core.Model;

namespace Custos.Core.Tests;

/// <summary>
/// Whole runs into real destinations (stage 4, section 6.3): a folder (always) and the servers of
/// client/tools/ci-destinations.sh (the CI job "destinations"). The same copies, checks, restores and retention must
/// come out everywhere.
/// </summary>
public sealed class RealDestinationTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Modified = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly TempFolder _temp = new();
    private readonly TestPlatform _platform = new();
    private readonly MemoryFileStateStore _state = new();
    private readonly TestLog _log = new();
    private readonly string _source;

    public RealDestinationTests()
    {
        _source = _temp.Combine("Data");
        _temp.WriteFile("Data/a.txt", "alpha", Modified);
        _temp.WriteFile("Data/sub/b.txt", "beta", Modified);
        _temp.WriteFile("Data/Отчёты 2026/Сводка (100%) & итог.txt", "сводка", Modified);
        _temp.WriteFile("Data/empty.bin", string.Empty, Modified);
        File.WriteAllBytes(_temp.Combine("Data", "big.bin"), Pattern(3 * 1024 * 1024 + 17));
        File.SetLastWriteTimeUtc(_temp.Combine("Data", "big.bin"), Modified);
    }

    public static TheoryData<string, string> KindsAndFormats
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (string kind in TestDestinations.All)
            {
                data.Add(kind, ArchiveFormats.None);
                data.Add(kind, ArchiveFormats.Zip);
            }

            return data;
        }
    }

    public static TheoryData<string, bool> KindsAndMirrors
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (string kind in TestDestinations.All)
            {
                data.Add(kind, false);
                data.Add(kind, true);
            }

            return data;
        }
    }

    public void Dispose() => _temp.Dispose();

    [Theory]
    [MemberData(nameof(KindsAndFormats))]
    public void A_chain_is_copied_checked_restored_and_thinned_out(string kind, string format)
    {
        var destination = TestDestinations.Create(kind, _platform, _temp);
        var task = NewTask(BackupTypes.Incremental, destination);
        task.Archive.Format = format;
        task.Verify.EveryRuns = 1;
        task.Retention.KeepFull = 1;
        task.ForceFull.EveryRuns = 2;

        var first = Run(task, Start);
        Assert.Equal(RunStatuses.Success, first.Status);
        Assert.Equal(BackupTypes.Full, first.BackupType);
        Assert.Equal(5, first.FilesProcessed);
        Assert.True(first.Verify.Performed, "The new copy is read back from the destination.");

        _temp.WriteFile("Data/a.txt", "alpha 2", Modified.AddHours(1));
        _temp.WriteFile("Data/c.txt", "gamma", Modified.AddHours(2));
        File.Delete(_temp.Combine("Data", "sub", "b.txt"));
        var second = Run(task, Start.AddDays(1));
        Assert.Equal(RunStatuses.Success, second.Status);
        Assert.Equal(BackupTypes.Incremental, second.BackupType);
        Assert.Equal(2, second.FilesProcessed);

        using (var store = _platform.OpenStore(destination))
        {
            var access = new ArchiveAccess(_platform.Archives, null, store);
            var sets = SetCatalog.Scan(store, store.Root, task.Id, access);
            Assert.Equal(2, sets.Count);
            Assert.Equal([first.SetName, second.SetName], sets.Select(s => s.Header.SetName));
            Assert.Equal(format == ArchiveFormats.None ? SetStorage.Folder : SetStorage.Archive, sets[0].Storage);

            // The date of the second run: a changed, c added, b deleted.
            string latest = _temp.Combine("Latest");
            var restored = Restore(sets, sets[1], latest, access);
            Assert.Equal(RunStatuses.Success, restored.Status);
            Assert.Equal(5, restored.FilesRestored);
            Assert.Equal("alpha 2", File.ReadAllText(Path.Combine(latest, "Data", "a.txt")));
            Assert.Equal("gamma", File.ReadAllText(Path.Combine(latest, "Data", "c.txt")));
            Assert.Equal("сводка", File.ReadAllText(Path.Combine(latest, "Data", "Отчёты 2026", "Сводка (100%) & итог.txt")));
            Assert.Equal(Pattern(3 * 1024 * 1024 + 17), File.ReadAllBytes(Path.Combine(latest, "Data", "big.bin")));
            Assert.Empty(File.ReadAllBytes(Path.Combine(latest, "Data", "empty.bin")));
            Assert.False(File.Exists(Path.Combine(latest, "Data", "sub", "b.txt")));
            Assert.Equal(Modified.AddHours(1), File.GetLastWriteTimeUtc(Path.Combine(latest, "Data", "a.txt")));

            // The date of the first run.
            string earliest = _temp.Combine("Earliest");
            Assert.Equal(RunStatuses.Success, Restore(sets, sets[0], earliest, access).Status);
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(earliest, "Data", "a.txt")));
            Assert.Equal("beta", File.ReadAllText(Path.Combine(earliest, "Data", "sub", "b.txt")));
            Assert.False(File.Exists(Path.Combine(earliest, "Data", "c.txt")));
        }

        // The third run is full again, checks what is stored, and retention keeps one chain: the first two sets go.
        task.Verify.StoredEveryDays = 1;
        var third = Run(task, Start.AddDays(2));
        Assert.Equal(RunStatuses.Success, third.Status);
        Assert.Equal(BackupTypes.Full, third.BackupType);
        Assert.Equal(2, third.DeletedSets);
        using (var store = _platform.OpenStore(destination))
        {
            var left = store.List(store.Root);
            Assert.Equal([third.SetName], left.Select(e => Path.GetFileNameWithoutExtension(e.Name) == third.SetName ? third.SetName : e.Name));
            var sets = SetCatalog.Scan(store, store.Root, task.Id, new ArchiveAccess(_platform.Archives, null, store));
            Assert.Single(sets);
        }

        Assert.DoesNotContain(_log.Lines, l => l.Level is RunLogLevel.Warning or RunLogLevel.Error);
    }

    [Theory]
    [MemberData(nameof(KindsAndMirrors))]
    public void A_mirror_follows_the_sources(string kind, bool safe)
    {
        var destination = TestDestinations.Create(kind, _platform, _temp);
        var task = NewTask(BackupTypes.Mirror, destination);
        task.MirrorMode = safe ? MirrorModes.Safe : MirrorModes.Exact;
        task.Verify.EveryRuns = 1;

        var first = Run(task, Start);
        Assert.Equal(RunStatuses.Success, first.Status);
        Assert.Equal(5, first.FilesProcessed);

        // Nothing changed: nothing is copied, though a server shows the time of the upload, not that of the file.
        var idle = Run(task, Start.AddHours(1));
        Assert.Equal(RunStatuses.Success, idle.Status);
        Assert.Equal(0, idle.FilesProcessed);

        // The same size, another content and time; one file gone.
        _temp.WriteFile("Data/a.txt", "omega", Modified.AddHours(1));
        File.Delete(_temp.Combine("Data", "sub", "b.txt"));
        Directory.Delete(_temp.Combine("Data", "sub"));
        var second = Run(task, Start.AddDays(1));
        Assert.Equal(RunStatuses.Success, second.Status);
        Assert.Equal(1, second.FilesProcessed);

        using var store = _platform.OpenStore(destination);
        string root = store.Root;
        Assert.Equal("omega", ReadText(store, store.Combine(root, "Data/a.txt")));
        Assert.False(store.FileExists(store.Combine(root, "Data/sub/b.txt")));
        Assert.Equal("сводка", ReadText(store, store.Combine(root, "Data/Отчёты 2026/Сводка (100%) & итог.txt")));
        if (store.HasFolders)
        {
            Assert.False(store.FolderExists(store.Combine(root, "Data/sub")), "A folder the sources no longer have is removed.");
        }

        string bin = store.Combine(root, BackupEngine.MirrorBinName, "2026-11-02 10-00");
        if (safe)
        {
            Assert.Equal("alpha", ReadText(store, store.Combine(bin, "Data/a.txt")));
            Assert.Equal("beta", ReadText(store, store.Combine(bin, "Data/sub/b.txt")));
        }
        else
        {
            Assert.Empty(store.List(store.Combine(root, BackupEngine.MirrorBinName)));
        }

        // The mirror is found as the one set of the task and gives back what the sources have now.
        var access = new ArchiveAccess(_platform.Archives, null, store);
        var sets = SetCatalog.Scan(store, root, task.Id, access, mirror: true);
        var mirror = Assert.Single(sets);
        string target = _temp.Combine("Restored");
        var restored = Restore(sets, mirror, target, access);
        Assert.Equal(RunStatuses.Success, restored.Status);
        Assert.Equal(4, restored.FilesRestored);
        Assert.Equal("omega", File.ReadAllText(Path.Combine(target, "Data", "a.txt")));

        if (safe)
        {
            // The bin is emptied when its days have passed.
            task.Retention.SafeMirrorDays = 3;
            var later = Run(task, Start.AddDays(10));
            Assert.Equal(RunStatuses.Success, later.Status);
            Assert.False(store.FileExists(store.Combine(bin, "Data/a.txt")));
        }

        Assert.DoesNotContain(_log.Lines, l => l.Level is RunLogLevel.Warning or RunLogLevel.Error);
    }

    [Theory]
    [MemberData(nameof(TestDestinations.All), MemberType = typeof(TestDestinations))]
    public void A_shared_folder_holds_the_last_version_of_every_file(string kind)
    {
        var destination = TestDestinations.Create(kind, _platform, _temp);
        var task = NewTask(BackupTypes.Incremental, destination);
        task.Layout.SeparateFolders = false;

        Assert.Equal(RunStatuses.Success, Run(task, Start).Status);
        _temp.WriteFile("Data/a.txt", "alpha 2", Modified.AddHours(1));
        var second = Run(task, Start.AddDays(1));
        Assert.Equal(RunStatuses.Success, second.Status);
        Assert.Equal(1, second.FilesProcessed);

        using var store = _platform.OpenStore(destination);
        Assert.Equal("alpha 2", ReadText(store, store.Combine(store.Root, "Data/a.txt")));
        Assert.Equal("beta", ReadText(store, store.Combine(store.Root, "Data/sub/b.txt")));
        Assert.True(store.FileExists(store.Combine(store.Root, SetManifest.FileName)));
        Assert.DoesNotContain(store.ListFiles(store.Root), e => e.Name.EndsWith(BackupEngine.TempSuffix, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(TestDestinations.All), MemberType = typeof(TestDestinations))]
    public void A_stopped_run_leaves_no_set_behind(string kind)
    {
        var destination = TestDestinations.Create(kind, _platform, _temp);
        var task = NewTask(BackupTypes.Full, destination);
        using var cancellation = new CancellationTokenSource();
        int reports = 0;
        var engine = new BackupEngine(_platform, _state, _log, progress =>
        {
            // In the middle of the copy: some files are in the destination already.
            if (progress.Phase == BackupPhases.Copy && progress.FilesDone >= 2 && Interlocked.Increment(ref reports) == 1)
            {
                cancellation.Cancel();
            }
        });
        using var gate = new PauseGate();

        var result = engine.Run(Request(task, Start), gate, cancellation.Token);

        Assert.Equal(RunStatuses.Aborted, result.Status);
        using var store = _platform.OpenStore(destination);
        Assert.Empty(store.List(store.Root));
        Assert.Null(_state.GetBackupState(task.Id).FullRunId);
    }

    [Theory]
    [InlineData(DestinationKinds.Ftp)]
    [InlineData(DestinationKinds.Ftps)]
    [InlineData(DestinationKinds.WebDav)]
    [InlineData(DestinationKinds.S3)]
    public void Seven_zip_is_refused_where_a_file_cannot_be_changed_after_it_is_sent(string kind)
    {
        var task = NewTask(BackupTypes.Full, new TaskDestination { Kind = kind, Host = "backup.example.org", Bucket = "copies", Path = "pc" });
        task.Archive.Format = ArchiveFormats.SevenZip;

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.Contains("7z", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains(DestinationStores.Label(task.Destinations[0]), result.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BackupTypes.Mirror, true)]
    [InlineData(BackupTypes.Incremental, false)]
    public void Object_lock_is_refused_where_files_are_replaced(string type, bool separate)
    {
        var task = NewTask(type, new TaskDestination { Kind = DestinationKinds.S3, Bucket = "copies", Path = "pc", ObjectLockDays = 30, CredentialId = "none" });
        task.Layout.SeparateFolders = separate;

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.Contains("Object Lock", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Object_lock_keeps_a_set_until_its_time_and_retention_takes_it_in_the_first_run_after()
    {
        TestDestinations.Settings(TestDestinations.S3);
        Assert.SkipWhen(TestDestinations.IsMoto, "moto knows no Object Lock to speak of; MinIO does (the CI job).");
        var destination = TestDestinations.Create(TestDestinations.S3Lock, _platform, _temp);
        var task = NewTask(BackupTypes.Full, destination);
        task.Retention.KeepFull = 1;

        var first = Run(task, Start);
        Assert.Equal(RunStatuses.Success, first.Status);

        // The second run wants the first set gone, but the bucket holds it: that is not an error, it is said and left.
        var second = Run(task, Start.AddDays(1));
        Assert.Equal(RunStatuses.Success, second.Status);
        Assert.Equal(0, second.DeletedSets);
        Assert.Contains(_log.Lines, l => l.Level == RunLogLevel.Info && l.Message.Contains(first.SetName!, StringComparison.Ordinal)
            && l.Message.Contains("Object Lock", StringComparison.Ordinal));

        using var store = _platform.OpenStore(destination);
        var access = new ArchiveAccess(_platform.Archives, null, store);
        var sets = SetCatalog.Scan(store, store.Root, task.Id, access);
        Assert.Equal(2, sets.Count);

        // Deleting it by hand is refused by the server itself.
        var refused = Assert.ThrowsAny<Exception>(() => SetCatalog.DeleteSet(sets[0], store));
        Assert.True(refused is IOException or UnauthorizedAccessException, refused.ToString());
        Assert.Equal("alpha", ReadText(store, store.Combine(sets[0].Location, "Data/a.txt")));

        // The time of the first set passes (the test shortens it, as only the owner of the bucket can).
        using var client = TestDestinations.S3Client();
        var until = DateTime.UtcNow.AddSeconds(3);
        string prefix = sets[0].Location.TrimEnd('/') + "/";
        var versions = await client.ListVersionsAsync(new ListVersionsRequest { BucketName = destination.Bucket, Prefix = prefix }, TestContext.Current.CancellationToken);
        Assert.NotEmpty(versions.Versions);
        foreach (var version in versions.Versions)
        {
            await client.PutObjectRetentionAsync(new PutObjectRetentionRequest
            {
                BucketName = destination.Bucket,
                Key = version.Key,
                VersionId = version.VersionId,
                BypassGovernanceRetention = true,
                Retention = new ObjectLockRetention { Mode = Amazon.S3.ObjectLockRetentionMode.Governance, RetainUntilDate = until },
            }, TestContext.Current.CancellationToken);
        }

        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The third run deletes the first set, with every version of its files; the second is still held.
        var third = Run(task, Start.AddDays(2));
        Assert.Equal(RunStatuses.Success, third.Status);
        Assert.Equal(1, third.DeletedSets);
        var left = await client.ListVersionsAsync(new ListVersionsRequest { BucketName = destination.Bucket, Prefix = prefix }, TestContext.Current.CancellationToken);
        Assert.Empty(left.Versions ?? []);
        Assert.Equal([second.SetName, third.SetName], SetCatalog.Scan(store, store.Root, task.Id, access).Select(s => s.Header.SetName));
    }

    private static byte[] Pattern(int length)
    {
        var data = new byte[length];
        new Random(42).NextBytes(data);
        return data;
    }

    private static string ReadText(DestinationStore store, string path)
    {
        using var stream = store.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private TaskDefinition NewTask(string type, TaskDestination destination) => new()
    {
        Name = "Docs",
        BackupType = type,
        Sources = [new TaskSource { Path = _source }],
        Destinations = [destination],
        Vss = new VssOptions { Enabled = false },
        Copy = new CopyOptions { ReadRetries = 0 },
    };

    private BackupResult Run(TaskDefinition task, DateTimeOffset startedAt)
    {
        var engine = new BackupEngine(_platform, _state, _log);
        using var gate = new PauseGate();
        return engine.Run(Request(task, startedAt), gate, TestContext.Current.CancellationToken);
    }

    private static BackupRequest Request(TaskDefinition task, DateTimeOffset startedAt) => new()
    {
        Task = task,
        RunId = Guid.NewGuid().ToString(),
        Trigger = RunTriggers.Manual,
        StartedAt = startedAt,
        TimeZone = TimeZoneInfo.Utc,
        LogEveryFile = true,
    };

    private RestoreResult Restore(IReadOnlyList<StoredSet> sets, StoredSet set, string target, ArchiveAccess access) =>
        new RestoreEngine(_log).Run(
            new Backup.RestoreRequest { Sets = sets, Set = set, TargetFolder = target, Existing = RestoreExisting.Overwrite, Access = access },
            TestContext.Current.CancellationToken);
}
