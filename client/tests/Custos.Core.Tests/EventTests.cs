using Custos.Core.Backup;
using Custos.Core.Model;

namespace Custos.Core.Tests;

/// <summary>Events before and after a run (section 6.9), with the shell of the system the tests run on.</summary>
public sealed class EventTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly TempFolder _temp = new();
    private readonly TestPlatform _platform = new();
    private readonly MemoryFileStateStore _store = new();
    private readonly TestLog _log = new();

    public EventTests() => _temp.WriteFile("Data/a.txt", "alpha", new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Events_run_before_and_after_and_their_output_is_logged()
    {
        var task = NewTask();
        task.Events.Pre.Add(Shell("before-copy", 0));
        task.Events.Post.Add(Shell("after-copy", 0));

        var result = Run(task);

        Assert.Equal(RunStatuses.Success, result.Status);
        var lines = _log.Lines.Select(l => l.Message).ToList();
        int before = lines.FindIndex(l => l.Contains("before-copy", StringComparison.Ordinal) && l.StartsWith('>'));
        int copied = lines.FindIndex(l => l.Contains("a.txt", StringComparison.Ordinal));
        int after = lines.FindIndex(l => l.Contains("after-copy", StringComparison.Ordinal) && l.StartsWith('>'));
        Assert.True(before >= 0 && copied > before && after > copied, string.Join(Environment.NewLine, lines));
    }

    [Fact]
    public void Arguments_of_an_event_take_the_variables_of_paths()
    {
        _platform.EnvironmentValues["CUSTOS_DUMP"] = "dump";
        var task = NewTask();
        task.Events.Pre.Add(Shell("%CUSTOS_DUMP%-{task}-{yyyy}{MM}{dd}", 0));

        var result = Run(task);

        Assert.Equal(RunStatuses.Success, result.Status);
        Assert.Contains(_log.Lines, l => l.Message == "> dump-Docs-20261101");
    }

    [Fact]
    public void A_failed_event_before_stops_the_run_but_the_events_after_still_run()
    {
        var task = NewTask();
        task.Events.Pre.Add(new TaskEvent { Kind = TaskEventKinds.ServiceStop, Service = "Database" });
        task.Events.Pre.Add(Shell("dump failed", 3));
        task.Events.Post.Add(new TaskEvent { Kind = TaskEventKinds.ServiceStart, Service = "Database" });

        var result = Run(task);

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.Contains("3", result.ErrorMessage, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_temp.Combine("Backup")) && Directory.EnumerateFileSystemEntries(_temp.Combine("Backup")).Any());
        Assert.Equal([("Database", false), ("Database", true)], _platform.Services);
    }

    [Fact]
    public void Without_stopping_a_failed_event_before_is_a_warning()
    {
        var task = NewTask();
        task.Events.AbortOnPreFailure = false;
        task.Events.Pre.Add(Shell("dump failed", 1));

        var result = Run(task);

        Assert.Equal(RunStatuses.Warning, result.Status);
        Assert.Equal(1, result.FilesProcessed);
    }

    [Fact]
    public void A_failed_event_after_is_a_warning()
    {
        var task = NewTask();
        _platform.BrokenService = "Mail";
        task.Events.Post.Add(new TaskEvent { Kind = TaskEventKinds.ServiceStart, Service = "Mail" });
        task.Events.Post.Add(Shell("still-runs", 0));

        var result = Run(task);

        Assert.Equal(RunStatuses.Warning, result.Status);
        Assert.Contains(_log.Lines, l => l.Level == RunLogLevel.Warning && l.Message.Contains("Mail", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Message.Contains("still-runs", StringComparison.Ordinal));
    }

    [Fact]
    public void A_program_that_runs_too_long_is_stopped()
    {
        var task = NewTask();
        var slow = OperatingSystem.IsWindows()
            ? new TaskEvent { Command = Cmd, Args = "/d /c ping -n 30 127.0.0.1 >nul", TimeoutS = 1 }
            : new TaskEvent { Command = "/bin/sh", Args = "-c \"sleep 30\"", TimeoutS = 1 };
        task.Events.Pre.Add(slow);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var result = Run(task);

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), clock.Elapsed.ToString());
    }

    [Fact]
    public void A_missing_program_is_an_error_in_words()
    {
        var task = NewTask();
        task.Events.Pre.Add(new TaskEvent { Command = _temp.Combine("no-such-program.exe") });

        var result = Run(task);

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.Contains("no-such-program.exe", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_batch_file_gets_its_arguments()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Batch files need Windows.");
        string script = _temp.WriteFile("Scripts/with space/dump.cmd", "@echo arg1=%1 arg2=%2\r\n@exit /b 0\r\n", DateTime.UtcNow);
        var task = NewTask();
        task.Events.Pre.Add(new TaskEvent { Command = script, Args = "first \"second one\"" });

        var result = Run(task);

        Assert.Equal(RunStatuses.Success, result.Status);
        Assert.Contains(_log.Lines, l => l.Message.Contains("arg1=first arg2=\"second one\"", StringComparison.Ordinal));
    }

    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>Prints the text and ends with the code, in cmd on Windows and sh elsewhere.</summary>
    private static TaskEvent Shell(string text, int code) => OperatingSystem.IsWindows()
        ? new TaskEvent { Command = Cmd, Args = $"/d /c \"echo {text}& exit /b {code}\"" }
        : new TaskEvent { Command = "/bin/sh", Args = $"-c \"echo {text}; exit {code}\"" };

    private TaskDefinition NewTask() => new()
    {
        Name = "Docs",
        BackupType = BackupTypes.Full,
        Sources = [new TaskSource { Path = _temp.Combine("Data") }],
        Destinations = [new TaskDestination { Kind = DestinationKinds.Local, Path = _temp.Combine("Backup") }],
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
            LogEveryFile = true,
        }, gate, TestContext.Current.CancellationToken);
    }
}
