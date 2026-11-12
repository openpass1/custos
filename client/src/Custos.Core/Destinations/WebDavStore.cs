using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Custos.Core.Model;

namespace Custos.Core.Destinations;

/// <summary>
/// A folder on a WebDAV server (section 6.3): Nextcloud, a NAS, Apache, IIS. A file is sent with one <c>PUT</c> under
/// a temporary name and renamed with <c>MOVE</c>; it is read with <c>GET</c> from the position the reader asks for.
/// </summary>
public sealed class WebDavStore : RemoteStore
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly HttpMethod PropFind = new("PROPFIND");
    private static readonly HttpMethod MkCol = new("MKCOL");
    private static readonly HttpMethod Move = new("MOVE");

    /// <summary>How long a server may take to store a file once all of it has arrived.</summary>
    private static readonly TimeSpan StoreTimeout = TimeSpan.FromMinutes(15);

    private const string PropFindBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/><d:getcontentlength/>"
        + "<d:getlastmodified/><d:quota-available-bytes/><d:quota-used-bytes/></d:prop></d:propfind>";

    private readonly Uri? _server;
    private readonly StoreAccount? _account;
    private readonly ServerTrust _trust;
    private readonly string _serverText;
    private readonly HttpClient _http;

    /// <param name="account">Null = no login.</param>
    public WebDavStore(TaskDestination destination, StoreAccount? account, SpeedLimiter? limiter = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        _account = account;
        _trust = new ServerTrust(destination.ServerFingerprint);
        Limiter = limiter;
        string prefix = string.Empty;
        if (TryServer(destination.Host, destination.Port, out var server, out string serverPath))
        {
            _server = server;
            prefix = serverPath;
        }

        _serverText = _server?.GetLeftPart(UriPartial.Authority) ?? string.Empty;
        Root = RootOf(prefix, destination.Path);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = ConnectTimeout,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) => _trust.Validate(certificate, errors);
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public override string Kind => DestinationKinds.WebDav;

    public override string Root { get; }

    public override string Label => _serverText + Root;

    public override string? Account => _account?.User;

    public override string CacheKey => "webdav:" + _account?.User + "@" + _serverText;

    public override string? SeenFingerprint
    {
        get => _trust.Seen;
        protected set { }
    }

    /// <summary>
    /// The address of a WebDAV server from what the destination holds: "nas.example.org" (https, or http on port 80),
    /// or a whole address such as "https://cloud.example.org/remote.php/dav/files/ivan", whose path then comes before
    /// the folder of the destination.
    /// </summary>
    public static bool TryServer(string? host, int? port, out Uri server, out string path)
    {
        server = null!;
        path = string.Empty;
        string text = (host ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = (port == 80 ? "http://" : "https://") + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var typed) || typed.Scheme is not ("http" or "https") || typed.Host.Length == 0)
        {
            return false;
        }

        var builder = new UriBuilder(typed.Scheme, typed.Host, port is > 0 and <= 65535 && typed.IsDefaultPort ? port.Value : typed.Port);
        server = builder.Uri;
        path = Uri.UnescapeDataString(typed.AbsolutePath).TrimEnd('/');
        return true;
    }

    /// <summary>The folder of a destination on the server: the path of its address, then its own folder.</summary>
    internal static string RootOf(string serverPath, string? folder)
    {
        string root = CleanRoot(serverPath + "/" + folder);
        return root.Length == 0 ? "/" : root;
    }

    public override void CreateFolder(string path) => CreateFolderChain(path, FolderExists, one =>
    {
        using var response = Send(MkCol, one, folder: true);

        // 405: it is there already.
        if (response.StatusCode != HttpStatusCode.MethodNotAllowed)
        {
            Check(response, one);
        }
    });

    public override bool FolderExists(string path)
    {
        if (IsKnownFolder(path))
        {
            return true;
        }

        bool exists = Stat(path) is { IsFolder: true };
        if (exists)
        {
            RememberFolder(path);
        }

        return exists;
    }

    public override bool FileExists(string path) => Stat(path) is { IsFolder: false };

    public override List<StoreEntry> List(string folder)
    {
        var result = new List<StoreEntry>();
        var found = Properties(folder, depth: 1, folder: true);
        if (found is null)
        {
            return result;
        }

        string self = folder.TrimEnd('/');
        foreach (var item in found)
        {
            if (string.Equals(item.Path.TrimEnd('/'), self, StringComparison.Ordinal))
            {
                continue;
            }

            string name = Name(item.Path.TrimEnd('/'));
            if (name.Length > 0)
            {
                result.Add(new StoreEntry(Combine(folder, name), name, item.IsFolder, item.IsFolder ? 0 : item.Size, item.ModifiedUtc));
            }
        }

        return result;
    }

    public override void DeleteFolder(string path)
    {
        ForgetFolders();
        Delete(path, folder: true);
    }

    public override bool RemoveEmptyFolder(string path)
    {
        ForgetFolders();
        var found = Properties(path, depth: 1, folder: true);
        if (found is null)
        {
            return true;
        }

        if (found.Any(item => !string.Equals(item.Path.TrimEnd('/'), path.TrimEnd('/'), StringComparison.Ordinal)))
        {
            return false;
        }

        Delete(path, folder: true);
        return true;
    }

    public override void MoveFolder(string source, string target)
    {
        ForgetFolders();
        MoveTo(source, target, folder: true);
    }

    public override Stream OpenRead(string path)
    {
        long length = GetFileSize(path);
        return new RangeReadStream(length, offset => OpenAt(path, offset));
    }

    public override long GetFileSize(string path) =>
        Stat(path) is { IsFolder: false } item ? item.Size : throw new FileNotFoundException(null, path);

    public override StoreWriter BeginWrite(string path, string tempSuffix, StoreWriteMode mode = StoreWriteMode.Blocks)
    {
        if (mode == StoreWriteMode.Seekable)
        {
            throw new NotSupportedException();
        }

        Cancellation.ThrowIfCancellationRequested();
        return new UploadWriter(this, path, tempSuffix, Put);
    }

    public override void DeleteFile(string path) => Delete(path, folder: false);

    public override void MoveFile(string source, string target) => MoveTo(source, target, folder: false);

    public override StoreSpace GetSpace(string path)
    {
        try
        {
            var item = Properties(path, depth: 0, folder: true)?.FirstOrDefault();
            return item is { Free: >= 0 } ? new StoreSpace(item.Used >= 0 ? item.Used + item.Free : null, item.Free) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    protected internal override Exception? Translate(Exception exception, string? path)
    {
        if (_trust.Failure(_serverText) is { } untrusted)
        {
            return untrusted;
        }

        return exception switch
        {
            HttpRequestException or AuthenticationException or InvalidOperationException or TimeoutException =>
                new StoreException(Messages.Format(Messages.StoreUnreachable, _serverText, Deepest(exception).Message), exception),
            OperationCanceledException when !Cancellation.IsCancellationRequested =>
                new StoreException(Messages.Format(Messages.StoreFailed, _serverText, Messages.StoreNoAnswer), exception),
            _ => null,
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _http.Dispose();
        }

        base.Dispose(disposing);
    }

    private static Exception Deepest(Exception exception)
    {
        while (exception.InnerException is { } inner)
        {
            exception = inner;
        }

        return exception;
    }

    private static string Escape(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private Uri Address(string path, bool folder)
    {
        if (_server is null)
        {
            throw new StoreException(Messages.StoreHostMissing);
        }

        string escaped = Escape(path.StartsWith('/') ? path : "/" + path);
        return new Uri(_server, folder && !escaped.EndsWith('/') ? escaped + "/" : escaped);
    }

    private HttpRequestMessage Request(HttpMethod method, string path, bool folder)
    {
        var request = new HttpRequestMessage(method, Address(path, folder));
        if (_account is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(_account.User + ":" + _account.Secret)));
        }

        return request;
    }

    /// <summary>Sends a request that has no body to speak of and waits for the headers of the answer.</summary>
    private HttpResponseMessage Send(HttpMethod method, string path, bool folder, Action<HttpRequestMessage>? prepare = null)
    {
        Cancellation.ThrowIfCancellationRequested();
        using var request = Request(method, path, folder);
        prepare?.Invoke(request);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        limit.CancelAfter(RequestTimeout);
        try
        {
            return _http.Send(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
        }
        catch (Exception ex) when (Translate(ex, path) is { } failure)
        {
            throw failure;
        }
    }

    /// <summary>Turns an answer that is not a success into the failure the engine knows.</summary>
    private void Check(HttpResponseMessage response, string path)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        int code = (int)response.StatusCode;
        string text = code.ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase;
        if (response.Headers.Location is { } location)
        {
            text += " → " + location;
        }

        string message = Messages.Format(Messages.StoreHttpFailed, _serverText, text, path);
        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound => new FileNotFoundException(message, path),
            HttpStatusCode.Unauthorized => new StoreException(Messages.Format(Messages.StoreLoginRefused, _serverText, _account?.User ?? "-")),
            HttpStatusCode.Forbidden => new UnauthorizedAccessException(message),
            HttpStatusCode.InsufficientStorage => new StoreException(Messages.Format(Messages.StoreFull, _serverText)),
            _ => new StoreException(message),
        };
    }

    private Item? Stat(string path)
    {
        // Asked as a file first: a server answers for a folder too, some only with "/" at the end.
        var found = Properties(path, depth: 0, folder: false) ?? Properties(path, depth: 0, folder: true);
        return found?.FirstOrDefault();
    }

    /// <summary>PROPFIND; null if there is nothing at the path.</summary>
    private List<Item>? Properties(string path, int depth, bool folder)
    {
        using var response = Send(PropFind, path, folder, request =>
        {
            request.Headers.Add("Depth", depth.ToString(CultureInfo.InvariantCulture));
            request.Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml");
        });
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        // A folder asked for without "/" at the end: the server points to the right address.
        if (!folder && (int)response.StatusCode is 301 or 302 or 307 or 308)
        {
            return null;
        }

        Check(response, path);
        XDocument document;
        try
        {
            using var body = response.Content.ReadAsStream(Cancellation);
            using var reader = XmlReader.Create(body, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is XmlException or IOException or HttpRequestException)
        {
            throw new StoreException(Messages.Format(Messages.StoreFailed, _serverText, ex.Message), ex);
        }

        var result = new List<Item>();
        foreach (var element in document.Descendants(Dav + "response"))
        {
            string? href = element.Element(Dav + "href")?.Value.Trim();
            if (string.IsNullOrEmpty(href))
            {
                continue;
            }

            var properties = element.Elements(Dav + "propstat")
                .Where(p => p.Element(Dav + "status")?.Value.Contains(" 200", StringComparison.Ordinal) != false)
                .Select(p => p.Element(Dav + "prop"))
                .Where(p => p is not null)
                .ToList();
            string Value(string name) => properties.Select(p => p!.Element(Dav + name)?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
            bool isFolder = properties.Any(p => p!.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null);
            long size = long.TryParse(Value("getcontentlength"), NumberStyles.None, CultureInfo.InvariantCulture, out long length) ? length : 0;
            var modified = DateTimeOffset.TryParse(Value("getlastmodified"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                ? time.UtcDateTime : DateTime.MinValue;
            long free = long.TryParse(Value("quota-available-bytes"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long available) ? available : -1;
            long used = long.TryParse(Value("quota-used-bytes"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long taken) ? taken : -1;
            result.Add(new Item(HrefPath(href), isFolder, size, modified, free, used));
        }

        return result;
    }

    /// <summary>The path of an href of an answer: it may be a whole address, and it is escaped.</summary>
    private static string HrefPath(string href)
    {
        string path = Uri.TryCreate(href, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https"
            ? absolute.AbsolutePath
            : href;
        int query = path.IndexOf('?', StringComparison.Ordinal);
        return Uri.UnescapeDataString(query >= 0 ? path[..query] : path);
    }

    private void Delete(string path, bool folder)
    {
        using var response = Send(HttpMethod.Delete, path, folder);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            Check(response, path);
        }
    }

    private void MoveTo(string from, string to, bool folder)
    {
        using var response = Send(Move, from, folder, request =>
        {
            request.Headers.Add("Destination", Address(to, folder).AbsoluteUri);
            request.Headers.Add("Overwrite", "T");
        });
        Check(response, from);
    }

    /// <summary>One PUT whose body is read from the pipe of the writer; its length is not known in advance.</summary>
    private void Put(Stream source, string path)
    {
        using var request = Request(HttpMethod.Put, path, folder: false);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        request.Content = new StreamContent(new EndWatch(source, () => limit.CancelAfter(StoreTimeout)), 256 * 1024);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        // A refusal (no rights, no such folder) comes before the body is sent, not after.
        request.Headers.ExpectContinue = true;
        using var response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
        Check(response, path);
    }

    private GuardedStream OpenAt(string path, long offset)
    {
        var response = Send(HttpMethod.Get, path, folder: false, request =>
        {
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
            }
        });
        try
        {
            Check(response, path);
            var body = response.Content.ReadAsStream(Cancellation);
            if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                // The server sent the whole file: what comes before the position is read and dropped.
                Skip(body, offset);
            }

            return new GuardedStream(body, ex => Translate(ex, path), response.Dispose);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static void Skip(Stream stream, long count)
    {
        byte[] scratch = new byte[64 * 1024];
        while (count > 0)
        {
            int read = stream.Read(scratch, 0, (int)Math.Min(scratch.Length, count));
            if (read == 0)
            {
                throw new EndOfStreamException(Messages.StoreFileShorter);
            }

            count -= read;
        }
    }

    private sealed record Item(string Path, bool IsFolder, long Size, DateTime ModifiedUtc, long Free, long Used);

    /// <summary>Tells when the body of an upload has been read to its end: from then on the answer is due.</summary>
    private sealed class EndWatch(Stream inner, Action ended) : Stream
    {
        private bool _ended;

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
            int read = inner.Read(buffer);
            if (read == 0 && !_ended)
            {
                _ended = true;
                ended();
            }

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0 && !_ended)
            {
                _ended = true;
                ended();
            }

            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
