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
/// The backup engine's view of macOS: shares mounted with stored accounts, APFS snapshots, sleep prevention, USB disks
/// in /Volumes, owners and permissions of files, launchd services and the scripts of events (sections 6.3, 6.7, 6.8,
/// 6.9). The counterpart of WindowsBackupPlatform.
/// </summary>
internal sealed class MacBackupPlatform(ConfigStore config, ShadowCopies shadows) : LocalBackupPlatform
{
    // Only the 7z.so in the program folder (Custos.app/Contents/MacOS): the service runs as root and must not load a
    // library from anywhere else.
    private readonly ArchiveProvider _archives = new(Path.Combine(AppContext.BaseDirectory, Core.Archives.SevenZip.SevenZipLibrary.FileName));

    public override IArchiveProvider Archives => _archives;

    public override string ComputerName => MacComputerName.Value;

    private static readonly Lazy<string> MacComputerName = new(() =>
    {
        // The name people gave the Mac (System Settings → General → Sharing); the host name is often "Mac-mini.local".
        if (OperatingSystem.IsMacOS())
        {
            var (code, output) = Tool.Run("/usr/sbin/scutil", ["--get", "ComputerName"], TimeSpan.FromSeconds(10));
            if (code == 0 && output.Length > 0)
            {
                return output.Trim();
            }
        }

        return Environment.MachineName;
    });

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
            return ProcessSession.Instance;
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

        return new SmbSession(credential.User, credential.Domain, password);
    }

    public override ISnapshotSet CreateSnapshots(IReadOnlyCollection<string> localPaths) => shadows.Create(localPaths);

    /// <summary>A disk shows up in /Volumes under its name; a USB destination names it.</summary>
    public override string? FindVolumeByLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label) || !Directory.Exists("/Volumes"))
        {
            return null;
        }

        try
        {
            foreach (string volume in Directory.EnumerateDirectories("/Volumes"))
            {
                if (string.Equals(Path.GetFileName(volume), label.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return volume + "/";
                }
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

    public override IDisposable KeepAwake(string reason) => PowerAssertion.Create(reason);

    /// <summary>Shell scripts run through the shell even without the executable bit (section 6.9).</summary>
    public override int RunProgram(string command, string arguments, TimeSpan timeout, Action<string> output, CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(command ?? string.Empty);
        if (extension.Equals(".sh", StringComparison.OrdinalIgnoreCase) || extension.Equals(".command", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".zsh", StringComparison.OrdinalIgnoreCase))
        {
            string shell = extension.Equals(".zsh", StringComparison.OrdinalIgnoreCase) ? "/bin/zsh" : "/bin/sh";
            return base.RunProgram(shell, $"\"{command}\" {arguments}".TrimEnd(), timeout, output, cancellationToken);
        }

        return base.RunProgram(command!, arguments, timeout, output, cancellationToken);
    }

    /// <summary>
    /// Shuts the Mac down, restarts it or puts it to sleep (section 6.9). Hibernation is sleep on a Mac: it writes
    /// memory to disk on its own when the battery runs low.
    /// </summary>
    /// <exception cref="BackupException">macOS refused.</exception>
    public static void PowerAction(string action)
    {
        var (program, arguments) = action switch
        {
            AfterActions.Shutdown => ("/sbin/shutdown", new[] { "-h", "now" }),
            AfterActions.Restart => ("/sbin/shutdown", new[] { "-r", "now" }),
            _ => ("/usr/bin/pmset", new[] { "sleepnow" }),
        };
        try
        {
            var (code, output) = Tool.Run(program, arguments, TimeSpan.FromSeconds(30));
            if (code != 0)
            {
                throw new BackupException(Messages.Format(Messages.PowerFailed, output.Length > 0 ? output : $"{Path.GetFileName(program)}: {code}"));
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new BackupException(Messages.Format(Messages.PowerFailed, ex.Message), ex);
        }
    }

    public override Func<string, string?> Variables(string? profileSid) => ProfileVariables.For(profileSid);

    public override void LowerIoPriority(SafeFileHandle handle)
    {
        // macOS sets the I/O priority per thread or process, not per file; the service as a whole runs at low
        // priority during a run (SetPriority), which is what Time Machine does too.
    }

    public override byte[]? ReadSecurity(string path, bool folder) => PosixSecurity.Read(path);

    public override bool WriteSecurity(string path, bool folder, byte[] descriptor) => PosixSecurity.Write(path, descriptor);

    /// <summary>Stops or starts a launchd service by its label ("homebrew.mxcl.mysql"); a label with a domain
    /// ("gui/501/…") is used as it is (section 6.9).</summary>
    public override void ControlService(string name, bool start, TimeSpan timeout, CancellationToken cancellationToken) =>
        LaunchdServices.Control(name, start, timeout, cancellationToken);

    private static DriveInfo? VolumeOf(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            return DriveInfo.GetDrives()
                .Where(d => full == d.Name || full.StartsWith(d.Name.TrimEnd('/') + "/", StringComparison.Ordinal))
                .OrderByDescending(d => d.Name.Length)
                .FirstOrDefault(d => d.IsReady);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Owner, group and permission bits of files (section 6.8, «Сохранять права доступа»), as macOS keeps them.</summary>
internal static class PosixSecurity
{
    private const string Magic = "custos-posix-1";

    /// <summary>"custos-posix-1:501:20:420": owner, group and mode; null if they cannot be read.</summary>
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

/// <summary>Keeps the Mac from sleeping while a run goes on; <c>pmset -g assertions</c> shows it with its reason
/// (section 6.7).</summary>
internal sealed partial class PowerAssertion : IDisposable
{
    private const string IoKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private readonly uint _id;
    private bool _disposed;

    private PowerAssertion(uint id) => _id = id;

    public static IDisposable Create(string reason)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return ProcessSession.Instance;
        }

        IntPtr type = CoreFoundation.String("PreventUserIdleSystemSleep");
        IntPtr name = CoreFoundation.String(reason);
        try
        {
            uint id;
            unsafe
            {
                // Not being able to stay awake is no reason to skip the backup.
                return IOPMAssertionCreateWithName(type, 255, name, &id) == 0 ? new PowerAssertion(id) : ProcessSession.Instance;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return ProcessSession.Instance;
        }
        finally
        {
            CoreFoundation.Release(type);
            CoreFoundation.Release(name);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = IOPMAssertionRelease(_id);
    }

    [LibraryImport(IoKit)]
    private static unsafe partial int IOPMAssertionCreateWithName(IntPtr type, uint level, IntPtr name, uint* id);

    [LibraryImport(IoKit)]
    private static partial int IOPMAssertionRelease(uint id);
}

/// <summary>Stopping and starting launchd services for the events of a task (section 6.9).</summary>
internal static class LaunchdServices
{
    private const string Launchctl = "/bin/launchctl";
    private static readonly string[] PlistFolders = ["/Library/LaunchDaemons", "/Library/LaunchAgents"];

    public static void Control(string name, bool start, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string label = name.Trim();
        string target = label.Contains('/', StringComparison.Ordinal) ? label : "system/" + label;
        string plain = target[(target.LastIndexOf('/') + 1)..];
        string? plist = PlistFolders.Select(folder => Path.Combine(folder, plain + ".plist")).FirstOrDefault(File.Exists);
        bool? before = State(target);
        if (start ? before == true : before is null)
        {
            return;
        }

        (int Code, string Output) result = start
            ? before is null && plist is not null
                ? Tool.Run(Launchctl, ["bootstrap", target[..target.LastIndexOf('/')], plist])
                : Tool.Run(Launchctl, ["kickstart", target])
            : Tool.Run(Launchctl, ["bootout", target]);
        if (result.Code != 0 && (start ? State(target) is null : State(target) == true))
        {
            throw new BackupException(Messages.Format(Messages.EventServiceFailed, name, result.Output.Length > 0 ? result.Output : $"launchctl: {result.Code}"));
        }

        var limit = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool? state = State(target);
            if (start ? state == true : state != true)
            {
                return;
            }

            if (DateTime.UtcNow >= limit)
            {
                throw new BackupException(Messages.Format(Messages.EventServiceTimedOut, name, (int)timeout.TotalSeconds));
            }

            Thread.Sleep(500);
        }
    }

    /// <summary>True = running, false = loaded but not running, null = not loaded at all.</summary>
    private static bool? State(string target)
    {
        var (code, output) = Tool.Run(Launchctl, ["print", target], TimeSpan.FromSeconds(20));
        if (code != 0)
        {
            return null;
        }

        return output.Contains("state = running", StringComparison.Ordinal);
    }
}
