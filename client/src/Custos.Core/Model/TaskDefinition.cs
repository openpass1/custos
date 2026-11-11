using System.Text.Json;
using System.Text.Json.Serialization;
using Custos.Core.Json;

namespace Custos.Core.Model;

/// <summary>
/// A backup task: the <c>task</c> object of appendix A.9. Defaults are the ones given there.
/// Fields this version does not know are kept in <see cref="ExtensionData"/> and written back unchanged.
/// </summary>
public sealed class TaskDefinition
{
    public string Id { get; set; } = NewId();

    public int Revision { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary><c>local:DOMAIN\user</c> or <c>server:login</c>.</summary>
    public string? UpdatedBy { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string Comment { get; set; } = string.Empty;

    /// <summary>Higher runs earlier in the queue.</summary>
    public int Priority { get; set; }

    /// <summary>One of <see cref="BackupTypes"/>.</summary>
    public string BackupType { get; set; } = BackupTypes.Incremental;

    /// <summary>One of <see cref="MirrorModes"/>, only for <see cref="BackupTypes.Mirror"/>.</summary>
    public string? MirrorMode { get; set; }

    public ForceFullOptions ForceFull { get; set; } = new();

    public List<TaskSource> Sources { get; set; } = [];

    public TaskFilters Filters { get; set; } = new();

    /// <summary>Whose profile %USERPROFILE% and the like refer to; null = whoever created the task.</summary>
    public string? ProfileSid { get; set; }

    public List<TaskDestination> Destinations { get; set; } = [];

    public LayoutOptions Layout { get; set; } = new();

    public ArchiveOptions Archive { get; set; } = new();

    public VerifyOptions Verify { get; set; } = new();

    public RetentionOptions Retention { get; set; } = new();

    public ScheduleOptions Schedule { get; set; } = new();

    public VssOptions Vss { get; set; } = new();

    public CopyOptions Copy { get; set; } = new();

    public TaskEvents Events { get; set; } = new();

    /// <summary>One of <see cref="LogLevels"/>.</summary>
    public string LogLevel { get; set; } = LogLevels.Inherit;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>A new random id (UUID v4, A.1).</summary>
    public static string NewId() => Guid.NewGuid().ToString();

    /// <summary>A deep copy.</summary>
    public TaskDefinition Clone() =>
        JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(this, ConfigJsonContext.Default.TaskDefinition), ConfigJsonContext.Default.TaskDefinition)!;
}

/// <summary>When an incremental or differential task makes a full copy anyway (section 6.1).</summary>
public sealed class ForceFullOptions
{
    /// <summary>Every N-th run is full; 0 = off.</summary>
    public int EveryRuns { get; set; }

    /// <summary>ISO weekday 1–7 (1 = Monday) or null.</summary>
    public int? Weekday { get; set; }

    /// <summary>Day of the month 1–31, -1 = the last day, or null.</summary>
    public int? MonthDay { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class TaskSource
{
    public string Id { get; set; } = TaskDefinition.NewId();

    /// <summary>One of <see cref="SourceKinds"/>.</summary>
    public string Kind { get; set; } = SourceKinds.Folder;

    public string Path { get; set; } = string.Empty;

    /// <summary>Account for a network source; null = the service account.</summary>
    public string? CredentialId { get; set; }

    public bool Recursive { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class TaskFilters
{
    /// <summary>File masks; if any are given, only matching files are copied.</summary>
    public List<string> Include { get; set; } = [];

    public List<string> Exclude { get; set; } = ["~$*", "*.tmp", "*.log"];

    /// <summary>Folder names (or masks, or paths) to leave out with everything inside.</summary>
    public List<string> ExcludeDirs { get; set; } = [];

    public bool SkipHidden { get; set; }

    public bool SkipSystem { get; set; } = true;

    public bool SkipTemp { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class TaskDestination
{
    public string Id { get; set; } = TaskDefinition.NewId();

    /// <summary>One of <see cref="DestinationKinds"/>.</summary>
    public string Kind { get; set; } = DestinationKinds.Local;

    /// <summary>Folder; for <see cref="DestinationKinds.Usb"/> relative to the root of the disk.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Volume label of the USB disk.</summary>
    public string? UsbLabel { get; set; }

    public string? Host { get; set; }

    public int? Port { get; set; }

    public string? Endpoint { get; set; }

    public string? Bucket { get; set; }

    public string? Region { get; set; }

    public string? CredentialId { get; set; }

    /// <summary>Close the connection to the share right after the run (section 6.3).</summary>
    public bool DisconnectAfter { get; set; } = true;

    /// <summary>0 = no limit.</summary>
    public int SpeedLimitKbps { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class LayoutOptions
{
    public const string DefaultNameTemplate = "{task} {yyyy}-{MM}-{dd} {HH}-{mm} ({type})";

    /// <summary>«Отдельная папка для каждой копии».</summary>
    public bool SeparateFolders { get; set; } = true;

    public string NameTemplate { get; set; } = DefaultNameTemplate;

    public bool KeepTopDir { get; set; } = true;

    public bool DeleteEmptyDirs { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class ArchiveOptions
{
    /// <summary>One of <see cref="ArchiveFormats"/>.</summary>
    public string Format { get; set; } = ArchiveFormats.None;

    public int Level { get; set; } = 5;

    public int SplitMb { get; set; }

    public bool Encrypt { get; set; }

    public bool EncryptNames { get; set; }

    public string? PasswordCredentialId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class VerifyOptions
{
    public int EveryRuns { get; set; }

    public int StoredEveryDays { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class RetentionOptions
{
    /// <summary>One of <see cref="RetentionModes"/>.</summary>
    public string Mode { get; set; } = RetentionModes.Count;

    public int KeepFull { get; set; } = 12;

    public int MaxAgeDays { get; set; }

    public GfsOptions Gfs { get; set; } = new();

    public int SafeMirrorDays { get; set; } = 30;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class GfsOptions
{
    public int Daily { get; set; } = 7;

    public int Weekly { get; set; } = 4;

    public int Monthly { get; set; } = 12;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class ScheduleOptions
{
    /// <summary>One of <see cref="ScheduleTypes"/>.</summary>
    public string Type { get; set; } = ScheduleTypes.Daily;

    /// <summary>Local time of the agent, "HH:mm".</summary>
    public List<string> Times { get; set; } = ["00:00"];

    /// <summary>ISO weekdays (1 = Monday). A filter for daily, required for weekly.</summary>
    public List<int> DaysOfWeek { get; set; } = [1, 2, 3, 4, 5, 6, 7];

    /// <summary>For monthly; -1 = the last day of the month.</summary>
    public List<int> DaysOfMonth { get; set; } = [];

    public int IntervalMinutes { get; set; }

    /// <summary>Run a slot missed while the machine was off or asleep once it is back (section 6.7).</summary>
    public bool CatchUp { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class VssOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>One of <see cref="VssFailureActions"/>.</summary>
    public string OnFailure { get; set; } = VssFailureActions.Abort;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class CopyOptions
{
    public bool PreserveAcl { get; set; } = true;

    /// <summary>How many times to retry reading a file, 0–5, two seconds apart.</summary>
    public int ReadRetries { get; set; } = 1;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class TaskEvents
{
    public List<TaskEvent> Pre { get; set; } = [];

    public List<TaskEvent> Post { get; set; } = [];

    public bool AbortOnPreFailure { get; set; } = true;

    /// <summary>One of <see cref="AfterActions"/>.</summary>
    public string After { get; set; } = AfterActions.None;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class TaskEvent
{
    /// <summary>One of <see cref="TaskEventKinds"/>.</summary>
    public string Kind { get; set; } = TaskEventKinds.Run;

    public string? Command { get; set; }

    public string Args { get; set; } = string.Empty;

    public int TimeoutS { get; set; } = 3600;

    public string? Service { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
