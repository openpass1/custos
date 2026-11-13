using System.Globalization;
using Custos.Core.Agent;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Text;

namespace Custos.Core.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly TempFolder _temp = new();
    private readonly ConfigStore _store;

    public ConfigStoreTests()
    {
        _store = new ConfigStore(_temp.Combine("config"), new ReversingProtector());
        _store.Load();
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_new_task_gets_revision_1_and_survives_a_reload()
    {
        var saved = _store.SaveTask(new TaskDefinition { Name = " Docs " }, null, @"local:OFFICE\admin", Now).Saved;

        var reloaded = new ConfigStore(_temp.Combine("config"), new ReversingProtector());
        reloaded.Load();
        var task = Assert.Single(reloaded.GetTasks());
        Assert.Equal("Docs", task.Name);
        Assert.Equal(1, task.Revision);
        Assert.Equal(@"local:OFFICE\admin", task.UpdatedBy);
        Assert.Equal(Now, task.UpdatedAt);
        Assert.Equal(saved.Id, task.Id);
        Assert.Equal(1, reloaded.ConfigRevision);
        Assert.Empty(reloaded.Problems);
    }

    [Fact]
    public void Saving_over_a_newer_revision_is_a_conflict()
    {
        var task = _store.SaveTask(new TaskDefinition { Name = "Docs" }, null, "local:a", Now).Saved;
        _store.SaveTask(task, 1, "local:b", Now);

        var error = Assert.Throws<ConfigException>(() => _store.SaveTask(task, 1, "local:a", Now));

        Assert.True(error.IsConflict);
        Assert.Equal(2, _store.GetTask(task.Id)!.Revision);
    }

    [Fact]
    public void Schedule_changes_and_enabling_reset_the_slots()
    {
        var created = _store.SaveTask(new TaskDefinition { Name = "Docs", Enabled = false }, null, "local:a", Now);
        Assert.True(created.ScheduleReset);

        var task = created.Saved;
        task.Comment = "only a comment";
        Assert.False(_store.SaveTask(task, 1, "local:a", Now).ScheduleReset);

        task.Enabled = true;
        Assert.True(_store.SaveTask(task, 2, "local:a", Now).ScheduleReset);

        task.Schedule.Times = ["03:00"];
        Assert.True(_store.SaveTask(task, 3, "local:a", Now).ScheduleReset);
    }

    [Fact]
    public void Invalid_tasks_are_refused()
    {
        Assert.Throws<ConfigException>(() => _store.SaveTask(new TaskDefinition { Name = "  " }, null, "local:a", Now));
        Assert.Throws<ConfigException>(() => _store.SaveTask(new TaskDefinition { Name = "A", Schedule = new ScheduleOptions { Times = ["25:00"] } }, null, "local:a", Now));
        Assert.Throws<ConfigException>(() => _store.SaveTask(new TaskDefinition { Name = "A", Destinations = [new TaskDestination { Path = @"\\nas\x", CredentialId = "nope" }] }, null, "local:a", Now));
        Assert.Empty(_store.GetTasks());
    }

    [Fact]
    public void Passwords_are_stored_protected_and_never_returned()
    {
        var credential = _store.SaveCredential(new Credential { User = "backup-user", Domain = "EXAMPLE" }, "S3cret!");

        Assert.True(credential.HasSecret);
        Assert.Equal("S3cret!", _store.GetPassword(credential.Id));
        string file = File.ReadAllText(_temp.Combine("config", ConfigStore.CredentialsFileName));
        Assert.DoesNotContain("S3cret!", file, StringComparison.Ordinal);

        credential.User = "backup-user2";
        _store.SaveCredential(credential, password: null);
        Assert.Equal("S3cret!", _store.GetPassword(credential.Id));
        Assert.Equal(@"EXAMPLE\backup-user2", _store.GetCredential(credential.Id)!.DisplayName);
    }

    [Fact]
    public void Database_servers_are_checked_and_their_accounts_count_as_used()
    {
        var account = _store.SaveCredential(new Credential { Kind = CredentialKinds.Db, User = "root" }, string.Empty);
        Assert.False(account.HasSecret);
        TaskDefinition Task(TaskDatabaseServer server) => new() { Name = "Sites", DbServers = [server] };

        Assert.Throws<ConfigException>(() => _store.SaveTask(Task(new TaskDatabaseServer { Engine = "oracle", BinDir = @"C:\bin" }), null, "local:a", Now));
        Assert.Throws<ConfigException>(() => _store.SaveTask(Task(new TaskDatabaseServer { BinDir = " " }), null, "local:a", Now));
        Assert.Throws<ConfigException>(() => _store.SaveTask(Task(new TaskDatabaseServer { BinDir = @"C:\bin", Port = 70000 }), null, "local:a", Now));
        Assert.Throws<ConfigException>(() => _store.SaveTask(Task(new TaskDatabaseServer { BinDir = @"C:\bin", AllDatabases = false }), null, "local:a", Now));
        Assert.Throws<ConfigException>(() => _store.SaveTask(Task(new TaskDatabaseServer { BinDir = @"C:\bin", CredentialId = "nope" }), null, "local:a", Now));

        var saved = _store.SaveTask(Task(new TaskDatabaseServer { Engine = " MariaDB ", BinDir = @"C:\OSPanel\modules\MariaDB-11.4\bin", Host = "", CredentialId = account.Id }), null, "local:a", Now);
        var server = Assert.Single(saved.Saved.DbServers);
        Assert.Equal(DbEngines.MariaDb, server.Engine);
        Assert.Equal("127.0.0.1", server.Host);
        Assert.Throws<ConfigException>(() => _store.DeleteCredential(account.Id));
    }

    [Fact]
    public void The_database_choice_of_a_linux_server_is_checked()
    {
        var key = _store.CreateSshKey("custos", "custos@test");
        TaskDefinition Task(RemoteDatabaseChoice choice) => new()
        {
            Name = "Server",
            Sources =
            [
                new TaskSource
                {
                    Kind = SourceKinds.Ssh,
                    Host = "vps.example.org",
                    User = "custos",
                    CredentialId = key.Id,
                    Helper = Custos.Core.Remote.HelperSetup.Preset(null),
                    Databases = choice,
                },
            ],
        };

        Assert.Throws<ConfigException>(() => _store.SaveTask(Task(new RemoteDatabaseChoice { AllDatabases = false }), null, "local:a", Now));
        Assert.Throws<ConfigException>(() => _store.SaveTask(Task(new RemoteDatabaseChoice { Exclude = ["oracle/shop"] }), null, "local:a", Now));
        Assert.Throws<ConfigException>(() => _store.SaveTask(Task(new RemoteDatabaseChoice { Exclude = ["mariadb/a b"] }), null, "local:a", Now));
        Assert.Throws<ConfigException>(() => _store.SaveTask(Task(new RemoteDatabaseChoice { Exclude = ["shop"] }), null, "local:a", Now));

        var saved = _store.SaveTask(Task(new RemoteDatabaseChoice { AllDatabases = false, Include = ["mariadb/shop", "", "mariadb/shop"] }), null, "local:a", Now);
        Assert.Equal(["mariadb/shop"], saved.Saved.Sources[0].Databases!.Include);
    }

    [Fact]
    public void An_account_in_use_cannot_be_deleted()
    {
        var credential = _store.SaveCredential(new Credential { User = "backup-user" }, "x");
        var task = new TaskDefinition { Name = "Docs", Destinations = [new TaskDestination { Kind = DestinationKinds.Unc, Path = @"\\nas\backup", CredentialId = credential.Id }] };
        _store.SaveTask(task, null, "local:a", Now);

        var error = Assert.Throws<ConfigException>(() => _store.DeleteCredential(credential.Id));
        Assert.Contains("Docs", error.Message, StringComparison.Ordinal);

        _store.DeleteTask(task.Id);
        Assert.True(_store.DeleteCredential(credential.Id));
        Assert.Empty(_store.GetCredentials());
    }

    [Fact]
    public void Import_adds_accounts_before_the_tasks_that_use_them()
    {
        var credential = new Credential { User = "backup-user", Domain = "." };
        var task = new TaskDefinition { Name = "From Cobian", Enabled = false, Sources = [new TaskSource { Path = @"\\nas\docs", CredentialId = credential.Id }] };

        var changes = _store.Import([task], [(credential, "pw")], "local:a", Now);

        Assert.Single(changes);
        Assert.Equal("pw", _store.GetPassword(credential.Id));
        Assert.Equal(credential.Id, _store.GetTasks()[0].Sources[0].CredentialId);
    }

    [Fact]
    public void A_damaged_file_is_set_aside_and_its_backup_used()
    {
        _store.SaveTask(new TaskDefinition { Name = "First" }, null, "local:a", Now);
        _store.SaveTask(new TaskDefinition { Name = "Second" }, null, "local:a", Now);
        File.WriteAllText(_temp.Combine("config", ConfigStore.TasksFileName), "{ broken");

        var reloaded = new ConfigStore(_temp.Combine("config"), new ReversingProtector());
        reloaded.Load();

        Assert.Equal(["First"], reloaded.GetTasks().Select(t => t.Name));
        Assert.Single(reloaded.Problems);
        Assert.Single(Directory.GetFiles(_temp.Combine("config"), ConfigStore.TasksFileName + ".damaged-*"));
    }

    [Fact]
    public void Settings_are_checked_and_kept()
    {
        Assert.Throws<ConfigException>(() => _store.SaveSettings(new AgentSettings { LogRetentionDays = 0 }));

        _store.SaveSettings(new AgentSettings { LogRetentionDays = 30, DefaultLogLevel = LogLevels.Files });

        var reloaded = new ConfigStore(_temp.Combine("config"), new ReversingProtector());
        reloaded.Load();
        Assert.Equal(30, reloaded.GetSettings().LogRetentionDays);
        Assert.Equal(LogLevels.Files, reloaded.GetSettings().DefaultLogLevel);
    }

    /// <summary>Not encryption, but enough to see that the stored text is not the password.</summary>
    private sealed class ReversingProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => [.. data.Reverse()];

        public byte[] Unprotect(byte[] data) => [.. data.Reverse()];
    }
}

public sealed class PendingRunsTests
{
    [Fact]
    public void Higher_priority_goes_first_then_the_order_queued()
    {
        var queue = new PendingRuns();
        var priorities = new Dictionary<string, int> { ["a"] = 0, ["b"] = 5, ["c"] = 0 };
        queue.TryEnqueue(Run("a"));
        queue.TryEnqueue(Run("b"));
        queue.TryEnqueue(Run("c"));

        Assert.Equal(["b", "a", "c"], queue.Snapshot(Priority(priorities)).Select(r => r.TaskId));
        Assert.Equal("b", queue.TryDequeue(Priority(priorities))!.TaskId);
        Assert.Equal("a", queue.TryDequeue(Priority(priorities))!.TaskId);
    }

    [Fact]
    public void A_task_is_queued_only_once_and_deleted_tasks_are_dropped()
    {
        var queue = new PendingRuns();
        Assert.True(queue.TryEnqueue(Run("a")));
        Assert.False(queue.TryEnqueue(Run("A")));
        queue.TryEnqueue(Run("gone"));

        Assert.Equal("a", queue.TryDequeue(Priority(new() { ["a"] = 0 }))!.TaskId);
        Assert.Null(queue.TryDequeue(Priority(new() { ["a"] = 0 })));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void Clearing_empties_the_queue_and_returns_what_was_in_it()
    {
        var queue = new PendingRuns();
        queue.TryEnqueue(Run("a"));
        queue.TryEnqueue(Run("b"));

        Assert.Equal(["a", "b"], queue.Clear().Select(r => r.TaskId));
        Assert.Equal(0, queue.Count);
        Assert.Empty(queue.Clear());
    }

    private static QueuedRun Run(string taskId) => new() { TaskId = taskId, Trigger = RunTriggers.Manual };

    private static Func<string, int?> Priority(Dictionary<string, int> priorities) =>
        id => priorities.TryGetValue(id, out int p) ? p : null;
}

public sealed class SlotPlannerTests
{
    private static readonly ScheduleOptions Nightly = new() { Type = ScheduleTypes.Daily, Times = ["02:00"] };
    private static readonly DateTimeOffset Slot = new(2026, 11, 4, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_slot_is_due_at_its_time()
    {
        var check = SlotPlanner.Check(Nightly, Slot.AddHours(-1), Slot.AddSeconds(10), TimeZoneInfo.Utc);

        Assert.Equal(new SlotCheck(SlotDecision.Run, Slot), check);
    }

    [Fact]
    public void A_slot_missed_while_off_is_caught_up_once()
    {
        // Off from Monday evening to Thursday morning: three slots missed, one catch-up run for the latest.
        var check = SlotPlanner.Check(Nightly, Slot.AddDays(-3).AddHours(-2), Slot.AddHours(6), TimeZoneInfo.Utc);

        Assert.Equal(new SlotCheck(SlotDecision.CatchUp, Slot), check);
    }

    [Fact]
    public void Without_catch_up_or_after_a_week_the_slot_is_missed()
    {
        var noCatchUp = new ScheduleOptions { Type = ScheduleTypes.Daily, Times = ["02:00"], CatchUp = false };

        Assert.Equal(SlotDecision.Missed, SlotPlanner.Check(noCatchUp, Slot.AddDays(-1), Slot.AddHours(1), TimeZoneInfo.Utc).Decision);
        // Monthly on the 1st, the computer was off from 30 October to 12 November: the slot is 11 days old.
        var monthly = new ScheduleOptions { Type = ScheduleTypes.Monthly, Times = ["02:00"], DaysOfMonth = [1] };
        var check = SlotPlanner.Check(monthly, Slot.AddDays(-5), Slot.AddDays(8), TimeZoneInfo.Utc);
        Assert.Equal(new SlotCheck(SlotDecision.Missed, new DateTimeOffset(2026, 11, 1, 2, 0, 0, TimeSpan.Zero)), check);
    }

    [Fact]
    public void Nothing_is_due_without_a_last_slot_or_for_manual_tasks()
    {
        Assert.Equal(SlotDecision.None, SlotPlanner.Check(Nightly, null, Slot, TimeZoneInfo.Utc).Decision);
        Assert.Equal(SlotDecision.None, SlotPlanner.Check(new ScheduleOptions { Type = ScheduleTypes.Manual }, Slot.AddDays(-1), Slot, TimeZoneInfo.Utc).Decision);
        Assert.Equal(SlotDecision.None, SlotPlanner.Check(Nightly, Slot, Slot.AddHours(1), TimeZoneInfo.Utc).Decision);
    }

    [Fact]
    public void The_next_run_is_after_now_and_after_the_last_slot()
    {
        var task = new TaskDefinition { Schedule = Nightly };

        Assert.Equal(Slot.AddDays(1), SlotPlanner.NextRun(task, Slot, Slot.AddMinutes(1), TimeZoneInfo.Utc));
        Assert.Equal(Slot, SlotPlanner.NextRun(task, Slot.AddDays(-1), Slot.AddHours(-1), TimeZoneInfo.Utc));
        task.Enabled = false;
        Assert.Null(SlotPlanner.NextRun(task, Slot, Slot, TimeZoneInfo.Utc));
    }
}

/// <summary>
/// Tests that switch <see cref="Messages.Culture"/>, which every test shares: they run alone, after the others, so a
/// test running beside them never writes its log in the other language.
/// </summary>
[CollectionDefinition(nameof(MessagesCultureTests), DisableParallelization = true)]
public sealed class MessagesCultureTests;

[Collection(nameof(MessagesCultureTests))]
public sealed class TaskDescriberTests : IDisposable
{
    private readonly CultureInfo? _previous = Messages.Culture;

    public void Dispose() => Messages.Culture = _previous;

    [Fact]
    public void The_example_of_the_design_document_reads_the_same_in_russian()
    {
        Messages.Culture = CultureInfo.GetCultureInfo("ru-RU");
        var task = Example();

        Assert.Equal(
            "Каждый день в 00:00 копирует изменившиеся файлы из D:\\Data в \\\\nas01\\backup и на USB-диск «BACKUP-USB». " +
            "Каждый 7-й запуск — полная копия. Хранятся 12 последних полных копий вместе с их инкрементальными.",
            TaskDescriber.Describe(task));
    }

    [Fact]
    public void English_descriptions_are_complete_sentences()
    {
        Messages.Culture = CultureInfo.GetCultureInfo("en-US");
        var task = Example();
        task.Retention.KeepFull = 1;

        Assert.Equal(
            "Every day at 00:00, copies the files changed since the last backup from D:\\Data to \\\\nas01\\backup and to the USB disk «BACKUP-USB». " +
            "Every 7th run is a full backup. Only the last full backup is kept, with its incremental backups.",
            TaskDescriber.Describe(task));
    }

    [Fact]
    public void A_linux_server_is_named_with_its_databases()
    {
        Messages.Culture = CultureInfo.GetCultureInfo("ru-RU");
        var task = Example();
        task.Sources = [new TaskSource { Kind = SourceKinds.Ssh, Host = "vps.example.org", Path = "custos@vps.example.org:22", Helper = new RemoteHelperSettings { Roots = ["/var/www"], DbEngines = [DbEngines.MariaDb] } }];

        Assert.StartsWith(
            "Каждый день в 00:00 копирует изменившиеся файлы с Linux-сервера vps.example.org в \\\\nas01\\backup и на USB-диск «BACKUP-USB». " +
            "Каждый запуск также выгружает целиком базы MariaDB на сервере в папку «db».",
            TaskDescriber.Describe(task),
            StringComparison.Ordinal);

        task.Sources[0].Databases = new RemoteDatabaseChoice { Exclude = ["mariadb/old_db"] };
        Assert.Contains("Каждый запуск также выгружает целиком базы MariaDB на сервере в папку «db», кроме: old_db.", TaskDescriber.Describe(task), StringComparison.Ordinal);
        task.Sources[0].Databases = new RemoteDatabaseChoice { AllDatabases = false, Include = ["mariadb/shop", "postgresql/blog"] };
        Assert.Contains("Каждый запуск также выгружает целиком в папку «db» эти базы MariaDB на сервере: MariaDB: shop, PostgreSQL: blog.", TaskDescriber.Describe(task), StringComparison.Ordinal);
    }

    [Fact]
    public void Events_and_the_action_after_are_mentioned()
    {
        Messages.Culture = CultureInfo.GetCultureInfo("ru-RU");
        var task = Example();
        task.Events.Pre.Add(new TaskEvent { Command = "dump.cmd" });
        task.Events.Post.Add(new TaskEvent { Command = "notify.cmd" });
        task.Events.After = AfterActions.Shutdown;

        Assert.EndsWith(" События: до копирования — 1, после — 1. После выполнения: выключение компьютера.", TaskDescriber.Describe(task), StringComparison.Ordinal);
    }

    [Fact]
    public void The_card_says_when_a_full_backup_is_made()
    {
        Messages.Culture = CultureInfo.GetCultureInfo("ru-RU");
        var task = Example();
        task.ForceFull = new ForceFullOptions { EveryRuns = 7, Weekday = 7, MonthDay = -1 };
        Assert.Equal("Каждый 7-й запуск, по воскресеньям, в последний день месяца", TaskDescriber.FullBackups(task));

        task.ForceFull = new ForceFullOptions { MonthDay = 15 };
        Assert.Equal("15-го числа месяца", TaskDescriber.FullBackups(task));

        // Without a forced full backup the chain never ends: the card says so.
        task.ForceFull = new ForceFullOptions();
        Assert.Equal("Только первая, и когда в назначении нет полной", TaskDescriber.FullBackups(task));

        Messages.Culture = CultureInfo.GetCultureInfo("en-US");
        task.ForceFull = new ForceFullOptions { EveryRuns = 2, Weekday = 1 };
        Assert.Equal("Every 2nd run, on Mondays", TaskDescriber.FullBackups(task));

        task.ForceFull = new ForceFullOptions();
        task.Retention.Mode = RetentionModes.Gfs;
        Assert.Equal("The first run of every week (grandfather-father-son)", TaskDescriber.FullBackups(task));

        task.BackupType = BackupTypes.Full;
        Assert.Null(TaskDescriber.FullBackups(task));
    }

    [Theory]
    [InlineData(ScheduleTypes.Daily, new[] { 1, 2, 3, 4, 5 }, new int[0], 0, "по будням в 02:00 и 14:00")]
    [InlineData(ScheduleTypes.Weekly, new[] { 1, 5 }, new int[0], 0, "по пн и пт в 02:00 и 14:00")]
    [InlineData(ScheduleTypes.Monthly, new int[0], new[] { 1, -1 }, 0, "каждый месяц 1-го и в последний день в 02:00 и 14:00")]
    [InlineData(ScheduleTypes.Interval, new[] { 6, 7 }, new int[0], 30, "каждые 30 мин по выходным")]
    [InlineData(ScheduleTypes.Manual, new int[0], new int[0], 0, "по запросу")]
    public void Schedules_are_described(string type, int[] weekdays, int[] monthDays, int interval, string expected)
    {
        Messages.Culture = CultureInfo.GetCultureInfo("ru-RU");
        var schedule = new ScheduleOptions
        {
            Type = type,
            Times = ["14:00", "02:00"],
            DaysOfWeek = [.. weekdays],
            DaysOfMonth = [.. monthDays],
            IntervalMinutes = interval,
        };

        Assert.Equal(expected, TaskDescriber.Schedule(schedule));
    }

    private static TaskDefinition Example() => new()
    {
        Name = "Документы офиса",
        BackupType = BackupTypes.Incremental,
        ForceFull = new ForceFullOptions { EveryRuns = 7 },
        Sources = [new TaskSource { Path = @"D:\Data" }],
        Destinations =
        [
            new TaskDestination { Kind = DestinationKinds.Unc, Path = @"\\nas01\backup" },
            new TaskDestination { Kind = DestinationKinds.Usb, UsbLabel = "BACKUP-USB", Path = "Copies" },
        ],
        Schedule = new ScheduleOptions { Type = ScheduleTypes.Daily, Times = ["00:00"] },
        Retention = new RetentionOptions { Mode = RetentionModes.Count, KeepFull = 12 },
    };
}

public sealed class LiveLogTests
{
    [Fact]
    public void Lines_are_numbered_and_only_the_latest_are_kept()
    {
        var log = new LiveLog(capacity: 3);
        var seen = new List<long>();
        log.Added += line => seen.Add(line.Seq);

        for (int i = 0; i < 5; i++)
        {
            log.Add(LogLineLevels.Info, "line " + i);
        }

        Assert.Equal([1, 2, 3, 4, 5], seen);
        Assert.Equal(["line 2", "line 3", "line 4"], log.Recent().Select(l => l.Text));
    }
}
