using System.Runtime.InteropServices;
using Custos.Core;
using Custos.Core.Backup;

namespace Custos.Service.Platform;

/// <summary>
/// A network folder with a stored account on macOS (section 6.3): the share (<c>\\server\share</c> or
/// <c>//server/share</c>) is mounted with the account through the NetFS framework of macOS into
/// <see cref="DataFolders.Shares"/> the first time a path of it is used, and unmounted when the session ends. The
/// password goes straight to the framework, never onto a command line.
/// </summary>
internal sealed partial class SmbSession(string user, string? domain, string password) : INetworkSession
{
    private const string NetFs = "/System/Library/Frameworks/NetFS.framework/NetFS";
    private readonly Dictionary<string, string> _mounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private bool _disposed;

    public string? Account => string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;

    /// <summary>As NetworkLogon.Open of Windows: nothing is checked until a path of a share is used.</summary>
    public static SmbSession Open(string user, string? domain, string password) => new(user, domain, password);

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

    public string LocalPath(string path)
    {
        if (!TryParse(path, out string server, out string share, out string rest))
        {
            return path;
        }

        string mountPoint;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string key = server + "/" + share;
            if (!_mounts.TryGetValue(key, out mountPoint!))
            {
                mountPoint = Mount(server, share);
                _mounts[key] = mountPoint;
            }
        }

        return rest.Length == 0 ? mountPoint : mountPoint + "/" + rest;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (string mountPoint in _mounts.Values)
            {
                Unmount(mountPoint);
            }

            _mounts.Clear();
        }
    }

    /// <summary>"\\nas\backup\pc1\x" or "//nas/backup/pc1/x" → ("nas", "backup", "pc1/x").</summary>
    internal static bool TryParse(string path, out string server, out string share, out string rest)
    {
        server = share = rest = string.Empty;
        if (!BackupEngine.IsNetworkPath(path))
        {
            return false;
        }

        string[] parts = path.Replace('\\', '/').TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        server = parts[0];
        share = parts[1];
        rest = string.Join('/', parts.Skip(2));
        return true;
    }

    /// <summary>Removes mounts a crashed service left in <see cref="DataFolders.Shares"/>.</summary>
    public static void UnmountLeftovers()
    {
        if (!Directory.Exists(DataFolders.Shares))
        {
            return;
        }

        foreach (string folder in Directory.EnumerateDirectories(DataFolders.Shares))
        {
            Unmount(folder);
        }
    }

    private string Mount(string server, string share)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new BackupException(Messages.Format(Messages.MacShareFailed, $"//{server}/{share}", "macOS only"));
        }

        string mountPoint = Path.Combine(DataFolders.Shares, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(mountPoint);

        // SMB names a domain account "DOMAIN;user".
        string login = string.IsNullOrEmpty(domain) ? user : domain + ";" + user;
        if (string.IsNullOrEmpty(domain) && user.IndexOf('\\', StringComparison.Ordinal) is int slash and > 0)
        {
            login = user[..slash] + ";" + user[(slash + 1)..];
        }

        IntPtr url = CoreFoundation.Url($"smb://{Uri.EscapeDataString(server)}/{Uri.EscapeDataString(share)}");
        IntPtr folder = CoreFoundation.Url(new Uri(mountPoint + "/").AbsoluteUri);
        IntPtr account = CoreFoundation.String(login);
        IntPtr secret = CoreFoundation.String(password);
        IntPtr uiKey = CoreFoundation.String("UIOption");
        IntPtr noUi = CoreFoundation.String("NoUI");
        IntPtr atDirKey = CoreFoundation.String("MountAtMountDir");
        IntPtr softKey = CoreFoundation.String("SoftMount");
        IntPtr openOptions = CoreFoundation.MutableDictionary((uiKey, noUi));
        IntPtr mountOptions = CoreFoundation.MutableDictionary((atDirKey, CoreFoundation.True), (softKey, CoreFoundation.True));
        IntPtr mountedList = IntPtr.Zero;
        int result;
        try
        {
            if (url == IntPtr.Zero || folder == IntPtr.Zero)
            {
                throw new BackupException(Messages.Format(Messages.MacShareFailed, $"//{server}/{share}", "bad name"));
            }

            unsafe
            {
                result = NetFSMountURLSync(url, folder, account, secret, openOptions, mountOptions, &mountedList);
            }
        }
        finally
        {
            foreach (IntPtr value in new[] { url, folder, account, secret, uiKey, noUi, atDirKey, softKey, openOptions, mountOptions, mountedList })
            {
                CoreFoundation.Release(value);
            }
        }

        if (result != 0)
        {
            TryDelete(mountPoint);
            string reason = result switch
            {
                13 or 80 or -6600 => "access denied (user or password)",
                2 or 64 or 65 => "server not found or share missing",
                _ => "error " + result,
            };
            throw new BackupException(Messages.Format(Messages.MacShareFailed, $"//{server}/{share}", reason));
        }

        return mountPoint;
    }

    private static void Unmount(string mountPoint)
    {
        var (code, _) = Tool.Run("/sbin/umount", [mountPoint], TimeSpan.FromMinutes(1));
        if (code != 0)
        {
            Tool.Run("/sbin/umount", ["-f", mountPoint], TimeSpan.FromMinutes(1));
        }

        TryDelete(mountPoint);
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder);
        }
        catch (IOException)
        {
            // Still mounted or not empty: left for the next start.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [LibraryImport(NetFs)]
    private static unsafe partial int NetFSMountURLSync(IntPtr url, IntPtr mountPath, IntPtr user, IntPtr password, IntPtr openOptions, IntPtr mountOptions, IntPtr* mountPoints);
}
