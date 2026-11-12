using System.Net.Sockets;
using System.Text;
using Custos.Core.Model;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Custos.Core.Destinations;

/// <summary>
/// A folder on an SFTP server (section 6.3). The key of the server is pinned in the destination, as for an SSH source:
/// an unknown or changed key stops the run before anything is sent. A file is uploaded with requests sent ahead of the
/// answers, which is what makes SFTP fast; 7z archives go the same way and their header is put in place afterwards.
/// </summary>
public sealed class SftpStore : RemoteStore
{
    public const int DefaultPort = 22;

    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

    private readonly string _host;
    private readonly int _port;
    private readonly StoreAccount _account;
    private readonly string? _pinned;
    private readonly string _server;
    private SftpClient? _client;
    private PrivateKeyFile? _keyFile;
    private bool _plainRename;

    public SftpStore(TaskDestination destination, StoreAccount account, SpeedLimiter? limiter = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        _host = (destination.Host ?? string.Empty).Trim();
        _port = destination.Port is > 0 and <= 65535 ? destination.Port.Value : DefaultPort;
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _pinned = string.IsNullOrWhiteSpace(destination.ServerFingerprint) ? null : destination.ServerFingerprint.Trim();
        _server = HostText(_host, _port, DefaultPort);
        Root = CleanRoot(destination.Path);
        Limiter = limiter;
    }

    public override string Kind => DestinationKinds.Sftp;

    public override string Root { get; }

    public override string Label => "sftp://" + _server + "/" + Root.TrimStart('/');

    public override string? Account => _account.User;

    public override string CacheKey => "sftp://" + _account.User + "@" + _server;

    public override bool CanSeek => true;

    private SftpClient Client
    {
        get
        {
            if (_client is { IsConnected: true })
            {
                return _client;
            }

            Disconnect();
            _client = Connect();
            return _client;
        }
    }

    public override void CreateFolder(string path) => CreateFolderChain(path, FolderExists, one => Run(one, client =>
    {
        try
        {
            client.CreateDirectory(Sftp(one));
        }
        catch (SshException) when (IsFolder(client, one))
        {
            // Someone else made it meanwhile.
        }

        return 0;
    }));

    public override bool FolderExists(string path)
    {
        if (IsKnownFolder(path))
        {
            return true;
        }

        bool exists = Run(path, client => IsFolder(client, path));
        if (exists)
        {
            RememberFolder(path);
        }

        return exists;
    }

    public override bool FileExists(string path) => Run(path, client =>
    {
        try
        {
            return !client.GetAttributes(Sftp(path)).IsDirectory;
        }
        catch (SftpPathNotFoundException)
        {
            return false;
        }
    });

    public override List<StoreEntry> List(string folder) => Run(folder, client =>
    {
        var result = new List<StoreEntry>();
        try
        {
            foreach (var file in client.ListDirectory(Sftp(folder)))
            {
                if (file.Name is "." or "..")
                {
                    continue;
                }

                // A link is a file here: it is removed, never followed.
                bool isFolder = file.IsDirectory && !file.IsSymbolicLink;
                result.Add(new StoreEntry(Combine(folder, file.Name), file.Name, isFolder, isFolder ? 0 : file.Length, file.LastWriteTimeUtc));
            }
        }
        catch (SftpPathNotFoundException)
        {
            // No such folder: nothing in it.
        }

        return result;
    });

    public override void DeleteFolder(string path)
    {
        ForgetFolders();
        if (!Run(path, client => IsFolder(client, path)))
        {
            return;
        }

        DeleteTree(path);
    }

    public override bool RemoveEmptyFolder(string path)
    {
        ForgetFolders();
        if (!Run(path, client => IsFolder(client, path)))
        {
            return true;
        }

        if (List(path).Count > 0)
        {
            return false;
        }

        Run(path, client =>
        {
            client.DeleteDirectory(Sftp(path));
            return 0;
        });
        return true;
    }

    public override void MoveFolder(string source, string target)
    {
        ForgetFolders();
        Run(source, client =>
        {
            client.RenameFile(Sftp(source), Sftp(target));
            return 0;
        });
    }

    public override Stream OpenRead(string path) => Run(path, client =>
        (Stream)new GuardedStream(client.OpenRead(Sftp(path)), ex => Translate(ex, path)));

    public override long GetFileSize(string path) => Run(path, client =>
    {
        var attributes = client.GetAttributes(Sftp(path));
        return attributes.IsDirectory ? throw new FileNotFoundException(null, path) : attributes.Size;
    });

    public override StoreWriter BeginWrite(string path, string tempSuffix, StoreWriteMode mode = StoreWriteMode.Blocks)
    {
        var client = Run(path, c => c);
        return new UploadWriter(
            this,
            path,
            tempSuffix,
            (source, temp) => client.UploadFile(source, Sftp(temp), true),
            mode == StoreWriteMode.Seekable ? OpenForChange : null);
    }

    public override void DeleteFile(string path) => Run(path, client =>
    {
        try
        {
            client.DeleteFile(Sftp(path));
        }
        catch (SftpPathNotFoundException)
        {
            // Not there: nothing to delete.
        }

        return 0;
    });

    public override void MoveFile(string source, string target) => Run(source, client =>
    {
        if (!_plainRename)
        {
            try
            {
                // Replaces the file in one step where the server knows how (OpenSSH does).
                client.RenameFile(Sftp(source), Sftp(target), isPosix: true);
                return 0;
            }
            catch (NotSupportedException)
            {
                _plainRename = true;
            }
        }

        try
        {
            client.DeleteFile(Sftp(target));
        }
        catch (SftpPathNotFoundException)
        {
            // Nothing to replace.
        }

        client.RenameFile(Sftp(source), Sftp(target));
        return 0;
    });

    public override void SetFileTimes(string path, DateTime createdUtc, DateTime modifiedUtc, FileAttributes attributes)
    {
        try
        {
            Run(path, client =>
            {
                client.SetLastWriteTimeUtc(Sftp(path), modifiedUtc);
                return 0;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only for the eyes of someone who looks into the folder; the manifest has the times.
        }
    }

    public override StoreSpace GetSpace(string path)
    {
        try
        {
            return Run(path, client =>
            {
                var status = client.GetStatus(Sftp(path));
                return new StoreSpace(
                    checked((long)(status.TotalBlocks * status.BlockSize)),
                    checked((long)(status.AvailableBlocks * status.BlockSize)));
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or OverflowException)
        {
            // The server does not tell (no statvfs@openssh.com).
            return default;
        }
    }

    protected internal override Exception? Translate(Exception exception, string? path)
    {
        string text = Messages.Format(Messages.StoreFailed, _server, exception.Message);
        return exception switch
        {
            SftpPathNotFoundException => new FileNotFoundException(text, path, exception),
            SftpPermissionDeniedException => new UnauthorizedAccessException(text, exception),
            SshException or SocketException or ObjectDisposedException or TimeoutException => new StoreException(text, exception),
            _ => null,
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Disconnect();
        }

        base.Dispose(disposing);
    }

    /// <summary>The top folder of a relative path is the one the server puts the account in.</summary>
    private static string Sftp(string path) => path.Length == 0 ? "." : path;

    private static bool IsFolder(SftpClient client, string path)
    {
        try
        {
            return client.GetAttributes(Sftp(path)).IsDirectory;
        }
        catch (SftpPathNotFoundException)
        {
            return false;
        }
    }

    private Stream OpenForChange(string path) => Run(path, client =>
        (Stream)new GuardedStream(client.Open(Sftp(path), FileMode.Open, FileAccess.Write), ex => Translate(ex, path)));

    private void DeleteTree(string folder)
    {
        foreach (var entry in List(folder))
        {
            Cancellation.ThrowIfCancellationRequested();
            if (entry.IsFolder)
            {
                DeleteTree(entry.Path);
            }
            else
            {
                DeleteFile(entry.Path);
            }
        }

        Run(folder, client =>
        {
            client.DeleteDirectory(Sftp(folder));
            return 0;
        });
    }

    private T Run<T>(string? path, Func<SftpClient, T> action)
    {
        Cancellation.ThrowIfCancellationRequested();
        try
        {
            return action(Client);
        }
        catch (Exception ex) when (Translate(ex, path) is { } failure)
        {
            throw failure;
        }
    }

    private SftpClient Connect()
    {
        if (_host.Length == 0)
        {
            throw new StoreException(Messages.StoreHostMissing);
        }

        var methods = new List<AuthenticationMethod>();
        if (_account.Secret.TrimStart().StartsWith("-----BEGIN", StringComparison.Ordinal))
        {
            try
            {
                _keyFile = new PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(_account.Secret)));
            }
            catch (Exception ex) when (ex is SshException or InvalidOperationException or FormatException or NotSupportedException or ArgumentException)
            {
                throw new StoreException(Messages.Format(Messages.StoreKeyBad, ex.Message), ex);
            }

            methods.Add(new PrivateKeyAuthenticationMethod(_account.User, _keyFile));
        }
        else
        {
            methods.Add(new PasswordAuthenticationMethod(_account.User, _account.Secret));

            // Servers that ask for the password "by keyboard" get the same one.
            var typed = new KeyboardInteractiveAuthenticationMethod(_account.User);
            typed.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    prompt.Response = _account.Secret;
                }
            };
            methods.Add(typed);
        }

        var info = new ConnectionInfo(_host, _port, _account.User, [.. methods]) { Timeout = ConnectTimeout };
        var client = new SftpClient(info) { KeepAliveInterval = KeepAlive, OperationTimeout = RequestTimeout };
        string? seen = null;
        client.HostKeyReceived += (_, e) =>
        {
            seen = "SHA256:" + e.FingerPrintSHA256.TrimEnd('=');
            e.CanTrust = _pinned is not null && string.Equals(seen, _pinned, StringComparison.Ordinal);
        };
        try
        {
            client.ConnectAsync(Cancellation).GetAwaiter().GetResult();
            SeenFingerprint = seen;
            return client;
        }
        catch (Exception ex) when (ex is SshException or SocketException or TimeoutException or IOException or InvalidOperationException)
        {
            client.Dispose();
            if (seen is not null && !string.Equals(seen, _pinned, StringComparison.Ordinal))
            {
                throw _pinned is null
                    ? new ServerTrustException(Messages.Format(Messages.StoreHostKeyNew, _server, seen), null, seen)
                    : new ServerTrustException(Messages.Format(Messages.StoreHostKeyChanged, _server, _pinned, seen), _pinned, seen);
            }

            throw ex is SshAuthenticationException
                ? new StoreException(Messages.Format(Messages.StoreLoginRefused, _server, _account.User), ex)
                : new StoreException(Messages.Format(Messages.StoreUnreachable, _server, ex.Message), ex);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            throw;
        }
    }

    private void Disconnect()
    {
        try
        {
            _client?.Dispose();
        }
        catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException or IOException)
        {
            // It was gone already.
        }

        _client = null;
        _keyFile?.Dispose();
        _keyFile = null;
    }
}
