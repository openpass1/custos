namespace Custos.Core.Backup;

/// <summary>
/// What the backup engine needs from Windows. The service supplies the real implementation (logon to shares, VSS,
/// power requests); tests and other systems use <see cref="LocalBackupPlatform"/>.
/// </summary>
public interface IBackupPlatform
{
    /// <summary>Name of this computer, for <c>{computer}</c> and the manifests.</summary>
    string ComputerName { get; }

    /// <summary>
    /// Opens a network logon for a stored credential (section 6.3). With a null id the service's own account is used.
    /// </summary>
    /// <exception cref="BackupException">The account cannot log on or has no password stored.</exception>
    INetworkSession OpenSession(string? credentialId);

    /// <summary>Creates shadow copies of the volumes holding these local paths (section 6.8).</summary>
    /// <exception cref="BackupException">VSS failed.</exception>
    ISnapshotSet CreateSnapshots(IReadOnlyCollection<string> localPaths);

    /// <summary>Root folder (like <c>E:\</c>) of the ready disk with this volume label, or null.</summary>
    string? FindVolumeByLabel(string label);

    /// <summary>Bytes the caller may still write at this path, or null if that is unknown.</summary>
    long? GetFreeSpace(string path);

    /// <summary>Keeps the computer from going to sleep until disposed (section 6.7).</summary>
    IDisposable KeepAwake(string reason);
}

/// <summary>A network logon. File operations on shares run inside <see cref="Run"/>; a file opened there stays usable
/// afterwards, so reading and writing can happen outside.</summary>
public interface INetworkSession : IDisposable
{
    /// <summary>DOMAIN\user, or null for the service's own account.</summary>
    string? Account { get; }

    void Run(Action action);

    T Run<T>(Func<T> action);
}

/// <summary>Shadow copies of some volumes. Disposing releases them.</summary>
public interface ISnapshotSet : IDisposable
{
    /// <summary>The path to read instead of <paramref name="path"/>: inside the shadow copy of its volume, or the path
    /// itself when its volume has none.</summary>
    string Map(string path);
}

/// <summary>A run cannot go on, with a message for the log.</summary>
public sealed class BackupException : Exception
{
    public BackupException()
    {
    }

    public BackupException(string message)
        : base(message)
    {
    }

    public BackupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A session with no logon of its own: the process account.</summary>
public sealed class ProcessSession : INetworkSession
{
    public static readonly ProcessSession Instance = new();

    public string? Account => null;

    public void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    public T Run<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action();
    }

    public void Dispose()
    {
    }
}

/// <summary>No shadow copies: every path is read as it is.</summary>
public sealed class NoSnapshots : ISnapshotSet
{
    public static readonly NoSnapshots Instance = new();

    public string Map(string path) => path;

    public void Dispose()
    {
    }
}

/// <summary>Platform without Windows specifics: the process account everywhere, no VSS, no sleep prevention.</summary>
public class LocalBackupPlatform : IBackupPlatform
{
    public virtual string ComputerName => Environment.MachineName;

    public virtual INetworkSession OpenSession(string? credentialId) => ProcessSession.Instance;

    public virtual ISnapshotSet CreateSnapshots(IReadOnlyCollection<string> localPaths) => NoSnapshots.Instance;

    public virtual string? FindVolumeByLabel(string label)
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && string.Equals(drive.VolumeLabel, label, StringComparison.OrdinalIgnoreCase))
                {
                    return drive.RootDirectory.FullName;
                }
            }
            catch (IOException)
            {
                // A drive that went away while we looked.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    public virtual long? GetFreeSpace(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public virtual IDisposable KeepAwake(string reason) => ProcessSession.Instance;
}
