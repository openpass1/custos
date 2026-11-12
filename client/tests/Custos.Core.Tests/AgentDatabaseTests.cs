using Custos.Core.Backup;
using Custos.Core.Model;
using Custos.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Custos.Core.Tests;

public sealed class AgentDatabaseTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly TempFolder _temp = new();
    private readonly AgentDatabase _database;

    public AgentDatabaseTests()
    {
        _database = new AgentDatabase(_temp.Combine("db", "custos.db"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    [Fact]
    public void A_full_run_becomes_the_base_and_later_runs_keep_it()
    {
        _database.CommitRun("task", new CommittedRun("run-1", BackupTypes.Full, "Set 1", Start), [new(@"D:\a.txt", new FileStamp(1, 10)), new(@"D:\b.txt", new FileStamp(2, 20))]);
        _database.CommitRun("task", new CommittedRun("run-2", BackupTypes.Incremental, "Set 2", Start.AddDays(1)), [new(@"D:\a.txt", new FileStamp(3, 30))]);

        var state = _database.GetBackupState("task");
        Assert.Equal("run-1", state.FullRunId);
        Assert.Equal("Set 1", state.FullSetName);
        Assert.Equal(Start, state.FullAt);
        Assert.Equal(1, state.RunsSinceFull);

        var last = _database.LoadFileState("task", baseFull: false);
        Assert.Equal(new FileStamp(3, 30), Assert.Single(last).Value);
        Assert.True(last.ContainsKey(@"d:\A.TXT"));

        var full = _database.LoadFileState("task", baseFull: true);
        Assert.Equal(2, full.Count);
        Assert.Equal(new FileStamp(2, 20), full[@"D:\b.txt"]);
    }

    [Fact]
    public void Forgetting_a_task_clears_its_state()
    {
        _database.CommitRun("task", new CommittedRun("run-1", BackupTypes.Full, "Set 1", Start), [new(@"D:\a.txt", new FileStamp(1, 10))]);
        _database.SetLastSlot("task", Start);

        _database.ForgetTask("task");

        Assert.Equal(TaskBackupState.Empty, _database.GetBackupState("task"));
        Assert.Empty(_database.LoadFileState("task", baseFull: true));
        Assert.Null(_database.GetLastSlot("task"));
    }

    [Fact]
    public void The_last_slot_is_kept_apart_from_the_backup_state()
    {
        _database.SetLastSlot("task", Start);
        _database.CommitRun("task", new CommittedRun("run-1", BackupTypes.Full, "Set 1", Start), []);
        _database.SetLastSlot("task", Start.AddHours(1));

        Assert.Equal(Start.AddHours(1), _database.GetLastSlot("task"));
        Assert.Equal("run-1", _database.GetBackupState("task").FullRunId);
    }

    [Fact]
    public void History_keeps_runs_newest_first()
    {
        _database.SaveRun(Record("run-1", "task", Start, RunStatuses.Success));
        _database.SaveRun(Record("run-2", "task", Start.AddDays(1), RunStatuses.Error));
        _database.SaveRun(Record("run-3", "other", Start, RunStatuses.Warning));

        var history = _database.GetHistory("task", 10);
        Assert.Equal(["run-2", "run-1"], history.Select(r => r.RunId));
        var destination = Assert.Single(history[0].Destinations);
        Assert.Equal("dest", destination.DestinationId);
        Assert.Equal(RunStatuses.Error, destination.Status);

        Assert.Equal("run-2", _database.GetLastRuns()["task"].RunId);
        Assert.Equal("run-1", _database.GetLastSuccessfulRuns()["task"].RunId);
        Assert.Equal("run-3", _database.GetLastSuccessfulRuns()["other"].RunId);
    }

    [Fact]
    public void Runs_left_open_by_a_crash_are_closed_as_aborted()
    {
        _database.SaveRun(Record("run-1", "task", Start, null));

        Assert.Equal(1, _database.CloseInterruptedRuns("power loss"));

        var run = Assert.Single(_database.GetHistory("task", 10));
        Assert.Equal(RunStatuses.Aborted, run.Status);
        Assert.Equal("power loss", run.ErrorMessage);
        Assert.Equal(0, _database.CloseInterruptedRuns("again"));
    }

    [Fact]
    public void Old_history_is_deleted()
    {
        _database.SaveRun(Record("run-1", "task", Start, RunStatuses.Success));
        _database.SaveRun(Record("run-2", "task", Start.AddDays(40), RunStatuses.Success));

        Assert.Equal(1, _database.DeleteRunsBefore(Start.AddDays(30)));
        Assert.Equal(["run-2"], _database.GetHistory("task", 10).Select(r => r.RunId));
    }

    [Fact]
    public void Opening_an_existing_database_keeps_its_data()
    {
        _database.SetLastSlot("task", Start);

        var again = new AgentDatabase(_temp.Combine("db", "custos.db"));

        Assert.Equal(Start, again.GetLastSlot("task"));
    }

    [Fact]
    public void A_run_keeps_its_database_dumps()
    {
        var record = Record("run-1", "task", Start, RunStatuses.Error);
        record.Databases = [new DatabaseResult { Engine = DbEngines.MySql, Name = "shop", Bytes = 2048, Ok = true }, new DatabaseResult { Engine = DbEngines.MySql, Name = "blog", Ok = false, Error = "cut off" }];
        _database.SaveRun(record);

        var run = Assert.Single(_database.GetHistory("task", 10));

        Assert.Equal(["shop", "blog"], run.Databases.Select(d => d.Name));
        Assert.Equal(2048, run.Databases[0].Bytes);
        Assert.False(run.Databases[1].Ok);
        Assert.Equal("cut off", run.Databases[1].Error);
    }

    [Fact]
    public void A_database_of_stage_2_gets_the_column_of_the_dumps()
    {
        string path = _temp.Combine("old", "custos.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE runs (run_id TEXT PRIMARY KEY, task_id TEXT NOT NULL, task_name TEXT NOT NULL, backup_type TEXT NOT NULL,
                    trigger TEXT NOT NULL, scheduled_for TEXT, requested_by TEXT, started_at TEXT NOT NULL, finished_at TEXT, status TEXT,
                    source_files INTEGER NOT NULL DEFAULT 0, source_bytes INTEGER NOT NULL DEFAULT 0, files_processed INTEGER NOT NULL DEFAULT 0,
                    bytes_written INTEGER NOT NULL DEFAULT 0, errors INTEGER NOT NULL DEFAULT 0, warnings INTEGER NOT NULL DEFAULT 0,
                    error_message TEXT, set_name TEXT, destinations TEXT);
                INSERT INTO runs (run_id, task_id, task_name, backup_type, trigger, started_at, status)
                    VALUES ('old-run', 'task', 'Docs', 'full', 'manual', '2026-10-01T10:00:00.0000000+00:00', 'success');
                PRAGMA user_version = 2;
                """;
            create.ExecuteNonQuery();
        }

        var database = new AgentDatabase(path);
        var record = Record("new-run", "task", Start, RunStatuses.Success);
        record.Databases = [new DatabaseResult { Engine = DbEngines.PostgreSql, Name = "crm", Bytes = 10, Ok = true }];
        database.SaveRun(record);

        var history = database.GetHistory("task", 10);
        Assert.Equal(["new-run", "old-run"], history.Select(r => r.RunId));
        Assert.Equal("crm", Assert.Single(history[0].Databases).Name);
        Assert.Empty(history[1].Databases);
    }

    private static RunRecord Record(string runId, string taskId, DateTimeOffset startedAt, string? status) => new()
    {
        RunId = runId,
        TaskId = taskId,
        TaskName = "Docs",
        BackupType = BackupTypes.Full,
        Trigger = RunTriggers.Schedule,
        ScheduledFor = startedAt,
        StartedAt = startedAt,
        FinishedAt = status is null ? null : startedAt.AddMinutes(5),
        Status = status,
        FilesProcessed = 10,
        BytesWritten = 1000,
        SetName = "Set",
        Destinations = [new DestinationResult { DestinationId = "dest", Status = status == RunStatuses.Error ? RunStatuses.Error : RunStatuses.Success }],
    };
}
