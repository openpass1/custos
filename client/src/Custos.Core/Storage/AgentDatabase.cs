using System.Globalization;
using System.Text.Json;
using Custos.Core.Backup;
using Custos.Core.Json;
using Custos.Core.Model;
using Microsoft.Data.Sqlite;

namespace Custos.Core.Storage;

/// <summary>
/// The local database <c>C:\ProgramData\CUSTOS\db\custos.db</c> (SQLite, section 3.3): the state of the files of each
/// task (section 7), the run history (section 6.10) and the last schedule slot each task has dealt with.
/// Every call opens its own connection, so the database may be used from several threads.
/// </summary>
public sealed class AgentDatabase : IFileStateStore
{
    private const int SchemaVersion = 3;

    private readonly string _connectionString;

    public AgentDatabase(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 60,
        }.ToString();
        Migrate();
    }

    // ---- Change tracking (IFileStateStore) ---------------------------------------------------------------------

    public TaskBackupState GetBackupState(string taskId)
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT full_run_id, full_set_name, full_at, runs_since_full, runs_since_verify, stored_checked_at, compression_ratio FROM task_state WHERE task_id = $task");
        command.Parameters.AddWithValue("$task", taskId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return TaskBackupState.Empty;
        }

        return new TaskBackupState(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : ParseTime(reader.GetString(2)),
            reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            reader.IsDBNull(5) ? null : ParseTime(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetDouble(6));
    }

    public Dictionary<string, FileStamp> LoadFileState(string taskId, bool baseFull)
    {
        var files = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        using var connection = Open();
        using var command = Command(connection, "SELECT path, size, mtime FROM file_state WHERE task_id = $task AND base = $base");
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$base", baseFull ? 1 : 0);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            files[reader.GetString(0)] = new FileStamp(reader.GetInt64(1), reader.GetInt64(2));
        }

        return files;
    }

    public void CommitRun(string taskId, CommittedRun run, IReadOnlyCollection<KeyValuePair<string, FileStamp>> files)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(files);
        bool full = run.BackupType == BackupTypes.Full;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        using (var delete = Command(connection, full
            ? "DELETE FROM file_state WHERE task_id = $task"
            : "DELETE FROM file_state WHERE task_id = $task AND base = 0", transaction))
        {
            delete.Parameters.AddWithValue("$task", taskId);
            delete.ExecuteNonQuery();
        }

        using (var insert = Command(connection, "INSERT OR REPLACE INTO file_state (task_id, base, path, size, mtime) VALUES ($task, $base, $path, $size, $mtime)", transaction))
        {
            var task = insert.Parameters.AddWithValue("$task", taskId);
            var baseFlag = insert.Parameters.Add("$base", SqliteType.Integer);
            var path = insert.Parameters.Add("$path", SqliteType.Text);
            var size = insert.Parameters.Add("$size", SqliteType.Integer);
            var mtime = insert.Parameters.Add("$mtime", SqliteType.Integer);
            insert.Prepare();
            foreach (int flag in full ? [0, 1] : new[] { 0 })
            {
                baseFlag.Value = flag;
                foreach (var file in files)
                {
                    path.Value = file.Key;
                    size.Value = file.Value.Size;
                    mtime.Value = file.Value.ModifiedTicks;
                    insert.ExecuteNonQuery();
                }
            }
        }

        using (var state = Command(connection, full
            ? """
              INSERT INTO task_state (task_id, full_run_id, full_set_name, full_at, runs_since_full, runs_since_verify, compression_ratio)
              VALUES ($task, $run, $set, $at, 0, $verify, $ratio)
              ON CONFLICT(task_id) DO UPDATE SET full_run_id = $run, full_set_name = $set, full_at = $at, runs_since_full = 0,
                runs_since_verify = CASE WHEN $verified THEN 0 ELSE runs_since_verify + 1 END,
                compression_ratio = COALESCE($ratio, compression_ratio)
              """
            : """
              INSERT INTO task_state (task_id, runs_since_full, runs_since_verify, compression_ratio) VALUES ($task, 1, $verify, $ratio)
              ON CONFLICT(task_id) DO UPDATE SET runs_since_full = runs_since_full + 1,
                runs_since_verify = CASE WHEN $verified THEN 0 ELSE runs_since_verify + 1 END,
                compression_ratio = COALESCE($ratio, compression_ratio)
              """, transaction))
        {
            state.Parameters.AddWithValue("$task", taskId);
            state.Parameters.AddWithValue("$run", run.RunId);
            state.Parameters.AddWithValue("$set", (object?)run.SetName ?? DBNull.Value);
            state.Parameters.AddWithValue("$at", UtcTimeConverter.ToText(run.StartedAt));
            state.Parameters.AddWithValue("$verified", run.Verified ? 1 : 0);
            state.Parameters.AddWithValue("$verify", run.Verified ? 0 : 1);
            state.Parameters.AddWithValue("$ratio", run.CompressionRatio is { } ratio ? ratio : DBNull.Value);
            state.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Forgets the change tracking of a deleted task. Its history stays.</summary>
    public void ForgetTask(string taskId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (string sql in new[] { "DELETE FROM file_state WHERE task_id = $task", "DELETE FROM task_state WHERE task_id = $task" })
        {
            using var command = Command(connection, sql, transaction);
            command.Parameters.AddWithValue("$task", taskId);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void SetStoredChecked(string taskId, DateTimeOffset checkedAt)
    {
        using var connection = Open();
        using var command = Command(connection, """
            INSERT INTO task_state (task_id, stored_checked_at) VALUES ($task, $at)
            ON CONFLICT(task_id) DO UPDATE SET stored_checked_at = $at
            """);
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$at", UtcTimeConverter.ToText(checkedAt));
        command.ExecuteNonQuery();
    }

    // ---- Schedule ----------------------------------------------------------------------------------------------

    /// <summary>The latest schedule slot the task has dealt with (run, skipped or too old), or null if never set.</summary>
    public DateTimeOffset? GetLastSlot(string taskId)
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT last_slot FROM task_state WHERE task_id = $task");
        command.Parameters.AddWithValue("$task", taskId);
        return command.ExecuteScalar() is string text ? ParseTime(text) : null;
    }

    public void SetLastSlot(string taskId, DateTimeOffset slot)
    {
        using var connection = Open();
        using var command = Command(connection, """
            INSERT INTO task_state (task_id, last_slot) VALUES ($task, $slot)
            ON CONFLICT(task_id) DO UPDATE SET last_slot = $slot
            """);
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$slot", UtcTimeConverter.ToText(slot));
        command.ExecuteNonQuery();
    }

    // ---- History -----------------------------------------------------------------------------------------------

    public void SaveRun(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        using var connection = Open();
        using var command = Command(connection, """
            INSERT OR REPLACE INTO runs (run_id, task_id, task_name, backup_type, trigger, scheduled_for, requested_by,
                started_at, finished_at, status, source_files, source_bytes, files_processed, bytes_written, errors,
                warnings, error_message, set_name, destinations, databases)
            VALUES ($run, $task, $name, $type, $trigger, $scheduled, $by, $started, $finished, $status, $sfiles, $sbytes,
                $files, $bytes, $errors, $warnings, $message, $set, $destinations, $databases)
            """);
        var p = command.Parameters;
        p.AddWithValue("$run", run.RunId);
        p.AddWithValue("$task", run.TaskId);
        p.AddWithValue("$name", run.TaskName);
        p.AddWithValue("$type", run.BackupType);
        p.AddWithValue("$trigger", run.Trigger);
        p.AddWithValue("$scheduled", Nullable(run.ScheduledFor));
        p.AddWithValue("$by", (object?)run.RequestedBy ?? DBNull.Value);
        p.AddWithValue("$started", UtcTimeConverter.ToText(run.StartedAt));
        p.AddWithValue("$finished", Nullable(run.FinishedAt));
        p.AddWithValue("$status", (object?)run.Status ?? DBNull.Value);
        p.AddWithValue("$sfiles", run.SourceFiles);
        p.AddWithValue("$sbytes", run.SourceBytes);
        p.AddWithValue("$files", run.FilesProcessed);
        p.AddWithValue("$bytes", run.BytesWritten);
        p.AddWithValue("$errors", run.Errors);
        p.AddWithValue("$warnings", run.Warnings);
        p.AddWithValue("$message", (object?)run.ErrorMessage ?? DBNull.Value);
        p.AddWithValue("$set", (object?)run.SetName ?? DBNull.Value);
        p.AddWithValue("$destinations", JsonSerializer.Serialize(run.Destinations, StorageJsonContext.Default.ListDestinationResult));
        p.AddWithValue("$databases", run.Databases.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(run.Databases, StorageJsonContext.Default.ListDatabaseResult));
        command.ExecuteNonQuery();
    }

    /// <summary>Runs of a task, newest first.</summary>
    public List<RunRecord> GetHistory(string taskId, int limit)
    {
        using var connection = Open();
        using var command = Command(connection, RunColumns + " WHERE task_id = $task ORDER BY started_at DESC LIMIT $limit");
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$limit", limit);
        return ReadRuns(command);
    }

    /// <summary>The newest run of every task.</summary>
    public Dictionary<string, RunRecord> GetLastRuns() => LatestPerTask("1 = 1");

    /// <summary>The newest successful (or with warnings) run of every task.</summary>
    public Dictionary<string, RunRecord> GetLastSuccessfulRuns() => LatestPerTask("status IN ('success', 'warning')");

    /// <summary>Marks runs left without a result by a crash or power loss.</summary>
    /// <returns>How many runs were marked.</returns>
    public int CloseInterruptedRuns(string message)
    {
        using var connection = Open();
        using var command = Command(connection, "UPDATE runs SET status = 'aborted', finished_at = started_at, error_message = $message WHERE status IS NULL");
        command.Parameters.AddWithValue("$message", message);
        return command.ExecuteNonQuery();
    }

    /// <summary>Deletes history older than <paramref name="before"/>.</summary>
    public int DeleteRunsBefore(DateTimeOffset before)
    {
        using var connection = Open();
        using var command = Command(connection, "DELETE FROM runs WHERE started_at < $before");
        command.Parameters.AddWithValue("$before", UtcTimeConverter.ToText(before));
        return command.ExecuteNonQuery();
    }

    // ---- Plumbing ----------------------------------------------------------------------------------------------

    private const string RunColumns = """
        SELECT run_id, task_id, task_name, backup_type, trigger, scheduled_for, requested_by, started_at, finished_at,
            status, source_files, source_bytes, files_processed, bytes_written, errors, warnings, error_message, set_name,
            destinations, databases
        FROM runs
        """;

    private Dictionary<string, RunRecord> LatestPerTask(string condition)
    {
        using var connection = Open();
        using var command = Command(connection, RunColumns + $"""
             WHERE rowid IN (
                SELECT (SELECT r2.rowid FROM runs r2 WHERE r2.task_id = r1.task_id AND {condition} ORDER BY r2.started_at DESC LIMIT 1)
                FROM (SELECT DISTINCT task_id FROM runs) r1)
            """);
        return ReadRuns(command).ToDictionary(r => r.TaskId, StringComparer.OrdinalIgnoreCase);
    }

    private static List<RunRecord> ReadRuns(SqliteCommand command)
    {
        var runs = new List<RunRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            runs.Add(new RunRecord
            {
                RunId = reader.GetString(0),
                TaskId = reader.GetString(1),
                TaskName = reader.GetString(2),
                BackupType = reader.GetString(3),
                Trigger = reader.GetString(4),
                ScheduledFor = reader.IsDBNull(5) ? null : ParseTime(reader.GetString(5)),
                RequestedBy = reader.IsDBNull(6) ? null : reader.GetString(6),
                StartedAt = ParseTime(reader.GetString(7)),
                FinishedAt = reader.IsDBNull(8) ? null : ParseTime(reader.GetString(8)),
                Status = reader.IsDBNull(9) ? null : reader.GetString(9),
                SourceFiles = reader.GetInt64(10),
                SourceBytes = reader.GetInt64(11),
                FilesProcessed = reader.GetInt64(12),
                BytesWritten = reader.GetInt64(13),
                Errors = reader.GetInt32(14),
                Warnings = reader.GetInt32(15),
                ErrorMessage = reader.IsDBNull(16) ? null : reader.GetString(16),
                SetName = reader.IsDBNull(17) ? null : reader.GetString(17),
                Destinations = reader.IsDBNull(18) ? [] : JsonSerializer.Deserialize(reader.GetString(18), StorageJsonContext.Default.ListDestinationResult) ?? [],
                Databases = reader.IsDBNull(19) ? [] : JsonSerializer.Deserialize(reader.GetString(19), StorageJsonContext.Default.ListDatabaseResult) ?? [],
            });
        }

        return runs;
    }

    private void Migrate()
    {
        using var connection = Open();
        using (var wal = Command(connection, "PRAGMA journal_mode = WAL"))
        {
            wal.ExecuteNonQuery();
        }

        long version;
        using (var read = Command(connection, "PRAGMA user_version"))
        {
            version = (long)read.ExecuteScalar()!;
        }

        if (version >= SchemaVersion)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        if (version == 1)
        {
            // Stage 2: verification counters and the compression ratio of the last archive.
            using var alter = Command(connection, """
                ALTER TABLE task_state ADD COLUMN runs_since_verify INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE task_state ADD COLUMN stored_checked_at TEXT;
                ALTER TABLE task_state ADD COLUMN compression_ratio REAL;
                """, transaction);
            alter.ExecuteNonQuery();
        }

        if (version is 1 or 2)
        {
            // Stage 2b: the dumped databases of a run.
            using var alter = Command(connection, "ALTER TABLE runs ADD COLUMN databases TEXT;", transaction);
            alter.ExecuteNonQuery();
        }

        using (var create = Command(connection, """
            CREATE TABLE IF NOT EXISTS file_state (
                task_id TEXT NOT NULL,
                base INTEGER NOT NULL,
                path TEXT NOT NULL COLLATE NOCASE,
                size INTEGER NOT NULL,
                mtime INTEGER NOT NULL,
                PRIMARY KEY (task_id, base, path)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS task_state (
                task_id TEXT PRIMARY KEY,
                full_run_id TEXT,
                full_set_name TEXT,
                full_at TEXT,
                runs_since_full INTEGER NOT NULL DEFAULT 0,
                last_slot TEXT,
                runs_since_verify INTEGER NOT NULL DEFAULT 0,
                stored_checked_at TEXT,
                compression_ratio REAL
            );

            CREATE TABLE IF NOT EXISTS runs (
                run_id TEXT PRIMARY KEY,
                task_id TEXT NOT NULL,
                task_name TEXT NOT NULL,
                backup_type TEXT NOT NULL,
                trigger TEXT NOT NULL,
                scheduled_for TEXT,
                requested_by TEXT,
                started_at TEXT NOT NULL,
                finished_at TEXT,
                status TEXT,
                source_files INTEGER NOT NULL DEFAULT 0,
                source_bytes INTEGER NOT NULL DEFAULT 0,
                files_processed INTEGER NOT NULL DEFAULT 0,
                bytes_written INTEGER NOT NULL DEFAULT 0,
                errors INTEGER NOT NULL DEFAULT 0,
                warnings INTEGER NOT NULL DEFAULT 0,
                error_message TEXT,
                set_name TEXT,
                destinations TEXT,
                databases TEXT
            );

            CREATE INDEX IF NOT EXISTS runs_by_task ON runs (task_id, started_at);
            """, transaction))
        {
            create.ExecuteNonQuery();
        }

        using (var set = Command(connection, $"PRAGMA user_version = {SchemaVersion.ToString(CultureInfo.InvariantCulture)}", transaction))
        {
            set.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
#pragma warning disable CA2100 // Every statement is a constant of this class; values go in as parameters.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.Transaction = transaction;
        return command;
    }

    private static object Nullable(DateTimeOffset? time) => time is { } value ? UtcTimeConverter.ToText(value) : DBNull.Value;

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
}
