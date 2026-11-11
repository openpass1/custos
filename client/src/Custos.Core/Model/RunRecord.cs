namespace Custos.Core.Model;

/// <summary>One run of a task, as kept in the local history (section 6.10) and reported to the server (A.5).</summary>
public sealed class RunRecord
{
    public required string RunId { get; set; }

    public required string TaskId { get; set; }

    public required string TaskName { get; set; }

    /// <summary>One of <see cref="BackupTypes"/>: what the run made.</summary>
    public required string BackupType { get; set; }

    /// <summary>One of <see cref="RunTriggers"/>.</summary>
    public required string Trigger { get; set; }

    public DateTimeOffset? ScheduledFor { get; set; }

    /// <summary>Who pressed "Run" (DOMAIN\user), for manual runs.</summary>
    public string? RequestedBy { get; set; }

    public required DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>One of <see cref="RunStatuses"/>; null while the run goes on.</summary>
    public string? Status { get; set; }

    public long SourceFiles { get; set; }

    public long SourceBytes { get; set; }

    public long FilesProcessed { get; set; }

    public long BytesWritten { get; set; }

    public int Errors { get; set; }

    public int Warnings { get; set; }

    public string? ErrorMessage { get; set; }

    public string? SetName { get; set; }

    public List<DestinationResult> Destinations { get; set; } = [];

    /// <summary>The dumped databases (section 6.2; <c>databases</c> of <c>runs/finish</c>, A.5).</summary>
    public List<DatabaseResult> Databases { get; set; } = [];
}

/// <summary>Result of a run for one destination (A.5).</summary>
public sealed class DestinationResult
{
    public required string DestinationId { get; set; }

    /// <summary><see cref="RunStatuses.Success"/> or <see cref="RunStatuses.Error"/>.</summary>
    public required string Status { get; set; }

    public string? SetName { get; set; }

    public long Bytes { get; set; }

    public string? Error { get; set; }
}

/// <summary>Result of the dump of one database (A.5).</summary>
public sealed class DatabaseResult
{
    /// <summary>One of <see cref="DbEngines"/>.</summary>
    public required string Engine { get; set; }

    public required string Name { get; set; }

    /// <summary>Size of the dump.</summary>
    public long Bytes { get; set; }

    public bool Ok { get; set; }

    /// <summary>Why the dump failed; null when it worked.</summary>
    public string? Error { get; set; }
}
