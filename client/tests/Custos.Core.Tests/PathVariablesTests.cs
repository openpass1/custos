using Custos.Core.Backup;
using Custos.Core.Model;

namespace Custos.Core.Tests;

/// <summary>Variables in the paths of a task (section 6.2).</summary>
public sealed class PathVariablesTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 5, 0, TimeSpan.Zero);

    private readonly TempFolder _temp = new();
    private readonly TestPlatform _platform = new();
    private readonly MemoryFileStateStore _store = new();
    private readonly TestLog _log = new();

    public PathVariablesTests() => _temp.WriteFile("Data/a.txt", "alpha", new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Tokens_and_variables_are_filled_in()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemDrive"] = "C:",
            ["USERPROFILE"] = @"C:\Users\anna",
        };
        var variables = new PathVariables(n => values.GetValueOrDefault(n), hasProfile: true, "Docs: main", "PC-01", BackupTypes.Incremental, new DateTime(2026, 3, 7, 9, 4, 0));

        Assert.Equal(@"C:\Users\anna\Documents", variables.Expand(@"%UserProfile%\Documents", out string? problem));
        Assert.Null(problem);
        Assert.Equal(@"C:\Backup\PC-01\Docs_ main\2026-03-07 09-04 Incremental", variables.Expand(@"%SystemDrive%\Backup\{computer}\{task}\{yyyy}-{MM}-{dd} {HH}-{mm} {type}"));
        Assert.Equal("100% sure", variables.Expand("100% sure"));
    }

    [Fact]
    public void An_unknown_variable_stays_and_says_why()
    {
        var withProfile = new PathVariables(_ => null, hasProfile: true, "Docs", "PC", BackupTypes.Full, DateTime.Now);
        Assert.Equal(@"%NOPE%\x", withProfile.Expand(@"%NOPE%\x", out string? unknown));
        Assert.Equal(Messages.Format(Messages.PathVariableUnknown, "%NOPE%", @"%NOPE%\x"), unknown);

        // Without a profile, %USERPROFILE% is not the profile of the service (SYSTEM): it stays unknown.
        var noProfile = new PathVariables(_ => @"C:\Windows\system32\config\systemprofile", hasProfile: false, "Docs", "PC", BackupTypes.Full, DateTime.Now);
        noProfile.Expand(@"%USERPROFILE%\Documents", out string? profile);
        Assert.Equal(Messages.Format(Messages.PathVariableNoProfile, "%USERPROFILE%", @"%USERPROFILE%\Documents"), profile);
    }

    [Theory]
    [InlineData(@"%USERPROFILE%\Documents", true)]
    [InlineData("%OneDrive%", true)]
    [InlineData(@"\\server\share\{computer}", true)]
    [InlineData(@"Documents\%USERPROFILE%", false)]
    [InlineData("%USERPROFILE%Documents", false)]
    [InlineData("", false)]
    public void A_path_that_starts_with_a_variable_counts_as_full(string path, bool expected)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) && !OperatingSystem.IsWindows())
        {
            path = "/srv/share/{computer}";
        }

        Assert.Equal(expected, PathVariables.LooksAbsolute(path));
    }

    [Fact]
    public void A_run_fills_in_the_paths_and_the_manifest_keeps_the_real_ones()
    {
        _platform.EnvironmentValues["CUSTOS_TEST_ROOT"] = _temp.Path;
        _platform.EnvironmentValues["USERPROFILE"] = _temp.Path;
        var task = NewTask("%CUSTOS_TEST_ROOT%/Data", Path.Combine("%USERPROFILE%", "Backup", "{computer}"));
        task.ProfileSid = _platform.ProfileSid;

        var result = Run(task);

        Assert.Equal(RunStatuses.Success, result.Status);
        string set = Assert.Single(Directory.GetDirectories(_temp.Combine("Backup", "TESTPC")));
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(set, "Data", "a.txt")));
        using var stream = File.OpenRead(Path.Combine(set, SetManifest.FileName));
        var header = SetManifest.ReadHeader(stream);
        Assert.Equal(Path.Combine(_temp.Path, "Data"), Assert.Single(header.Sources).Path.Replace('/', Path.DirectorySeparatorChar));
    }

    [Fact]
    public void A_profile_variable_in_a_task_without_a_profile_stops_the_run()
    {
        _platform.EnvironmentValues["USERPROFILE"] = _temp.Path;
        var task = NewTask(Path.Combine("%USERPROFILE%", "Data"), _temp.Combine("Backup"));

        var result = Run(task);

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.Contains("%USERPROFILE%", result.ErrorMessage, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_temp.Combine("Backup")));
    }

    private static TaskDefinition NewTask(string source, string destination) => new()
    {
        Name = "Docs",
        BackupType = BackupTypes.Full,
        Sources = [new TaskSource { Path = source }],
        Destinations = [new TaskDestination { Kind = DestinationKinds.Local, Path = destination }],
        Layout = new LayoutOptions { SeparateFolders = true },
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
