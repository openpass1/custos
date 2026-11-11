using Custos.Core.Ipc;

namespace Custos.Core.Agent;

/// <summary>
/// The queue of runs (section 6.7): strictly one after another, higher priority first, then in the order queued.
/// A task is never in the queue twice. Thread-safe.
/// </summary>
public sealed class PendingRuns
{
    private readonly List<QueuedRun> _entries = [];
    private readonly Lock _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Adds a run unless its task is already queued.</summary>
    public bool TryEnqueue(QueuedRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_lock)
        {
            if (_entries.Any(e => SameTask(e.TaskId, run.TaskId)))
            {
                return false;
            }

            _entries.Add(run);
            return true;
        }
    }

    public bool Contains(string taskId)
    {
        lock (_lock)
        {
            return _entries.Any(e => SameTask(e.TaskId, taskId));
        }
    }

    /// <summary>Takes the run to start next.</summary>
    /// <param name="priorityOf">The current priority of a task, or null if the task no longer exists (its run is dropped).</param>
    public QueuedRun? TryDequeue(Func<string, int?> priorityOf)
    {
        ArgumentNullException.ThrowIfNull(priorityOf);
        lock (_lock)
        {
            _entries.RemoveAll(e => priorityOf(e.TaskId) is null);
            if (_entries.Count == 0)
            {
                return null;
            }

            var next = Ordered(priorityOf)[0];
            _entries.Remove(next);
            return next;
        }
    }

    public bool Remove(string taskId)
    {
        lock (_lock)
        {
            return _entries.RemoveAll(e => SameTask(e.TaskId, taskId)) > 0;
        }
    }

    /// <summary>The runs in the order they will start.</summary>
    public List<QueuedRun> Snapshot(Func<string, int?> priorityOf)
    {
        ArgumentNullException.ThrowIfNull(priorityOf);
        lock (_lock)
        {
            return Ordered(priorityOf);
        }
    }

    // OrderBy is stable, so runs of the same priority keep the order they were queued in.
    private List<QueuedRun> Ordered(Func<string, int?> priorityOf) =>
        [.. _entries.OrderByDescending(e => priorityOf(e.TaskId) ?? int.MinValue)];

    private static bool SameTask(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
