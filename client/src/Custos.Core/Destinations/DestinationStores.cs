using Custos.Core.Backup;
using Custos.Core.Model;

namespace Custos.Core.Destinations;

/// <summary>Makes the <see cref="DestinationStore"/> of a destination of a task and names destinations for people.</summary>
public static class DestinationStores
{
    /// <summary>Opens the store of a destination. Nothing is connected yet; the first operation does that.</summary>
    /// <param name="session">The logon to use for a share; null = the store opens one of its own and closes it.</param>
    /// <param name="account">The account of a server typed in the editor and not saved yet; null = the stored one.</param>
    /// <exception cref="BackupException">The destination cannot be used as it is set up.</exception>
    public static DestinationStore Open(IBackupPlatform platform, TaskDestination destination, INetworkSession? session = null, StoreAccount? account = null)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(destination);
        var limiter = SpeedLimiter.ForKbps(destination.SpeedLimitKbps);
        switch (destination.Kind)
        {
            case DestinationKinds.Local:
            case DestinationKinds.Usb:
                return new LocalStore(ResolveRoot(platform, destination), destination.Kind, platform, session);
            case DestinationKinds.Unc:
                string root = ResolveRoot(platform, destination);
                bool owns = session is null;
                return new LocalStore(root, destination.Kind, platform, session ?? platform.OpenSession(destination.CredentialId), owns) { Limiter = limiter };
            case DestinationKinds.Sftp:
                return new SftpStore(destination, account ?? Account(platform, destination) ?? throw new BackupException(Messages.StoreAccountMissing), limiter);
            case DestinationKinds.Ftp:
            case DestinationKinds.Ftps:
                return new FtpStore(destination, account ?? Account(platform, destination), limiter);
            case DestinationKinds.WebDav:
                return new WebDavStore(destination, account ?? Account(platform, destination), limiter);
            case DestinationKinds.S3:
                return new S3Store(destination, account ?? Account(platform, destination) ?? throw new BackupException(Messages.StoreAccountMissing), limiter);
            case DestinationKinds.GDrive:
                return new GoogleDriveStore(destination, CloudAccount(platform, destination), limiter);
            case DestinationKinds.YaDisk:
                return new YandexDiskStore(destination, CloudAccount(platform, destination), limiter);
            case DestinationKinds.Dropbox:
                return new DropboxStore(destination, CloudAccount(platform, destination), limiter);
            default:
                throw new BackupException(Messages.Format(Messages.DestinationKindNotSupported, destination.Kind));
        }
    }

    /// <summary>The folder of a local destination: its path, or for a USB disk the folder on the disk with that label.</summary>
    /// <exception cref="BackupException">The disk is not connected or the path is not absolute.</exception>
    public static string ResolveRoot(IBackupPlatform platform, TaskDestination destination)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Kind == DestinationKinds.Usb)
        {
            string label = destination.UsbLabel ?? string.Empty;
            string volume = platform.FindVolumeByLabel(label)
                ?? throw new BackupException(Messages.Format(Messages.UsbDiskNotFound, label));
            return Path.Combine(volume, DestinationKinds.UsbFolder(destination.Path));
        }

        if (string.IsNullOrWhiteSpace(destination.Path) || !Path.IsPathFullyQualified(destination.Path))
        {
            throw new BackupException(Messages.Format(Messages.PathNotAbsolute, destination.Path));
        }

        return destination.Path;
    }

    /// <summary>
    /// A destination for people, without connecting to it: a folder, "USB disk «label»", "sftp://nas.example.org/backup",
    /// "s3://bucket/folder".
    /// </summary>
    public static string Label(TaskDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        string host = (destination.Host ?? string.Empty).Trim();
        string folder = RemoteStore.CleanRoot(destination.Path);
        return destination.Kind switch
        {
            DestinationKinds.Usb => Messages.Format(Messages.UsbDiskLabel, destination.UsbLabel),
            DestinationKinds.Sftp => "sftp://" + RemoteStore.HostText(host, Port(destination, SftpStore.DefaultPort), SftpStore.DefaultPort) + "/" + folder.TrimStart('/'),
            DestinationKinds.Ftp or DestinationKinds.Ftps =>
                destination.Kind + "://" + RemoteStore.HostText(host, Port(destination, FtpStore.DefaultPort), FtpStore.DefaultPort) + "/" + folder.TrimStart('/'),
            DestinationKinds.WebDav => WebDavStore.TryServer(destination.Host, destination.Port, out var server, out string prefix)
                ? server.GetLeftPart(UriPartial.Authority) + WebDavStore.RootOf(prefix, destination.Path)
                : host + "/" + folder.TrimStart('/'),
            DestinationKinds.S3 => "s3://" + (destination.Bucket ?? string.Empty).Trim() + (folder.Trim('/').Length > 0 ? "/" + folder.Trim('/') : string.Empty),
            DestinationKinds.GDrive or DestinationKinds.YaDisk or DestinationKinds.Dropbox => CloudStore.LabelOf(destination.Kind, destination.Path),
            _ => destination.Path,
        };
    }

    /// <summary>What a destination lacks to be used at all, in words; null if nothing.</summary>
    public static string? Problem(TaskDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return destination.Kind switch
        {
            DestinationKinds.Ftp or DestinationKinds.Ftps or DestinationKinds.Sftp when string.IsNullOrWhiteSpace(destination.Host) => Messages.StoreHostMissing,
            DestinationKinds.WebDav when !WebDavStore.TryServer(destination.Host, destination.Port, out _, out _) => Messages.StoreHostMissing,
            DestinationKinds.S3 when string.IsNullOrWhiteSpace(destination.Bucket) => Messages.StoreBucketMissing,
            DestinationKinds.Sftp or DestinationKinds.S3 when string.IsNullOrWhiteSpace(destination.CredentialId) => Messages.StoreAccountMissing,
            DestinationKinds.GDrive or DestinationKinds.YaDisk or DestinationKinds.Dropbox when string.IsNullOrWhiteSpace(destination.CredentialId) =>
                Messages.Format(Messages.CloudAccountMissing, Cloud.CloudService.NameOf(destination.Kind)),
            _ => null,
        };
    }

    internal static int Port(TaskDestination destination, int usual) => destination.Port is > 0 and <= 65535 ? destination.Port.Value : usual;

    /// <summary>The connected account of a cloud drive.</summary>
    private static Cloud.CloudAccount CloudAccount(IBackupPlatform platform, TaskDestination destination) =>
        string.IsNullOrWhiteSpace(destination.CredentialId)
            ? throw new BackupException(Messages.Format(Messages.CloudAccountMissing, Cloud.CloudService.NameOf(destination.Kind)))
            : platform.GetCloudAccount(destination.CredentialId);

    private static StoreAccount? Account(IBackupPlatform platform, TaskDestination destination) =>
        string.IsNullOrWhiteSpace(destination.CredentialId) ? null : platform.GetStoreAccount(destination.CredentialId);
}
