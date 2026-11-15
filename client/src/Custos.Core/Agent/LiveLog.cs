using Custos.Core.Ipc;

namespace Custos.Core.Agent;

/// <summary>
/// The live log of the interface (section 6.10): the latest lines in memory, numbered, with an event for each new line.
/// Thread-safe.
/// </summary>
public sealed class LiveLog(int capacity = 2000)
{
    private readonly Queue<LogLine> _lines = new();
    private readonly Lock _lock = new();
    private long _seq;

    public int Capacity => capacity;

    /// <summary>Raised after a line is added, on the thread that added it.</summary>
    public event Action<LogLine>? Added;

    public LogLine Add(string level, string text, string? taskId = null, string? runId = null, string? taskName = null)
    {
        LogLine line;
        lock (_lock)
        {
            line = new LogLine
            {
                Seq = ++_seq,
                Time = DateTimeOffset.UtcNow,
                Level = level,
                TaskId = taskId,
                TaskName = taskName,
                RunId = runId,
                Text = text,
            };
            _lines.Enqueue(line);
            while (_lines.Count > capacity)
            {
                _lines.Dequeue();
            }
        }

        Added?.Invoke(line);
        return line;
    }

    /// <summary>
    /// Puts lines kept from before (the journal on disk after a restart) in front, numbered like new ones. Raises no
    /// <see cref="Added"/>: they are written already.
    /// </summary>
    public void Restore(IEnumerable<LogLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        lock (_lock)
        {
            var newer = _lines.ToList();
            _lines.Clear();
            foreach (var line in lines)
            {
                _lines.Enqueue(line);
            }

            foreach (var line in newer)
            {
                _lines.Enqueue(line);
            }

            _seq = 0;
            foreach (var line in _lines)
            {
                line.Seq = ++_seq;
            }

            while (_lines.Count > capacity)
            {
                _lines.Dequeue();
            }
        }
    }

    public List<LogLine> Recent()
    {
        lock (_lock)
        {
            return [.. _lines];
        }
    }
}
