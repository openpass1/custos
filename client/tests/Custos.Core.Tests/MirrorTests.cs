using Custos.Core.Archives;
using Custos.Core.Backup;
using Custos.Core.Model;

namespace Custos.Core.Tests;

/// <summary>Mirror (section 6.1): the destination is kept the same as the sources, exact or safe.</summary>
public sealed class MirrorTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Modified = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly TempFolder _temp = new();
    private readonly TestPlatform _platform = new();
    private readonly MemoryFileStateStore _store = new();
    private readonly TestLog _log = new();
    private readonly string _source;
    private readonly string _mirror;

    public MirrorTests()
    {
        _source = _temp.Combine("Data");
        _mirror = _temp.Combine("Mirror");
        _temp.WriteFile("Data/a.txt", "alpha", Modified);
        _temp.WriteFile("Data/sub/b.txt", "beta", Modified);
        Directory.CreateDirectory(_temp.Combine("Data", "empty"));
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void An_exact_mirror_copies_what_changed_and_deletes_what_is_gone()
    {
        var task = NewTask(MirrorModes.Exact, _mirror);

        var first = Run(task, Start);
        Assert.Equal(RunStatuses.Success, first.Status);
        Assert.Equal(BackupTypes.Mirror, first.BackupType);
        Assert.Equal(2, first.FilesProcessed);
        Assert.Equal("alpha", File.ReadAllText(Mirrored("a.txt")));
        Assert.True(Directory.Exists(Mirrored("empty")));
        var (header, files) = ReadManifest();
        Assert.Equal(BackupTypes.Mirror, header.BackupType);
        Assert.Null(header.ParentFullRunId);
        Assert.Equal(2, header.SetFileCount);
        Assert.All(files, f => Assert.True(f.InSet && f.Sha256 is not null));

        // Nothing changed: nothing is copied.
        var second = Run(task, Start.AddHours(1));
        Assert.Equal(RunStatuses.Success, second.Status);
        Assert.Equal(0, second.FilesProcessed);
        Assert.All(ReadManifest().Files, f => Assert.NotNull(f.Sha256));

        _temp.WriteFile("Data/a.txt", "alpha 2", Modified.AddHours(1));
        Directory.Delete(_temp.Combine("Data", "sub"), recursive: true);
        _temp.WriteFile("Data/c.txt", "gamma", Modified);
        var third = Run(task, Start.AddHours(2));

        Assert.Equal(RunStatuses.Success, third.Status);
        Assert.Equal(2, third.FilesProcessed);
        Assert.Equal("alpha 2", File.ReadAllText(Mirrored("a.txt")));
        Assert.Equal("gamma", File.ReadAllText(Mirrored("c.txt")));
        Assert.False(File.Exists(Mirrored("sub", "b.txt")));
        Assert.False(Directory.Exists(Mirrored("sub")));
        Assert.False(Directory.Exists(Path.Combine(_mirror, BackupEngine.MirrorBinName)));
        Assert.Equal(["Data/a.txt", "Data/c.txt"], ReadManifest().Files.Select(f => f.Path.Replace('\\', '/')).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Files_the_mirror_did_not_put_there_stay()
    {
        _temp.WriteFile("Mirror/Data/foreign.txt", "not ours", Modified);
        _temp.WriteFile("Mirror/notes.txt", "not ours either", Modified);
        var task = NewTask(MirrorModes.Exact, _mirror);

        Run(task, Start);
        File.Delete(_temp.Combine("Data", "a.txt"));
        var second = Run(task, Start.AddHours(1));

        Assert.Equal(RunStatuses.Success, second.Status);
        Assert.False(File.Exists(Mirrored("a.txt")));
        Assert.True(File.Exists(Mirrored("foreign.txt")));
        Assert.True(File.Exists(Path.Combine(_mirror, "notes.txt")));
    }

    [Fact]
    public void A_file_already_in_the_mirror_is_not_copied_again()
    {
        _temp.WriteFile("Mirror/Data/a.txt", "alpha", Modified.AddSeconds(1));
        var task = NewTask(MirrorModes.Exact, _mirror);

        var result = Run(task, Start);

        // Within the 2 seconds FAT allows: the same version.
        Assert.Equal(1, result.FilesProcessed);
        var a = ReadManifest().Files.Single(f => f.Path.EndsWith("a.txt", StringComparison.Ordinal));
        Assert.True(a.InSet);
        Assert.Null(a.Sha256);
    }

    [Fact]
    public void Each_destination_gets_only_what_it_lacks()
    {
        string second = _temp.Combine("Mirror2");
        _temp.WriteFile("Mirror2/Data/a.txt", "alpha", Modified);
        _temp.WriteFile("Mirror2/Data/sub/b.txt", "beta", Modified);
        var task = NewTask(MirrorModes.Exact, _mirror, second);

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Success, result.Status);
        Assert.Equal(9, result.Destinations[0].Bytes);
        Assert.Equal(0, result.Destinations[1].Bytes);
        Assert.Equal("beta", File.ReadAllText(Mirrored("sub", "b.txt")));
    }

    [Fact]
    public void A_safe_mirror_keeps_replaced_and_deleted_files()
    {
        var task = NewTask(MirrorModes.Safe, _mirror);
        task.Retention.SafeMirrorDays = 30;
        Run(task, Start);
        string oldBin = Path.Combine(_mirror, BackupEngine.MirrorBinName, "2026-09-01 10-00");
        string youngBin = Path.Combine(_mirror, BackupEngine.MirrorBinName, "2026-10-20 10-00");
        Directory.CreateDirectory(oldBin);
        Directory.CreateDirectory(youngBin);

        _temp.WriteFile("Data/a.txt", "alpha 2", Modified.AddHours(1));
        File.Delete(_temp.Combine("Data", "sub", "b.txt"));
        var second = Run(task, Start.AddHours(2));

        Assert.Equal(RunStatuses.Success, second.Status);
        string bin = Path.Combine(_mirror, BackupEngine.MirrorBinName, "2026-11-01 12-00");
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(bin, "Data", "a.txt")));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(bin, "Data", "sub", "b.txt")));
        Assert.Equal("alpha 2", File.ReadAllText(Mirrored("a.txt")));
        Assert.False(File.Exists(Mirrored("sub", "b.txt")));
        // The folder is still in the source (empty now), so it stays in the mirror.
        Assert.True(Directory.Exists(Mirrored("sub")));
        Assert.False(Directory.Exists(oldBin));
        Assert.True(Directory.Exists(youngBin));

        // The bin is not part of the mirror: it is neither listed nor deleted.
        Assert.DoesNotContain(ReadManifest().Files, f => f.Path.Contains(BackupEngine.MirrorBinName, StringComparison.Ordinal));
        Run(task, Start.AddHours(3));
        Assert.True(Directory.Exists(bin));
    }

    [Fact]
    public void A_mirror_cannot_be_an_archive_or_lie_inside_a_source()
    {
        var archive = NewTask(MirrorModes.Exact, _mirror);
        archive.Archive.Format = ArchiveFormats.Zip;
        Assert.Equal(RunStatuses.Error, Run(archive, Start).Status);

        var inside = NewTask(MirrorModes.Exact, _temp.Combine("Data", "Mirror"));
        var result = Run(inside, Start);
        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.False(Directory.Exists(_temp.Combine("Data", "Mirror")));
    }

    [Fact]
    public void Retention_never_touches_a_mirror_and_the_mirror_can_be_restored()
    {
        var task = NewTask(MirrorModes.Exact, _mirror);
        task.Layout.SeparateFolders = true;
        task.Retention = new RetentionOptions { Mode = RetentionModes.Count, KeepFull = 1 };
        Run(task, Start);
        Run(task, Start.AddDays(1));

        var sets = SetCatalog.Scan(_mirror, task.Id, mirror: true);
        var set = Assert.Single(sets);
        Assert.Equal(_mirror, set.Location);
        Assert.Empty(SetCatalog.Scan(_mirror, task.Id));

        string target = _temp.Combine("Restored");
        var restored = new RestoreEngine(_log).Run(
            new RestoreRequest { Sets = sets, Set = set, TargetFolder = target, Access = new ArchiveAccess(_platform.Archives, null) },
            TestContext.Current.CancellationToken);

        Assert.Equal(RunStatuses.Success, restored.Status);
        Assert.Equal(2, restored.FilesRestored);
        Assert.Equal("beta", File.ReadAllText(Path.Combine(target, "Data", "sub", "b.txt")));
    }

    private string Mirrored(params string[] parts) => Path.Combine([_mirror, "Data", .. parts]);

    private (SetManifestHeader Header, List<ManifestFile> Files) ReadManifest()
    {
        string path = Path.Combine(_mirror, SetManifest.FileName);
        using var headerStream = File.OpenRead(path);
        var header = SetManifest.ReadHeader(headerStream);
        using var filesStream = File.OpenRead(path);
        return (header, SetManifest.ReadFiles(filesStream));
    }

    private TaskDefinition NewTask(string mode, params string[] destinations) => new()
    {
        Name = "Docs",
        BackupType = BackupTypes.Mirror,
        MirrorMode = mode,
        Sources = [new TaskSource { Path = _source }],
        Destinations = [.. destinations.Select(d => new TaskDestination { Kind = DestinationKinds.Local, Path = d })],
        Vss = new VssOptions { Enabled = false },
        Copy = new CopyOptions { ReadRetries = 0 },
    };

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
            LogEveryFile = true,
        }, gate, TestContext.Current.CancellationToken);
    }
}
