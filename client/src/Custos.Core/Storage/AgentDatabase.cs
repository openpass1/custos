using System.Globalization;
using System.Text.Json;
using Custos.Core.Backup;
using Custos.Core.Json;
using Custos.Core.Model;
using Microsoft.Data.Sqlite;

namespace Custos.Core.Storage;

/// <summary>
/// The local database <c>C:\ProgramData\CUSTOS\db\custos.db</c> (SQLite, section 3.3): the state of the files of each
/// task (section 7), the run history (section 6.10), the last schedule slot each task has dealt with, the reports
/// waiting for the monitoring server (A.5) and when each stored set was last verified (A.8).
/// Every call opens its own connection, so the database may be used from several threads.
/// </summary>
public sealed class AgentDatabase : IFileStateStore
{
    private const int SchemaVersion = 5;

    /// <summary>Most reports kept for the monitoring server; the oldest go first (A.11).</summary>
    public const int MaxReports = 10_000;

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
        using var command = Command(connection, "SELECT full_run_id, full_set_name, full_at, runs_since_full, runs_since_verify, stored_checked_at, compression_ratio, last_run_id FROM task_state WHERE task_id = $task");
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
            reader.IsDBNull(6) ? null : reader.GetDouble(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));
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
              INSERT INTO task_state (task_id, full_run_id, full_set_name, full_at, runs_since_full, runs_since_verify, compression_ratio, last_run_id)
              VALUES ($task, $run, $set, $at, 0, $verify, $ratio, $run)
              ON CONFLICT(task_id) DO UPDATE SET full_run_id = $run, full_set_name = $set, full_at = $at, runs_since_full = 0,
                runs_since_verify = CASE WHEN $verified THEN 0 ELSE runs_since_verify + 1 END,
                compression_ratio = COALESCE($ratio, compression_ratio), last_run_id = $run
              """
            : """
              INSERT INTO task_state (task_id, runs_since_full, runs_since_verify, compression_ratio, last_run_id) VALUES ($task, 1, $verify, $ratio, $run)
              ON CONFLICT(task_id) DO UPDATE SET runs_since_full = runs_since_full + 1,
                runs_since_verify = CASE WHEN $verified THEN 0 ELSE runs_since_verify + 1 END,
                compression_ratio = COALESCE($ratio, compression_ratio), last_run_id = $run
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
        foreach (string sql in new[] { "DELETE FROM file_state WHERE task_id = $task", "DELETE FROM task_state WHERE task_id = $task", "DELETE FROM set_checks WHERE task_id = $task" })
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
                warnings, error_message, set_name, destinations, databases, remote, verify)
            VALUES ($run, $task, $name, $type, $trigger, $scheduled, $by, $started, $finished, $status, $sfiles, $sbytes,
                $files, $bytes, $errors, $warnings, $message, $set, $destinations, $databases, $remote, $verify)
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
        p.AddWithValue("$remote", run.Remote is null ? DBNull.Value : JsonSerializer.Serialize(run.Remote, StorageJsonContext.Default.RemoteResult));
        p.AddWithValue("$verify", run.Verify is null ? DBNull.Value : JsonSerializer.Serialize(run.Verify, StorageJsonContext.Default.RunVerify));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Runs of a task, newest first: those started before <paramref name="before"/> when it is given (the next page),
    /// and only those with warnings, errors or stopped when <paramref name="problemsOnly"/> is set.
    /// </summary>
    public List<RunRecord> GetHistory(string taskId, int limit, DateTimeOffset? before = null, bool problemsOnly = false)
    {
        using var connection = Open();
        string where = " WHERE task_id = $task"
            + (before is null ? string.Empty : " AND started_at < $before")
            + (problemsOnly ? " AND status IN ('warning', 'error', 'aborted')" : string.Empty);
        using var command = Command(connection, RunColumns + where + " ORDER BY started_at DESC LIMIT $limit");
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$limit", limit);
        if (before is { } time)
        {
            command.Parameters.AddWithValue("$before", UtcTimeConverter.ToText(time));
        }

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

    // ---- Reports for the monitoring server (A.5, A.11) ------------------------------------------------------------

    /// <summary>
    /// Queues a report. With <paramref name="key"/>, an older report with the same key that has not been sent is
    /// replaced (the inventory: only the newest one matters).
    /// </summary>
    public void AddReport(string path, byte[] body, string? key = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(body);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (key is not null)
        {
            using var delete = Command(connection, "DELETE FROM outbox WHERE coalesce_key = $key", transaction);
            delete.Parameters.AddWithValue("$key", key);
            delete.ExecuteNonQuery();
        }

        using (var insert = Command(connection, "INSERT INTO outbox (path, body, coalesce_key, created_at) VALUES ($path, $body, $key, $at)", transaction))
        {
            insert.Parameters.AddWithValue("$path", path);
            insert.Parameters.AddWithValue("$body", body);
            insert.Parameters.AddWithValue("$key", (object?)key ?? DBNull.Value);
            insert.Parameters.AddWithValue("$at", UtcTimeConverter.ToText(DateTimeOffset.UtcNow));
            insert.ExecuteNonQuery();
        }

        using (var trim = Command(connection, "DELETE FROM outbox WHERE seq <= (SELECT MAX(seq) FROM outbox) - $max", transaction))
        {
            trim.Parameters.AddWithValue("$max", MaxReports);
            trim.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>The oldest reports, in the order they were queued.</summary>
    public List<QueuedReport> GetReports(int limit)
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT seq, path, body, created_at FROM outbox ORDER BY seq LIMIT $limit");
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var reports = new List<QueuedReport>();
        while (reader.Read())
        {
            reports.Add(new QueuedReport(reader.GetInt64(0), reader.GetString(1), (byte[])reader.GetValue(2), ParseTime(reader.GetString(3))));
        }

        return reports;
    }

    public void DeleteReport(long seq)
    {
        using var connection = Open();
        using var command = Command(connection, "DELETE FROM outbox WHERE seq = $seq");
        command.Parameters.AddWithValue("$seq", seq);
        command.ExecuteNonQuery();
    }

    /// <summary>Puts another body in place of a report (the same report without its log, A.11).</summary>
    public void ReplaceReport(long seq, byte[] body)
    {
        using var connection = Open();
        using var command = Command(connection, "UPDATE outbox SET body = $body WHERE seq = $seq");
        command.Parameters.AddWithValue("$seq", seq);
        command.Parameters.AddWithValue("$body", body);
        command.ExecuteNonQuery();
    }

    public int CountReports()
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT COUNT(*) FROM outbox");
        return (int)(long)command.ExecuteScalar()!;
    }

    /// <summary>Drops every queued report: the binding to the server was reset.</summary>
    public void ClearReports()
    {
        using var connection = Open();
        using var command = Command(connection, "DELETE FROM outbox");
        command.ExecuteNonQuery();
    }

    // ---- Verification of stored sets (A.8) ---------------------------------------------------------------------

    public void SaveSetChecks(string taskId, IEnumerable<SetCheck> checks, DateTimeOffset checkedAt)
    {
        ArgumentNullException.ThrowIfNull(checks);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = Command(connection, """
            INSERT INTO set_checks (task_id, destination_id, set_name, checked_at, ok) VALUES ($task, $destination, $set, $at, $ok)
            ON CONFLICT(task_id, destination_id, set_name) DO UPDATE SET checked_at = $at, ok = $ok
            """, transaction);
        command.Parameters.AddWithValue("$task", taskId);
        var destination = command.Parameters.Add("$destination", SqliteType.Text);
        var set = command.Parameters.Add("$set", SqliteType.Text);
        command.Parameters.AddWithValue("$at", UtcTimeConverter.ToText(checkedAt));
        var ok = command.Parameters.Add("$ok", SqliteType.Integer);
        foreach (var check in checks)
        {
            destination.Value = check.DestinationId;
            set.Value = check.SetName;
            ok.Value = check.Ok ? 1 : 0;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>The last check of each set of a task, by destination and set name.</summary>
    public Dictionary<(string DestinationId, string SetName), (DateTimeOffset CheckedAt, bool Ok)> GetSetChecks(string taskId)
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT destination_id, set_name, checked_at, ok FROM set_checks WHERE task_id = $task");
        command.Parameters.AddWithValue("$task", taskId);
        using var reader = command.ExecuteReader();
        var checks = new Dictionary<(string, string), (DateTimeOffset, bool)>();
        while (reader.Read())
        {
            checks[(reader.GetString(0).ToUpperInvariant(), reader.GetString(1))] = (ParseTime(reader.GetString(2)), reader.GetInt64(3) != 0);
        }

        return checks;
    }

    // ---- Plumbing ----------------------------------------------------------------------------------------------

    private const string RunColumns = """
        SELECT run_id, task_id, task_name, backup_type, trigger, scheduled_for, requested_by, started_at, finished_at,
            status, source_files, source_bytes, files_processed, bytes_written, errors, warnings, error_message, set_name,
            destinations, databases, remote, verify
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
                Remote = reader.IsDBNull(20) ? null : JsonSerializer.Deserialize(reader.GetString(20), StorageJsonContext.Default.RemoteResult),
                Verify = reader.IsDBNull(21) ? null : JsonSerializer.Deserialize(reader.GetString(21), StorageJsonContext.Default.RunVerify),
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

        if (version is 1 or 2 or 3)
        {
            // Stage 2a: the report of custos-helper, and the last committed run (its set is where the next copy of a
            // Linux server starts from).
            using var alter = Command(connection, "ALTER TABLE runs ADD COLUMN remote TEXT;", transaction);
            alter.ExecuteNonQuery();
            using var exists = Command(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'task_state';", transaction);
            if ((long)exists.ExecuteScalar()! > 0)
            {
                // Without the table it is made below, with the column.
                using var state = Command(connection, "ALTER TABLE task_state ADD COLUMN last_run_id TEXT;", transaction);
                state.ExecuteNonQuery();
            }
        }

        if (version is 1 or 2 or 3 or 4)
        {
            // Stage 3: what the verification of a run found, for the history and the monitoring server.
            using var alter = Command(connection, "ALTER TABLE runs ADD COLUMN verify TEXT;", transaction);
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
                compression_ratio REAL,
                last_run_id TEXT
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
                databases TEXT,
                remote TEXT,
                verify TEXT
            );

            CREATE INDEX IF NOT EXISTS runs_by_task ON runs (task_id, started_at);

            CREATE TABLE IF NOT EXISTS outbox (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                path TEXT NOT NULL,
                body BLOB NOT NULL,
                coalesce_key TEXT,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS set_checks (
                task_id TEXT NOT NULL,
                destination_id TEXT NOT NULL COLLATE NOCASE,
                set_name TEXT NOT NULL,
                checked_at TEXT NOT NULL,
                ok INTEGER NOT NULL,
                PRIMARY KEY (task_id, destination_id, set_name)
            ) WITHOUT ROWID;
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

/// <summary>A report waiting for the monitoring server: <c>POST {Path}</c> with <see cref="Body"/>.</summary>
public sealed record QueuedReport(long Seq, string Path, byte[] Body, DateTimeOffset CreatedAt);
