using System.Security.Cryptography;
using System.Text;
using Custos.Core.Backup;
using Custos.Core.Model;

namespace Custos.Core.Tests;

public sealed class BackupEngineTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Modified = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly TempFolder _temp = new();
    private readonly TestPlatform _platform = new();
    private readonly MemoryFileStateStore _store = new();
    private readonly TestLog _log = new();
    private readonly string _source;

    public BackupEngineTests()
    {
        _source = _temp.Combine("Data");
        _temp.WriteFile("Data/a.txt", "alpha", Modified);
        _temp.WriteFile("Data/sub/b.txt", "beta", Modified);
        _temp.WriteFile("Data/skip.tmp", "temporary", Modified);
        Directory.CreateDirectory(_temp.Combine("Data", "empty"));
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Full_copy_writes_every_file_and_a_manifest()
    {
        string destination = _temp.Combine("Backup");
        var task = NewTask(BackupTypes.Full, destination);

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Success, result.Status);
        Assert.Equal(BackupTypes.Full, result.BackupType);
        Assert.Equal("Docs 2026-11-01 10-00 (Full)", result.SetName);
        string set = Path.Combine(destination, result.SetName!);
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(set, "Data", "a.txt")));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(set, "Data", "sub", "b.txt")));
        Assert.False(File.Exists(Path.Combine(set, "Data", "skip.tmp")));
        Assert.True(Directory.Exists(Path.Combine(set, "Data", "empty")));
        Assert.Equal(Modified, File.GetLastWriteTimeUtc(Path.Combine(set, "Data", "a.txt")));
        Assert.Equal(2, result.FilesProcessed);
        Assert.Equal(9, result.BytesWritten);

        var (header, files) = ReadManifest(set);
        Assert.Equal(task.Id, header.TaskId);
        Assert.Equal(BackupTypes.Full, header.BackupType);
        Assert.Null(header.ParentFullRunId);
        Assert.Equal("TESTPC", header.Computer);
        Assert.Equal(RunStatuses.Success, header.Status);
        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.True(f.InSet));
        var a = files.Single(f => f.Path.EndsWith("a.txt", StringComparison.Ordinal));
        Assert.Equal(Sha256("alpha"), a.Sha256);
        Assert.Equal(Modified, a.ModifiedUtc);

        var state = _store.GetBackupState(task.Id);
        Assert.Equal(result.SetName, state.FullSetName);
        Assert.Single(Directory.GetDirectories(destination));
    }

    [Fact]
    public void Incremental_copies_only_what_changed_since_the_last_copy()
    {
        string destination = _temp.Combine("Backup");
        var task = NewTask(BackupTypes.Incremental, destination);

        var first = Run(task, Start, "run-1");
        Assert.Equal(BackupTypes.Full, first.BackupType);

        _temp.WriteFile("Data/a.txt", "alpha 2", Modified.AddHours(1));
        var second = Run(task, Start.AddDays(1), "run-2");
        Assert.Equal(RunStatuses.Success, second.Status);
        Assert.Equal(BackupTypes.Incremental, second.BackupType);
        Assert.Equal("Docs 2026-11-02 10-00 (Incremental)", second.SetName);
        string set2 = Path.Combine(destination, second.SetName!);
        Assert.Equal("alpha 2", File.ReadAllText(Path.Combine(set2, "Data", "a.txt")));
        Assert.False(File.Exists(Path.Combine(set2, "Data", "sub", "b.txt")));
        var (header2, files2) = ReadManifest(set2);
        Assert.Equal("run-1", header2.ParentFullRunId);
        Assert.Equal(first.SetName, header2.ParentFullSetName);
        Assert.Equal(2, files2.Count);
        Assert.False(files2.Single(f => f.Path.EndsWith("b.txt", StringComparison.Ordinal)).InSet);

        _temp.WriteFile("Data/sub/b.txt", "beta 2", Modified.AddHours(2));
        var third = Run(task, Start.AddDays(2), "run-3");
        string set3 = Path.Combine(destination, third.SetName!);
        Assert.Equal(BackupTypes.Incremental, third.BackupType);
        Assert.False(File.Exists(Path.Combine(set3, "Data", "a.txt")));
        Assert.Equal("beta 2", File.ReadAllText(Path.Combine(set3, "Data", "sub", "b.txt")));
    }

    [Fact]
    public void Incremental_with_nothing_changed_makes_no_set()
    {
        string destination = _temp.Combine("Backup");
        var task = NewTask(BackupTypes.Incremental, destination);
        Run(task, Start);

        var result = Run(task, Start.AddDays(1));

        Assert.Equal(RunStatuses.Success, result.Status);
        Assert.True(result.NothingToDo);
        Assert.Null(result.SetName);
        Assert.Single(Directory.GetDirectories(destination));
        Assert.All(result.Destinations, d => Assert.Equal(RunStatuses.Success, d.Status));
        Assert.Equal(0, _store.GetBackupState(task.Id).RunsSinceFull);
    }

    [Fact]
    public void Differential_copies_everything_changed_since_the_full_copy()
    {
        string destination = _temp.Combine("Backup");
        var task = NewTask(BackupTypes.Differential, destination);
        var first = Run(task, Start, "run-1");
        Assert.Equal(BackupTypes.Full, first.BackupType);

        _temp.WriteFile("Data/a.txt", "alpha 2", Modified.AddHours(1));
        var second = Run(task, Start.AddDays(1));
        Assert.Equal(BackupTypes.Differential, second.BackupType);
        Assert.Equal(1, second.FilesProcessed);

        _temp.WriteFile("Data/sub/b.txt", "beta 2", Modified.AddHours(2));
        var third = Run(task, Start.AddDays(2));
        string set3 = Path.Combine(destination, third.SetName!);
        Assert.Equal(2, third.FilesProcessed);
        Assert.Equal("alpha 2", File.ReadAllText(Path.Combine(set3, "Data", "a.txt")));
        Assert.Equal("beta 2", File.ReadAllText(Path.Combine(set3, "Data", "sub", "b.txt")));
        Assert.Equal("run-1", ReadManifest(set3).Header.ParentFullRunId);
    }

    [Fact]
    public void Every_nth_run_is_full_and_retention_removes_whole_old_chains()
    {
        string destination = _temp.Combine("Backup");
        var task = NewTask(BackupTypes.Incremental, destination);
        task.ForceFull.EveryRuns = 2;
        task.Retention.KeepFull = 1;

        var run1 = Run(task, Start, "run-1");
        _temp.WriteFile("Data/a.txt", "alpha 2", Modified.AddHours(1));
        var run2 = Run(task, Start.AddDays(1), "run-2");
        _temp.WriteFile("Data/a.txt", "alpha 3", Modified.AddHours(2));
        var run3 = Run(task, Start.AddDays(2), "run-3");

        Assert.Equal(BackupTypes.Full, run1.BackupType);
        Assert.Equal(BackupTypes.Incremental, run2.BackupType);
        Assert.Equal(BackupTypes.Full, run3.BackupType);
        Assert.Equal(2, run3.DeletedSets);
        var left = Directory.GetDirectories(destination).Select(Path.GetFileName).ToList();
        Assert.Equal([run3.SetName], left);
        Assert.Equal("alpha 3", File.ReadAllText(Path.Combine(destination, run3.SetName!, "Data", "a.txt")));
    }

    [Fact]
    public void Retention_by_age_deletes_sets_older_than_the_limit()
    {
        string destination = _temp.Combine("Backup");
        var task = NewTask(BackupTypes.Full, destination);
        task.Retention = new RetentionOptions { Mode = RetentionModes.Age, MaxAgeDays = 3 };

        Run(task, Start);
        Run(task, Start.AddDays(1));
        var kept1 = Run(task, Start.AddDays(2));
        var last = Run(task, Start.AddDays(5));

        Assert.Equal(2, last.DeletedSets);
        var left = Directory.GetDirectories(destination).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal([kept1.SetName, last.SetName], left);
    }

    [Fact]
    public void Gfs_retention_keeps_the_last_set_of_each_day()
    {
        string destination = _temp.Combine("Backup");
        var task = NewTask(BackupTypes.Full, destination);
        task.Retention = new RetentionOptions { Mode = RetentionModes.Gfs, Gfs = new GfsOptions { Daily = 2, Weekly = 0, Monthly = 0 } };

        Run(task, Start);
        Run(task, Start.AddDays(1));
        var dayTwo = Run(task, Start.AddDays(1).AddHours(2));
        var last = Run(task, Start.AddDays(2));

        var left = Directory.GetDirectories(destination).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal([dayTwo.SetName, last.SetName], left);
    }

    [Fact]
    public void Gfs_makes_the_first_backup_of_every_week_full()
    {
        // Start is Sunday 1 November 2026: the next day begins a new ISO week.
        string destination = _temp.Combine("Backup");
        var task = NewTask(BackupTypes.Incremental, destination);
        task.Retention = new RetentionOptions { Mode = RetentionModes.Gfs, Gfs = new GfsOptions { Daily = 7, Weekly = 4, Monthly = 12 } };

        Assert.Equal(BackupTypes.Full, Run(task, Start).BackupType);
        Assert.Equal(BackupTypes.Incremental, Run(task, Start.AddHours(5)).BackupType);
        _log.Lines.Clear();
        Assert.Equal(BackupTypes.Full, Run(task, Start.AddDays(1)).BackupType);
        Assert.Contains(_log.Lines, l => l.Message == Messages.FullBecauseGfsWeek);
        Assert.Equal(BackupTypes.Incremental, Run(task, Start.AddDays(2)).BackupType);
        Assert.Equal(BackupTypes.Incremental, Run(task, Start.AddDays(7)).BackupType);
        Assert.Equal(BackupTypes.Full, Run(task, Start.AddDays(8)).BackupType);

        // Other retention modes leave the type alone.
        string other = _temp.Combine("Other");
        var counted = NewTask(BackupTypes.Incremental, other);
        Run(counted, Start);
        Assert.Equal(BackupTypes.Incremental, Run(counted, Start.AddDays(1)).BackupType);
    }

    [Fact]
    public void A_missing_parent_set_makes_the_next_copy_full()
    {
        string destination = _temp.Combine("Backup");
        var task = NewTask(BackupTypes.Incremental, destination);
        var first = Run(task, Start);
        SetCatalog.DeleteSetFolder(Path.Combine(destination, first.SetName!));

        _temp.WriteFile("Data/a.txt", "alpha 2", Modified.AddHours(1));
        var second = Run(task, Start.AddDays(1));

        Assert.Equal(BackupTypes.Full, second.BackupType);
        Assert.Equal(2, second.FilesProcessed);
    }

    [Fact]
    public void A_failed_destination_does_not_stop_the_others_but_nothing_is_committed()
    {
        string blocked = _temp.WriteFile("blocked", "a file where a folder should be", Modified);
        string good = _temp.Combine("Backup");
        var task = NewTask(BackupTypes.Incremental, blocked, good);

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.NotNull(result.ErrorMessage);
        Assert.Equal(RunStatuses.Error, result.Destinations[0].Status);
        Assert.Equal(RunStatuses.Success, result.Destinations[1].Status);
        string set = Path.Combine(good, result.Destinations[1].SetName!);
        Assert.True(File.Exists(Path.Combine(set, SetManifest.FileName)));
        Assert.Null(_store.GetBackupState(task.Id).FullRunId);
        Assert.Contains(_log.Lines, l => l.Level == RunLogLevel.Error);
    }

    [Fact]
    public void A_missing_source_fails_the_run_and_leaves_no_partial_set()
    {
        string destination = _temp.Combine("Backup");
        var task = NewTask(BackupTypes.Full, destination);
        task.Sources.Add(new TaskSource { Path = _temp.Combine("Missing") });

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.Empty(Directory.GetDirectories(destination));
        Assert.Null(_store.GetBackupState(task.Id).FullRunId);
    }

    [Fact]
    public void Too_little_free_space_fails_the_destination()
    {
        string destination = _temp.Combine("Backup");
        var task = NewTask(BackupTypes.Full, destination);
        _platform.FreeSpace = 100;

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.True(result.OutOfSpace);
        Assert.Empty(Directory.GetDirectories(destination));
    }

    [Fact]
    public void A_usb_destination_is_found_by_its_volume_label()
    {
        string disk = _temp.Combine("UsbDisk");
        Directory.CreateDirectory(disk);
        _platform.Volumes["BACKUP-1"] = disk;
        var task = NewTask(BackupTypes.Full);
        task.Destinations.Add(new TaskDestination { Kind = DestinationKinds.Usb, UsbLabel = "BACKUP-1", Path = "Copies" });

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Success, result.Status);
        Assert.True(File.Exists(Path.Combine(disk, "Copies", result.SetName!, "Data", "a.txt")));
    }

    [Fact]
    public void A_drive_letter_in_the_folder_of_a_usb_destination_is_ignored()
    {
        string disk = _temp.Combine("UsbDisk");
        Directory.CreateDirectory(disk);
        _platform.Volumes["BACKUP-1"] = disk;
        var task = NewTask(BackupTypes.Full);
        task.Destinations.Add(new TaskDestination { Kind = DestinationKinds.Usb, UsbLabel = "BACKUP-1", Path = @"E:\Copies\" });

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Success, result.Status);
        Assert.True(File.Exists(Path.Combine(disk, "Copies", result.SetName!, "Data", "a.txt")));
    }

    [Theory]
    [InlineData(@"Copies\Office", @"Copies\Office")]
    [InlineData(@"\Copies\", "Copies")]
    [InlineData(@"E:\Copies", "Copies")]
    [InlineData("e:", "")]
    [InlineData(null, "")]
    public void The_folder_of_a_usb_destination_is_relative_to_the_disk(string? path, string expected) =>
        Assert.Equal(expected, DestinationKinds.UsbFolder(path));

    [Fact]
    public void A_usb_disk_that_is_not_connected_is_an_error()
    {
        var task = NewTask(BackupTypes.Full);
        task.Destinations.Add(new TaskDestination { Kind = DestinationKinds.Usb, UsbLabel = "ABSENT", Path = "Copies" });

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.Contains("ABSENT", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_source_is_stored_under_its_name()
    {
        string destination = _temp.Combine("Backup");
        var task = NewTask(BackupTypes.Full, destination);
        task.Sources = [new TaskSource { Kind = SourceKinds.File, Path = _temp.Combine("Data", "a.txt") }];

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Success, result.Status);
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(destination, result.SetName!, "a.txt")));
    }

    [Fact]
    public void Without_the_top_folder_the_files_go_to_the_set_folder_itself()
    {
        string destination = _temp.Combine("Backup");
        var task = NewTask(BackupTypes.Full, destination);
        task.Layout.KeepTopDir = false;

        var result = Run(task, Start);

        Assert.True(File.Exists(Path.Combine(destination, result.SetName!, "a.txt")));
        Assert.True(File.Exists(Path.Combine(destination, result.SetName!, "sub", "b.txt")));
    }

    [Fact]
    public void Without_separate_folders_the_copy_is_updated_in_place()
    {
        string destination = _temp.Combine("Backup");
        var task = NewTask(BackupTypes.Incremental, destination);
        task.Layout.SeparateFolders = false;

        var first = Run(task, Start);
        _temp.WriteFile("Data/a.txt", "alpha 2", Modified.AddHours(1));
        var second = Run(task, Start.AddDays(1));

        Assert.Equal(BackupTypes.Full, first.BackupType);
        Assert.Equal(BackupTypes.Incremental, second.BackupType);
        Assert.Equal(RunStatuses.Success, second.Status);
        Assert.Equal("alpha 2", File.ReadAllText(Path.Combine(destination, "Data", "a.txt")));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(destination, "Data", "sub", "b.txt")));
        Assert.Equal(BackupTypes.Incremental, ReadManifest(destination).Header.BackupType);
    }

    [Fact]
    public void Filters_leave_out_matching_files_and_folders()
    {
        _temp.WriteFile("Data/logs/today.log", "log", Modified);
        _temp.WriteFile("Data/report.docx", "report", Modified);
        string destination = _temp.Combine("Backup");
        var task = NewTask(BackupTypes.Full, destination);
        task.Filters.ExcludeDirs = ["logs"];
        task.Filters.Exclude.Add("*.docx");

        var result = Run(task, Start);

        string set = Path.Combine(destination, result.SetName!, "Data");
        Assert.False(Directory.Exists(Path.Combine(set, "logs")));
        Assert.False(File.Exists(Path.Combine(set, "report.docx")));
        Assert.True(File.Exists(Path.Combine(set, "a.txt")));
    }

    [Fact]
    public void A_cancelled_run_is_aborted_and_leaves_nothing_behind()
    {
        string destination = _temp.Combine("Backup");
        var task = NewTask(BackupTypes.Full, destination);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = RunWith(task, Start, null, cancellation.Token);

        Assert.Equal(RunStatuses.Aborted, result.Status);
        Assert.Empty(Directory.GetDirectories(destination));
        Assert.Null(_store.GetBackupState(task.Id).FullRunId);
    }

    [Fact]
    public void Progress_reaches_the_total()
    {
        var reports = new List<BackupProgress>();
        var engine = new BackupEngine(_platform, _store, _log, p => reports.Add(p));
        using var gate = new PauseGate();
        var task = NewTask(BackupTypes.Full, _temp.Combine("Backup"));

        engine.Run(Request(task, Start, "run-1"), gate, TestContext.Current.CancellationToken);

        var last = reports.Last(r => r.Phase == BackupPhases.Copy);
        Assert.Equal(2, last.FilesTotal);
        Assert.Equal(2, last.FilesDone);
        Assert.Equal(last.BytesTotal, last.BytesDone);
        Assert.Equal(100, last.Percent);
    }

    private TaskDefinition NewTask(string type, params string[] destinations) => new()
    {
        Name = "Docs",
        BackupType = type,
        Sources = [new TaskSource { Path = _source }],
        Destinations = [.. destinations.Select(d => new TaskDestination { Kind = DestinationKinds.Local, Path = d })],
        Vss = new VssOptions { Enabled = false },
        Copy = new CopyOptions { ReadRetries = 0 },
    };

    [Fact]
    public void Low_priority_asks_for_low_io_on_every_file_read_and_written()
    {
        var task = NewTask(BackupTypes.Full, _temp.Combine("Backup"));
        task.Verify.EveryRuns = 1;
        var engine = new BackupEngine(_platform, _store, _log);
        using var gate = new PauseGate();
        var normal = engine.Run(Request(task, Start, Guid.NewGuid().ToString()), gate, TestContext.Current.CancellationToken);
        Assert.Equal(RunStatuses.Success, normal.Status);
        Assert.Equal(0, _platform.LoweredFiles);

        var request = Request(task, Start.AddHours(1), Guid.NewGuid().ToString());
        var low = new BackupEngine(_platform, _store, _log).Run(new BackupRequest
        {
            Task = request.Task,
            RunId = request.RunId,
            Trigger = request.Trigger,
            StartedAt = request.StartedAt,
            TimeZone = request.TimeZone,
            LowPriority = true,
        }, gate, TestContext.Current.CancellationToken);

        Assert.Equal(RunStatuses.Success, low.Status);

        // Two sources read, two copies written, two copies read back by the check.
        Assert.Equal(6, _platform.LoweredFiles);
    }

    private BackupResult Run(TaskDefinition task, DateTimeOffset startedAt, string? runId = null) =>
        RunWith(task, startedAt, runId, TestContext.Current.CancellationToken);

    private BackupResult RunWith(TaskDefinition task, DateTimeOffset startedAt, string? runId, CancellationToken cancellationToken)
    {
        var engine = new BackupEngine(_platform, _store, _log);
        using var gate = new PauseGate();
        return engine.Run(Request(task, startedAt, runId ?? Guid.NewGuid().ToString()), gate, cancellationToken);
    }

    private static BackupRequest Request(TaskDefinition task, DateTimeOffset startedAt, string runId) => new()
    {
        Task = task,
        RunId = runId,
        Trigger = RunTriggers.Manual,
        StartedAt = startedAt,
        TimeZone = TimeZoneInfo.Utc,
        LogEveryFile = true,
    };

    private static (SetManifestHeader Header, List<ManifestFile> Files) ReadManifest(string folder)
    {
        string path = Path.Combine(folder, SetManifest.FileName);
        using var headerStream = File.OpenRead(path);
        var header = SetManifest.ReadHeader(headerStream);
        using var filesStream = File.OpenRead(path);
        return (header, SetManifest.ReadFiles(filesStream));
    }

    private static string Sha256(string text) => SetManifest.ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
