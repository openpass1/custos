using System.Text;
using Custos.Core;
using Custos.Core.Backup;

namespace Custos.Service.Platform;

/// <summary>
/// A network folder on Linux (section 6.3): the share (<c>\\server\share</c> or <c>//server/share</c>) is mounted with
/// mount.cifs (the package cifs-utils) into <see cref="DataFolders.Shares"/> the first time a path of it is used, and
/// unmounted when the session ends. The account goes to mount.cifs in a file only root can read, deleted right after,
/// never on a command line; without an account the share is mounted as a guest.
/// </summary>
internal sealed class SmbSession : INetworkSession
{
    private readonly string? _user;
    private readonly string? _domain;
    private readonly string? _password;
    private readonly Dictionary<string, string> _mounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private bool _disposed;

    private SmbSession(string? user, string? domain, string? password)
    {
        _user = user;
        _domain = domain;
        _password = password;
    }

    public string? Account => _user is null ? null : string.IsNullOrEmpty(_domain) ? _user : _domain + "\\" + _user;

    /// <summary>As NetworkLogon.Open of Windows: nothing is checked until a path of a share is used.</summary>
    public static SmbSession Open(string user, string? domain, string password) => new(user, domain, password);

    /// <summary>A share without an account: mounted as a guest.</summary>
    public static SmbSession Guest() => new(null, null, null);

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

        foreach (var mount in MountTable.Read().Below(DataFolders.Shares).Reverse())
        {
            Unmount(mount.MountPoint);
        }

        foreach (string folder in Directory.EnumerateDirectories(DataFolders.Shares))
        {
            TryDelete(folder);
        }
    }

    /// <summary>The text of the credentials file of mount.cifs.</summary>
    internal static string CredentialsText(string user, string? domain, string password)
    {
        // "OFFICE\anna" in the user field is the domain and the user.
        if (string.IsNullOrEmpty(domain) && user.IndexOf('\\', StringComparison.Ordinal) is int slash and > 0)
        {
            domain = user[..slash];
            user = user[(slash + 1)..];
        }

        var text = new StringBuilder();
        text.Append("username=").Append(user).Append('\n');
        text.Append("password=").Append(password).Append('\n');
        if (!string.IsNullOrEmpty(domain))
        {
            text.Append("domain=").Append(domain).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>What mount.cifs said, in words people understand.</summary>
    internal static string Reason(string output, int code)
    {
        if (output.Contains("error(13)", StringComparison.Ordinal) || output.Contains("Permission denied", StringComparison.Ordinal))
        {
            return LinuxStrings.ShareAccessDenied;
        }

        if (output.Contains("error(112)", StringComparison.Ordinal) || output.Contains("error(113)", StringComparison.Ordinal)
            || output.Contains("error(115)", StringComparison.Ordinal) || output.Contains("could not resolve", StringComparison.OrdinalIgnoreCase)
            || output.Contains("error(2)", StringComparison.Ordinal) || output.Contains("error(6)", StringComparison.Ordinal))
        {
            return LinuxStrings.ShareNotFound;
        }

        return Tool.Reason(output, "mount.cifs", code);
    }

    private string Mount(string server, string share)
    {
        string unc = $"//{server}/{share}";
        if (Tool.Find("mount.cifs") is null)
        {
            throw new BackupException(Messages.Format(Messages.MacShareFailed, unc, LinuxStrings.Format(LinuxStrings.ReasonToolMissing, "cifs-utils")));
        }

        if (_password is not null && (_password.Contains('\n', StringComparison.Ordinal) || _user!.Contains('\n', StringComparison.Ordinal)))
        {
            throw new BackupException(Messages.Format(Messages.MacShareFailed, unc, "line break in the account"));
        }

        Directory.CreateDirectory(DataFolders.Shares);
        string mountPoint = Path.Combine(DataFolders.Shares, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(mountPoint);

        // The files belong to root, as everything the service writes; soft: a share that stops answering fails the
        // run instead of hanging it.
        string options = "uid=0,gid=0,file_mode=0600,dir_mode=0700,soft,nobrl,iocharset=utf8";
        string? credentials = null;
        try
        {
            if (_password is not null)
            {
                credentials = Path.Combine(DataFolders.Config, "smb-" + Guid.NewGuid().ToString("N")[..12]);
                using (var file = new FileStream(credentials, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                }))
                {
                    file.Write(Encoding.UTF8.GetBytes(CredentialsText(_user!, _domain, _password)));
                }

                options = "credentials=" + credentials + "," + options;
            }
            else
            {
                options = "guest," + options;
            }

            var (code, output) = Tool.Run("mount", ["-t", "cifs", unc, mountPoint, "-o", options], TimeSpan.FromMinutes(2));
            if (code != 0)
            {
                TryDelete(mountPoint);
                throw new BackupException(Messages.Format(Messages.MacShareFailed, unc, Reason(output, code)));
            }
        }
        finally
        {
            if (credentials is not null)
            {
                File.Delete(credentials);
            }
        }

        return mountPoint;
    }

    private static void Unmount(string mountPoint)
    {
        var (code, _) = Tool.Run("umount", [mountPoint], TimeSpan.FromMinutes(1));
        if (code != 0)
        {
            Tool.Run("umount", ["-l", mountPoint], TimeSpan.FromMinutes(1));
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
}
