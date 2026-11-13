using System.Text.Json;
using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Json;
using Custos.Core.Model;

namespace Custos.Gui.Tests;

/// <summary>Made-up tasks, accounts and states as the service would report them: no real paths, servers or accounts.</summary>
internal static class Samples
{
    public const string AccountId = "aaaaaaaa-0000-4000-8000-000000000001";
    public const string OfficeId = "11111111-0000-4000-8000-000000000001";
    public const string PhotosId = "22222222-0000-4000-8000-000000000002";
    public const string ArchiveId = "33333333-0000-4000-8000-000000000003";
    public const string RunId = "44444444-0000-4000-8000-000000000004";
    public const string UsbProblem = "The disk BACKUP-USB is not connected.";

    private static readonly DateTimeOffset Evening = new(2026, 11, 3, 21, 0, 0, TimeSpan.Zero);

    public static Credential Account() => new()
    {
        Id = AccountId,
        Kind = CredentialKinds.Windows,
        User = "backup-user",
        Domain = "EXAMPLE",
        HasSecret = true,
    };

    public const string SftpAccountId = "aaaaaaaa-0000-4000-8000-000000000002";
    public const string FtpAccountId = "aaaaaaaa-0000-4000-8000-000000000003";
    public const string WebDavAccountId = "aaaaaaaa-0000-4000-8000-000000000004";
    public const string S3AccountId = "aaaaaaaa-0000-4000-8000-000000000005";

    /// <summary>An account of a server: the kind and a made-up login (for S3 the access key).</summary>
    public static Credential ServerAccount(string kind) => new()
    {
        Id = kind switch
        {
            CredentialKinds.Sftp => SftpAccountId,
            CredentialKinds.Ftp => FtpAccountId,
            CredentialKinds.WebDav => WebDavAccountId,
            _ => S3AccountId,
        },
        Kind = kind,
        User = kind == CredentialKinds.S3 ? "EXAMPLEACCESSKEY" : "backup-" + kind,
        HasSecret = true,
    };

    public const string GDriveAccountId = "aaaaaaaa-0000-4000-8000-000000000006";
    public const string YaDiskAccountId = "aaaaaaaa-0000-4000-8000-000000000007";
    public const string DropboxAccountId = "aaaaaaaa-0000-4000-8000-000000000008";

    /// <summary>An account of a cloud drive connected on this computer: the login the drive told, no password.</summary>
    public static Credential CloudAccount(string kind) => new()
    {
        Id = kind switch
        {
            CredentialKinds.GDrive => GDriveAccountId,
            CredentialKinds.YaDisk => YaDiskAccountId,
            _ => DropboxAccountId,
        },
        Kind = kind,
        User = "backup." + kind + "@example.org",
        HasSecret = true,
    };

    /// <summary>A destination on a cloud drive, every field of its kind filled in.</summary>
    public static TaskDestination Cloud(string kind) => new()
    {
        Id = "c-" + kind,
        Kind = kind,
        Path = "office/pc01",
        CredentialId = CloudAccount(kind).Id,
        SpeedLimitKbps = 1000,
        PartMb = kind == DestinationKinds.YaDisk ? 900 : 4000,
    };

    /// <summary>A destination on a server, every field of its kind filled in (example.org is reserved for examples).</summary>
    public static TaskDestination Remote(string kind) => kind switch
    {
        DestinationKinds.Sftp => new TaskDestination
        {
            Id = "r1",
            Kind = kind,
            Host = "backup.example.org",
            Port = 2222,
            Path = "/srv/backup/office",
            CredentialId = SftpAccountId,
            SpeedLimitKbps = 2000,
            ServerFingerprint = ServerKey,
        },
        DestinationKinds.Ftp => new TaskDestination { Id = "r2", Kind = kind, Host = "ftp.example.org", Path = "backup/office" },
        DestinationKinds.Ftps => new TaskDestination
        {
            Id = "r3",
            Kind = kind,
            Host = "ftp.example.org",
            Port = 990,
            Path = "backup",
            CredentialId = FtpAccountId,
            ServerFingerprint = "9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08",
        },
        DestinationKinds.WebDav => new TaskDestination
        {
            Id = "r4",
            Kind = kind,
            Host = "https://dav.example.org/remote.php/dav/files/backup",
            Path = "office",
            CredentialId = WebDavAccountId,
            SpeedLimitKbps = 300,
        },
        _ => new TaskDestination
        {
            Id = "r5",
            Kind = DestinationKinds.S3,
            Endpoint = "https://s3.example.org",
            Bucket = "office-backup",
            Region = "eu-central-1",
            Path = "pc01",
            CredentialId = S3AccountId,
            SpeedLimitKbps = 500,
            ObjectLockDays = 30,
            ObjectLockMode = ObjectLockModes.Compliance,
        },
    };

    public const string ArchivePasswordId = "44444444-0000-4000-8000-000000000004";

    public static Credential ArchivePassword() => new()
    {
        Id = ArchivePasswordId,
        Kind = CredentialKinds.ArchivePassword,
        User = "Archive password: Office documents",
        HasSecret = true,
    };

    public const string SshKeyId = "55555555-0000-4000-8000-000000000005";

    public const string ServerKey = "SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU";

    /// <summary>The key the service made for the Linux server (example.org is reserved for examples).</summary>
    public static Credential SshKey() => new()
    {
        Id = SshKeyId,
        Kind = CredentialKinds.SshKey,
        User = "custos@vps.example.org",
        HasSecret = true,
        PublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOmc2Nqy7ZFj7EM3zdVr8Cnk6ZbFEtZGNwHE3+8hkOLz custos@PC01",
    };

    /// <summary>A task that takes a Linux server through custos-helper, as the wizard writes it.</summary>
    public static TaskDefinition Server()
    {
        var task = Office();
        task.Name = "Web server";
        task.Sources =
        [
            new TaskSource
            {
                Id = "r1",
                Kind = SourceKinds.Ssh,
                Path = "custos@vps.example.org:22",
                Host = "vps.example.org",
                Port = 22,
                User = "custos",
                CredentialId = SshKeyId,
                HostKeySha256 = ServerKey,
                Preset = RemotePresets.IspmanagerSites,
                OfficeIp = "203.0.113.10",
                Helper = Custos.Core.Remote.HelperSetup.Preset(RemotePresets.IspmanagerSites),
                Databases = new RemoteDatabaseChoice { Exclude = ["mariadb/old_db"] },
            },
        ];
        task.DbServers = [];
        return task;
    }

    /// <summary>A task that uses every page of the editor, with values as the editor writes them.</summary>
    public static TaskDefinition Office() => new()
    {
        Id = OfficeId,
        Revision = 3,
        UpdatedAt = Evening.AddDays(-2),
        UpdatedBy = @"local:PC01\admin",
        Name = "Office documents",
        Comment = "Made up for the window tests.",
        Priority = 5,
        BackupType = BackupTypes.Differential,
        ForceFull = new ForceFullOptions { Weekday = 7, MonthDay = -1 },
        Sources =
        [
            new TaskSource { Id = "s1", Kind = SourceKinds.Folder, Path = @"C:\Data\Office" },
            new TaskSource { Id = "s2", Kind = SourceKinds.File, Path = @"C:\Data\notes.txt" },
            new TaskSource { Id = "s3", Kind = SourceKinds.Unc, Path = @"\\server\share\docs", CredentialId = AccountId, Recursive = false },
        ],
        Filters = new TaskFilters
        {
            Include = ["*.docx", "*.xlsx"],
            Exclude = ["~$*", "*.tmp", "*.log"],
            ExcludeDirs = ["Cache", @"C:\Data\Office\Old"],
            SkipHidden = true,
            SkipTemp = false,
        },
        Destinations =
        [
            new TaskDestination { Id = "d1", Kind = DestinationKinds.Local, Path = @"D:\Backup" },
            new TaskDestination { Id = "d2", Kind = DestinationKinds.Unc, Path = @"\\nas\backup\office", CredentialId = AccountId },
            new TaskDestination { Id = "d3", Kind = DestinationKinds.Usb, Path = "Copies", UsbLabel = "BACKUP-USB" },
        ],
        Layout = new LayoutOptions { KeepTopDir = false },
        Retention = new RetentionOptions { Mode = RetentionModes.Count, KeepFull = 5 },
        Schedule = new ScheduleOptions
        {
            Type = ScheduleTypes.Weekly,
            Times = ["08:30", "20:00"],
            DaysOfWeek = [1, 3, 5],
            CatchUp = false,
        },
        Vss = new VssOptions { OnFailure = VssFailureActions.Continue },
        Copy = new CopyOptions { ReadRetries = 2 },
        LogLevel = LogLevels.Files,
    };

    public static TaskDefinition Photos() => new()
    {
        Id = PhotosId,
        Revision = 1,
        Name = "Photos",
        BackupType = BackupTypes.Full,
        Sources = [new TaskSource { Id = "s4", Kind = SourceKinds.Folder, Path = @"D:\Photos" }],
        Destinations = [new TaskDestination { Id = "d4", Kind = DestinationKinds.Usb, Path = "Photos", UsbLabel = "BACKUP-USB" }],
        Schedule = new ScheduleOptions { Type = ScheduleTypes.Monthly, Times = ["23:00"], DaysOfMonth = [1, -1] },
    };

    public static TaskDefinition Archive() => new()
    {
        Id = ArchiveId,
        Revision = 2,
        Name = "Old archive",
        Enabled = false,
        Sources = [new TaskSource { Id = "s5", Kind = SourceKinds.Folder, Path = @"E:\Archive" }],
        Destinations = [new TaskDestination { Id = "d5", Kind = DestinationKinds.Local, Path = @"F:\Copies" }],
        Schedule = new ScheduleOptions { Type = ScheduleTypes.Manual },
    };

    /// <summary>The three tasks: the office task ran well, the photos failed, the archive is off.</summary>
    /// <param name="running">The office task is running, 47 % done.</param>
    /// <param name="withoutPhotos">The photos task was deleted.</param>
    public static AgentSnapshot Snapshot(long version, bool running = false, bool withoutPhotos = false)
    {
        var snapshot = new AgentSnapshot
        {
            Version = version,
            ConfigRevision = version,
            ComputerName = "PC01",
            Tasks = [Office(), Photos(), Archive()],
            Credentials =
            [
                Account(), ArchivePassword(), SshKey(),
                ServerAccount(CredentialKinds.S3), ServerAccount(CredentialKinds.WebDav), ServerAccount(CredentialKinds.Ftp), ServerAccount(CredentialKinds.Sftp),
                CloudAccount(CredentialKinds.Dropbox), CloudAccount(CredentialKinds.YaDisk), CloudAccount(CredentialKinds.GDrive),
            ],
            States =
            [
                new TaskState
                {
                    TaskId = OfficeId,
                    LastRun = Run(OfficeId, "Office documents", RunStatuses.Success),
                    LastSuccess = Run(OfficeId, "Office documents", RunStatuses.Success),
                    NextRun = Evening.AddDays(1),
                },
                new TaskState
                {
                    TaskId = PhotosId,
                    LastRun = Run(PhotosId, "Photos", RunStatuses.Error, UsbProblem),
                    NextRun = Evening.AddDays(28),
                },
                new TaskState { TaskId = ArchiveId },
            ],
        };

        if (withoutPhotos)
        {
            snapshot.Tasks.RemoveAll(t => t.Id == PhotosId);
            snapshot.States.RemoveAll(s => s.TaskId == PhotosId);
        }

        if (running)
        {
            snapshot.States[0].Running = true;
            snapshot.Current = new CurrentRun
            {
                RunId = RunId,
                TaskId = OfficeId,
                TaskName = "Office documents",
                Trigger = RunTriggers.Manual,
                RequestedBy = @"PC01\admin",
                StartedAt = Evening.AddDays(1),
                Progress = Progress(470),
            };
        }

        return snapshot;
    }

    /// <summary>A copy in progress: the given number of bytes of 1000 done.</summary>
    public static BackupProgress Progress(long bytesDone) => new()
    {
        Phase = BackupPhases.Copy,
        BackupType = BackupTypes.Differential,
        FilesDone = 10,
        FilesTotal = 40,
        BytesDone = bytesDone,
        BytesTotal = 1000,
        CurrentFile = @"C:\Data\Office\report.docx",
    };

    /// <summary>A monitor that is not connected, holding <see cref="Snapshot"/>.</summary>
    public static ServiceMonitor Monitor()
    {
        var monitor = new ServiceMonitor();
        monitor.SimulateSnapshot(Snapshot(1));
        return monitor;
    }

    public static string Json(TaskDefinition task) => JsonSerializer.Serialize(task, ConfigJsonContext.Default.TaskDefinition);

    public static string Json(TaskDestination destination) => JsonSerializer.Serialize(destination, ConfigJsonContext.Default.TaskDestination);

    private static RunRecord Run(string taskId, string taskName, string status, string? error = null) => new()
    {
        RunId = Guid.NewGuid().ToString(),
        TaskId = taskId,
        TaskName = taskName,
        BackupType = BackupTypes.Full,
        Trigger = RunTriggers.Schedule,
        ScheduledFor = Evening,
        StartedAt = Evening,
        FinishedAt = Evening.AddMinutes(12),
        Status = status,
        SourceFiles = 1200,
        SourceBytes = 3_500_000,
        FilesProcessed = 1200,
        BytesWritten = 3_500_000,
        Errors = error is null ? 0 : 1,
        ErrorMessage = error,
        SetName = taskName + " 2026-11-03 21-00 (full)",
    };
}
