using Custos.Core.Archives;
using Custos.Core.Backup;
using Custos.Core.Model;

namespace Custos.Core.Tests;

/// <summary>Restore to a date (section 6.11), from plain sets and from archives.</summary>
public sealed class RestoreEngineTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Modified = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly TempFolder _temp = new();
    private readonly TestPlatform _platform = new();
    private readonly MemoryFileStateStore _store = new();
    private readonly TestLog _log = new();
    private readonly string _source;
    private readonly string _backup;

    public RestoreEngineTests()
    {
        _source = _temp.Combine("Data");
        _backup = _temp.Combine("Backup");
        _temp.WriteFile("Data/a.txt", "alpha", Modified);
        _temp.WriteFile("Data/sub/b.txt", "beta", Modified);
    }

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(ArchiveFormats.None)]
    [InlineData(ArchiveFormats.Zip)]
    [InlineData(ArchiveFormats.SevenZip)]
    public void Each_date_comes_back_as_it_was(string format)
    {
        SevenZipCheck.SkipUnlessAvailable(format);
        var task = MakeChain(format);
        var sets = Scan(task);
        Assert.Equal(3, sets.Count);

        // The second incremental: a changed, c added, b deleted.
        string latest = _temp.Combine("Latest");
        var result = Restore(sets, sets[2], latest);
        Assert.Equal(RunStatuses.Success, result.Status);
        Assert.Equal("alpha 2", Read(latest, "Data", "a.txt"));
        Assert.Equal("gamma", Read(latest, "Data", "c.txt"));
        Assert.False(File.Exists(Path.Combine(latest, "Data", "sub", "b.txt")));
        Assert.Equal(Modified.AddHours(1), File.GetLastWriteTimeUtc(Path.Combine(latest, "Data", "a.txt")));

        // The full copy: the first state.
        string first = _temp.Combine("First");
        Restore(sets, sets[0], first);
        Assert.Equal("alpha", Read(first, "Data", "a.txt"));
        Assert.Equal("beta", Read(first, "Data", "sub", "b.txt"));
        Assert.False(File.Exists(Path.Combine(first, "Data", "c.txt")));

        // The first incremental: a changed, b still there.
        string middle = _temp.Combine("Middle");
        var counts = Restore(sets, sets[1], middle);
        Assert.Equal("alpha 2", Read(middle, "Data", "a.txt"));
        Assert.Equal("beta", Read(middle, "Data", "sub", "b.txt"));
        Assert.Equal(2, counts.FilesRestored);
        Assert.Empty(Directory.GetFiles(middle, "*" + BackupEngine.TempSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public void Existing_files_are_skipped_replaced_or_replaced_when_older()
    {
        var task = MakeChain(ArchiveFormats.Zip);
        var sets = Scan(task);
        string target = _temp.Combine("Target");
        string a = _temp.WriteFile("Target/Data/a.txt", "newer local", Modified.AddDays(10));
        string c = _temp.WriteFile("Target/Data/c.txt", "older local", Modified.AddDays(-10));

        var skip = Restore(sets, sets[2], target, RestoreExisting.Skip);
        Assert.Equal(2, skip.FilesSkipped);
        Assert.Equal("newer local", File.ReadAllText(a));

        var older = Restore(sets, sets[2], target, RestoreExisting.OverwriteOlder);
        Assert.Equal("newer local", File.ReadAllText(a));
        Assert.Equal("gamma", File.ReadAllText(c));
        Assert.Equal(1, older.FilesSkipped);

        Restore(sets, sets[2], target, RestoreExisting.Overwrite);
        Assert.Equal("alpha 2", File.ReadAllText(a));
    }

    [Fact]
    public void Files_go_back_to_their_source_by_default()
    {
        var task = MakeChain(ArchiveFormats.None);
        var sets = Scan(task);
        Directory.Delete(_source, recursive: true);

        var result = Restore(sets, sets[2], null);

        Assert.Equal(RunStatuses.Success, result.Status);
        Assert.Equal("alpha 2", File.ReadAllText(Path.Combine(_source, "a.txt")));
        Assert.Equal("gamma", File.ReadAllText(Path.Combine(_source, "c.txt")));
    }

    [Fact]
    public void A_damaged_file_is_not_restored_and_says_so()
    {
        var task = MakeChain(ArchiveFormats.None);
        var sets = Scan(task);
        File.WriteAllText(Path.Combine(sets[2].Location, "Data", "c.txt"), "tampered");

        var result = Restore(sets, sets[2], _temp.Combine("Target"));

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.Equal(1, result.FilesFailed);
        Assert.False(File.Exists(_temp.Combine("Target", "Data", "c.txt")));
        Assert.True(File.Exists(_temp.Combine("Target", "Data", "a.txt")));
    }

    [Fact]
    public void A_restore_without_its_full_set_is_refused()
    {
        var task = MakeChain(ArchiveFormats.None);
        var sets = Scan(task);

        Assert.Throws<InvalidDataException>(() => Restore([sets[1], sets[2]], sets[2], _temp.Combine("Target")));
    }

    [Fact]
    public void Files_packed_after_the_manifest_come_back_without_a_checksum()
    {
        // As 7-Zip does: custos-set.json is packed before Data\…, so it cannot hold their SHA-256 yet.
        _platform.ArchivesOverride = new NameSortingArchives(_platform.Archives);
        var task = MakeChain(ArchiveFormats.Zip);
        var sets = Scan(task);
        var (_, files) = SetCatalog.ReadManifest(sets[0], new ArchiveAccess(_platform.Archives, null));
        Assert.All(files, f =>
        {
            Assert.True(f.InSet);
            Assert.Null(f.Sha256);
        });

        string latest = _temp.Combine("Latest");
        var result = Restore(sets, sets[2], latest);

        Assert.Equal(RunStatuses.Success, result.Status);
        Assert.Equal("alpha 2", Read(latest, "Data", "a.txt"));
        Assert.Equal("gamma", Read(latest, "Data", "c.txt"));
        Assert.False(File.Exists(Path.Combine(latest, "Data", "sub", "b.txt")));
    }

    [Fact]
    public void A_file_gone_after_the_manifest_was_packed_comes_from_the_older_set()
    {
        var archives = new NameSortingArchives(_platform.Archives);
        _platform.ArchivesOverride = archives;
        var task = NewTask(ArchiveFormats.Zip);
        Assert.Equal(RunStatuses.Success, Run(task, Start).Status);
        _temp.WriteFile("Data/a.txt", "alpha 2", Modified.AddHours(1));
        archives.BeforeOpen = item =>
        {
            if (item.Path.EndsWith("a.txt", StringComparison.Ordinal))
            {
                File.Delete(_temp.Combine("Data", "a.txt"));
            }
        };
        Assert.Equal(RunStatuses.Success, Run(task, Start.AddDays(1)).Status);
        archives.BeforeOpen = null;
        var sets = Scan(task);
        Assert.Equal(2, sets.Count);

        string target = _temp.Combine("Target");
        var result = Restore(sets, sets[1], target);

        Assert.Equal(RunStatuses.Success, result.Status);
        Assert.Equal("alpha", Read(target, "Data", "a.txt"));
        Assert.Equal("beta", Read(target, "Data", "sub", "b.txt"));
    }

    [Theory]
    [InlineData(@"Docs\a.txt", @"C:\Data\Docs\a.txt")]
    [InlineData(@"Docs\sub\b.txt", @"C:\Data\Docs\sub\b.txt")]
    [InlineData("report.xlsx", @"D:\Reports\report.xlsx")]
    [InlineData(@"..\evil.txt", null)]
    [InlineData(@"Other\x.txt", null)]
    public void Files_find_their_source_by_the_folder_they_lie_in(string stored, string? expected)
    {
        var header = new SetManifestHeader
        {
            Sources =
            [
                new ManifestSource { Path = @"C:\Data\Docs", Target = "Docs" },
                new ManifestSource { Path = @"D:\Reports\report.xlsx", Target = "report.xlsx" },
            ],
        };

        string? result = RestoreEngine.Destination(header, stored, null);

        Assert.Equal(expected?.Replace('\\', Path.DirectorySeparatorChar), result?.Replace('\\', Path.DirectorySeparatorChar));
    }

    /// <summary>Full, then incremental (a changed), then incremental (c added, b deleted).</summary>
    private TaskDefinition MakeChain(string format)
    {
        var task = NewTask(format);
        Assert.Equal(RunStatuses.Success, Run(task, Start).Status);
        _temp.WriteFile("Data/a.txt", "alpha 2", Modified.AddHours(1));
        Assert.Equal(RunStatuses.Success, Run(task, Start.AddDays(1)).Status);
        _temp.WriteFile("Data/c.txt", "gamma", Modified.AddHours(2));
        File.Delete(_temp.Combine("Data", "sub", "b.txt"));
        Assert.Equal(RunStatuses.Success, Run(task, Start.AddDays(2)).Status);
        return task;
    }

    private TaskDefinition NewTask(string format) => new()
    {
        Name = "Docs",
        BackupType = BackupTypes.Incremental,
        Sources = [new TaskSource { Path = _source }],
        Destinations = [new TaskDestination { Kind = DestinationKinds.Local, Path = _backup }],
        Vss = new VssOptions { Enabled = false },
        Copy = new CopyOptions { ReadRetries = 0 },
        Archive = new ArchiveOptions { Format = format },
    };

    private List<StoredSet> Scan(TaskDefinition task) => SetCatalog.Scan(_backup, task.Id, new ArchiveAccess(_platform.Archives, null));

    private RestoreResult Restore(IReadOnlyList<StoredSet> sets, StoredSet set, string? target, RestoreExisting existing = RestoreExisting.Overwrite) =>
        new RestoreEngine(_log).Run(
            new RestoreRequest { Sets = sets, Set = set, TargetFolder = target, Existing = existing, Access = new ArchiveAccess(_platform.Archives, null) },
            TestContext.Current.CancellationToken);

    private BackupResult Run(TaskDefinition task, DateTimeOffset startedAt)
    {
        var engine = new BackupEngine(_platform, _store, _log);
        using var gate = new PauseGate();
        return engine.Run(new BackupRequest
        {
            Task = task,
            RunId = Guid.NewGuid().ToString(),
            Trigger = RunTriggers.Manual,
            StartedAt = startedAt,
            TimeZone = TimeZoneInfo.Utc,
        }, gate, TestContext.Current.CancellationToken);
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(parts));
}
