using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Custos.Core;
using Custos.Core.Agent;
using Custos.Core.Archives;
using Custos.Core.Backup;
using Custos.Core.Databases;
using Custos.Core.Destinations;
using Custos.Core.Model;
using Microsoft.Win32.SafeHandles;

namespace Custos.Service.Platform;

/// <summary>
/// The backup engine's view of Linux: shares mounted with stored accounts, snapshots of LVM, btrfs and ZFS, the mounts
/// of the system and of other computers left out of a scan, databases of this computer without a password, disks by
/// their label, owners and permissions of files, systemd services and the scripts of events (sections 6.3, 6.7, 6.8,
/// 6.9). The counterpart of WindowsBackupPlatform.
/// </summary>
internal sealed class LinuxBackupPlatform(ConfigStore config, ShadowCopies shadows) : LocalBackupPlatform
{
    private static readonly TimeSpan MountCacheTime = TimeSpan.FromSeconds(30);

    // Only the 7z.so in the program folder (/opt/custos): the service runs as root and must not load a library from
    // anywhere else.
    private readonly ArchiveProvider _archives = new(Path.Combine(AppContext.BaseDirectory, Core.Archives.SevenZip.SevenZipLibrary.FileName));
    private readonly Lock _mountLock = new();
    private MountTable? _mounts;
    private Microsoft.Win32.SafeHandles.SafeFileHandle? _mountWatch;
    private DateTime _mountsRead;

    public override IArchiveProvider Archives => _archives;

    public override string GetArchivePassword(string credentialId)
    {
        var credential = config.GetCredential(credentialId);
        if (credential is null || credential.Kind != CredentialKinds.ArchivePassword)
        {
            throw new BackupException(Messages.ArchivePasswordMissing);
        }

        try
        {
            return config.GetPassword(credentialId) is { Length: > 0 } password ? password : throw new BackupException(Messages.ArchivePasswordMissing);
        }
        catch (ConfigException ex)
        {
            throw new BackupException(ex.Message, ex);
        }
    }

    public override DbAccount GetDbAccount(string credentialId)
    {
        var credential = config.GetCredential(credentialId);
        if (credential is null || credential.Kind != CredentialKinds.Db)
        {
            throw new BackupException(Messages.DbAccountMissing);
        }

        try
        {
            return new DbAccount(credential.User, config.GetPassword(credentialId) ?? string.Empty);
        }
        catch (ConfigException ex)
        {
            throw new BackupException(ex.Message, ex);
        }
    }

    /// <summary>
    /// The client programs of the server: from the folder of the task, or where this computer has them when the task
    /// names none (made on the site). A server of this computer without an account is reached as root reaches it.
    /// </summary>
    public override IDbClient CreateDbClient(TaskDatabaseServer server, DbAccount? account)
    {
        ArgumentNullException.ThrowIfNull(server);
        var actual = server;
        if ((string.IsNullOrWhiteSpace(server.BinDir) || !Directory.Exists(server.BinDir)) && DbFinder.ToolFolder(server.Engine) is { } folder)
        {
            actual = new TaskDatabaseServer
            {
                Id = server.Id,
                Engine = server.Engine,
                BinDir = folder,
                Host = server.Host,
                Port = server.Port,
                CredentialId = server.CredentialId,
                AllDatabases = server.AllDatabases,
                Exclude = server.Exclude,
                Include = server.Include,
                DataDir = server.DataDir,
            };
        }

        var local = account is null or { User.Length: 0 } ? DbFinder.LocalAccess(actual) : null;
        return new ProcessDbClient(actual, account, consoleEncoding: null, local);
    }

    public override byte[] GetSshKey(string credentialId)
    {
        var credential = config.GetCredential(credentialId);
        if (credential is null || credential.Kind != CredentialKinds.SshKey)
        {
            throw new BackupException(Messages.RemoteKeyMissing);
        }

        try
        {
            return config.GetSshKey(credentialId);
        }
        catch (ConfigException ex)
        {
            throw new BackupException(ex.Message, ex);
        }
    }

    public override INetworkSession OpenSession(string? credentialId)
    {
        if (credentialId is null)
        {
            return SmbSession.Guest();
        }

        var credential = config.GetCredential(credentialId) ?? throw new BackupException(Messages.CredentialNotFound);
        string? password;
        try
        {
            password = config.GetPassword(credentialId);
        }
        catch (ConfigException ex)
        {
            throw new BackupException(ex.Message, ex);
        }

        if (password is null)
        {
            throw new BackupException(Messages.Format(Messages.CredentialNoPassword, credential.DisplayName));
        }

        return SmbSession.Open(credential.User, credential.Domain, password);
    }

    public override ISnapshotSet CreateSnapshots(IReadOnlyCollection<string> localPaths) => shadows.Create(localPaths);

    public override bool IsRegularSourceFile(string path) => Native.IsRegularFile(path);

    public override FileStream OpenSourceFile(string path, int bufferSize)
    {
        using var file = Native.open(path, Native.PathOnly | Native.CloseOnExec);
        if (file.IsInvalid)
        {
            throw Native.SourceFileError(path, Marshal.GetLastPInvokeError());
        }

        return Native.OpenRegularFile(file, bufferSize);
    }

    public override string CanonicalLocalPath(string path) => Native.ResolvePath(path);

    /// <summary>
    /// The mounts of the system (/proc, /sys, /run …), of other computers (NFS, SMB) and the folders CUSTOS mounts
    /// its snapshots and shares in are not read when met inside a source; a path inside a snapshot view stands for
    /// the same path of the real tree.
    /// </summary>
    public override string? SkipFolder(string readPath)
    {
        ArgumentNullException.ThrowIfNull(readPath);
        string path = ShadowCopies.Original(readPath);
        if (DataFolders.IsMountFolder(path) || Path.GetFileName(path).StartsWith(ShadowCopies.BtrfsPrefix, StringComparison.Ordinal))
        {
            return LinuxStrings.SkipCustos;
        }

        if (Mounts().At(path) is not { } mount)
        {
            return null;
        }

        return MountTable.IsSystem(mount.FileSystem) ? LinuxStrings.Format(LinuxStrings.SkipSystem, mount.FileSystem)
            : MountTable.IsNetwork(mount.FileSystem) ? LinuxStrings.Format(LinuxStrings.SkipNetwork, mount.FileSystem, mount.Source)
            : null;
    }

    /// <summary>A USB destination names its disk by label: the disk is found among the mounted ones by
    /// /dev/disk/by-label (a server mounts disks itself, CUSTOS does not).</summary>
    public override string? FindVolumeByLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        const string byLabel = "/dev/disk/by-label";
        try
        {
            if (!Directory.Exists(byLabel))
            {
                return null;
            }

            foreach (string link in Directory.EnumerateFileSystemEntries(byLabel))
            {
                if (!string.Equals(UdevName(Path.GetFileName(link)), label.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string device = File.ResolveLinkTarget(link, returnFinalTarget: true)?.FullName ?? link;
                var mount = MountTable.Read().Entries
                    .Where(e => e.Source == device || ResolveDevice(e.Source) == device)
                    .OrderBy(e => e.MountPoint.Length)
                    .FirstOrDefault();
                return mount is null ? null : mount.MountPoint.TrimEnd('/') + "/";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not readable now: as if the disk were not there.
        }

        return null;
    }

    public override long? GetFreeSpace(string path) => VolumeOf(path)?.AvailableFreeSpace;

    public override long? GetTotalSpace(string path) => VolumeOf(path)?.TotalSize;

    public override StoreAccount GetStoreAccount(string credentialId)
    {
        var credential = config.GetCredential(credentialId);
        if (credential is null
            || credential.Kind is not (CredentialKinds.Ftp or CredentialKinds.Sftp or CredentialKinds.WebDav or CredentialKinds.S3))
        {
            throw new BackupException(Messages.StoreAccountMissing);
        }

        try
        {
            return new StoreAccount(credential.User, config.GetPassword(credentialId) ?? string.Empty);
        }
        catch (ConfigException ex)
        {
            throw new BackupException(ex.Message, ex);
        }
    }

    public override Core.Cloud.CloudAccount GetCloudAccount(string credentialId)
    {
        var credential = config.GetCredential(credentialId);
        if (credential is null || !CredentialKinds.IsCloud(credential.Kind))
        {
            throw new BackupException(Messages.Format(Messages.CloudAccountMissing, credential is null ? "?" : Core.Cloud.CloudService.NameOf(credential.Kind)));
        }

        string token;
        try
        {
            token = config.GetPassword(credentialId) ?? string.Empty;
        }
        catch (ConfigException ex)
        {
            throw new BackupException(ex.Message, ex);
        }

        var service = Core.Cloud.CloudService.Of(credential.Kind);
        var app = Core.Cloud.CloudApps.ForToken(credential.Kind, credential.ClientId, config.GetCloudApps());
        var tokens = new Core.Cloud.OAuthTokens(credentialId, credential.User, service, app, token, replaced => config.UpdateCloudToken(credentialId, replaced));
        return new Core.Cloud.CloudAccount(credential.User, tokens);
    }

    public override IDisposable KeepAwake(string reason) => SleepInhibitor.Create(reason);

    /// <summary>Shell scripts run through their shell even without the executable bit (section 6.9).</summary>
    public override int RunProgram(string command, string arguments, TimeSpan timeout, Action<string> output, CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(command ?? string.Empty);
        if (extension is ".sh" or ".bash")
        {
            string shell = extension == ".bash" && File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
            return base.RunProgram(shell, $"\"{command}\" {arguments}".TrimEnd(), timeout, output, cancellationToken);
        }

        return base.RunProgram(command!, arguments, timeout, output, cancellationToken);
    }

    /// <summary>Shuts the computer down, restarts it, puts it to sleep or hibernates it (section 6.9).</summary>
    /// <exception cref="BackupException">The system refused.</exception>
    public static void PowerAction(string action)
    {
        string[] arguments = action switch
        {
            AfterActions.Shutdown => ["poweroff"],
            AfterActions.Restart => ["reboot"],
            AfterActions.Hibernate => ["hibernate"],
            _ => ["suspend"],
        };
        var (code, output) = Tool.Run("systemctl", arguments, TimeSpan.FromSeconds(60));
        if (code != 0)
        {
            throw new BackupException(Messages.Format(Messages.PowerFailed, Tool.Reason(output, "systemctl", code)));
        }
    }

    public override Func<string, string?> Variables(string? profileSid) => ProfileVariables.For(profileSid);

    public override void LowerIoPriority(SafeFileHandle handle)
    {
        // Linux sets the I/O priority per process: the unit of the service has the lowest of the usual class
        // (IOSchedulingPriority=7), and the whole service runs below normal while a run lasts (SetPriority).
    }

    public override byte[]? ReadSecurity(string path, bool folder) => PosixSecurity.Read(path);

    public override bool WriteSecurity(string path, bool folder, byte[] descriptor) => PosixSecurity.Write(path, descriptor);

    /// <summary>Stops or starts a systemd service by its name ("mysql", "nginx.service") (section 6.9).</summary>
    public override void ControlService(string name, bool start, TimeSpan timeout, CancellationToken cancellationToken) =>
        SystemdServices.Control(name, start, timeout, cancellationToken);

    /// <summary>"Backup\x20disk" → "Backup disk": udev writes unusual characters of a label as \xHH.</summary>
    internal static string UdevName(string name)
    {
        if (!name.Contains("\\x", StringComparison.Ordinal))
        {
            return name;
        }

        var bytes = new List<byte>();
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] == '\\' && i + 3 < name.Length + 0 && name[i + 1] == 'x'
                && byte.TryParse(name.AsSpan(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
            {
                bytes.Add(value);
                i += 3;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(name[i].ToString()));
            }
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }

    private static string? ResolveDevice(string source)
    {
        try
        {
            return source.StartsWith("/dev/", StringComparison.Ordinal) ? File.ResolveLinkTarget(source, returnFinalTarget: true)?.FullName ?? source : source;
        }
        catch (IOException)
        {
            return source;
        }
    }

    /// <summary>The mounts, read again as soon as something is mounted or unmounted (a folder of a source may become a
    /// mount between two runs, or during one).</summary>
    private MountTable Mounts()
    {
        lock (_mountLock)
        {
            if (_mountWatch is null)
            {
                try
                {
                    _mountWatch = File.OpenHandle("/proc/self/mountinfo");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Then only the age of the table says when to read it again.
                }
            }

            bool changed = _mountWatch is not null && Native.MountsChanged(_mountWatch);
            if (_mounts is null || changed || DateTime.UtcNow - _mountsRead > MountCacheTime)
            {
                _mounts = MountTable.Read();
                _mountsRead = DateTime.UtcNow;
            }

            return _mounts;
        }
    }

    private static DriveInfo? VolumeOf(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            for (string? folder = full; folder is not null; folder = Path.GetDirectoryName(folder))
            {
                if (Directory.Exists(folder))
                {
                    // statvfs of the folder: the numbers of the file system it is on.
                    var drive = new DriveInfo(folder);
                    return drive.IsReady ? drive : null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unknown.
        }

        return null;
    }
}

/// <summary>Owner, group and permission bits of files (section 6.8, «Сохранять права доступа»), the format of CUSTOS
/// for macOS: a copy made on one restores on the other.</summary>
internal static class PosixSecurity
{
    private const string Magic = "custos-posix-1";

    /// <summary>"custos-posix-1:1000:1000:420": owner, group and mode; null if they cannot be read.</summary>
    public static byte[]? Read(string path) =>
        Native.Stat(path) is { } stat
            ? Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{Magic}:{stat.Uid}:{stat.Gid}:{stat.Mode & 0xFFF}"))
            : null;

    /// <returns>False for what another system wrote (NTFS permissions of a Windows set): nothing to do here.</returns>
    /// <exception cref="BackupException">They could not be set.</exception>
    public static bool Write(string path, byte[] descriptor)
    {
        string text = Encoding.ASCII.GetString(descriptor);
        string[] parts = text.Split(':');
        if (parts.Length != 4 || parts[0] != Magic
            || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint uid)
            || !uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint gid)
            || !uint.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out uint mode))
        {
            return false;
        }

        if (Native.lchown(path, uid, gid) != 0)
        {
            int error = Marshal.GetLastPInvokeError();

            // Only root may give a file away; a share may not keep owners at all. The permissions still apply.
            if (Native.IsRoot && error != 1)
            {
                throw new BackupException(Messages.Format(Messages.MacOwnerNotSet, path, error));
            }
        }

        try
        {
            // The permissions of a symbolic link mean nothing on Linux, and setting them would change its target.
            if (!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                File.SetUnixFileMode(path, (UnixFileMode)(mode & 0xFFF));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BackupException(Messages.Format(Messages.MacOwnerNotSet, path, ex.Message), ex);
        }

        return true;
    }
}

/// <summary>Keeps the computer from sleeping while a run goes on, the systemd way: <c>systemd-inhibit</c> holds a
/// lock that <c>systemd-inhibit --list</c> shows with its reason (section 6.7).</summary>
internal sealed class SleepInhibitor : IDisposable
{
    private readonly Process _process;
    private bool _disposed;

    private SleepInhibitor(Process process) => _process = process;

    public static IDisposable Create(string reason)
    {
        if (Tool.Find("systemd-inhibit") is not { } inhibit || Tool.Find("sleep") is not { } sleep)
        {
            return ProcessSession.Instance;
        }

        try
        {
            var start = new ProcessStartInfo(inhibit)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in new[] { "--what=sleep:idle", "--who=CUSTOS", "--why=" + reason, "--mode=block", sleep, "infinity" })
            {
                start.ArgumentList.Add(argument);
            }

            // Not being able to stay awake is no reason to skip the backup.
            return Process.Start(start) is { } process ? new SleepInhibitor(process) : ProcessSession.Instance;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ProcessSession.Instance;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It ended already.
        }

        _process.Dispose();
    }
}

/// <summary>Stopping and starting systemd services for the events of a task (section 6.9).</summary>
internal static class SystemdServices
{
    public static void Control(string name, bool start, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string unit = name.Trim();
        if (unit.Length == 0 || unit.StartsWith('-'))
        {
            throw new BackupException(Messages.Format(Messages.EventServiceFailed, name, "no name"));
        }

        if (start ? IsActive(unit) : !IsActive(unit))
        {
            return;
        }

        // --no-block: the wait below has the time limit of the task, not systemctl's own.
        var (code, output) = Tool.Run("systemctl", [start ? "start" : "stop", "--no-block", "--", unit], TimeSpan.FromMinutes(1));
        if (code != 0)
        {
            throw new BackupException(Messages.Format(Messages.EventServiceFailed, name, Tool.Reason(output, "systemctl", code)));
        }

        var limit = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string state = State(unit);
            if (start ? state == "active" : state is "inactive" or "failed")
            {
                return;
            }

            if (start && state == "failed")
            {
                throw new BackupException(Messages.Format(Messages.EventServiceFailed, name, state));
            }

            if (DateTime.UtcNow >= limit)
            {
                throw new BackupException(Messages.Format(Messages.EventServiceTimedOut, name, (int)timeout.TotalSeconds));
            }

            Thread.Sleep(500);
        }
    }

    private static bool IsActive(string unit) => State(unit) == "active";

    /// <summary>active, inactive, activating, deactivating, failed …</summary>
    private static string State(string unit)
    {
        var (_, _) = Tool.Run("systemctl", ["is-active", "--", unit], out string output, TimeSpan.FromSeconds(30));
        return output.Trim();
    }
}
