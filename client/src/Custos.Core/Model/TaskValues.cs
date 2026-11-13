namespace Custos.Core.Model;

// String values of the task object (appendix A.9 of docs/protocol.md). They are kept as strings rather than enums so
// that a value written by a newer version survives a round trip through an older one.

/// <summary>Values of <see cref="TaskDefinition.BackupType"/> and of the backup type of a run.</summary>
public static class BackupTypes
{
    public const string Full = "full";
    public const string Incremental = "incremental";
    public const string Differential = "differential";
    public const string Mirror = "mirror";
}

/// <summary>Values of <see cref="TaskDefinition.MirrorMode"/>.</summary>
public static class MirrorModes
{
    public const string Exact = "exact";
    public const string Safe = "safe";
}

/// <summary>Values of <see cref="TaskSource.Kind"/>.</summary>
public static class SourceKinds
{
    public const string Folder = "folder";
    public const string File = "file";
    public const string Unc = "unc";

    /// <summary>A Linux server, read through custos-helper over SSH (CUSTOS-remote-pull.md).</summary>
    public const string Ssh = "ssh";
}

/// <summary>Values of <see cref="TaskSource.Preset"/>: what the wizard offers to take from a Linux server.</summary>
public static class RemotePresets
{
    /// <summary>ispmanager: the sites only (the default).</summary>
    public const string IspmanagerSites = "ispmanager_sites";

    /// <summary>ispmanager: the sites and the panel.</summary>
    public const string IspmanagerFull = "ispmanager_full";

    /// <summary>A Linux server without a panel.</summary>
    public const string Linux = "linux";

    /// <summary>The lists changed by hand.</summary>
    public const string Custom = "custom";
}

/// <summary>Values of <see cref="TaskDestination.Kind"/>.</summary>
public static class DestinationKinds
{
    public const string Local = "local";
    public const string Unc = "unc";
    public const string Usb = "usb";
    public const string Ftp = "ftp";
    public const string Ftps = "ftps";
    public const string Sftp = "sftp";
    public const string S3 = "s3";
    public const string WebDav = "webdav";

    /// <summary>Kinds this version can write to.</summary>
    public static bool IsSupported(string kind) => kind is Local or Unc or Usb or Ftp or Ftps or Sftp or S3 or WebDav;

    /// <summary>A folder of this computer, of a share or of a USB disk; the others are servers (stage 4).</summary>
    public static bool IsFileSystem(string kind) => kind is Local or Unc or Usb;

    /// <summary>The kind of credential a destination of this kind logs in with; null if it needs none.</summary>
    public static string? CredentialKind(string kind) => kind switch
    {
        Unc => CredentialKinds.Windows,
        Ftp or Ftps => CredentialKinds.Ftp,
        Sftp => CredentialKinds.Sftp,
        S3 => CredentialKinds.S3,
        WebDav => CredentialKinds.WebDav,
        _ => null,
    };

    /// <summary>Kinds that let a writer go back in the file, which 7z needs to finish its header (section 6.5).</summary>
    public static bool CanSeek(string kind) => kind is Local or Unc or Usb or Sftp;

    /// <summary>
    /// The folder of a <see cref="Usb"/> destination relative to the root of the disk. A drive letter typed by mistake
    /// is dropped: the disk is found by its label, and writing to "E:\..." would go to whatever disk has that letter.
    /// </summary>
    public static string UsbFolder(string? path)
    {
        string folder = (path ?? string.Empty).Trim();
        if (folder.Length >= 2 && folder[1] == ':' && char.IsAsciiLetter(folder[0]))
        {
            folder = folder[2..];
        }

        return folder.Trim('\\', '/');
    }
}

/// <summary>Values of <see cref="ArchiveOptions.Format"/>.</summary>
public static class ArchiveFormats
{
    public const string None = "none";
    public const string Zip = "zip";
    public const string SevenZip = "7z";
}

/// <summary>Values of <see cref="RetentionOptions.Mode"/>.</summary>
public static class RetentionModes
{
    public const string None = "none";
    public const string Count = "count";
    public const string Age = "age";
    public const string Gfs = "gfs";
}

/// <summary>Values of <see cref="ScheduleOptions.Type"/>.</summary>
public static class ScheduleTypes
{
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";
    public const string Interval = "interval";
    public const string Manual = "manual";
}

/// <summary>Values of <see cref="VssOptions.OnFailure"/>.</summary>
public static class VssFailureActions
{
    public const string Abort = "abort";
    public const string Continue = "continue";
}

/// <summary>Values of <see cref="TaskDefinition.LogLevel"/> and <see cref="AgentSettings.DefaultLogLevel"/>.</summary>
public static class LogLevels
{
    public const string Inherit = "inherit";

    /// <summary>"Итоги и ошибки": totals, warnings and errors.</summary>
    public const string Summary = "summary";

    /// <summary>"Каждый файл": also every copied file.</summary>
    public const string Files = "files";
}

/// <summary>Values of <see cref="TaskEvent.Kind"/>.</summary>
public static class TaskEventKinds
{
    public const string Run = "run";
    public const string ServiceStop = "service_stop";
    public const string ServiceStart = "service_start";
}

/// <summary>Values of <see cref="TaskEvents.After"/>.</summary>
public static class AfterActions
{
    public const string None = "none";
    public const string Shutdown = "shutdown";
    public const string Restart = "restart";
    public const string Sleep = "sleep";
    public const string Hibernate = "hibernate";
}

/// <summary>How a run was started (A.5).</summary>
public static class RunTriggers
{
    public const string Schedule = "schedule";
    public const string CatchUp = "catch_up";
    public const string Manual = "manual";
    public const string Remote = "remote";
}

/// <summary>Result of a run (A.5, A.12).</summary>
public static class RunStatuses
{
    public const string Success = "success";
    public const string Warning = "warning";
    public const string Error = "error";
    public const string Aborted = "aborted";

    /// <summary>A run that counts as a backup: its set is complete, at most some files were skipped.</summary>
    public static bool IsSuccessful(string? status) => status is Success or Warning;
}

/// <summary>Values of <see cref="TaskDestination.ObjectLockMode"/> (section 6.3).</summary>
public static class ObjectLockModes
{
    /// <summary>An account with the permission s3:BypassGovernanceRetention can still delete; the agent's must not have it.</summary>
    public const string Governance = "governance";

    /// <summary>Nobody can delete before the time, the owner of the bucket included.</summary>
    public const string Compliance = "compliance";

    public static bool IsKnown(string? mode) => mode is null or Governance or Compliance;
}

/// <summary>Values of <see cref="Credential.Kind"/>.</summary>
public static class CredentialKinds
{
    public const string Windows = "windows";
    public const string Ftp = "ftp";
    public const string Sftp = "sftp";
    public const string S3 = "s3";
    public const string WebDav = "webdav";
    public const string ArchivePassword = "archive_password";

    /// <summary>Login and password of a database server (section 6.2); the password may be empty.</summary>
    public const string Db = "db";

    /// <summary>An Ed25519 key the agent made for a Linux server; the private part never leaves the agent.</summary>
    public const string SshKey = "ssh_key";
}

/// <summary>Values of <see cref="TaskDatabaseServer.Engine"/>.</summary>
public static class DbEngines
{
    public const string MySql = "mysql";
    public const string MariaDb = "mariadb";
    public const string PostgreSql = "postgresql";

    public static bool IsKnown(string? engine) => engine is MySql or MariaDb or PostgreSql;

    /// <summary>MySQL and MariaDB share their programs and their behaviour.</summary>
    public static bool IsMySqlFamily(string? engine) => engine is MySql or MariaDb;

    public static int DefaultPort(string? engine) => engine == PostgreSql ? 5432 : 3306;

    /// <summary>The name for people: MySQL, MariaDB, PostgreSQL.</summary>
    public static string Title(string? engine) => engine switch
    {
        MySql => "MySQL",
        MariaDb => "MariaDB",
        PostgreSql => "PostgreSQL",
        _ => engine ?? "?",
    };
}
