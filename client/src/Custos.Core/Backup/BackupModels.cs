using Custos.Core.Model;

namespace Custos.Core.Backup;

/// <summary>One run to perform.</summary>
public sealed class BackupRequest
{
    public required TaskDefinition Task { get; init; }

    public required string RunId { get; init; }

    /// <summary>One of <see cref="RunTriggers"/>.</summary>
    public required string Trigger { get; init; }

    public DateTimeOffset? ScheduledFor { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Write a log line for every copied file (<see cref="LogLevels.Files"/>).</summary>
    public bool LogEveryFile { get; init; }

    /// <summary>Time zone for set names and for "full copy on Sunday".</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>Read and write the files with low I/O priority (the program setting "Low", the default).</summary>
    public bool LowPriority { get; init; }

    /// <summary>How big the dumps of the databases were last time: the progress counts on about as much.</summary>
    public long DumpBytesHint { get; init; }
}

/// <summary>What a run did (the fields of <c>runs/finish</c>, A.5).</summary>
public sealed class BackupResult
{
    /// <summary>One of <see cref="RunStatuses"/>.</summary>
    public string Status { get; set; } = RunStatuses.Success;

    /// <summary>The type actually made: an incremental task makes a full copy now and then.</summary>
    public required string BackupType { get; set; }

    public string? SetName { get; set; }

    public long SourceFiles { get; set; }

    public long SourceBytes { get; set; }

    public long FilesProcessed { get; set; }

    public long BytesWritten { get; set; }

    public int Errors { get; set; }

    public int Warnings { get; set; }

    /// <summary>The first error, in words.</summary>
    public string? ErrorMessage { get; set; }

    public List<DestinationResult> Destinations { get; } = [];

    /// <summary>The dumped databases (section 6.2, A.5).</summary>
    public List<DatabaseResult> Databases { get; } = [];

    public int DeletedSets { get; set; }

    /// <summary>The shadow copy failed (event 3000).</summary>
    public bool VssFailed { get; set; }

    /// <summary>A destination had too little space (event 3001).</summary>
    public bool OutOfSpace { get; set; }

    /// <summary>The run changed nothing in the destinations because no file had changed.</summary>
    public bool NothingToDo { get; set; }
}

/// <summary>A run in progress, for the interface (and later for <c>runs/progress</c>, A.5).</summary>
public sealed class BackupProgress
{
    /// <summary><c>vss</c>, <c>scan</c>, <c>copy</c>, <c>retention</c> (A.5).</summary>
    public string Phase { get; set; } = BackupPhases.Scan;

    public string? BackupType { get; set; }

    public long FilesDone { get; set; }

    public long FilesTotal { get; set; }

    public long BytesDone { get; set; }

    public long BytesTotal { get; set; }

    public long SpeedBps { get; set; }

    public long? EtaS { get; set; }

    public string? CurrentFile { get; set; }

    public List<DestinationProgress> Destinations { get; set; } = [];

    public double Percent => BytesTotal > 0 ? Math.Min(100, BytesDone * 100.0 / BytesTotal)
        : FilesTotal > 0 ? Math.Min(100, FilesDone * 100.0 / FilesTotal) : 0;

    public BackupProgress Copy() => new()
    {
        Phase = Phase,
        BackupType = BackupType,
        FilesDone = FilesDone,
        FilesTotal = FilesTotal,
        BytesDone = BytesDone,
        BytesTotal = BytesTotal,
        SpeedBps = SpeedBps,
        EtaS = EtaS,
        CurrentFile = CurrentFile,
        Destinations = [.. Destinations.Select(d => new DestinationProgress { DestinationId = d.DestinationId, Label = d.Label, State = d.State, Error = d.Error })],
    };
}

/// <summary>Phases of a run (A.5).</summary>
public static class BackupPhases
{
    public const string PreEvents = "pre_events";
    public const string Vss = "vss";
    public const string Scan = "scan";
    public const string Copy = "copy";
    public const string Verify = "verify";
    public const string Retention = "retention";
    public const string PostEvents = "post_events";
}

public sealed class DestinationProgress
{
    public string DestinationId { get; set; } = string.Empty;

    /// <summary>Where it writes, in words: a path or "USB disk «LABEL»".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary><c>pending</c>, <c>writing</c>, <c>done</c> or <c>failed</c>.</summary>
    public string State { get; set; } = DestinationStates.Pending;

    public string? Error { get; set; }
}

public static class DestinationStates
{
    public const string Pending = "pending";
    public const string Writing = "writing";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>Holds a run while it is paused (section 6.7, A.4).</summary>
public sealed class PauseGate : IDisposable
{
    private readonly ManualResetEventSlim _open = new(initialState: true);

    public bool IsPaused => !_open.IsSet;

    public DateTimeOffset? PausedAt { get; private set; }

    public void Pause()
    {
        if (_open.IsSet)
        {
            PausedAt = DateTimeOffset.UtcNow;
            _open.Reset();
        }
    }

    public void Resume()
    {
        PausedAt = null;
        _open.Set();
    }

    /// <summary>Returns at once unless paused; then waits for <see cref="Resume"/> or cancellation.</summary>
    public void WaitWhilePaused(CancellationToken cancellationToken) => _open.Wait(cancellationToken);

    public void Dispose() => _open.Dispose();
}

/// <summary>Where the engine writes its log: the run log file and the live log of the interface.</summary>
public interface IRunLog
{
    void Write(RunLogLevel level, string message);
}

public enum RunLogLevel
{
    /// <summary>Per-file details, written only at the "every file" log level.</summary>
    Detail,
    Info,
    Warning,
    Error,
}
