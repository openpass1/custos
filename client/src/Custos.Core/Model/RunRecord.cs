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

    /// <summary>For a task with a Linux server: what custos-helper reported (<c>remote</c> of <c>runs/finish</c>, A.5).</summary>
    public RemoteResult? Remote { get; set; }

    /// <summary>What the verification of this run found (section 6.5); null when the run verified nothing.</summary>
    public RunVerify? Verify { get; set; }
}

/// <summary>The verification of a run, as the history shows it (and <c>verify</c> of <c>runs/finish</c>, A.5).</summary>
public sealed class RunVerify
{
    /// <summary>Every set checked is whole.</summary>
    public bool Ok { get; set; }

    /// <summary><c>new</c> (the new copy) or <c>stored</c> (all the stored sets).</summary>
    public string Scope { get; set; } = "new";

    public int SetsChecked { get; set; }

    /// <summary>Names of the sets that failed.</summary>
    public List<string> FailedSets { get; set; } = [];
}

/// <summary>The <c>remote</c> block of <c>runs/finish</c> (A.5, CUSTOS-remote-pull.md 5).</summary>
public sealed class RemoteResult
{
    public string HelperVersion { get; set; } = string.Empty;

    /// <summary>Free space on the disk of the roots of the server (the smallest), or null.</summary>
    public long? ServerFreeBytes { get; set; }

    /// <summary>Files changed since the last copy.</summary>
    public long? FilesChanged { get; set; }

    /// <summary>Files deleted since the last copy; null when the last copy could not be read.</summary>
    public long? FilesDeleted { get; set; }
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
