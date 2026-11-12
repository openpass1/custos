using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Custos.Core.Destinations;

/// <summary>Login and secret of an account of a remote destination (kinds ftp, sftp, s3, webdav).</summary>
/// <param name="User">Login, or the access key of S3.</param>
/// <param name="Secret">Password, secret key, or for SFTP a private key (text starting with "-----BEGIN").</param>
public sealed record StoreAccount(string User, string Secret);

/// <summary>
/// A destination on a server: paths with "/" between their parts, a speed limit, and the failures of the client
/// library turned into <see cref="IOException"/> with a message for the log.
/// </summary>
public abstract class RemoteStore : DestinationStore
{
    /// <summary>How long a server may take to answer a single request.</summary>
    protected static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);

    /// <summary>How long a connection attempt may take.</summary>
    protected static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The limit on writing, if the destination has one.</summary>
    protected SpeedLimiter? Limiter { get; init; }

    /// <summary>A folder as the user typed it: "\" becomes "/", no "/" at the end ("/" itself stays).</summary>
    public static string CleanRoot(string? path)
    {
        string clean = (path ?? string.Empty).Trim().Replace('\\', '/');
        while (clean.Contains("//", StringComparison.Ordinal))
        {
            clean = clean.Replace("//", "/", StringComparison.Ordinal);
        }

        return clean.Length > 1 ? clean.TrimEnd('/') : clean;
    }

    public override string Combine(string folder, string relative)
    {
        string tail = relative.Replace('\\', '/').Trim('/');
        return tail.Length == 0 ? folder
            : folder.Length == 0 ? tail
            : folder.EndsWith('/') ? folder + tail
            : folder + "/" + tail;
    }

    public override string? Parent(string path)
    {
        if (path.Length == 0 || path == "/")
        {
            return null;
        }

        int slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : slash == 0 ? "/" : path[..slash];
    }

    public override string Name(string path) => path[(path.LastIndexOf('/') + 1)..];

    public override string Relative(string folder, string path)
    {
        string prefix = folder.Length == 0 || folder.EndsWith('/') ? folder : folder + "/";
        return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
    }

    /// <summary>The key or certificate the server showed when the store connected; null before that and for plain FTP.</summary>
    public virtual string? SeenFingerprint { get; protected set; }

    /// <summary>The stream of a file being written, with the speed limit of the destination.</summary>
    protected Stream Limited(Stream stream) => Limiter is null ? stream : new ThrottledStream(stream, Limiter, Cancellation);

    /// <summary>"host" or "host:port" when the port is not the usual one.</summary>
    internal static string HostText(string host, int port, int usualPort) =>
        port == usualPort ? host : host + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The failure of the client library as the engine knows it: <see cref="FileNotFoundException"/>,
    /// <see cref="UnauthorizedAccessException"/> or <see cref="StoreException"/>; null for anything that is not a
    /// failure of the destination (a bug, a stopped run).
    /// </summary>
    protected internal abstract Exception? Translate(Exception exception, string? path);

    // ---- Folders known to exist --------------------------------------------------------------------------------

    private readonly HashSet<string> _knownFolders = new(StringComparer.Ordinal);

    /// <summary>A folder this store has created or seen: a copy of many small folders asks the server once.</summary>
    protected bool IsKnownFolder(string path) => _knownFolders.Contains(path);

    protected void RememberFolder(string path) => _knownFolders.Add(path);

    /// <summary>Folders were removed or renamed: nothing is known any more.</summary>
    protected void ForgetFolders() => _knownFolders.Clear();

    /// <summary>
    /// Creates a folder and those above it, asking the server about each only once: <paramref name="exists"/> and
    /// <paramref name="create"/> work on one level.
    /// </summary>
    protected void CreateFolderChain(string path, Func<string, bool> exists, Action<string> create)
    {
        if (path.Length == 0 || path == "/" || path == "." || IsKnownFolder(path))
        {
            return;
        }

        if (!exists(path))
        {
            if (Parent(path) is { } parent)
            {
                CreateFolderChain(parent, exists, create);
            }

            create(path);
        }

        RememberFolder(path);
    }

    // ---- Uploads -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A file uploaded by a call of the client library that reads a stream to its end (SFTP, FTP, WebDAV). The call
    /// runs on a thread of its own and is fed through a pipe, so the engine writes into a stream as everywhere else.
    /// </summary>
    protected sealed class UploadWriter : RenamingWriter
    {
        private readonly RemoteStore _store;
        private readonly BlockingPipe _pipe = new();
        private readonly Task _upload;
        private readonly PatchedWriteStream? _patched;
        private readonly Func<string, Stream>? _reopen;

        /// <param name="upload">Uploads the stream to the path; returns when the stream ends.</param>
        /// <param name="reopen">Opens the uploaded file to change it in place; null where the server cannot (then the
        /// file is written strictly forward).</param>
        public UploadWriter(RemoteStore store, string path, string tempSuffix, Action<Stream, string> upload, Func<string, Stream>? reopen = null)
            : base(store, path, tempSuffix)
        {
            _store = store;
            _reopen = reopen;
            var forward = store.Limited(_pipe.Writer);
            if (reopen is not null)
            {
                _patched = new PatchedWriteStream(forward);
            }

            Stream = _patched ?? forward;
            string temp = TempPath;
            _upload = Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        upload(_pipe.Reader, temp);
                    }
                    catch (Exception ex)
                    {
                        _pipe.FailReader(store.Translate(ex, temp) ?? ex);
                        throw;
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public override Stream Stream { get; }

        public override Stream Reopen() => _reopen is not null ? _reopen(TempPath) : throw new NotSupportedException();

        protected override void CloseCore()
        {
            try
            {
                _patched?.Finish();
            }
            finally
            {
                _pipe.Complete();
            }

            try
            {
                _upload.GetAwaiter().GetResult();
            }
            catch (Exception ex) when (_store.Translate(ex, TempPath) is { } failure)
            {
                throw failure;
            }

            if (_patched is { } patched && (patched.Patches.Count > 0 || patched.CutTo is not null))
            {
                using var stream = _reopen!(TempPath);
                foreach (var (offset, data) in patched.Patches)
                {
                    stream.Seek(offset, SeekOrigin.Begin);
                    stream.Write(data);
                }

                if (patched.CutTo is { } length)
                {
                    stream.SetLength(length);
                }
            }
        }

        protected override void AbortCore()
        {
            // The upload stops instead of being completed.
            _pipe.Abort();
            base.AbortCore();
        }
    }
}

/// <summary>
/// Decides whether the TLS certificate of a server is trusted (FTPS, WebDAV and S3 over https): one that Windows
/// trusts is; any other only if its fingerprint is the one stored in the destination (a self-signed certificate of a
/// NAS, confirmed in «Проверить доступ»).
/// </summary>
public sealed class ServerTrust(string? pinned)
{
    /// <summary>The fingerprint stored in the destination; null if none.</summary>
    public string? Pinned { get; } = string.IsNullOrWhiteSpace(pinned) ? null : pinned.Trim();

    /// <summary>The fingerprint of the certificate the server showed last.</summary>
    public string? Seen { get; private set; }

    /// <summary>The last certificate was refused.</summary>
    public bool Refused { get; private set; }

    /// <summary>The SHA-256 of a certificate as OpenSSL prints it: "AB:CD:…".</summary>
    public static string Fingerprint(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return string.Join(':', SHA256.HashData(certificate.GetRawCertData()).Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>Whether two fingerprints are the same, whatever their case and separators.</summary>
    public static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The callback of <see cref="SslStream"/> and of the HTTP handlers.</summary>
    public bool Validate(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate is null)
        {
            Refused = true;
            return false;
        }

        Seen = Fingerprint(certificate);
        bool trusted = errors == SslPolicyErrors.None || Same(Pinned, Seen);
        Refused = !trusted;
        return trusted;
    }

    /// <summary>The failure to report when a connection broke because the certificate was refused; null otherwise.</summary>
    public ServerTrustException? Failure(string server) => !Refused || Seen is null ? null
        : new ServerTrustException(Pinned is null
            ? Messages.Format(Messages.StoreCertificateUnknown, server, Seen)
            : Messages.Format(Messages.StoreCertificateChanged, server, Pinned, Seen), Pinned, Seen);

    private static string Normalize(string fingerprint) => fingerprint.Replace(":", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
}
