using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Custos.Core.Model;
using FluentFTP;
using FluentFTP.Exceptions;

namespace Custos.Core.Destinations;

/// <summary>
/// A folder on an FTP server, plain or over TLS (kinds <c>ftp</c> and <c>ftps</c>, section 6.3). FTP sends one file at
/// a time per connection and nothing else meanwhile, so commands have a connection of their own and every upload or
/// download takes another one, kept for the next file.
/// </summary>
public sealed class FtpStore : RemoteStore
{
    public const int DefaultPort = 21;

    /// <summary>The port where TLS starts with the connection ("implicit"), not with a command.</summary>
    public const int ImplicitTlsPort = 990;

    private const int IdleTransfers = 2;

    private readonly string _host;
    private readonly int _port;
    private readonly bool _tls;
    private readonly StoreAccount? _account;
    private readonly ServerTrust _trust;
    private readonly string _server;
    private readonly Lock _sync = new();
    private readonly Stack<FtpClient> _idle = new();
    private FtpClient? _commands;
    private bool _disposed;

    /// <param name="account">Null = anonymous.</param>
    public FtpStore(TaskDestination destination, StoreAccount? account, SpeedLimiter? limiter = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        _tls = destination.Kind == DestinationKinds.Ftps;
        _host = (destination.Host ?? string.Empty).Trim();
        _port = destination.Port is > 0 and <= 65535 ? destination.Port.Value : DefaultPort;
        _account = account;
        _trust = new ServerTrust(destination.ServerFingerprint);
        _server = HostText(_host, _port, DefaultPort);
        Root = CleanRoot(destination.Path);
        Limiter = limiter;
    }

    public override string Kind => _tls ? DestinationKinds.Ftps : DestinationKinds.Ftp;

    public override string Root { get; }

    public override string Label => (_tls ? "ftps://" : "ftp://") + _server + "/" + Root.TrimStart('/');

    public override string? Account => _account?.User;

    public override string CacheKey => Kind + "://" + _account?.User + "@" + _server;

    public override void CreateFolder(string path) => CreateFolderChain(path, FolderExists, one => Run(one, client =>
    {
        try
        {
            client.CreateDirectory(Ftp(one), false);
        }
        catch (FtpCommandException) when (client.DirectoryExists(Ftp(one)))
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

        bool exists = path.Length == 0 || Run(path, client => client.DirectoryExists(Ftp(path)));
        if (exists)
        {
            RememberFolder(path);
        }

        return exists;
    }

    public override bool FileExists(string path) => Run(path, client => client.FileExists(Ftp(path)));

    public override List<StoreEntry> List(string folder) => Run(folder, client =>
    {
        var result = new List<StoreEntry>();
        FtpListItem[] items;
        try
        {
            items = client.GetListing(Ftp(folder), FtpListOption.AllFiles);
        }
        catch (FtpCommandException) when (!client.DirectoryExists(Ftp(folder)))
        {
            // No such folder: nothing in it.
            return result;
        }

        foreach (var item in items)
        {
            if (item.Name is "." or ".." or "")
            {
                continue;
            }

            // A link is a file here: it is removed, never followed.
            bool isFolder = item.Type == FtpObjectType.Directory;
            result.Add(new StoreEntry(Combine(folder, item.Name), item.Name, isFolder, isFolder ? 0 : Math.Max(0, item.Size), item.Modified));
        }

        return result;
    });

    public override void DeleteFolder(string path)
    {
        ForgetFolders();
        if (Run(path, client => client.DirectoryExists(Ftp(path))))
        {
            DeleteTree(path);
        }
    }

    public override bool RemoveEmptyFolder(string path)
    {
        ForgetFolders();
        if (!Run(path, client => client.DirectoryExists(Ftp(path))))
        {
            return true;
        }

        if (List(path).Count > 0)
        {
            return false;
        }

        RemoveFolder(path);
        return true;
    }

    public override void MoveFolder(string source, string target)
    {
        ForgetFolders();
        Run(source, client =>
        {
            client.Rename(Ftp(source), Ftp(target));
            return 0;
        });
    }

    public override Stream OpenRead(string path)
    {
        long length = GetFileSize(path);
        return new RangeReadStream(length, offset => OpenAt(path, offset, length));
    }

    public override long GetFileSize(string path) => Run(path, client =>
    {
        long size = client.GetFileSize(Ftp(path), -1);
        if (size >= 0)
        {
            return size;
        }

        // A server without SIZE: the listing of the folder knows.
        var item = client.GetObjectInfo(Ftp(path), false);
        return item is { Type: FtpObjectType.File } ? Math.Max(0, item.Size) : throw new FileNotFoundException(null, path);
    });

    public override StoreWriter BeginWrite(string path, string tempSuffix, StoreWriteMode mode = StoreWriteMode.Blocks)
    {
        if (mode == StoreWriteMode.Seekable)
        {
            throw new NotSupportedException();
        }

        var client = TakeTransfer();
        return new UploadWriter(this, path, tempSuffix, (source, temp) =>
        {
            try
            {
                var status = client.UploadStream(source, Ftp(temp), FtpRemoteExists.NoCheck, false);
                if (status != FtpStatus.Success)
                {
                    throw new StoreException(Messages.Format(Messages.StoreFailed, _server, Messages.StoreUploadRefused));
                }
            }
            catch
            {
                Drop(client);
                throw;
            }

            ReturnTransfer(client);
        });
    }

    public override void DeleteFile(string path) => Run(path, client =>
    {
        try
        {
            client.DeleteFile(Ftp(path));
        }
        catch (FtpCommandException) when (!client.FileExists(Ftp(path)))
        {
            // Not there: nothing to delete.
        }

        return 0;
    });

    public override void MoveFile(string source, string target) => Run(source, client =>
    {
        try
        {
            client.Rename(Ftp(source), Ftp(target));
        }
        catch (FtpCommandException) when (client.FileExists(Ftp(target)))
        {
            // A server that does not replace a file by renaming (Windows ones): make room first.
            client.DeleteFile(Ftp(target));
            client.Rename(Ftp(source), Ftp(target));
        }

        return 0;
    });

    protected internal override Exception? Translate(Exception exception, string? path)
    {
        if (_trust.Failure(_server) is { } untrusted)
        {
            return untrusted;
        }

        string text = Messages.Format(Messages.StoreFailed, _server, exception.Message.Trim());
        return exception switch
        {
            FtpAuthenticationException => new StoreException(Messages.Format(Messages.StoreLoginRefused, _server, _account?.User ?? "anonymous"), exception),
            FtpCommandException { CompletionCode: "530" } => new StoreException(Messages.Format(Messages.StoreLoginRefused, _server, _account?.User ?? "anonymous"), exception),
            FtpCommandException { CompletionCode: "532" or "550" or "553" } command when IsDenied(command.Message) => new UnauthorizedAccessException(text, exception),
            FtpException or SocketException or TimeoutException or AuthenticationException or ObjectDisposedException => new StoreException(text, exception),
            _ => null,
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            base.Dispose(disposing);
            return;
        }

        FtpClient[] clients;
        lock (_sync)
        {
            _disposed = true;
            clients = [.. _idle];
            _idle.Clear();
        }

        foreach (var client in clients)
        {
            Drop(client);
        }

        if (_commands is not null)
        {
            Drop(_commands);
            _commands = null;
        }

        base.Dispose(disposing);
    }

    /// <summary>The top folder of a relative path is the one the server puts the account in.</summary>
    private static string Ftp(string path) => path.Length == 0 ? "." : path;

    private static bool IsDenied(string message) =>
        message.Contains("denied", StringComparison.OrdinalIgnoreCase) || message.Contains("permission", StringComparison.OrdinalIgnoreCase);

    private static void Drop(FtpClient client)
    {
        try
        {
            client.Dispose();
        }
        catch (Exception ex) when (ex is FtpException or IOException or SocketException or ObjectDisposedException or TimeoutException)
        {
            // It was gone already.
        }
    }

    private void RemoveFolder(string path) => Run(path, client =>
    {
        var reply = client.Execute("RMD " + Ftp(path));
        return reply.Success ? 0 : throw new StoreException(Messages.Format(Messages.StoreFailed, _server, reply.ErrorMessage));
    });

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

        RemoveFolder(folder);
    }

    /// <summary>A download from a position; it has a connection of its own until it is closed.</summary>
    private Download OpenAt(string path, long offset, long length)
    {
        Cancellation.ThrowIfCancellationRequested();
        var client = TakeTransfer();
        try
        {
            var data = client.OpenRead(Ftp(path), FtpDataType.Binary, offset, length);
            return new Download(this, client, data, length - offset, path);
        }
        catch (Exception ex)
        {
            Drop(client);
            if (Translate(ex, path) is { } failure)
            {
                throw failure;
            }

            throw;
        }
    }

    private T Run<T>(string? path, Func<FtpClient, T> action)
    {
        Cancellation.ThrowIfCancellationRequested();
        try
        {
            if (_commands is not { IsConnected: true })
            {
                if (_commands is not null)
                {
                    Drop(_commands);
                    _commands = null;
                }

                _commands = Connect();
            }

            return action(_commands);
        }
        catch (Exception ex) when (Translate(ex, path) is { } failure)
        {
            throw failure;
        }
    }

    private FtpClient TakeTransfer()
    {
        Cancellation.ThrowIfCancellationRequested();
        while (true)
        {
            FtpClient? client;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _idle.TryPop(out client);
            }

            if (client is null)
            {
                try
                {
                    return Connect();
                }
                catch (Exception ex) when (Translate(ex, null) is { } failure)
                {
                    throw failure;
                }
            }

            if (client.IsConnected)
            {
                return client;
            }

            Drop(client);
        }
    }

    private void ReturnTransfer(FtpClient client)
    {
        lock (_sync)
        {
            if (!_disposed && _idle.Count < IdleTransfers)
            {
                _idle.Push(client);
                return;
            }
        }

        Drop(client);
    }

    private FtpClient Connect()
    {
        if (_host.Length == 0)
        {
            throw new StoreException(Messages.StoreHostMissing);
        }

        var config = new FtpConfig
        {
            ConnectTimeout = (int)ConnectTimeout.TotalMilliseconds,
            ReadTimeout = (int)RequestTimeout.TotalMilliseconds,
            DataConnectionConnectTimeout = (int)ConnectTimeout.TotalMilliseconds,
            DataConnectionReadTimeout = (int)RequestTimeout.TotalMilliseconds,
            SocketKeepAlive = true,

            // A long upload leaves the command connection silent; without a word now and then a router forgets it.
            Noop = true,
            EncryptionMode = !_tls ? FtpEncryptionMode.None : _port == ImplicitTlsPort ? FtpEncryptionMode.Implicit : FtpEncryptionMode.Explicit,
            DataConnectionEncryption = _tls,
            ValidateAnyCertificate = false,
            RetryAttempts = 1,

            // A connection is closed by closing it: a server in the middle of a transfer would not answer a goodbye.
            DisconnectWithQuit = false,

            // Names of files are what they are: "100%.txt" and letters of any alphabet are no attack. A name that
            // would break out of its command (a line break, a control character) is still refused.
            SanitizeUrlEncoding = false,
            SanitizeUnicodeSpoofing = false,
            SanitizeTraversal = false,
        };
        var client = _account is null
            ? new FtpClient(_host, _port, config)
            : new FtpClient(_host, new NetworkCredential(_account.User, _account.Secret), _port, config);
        if (_tls)
        {
            client.ValidateCertificate += (_, e) => e.Accept = _trust.Validate(e.Certificate, e.PolicyErrors);
        }

        try
        {
            client.Connect();
            if (_tls)
            {
                SeenFingerprint = _trust.Seen;
            }

            return client;
        }
        catch (Exception ex)
        {
            Drop(client);
            if (ex is OperationCanceledException)
            {
                throw;
            }

            if (_trust.Failure(_server) is { } untrusted)
            {
                throw untrusted;
            }

            throw ex switch
            {
                FtpAuthenticationException or FtpCommandException { CompletionCode: "530" } =>
                    new StoreException(Messages.Format(Messages.StoreLoginRefused, _server, _account?.User ?? "anonymous"), ex),
                FtpException or SocketException or TimeoutException or AuthenticationException or IOException =>
                    new StoreException(Messages.Format(Messages.StoreUnreachable, _server, ex.Message.Trim()), ex),
                _ => ex,
            };
        }
    }

    /// <summary>
    /// The stream of a download. Read to its end, it leaves the connection ready for the next file; closed earlier
    /// (the reader went elsewhere in the file), the connection is dropped, which is quicker than asking the server to
    /// stop.
    /// </summary>
    private sealed class Download(FtpStore store, FtpClient client, Stream data, long length, string path) : Stream
    {
        private long _left = length;
        private bool _disposed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_left <= 0 || buffer.Length == 0)
            {
                return 0;
            }

            try
            {
                int read = data.Read(buffer[..(int)Math.Min(buffer.Length, _left)]);
                _left -= read;
                return read;
            }
            catch (Exception ex) when (store.Translate(ex, path) is { } failure)
            {
                throw failure;
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                if (_left > 0)
                {
                    Drop(client);
                    try
                    {
                        data.Dispose();
                    }
                    catch (Exception ex) when (ex is FtpException or IOException or SocketException or TimeoutException or ObjectDisposedException or InvalidOperationException)
                    {
                        // Its connection is closed: there is no answer to wait for.
                    }
                }
                else
                {
                    try
                    {
                        // Reads the "transfer complete" of the server.
                        data.Dispose();
                        store.ReturnTransfer(client);
                    }
                    catch (Exception ex) when (ex is FtpException or IOException or SocketException or TimeoutException or ObjectDisposedException)
                    {
                        Drop(client);
                    }
                }
            }

            base.Dispose(disposing);
        }
    }
}
