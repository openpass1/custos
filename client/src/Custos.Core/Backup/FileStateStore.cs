namespace Custos.Core.Backup;

/// <summary>What the database remembers about the copies of a task between runs (section 7).</summary>
public sealed record TaskBackupState(string? FullRunId, string? FullSetName, DateTimeOffset? FullAt, int RunsSinceFull)
{
    public static readonly TaskBackupState Empty = new(null, null, null, 0);
}

/// <summary>A successful run, as written to the database.</summary>
public sealed record CommittedRun(string RunId, string BackupType, string? SetName, DateTimeOffset StartedAt);

/// <summary>
/// The state of the files of each task at its last successful copy, and at its last full copy (for differential
/// copies). Updated in one transaction, and only after a successful run.
/// </summary>
public interface IFileStateStore
{
    TaskBackupState GetBackupState(string taskId);

    /// <summary>Files by original path (case-insensitive).</summary>
    /// <param name="baseFull">The state of the last full copy instead of the last copy of any type.</param>
    Dictionary<string, FileStamp> LoadFileState(string taskId, bool baseFull);

    /// <summary>Replaces the state after a successful run; a full run also becomes the new base.</summary>
    void CommitRun(string taskId, CommittedRun run, IReadOnlyCollection<KeyValuePair<string, FileStamp>> files);
}

/// <summary>A store kept in memory, for tests.</summary>
public sealed class MemoryFileStateStore : IFileStateStore
{
    private readonly Dictionary<string, TaskBackupState> _states = [];
    private readonly Dictionary<(string, bool), Dictionary<string, FileStamp>> _files = [];
    private readonly Lock _lock = new();

    public TaskBackupState GetBackupState(string taskId)
    {
        lock (_lock)
        {
            return _states.TryGetValue(taskId, out var state) ? state : TaskBackupState.Empty;
        }
    }

    public Dictionary<string, FileStamp> LoadFileState(string taskId, bool baseFull)
    {
        lock (_lock)
        {
            return _files.TryGetValue((taskId, baseFull), out var files)
                ? new Dictionary<string, FileStamp>(files, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void CommitRun(string taskId, CommittedRun run, IReadOnlyCollection<KeyValuePair<string, FileStamp>> files)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_lock)
        {
            var copy = new Dictionary<string, FileStamp>(files, StringComparer.OrdinalIgnoreCase);
            _files[(taskId, false)] = copy;
            var state = _states.TryGetValue(taskId, out var current) ? current : TaskBackupState.Empty;
            if (run.BackupType == Model.BackupTypes.Full)
            {
                _files[(taskId, true)] = new Dictionary<string, FileStamp>(copy, StringComparer.OrdinalIgnoreCase);
                _states[taskId] = new TaskBackupState(run.RunId, run.SetName, run.StartedAt, 0);
            }
            else
            {
                _states[taskId] = state with { RunsSinceFull = state.RunsSinceFull + 1 };
            }
        }
    }
}
