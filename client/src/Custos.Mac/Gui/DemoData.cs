using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Gui;

namespace Custos.Mac.Gui;

/// <summary>
/// Made-up tasks, runs and log lines (dash-dash-demo and the CI pictures): no real paths, servers or accounts. «Now» is
/// Wednesday 18.11.2026 15:31 local time, so the pictures are the same on every run.
/// </summary>
internal static class DemoData
{
    private const string Account = "aaaaaaaa-0000-4000-8000-000000000001";
    private const string SftpAccount = "aaaaaaaa-0000-4000-8000-000000000002";
    private const string DesktopRun = "44444444-0000-4000-8000-000000000004";

    public static readonly DateTimeOffset Now = new(new DateTime(2026, 11, 18, 15, 31, 12, DateTimeKind.Local));

    private static readonly string[] Ids =
    [
        "11111111-0000-4000-8000-000000000001", "11111111-0000-4000-8000-000000000002", "11111111-0000-4000-8000-000000000003",
        "11111111-0000-4000-8000-000000000004", "11111111-0000-4000-8000-000000000005", "11111111-0000-4000-8000-000000000006",
    ];

    private static string Accounting => Ids[0];

    private static string Desktop => Ids[1];

    private static string Documents => Ids[2];

    private static string Photos => Ids[3];

    private static string Projects => Ids[4];

    private static string OldArchive => Ids[5];

    /// <summary>Hands the made-up state to the monitor as if the service had sent it.</summary>
    public static void Feed(ServiceMonitor monitor)
    {
        monitor.SimulateSnapshot(Snapshot());
        monitor.SimulateProgress(new RunProgressEvent { RunId = DesktopRun, TaskId = Desktop, Progress = DesktopProgress() });
        monitor.SimulateLog(Log());
    }

    public static HistoryResult History(string taskId)
    {
        var runs = new List<RunRecord>();
        if (taskId == Photos)
        {
            runs.Add(Run(Photos, "Photos", BackupTypes.Full, At(-15, 21, 0), 2, RunStatuses.Error, 0, 0, MacStrings.DemoUsb));
            runs.Add(Run(Photos, "Photos", BackupTypes.Full, At(-17, 23, 0), 848, RunStatuses.Success, 8412, 46_300_000_000));
            runs.Add(Run(Photos, "Photos", BackupTypes.Full, At(-18, 23, 0), 831, RunStatuses.Success, 8377, 46_100_000_000));
            var manual = Run(Photos, "Photos", BackupTypes.Full, At(-35, 18, 20), 820, RunStatuses.Warning, 8290, 45_600_000_000);
            manual.Trigger = RunTriggers.Manual;
            manual.RequestedBy = "admin";
            manual.Warnings = 2;
            runs.Add(manual);
        }
        else
        {
            var (name, type, files, bytes) = taskId == Desktop ? ("Desktop", BackupTypes.Incremental, 12640L, 212_000_000L)
                : taskId == Accounting ? ("Accounting", BackupTypes.Differential, 3118L, 1_900_000_000L)
                : taskId == Documents ? ("Documents", BackupTypes.Incremental, 2210L, 1_100_000_000L)
                : taskId == Projects ? ("Projects", BackupTypes.Mirror, 41207L, 3_400_000_000L)
                : (string.Empty, BackupTypes.Full, 0L, 0L);
            if (name.Length > 0)
            {
                for (int i = 0; i < 12; i++)
                {
                    var started = Started(taskId, i);
                    string status = taskId == Documents && i == 0 ? RunStatuses.Warning : RunStatuses.Success;
                    var run = Run(taskId, name, type, started, 38 + (i * 3), status, files - (i * 17), bytes - (i * 1_000_000));
                    run.Warnings = status == RunStatuses.Warning ? 3 : 0;
                    runs.Add(run);
                }
            }
        }

        return new HistoryResult { Runs = runs };
    }

    private static AgentSnapshot Snapshot() => new()
    {
        Version = 1,
        ConfigRevision = 14,
        ComputerName = "Office-Mac-03",
        Settings = new AgentSettings(),
        Credentials =
        [
            new Credential { Id = Account, Kind = CredentialKinds.Windows, User = "backup", HasSecret = true },
            new Credential { Id = SftpAccount, Kind = CredentialKinds.Sftp, User = "backup-sftp", HasSecret = true },
        ],
        Tasks =
        [
            new TaskDefinition
            {
                Id = Accounting,
                Name = "Accounting",
                BackupType = BackupTypes.Differential,
                Sources = [new TaskSource { Id = "s1", Kind = SourceKinds.Folder, Path = "/Users/Shared/Accounting" }],
                Destinations = [new TaskDestination { Id = "d1", Kind = DestinationKinds.Unc, Path = "//fileserver/backup/accounting", CredentialId = Account }],
                Archive = new ArchiveOptions { Format = ArchiveFormats.SevenZip, Level = 5, Encrypt = true },
                Retention = new RetentionOptions { Mode = RetentionModes.Count, KeepFull = 8 },
                Schedule = new ScheduleOptions { Type = ScheduleTypes.Weekly, Times = ["11:30"], DaysOfWeek = [1, 2, 3, 4, 5] },
                UpdatedAt = At(-6, 10, 0),
                UpdatedBy = "local:admin",
            },
            new TaskDefinition
            {
                Id = Desktop,
                Name = "Desktop",
                BackupType = BackupTypes.Incremental,
                Sources = [new TaskSource { Id = "s2", Kind = SourceKinds.Folder, Path = "/Users/anna/Desktop" }],
                Destinations = [new TaskDestination { Id = "d2", Kind = DestinationKinds.Unc, Path = "//fileserver/backup/desktop", CredentialId = Account }],
                Archive = new ArchiveOptions { Format = ArchiveFormats.SevenZip, Level = 5, Encrypt = true },
                Retention = new RetentionOptions { Mode = RetentionModes.Count, KeepFull = 96 },
                Schedule = new ScheduleOptions { Type = ScheduleTypes.Interval, IntervalMinutes = 15, CatchUp = false },
                UpdatedAt = At(-6, 10, 5),
                UpdatedBy = "server:admin",
            },
            new TaskDefinition
            {
                Id = Documents,
                Name = "Documents",
                BackupType = BackupTypes.Incremental,
                Sources = [new TaskSource { Id = "s3", Kind = SourceKinds.Folder, Path = "/Users/Shared/Data" }],
                Destinations =
                [
                    new TaskDestination { Id = "d3", Kind = DestinationKinds.Usb, Path = "Backups/Documents", UsbLabel = "BACKUP-USB" },
                    new TaskDestination { Id = "d4", Kind = DestinationKinds.Unc, Path = "//nas/backup/documents", CredentialId = Account, WormDays = 7, WormLockHours = 1 },
                    new TaskDestination { Id = "d5", Kind = DestinationKinds.Sftp, Host = "backup.example.org", Path = "/backup/office", CredentialId = SftpAccount, SpeedLimitKbps = 20_000 },
                ],
                Retention = new RetentionOptions { Mode = RetentionModes.Age, MaxAgeDays = 30 },
                Schedule = new ScheduleOptions { Type = ScheduleTypes.Daily, Times = ["23:00"] },
                UpdatedAt = At(-3, 9, 40),
                UpdatedBy = "server:admin",
            },
            new TaskDefinition
            {
                Id = Photos,
                Name = "Photos",
                BackupType = BackupTypes.Full,
                Sources = [new TaskSource { Id = "s4", Kind = SourceKinds.Folder, Path = "/Users/anna/Pictures" }],
                Destinations = [new TaskDestination { Id = "d6", Kind = DestinationKinds.Usb, Path = "Photos", UsbLabel = "BACKUP-USB" }],
                Schedule = new ScheduleOptions { Type = ScheduleTypes.Monthly, Times = ["23:00"], DaysOfMonth = [1, -1] },
                UpdatedAt = At(-20, 12, 0),
                UpdatedBy = "local:admin",
            },
            new TaskDefinition
            {
                Id = Projects,
                Name = "Projects",
                BackupType = BackupTypes.Mirror,
                Sources = [new TaskSource { Id = "s5", Kind = SourceKinds.Folder, Path = "/Users/Shared/Projects" }],
                Destinations = [new TaskDestination { Id = "d7", Kind = DestinationKinds.Unc, Path = "//nas/backup/projects", CredentialId = Account }],
                Schedule = new ScheduleOptions { Type = ScheduleTypes.Weekly, Times = ["08:30", "20:00"], DaysOfWeek = [1, 3, 5] },
                UpdatedAt = At(-6, 10, 0),
                UpdatedBy = "local:admin",
            },
            new TaskDefinition
            {
                Id = OldArchive,
                Name = "Old archive",
                Enabled = false,
                BackupType = BackupTypes.Full,
                Sources = [new TaskSource { Id = "s6", Kind = SourceKinds.Folder, Path = "/Volumes/Archive" }],
                Destinations = [new TaskDestination { Id = "d8", Kind = DestinationKinds.Local, Path = "/Volumes/Copies/CUSTOS" }],
                Schedule = new ScheduleOptions { Type = ScheduleTypes.Manual },
            },
        ],
        States =
        [
            State(Accounting, Run(Accounting, "Accounting", BackupTypes.Differential, At(0, 11, 30), 151, RunStatuses.Success, 3118, 1_900_000_000), At(1, 11, 30)),
            new TaskState
            {
                TaskId = Desktop,
                Running = true,
                LastRun = Run(Desktop, "Desktop", BackupTypes.Incremental, At(0, 15, 15), 38, RunStatuses.Success, 12640, 212_000_000),
                LastSuccess = Run(Desktop, "Desktop", BackupTypes.Incremental, At(0, 15, 15), 38, RunStatuses.Success, 12640, 212_000_000),
                NextRun = At(0, 15, 45),
            },
            State(Documents, Run(Documents, "Documents", BackupTypes.Incremental, At(-1, 23, 0), 72, RunStatuses.Warning, 2210, 1_100_000_000), At(0, 23, 0),
                Run(Documents, "Documents", BackupTypes.Incremental, At(-2, 23, 0), 70, RunStatuses.Success, 2198, 1_090_000_000)),
            State(Photos, Run(Photos, "Photos", BackupTypes.Full, At(-15, 21, 0), 2, RunStatuses.Error, 0, 0, MacStrings.DemoUsb), At(12, 23, 0),
                Run(Photos, "Photos", BackupTypes.Full, At(-17, 23, 0), 848, RunStatuses.Success, 8412, 46_300_000_000)),
            State(Projects, Run(Projects, "Projects", BackupTypes.Mirror, At(0, 8, 30), 65, RunStatuses.Success, 41207, 3_400_000_000), At(0, 20, 0)),
            new TaskState { TaskId = OldArchive },
        ],
        Current = new CurrentRun
        {
            RunId = DesktopRun,
            TaskId = Desktop,
            TaskName = "Desktop",
            Trigger = RunTriggers.Schedule,
            ScheduledFor = At(0, 15, 30),
            StartedAt = At(0, 15, 30),
            Progress = DesktopProgress(),
        },
        Server = new ServerLinkInfo { State = ServerLinkStates.Approved, ServerHost = "monitor.example.org" },
    };

    private static BackupProgress DesktopProgress() => new()
    {
        Phase = BackupPhases.Copy,
        BackupType = BackupTypes.Incremental,
        FilesDone = 5812,
        FilesTotal = 12640,
        BytesDone = 98_000_000,
        BytesTotal = 212_000_000,
        SpeedBps = 1_100_000,
        EtaS = 104,
        CurrentFile = "/Users/anna/Desktop/Reports/2026-11 budget.xlsx",
        Destinations = [new DestinationProgress { DestinationId = "d2", Label = "//fileserver/backup/desktop", State = DestinationStates.Writing }],
    };

    private static List<LogLine> Log()
    {
        var lines = new List<(DateTimeOffset Time, string Level, string? Task, string Text)>
        {
            (At(0, 8, 30), LogLineLevels.Info, "Projects", MacStrings.DemoStarted),
            (At(0, 8, 31).AddSeconds(5), LogLineLevels.Info, "Projects", Done(41207, 3_400_000_000, 65)),
            (At(0, 11, 30), LogLineLevels.Info, "Accounting", MacStrings.DemoStarted),
            (At(0, 11, 32).AddSeconds(31), LogLineLevels.Info, "Accounting", Done(3118, 1_900_000_000, 151)),
            (At(0, 15, 15), LogLineLevels.Info, "Desktop", MacStrings.DemoStarted),
            (At(0, 15, 15).AddSeconds(38), LogLineLevels.Info, "Desktop", Done(12640, 212_000_000, 38)),
            (At(0, 15, 30), LogLineLevels.Info, "Desktop", MacStrings.DemoStarted),
            (At(0, 15, 30).AddSeconds(4), LogLineLevels.Detail, "Desktop", MacStrings.DemoScan),
            (At(0, 15, 31).AddSeconds(12), LogLineLevels.Warning, "Desktop", MacStrings.DemoLocked),
        };
        long seq = 1;
        return [.. lines.Select(l => new LogLine
        {
            Seq = seq++,
            Time = l.Time,
            Level = l.Level,
            TaskName = l.Task,
            TaskId = l.Task switch
            {
                "Projects" => Projects,
                "Accounting" => Accounting,
                "Desktop" => Desktop,
                _ => null,
            },
            Text = l.Text,
        })];
    }

    private static string Done(long files, long bytes, int seconds) => Strings.Format(MacStrings.DemoDone,
        Describe.Number(files), Core.Text.Sizes.Format(bytes), Core.Text.Sizes.FormatDuration(TimeSpan.FromSeconds(seconds)));

    private static TaskState State(string id, RunRecord last, DateTimeOffset next, RunRecord? success = null) => new()
    {
        TaskId = id,
        LastRun = last,
        LastSuccess = success ?? (RunStatuses.IsSuccessful(last.Status!) ? last : null),
        NextRun = next,
    };

    private static RunRecord Run(string taskId, string name, string type, DateTimeOffset started, int seconds, string status, long files, long bytes,
        string? error = null) => new()
    {
        RunId = Guid.NewGuid().ToString(),
        TaskId = taskId,
        TaskName = name,
        BackupType = type,
        Trigger = RunTriggers.Schedule,
        ScheduledFor = started,
        StartedAt = started,
        FinishedAt = started.AddSeconds(seconds),
        Status = status,
        SourceFiles = files,
        SourceBytes = bytes,
        FilesProcessed = files,
        BytesWritten = bytes,
        Errors = error is null ? 0 : 1,
        ErrorMessage = error,
    };

    /// <summary>A day from «now» (negative = before) at the given local time.</summary>
    /// <summary>When the i-th run before now started, by the task's schedule.</summary>
    private static DateTimeOffset Started(string taskId, int i)
    {
        if (taskId == Desktop)
        {
            return At(0, 15, 15).AddMinutes(-15 * i);
        }

        if (taskId == Documents)
        {
            return At(-1 - i, 23, 0);
        }

        if (taskId == Accounting)
        {
            return Days(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).ElementAt(i).AddHours(11.5);
        }

        // Projects: Monday, Wednesday and Friday at 08:30 and 20:00.
        return Days(d => d.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Wednesday or DayOfWeek.Friday)
            .SelectMany(d => new[] { d.AddHours(20), d.AddHours(8.5) })
            .Where(t => t < Now)
            .ElementAt(i);
    }

    /// <summary>Midnights of the days that fit, from today back.</summary>
    private static IEnumerable<DateTimeOffset> Days(Func<DateTimeOffset, bool> fits)
    {
        for (int day = 0; ; day--)
        {
            var midnight = At(day, 0, 0);
            if (fits(midnight))
            {
                yield return midnight;
            }
        }
    }

    private static DateTimeOffset At(int days, int hour, int minute) =>
        new(new DateTime(2026, 11, 18, hour, minute, 0, DateTimeKind.Local).AddDays(days));
}
