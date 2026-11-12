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

    /// <summary>Database servers on this computer whose databases are dumped into every set (section 6.2).</summary>
    public List<TaskDatabaseServer> DbServers { get; set; } = [];

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

    // ---- A Linux server over SSH (kind "ssh", CUSTOS-remote-pull.md 4.1); null for the other kinds ----

    /// <summary>Address of the Linux server.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Host { get; set; }

    /// <summary>Null = 22.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Port { get; set; }

    /// <summary>The account custos-helper runs for ("custos").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? User { get; set; }

    /// <summary>The pinned key of the server, "SHA256:…". Changed only on this computer (wizard, editor).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HostKeySha256 { get; set; }

    /// <summary>One of <see cref="RemotePresets"/>; only the wizard needs it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Preset { get; set; }

    /// <summary>External address of the office for <c>from=</c> of the key; null = the key works from anywhere.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OfficeIp { get; set; }

    /// <summary>Limit for the files in KB/s; 0 = none. Dumps are never limited.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SpeedLimitKbps { get; set; }

    /// <summary>The settings of helper.conf the wizard wrote into the install block; only the wizard needs them.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RemoteHelperSettings? Helper { get; set; }

    /// <summary>Which of the databases custos-helper gives out are dumped; null = all of them.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RemoteDatabaseChoice? Databases { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>What custos-helper may give out, as the wizard writes it into <c>/etc/custos/helper.conf</c>.</summary>
public sealed class RemoteHelperSettings
{
    /// <summary>Folders taken with everything inside (<c>root</c>).</summary>
    public List<string> Roots { get; set; } = [];

    /// <summary><c>find -path</c> patterns left out with everything inside (<c>exclude</c>).</summary>
    public List<string> Excludes { get; set; } = [];

    /// <summary>Values of <see cref="DbEngines"/> (<c>db_engine</c>).</summary>
    public List<string> DbEngines { get; set; } = [];

    /// <summary>Databases left out besides the system ones (<c>db_exclude</c>).</summary>
    public List<string> DbExclude { get; set; } = [];

    /// <summary>A my.cnf with an account for MariaDB or MySQL; null = root over the socket (<c>db_defaults_file</c>).</summary>
    public string? DbDefaultsFile { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// The databases of a Linux server a task dumps, ticked in the wizard from what custos-helper lists. Entries are
/// "&lt;engine&gt;/&lt;name&gt;", for example "mariadb/shop". What custos-helper gives out at all is decided on the
/// server (<c>db_exclude</c> of helper.conf); this choice only leaves out more.
/// </summary>
public sealed class RemoteDatabaseChoice
{
    /// <summary>True: every database custos-helper lists but <see cref="Exclude"/>, new ones too. False: only
    /// <see cref="Include"/>.</summary>
    public bool AllDatabases { get; set; } = true;

    public List<string> Exclude { get; set; } = [];

    public List<string> Include { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>"mariadb/shop".</summary>
    public static string Key(string engine, string name) => engine + "/" + name;

    /// <summary>Splits "mariadb/shop"; false when it is not a known engine and a name.</summary>
    public static bool TrySplit(string? key, out string engine, out string name)
    {
        int slash = key?.IndexOf('/', StringComparison.Ordinal) ?? -1;
        engine = slash > 0 ? key![..slash] : string.Empty;
        name = slash > 0 ? key![(slash + 1)..] : string.Empty;
        return DbEngines.IsKnown(engine) && name.Length > 0 && !name.Contains('/', StringComparison.Ordinal);
    }

    /// <summary>The names of one engine among the entries.</summary>
    public static List<string> Names(IEnumerable<string> keys, string engine) =>
        [.. keys.Select(k => TrySplit(k, out string e, out string n) && e == engine ? n : null).OfType<string>().Distinct(StringComparer.Ordinal)];

    /// <summary>"shop, blog" when all entries are of one engine, else "MariaDB: shop, PostgreSQL: blog".</summary>
    public static string Describe(IReadOnlyCollection<string> keys)
    {
        var split = keys.Select(k => TrySplit(k, out string e, out string n) ? (Engine: e, Name: n) : (Engine: string.Empty, Name: k)).ToList();
        bool several = split.Select(s => s.Engine).Distinct(StringComparer.Ordinal).Count() > 1;
        return string.Join(", ", split.Select(s => several && s.Engine.Length > 0 ? DbEngines.Title(s.Engine) + ": " + s.Name : s.Name));
    }
}

/// <summary>
/// A local MySQL, MariaDB or PostgreSQL server (for example the one of OSPanel): its databases are dumped with the
/// programs of its client into <c>db/&lt;engine&gt;/&lt;name&gt;.sql</c> of every set (section 6.2).
/// </summary>
public sealed class TaskDatabaseServer
{
    public string Id { get; set; } = TaskDefinition.NewId();

    /// <summary>One of <see cref="DbEngines"/>.</summary>
    public string Engine { get; set; } = DbEngines.MySql;

    /// <summary>The folder with mysqldump.exe, mariadb-dump.exe or pg_dump.exe and the other client programs.</summary>
    public string BinDir { get; set; } = string.Empty;

    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Null = the usual port of the engine (3306 or 5432).</summary>
    public int? Port { get; set; }

    /// <summary>An account of kind <see cref="CredentialKinds.Db"/>: login and password of the database server.</summary>
    public string? CredentialId { get; set; }

    /// <summary>True: every database but the system ones and <see cref="Exclude"/>. False: only <see cref="Include"/>.</summary>
    public bool AllDatabases { get; set; } = true;

    public List<string> Exclude { get; set; } = [];

    public List<string> Include { get; set; } = [];

    /// <summary>The data folder the server reported at the last connection check; only for the warning of the editor
    /// when it is among the sources.</summary>
    public string? DataDir { get; set; }

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

    /// <summary>S3: every file of a set cannot be deleted for this many days after it is written (Object Lock,
    /// section 6.3); 0 = off.</summary>
    public int ObjectLockDays { get; set; }

    /// <summary>S3: one of <see cref="ObjectLockModes"/>; null = <see cref="ObjectLockModes.Governance"/>.</summary>
    public string? ObjectLockMode { get; set; }

    /// <summary>
    /// The server this destination trusts: the SSH key of an SFTP server ("SHA256:…") or the SHA-256 of a TLS
    /// certificate Windows does not trust by itself ("AB:CD:…", FTPS, WebDAV, S3). Set on this computer only, by
    /// «Проверить доступ»; null = an SFTP server is not confirmed yet, a TLS server must have a trusted certificate.
    /// </summary>
    public string? ServerFingerprint { get; set; }

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
