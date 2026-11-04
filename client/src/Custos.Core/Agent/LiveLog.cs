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

    /// <summary>Raised after a line is added, on the thread that added it.</summary>
    public event Action<LogLine>? Added;

    public LogLine Add(string level, string text, string? taskId = null, string? runId = null)
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

    public List<LogLine> Recent()
    {
        lock (_lock)
        {
            return [.. _lines];
        }
    }
}
