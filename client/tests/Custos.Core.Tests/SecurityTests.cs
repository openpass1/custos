using Custos.Core.Archives;
using Custos.Core.Backup;
using Custos.Core.Model;

namespace Custos.Core.Tests;

/// <summary>NTFS permissions of plain copies (section 6.8). The platform of the tests stands in for Windows.</summary>
public sealed class SecurityTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Modified = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly TempFolder _temp = new();
    private readonly TestPlatform _platform = new();
    private readonly MemoryFileStateStore _store = new();
    private readonly TestLog _log = new();

    public SecurityTests()
    {
        _temp.WriteFile("Data/a.txt", "alpha", Modified);
        _temp.WriteFile("Data/sub/b.txt", "beta", Modified);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Files_and_folders_get_the_permissions_of_their_sources()
    {
        var result = Run(NewTask());

        Assert.Equal(RunStatuses.Success, result.Status);
        string copy = _temp.Combine("Backup", "Data");
        Assert.Contains((Path.Combine(copy, "a.txt"), false, _temp.Combine("Data", "a.txt")), _platform.SecurityWritten);
        Assert.Contains((Path.Combine(copy, "sub", "b.txt"), false, _temp.Combine("Data", "sub", "b.txt")), _platform.SecurityWritten);
        Assert.Contains((Path.Combine(copy, "sub"), true, _temp.Combine("Data", "sub")), _platform.SecurityWritten);
        Assert.Contains((copy, true, _temp.Combine("Data")), _platform.SecurityWritten);

        // Folders come after the files, so that a folder closed for writing does not stop the copy into it.
        int lastFile = _platform.SecurityWritten.FindLastIndex(w => !w.Folder);
        int firstFolder = _platform.SecurityWritten.FindIndex(w => w.Folder);
        Assert.True(firstFolder > lastFile);
    }

    [Fact]
    public void Nothing_is_set_when_switched_off_or_for_archives()
    {
        var off = NewTask();
        off.Copy.PreserveAcl = false;
        Assert.Equal(RunStatuses.Success, Run(off).Status);

        var zip = NewTask();
        zip.Archive.Format = ArchiveFormats.Zip;
        Assert.Equal(RunStatuses.Success, Run(zip).Status);

        Assert.Empty(_platform.SecurityWritten);
    }

    [Fact]
    public void A_destination_that_refuses_permissions_gets_the_files_and_one_warning()
    {
        _platform.SecurityBroken = true;

        var result = Run(NewTask());

        Assert.Equal(RunStatuses.Warning, result.Status);
        Assert.Equal(2, result.FilesProcessed);
        Assert.Equal("beta", File.ReadAllText(_temp.Combine("Backup", "Data", "sub", "b.txt")));
        Assert.Single(_log.Lines, l => l.Level == RunLogLevel.Warning && l.Message.Contains("access denied", StringComparison.Ordinal));
    }

    private TaskDefinition NewTask() => new()
    {
        Name = "Docs",
        BackupType = BackupTypes.Full,
        Sources = [new TaskSource { Path = _temp.Combine("Data") }],
        Destinations = [new TaskDestination { Kind = DestinationKinds.Local, Path = _temp.Combine("Backup") }],
        Layout = new LayoutOptions { SeparateFolders = false },
        Vss = new VssOptions { Enabled = false },
        Copy = new CopyOptions { ReadRetries = 0 },
    };

    private BackupResult Run(TaskDefinition task)
    {
        var engine = new BackupEngine(_platform, _store, _log);
        using var gate = new PauseGate();
        return engine.Run(new BackupRequest
        {
            Task = task,
            RunId = Guid.NewGuid().ToString(),
            Trigger = RunTriggers.Manual,
            StartedAt = Start,
            TimeZone = TimeZoneInfo.Utc,
        }, gate, TestContext.Current.CancellationToken);
    }
}
