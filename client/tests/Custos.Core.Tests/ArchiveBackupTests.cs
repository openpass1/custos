using System.Security.Cryptography;
using System.Text;
using Custos.Core.Archives;
using Custos.Core.Backup;
using Custos.Core.Model;

namespace Custos.Core.Tests;

/// <summary>Runs of tasks that pack into 7z or ZIP (section 6.5). ZIP runs everywhere, 7z on Windows with 7z.dll.</summary>
public sealed class ArchiveBackupTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Modified = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly TempFolder _temp = new();
    private readonly TestPlatform _platform = new();
    private readonly MemoryFileStateStore _store = new();
    private readonly TestLog _log = new();
    private readonly string _source;

    public ArchiveBackupTests()
    {
        _source = _temp.Combine("Data");
        _temp.WriteFile("Data/a.txt", "alpha", Modified);
        _temp.WriteFile("Data/sub/b.txt", "beta", Modified);
        Directory.CreateDirectory(_temp.Combine("Data", "empty"));
        _platform.ArchivePasswords["pw"] = Password;
    }

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(ArchiveFormats.Zip)]
    [InlineData(ArchiveFormats.SevenZip)]
    public void A_full_copy_is_one_archive_with_the_manifest_inside(string format)
    {
        SevenZipCheck.SkipUnlessAvailable(format);
        string destination = _temp.Combine("Backup");
        var task = NewTask(format, BackupTypes.Full, destination);

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Success, result.Status);
        string archive = Path.Combine(destination, "Docs 2026-11-01 10-00 (Full)" + Extension(format));
        Assert.Equal([archive], Directory.GetFiles(destination));
        Assert.Equal(new FileInfo(archive).Length, result.BytesWritten);
        Assert.Equal(2, result.FilesProcessed);

        var sets = SetCatalog.Scan(destination, task.Id, Access());
        var set = Assert.Single(sets);
        Assert.Equal(SetStorage.Archive, set.Storage);
        Assert.Equal(format, set.Format);
        Assert.Equal(BackupTypes.Full, set.Header.BackupType);
        Assert.Equal(9, set.Header.SetBytes);

        var contents = Unpack(set, Access());
        Assert.Equal("alpha", contents[@"Data\a.txt"]);
        Assert.Equal("beta", contents[@"Data\sub\b.txt"]);
        var (_, files) = SetCatalog.ReadManifest(set, Access());
        var a = files.Single(f => ArchivePaths.ToWindows(f.Path) == @"Data\a.txt");
        Assert.True(a.InSet);
        using var reader = Access().Open(set);
        Assert.Contains(reader.Entries, e => e.IsFolder && e.Path == @"Data\empty");
        if (format == ArchiveFormats.Zip)
        {
            Assert.Equal(Sha256("alpha"), a.Sha256);
            Assert.Equal(SetManifest.FileName, reader.Entries[^1].Path);
        }
        else
        {
            // 7-Zip packs by path: custos-set.json before Data\, so the manifest has no SHA-256 for these files.
            Assert.Null(a.Sha256);
            Assert.Contains(reader.Entries, e => e.Path == SetManifest.FileName);
        }

        Assert.NotNull(_store.GetBackupState(task.Id).CompressionRatio);
    }

    [Theory]
    [InlineData(ArchiveFormats.Zip)]
    [InlineData(ArchiveFormats.SevenZip)]
    public void An_encrypted_archive_opens_only_with_its_password(string format)
    {
        SevenZipCheck.SkipUnlessAvailable(format);
        string destination = _temp.Combine("Backup");
        var task = NewTask(format, BackupTypes.Full, destination);
        task.Archive.Encrypt = true;
        task.Archive.EncryptNames = true;
        task.Archive.PasswordCredentialId = "pw";

        Assert.Equal(RunStatuses.Success, Run(task, Start).Status);

        var set = Assert.Single(SetCatalog.Scan(destination, task.Id, Access(Password)));
        Assert.Equal("alpha", Unpack(set, Access(Password))[@"Data\a.txt"]);
        Assert.Empty(SetCatalog.Scan(destination, task.Id, Access("wrong password")));
        var error = Assert.ThrowsAny<Exception>(() => Unpack(set, Access("wrong password")));
        Assert.True(error is ArchiveException or InvalidDataException, error.ToString());
    }

    [Fact]
    public void Encryption_without_a_password_fails_the_run()
    {
        var task = NewTask(ArchiveFormats.Zip, BackupTypes.Full, _temp.Combine("Backup"));
        task.Archive.Encrypt = true;

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.Equal(Messages.ArchivePasswordNotSet, result.ErrorMessage);
    }

    [Theory]
    [InlineData(ArchiveFormats.Zip)]
    [InlineData(ArchiveFormats.SevenZip)]
    public void An_incremental_archive_holds_only_changes_and_finds_its_full_archive(string format)
    {
        SevenZipCheck.SkipUnlessAvailable(format);
        string destination = _temp.Combine("Backup");
        var task = NewTask(format, BackupTypes.Incremental, destination);

        Assert.Equal(BackupTypes.Full, Run(task, Start, "run-1").BackupType);
        _temp.WriteFile("Data/a.txt", "alpha 2", Modified.AddHours(1));
        var second = Run(task, Start.AddDays(1), "run-2");

        Assert.Equal(RunStatuses.Success, second.Status);
        Assert.Equal(BackupTypes.Incremental, second.BackupType);
        var sets = SetCatalog.Scan(destination, task.Id, Access());
        Assert.Equal(2, sets.Count);
        Assert.Equal("run-1", sets[1].Header.ParentFullRunId);
        var contents = Unpack(sets[1], Access());
        Assert.Equal("alpha 2", contents[@"Data\a.txt"]);
        Assert.False(contents.ContainsKey(@"Data\sub\b.txt"));
    }

    [Theory]
    [InlineData(ArchiveFormats.Zip)]
    [InlineData(ArchiveFormats.SevenZip)]
    public void Retention_by_count_deletes_old_archives(string format)
    {
        SevenZipCheck.SkipUnlessAvailable(format);
        string destination = _temp.Combine("Backup");
        var task = NewTask(format, BackupTypes.Full, destination);
        task.Retention = new RetentionOptions { Mode = RetentionModes.Count, KeepFull = 2 };

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(RunStatuses.Success, Run(task, Start.AddDays(i)).Status);
        }

        var sets = SetCatalog.Scan(destination, task.Id, Access());
        Assert.Equal(["Docs 2026-11-02 10-00 (Full)", "Docs 2026-11-03 10-00 (Full)"], sets.Select(s => s.Header.SetName));
        Assert.Equal(2, Directory.GetFiles(destination).Length);
    }

    [Theory]
    [InlineData(ArchiveFormats.Zip)]
    [InlineData(ArchiveFormats.SevenZip)]
    public void Every_destination_gets_the_same_archive_and_it_is_verified(string format)
    {
        SevenZipCheck.SkipUnlessAvailable(format);
        string first = _temp.Combine("One");
        string second = _temp.Combine("Two");
        var task = NewTask(format, BackupTypes.Full, first, second);
        task.Verify.EveryRuns = 1;

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Success, result.Status);
        byte[] one = File.ReadAllBytes(Assert.Single(Directory.GetFiles(first)));
        byte[] two = File.ReadAllBytes(Assert.Single(Directory.GetFiles(second)));
        Assert.Equal(one, two);
        Assert.Contains(_log.Lines, l => l.Message == Messages.Format(Messages.VerifyNewOk, first));
        Assert.Contains(_log.Lines, l => l.Message == Messages.Format(Messages.VerifyNewOk, second));
        Assert.Equal(0, _store.GetBackupState(task.Id).RunsSinceVerify);
    }

    [Theory]
    [InlineData(ArchiveFormats.Zip)]
    [InlineData(ArchiveFormats.SevenZip)]
    public void A_damaged_stored_archive_makes_the_run_an_error(string format)
    {
        SevenZipCheck.SkipUnlessAvailable(format);
        string destination = _temp.Combine("Backup");
        var task = NewTask(format, BackupTypes.Full, destination);
        task.Archive.Level = 0;
        task.Verify.StoredEveryDays = 1;

        Assert.Equal(RunStatuses.Success, Run(task, Start).Status);
        Assert.NotNull(_store.GetBackupState(task.Id).StoredCheckedAt);
        string archive = Assert.Single(Directory.GetFiles(destination));
        byte[] bytes = File.ReadAllBytes(archive);
        int at = IndexOf(bytes, Encoding.UTF8.GetBytes("alpha"));
        Assert.True(at >= 0, "Level 0 stores the data as it is.");
        bytes[at] = (byte)'A';
        File.WriteAllBytes(archive, bytes);

        var result = Run(task, Start.AddDays(2));

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.Contains("Docs 2026-11-01 10-00 (Full)", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(2, Directory.GetFiles(destination).Length);
    }

    [Fact]
    public void Stored_copies_are_not_verified_again_before_their_time()
    {
        var task = NewTask(ArchiveFormats.Zip, BackupTypes.Full, _temp.Combine("Backup"));
        task.Verify.StoredEveryDays = 7;

        Run(task, Start);
        var checkedAt = _store.GetBackupState(task.Id).StoredCheckedAt;
        Run(task, Start.AddDays(1));

        Assert.Equal(checkedAt, _store.GetBackupState(task.Id).StoredCheckedAt);
    }

    [Fact]
    public void Volumes_are_numbered_and_read_back_as_one_archive()
    {
        SevenZipCheck.SkipUnlessAvailable(ArchiveFormats.SevenZip);
        var random = new byte[3 * 1024 * 1024];
        new Random(42).NextBytes(random);
        File.WriteAllBytes(_temp.Combine("Data", "big.bin"), random);
        string destination = _temp.Combine("Backup");
        var task = NewTask(ArchiveFormats.SevenZip, BackupTypes.Full, destination);
        task.Archive.SplitMb = 1;
        task.Verify.EveryRuns = 1;

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Success, result.Status);
        var names = Directory.GetFiles(destination).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.True(names.Count >= 3, string.Join(", ", names));
        Assert.Equal("Docs 2026-11-01 10-00 (Full).7z.001", names[0]);
        var set = Assert.Single(SetCatalog.Scan(destination, task.Id, Access()));
        Assert.Equal(names.Count, set.Parts.Count);
        using var reader = Access().Open(set);
        var entry = reader.Entries.Single(e => e.Path == @"Data\big.bin");
        using var output = new MemoryStream();
        reader.Extract([entry.Index], _ => output, (_, outcome) => Assert.Equal(ExtractOutcome.Ok, outcome), TestContext.Current.CancellationToken);
        Assert.Equal(random, output.ToArray());
    }

    [Fact]
    public void Without_7z_only_the_7z_task_fails_and_says_why()
    {
        _platform.ArchivesOverride = new ArchiveProvider(_temp.Combine("missing", "7z.dll"));
        var sevenZip = NewTask(ArchiveFormats.SevenZip, BackupTypes.Full, _temp.Combine("Seven"));
        var zip = NewTask(ArchiveFormats.Zip, BackupTypes.Full, _temp.Combine("Zip"));

        var failed = Run(sevenZip, Start);
        var fine = Run(zip, Start);

        Assert.Equal(RunStatuses.Error, failed.Status);
        Assert.Contains(OperatingSystem.IsWindows() ? "7z.dll" : "7z", failed.ErrorMessage, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_temp.Combine("Seven")) && Directory.GetFiles(_temp.Combine("Seven")).Length > 0);
        Assert.Equal(RunStatuses.Success, fine.Status);
    }

    [Fact]
    public void A_cancelled_archive_run_leaves_no_partial_files()
    {
        string destination = _temp.Combine("Backup");
        var task = NewTask(ArchiveFormats.Zip, BackupTypes.Full, destination);
        using var cancellation = new CancellationTokenSource();
        var engine = new BackupEngine(_platform, _store, _log, p =>
        {
            if (p.Phase == BackupPhases.Copy)
            {
                cancellation.Cancel();
            }
        });
        using var gate = new PauseGate();

        var result = engine.Run(Request(task, Start, "run-1"), gate, cancellation.Token);

        Assert.Equal(RunStatuses.Aborted, result.Status);
        Assert.Empty(Directory.GetFiles(destination));
    }

    private TaskDefinition NewTask(string format, string type, params string[] destinations) => new()
    {
        Name = "Docs",
        BackupType = type,
        Sources = [new TaskSource { Path = _source }],
        Destinations = [.. destinations.Select(d => new TaskDestination { Kind = DestinationKinds.Local, Path = d })],
        Vss = new VssOptions { Enabled = false },
        Copy = new CopyOptions { ReadRetries = 0 },
        Archive = new ArchiveOptions { Format = format, Level = 5 },
    };

    private ArchiveAccess Access(string? password = null) => new(_platform.Archives, password);

    private BackupResult Run(TaskDefinition task, DateTimeOffset startedAt, string? runId = null)
    {
        var engine = new BackupEngine(_platform, _store, _log);
        using var gate = new PauseGate();
        return engine.Run(Request(task, startedAt, runId ?? Guid.NewGuid().ToString()), gate, TestContext.Current.CancellationToken);
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

    /// <summary>Every file of an archive set as text, by path.</summary>
    private static Dictionary<string, string> Unpack(StoredSet set, ArchiveAccess access)
    {
        using var reader = access.Open(set);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buffers = new Dictionary<int, MemoryStream>();
        reader.Extract(
            [.. reader.Entries.Where(e => !e.IsFolder).Select(e => e.Index)],
            e => buffers[e.Index] = new MemoryStream(),
            (e, outcome) =>
            {
                if (outcome != ExtractOutcome.Ok)
                {
                    throw new ArchiveException(outcome.ToString());
                }

                result[e.Path] = Encoding.UTF8.GetString(buffers[e.Index].ToArray());
            },
            TestContext.Current.CancellationToken);
        return result;
    }

    private static string Extension(string format) => format == ArchiveFormats.Zip ? ".zip" : ".7z";

    private static int IndexOf(byte[] data, byte[] pattern)
    {
        for (int i = 0; i + pattern.Length <= data.Length; i++)
        {
            if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Sha256(string text) => SetManifest.ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
